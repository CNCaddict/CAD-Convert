# The geometry of a round, shared by the FreeCAD build (build_model.py) and the SolidCraft build (solidcraft/build_script.py,
# which runs inside FreeCAD's Python and reads SolidCraft's part back as STEP): the profile of the corner outside the
# rolling ball, two rounds that meet becoming one, and Inventor's edge descriptions matched to a shape's edges.
import math, os
import FreeCAD as App
import Part

V = App.Vector
# How far a point may be from where it should be: SolidCraft's STEP puts edges up to about 0.0001 mm off.
NEAR = 1e-3


def probe(shape, p, on_face=False):
    """Whether the point is in the solid, or None where it can't answer. OpenCASCADE's classifier sometimes throws for
    a solid read back from SolidCraft's STEP; a looser tolerance usually gets an answer."""
    for tol in (1e-6, 1e-4):
        try:
            return shape.isInside(p, tol, on_face)
        except Part.OCCError:
            pass
    # Or count where rays from the point cross the surface (odd: inside), which works on a solid the classifier calls
    # invalid. A ray through an edge meets both faces there at one point, counted once; most of three rays decide.
    reach = shape.BoundBox.DiagonalLength * 2
    votes = []
    for d in (V(0.5773, 0.5774, 0.5775), V(-0.6, 0.48, -0.64), V(0.28, -0.64, 0.7155)):
        try:
            hits = Part.LineSegment(p, p + d * reach).toShape().section(shape).Vertexes
        except Exception:
            continue
        crossings = []
        for v in hits:
            if v.Point.distanceToPoint(p) > 1e-7 and not any(v.Point.distanceToPoint(q) < 1e-6 for q in crossings):
                crossings.append(v.Point)
        votes.append(len(crossings) % 2 == 1)
    if votes.count(True) >= 2:
        return True
    if votes.count(False) >= 2:
        return False
    return None


def on_side(shape, face, p):
    """Whether the point lies on this flat face, or on another face of the shape in the same plane: SolidCraft can
    split one face in two where FreeCAD has one."""
    v = Part.Vertex(p)
    if face.distToShape(v)[0] <= NEAR:
        return True
    s = face.Surface
    if not isinstance(s, Part.Plane):
        return False
    n = s.Axis
    for f in shape.Faces:
        t = f.Surface
        if isinstance(t, Part.Plane) and abs(abs(t.Axis.dot(n)) - 1) < 1e-9 and abs((t.Position - s.Position).dot(n)) <= NEAR and f.distToShape(v)[0] <= NEAR:
            return True
    return False


def inside(shape, p):
    """Whether the point is in the solid; True (so nothing is done there) where the solid can't answer."""
    found = probe(shape, p, True)
    return True if found is None else found


def holes_to_reopen(before, added):
    """A round that fills a corner can run over a hole there. Inventor keeps the hole open; FreeCAD's fillet and a
    profile added along the edge both fill its mouth (seen on real parts). For each hole the new material
    reaches into: (radius, axis, start, height) of the cylinder to cut through it again, only through what was empty
    before, so nothing else is cut."""
    if added.isNull() or added.Volume < 1e-6:
        return []
    holes = []
    for face in before.Faces:
        c = face.Surface
        if not isinstance(c, Part.Cylinder) or not face.BoundBox.intersect(added.BoundBox):
            continue
        axis = V(c.Axis)
        # A hole, not a rounded inside corner: its axis is clear of material, and its faces go more than half round.
        u0, u1 = face.ParameterRange[:2]
        mid = face.valueAt((u0 + u1) / 2, sum(face.ParameterRange[2:]) / 2)
        centre = c.Center + axis * (mid - c.Center).dot(axis)
        same = [h for h in holes if abs(h[0] - c.Radius) < 1e-6 and abs(abs(h[1].dot(axis)) - 1) < 1e-9 and ((h[2] - centre) - axis * (h[2] - centre).dot(axis)).Length < 1e-6]
        if same:
            same[0][3] += u1 - u0
        elif not inside(before, centre):
            holes.append([c.Radius, axis, centre, u1 - u0])
    out = []
    # Each lump of new material on its own: the same round on two arms mustn't make one cut across the part.
    for (r, axis, centre, turn), lump in [(h, s) for h in holes if h[3] > math.pi + 1e-6 for s in added.Solids]:
        b = lump.BoundBox
        ends = [(V(x, y, z) - centre).dot(axis) for x in (b.XMin, b.XMax) for y in (b.YMin, b.YMax) for z in (b.ZMin, b.ZMax)]
        lo, hi = min(ends) - 0.01, max(ends) + 0.01
        probe = Part.makeCylinder(r, hi - lo, centre + axis * lo, axis)
        if lump.common(probe).Volume < 1e-6 or before.common(probe).Volume > 1e-6:
            continue
        out.append((r, axis, centre + axis * lo, hi - lo))
    return out


