# Builds a FreeCAD assembly document from a converter assembly model (invre convert on an .iam): one App::Link per
# occurrence, to the part or sub-assembly file already converted beside it, where Inventor has it; then a joint for each
# constraint FreeCAD has an equal for. A joint goes in only if the assembly still solves with it and nothing moves from where
# Inventor has it. The rest are left out and listed, and a part with no joint is simply free.
#   CADCONVERT_MODEL  the assembly .json     CADCONVERT_OUT  the .FCStd to write     CADCONVERT_REPORT  the report .json
import json, os, re, time, traceback
import FreeCAD as App
import Part
import JointObject

V = App.Vector
report = {"occurrences": 0, "linked": 0, "suppressed": 0, "hidden": 0, "missing": [], "notes": []}
App.ParamGet("User parameter:BaseApp/Preferences/Document").SetBool("CreateBackupFiles", False)
NEAR_MM = 0.01
MOVED_MM = 0.05
MOVED_RAD = 1e-3
JOINT_SECONDS = 300


def targets(doc):
    """What an occurrence shows of its file: the assembly, Inventor's exact solid if it had to be added, else the bodies."""
    a = doc.getObject("Assembly")
    if a is not None:
        return [a]
    frozen = doc.getObject("Inventor_solid")
    if frozen is not None:
        return [frozen]
    bodies = [o for o in doc.Objects if o.TypeId == "PartDesign::Body" and o.Visibility and not o.Shape.isNull()]
    return bodies


# ---------------------------------------------------------------- the geometry a constraint holds

def parse(text):
    """A constraint's geometry, from its text, in mm: a kind, a point, a direction, radii."""
    w = text.split()
    if not w or w[0] == "?":
        return None
    numbers = [float(x) for x in w[1:] if re.match(r"^-?[\d.]+(E[-+]?\d+)?$", x, re.I)]
    g = {"kind": w[0], "p": V(*numbers[:3]) * 10}
    if w[0] in ("plane", "line", "circle", "cylinder", "torus") and len(numbers) >= 6:
        g["d"] = V(*numbers[3:6]).normalize()
    if w[0] in ("circle", "cylinder") and len(numbers) >= 7:
        g["r"] = numbers[6] * 10
    if w[0] == "sphere" and len(numbers) >= 4:
        g["r"] = numbers[3] * 10
    if w[0] == "torus" and len(numbers) >= 8:
        g["radii"] = (numbers[6] * 10, numbers[7] * 10)
    return g


def frame(p, z):
    """A placement at p with its Z along z."""
    return App.Placement(p, App.Rotation(V(0, 0, 1), z))


def leaves(link, placement, prefix):
    """The solids an occurrence holds, through any sub-assemblies: (shape, its placement in this assembly, the path to it)."""
    target = link.LinkedObject
    if target is None:
        return
    if target.TypeId == "Assembly::AssemblyObject":
        for child in target.Group:
            if child.TypeId == "App::Link":
                yield from leaves(child, placement.multiply(child.Placement), prefix + child.Name + ".")
    elif hasattr(target, "Shape") and not target.Shape.isNull():
        yield target.Shape, placement, prefix


def same_line(p, d, q, e):
    return abs(abs(d.dot(e)) - 1) < 1e-6 and (q - p - e * (q - p).dot(e)).Length < NEAR_MM