def profile_solid(tool, radius):
    """The solid a profile from round_profile or merged_profile sweeps out, where it lies in the part."""
    (cx, cy), (a0, a1) = tool["center"], tool["arc"]
    arc = Part.ArcOfCircle(Part.Circle(V(cx, cy, 0), V(0, 0, 1), radius), a0, a1).toShape()
    points = [arc.Vertexes[-1].Point] + [V(p[0], p[1], 0) for p in tool["corner"]] + [arc.Vertexes[0].Point]
    pieces = [arc] + [Part.LineSegment(points[i], points[i + 1]).toShape() for i in range(len(points) - 1)]
    if tool.get("middle"):
        (mx, my), mr = tool["middle"]
        pieces[2] = Part.Arc(points[1], V(mx, my, 0) + ((points[1] + points[2]) * 0.5 - V(mx, my, 0)).normalize() * mr, points[2]).toShape()
    face = Part.Face(Part.Wire(Part.__sortEdges__(pieces)))
    if tool["kind"] == "line":
        solid = face.translated(V(0, 0, -tool["length"] / 2)).extrude(V(0, 0, tool["length"]))
    else:
        turn = tool["angle"]
        solid = face.revolve(V(0, 0, 0), V(0, 1, 0), turn)
        if turn < 360:
            solid.rotate(V(0, 0, 0), V(0, 1, 0), -turn / 2)
    solid.transformShape(tool["placement"].toMatrix())
    return solid


def section_curve(s, kind, local, axis):
    """What a face next to the edge looks like in the plane across it, if it's the same all the way along: ("line",) or
    ("circle", centre, radius). Straight edges take planes and parallel cylinders; round ones anything turned on the same axis."""
    if kind == "line":
        if isinstance(s, Part.Plane):
            return ("line",)
        if isinstance(s, Part.Cylinder) and abs(abs(s.Axis.dot(axis)) - 1) < 1e-6:
            return ("circle", local(s.Center), s.Radius)
        return None
    center, turn = axis
    on_axis = lambda p: ((p - center) - turn * (p - center).dot(turn)).Length < 1e-6
    if isinstance(s, Part.Plane):
        return ("line",) if abs(abs(s.Axis.dot(turn)) - 1) < 1e-6 else None
    if isinstance(s, (Part.Cylinder, Part.Cone)):
        same = abs(abs(s.Axis.dot(turn)) - 1) < 1e-6 and on_axis(s.Center)
        return ("line",) if same else None
    if isinstance(s, Part.Sphere) and on_axis(s.Center):
        return ("circle", (0.0, (s.Center - center).dot(turn)), s.Radius)
    if isinstance(s, Part.Toroid) and abs(abs(s.Axis.dot(turn)) - 1) < 1e-6 and on_axis(s.Center):
        return ("circle", (s.MajorRadius, (s.Center - center).dot(turn)), s.MinorRadius)
    return None


def ball_center(p, curves, normals, offset, radius, points=None, every=False):
    """Where a ball of this radius touches both section curves, nearest the edge at p (or, with every, all the places,
    nearest first). offset is +1 or -1 along the normals, each taken at its point on its curve (p, unless points gives others)."""
    points = points or (p, p)
    def shifted(curve, n, q):
        if curve[0] == "line":
            return ("line", (q[0] + offset * radius * n[0], q[1] + offset * radius * n[1]), (-n[1], n[0]))
        c, r = curve[1], curve[2]
        return ("circle", c, math.hypot(q[0] + offset * radius * n[0] - c[0], q[1] + offset * radius * n[1] - c[1]))
    a, b = shifted(curves[0], normals[0], points[0]), shifted(curves[1], normals[1], points[1])
    if a[0] == "circle" and b[0] == "line":
        a, b = b, a
    found = []
    if a[0] == "line" and b[0] == "line":
        d = a[2][0] * b[2][1] - a[2][1] * b[2][0]
        if abs(d) < 1e-9:
            return None
        t = ((b[1][0] - a[1][0]) * b[2][1] - (b[1][1] - a[1][1]) * b[2][0]) / d
        found = [(a[1][0] + t * a[2][0], a[1][1] + t * a[2][1])]
    elif a[0] == "line":
        fx, fy = a[1][0] - b[1][0], a[1][1] - b[1][1]
        along = fx * a[2][0] + fy * a[2][1]
        rest = fx * fx + fy * fy - b[2] * b[2]
        q = along * along - rest
        if q < 0:
            return None
        found = [(a[1][0] + t * a[2][0], a[1][1] + t * a[2][1]) for t in (-along + math.sqrt(q), -along - math.sqrt(q))]
    else:
        dx, dy = b[1][0] - a[1][0], b[1][1] - a[1][1]
        d = math.hypot(dx, dy)
        if d < 1e-9 or d > a[2] + b[2] or d < abs(a[2] - b[2]):
            return None
        x = (d * d + a[2] * a[2] - b[2] * b[2]) / (2 * d)
        y = math.sqrt(max(0.0, a[2] * a[2] - x * x))
        mx, my = a[1][0] + x * dx / d, a[1][1] + x * dy / d
        found = [(mx - y * dy / d, my + y * dx / d), (mx + y * dy / d, my - y * dx / d)]
    found.sort(key=lambda c: math.hypot(c[0] - p[0], c[1] - p[1]))
    return found if every else found[0]