def find_element(links, g):
    """The face, edge or vertex of an occurrence that carries a geometry: its link and the path to it within that link."""
    kind = g["kind"]
    for link in links:
        for shape, plc, prefix in leaves(link, link.Placement, ""):
            inverse = plc.inverse()
            p = inverse.multVec(g["p"])
            d = inverse.Rotation.multVec(g["d"]) if "d" in g else None
            if kind == "plane":
                for i, f in enumerate(shape.Faces):
                    s = f.Surface
                    if isinstance(s, Part.Plane) and abs(abs(s.Axis.dot(d)) - 1) < 1e-6 and abs((p - s.Position).dot(s.Axis)) < NEAR_MM:
                        return link, prefix + "Face%d" % (i + 1), plc
            elif kind == "circle":
                for i, e in enumerate(shape.Edges):
                    c = getattr(e, "Curve", None) if not e.Degenerated else None
                    if isinstance(c, Part.Circle) and c.Center.distanceToPoint(p) < NEAR_MM and abs(abs(c.Axis.dot(d)) - 1) < 1e-6 and abs(c.Radius - g.get("r", c.Radius)) < NEAR_MM:
                        return link, prefix + "Edge%d" % (i + 1), plc
            if kind in ("line", "cylinder", "circle"):
                for i, f in enumerate(shape.Faces):
                    s = f.Surface
                    if isinstance(s, (Part.Cylinder, Part.Cone)) and same_line(p, d, s.Center if isinstance(s, Part.Cylinder) else s.Apex, s.Axis) and (kind != "cylinder" or abs(getattr(s, "Radius", -1) - g.get("r", -2)) < NEAR_MM):
                        return link, prefix + "Face%d" % (i + 1), plc
            if kind == "line":
                for i, e in enumerate(shape.Edges):
                    c = getattr(e, "Curve", None) if not e.Degenerated else None
                    if isinstance(c, Part.Line) and same_line(p, d, c.Location, c.Direction):
                        return link, prefix + "Edge%d" % (i + 1), plc
            if kind == "point":
                for i, v in enumerate(shape.Vertexes):
                    if v.Point.distanceToPoint(p) < NEAR_MM:
                        return link, prefix + "Vertex%d" % (i + 1), plc
                for i, e in enumerate(shape.Edges):
                    c = getattr(e, "Curve", None) if not e.Degenerated else None
                    if isinstance(c, Part.Circle) and c.Center.distanceToPoint(p) < NEAR_MM:
                        return link, prefix + "Edge%d" % (i + 1), plc
            if kind == "sphere":
                for i, f in enumerate(shape.Faces):
                    s = f.Surface
                    if isinstance(s, Part.Sphere) and s.Center.distanceToPoint(p) < NEAR_MM and abs(s.Radius - g["r"]) < NEAR_MM:
                        return link, prefix + "Face%d" % (i + 1), plc
            if kind == "torus":
                for i, f in enumerate(shape.Faces):
                    s = f.Surface
                    if isinstance(s, Part.Toroid) and s.Center.distanceToPoint(p) < NEAR_MM and abs(abs(s.Axis.dot(d)) - 1) < 1e-6:
                        return link, prefix + "Face%d" % (i + 1), plc
    return None


def stand_in(links, kind="any"):
    """An element to hang a joint on whose connectors are worked out here. It says which part moves, and for a Distance joint
    its type says what is being held: a plane, a line, a point. A work plane or an axis has no face, and needn't."""
    for link in links:
        for shape, plc, prefix in leaves(link, link.Placement, ""):
            if kind == "plane":
                for i, f in enumerate(shape.Faces):
                    if isinstance(f.Surface, Part.Plane):
                        return link, prefix + "Face%d" % (i + 1), plc
            elif kind == "line":
                for i, e in enumerate(shape.Edges):
                    if not e.Degenerated and isinstance(getattr(e, "Curve", None), Part.Line):
                        return link, prefix + "Edge%d" % (i + 1), plc
            elif kind == "point":
                if shape.Vertexes:
                    return link, prefix + "Vertex1", plc
            elif shape.Faces:
                return link, prefix + "Face1", plc
    return None


# ---------------------------------------------------------------- joints

class Joints:
    def __init__(self, doc, assembly, group, parts):
        self.doc, self.assembly, self.group, self.parts = doc, assembly, group, parts
        self.worst = 0.0
        self.started = time.time()

    def where(self):
        return {l.Name: App.Placement(l.Placement) for l in self.parts}

    def moved(self, before):
        mm = rad = 0.0
        for l in self.parts:
            a, b = before[l.Name], l.Placement
            # How far the part's own far corner travels covers turning about any point, not only its origin.
            mm = max(mm, a.Base.distanceToPoint(b.Base))
            rad = max(rad, abs((a.Rotation.inverted() * b.Rotation).Angle))
        return mm, rad

    def attempt(self, make):
        """A joint stays only if the assembly solves with it and everything stays where it was."""
        before = self.where()
        j = self.group.newObject("App::FeaturePython", "Joint")
        try:
            make(j)
            self.doc.recompute()
            result = self.assembly.solve()
            mm, rad = self.moved(before)
            if result == 0 and j.isValid() and mm < MOVED_MM and rad < MOVED_RAD:
                self.worst = max(self.worst, mm)
                return j
            why = "the solver refused it (%s)" % result if result != 0 else "it would move parts %.3f mm" % mm
        except Exception as ex:
            why = "%s: %s" % (type(ex).__name__, str(ex)[:80])
        for l in self.parts:
            l.Placement = before[l.Name]
        self.group.removeObject(j)
        self.doc.removeObject(j.Name)
        self.doc.recompute()
        self.assembly.solve()
        for l in self.parts:
            l.Placement = before[l.Name]
        self.last_why = why
        return None

    def from_elements(self, kind, a, b, distance=0.0):
        def make(j):
            JointObject.Joint(j, JointObject.JointTypes.index(kind))
            j.Reference1 = (a[0], [a[1], a[1]])
            j.Reference2 = (b[0], [b[1], b[1]])
            j.Distance = distance
        return make

    def from_frames(self, kind, a, b, world1, world2=None, distance=0.0):
        """Connectors worked out from the geometry: each is a frame in the assembly, turned into its own solid's coordinates."""
        def make(j):
            JointObject.Joint(j, JointObject.JointTypes.index(kind))
            j.Detach1 = True
            j.Detach2 = True
            j.Reference1 = (a[0], [a[1], a[1]])
            j.Reference2 = (b[0], [b[1], b[1]])
            j.Placement1 = a[2].inverse().multiply(world1)
            j.Placement2 = b[2].inverse().multiply(world2 or world1)
            j.Distance = distance
        return make


def carry(js, c, geos, sides):
    """One constraint, as the joint(s) to try, in order. Returns the joint, or why not."""
    kinds = [g["kind"] for g in geos]
    has = lambda *k: sorted(kinds) == sorted(k)
    d = c["values"][0] * 10 if c["values"] else 0.0
    t = c["type"]
    js.last_why = None

    def elements():
        a, b = find_element(sides[0], geos[0]), find_element(sides[1], geos[1])
        if a is None or b is None:
            js.last_why = "no face or edge on the converted part carries its geometry"
            return None
        return a, b

    def hangers(ka="any", kb="any"):
        a, b = stand_in(sides[0], ka), stand_in(sides[1], kb)
        if a is None or b is None:
            js.last_why = "the part has no %s to hang it on" % (ka if a is None else kb)
            return None
        return a, b

    # What a Distance joint is told it holds, and the frame it holds it by.
    def holds(g):
        if g["kind"] == "plane":
            return "plane", frame(g["p"], g["d"])
        if g["kind"] in ("line", "circle", "cylinder"):
            return "line", frame(g["p"], g["d"])
        return "point", frame(g["p"], V(0, 0, 1))

    def try_all(makes):
        for m in makes:
            j = js.attempt(m)
            if j is not None:
                return j
        return None

    if t == 5 or t in (0x0E, 0x10, 0x11, 0x0F, 0x12):    # Insert, Rotational, Rigid, Ball, Slider, Cylindrical
        h = hangers()
        if h is None:
            return None
        z = geos[0].get("d", V(0, 0, 1))
        kind = {5: "Fixed" if c["settings"] & 0x2000 else "Revolute", 0x0E: "Revolute", 0x10: "Fixed", 0x11: "Ball", 0x0F: "Slider", 0x12: "Cylindrical"}[t]
        # An Insert or a Rigid holds its parts as they are now. Inventor's other joints have an origin in each part, and what lies
        # between them along the axis is free to change.
        if t in (5, 0x10, 0x11):
            return try_all([js.from_frames(kind, h[0], h[1], frame(geos[0]["p"], z))])
        return try_all([js.from_frames(kind, h[0], h[1], frame(geos[0]["p"], z), frame(geos[1]["p"], z))])
    if t in (1, 2):                                       # Mate, Flush
        if has("line", "line") and abs(d) < 1e-6:
            h = hangers()
            return try_all([js.from_frames("Cylindrical", h[0], h[1], frame(geos[0]["p"], geos[0]["d"]))]) if h else None
        if has("point", "point") and abs(d) < 1e-6:
            h = hangers()
            return try_all([js.from_frames("Ball", h[0], h[1], frame(geos[0]["p"], V(0, 0, 1)))]) if h else None
        if not all(g["kind"] in ("plane", "line", "circle", "cylinder", "point") for g in geos):
            e = elements()
            return try_all([js.from_elements("Distance", e[0], e[1], x) for x in (d, -d)]) if e else None
        (ka, wa), (kb, wb) = holds(geos[0]), holds(geos[1])
        h = hangers(ka, kb)
        return try_all([js.from_frames("Distance", h[0], h[1], wa, wb, x) for x in dict.fromkeys((d, -d))]) if h else None
    if t == 3:                                            # Angle
        # A plain direction reads as a point: the file holds no place for it.
        for g in geos:
            if "d" not in g:
                g["d"] = App.Vector(g["p"]).normalize()
                g["p"] = V(0, 0, 0)
        h = hangers()
        if h is None:
            return None
        import math
        now = math.degrees(geos[0]["d"].getAngle(geos[1]["d"]))
        def make(j):
            js.from_frames("Angle", h[0], h[1], frame(geos[0]["p"], geos[0]["d"]), frame(geos[1]["p"], geos[1]["d"]))(j)
            j.Angle = now
        return try_all([make])
    if t == 4:                                            # Tangent
        e = elements()
        if e is None:
            return None
        radii = [g.get("r", 0.0) for g in geos]
        values = [d, sum(radii) + d, abs(radii[0] - radii[1]) + d, -d]
        return try_all([js.from_elements("Distance", e[0], e[1], x) for x in dict.fromkeys(values)])
    js.last_why = "FreeCAD has no equal for a %s constraint" % c["kind"]
    return None