def arc_clear(shape, c, a, b, radius, at):
    """Whether the short arc of the ball (centre c) between section points a and b lies outside the solid, sampled
    between its ends."""
    a0, a1 = math.atan2(a[1] - c[1], a[0] - c[0]), math.atan2(b[1] - c[1], b[0] - c[0])
    turn = (a1 - a0 + math.pi) % (2 * math.pi) - math.pi
    for k in (0.2, 0.4, 0.6, 0.8):
        t = a0 + turn * k
        q = at((c[0] + radius * math.cos(t), c[1] + radius * math.sin(t)))
        if probe(shape, q) is not False and Part.Vertex(q).distToShape(shape)[0] > NEAR:
            return False
    return True


def roll_over(shape, faces, curves, normals, touch, center, p, radius, kind, local, axis, at, x, y, z, origin, touch_of, elsewhere):
    """A ball wider than the face it should touch rolls over onto the face beyond: on a real part a 3 mm round
    in a corner whose flat is 2 mm wide meets the earlier 2 mm round instead. For an inside corner: (faces, curves,
    normals, centre, touch points, which side moved), or None where both touch points are on their faces already."""
    off = [i for i in (0, 1) if not on_side(shape, faces[i], at(touch[i]))]
    if len(off) != 1:
        return None
    i = off[0]
    keep = 1 - i
    lost = at(touch[i])
    # Every ball that fits, onto a face beyond or onto the corner; the one Inventor makes is the first the ball meets
    # rolling out from the edge: its touch on the kept face nearest the edge (gaps-step-roll-r3: a 3 mm ball fits on the
    # floor below the 0.5 mm flat too, but meets the flat's corner first).
    found = []
    def nearest(found):
        return min(found, key=lambda r: at(r[4][keep]).distanceToPoint(at(p))) if found else None
    size = 4 * (radius + lost.distanceToPoint(at(p)))
    cross = Part.makePlane(size, size, lost - x * (size / 2) - y * (size / 2), z, x)
    near = []
    for e in faces[i].Edges:
        for g in shape.ancestorsOfType(e, Part.Face):
            if not any(g.isSame(h) for h in list(faces) + near):
                near.append(g)
    for g in near:
        curve = section_curve(g.Surface, kind, local, axis)
        if curve is None:
            continue
        try:
            cut = g.section(cross)
            if not cut.Edges:
                continue
            q3 = cut.distToShape(Part.Vertex(lost))[1][0][0]
            n = g.normalAt(*g.Surface.parameter(q3))
        except Exception:
            continue
        n2 = (n.dot(x), n.dot(y))
        if math.hypot(*n2) < 1e-6:
            continue
        n2 = (n2[0] / math.hypot(*n2), n2[1] / math.hypot(*n2))
        q = local(q3)
        cs = [None, None]
        ns = [None, None]
        cs[keep], cs[i] = curves[keep], curve
        ns[keep], ns[i] = normals[keep], n2
        pts = [None, None]
        pts[keep], pts[i] = p, q
        c = ball_center(p, cs, ns, 1, radius, pts)
        if c is None:
            continue
        t = [touch_of(cs[k], ns[k], c) for k in (0, 1)]
        if None in t:
            continue
        fs = list(faces)
        fs[i] = g
        if not all(on_side(shape, fs[k], at(t[k])) for k in (0, 1)):
            continue
        # The ball clear of the solid, and the profile's straight side from the edge to the new touch point in it.
        if Part.Vertex(at(c)).distToShape(shape)[0] < radius - NEAR or not inside(shape, (at(p) + at(t[i])) * 0.5):
            continue
        found.append((fs, cs, ns, c, t, i))
    # Or onto the far edge of that face: in gaps-step-roll a 1 mm round's flat is 0.5 mm wide and the ball meets the corner
    # beyond it (and in the test parts gaps-step-*, at 1 and 3 mm, with "roll along sharp edges" on or off alike). In the
    # cross section, a ball touching the kept face and passing through that corner point. Only where the flat is that
    # narrow all along the edge: on some real parts a hole cuts a 20 mm round's flat down to 0.5 mm at the edge's
    # middle only, and Inventor rounds across the hole's mouth as if it weren't there.
    if any(on_side(shape, faces[i], q) for q in elsewhere(lost)):
        return nearest(found)
    try:
        ends = faces[i].section(cross).Vertexes
    except Exception:
        ends = []
    far = [v.Point for v in ends if v.Point.distanceToPoint(at(p)) > NEAR]
    if not far:
        return nearest(found)
    q3 = min(far, key=lambda v: v.distanceToPoint(lost))
    # Only where the face really stops short: its far edge on the way from the edge to where the ball would touch it,
    # and nearer.
    run = lost - at(p)
    if run.Length < NEAR:
        return nearest(found)
    u = run * (1.0 / run.Length)
    along = (q3 - at(p)).dot(u)
    if along <= NEAR or along >= run.Length - NEAR or ((q3 - at(p)) - u * along).Length > NEAR:
        return nearest(found)
    # And a real corner there, not the face running smoothly on into the next (on a real part the flat runs into an
    # earlier round, and the ball rolls onto that round instead).
    try:
        own = faces[i].normalAt(*faces[i].Surface.parameter(q3))
        beyond = [g for g in shape.Faces if not g.isSame(faces[i]) and g.distToShape(Part.Vertex(q3))[0] < NEAR]
        if not beyond or all(abs(own.getAngle(g.normalAt(*g.Surface.parameter(q3)))) < math.radians(2) for g in beyond):
            return nearest(found)
    except Exception:
        return nearest(found)
    q = local(q3)
    cs, ns, pts = [None, None], [None, None], [None, None]
    cs[keep], ns[keep], pts[keep] = curves[keep], normals[keep], p
    away = (q[0] - p[0], q[1] - p[1])
    size = math.hypot(*away) or 1.0
    cs[i], ns[i], pts[i] = ("circle", q, 0.0), (away[0] / size, away[1] / size), q
    # The ball can sit either side of the corner point; only one stays clear of the solid.
    for c in ball_center(p, cs, ns, 1, radius, pts, every=True) or []:
        t = [None, None]
        t[keep], t[i] = touch_of(cs[keep], ns[keep], c), q
        if t[keep] is None or not on_side(shape, faces[keep], at(t[keep])):
            continue
        # Only the arc between the two touch points becomes surface, so it must run through open air; the rest of the
        # ball may dip into the solid (gaps-step-roll-r3: a 3 mm ball reaches below the floor past the corner).
        if not inside(shape, (at(p) + q3) * 0.5) or not arc_clear(shape, c, t[keep], q, radius, at):
            continue
        if os.environ.get("CADCONVERT_DEBUG_ROLL") == "1":
            print("ROLL onto edge: r %s edge at %s, corner %s, ball %s" % (radius, at(p), q3, at(c)), flush=True)
        found.append((list(faces), cs, ns, c, t, i))
        break
    return nearest(found)


def round_profile(shape, name, radius):
    """The profile round_with_tool needs for one edge, in the coordinates of a sketch placed across the edge's middle;
    None where the faces beside it change along the edge."""
    e = shape.Edges[int(name[4:]) - 1]
    faces = shape.ancestorsOfType(e, Part.Face)
    if len(faces) != 2:
        return None
    try:
        c = e.Curve
    except Exception:
        return None
    middle = (e.FirstParameter + e.LastParameter) / 2
    pm = e.valueAt(middle)
    if isinstance(c, Part.Line):
        kind = "line"
        z = e.tangentAt(middle)
        x = z.cross(V(1, 0, 0)) if abs(z.x) < 0.9 else z.cross(V(0, 1, 0))
        x = x * (1.0 / x.Length)
        y = z.cross(x)
        origin = pm
        axis = z
        p = (0.0, 0.0)
    elif isinstance(c, Part.Circle):
        kind = "circle"
        y = V(c.Axis)
        x = pm - c.Center
        x = x * (1.0 / x.Length)
        z = x.cross(y)
        origin = c.Center
        axis = (c.Center, y)
        p = (c.Radius, 0.0)
    else:
        return None
    local = lambda q: ((q - origin).dot(x), (q - origin).dot(y))
    curves, normals, runs = [], [], []
    step = min(0.01, radius / 100)
    for face in faces:
        curve = section_curve(face.Surface, kind, local, axis)
        if curve is None:
            return None
        n = face.normalAt(*face.Surface.parameter(pm))
        n2 = (n.dot(x), n.dot(y))
        size = math.hypot(*n2)
        if size < 1e-6:
            return None
        n2 = (n2[0] / size, n2[1] / size)
        # Which way the face runs away from the edge.
        side = (-n2[1], n2[0])
        ahead = Part.Vertex(pm + x * (side[0] * step) + y * (side[1] * step))
        behind = Part.Vertex(pm - x * (side[0] * step) - y * (side[1] * step))
        if face.distToShape(ahead)[0] > face.distToShape(behind)[0]:
            side = (-side[0], -side[1])
        curves.append(curve)
        normals.append(n2)
        runs.append(side)
    bend = runs[0][0] * normals[1][0] + runs[0][1] * normals[1][1]
    if abs(bend) < 1e-6:
        return None
    convex = bend < 0
    # The ball sits in the material of a convex edge, and outside it at a concave one.
    center = ball_center(p, curves, normals, -1 if convex else 1, radius)
    if center is None:
        return None
    def touch_of(curve, n, center):
        s = -1 if convex else 1
        if curve[0] == "line":
            return (center[0] - s * radius * n[0], center[1] - s * radius * n[1])
        d = math.hypot(center[0] - curve[1][0], center[1] - curve[1][1])
        if d < 1e-9:
            return None
        return (curve[1][0] + (center[0] - curve[1][0]) * curve[2] / d, curve[1][1] + (center[1] - curve[1][1]) * curve[2] / d)
    touch = [touch_of(curve, n, center) for curve, n in zip(curves, normals)]
    if None in touch or math.hypot(touch[0][0] - touch[1][0], touch[0][1] - touch[1][1]) < 1e-6:
        return None
    at = lambda t: origin + x * t[0] + y * t[1]
    def elsewhere(q):
        # A point of the cross section, moved along the edge to near each end (slid along a straight edge, turned with
        # a round one).
        out = []
        for share in (0.1, 0.9):
            u = e.FirstParameter + (e.LastParameter - e.FirstParameter) * share
            if kind == "line":
                out.append(q + (e.valueAt(u) - pm))
            else:
                out.append(axis[0] + App.Rotation(axis[1], math.degrees(u - middle)).multVec(q - axis[0]))
        return out
    if not convex:
        rolled = roll_over(shape, faces, curves, normals, touch, center, p, radius, kind, local, axis, at, x, y, z, origin, touch_of, elsewhere)
        if rolled:
            faces, curves, normals, center, touch, moved = rolled
            runs[moved] = None
    # Away from the ball's centre at both touching points and past the edge: the corner the profile has to take in.
    margin = min(0.1 * radius, 0.5)
    def out(q):
        d = math.hypot(q[0] - center[0], q[1] - center[1])
        return (q[0] + (q[0] - center[0]) / d * margin, q[1] + (q[1] - center[1]) / d * margin)
    swapped = (touch[0][0] - center[0]) * (touch[1][1] - center[1]) - (touch[0][1] - center[1]) * (touch[1][0] - center[0]) < 0
    first, second = (touch[1], touch[0]) if swapped else (touch[0], touch[1])
    a0 = math.atan2(first[1] - center[1], first[0] - center[0])
    a1 = math.atan2(second[1] - center[1], second[0] - center[0])
    if a1 <= a0:
        a1 += 2 * math.pi
    corner = [out(second), out(p), out(first)]
    lines = []
    for i, curve in enumerate(curves):
        if curve[0] == "line" and runs[i] is not None:
            t = touch[i]
            r = runs[i]
            reach = 2 * (radius + math.hypot(t[0] - p[0], t[1] - p[1]))
            lines.append(((p[0] - r[0] * reach, p[1] - r[1] * reach), (p[0] + r[0] * reach, p[1] + r[1] * reach)))
        else:
            lines.append(None)
    placement = App.Placement(App.Matrix(x.x, y.x, z.x, origin.x, x.y, y.y, z.y, origin.y, x.z, y.z, z.z, origin.z, 0, 0, 0, 1))
    tool = {"kind": kind, "cut": convex, "placement": placement, "center": center, "arc": (a0, a1), "corner": corner, "faces": lines, "swapped": swapped,
            "touch": [(face, origin + x * t[0] + y * t[1]) for face, t in zip(faces, touch)]}
    if kind == "line":
        tool["length"] = e.Length
    else:
        whole = len(e.Vertexes) == 1 or e.isClosed()
        tool["angle"] = 360 if whole else math.degrees(e.LastParameter - e.FirstParameter)
    return tool