def add_joints(doc, assembly, model, occurrence_links):
    group = assembly.newObject("Assembly::JointGroup", "Joints")
    parts = [l for links in occurrence_links for l in links]
    js = Joints(doc, assembly, group, parts)
    grounded = 0
    for occ, links in zip(model["occurrences"], occurrence_links):
        if occ.get("grounded") and links:
            g = group.newObject("App::FeaturePython", "Grounded")
            JointObject.GroundedJoint(g, links[0])
            grounded += 1
    if grounded == 0 and parts:
        g = group.newObject("App::FeaturePython", "Grounded")
        JointObject.GroundedJoint(g, parts[0])
        report["notes"].append("nothing is grounded in Inventor, so %s is, as FreeCAD needs one" % parts[0].Label)
    # An occurrence of several bodies moves as one.
    for links in occurrence_links:
        for other in links[1:]:
            a, b = stand_in([links[0]]), stand_in([other])
            if a and b:
                js.attempt(js.from_frames("Fixed", a, b, App.Placement()))
    still = next((links for occ, links in zip(model["occurrences"], occurrence_links) if occ.get("grounded") and links), parts[:1])
    total = carried = 0
    why = []
    tally = {}
    for c in model.get("constraints", []):
        total += 1
        label = "%s (%s)" % (c["name"], c["kind"])
        def leave(text):
            why.append("%s: %s" % (label, text))
            tally[text.split(":")[0][:40]] = tally.get(text.split(":")[0][:40], 0) + 1
        if c["suppressed"]:
            leave("suppressed in Inventor")
            continue
        if c["failing"]:
            leave("failing in Inventor")
            continue
        if len(c["occurrences"]) != 2 or len(c["geometry"]) != 2:
            leave("FreeCAD has no equal for this constraint, with %d arguments" % len(c["occurrences"]))
            continue
        # The assembly's own planes and axes don't move, and neither does what's grounded.
        sides = [occurrence_links[i] if 0 <= i < len(occurrence_links) else still for i in c["occurrences"]]
        geos = [parse(g) for g in c["geometry"]]
        # A joint's origin and a plain direction are stored in the part's own coordinates, not the assembly's.
        for i, g, axis in zip(c["occurrences"], geos, c.get("axes") or [None, None]):
            if g is None or i < 0:
                continue
            m = model["occurrences"][i].get("matrix")
            place = App.Placement(App.Matrix(m[0], m[1], m[2], m[3] * 10, m[4], m[5], m[6], m[7] * 10, m[8], m[9], m[10], m[11] * 10, 0, 0, 0, 1)) if m else App.Placement()
            if axis:
                g["d"] = place.Rotation.multVec(V(*axis))
                g["p"] = place.multVec(g["p"])
            elif c["type"] == 3 and g["kind"] == "point":
                g["d"] = place.Rotation.multVec(App.Vector(g["p"]).normalize())
                g["p"] = V(0, 0, 0)
        if not sides[0] or not sides[1] or None in geos:
            leave("one of its parts isn't in the file")
            continue
        if sides[0][0] is sides[1][0]:
            if -1 in c["occurrences"]:
                # Held to the assembly, and grounded here: nothing more to do.
                carried += 1
            else:
                leave("both ends are in one rigid sub-assembly")
            continue
        if time.time() - js.started > JOINT_SECONDS:
            leave("out of time")
            continue
        j = carry(js, c, geos, sides)
        if j is None:
            leave(js.last_why or "no joint worked")
            continue
        j.Label = c["name"]
        carried += 1
    report["joints"] = {"total": total, "carried": carried, "left_out": total - carried, "moved_mm": js.worst, "why": why, "tally": tally, "seconds": round(time.time() - js.started, 1)}