def between(shape, ea, eb):
    """The face between two edges: (its piece at ea, its piece at eb), or None. The same face, or two pieces of one
    flat face: SolidCraft can split a face FreeCAD keeps whole (at a plate's end)."""
    fa, fb = shape.ancestorsOfType(ea, Part.Face), shape.ancestorsOfType(eb, Part.Face)
    if len(fa) != 2 or len(fb) != 2:
        return None
    pairs = [(f, g) for f in fa for g in fb if f.isSame(g)]
    if not pairs:
        coplanar = lambda f, g: isinstance(f.Surface, Part.Plane) and isinstance(g.Surface, Part.Plane) and abs(abs(f.Surface.Axis.dot(g.Surface.Axis)) - 1) < 1e-9 and abs((g.Surface.Position - f.Surface.Position).dot(f.Surface.Axis)) <= NEAR
        # And the face really lies between them: not the two sides of a slot's mouth.
        mid = Part.Vertex((ea.valueAt((ea.FirstParameter + ea.LastParameter) / 2) + eb.valueAt((eb.FirstParameter + eb.LastParameter) / 2)) * 0.5)
        pairs = [(f, g) for f in fa for g in fb if coplanar(f, g) and min(f.distToShape(mid)[0], g.distToShape(mid)[0]) <= NEAR]
    return pairs[0] if len(pairs) == 1 else None


def merged_round(shape, names, radius):
    """Two straight convex edges either side of a flat face too narrow for two rounds: Inventor makes them one round,
    tangent to the faces either side, and the narrow face goes. Returns the pair and the profile for round_with_tool."""
    edges = []
    for n in names:
        e = shape.Edges[int(n[4:]) - 1]
        try:
            if isinstance(e.Curve, (Part.Line, Part.Circle)):
                edges.append((n, e, shape.ancestorsOfType(e, Part.Face)))
        except Exception:
            pass
    for i, (na, ea, fa) in enumerate(edges):
        for nb, eb, fb in edges[i + 1:]:
            # Only edges of one kind either side of one face: straight and parallel, or circles on one axis.
            if type(ea.Curve) is not type(eb.Curve) or between(shape, ea, eb) is None:
                continue
            if not collide(shape, na, nb, radius):
                continue
            tool = merged_profile(shape, ea, eb, radius)
            if tool:
                return [na, nb], tool
    return None


def collide(shape, na, nb, radius):
    """Whether the two edges' own rounds would meet on the face between them: each reaches some way across it, and
    together they reach further than it is wide (or exactly as far, a full round)."""
    ra, rb = round_profile(shape, na, radius), round_profile(shape, nb, radius)
    if ra is None or rb is None:
        return False
    ea, eb = shape.Edges[int(na[4:]) - 1], shape.Edges[int(nb[4:]) - 1]
    pa = ea.valueAt((ea.FirstParameter + ea.LastParameter) / 2)
    pb = eb.valueAt((eb.FirstParameter + eb.LastParameter) / 2)
    if isinstance(ea.Curve, Part.Circle):
        # Round edges: distances in the half-plane through the axis, the same at every angle.
        centre, axis = ea.Curve.Center, V(ea.Curve.Axis)
        def flat(q):
            v = q - centre
            h = v.dot(axis)
            return V((v - axis * h).Length, h, 0)
    else:
        z = ea.tangentAt(ea.FirstParameter)
        flat = lambda q: q - z * (q - pa).dot(z)
    middle = between(shape, ea, eb)
    if middle is None:
        return False
    reach = []
    for r, p in ((ra, pa), (rb, pb)):
        across = [t for face, t in r["touch"] if face.isSame(middle[0]) or face.isSame(middle[1])]
        if not across:
            return False
        reach.append(flat(across[0]).distanceToPoint(flat(p)))
    return reach[0] + reach[1] >= flat(pa).distanceToPoint(flat(pb)) - 1e-6


def straight_section(face, kind, z, axis=None):
    """Whether the face cuts the plane across the edge in a straight line: for a straight edge, a plane or (as the
    face between) a cylinder along it; for a round edge (axis: its centre and direction), a plane square to the axis,
    or a cylinder or cone on it."""
    s = face.Surface
    if kind == "line":
        return isinstance(s, Part.Plane)
    centre, turn = axis
    if isinstance(s, Part.Plane):
        return abs(abs(s.Axis.dot(turn)) - 1) < 1e-9
    if isinstance(s, (Part.Cylinder, Part.Cone)):
        v = s.Center - centre
        return abs(abs(s.Axis.dot(turn)) - 1) < 1e-9 and (v - turn * v.dot(turn)).Length < NEAR
    return False