def main():
    with open(os.environ["CADCONVERT_MODEL"], encoding="utf-8") as fh:
        model = json.load(fh)
    report["name"] = model["name"]
    doc = App.newDocument(re.sub(r"\W", "_", model["name"]) or "Assembly")
    assembly = doc.addObject("Assembly::AssemblyObject", "Assembly")
    assembly.Label = model["name"]
    # FreeCAD links between files only from a document that has been saved.
    doc.saveAs(os.environ["CADCONVERT_OUT"])
    opened = {}
    occurrence_links = []
    for occ in model["occurrences"]:
        report["occurrences"] += 1
        links = []
        occurrence_links.append(links)
        if occ.get("suppressed"):
            report["suppressed"] += 1
            continue
        path = occ.get("target")
        if not path or not os.path.exists(path):
            report["missing"].append(occ["name"] + " (" + str(occ.get("file")) + ")")
            continue
        if path not in opened:
            opened[path] = App.openDocument(path)
        objects = targets(opened[path])
        if not objects:
            report["missing"].append(occ["name"] + " (nothing solid in " + os.path.basename(path) + ")")
            continue
        m = occ["matrix"] or [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0]
        matrix = App.Matrix(m[0], m[1], m[2], m[3] * 10, m[4], m[5], m[6], m[7] * 10, m[8], m[9], m[10], m[11] * 10, 0, 0, 0, 1)
        try:
            placement = App.Placement(matrix)
        except Exception:
            report["notes"].append("%s has a placement FreeCAD can't hold (mirrored?)" % occ["name"])
            placement = App.Placement()
        for o in objects:
            link = assembly.newObject("App::Link", "Link")
            link.Label = occ["name"]
            link.LinkedObject = o
            link.Placement = placement
            if not occ.get("visible", True):
                link.Visibility = False
            links.append(link)
        report["linked"] += 1
        if not occ.get("visible", True):
            report["hidden"] += 1
    doc.recompute()
    if os.environ.get("CADCONVERT_NO_JOINTS") == "1":
        report["notes"].append("FreeCAD's solver stopped on this assembly, so it has no joints: its parts are where Inventor has them, and free")
    else:
        add_joints(doc, assembly, model, occurrence_links)
    doc.recompute()
    report["errors"] = [o.Label for o in doc.Objects if not o.isValid()]
    doc.saveAs(os.environ["CADCONVERT_OUT"])
    shape = Part.getShape(assembly)
    if not shape.isNull():
        b = shape.BoundBox
        report["bbox"] = [b.XMin, b.YMin, b.ZMin, b.XMax, b.YMax, b.ZMax]
        report["solids"] = len(shape.Solids)
        report["volume"] = sum(s.Volume for s in shape.Solids)
    doc.save()
    App.closeDocument(doc.Name)


try:
    main()
except Exception as ex:
    report["fatal"] = "%s: %s" % (type(ex).__name__, str(ex))
    report["trace"] = traceback.format_exc()
with open(os.environ["CADCONVERT_REPORT"], "w", encoding="utf-8") as fh:
    json.dump(report, fh, indent=1)