def merged_profile(shape, ea, eb, radius):
    """Two rounds that meet across the face between two edges, as one profile: straight parallel edges (an extrude
    along them) or circles on one axis (turned round it, as a bushing's rims are)."""
    try:
        ca, cb = ea.Curve, eb.Curve
    except Exception:
        return None
    pa = ea.valueAt((ea.FirstParameter + ea.LastParameter) / 2)
    if isinstance(ca, Part.Line) and isinstance(cb, Part.Line):
        kind = "line"
        z = ea.tangentAt(ea.FirstParameter)
        if abs(abs(z.dot(eb.tangentAt(eb.FirstParameter))) - 1) > 1e-9:
            return None
        pb = eb.valueAt((eb.FirstParameter + eb.LastParameter) / 2)
        pb = pb - z * (pb - pa).dot(z)
        x = z.cross(V(1, 0, 0)) if abs(z.x) < 0.9 else z.cross(V(0, 1, 0))
        x = x * (1.0 / x.Length)
        y = z.cross(x)
        origin, axis = pa, None
    elif isinstance(ca, Part.Circle) and isinstance(cb, Part.Circle):
        kind = "circle"
        y = V(ca.Axis)
        v = cb.Center - ca.Center
        if abs(abs(y.dot(V(cb.Axis))) - 1) > 1e-9 or (v - y * v.dot(y)).Length > NEAR:
            return None
        origin, axis = ca.Center, (ca.Center, y)
        x = (pa - origin) - y * (pa - origin).dot(y)
        if x.Length < 1e-9:
            return None
        x = x * (1.0 / x.Length)
        z = x.cross(y)
        pb = cb.Center + x * cb.Radius
        if eb.distToShape(Part.Vertex(pb))[0] > NEAR:
            return None
    else:
        return None
    fa, fb = shape.ancestorsOfType(ea, Part.Face), shape.ancestorsOfType(eb, Part.Face)
    pieces = between(shape, ea, eb)
    if pieces is None:
        return None
    middle, other = pieces
    s = middle.Surface
    if kind == "line":
        if not (isinstance(s, Part.Plane) or isinstance(s, Part.Cylinder) and abs(abs(s.Axis.dot(z)) - 1) < 1e-9):
            return None
    elif not straight_section(middle, kind, z, axis):
        return None
    flat_middle = kind == "circle" or isinstance(s, Part.Plane)
    side_a = [f for f in fa if not f.isSame(middle)][0]
    side_b = [f for f in fb if not f.isSame(other)][0]
    if not (straight_section(side_a, kind, z, axis) and straight_section(side_b, kind, z, axis)):
        return None
    local = lambda q: ((q - origin).dot(x), (q - origin).dot(y))
    flat = lambda n: (n.dot(x), n.dot(y))
    def normal(face, p):
        n = face.normalAt(*face.Surface.parameter(p))
        return flat(n) if abs(n.dot(z)) < 1e-9 else None
    a2, b2 = normal(side_a, pa), normal(side_b, pb)
    if a2 is None or b2 is None:
        return None
    qa, qb = local(pa), local(pb)
    at = lambda q: origin + x * q[0] + y * q[1]
    da, db = (-a2[1], a2[0]), (-b2[1], b2[0])
    d = da[0] * db[1] - da[1] * db[0]
    centres = []
    if abs(d) > 1e-9:
        for sa in (1, -1):
            for sb in (1, -1):
                la = (qa[0] + sa * radius * a2[0], qa[1] + sa * radius * a2[1])
                lb = (qb[0] + sb * radius * b2[0], qb[1] + sb * radius * b2[1])
                t = ((lb[0] - la[0]) * db[1] - (lb[1] - la[1]) * db[0]) / d
                centres.append(((la[0] + t * da[0], la[1] + t * da[1]), sa, sb))
    elif flat_middle:
        # Parallel sides exactly a diameter apart, with a flat end between: a full round, which FreeCAD's fillet can
        # only come near by easing the radius (at a real part's arm ends).
        apart = abs((qb[0] - qa[0]) * a2[0] + (qb[1] - qa[1]) * a2[1])
        m2 = normal(middle, pa)
        if m2 is None or abs(apart - 2 * radius) > NEAR:
            return None
        mid = ((qa[0] + qb[0]) / 2, (qa[1] + qb[1]) / 2)
        for sm in (1, -1):
            c = (mid[0] + sm * radius * m2[0], mid[1] + sm * radius * m2[1])
            sa = 1 if (c[0] - qa[0]) * a2[0] + (c[1] - qa[1]) * a2[1] > 0 else -1
            sb = 1 if (c[0] - qb[0]) * b2[0] + (c[1] - qb[1]) * b2[1] > 0 else -1
            centres.append((c, sa, sb))
    found = None
    # The ball a radius off both side faces; which side is material is read from the solid, not the normals.
    for c, sa, sb in centres:
        ta = (c[0] - sa * radius * a2[0], c[1] - sa * radius * a2[1])
        tb = (c[0] - sb * radius * b2[0], c[1] - sb * radius * b2[1])
        # The touching points have to lie on the side faces, or the round runs past them.
        if not on_side(shape, side_a, at(ta)) or not on_side(shape, side_b, at(tb)):
            continue
        # It merges only where the new round face lies wholly in the material (a convex corner) or clear of it (a
        # concave one): otherwise two separate rounds have room, and this one would cross the middle face. Only that
        # arc is looked at, as a full round usually goes round a hole.
        u = math.atan2(ta[1] - c[1], ta[0] - c[0])
        w = math.atan2(tb[1] - c[1], tb[0] - c[0])
        toward = math.atan2((qa[1] + qb[1]) / 2 - c[1], (qa[0] + qb[0]) / 2 - c[0])
        sweep = (w - u) % (2 * math.pi)
        if (toward - u) % (2 * math.pi) > sweep:
            sweep -= 2 * math.pi
        arc = [at((c[0] + 0.999 * radius * math.cos(u + sweep * k / 16), c[1] + 0.999 * radius * math.sin(u + sweep * k / 16))) for k in range(1, 16)]
        inside = [probe(shape, p) for p in arc]
        if None in inside:
            # The solid couldn't answer for a point; then it can't be told, and no merge is made.
            continue
        if all(inside) or not any(inside):
            found = (c, ta, tb, all(inside))
    if found is None:
        return None
    c, ta, tb, convex = found
    margin = min(0.1 * radius, 0.5)
    def out(q, far=0.0):
        dd = math.hypot(q[0] - c[0], q[1] - c[1])
        return (q[0] + (q[0] - c[0]) / dd * (margin + far), q[1] + (q[1] - c[1]) / dd * (margin + far))
    # The arc runs counterclockwise from its start to its end, round the side towards the middle face.
    ang = lambda q: math.atan2(q[1] - c[1], q[0] - c[0])
    a0, a1 = ang(ta), ang(tb)
    toward = ang(((qa[0] + qb[0]) / 2, (qa[1] + qb[1]) / 2))
    span = lambda s, e: (e - s) % (2 * math.pi)
    swapped = span(a0, toward) > span(a0, a1)
    if swapped:
        a0, a1 = a1, a0
    first, second, p_first, p_second = (tb, ta, qb, qa) if swapped else (ta, tb, qa, qb)
    if a1 <= a0:
        a1 += 2 * math.pi
    middle_arc = None
    if convex:
        # Cut: far enough past the edges to take in a curved middle face whole.
        far = math.hypot(qa[0] - qb[0], qa[1] - qb[1])
        corner = [out(second), out(p_second, far), out(p_first, far), out(first)]
        if kind == "circle":
            # Turned round the axis, the profile mustn't cross it.
            corner = [(max(q[0], 0.0), q[1]) for q in corner]
    else:
        # Added: exactly up to the middle face, which stays where Inventor keeps it (on a real part, a hole's wall).
        corner = [p_second, p_first]
        if kind == "line" and isinstance(s, Part.Cylinder):
            middle_arc = (local(s.Center), s.Radius)
    def along(q, n):
        r = (-n[1], n[0])
        reach = 4 * radius
        return ((q[0] - r[0] * reach, q[1] - r[1] * reach), (q[0] + r[0] * reach, q[1] + r[1] * reach))
    # Along the whole of the face it removes or fills against: Inventor's round runs on through chamfered corners
    # where the edges themselves stop short (at a real part's arm ends).
    tool = {"kind": kind, "cut": convex, "center": c, "arc": (a0, a1), "corner": corner,
            "faces": [along(qa, a2), along(qb, b2)], "swapped": swapped, "middle": middle_arc}
    if kind == "line":
        reach = [(v.Point - pa).dot(z) for v in middle.Vertexes + other.Vertexes]
        o = pa + z * ((min(reach) + max(reach)) / 2)
        tool["length"] = max(reach) - min(reach)
    else:
        # Turned round the axis, as far as the edge goes, centred on the sketch across its middle.
        o = origin
        whole = (len(ea.Vertexes) == 1 or ea.isClosed()) and (len(eb.Vertexes) == 1 or eb.isClosed())
        tool["angle"] = 360 if whole else math.degrees(min(ea.LastParameter - ea.FirstParameter, eb.LastParameter - eb.FirstParameter))
    tool["placement"] = App.Placement(App.Matrix(x.x, y.x, z.x, o.x, x.y, y.y, z.y, o.y, x.z, y.z, z.z, o.z, 0, 0, 0, 1))
    return tool


def edge_matches(ref, e, shape, tol=1e-3):
    kind = ref.get("kind")
    try:
        c = e.Curve
    except Exception:   # a curve type FreeCAD's Python side can't represent
        return False
    if kind == "line" and isinstance(c, Part.Line):
        a, b = V(*ref["a"]), V(*ref["b"])
        d = b - a
        if d.Length < 1e-9:
            return False
        d = d * (1.0 / d.Length)
        ends = [v.Point for v in e.Vertexes]
        for p in ends:
            w = p - a
            if (w - d * w.dot(d)).Length > tol:
                return False
        if ref.get("unbounded"):
            return True
        # The same stretch of the line, or a piece of it: FreeCAD may split or merge what Inventor has as one edge.
        t = sorted((p - a).dot(d) for p in ends)
        return t[1] > tol and t[0] < (b - a).Length - tol
    if kind == "circle" and isinstance(c, Part.Circle):
        n = V(*ref["b"])
        n = n * (1.0 / n.Length)
        return c.Center.distanceToPoint(V(*ref["a"])) < tol and abs(c.Radius - ref["r"]) < tol and abs(abs(c.Axis.dot(n)) - 1) < 1e-6
    if kind == "circle" and isinstance(c, Part.BSplineCurve) and ref.get("from") and ref.get("to"):
        # Where a round's radius was eased, its neighbour's arc can come out as a spline lying on the same circle.
        ends = [v.Point for v in e.Vertexes]
        a, b = V(*ref["from"]), V(*ref["to"])
        if len(ends) != 2 or not ((ends[0].distanceToPoint(a) < tol and ends[1].distanceToPoint(b) < tol) or (ends[0].distanceToPoint(b) < tol and ends[1].distanceToPoint(a) < tol)):
            return False
        centre, n = V(*ref["a"]), V(*ref["b"])
        n = n * (1.0 / n.Length)
        for k in range(1, 8):
            w = e.valueAt(e.FirstParameter + (e.LastParameter - e.FirstParameter) * k / 8.0) - centre
            if abs(w.dot(n)) > tol or abs((w - n * w.dot(n)).Length - ref["r"]) > tol:
                return False
        return True
    if kind == "curve" and ref.get("a") and ref.get("b"):
        ends = [v.Point for v in e.Vertexes]
        a, b = V(*ref["a"]), V(*ref["b"])
        if len(ends) == 2:
            return (ends[0].distanceToPoint(a) < tol and ends[1].distanceToPoint(b) < tol) or (ends[0].distanceToPoint(b) < tol and ends[1].distanceToPoint(a) < tol)
        return len(ends) == 1 and ends[0].distanceToPoint(a) < tol
    if kind == "meet":
        faces = shape.ancestorsOfType(e, Part.Face)
        if len(faces) != 2:
            return False
        (x, y) = faces
        return (same_surface(ref["s1"], x) and same_surface(ref["s2"], y)) or (same_surface(ref["s1"], y) and same_surface(ref["s2"], x))
    return False


def same_surface(want, face, tol=1e-3):
    s = face.Surface
    a = V(*want["a"]) if want.get("a") else None
    b = V(*want["b"]) if want.get("b") else None
    kind = want["kind"]
    if kind == "plane" and isinstance(s, Part.Plane):
        n = b * (1.0 / b.Length)
        return abs(abs(s.Axis.dot(n)) - 1) < 1e-6 and abs((s.Position - a).dot(n)) < tol
    if kind == "cylinder" and isinstance(s, Part.Cylinder):
        n = b * (1.0 / b.Length)
        v = s.Center - a
        return abs(abs(s.Axis.dot(n)) - 1) < 1e-6 and (v - n * v.dot(n)).Length < tol and abs(s.Radius - want["r"]) < tol
    if kind == "sphere" and isinstance(s, Part.Sphere):
        return s.Center.distanceToPoint(a) < tol and abs(s.Radius - want["r"]) < tol
    if kind == "torus" and isinstance(s, Part.Toroid):
        return s.Center.distanceToPoint(a) < tol and abs(s.MajorRadius - want["r"]) < tol and abs(s.MinorRadius - want["r2"]) < tol
    if kind == "cone" and isinstance(s, Part.Cone):
        n = b * (1.0 / b.Length)
        return abs(abs(s.Axis.dot(n)) - 1) < 1e-6
    return False
