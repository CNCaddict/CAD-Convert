# Builds a FreeCAD document from a converter model (invre model), using FreeCAD's own engine, then checks the result
# against what Inventor's B-rep says it should be. Run by FreeCADCmd; paths come from the environment:
#   CADCONVERT_MODEL  the model .json     CADCONVERT_OUT  the .FCStd to write     CADCONVERT_REPORT  the report .json
import copy, json, math, os, re, time, traceback
import FreeCAD as App
import Part, Sketcher
import sys
sys.path.insert(0, os.environ.get("CADCONVERT_TOOLS") or os.path.dirname(os.path.abspath(__file__)))
from rounds import probe, holes_to_reopen, section_curve, ball_center, round_profile, revolved_round, merged_round, collide, merged_profile, edge_matches, same_surface

V = App.Vector
LENGTH_UNITS = {"mm", "cm", "m", "km", "in", "ft", "yd", "mil", "micron", "nm", "um"}
ANGLE_UNITS = {"deg", "rad", "gon", "grad"}
UNIT_WORDS = LENGTH_UNITS | ANGLE_UNITS | {"ul", "kg", "g", "lbmass", "lbm", "s", "min", "hr", "N", "lbf", "psi", "Pa"}
FUNCTIONS = {"cos": "cos", "sin": "sin", "tan": "tan", "acos": "acos", "asin": "asin", "atan": "atan", "sqrt": "sqrt",
             "exp": "exp", "pow": "pow", "ln": "log", "log": "log10", "floor": "floor", "ceil": "ceil", "round": "round",
             "abs": "abs", "min": "min", "max": "max"}
RESERVED = {"Label", "Label2", "Visibility", "ExpressionEngine"}
# Sketch members that aren't rules between curves: a spline's shape comes with its poles, a frame belongs to a text or image.
NOT_CONSTRAINTS = {"SplineFitPoint2D", "SplineHandle2D", "TextBox2D", "1FBB3C01", "8FEC335F"}
FILLET_SEARCH_SECONDS = 60
SKETCH_RETRY_SECONDS = 60
SKETCH_SECONDS = 150
POLYGON_RULES = 60
SURFACE_FAR_MM = 0.05
SURFACE_FAR_LIMIT = 0.02
SURFACE_CHECK_SECONDS = 40

report = {"sketches": [], "features": [], "parameters": [], "notes": []}
App.ParamGet("User parameter:BaseApp/Preferences/Document").SetBool("CreateBackupFiles", False)


def note(text):
    report["notes"].append(text)


# ---------------------------------------------------------------- parameters

class Parameters:
    def __init__(self, doc, model):
        self.doc = doc
        self.names = {}     # Inventor's name -> the property's name
        self.model = {p["name"]: p for p in model["parameters"]}
        self.obj = doc.addObject("App::VarSet", "Parameters") if model["parameters"] else None
        for p in model["parameters"]:
            self.add(p)
        for p in model["parameters"]:
            self.formula(p)
        doc.recompute()
        for p in model["parameters"]:
            self.check(p)

    def add(self, p):
        unit = p["unit"]
        kind = "App::PropertyDistance" if unit in LENGTH_UNITS else "App::PropertyAngle" if unit in ANGLE_UNITS else "App::PropertyFloat" if unit == "ul" else "App::PropertyQuantity"
        name = re.sub(r"\W", "_", p["name"])
        if name in RESERVED or not self.usable(name):
            name += "_"
            note("Parameter %s is named %s here: FreeCAD reads %s as a unit or constant." % (p["name"], name, p["name"]))
        self.obj.addProperty(kind, name, "Inventor")
        self.names[p["name"]] = name

    # Names FreeCAD reads as units or constants (m, e, ...) can't be used in expressions.
    def usable(self, name):
        try:
            App.Units.Quantity("1 " + name)
            return False
        except Exception:
            pass
        return name not in ("e", "pi")

    def translate(self, formula):
        out, last = [], None
        for token in re.findall(r"\d+\.?\d*(?:[eE][-+]?\d+)?|\.\d+|\w+|\S", formula):
            if re.match(r"[\d.]", token):
                out.append(token)
            elif re.match(r"[A-Za-z_]\w*$", token):
                after_number = last is not None and re.match(r"[\d.]", last)
                if after_number and token in UNIT_WORDS:
                    if token != "ul":
                        out.append("lbm" if token == "lbmass" else token)
                elif token in self.names:
                    out.append("Parameters." + self.names[token])
                elif token in FUNCTIONS:
                    out.append(FUNCTIONS[token])
                elif token in ("PI", "E"):
                    out.append(token.lower())
                elif token in UNIT_WORDS:
                    out.append(token)
                else:
                    raise ValueError("no equivalent for " + token)
            else:
                out.append(token)
            last = token
        # FreeCAD wants a function's bracket right after its name.
        return re.sub(r"\b([a-z0-9]+) \(", lambda m: m.group(1) + "(" if m.group(1) in FUNCTIONS.values() else m.group(0), " ".join(out))

    def formula(self, p):
        name = self.names[p["name"]]
        status = {"name": p["name"], "formula": p["formula"], "shown": p["shown"], "status": "ok"}
        report["parameters"].append(status)
        try:
            text = self.translate(p["formula"])
            self.obj.setExpression(name, text)
            status["freecad"] = text
        except Exception as ex:
            status["status"] = "value only"
            status["why"] = str(ex)[:120]
            self.set_value(p, name)

    def set_value(self, p, name):
        try:
            self.obj.setExpression(name, None)
        except Exception:
            pass
        try:
            setattr(self.obj, name, App.Units.Quantity(p["shown"].replace(" ul", "")))
        except Exception:
            setattr(self.obj, name, self.expected(p) or 0.0)

    def expected(self, p):
        if p["unit"] in LENGTH_UNITS:
            return p["value"] * 10
        if p["unit"] in ANGLE_UNITS:
            return math.degrees(p["value"])
        if p["unit"] == "ul":
            return p["value"]
        return None

    def check(self, p):
        name = self.names[p["name"]]
        want = self.expected(p)
        status = next(s for s in report["parameters"] if s["name"] == p["name"])
        if want is None:
            return
        got = getattr(self.obj, name)
        got = got.Value if hasattr(got, "Value") else float(got)
        status["value"] = got
        if abs(got - want) > 1e-6 * max(1, abs(want)):
            status["status"] = "value only"
            status["why"] = "the formula gave %r, Inventor has %r" % (got, want)
            self.set_value(p, name)
            self.doc.recompute()

    def ref(self, inventor_name):
        return "Parameters." + self.names[inventor_name] if inventor_name in self.names else None

    def value(self, inventor_name, default=None):
        p = self.model.get(inventor_name)
        return self.expected(p) if p else default


# ---------------------------------------------------------------- sketches

def matrix_of(m):
    return App.Matrix(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15])


def attach(sk, body, m, on=None):
    """On the origin plane the sketch lies parallel to, with the offset that puts it where Inventor has it. What
    Inventor drew it on (a face of the solid so far, or a work plane) is noted, and reattach_all attaches it there once
    the whole part is built: a feature can still change afterwards (a retry without face merging renumbers its faces),
    and a sketch on one of its faces would jump (on some real parts the volume nearly doubled)."""
    want = App.Placement(matrix_of(m))
    place_sketch(sk, body, want)
    work = on and on.get("kind") == "plane" and on.get("origin") and on.get("name") not in ("XY Plane", "XZ Plane", "YZ Plane")
    if on and (on.get("kind") == "face" or work):
        to_reattach.append((sk, body, body.Tip, want, on))
    return "origin plane"


work_planes = {}   # (body name, work plane name) -> its datum plane
to_reattach = []   # (sketch, body, the feature before it, where it goes, what it was drawn on)


def alive(o):
    """Still in the document: reading anything of a deleted object raises."""
    try:
        return o is not None and o.Document is not None
    except ReferenceError:
        return False


def reattach_all(doc, bodies):
    """Each noted sketch on its face or work plane, now the part is built; kept only if the part comes out the same,
    else they all go back on their origin planes. Returns what each got, by sketch name."""
    if not to_reattach:
        return None
    bodies = [b for b in bodies if alive(b)]
    before = [b.Shape.Volume if not b.Shape.isNull() else 0.0 for b in bodies]
    got = {}
    for sk, body, tip, want, on in to_reattach:
        # Not if the sketch, or the feature it would hang on, was taken back.
        if not alive(sk) or not alive(body) or (on["kind"] == "face" and not alive(tip)):
            continue
        kind = attach_to(sk, body, want, on, tip)
        if kind:
            got[sk.Name] = kind
    if not got:
        return {}
    doc.recompute()
    after = [b.Shape.Volume if not b.Shape.isNull() else 0.0 for b in bodies]
    # Within 10 parts per million: a plain recompute of an unchanged real part already moves it by about one (0.07 mm³
    # of 68,586), while a sketch that jumped to the wrong face moves it by percent.
    same = all(abs(a - b) <= 1e-5 * max(1.0, abs(b)) for a, b in zip(after, before)) and all(o.isValid() for b in bodies for o in b.Group)
    if not same:
        for sk, body, tip, want, on in to_reattach:
            if alive(sk) and sk.Name in got:
                place_sketch(sk, body, want)
        doc.recompute()
        return {}
    return got


def attach_to(sk, body, want, on, tip=None):
    """Attached to the face or work plane, if found and if the sketch then lands exactly where Inventor has it."""
    if on["kind"] == "face":
        tip = tip if tip is not None else body.Tip
        refs = [a for alt in on.get("face") or [] for a in alt if a.get("kind") == "plane"]
        if tip is None or tip.Shape.isNull() or not refs:
            return None
        found = [(i, f) for i, f in enumerate(tip.Shape.Faces) if isinstance(f.Surface, Part.Plane) and same_surface(refs[0], f)]
        if not found:
            return None
        # Of the faces on that plane, the one nearest the sketch's origin.
        i, _ = min(found, key=lambda c: c[1].distToShape(Part.Vertex(want.Base))[0])
        support, got = [(tip, "Face%d" % (i + 1))], "face"
    elif on["kind"] == "plane" and on.get("origin") and on.get("normal") and on.get("name") not in ("XY Plane", "XZ Plane", "YZ Plane"):
        key = (body.Name, on.get("name"))
        d = work_planes.get(key)
        if not alive(d):
            d = body.newObject("PartDesign::Plane", "Plane")
            d.Label = on.get("name") or "Work Plane"
            d.MapMode = "Deactivated"
            d.Placement = App.Placement(V(*on["origin"]), App.Rotation(V(0, 0, 1), V(*on["normal"])))
            d.Visibility = False
            work_planes[key] = d
        support, got = [(d, "")], "work plane"
    else:
        return None
    sk.AttachmentSupport = support
    sk.MapMode = "FlatFace"
    sk.AttachmentOffset = App.Placement()
    try:
        sk.positionBySupport()
        sk.AttachmentOffset = sk.Placement.inverse().multiply(want)
        sk.positionBySupport()
        ok = sk.Placement.isSame(want, 1e-7)
    except Exception:
        ok = False
    if not ok:
        sk.AttachmentSupport = []
        sk.MapMode = "Deactivated"
        sk.AttachmentOffset = App.Placement()
        return None
    return got


def place_sketch(sk, body, want):
    origin = {f.Role: f for f in body.Origin.OriginFeatures}
    bases = [("XY_Plane", App.Placement()), ("XZ_Plane", App.Placement(V(), App.Rotation(V(1, 0, 0), 90))), ("YZ_Plane", App.Placement(V(), App.Rotation(V(1, 1, 1), 120)))]
    best = bases[0]
    for role, base in bases:
        if (base.inverse().multiply(want)).Rotation.isIdentity(1e-9):
            best = (role, base)
            break
    sk.AttachmentSupport = [(origin[best[0]], "")]
    sk.MapMode = "FlatFace"
    sk.AttachmentOffset = best[1].inverse().multiply(want)


class Restart(Exception):
    pass


def same_curve(sk, g, taken, tol=1e-5):
    """The sketch's construction curve that is exactly this outline curve, or None."""
    def together(a, b):
        return a.distanceToPoint(b) < tol
    for k in range(sk.GeometryCount):
        h = sk.Geometry[k]
        if k in taken or not sk.getConstruction(k) or type(h) is not type(g):
            continue
        if isinstance(g, (Part.LineSegment, Part.BSplineCurve)):
            ends = True
        elif isinstance(g, (Part.ArcOfCircle, Part.Circle)):
            ends = together(g.Center, h.Center) and abs(g.Radius - h.Radius) < tol
        else:
            ends = False
        if not ends:
            continue
        if isinstance(g, Part.Circle):
            return k
        if not ((together(g.StartPoint, h.StartPoint) and together(g.EndPoint, h.EndPoint)) or (together(g.StartPoint, h.EndPoint) and together(g.EndPoint, h.StartPoint))):
            continue
        # An arc and the rest of its circle share both ends.
        if isinstance(g, Part.ArcOfCircle) and not together(g.value((g.FirstParameter + g.LastParameter) / 2), h.value((h.FirstParameter + h.LastParameter) / 2)):
            continue
        return k
    return None


def related(expr, want, have, angle):
    """The formula tying a dimension to its parameter, where the two measure different things: a diameter dimension on a
    half profile (the parameter is twice the distance), or the other angle between two lines. None if no simple one fits."""
    a, b = abs(want), abs(have)
    tol = 1e-4
    formula = None
    if abs(a - b) <= tol:
        formula = expr
    elif abs(a - 2 * b) <= tol:
        formula = "%s / 2" % expr
    elif abs(2 * a - b) <= tol:
        formula = "%s * 2" % expr
    elif angle and abs(a + b - 180) <= tol:
        formula = "180 deg - %s" % expr
    elif angle and abs(a + b - 360) <= tol:
        formula = "360 deg - %s" % expr
    if formula is None:
        return None
    # FreeCAD keeps which way a distance runs in its sign.
    return "-(%s)" % formula if have < 0 else formula


def spline_of(c):
    """A B-spline from Inventor's flat knot list and poles."""
    poles = [V(c["poles"][i], c["poles"][i + 1], 0) for i in range(0, len(c["poles"]), 2)]
    knots, mults = [], []
    for k in c["knots"]:
        if knots and abs(k - knots[-1]) < 1e-12:
            mults[-1] += 1
        else:
            knots.append(k)
            mults.append(1)
    s = Part.BSplineCurve()
    s.buildFromPolesMultsKnots(poles, mults, knots, False, c["degree"])
    return s


def cut_back(shape, ref, point):
    """How far from a point the faces on a named plane or cylinder now begin, or None if none are found."""
    found = []
    for face in shape.Faces:
        s = face.Surface
        if ref["kind"] == "plane" and isinstance(s, Part.Plane):
            on = abs(s.Axis.dot(V(*ref["b"]))) > 1 - 1e-6 and abs((V(*ref["a"]) - s.Position).dot(s.Axis)) < 1e-5
        elif ref["kind"] == "cylinder" and isinstance(s, Part.Cylinder):
            on = abs(s.Axis.dot(V(*ref["b"]))) > 1 - 1e-6 and abs(s.Radius - ref["r"]) < 1e-5 and (V(*ref["a"]) - s.Center).cross(s.Axis).Length < 1e-5
        else:
            on = False
        if on:
            found.append(Part.Vertex(point).distToShape(face)[0])
    return min(found) if found else None


def whole_circle_profile(f):
    """The same socket profile can be stored as the polygon plus slivers of the circle, each a side and an arc. Then it
    means the polygon and the whole circle."""
    loops, wires = f.get("loops"), f.get("wires")
    if not loops or not wires or len(loops) != len(wires) or len(wires) < 2:
        return f
    polygon = [i for i, w in enumerate(wires) if all(e["kind"] == "line" for e in w)]
    arcs = [e for w in wires for e in w if e["kind"] == "circle"]
    if len(polygon) != 1 or not arcs or any(len(w) != 2 or sum(e["kind"] == "circle" for e in w) != 1 for i, w in enumerate(wires) if i != polygon[0]):
        return f
    sides = set(loops[polygon[0]])
    circle = set(x for i, l in enumerate(loops) if i != polygon[0] for x in l) - sides
    if len(circle) != 1:
        return f
    whole = dict(arcs[0], to=arcs[0]["from"])
    return dict(f, loops=[list(circle), loops[polygon[0]]], wires=[[whole], wires[polygon[0]]])


def touching_loops(f):
    """Which of a profile's two loops is a circle that the other's corners all sit on, if so."""
    loops, wires = f.get("loops"), f.get("wires")
    if not loops or not wires or len(loops) != 2 or len(wires) != 2:
        return None
    for i in (0, 1):
        if all(e["kind"] == "circle" for e in wires[i]):
            centre, r = V(*wires[i][0]["a"]), wires[i][0]["r"]
            corners = [V(*e["a"]) for e in wires[1 - i] if e["kind"] == "line"]
            if corners and all(abs((p - centre).Length - r) < 1e-6 for p in corners):
                return i
    return None


def loop_box(data, loop):
    """A loop's box in sketch coordinates, from its curves' points (a circle or arc as its whole circle); None if unknown."""
    curves = {c["id"]: c for c in data["curves"]}
    xs, ys = [], []
    def point(i):
        c = curves.get(i)
        if c is not None and c["kind"] == "point":
            xs.append(c["x"])
            ys.append(c["y"])
            return c
        return None
    for i in loop:
        c = curves.get(i)
        if c is None:
            return None
        if c["kind"] in ("circle", "arc"):
            p = curves.get(c["center"])
            if p is None:
                return None
            xs.extend((p["x"] - c["r"], p["x"] + c["r"]))
            ys.extend((p["y"] - c["r"], p["y"] + c["r"]))
        else:
            for k in ("start", "end"):
                point(c.get(k))
            for q in c.get("poles") or []:
                xs.append(q[0])
                ys.append(q[1])
    return (min(xs), min(ys), max(xs), max(ys)) if xs else None


def outline_points(wire, inverse):
    """A loop's outline from Inventor's trimmed wire, in order, as sketch-plane points (arcs sampled); None if unknown."""
    pts = []
    for e in wire:
        if e.get("kind") == "line" and e.get("a") and e.get("b"):
            pts.append(inverse.multVec(V(*e["a"])))
        elif e.get("kind") == "circle" and e.get("a") and e.get("from") and e.get("to"):
            c, a, b = inverse.multVec(V(*e["a"])), inverse.multVec(V(*e["from"])), inverse.multVec(V(*e["to"]))
            a0, a1 = math.atan2(a.y - c.y, a.x - c.x), math.atan2(b.y - c.y, b.x - c.x)
            n = inverse.Rotation.multVec(V(*e["b"]))
            if (n.z > 0) == bool(e.get("rev")):
                a0, a1 = a1, a0
            if a1 <= a0:
                a1 += 2 * math.pi
            for k in range(24):
                t = a0 + (a1 - a0) * k / 24.0
                pts.append(V(c.x + e["r"] * math.cos(t), c.y + e["r"] * math.sin(t), 0))
        else:
            return None
    return pts if len(pts) >= 3 else None


def in_polygon(p, poly):
    inside = False
    for k in range(len(poly)):
        a, b = poly[k], poly[(k + 1) % len(poly)]
        if (a.y > p.y) != (b.y > p.y) and p.x < a.x + (p.y - a.y) * (b.x - a.x) / (b.y - a.y):
            inside = not inside
    return inside


def filled_inside(f, data):
    """A profile whose loops each add material (made without combining regions): a loop inside another adds nothing
    more, and FreeCAD would take it for a hole, so it goes. Profiles with holes are left as they are. "Inside" by the
    loops' real outlines: two separate regions of a real part's cut can have one's box within the other's."""
    adds, loops, wires = f.get("adds"), f.get("loops"), f.get("wires")
    if not adds or not loops or len(adds) != len(loops) or sum(adds) < 2 or not wires or len(wires) != len(loops) or not data.get("matrix"):
        return f
    inverse = App.Placement(matrix_of(data["matrix"])).inverse()
    outlines = [outline_points(w, inverse) for w in wires]
    def inside(i, j):
        if outlines[i] is None or outlines[j] is None:
            return False
        return all(in_polygon(p, outlines[j]) for p in outlines[i][::max(1, len(outlines[i]) // 8)])
    drop = [i for i in range(len(loops)) if adds[i] and any(adds[j] and inside(i, j) for j in range(len(loops)) if j != i)]
    if not drop:
        return f
    keep = [i for i in range(len(loops)) if i not in drop]
    out = dict(f, loops=[loops[i] for i in keep], adds=[adds[i] for i in keep])
    if f.get("wires") and len(f["wires"]) == len(loops):
        out["wires"] = [f["wires"][i] for i in keep]
    return out


def all_closed(sk):
    wires = sk.Shape.Wires if not sk.Shape.isNull() else []
    return len(wires) > 0 and all(w.isClosed() for w in wires)


class SketchBuilder:
    # points: for a hole, the sketch points its centres are on. FreeCAD's Hole drills at a sketch's solid points.
    def __init__(self, doc, params, data, body, keep, label, points=None):
        self.points = points or []
        self.doc, self.params, self.data = doc, params, data
        self.curves = {c["id"]: c for c in data["curves"]}
        self.keep = keep
        self.bad = set()    # which add() calls, counted in order, moved the sketch
        self.status = None
        self.last_try = False
        self.started = time.time()
        for attempt in range(40):
            self.last_try = attempt == 39 or time.time() - self.started > SKETCH_RETRY_SECONDS
            try:
                self.build(body, label)
                break
            except Restart:
                report["sketches"].remove(self.status)
                doc.removeObject(self.sk.Name)
        self.status["moved_by_constraints"] = len(self.bad)
        doc.recompute()
        self.verify()

    def build(self, body, label):
        data = self.data
        self.count = 0
        self.status = {"name": label, "dropped": [], "skipped_curves": [], "dimensions": 0, "constraints": 0, "kept_value": 0}
        report["sketches"].append(self.status)
        self.geo = {}       # curve id -> geometry index
        self.where = {}     # point id -> [(geometry, position)]
        self.spokes = {}    # polygon point id -> construction line from the centre
        self.sk = body.newObject("Sketcher::SketchObject", "Sketch")
        self.sk.Label = label
        if data["matrix"]:
            self.status["on"] = attach(self.sk, body, data["matrix"], data.get("on"))
        for r in data["rules"]:
            r.pop("done", None)
        self.geometry()
        self.structure()
        for r in data["rules"]:
            if not r["kind"].startswith("Dim"):
                self.rule(r)
        self.join_ends()
        for r in data["rules"]:
            if r["kind"].startswith("Dim"):
                self.dimension(r)
        self.clear_redundant()

    def clear_redundant(self):
        """A rule can pass on its own and still leave the whole sketch with one too many, which fails it at the next recompute."""
        for _ in range(8):
            self.sk.solve()
            extra = sorted(set(getattr(self.sk, "RedundantConstraints", []) or []), reverse=True)
            if not extra:
                return
            for i in extra:
                self.sk.delConstraint(i - 1)
            self.status["redundant_removed"] = self.status.get("redundant_removed", 0) + len(extra)

    def join_ends(self):
        """Ends in the same place, tied so they stay together when a dimension changes. Inventor's own rules do most of
        this; one that had to be dropped would otherwise leave a loop that opens at the first change."""
        ends = []
        for pid, at in self.where.items():
            for g, pos in at:
                if pos != 3:
                    ends.append((self.pt(pid), g, pos, pid))
        for i, a in enumerate(ends):
            for b in ends[i + 1:]:
                if a[3] != b[3] and a[0].distanceToPoint(b[0]) < 1e-6 and a[1] != b[1]:
                    if self.add("Coincident", a[1], a[2], b[1], b[2]) is not None:
                        self.status["ends_joined"] = self.status.get("ends_joined", 0) + 1

    def pt(self, cid):
        c = self.curves[cid]
        return V(c["x"], c["y"], 0)

    def geometry(self):
        for c in self.data["curves"]:
            k = c["kind"]
            # What the profile uses is solid, projected or not; without a profile, projected curves are only references.
            con = c["id"] not in self.keep if self.keep is not None else c["ref"]
            g = None
            if k == "line" and c["start"] in self.curves and c["end"] in self.curves:
                a, b = self.pt(c["start"]), self.pt(c["end"])
                if a.distanceToPoint(b) < 1e-7:
                    continue
                g = Part.LineSegment(a, b)
            elif k == "circle" and c["center"] in self.curves:
                g = Part.Circle(self.pt(c["center"]), V(0, 0, 1), c["r"])
            elif k == "arc" and c["center"] in self.curves and c["start"] in self.curves and c["end"] in self.curves:
                o, a, b = self.pt(c["center"]), self.pt(c["start"]), self.pt(c["end"])
                a0, a1 = math.atan2(a.y - o.y, a.x - o.x), math.atan2(b.y - o.y, b.x - o.x)
                if a1 <= a0:
                    a1 += 2 * math.pi
                g = Part.ArcOfCircle(Part.Circle(o, V(0, 0, 1), c["r"]), a0, a1)
            elif k == "spline" and c.get("poles"):
                g = spline_of(c)
            elif k == "ellipse" and c["center"] in self.curves and c.get("major"):
                o = self.pt(c["center"])
                major = V(c["major"][0], c["major"][1], 0)
                e = Part.Ellipse(o + major * c["r"], o + V(-major.y, major.x, 0) * c["minor"], o)
                if c["start"] in self.curves and c["end"] in self.curves:
                    g = Part.ArcOfEllipse(e, e.parameter(self.pt(c["start"])), e.parameter(self.pt(c["end"])))
                else:
                    g = e
            elif k != "point":
                self.status["skipped_curves"].append(k)
                continue
            if g is None:
                continue
            i = self.sk.addGeometry(g, con)
            self.geo[c["id"]] = i
            if c["ref"]:
                self.sk.addConstraint(Sketcher.Constraint("Block", i))
            if k in ("line", "arc") or (k in ("spline", "ellipse") and c["start"] in self.curves and c["end"] in self.curves and not isinstance(g, Part.Ellipse)):
                self.where.setdefault(c["start"], []).append((i, 1))
                self.where.setdefault(c["end"], []).append((i, 2))
            if k in ("circle", "arc", "ellipse"):
                self.where.setdefault(c["center"], []).append((i, 3))
        # Points that are nothing's end or centre.
        for c in self.data["curves"]:
            if c["kind"] == "point" and c["id"] not in self.where and (not c["hidden"] or c["id"] in self.points):
                i = self.sk.addGeometry(Part.Point(self.pt(c["id"])), c["id"] not in self.points)
                self.geo[c["id"]] = i
                self.where[c["id"]] = [(i, 1)]
                if c["ref"]:
                    self.sk.addConstraint(Sketcher.Constraint("Block", i))
        # A hole centred on the end or centre of a curve: a solid point there, tied to it.
        for pid in self.points:
            if pid in self.where and pid not in self.geo:
                i = self.sk.addGeometry(Part.Point(self.pt(pid)), False)
                self.where[pid].append((i, 1))

    def structure(self):
        """Curves that share a point node are joined; a tangent rule between them joins them smoothly instead."""
        joins = set()
        for r in self.data["rules"]:
            if r["kind"] == "Tangent2D" and len(r["items"]) == 2:
                a, b = r["items"]
                for pid, ends in self.where.items():
                    ga = [e for e in ends if e[0] == self.geo.get(a) and e[1] != 3]
                    gb = [e for e in ends if e[0] == self.geo.get(b) and e[1] != 3]
                    if ga and gb:
                        if self.add("Tangent", ga[0][0], ga[0][1], gb[0][0], gb[0][1]):
                            joins.add((ga[0], gb[0]))
                            r["done"] = True
                        break
        for pid, ends in self.where.items():
            linked = [{e} for e in ends]
            for x, y in joins:
                sx = next((s for s in linked if x in s), None)
                sy = next((s for s in linked if y in s), None)
                if sx is not None and sy is not None and sx is not sy:
                    sx |= sy
                    linked.remove(sy)
            for s in linked[1:]:
                a, b = sorted(linked[0])[0], sorted(s)[0]
                self.add("Coincident", a[0], a[1], b[0], b[1])

    def add(self, *args, **kw):
        """One constraint, kept only if the sketch still solves with it and nothing moves from where Inventor has it.
        A solve that moved things can't be undone, so the sketch starts over without that constraint."""
        self.count += 1
        if self.count in self.bad:
            return None
        # Every constraint solves the whole sketch, which a sketch of hundreds of curves can't afford for long.
        if time.time() - self.started > SKETCH_SECONDS:
            self.status["out_of_time"] = True
            return None
        i = None
        try:
            i = self.sk.addConstraint(Sketcher.Constraint(*args))
            if self.sk.solve() == 0:
                if self.moved() > 0 and not self.last_try:
                    self.bad.add(self.count)
                    raise Restart()
                self.status["constraints"] += 1
                return i
        except Restart:
            raise
        except Exception:
            pass
        if i is not None:
            self.sk.delConstraint(i)
            self.sk.solve()
            if self.moved() > 0 and not self.last_try:
                self.bad.add(self.count)
                raise Restart()
        return None

    def moved(self):
        n = 0
        for cid, i in self.geo.items():
            c = self.curves[cid]
            g = self.sk.Geometry[i]
            k = c["kind"]
            if k == "point":
                good = V(g.X, g.Y, 0).distanceToPoint(self.pt(cid)) < 1e-3
            elif k == "line":
                good = g.StartPoint.distanceToPoint(self.pt(c["start"])) < 1e-3 and g.EndPoint.distanceToPoint(self.pt(c["end"])) < 1e-3
            elif k in ("circle", "arc"):
                good = g.Center.distanceToPoint(self.pt(c["center"])) < 1e-3 and abs(g.Radius - c["r"]) < 1e-3
            elif k == "ellipse":
                good = g.Center.distanceToPoint(self.pt(c["center"])) < 1e-3
            else:
                good = True
            if not good:
                n += 1
        return n

    def ref(self, cid):
        """A point's (geometry, position), or None if it's a curve or not in the sketch."""
        ends = self.where.get(cid)
        return ends[0] if ends else None

    def dropped(self, r):
        self.status["dropped"].append(r["kind"] + (" " + r["param"] if r.get("param") else ""))

    def rule(self, r):
        kind, items = r["kind"], r["items"]
        if r.get("done") or kind in NOT_CONSTRAINTS:
            return
        geo, ref, cur = self.geo, self.ref, self.curves
        ok = True
        try:
            if kind == "Coincident2D":
                ok = self.coincident(items)
            elif kind in ("Parallel2D", "Perpendicular2D", "Tangent2D", "Collinear2D", "EqualLength2D", "EqualRadius2D"):
                name = {"Parallel2D": "Parallel", "Perpendicular2D": "Perpendicular", "Tangent2D": "Tangent", "Collinear2D": "Tangent", "EqualLength2D": "Equal", "EqualRadius2D": "Equal"}[kind]
                ok = self.add(name, geo[items[0]], geo[items[1]]) is not None
            elif kind == "PolygonCenter2D":
                pass
            elif kind == "PolygonEdge2D":
                ok = self.polygon_edge(items)
            elif kind in ("Horizontal2D", "Vertical2D", "AlignHorizontal2D", "AlignVertical2D"):
                name = "Horizontal" if "Horizontal" in kind else "Vertical"
                if len(items) == 1:
                    ok = self.add(name, geo[items[0]]) is not None
                else:
                    a, b = ref(items[0]), ref(items[1])
                    ok = self.add(name, a[0], a[1], b[0], b[1]) is not None
            elif kind == "Fix2D":
                ok = self.fix(items[0])
            elif kind == "Concentric2D":
                ok = self.concentric(items)
            elif kind == "SymmetryPoint2D":
                line, point = (items[0], items[1]) if cur[items[0]]["kind"] == "line" else (items[1], items[0])
                p = ref(point)
                ok = self.add("Symmetric", geo[line], 1, geo[line], 2, p[0], p[1]) is not None
            elif kind == "SymmetryLine2D":
                ok = self.symmetric(items)
            else:
                ok = False
        except Restart:
            raise
        except (KeyError, IndexError, TypeError, Part.OCCError):
            ok = False
        if not ok:
            self.dropped(r)

    def coincident(self, items):
        a, b = items[0], items[1]
        ca, cb = self.curves[a], self.curves[b]
        if ca["kind"] == "point" and cb["kind"] == "point":
            pa, pb = self.ref(a), self.ref(b)
            return self.add("Coincident", pa[0], pa[1], pb[0], pb[1]) is not None
        curve, point = (a, b) if ca["kind"] != "point" else (b, a)
        c = self.curves[curve]
        if point in (c["start"], c["end"], c["center"]):
            return True     # it's the curve's own end, joined already
        p = self.ref(point)
        return self.add("PointOnObject", p[0], p[1], self.geo[curve]) is not None

    def fix(self, item):
        c = self.curves[item]
        if c["kind"] != "point":
            return self.add("Block", self.geo[item]) is not None
        p = self.ref(item)
        return self.add("DistanceX", -1, 1, p[0], p[1], c["x"]) is not None and self.add("DistanceY", -1, 1, p[0], p[1], c["y"]) is not None

    def concentric(self, items):
        a, b = self.curves[items[0]], self.curves[items[1]]
        ca = items[0] if a["kind"] == "point" else a["center"]
        cb = items[1] if b["kind"] == "point" else b["center"]
        if ca == cb:
            return True
        pa, pb = self.ref(ca), self.ref(cb)
        return self.add("Coincident", pa[0], pa[1], pb[0], pb[1]) is not None

    def symmetric_points(self, a, b, ax):
        pa, pb = self.ref(a), self.ref(b)
        if self.add("Symmetric", pa[0], pa[1], pb[0], pb[1], ax) is not None:
            return True
        # The solver refuses the whole rule if part of it is settled already, say both points on a line square to the axis.
        # A point halfway between them, kept on the axis, carries the part that isn't.
        middle = self.sk.addGeometry(Part.Point((self.pt(a) + self.pt(b)) * 0.5), True)
        return self.add("Symmetric", pa[0], pa[1], pb[0], pb[1], middle, 1) is not None and self.add("PointOnObject", middle, 1, ax) is not None

    def spoke(self, centre, point):
        """A construction line from a polygon's centre to one of its points."""
        if point not in self.spokes:
            c, p = self.ref(centre), self.ref(point)
            i = self.sk.addGeometry(Part.LineSegment(self.pt(centre), self.pt(point)), True)
            self.add("Coincident", i, 1, c[0], c[1])
            self.add("Coincident", i, 2, p[0], p[1])
            self.spokes[point] = i
        return self.spokes[point]

    def polygon_edge(self, items):
        """Neighbours on a regular polygon or a ring of holes. Points: the same distance from the centre, the same turn
        apart, by a line from the centre to each. Curves: the same size. The last of a ring is settled by the rest."""
        a, b = items[0], items[1]
        # Every constraint solves the whole sketch: a big ring of patterns would take minutes.
        if sum(1 for r in self.data["rules"] if r["kind"] == "PolygonEdge2D") > POLYGON_RULES:
            return False
        if self.curves[a]["kind"] != "point":
            self.add("Equal", self.geo[a], self.geo[b])
            return True
        c = self.pt(items[2])
        if self.pt(a).distanceToPoint(c) < 1e-6 or self.pt(b).distanceToPoint(c) < 1e-6:
            return False    # a point on the centre has no line to it
        sa, sb = self.spoke(items[2], a), self.spoke(items[2], b)
        self.add("Equal", sa, sb)
        da, db = self.pt(a) - c, self.pt(b) - c
        turn = math.atan2(db.y, db.x) - math.atan2(da.y, da.x)
        while turn <= -math.pi:
            turn += 2 * math.pi
        while turn > math.pi:
            turn -= 2 * math.pi
        if turn > 0:
            self.add("Angle", sa, 1, sb, 1, turn)
        else:
            self.add("Angle", sb, 1, sa, 1, -turn)
        return True

    def symmetric(self, items):
        a, b, axis = items[0], items[1], items[2]
        ca, cb = self.curves[a], self.curves[b]
        ax = self.geo[axis]
        if ca["kind"] == "point":
            return self.symmetric_points(a, b, ax)
        if ca["kind"] in ("circle", "arc"):
            kept = self.symmetric_points(ca["center"], cb["center"], ax)
            # Redundant if both are already sized, and that's fine.
            self.add("Equal", self.geo[a], self.geo[b])
            return kept
        # Two lines: the mirror of one lies along the other, and they needn't be the same length. So each end's mirror image is
        # a construction point, kept on the other line. On parallel lines the second is redundant, and the solver says so.
        l = self.curves[axis]
        p0, p1 = self.pt(l["start"]), self.pt(l["end"])
        d = (p1 - p0)
        d = d * (1.0 / d.Length)
        def mirror(p):
            v = p - p0
            return p0 + d * (2 * v.dot(d)) - v
        kept = 0
        for end, position in ((1, self.pt(ca["start"])), (2, self.pt(ca["end"]))):
            image = self.sk.addGeometry(Part.Point(mirror(position)), True)
            if self.add("Symmetric", self.geo[a], end, image, 1, ax) is None or self.add("PointOnObject", image, 1, self.geo[b]) is None:
                continue
            kept += 1
        return kept > 0

    def point_of(self, cid):
        """A dimension's end: a point, or a circle's centre."""
        c = self.curves[cid]
        if c["kind"] in ("circle", "arc"):
            return self.ref(c["center"]), self.pt(c["center"])
        if c["kind"] == "point":
            return self.ref(cid), self.pt(cid)
        return None, None

    def dimension(self, r):
        kind, items = r["kind"], r["items"]
        i = None
        try:
            i = self.dimension_constraint(kind, items)
        except Restart:
            raise
        except (KeyError, IndexError, TypeError, Part.OCCError):
            i = None
        if i is None:
            self.dropped(r)
            return
        self.status["dimensions"] += 1
        name = r.get("param")
        expr = self.params.ref(name) if name else None
        if expr:
            try:
                self.sk.renameConstraint(i, name)
                # Only tied to its parameter if they agree: a sketch out of step with its parameter would move when the
                # file is opened, and everything built on it with it.
                have = self.sk.Constraints[i].Value
                angle = self.sk.Constraints[i].Type == "Angle"
                if angle:
                    have = math.degrees(have)
                want = self.params.value(name)
                formula = expr if want is None else related(expr, want, have, angle)
                if formula is None:
                    self.status["kept_value"] += 1
                    self.status.setdefault("out_of_step", []).append("%s %s: sketch %.5f, parameter %.5f" % (r["kind"], name, have, want))
                else:
                    self.sk.setExpression("Constraints." + name, formula)
            except Exception:
                self.status["kept_value"] += 1

    def dimension_constraint(self, kind, items):
        cur, geo = self.curves, self.geo
        if kind in ("DimRadius2D", "DimRadiusA2D"):
            return self.add("Radius", geo[items[0]], cur[items[0]]["r"])
        if kind == "DimDiameter2D":
            return self.add("Diameter", geo[items[0]], 2 * cur[items[0]]["r"])
        if kind == "DimAngle2Line2D":
            return self.angle(items)
        if kind == "DimAngle3Point2D" and len(items) == 2 and sum(1 for r in self.data["rules"] if r["kind"] == "PolygonEdge2D") <= POLYGON_RULES:     # how far a polygon is turned
            s = self.spoke(items[0], items[1])
            d = self.pt(items[1]) - self.pt(items[0])
            return self.add("Angle", s, math.atan2(d.y, d.x))
        if kind in ("DimDistance2D", "DimDistanceH2D", "DimDistanceV2D"):
            if len(items) == 1:     # a line's own length
                c = cur[items[0]]
                items = [c["start"], c["end"]]
            # Between two circles on one centre: a wall's thickness, the gap between them.
            if kind == "DimDistance2D" and all(cur[x]["kind"] in ("circle", "arc") for x in items) and self.pt(cur[items[0]]["center"]).distanceToPoint(self.pt(cur[items[1]]["center"])) < 1e-6:
                return self.add("Distance", geo[items[0]], geo[items[1]], abs(cur[items[0]]["r"] - cur[items[1]]["r"]))
            pa, xa = self.point_of(items[0])
            pb, xb = self.point_of(items[1])
            if pa is None or pb is None:
                return self.line_distance(kind, items)
            if kind == "DimDistance2D":
                return self.add("Distance", pa[0], pa[1], pb[0], pb[1], xa.distanceToPoint(xb))
            axis = "DistanceX" if kind == "DimDistanceH2D" else "DistanceY"
            d = (xb.x - xa.x) if kind == "DimDistanceH2D" else (xb.y - xa.y)
            if d < 0:
                pa, pb, d = pb, pa, -d
            return self.add(axis, pa[0], pa[1], pb[0], pb[1], d)
        return None

    def line_distance(self, kind, items):
        """Between a point and a line, or two parallel lines."""
        if kind != "DimDistance2D":
            a, b = items[0], items[1]
            ca, cb = self.curves[a], self.curves[b]
            pa, xa = (self.ref(ca["start"]), self.pt(ca["start"])) if ca["kind"] == "line" else self.point_of(a)
            pb, xb = (self.ref(cb["start"]), self.pt(cb["start"])) if cb["kind"] == "line" else self.point_of(b)
            axis = "DistanceX" if kind == "DimDistanceH2D" else "DistanceY"
            d = (xb.x - xa.x) if kind == "DimDistanceH2D" else (xb.y - xa.y)
            if d < 0:
                pa, pb, d = pb, pa, -d
            return self.add(axis, pa[0], pa[1], pb[0], pb[1], d)
        a, b = items[0], items[1]
        if self.curves[a]["kind"] == "line" and self.curves[b]["kind"] != "line":
            a, b = b, a
        line = self.curves[b]
        s, e = self.pt(line["start"]), self.pt(line["end"])
        if self.curves[a]["kind"] == "line":
            p, x = self.ref(self.curves[a]["start"]), self.pt(self.curves[a]["start"])
        else:
            p, x = self.point_of(a)
        d = (e - s)
        n = V(-d.y, d.x, 0) * (1.0 / d.Length)
        i = self.add("Distance", p[0], p[1], self.geo[b], abs((x - s).dot(n)))
        # Inventor's distance between two lines keeps them parallel too.
        if i is not None and self.curves[a]["kind"] == "line":
            other = self.pt(self.curves[a]["end"]) - self.pt(self.curves[a]["start"])
            if abs(other.x * d.y - other.y * d.x) < 1e-6 * other.Length * d.Length:
                self.add("Parallel", self.geo[a], self.geo[b])
        return i

    def angle(self, items):
        """Angle(a, posA, b, posB, value): pick the ends that make the positive angle between the lines' directions."""
        a, b = self.curves[items[0]], self.curves[items[1]]
        def direction(c, pos):
            s, e = self.pt(c["start"]), self.pt(c["end"])
            return (e - s) if pos == 1 else (s - e)
        best = None
        for pa in (1, 2):
            for pb in (1, 2):
                da, db = direction(a, pa), direction(b, pb)
                ang = math.atan2(db.y, db.x) - math.atan2(da.y, da.x)
                while ang <= -math.pi:
                    ang += 2 * math.pi
                while ang > math.pi:
                    ang -= 2 * math.pi
                if ang > 0 and (best is None or ang < best[0]):
                    best = (ang, pa, pb)
        if best is None:
            return None
        return self.add("Angle", self.geo[items[0]], best[1], self.geo[items[1]], best[2], best[0])

    def verify(self):
        """Curves where Inventor has them, after the solver and the expressions have had their say."""
        self.status["moved"] = self.moved()
        self.status["solver"] = self.sk.solve()


# ---------------------------------------------------------------- features

TRANSFORM_KINDS = {"mirror": "PartDesign::Mirrored", "rectangular": "PartDesign::LinearPattern", "circular": "PartDesign::PolarPattern"}


def outer_pieces(made):
    """Regions that touch share a stretch of boundary, listed once for each, and the outline around them doesn't have
    it. Not always as the same piece: in one real part two small rectangles sit against part of an arc the main region
    has whole, and three faces revolved touching make a solid that cuts itself. So on each circle and each straight line,
    only the stretches covered an odd number of times are kept."""
    tol = 1e-6
    circles, lines, rest = {}, {}, []
    for g in made:
        if isinstance(g, Part.Circle):
            circles.setdefault(("c", round(g.Center.x, 5), round(g.Center.y, 5), round(g.Radius, 5)), []).append(g)
        elif isinstance(g, Part.ArcOfCircle):
            c = g.Circle
            circles.setdefault(("c", round(c.Center.x, 5), round(c.Center.y, 5), round(c.Radius, 5)), []).append(g)
        elif isinstance(g, Part.LineSegment):
            d = g.EndPoint - g.StartPoint
            d = d * (1.0 / d.Length)
            if d.x < -tol or (abs(d.x) <= tol and d.y < 0):
                d = d * -1
            n = V(-d.y, d.x, 0)
            lines.setdefault((round(d.x, 6), round(d.y, 6), round(g.StartPoint.dot(n), 5)), []).append((g, d))
        else:
            rest.append(g)
    def odd(spans):
        cuts = sorted({x for s in spans for x in s})
        keep = []
        for a, b in zip(cuts, cuts[1:]):
            if b - a > tol and sum(1 for s in spans if s[0] <= a + tol and b <= s[1] + tol) % 2 == 1:
                if keep and abs(keep[-1][1] - a) <= tol:
                    keep[-1] = (keep[-1][0], b)
                else:
                    keep.append((a, b))
        return keep
    def arc_spans(gs):
        spans = []
        for g in gs:
            if isinstance(g, Part.Circle):
                spans.append([(0.0, 2 * math.pi)])
                continue
            a0 = g.FirstParameter % (2 * math.pi)
            a1 = a0 + (g.LastParameter - g.FirstParameter)
            spans.append([(a0, min(a1, 2 * math.pi))] + ([(0.0, a1 - 2 * math.pi)] if a1 > 2 * math.pi else []))
        return spans
    def line_spans(gs):
        d, base = gs[0][1], gs[0][0].StartPoint
        return [[tuple(sorted(((g.StartPoint - base).dot(d), (g.EndPoint - base).dot(d))))] for g, _ in gs]
    def partly(spans):
        # Two pieces sharing a stretch without being the same piece.
        for i in range(len(spans)):
            for j in range(i + 1, len(spans)):
                for a in spans[i]:
                    for b in spans[j]:
                        common = min(a[1], b[1]) - max(a[0], b[0])
                        same = abs(a[0] - b[0]) <= tol and abs(a[1] - b[1]) <= tol
                        if common > tol and not same:
                            return True
        return False
    if not any(partly(arc_spans(gs)) for gs in circles.values() if len(gs) > 1) and not any(partly(line_spans(gs)) for gs in lines.values() if len(gs) > 1):
        # Only whole pieces repeat: each pair of them goes, as it always did.
        def key(g):
            if isinstance(g, Part.Circle):
                return ("c", round(g.Center.x, 4), round(g.Center.y, 4), round(g.Radius, 4))
            ends = sorted([(round(g.StartPoint.x, 4), round(g.StartPoint.y, 4)), (round(g.EndPoint.x, 4), round(g.EndPoint.y, 4))])
            return (type(g).__name__, ends[0], ends[1])
        seen = {}
        for g in made:
            seen.setdefault(key(g), []).append(g)
        return [v[0] for v in seen.values() if len(v) % 2 == 1]
    out = list(rest)
    for key, gs in circles.items():
        if len(gs) == 1:
            out += gs
            continue
        whole = sum(1 for g in gs if isinstance(g, Part.Circle))
        c = gs[0] if isinstance(gs[0], Part.Circle) else gs[0].Circle
        spans = []
        for g in gs:
            if isinstance(g, Part.Circle):
                spans += [(0.0, 2 * math.pi)]
                continue
            a0 = g.FirstParameter % (2 * math.pi)
            a1 = a0 + (g.LastParameter - g.FirstParameter)
            spans += [(a0, min(a1, 2 * math.pi))] + ([(0.0, a1 - 2 * math.pi)] if a1 > 2 * math.pi else [])
        kept = odd(spans)
        # Join a stretch that runs over angle 0 back into one arc.
        if len(kept) > 1 and kept[0][0] <= tol and kept[-1][1] >= 2 * math.pi - tol:
            kept = [(kept[-1][0], kept[0][1] + 2 * math.pi)] + kept[1:-1]
        for a, b in kept:
            circle = Part.Circle(V(c.Center.x, c.Center.y, 0), V(0, 0, 1), c.Radius)
            out.append(circle if b - a >= 2 * math.pi - tol else Part.ArcOfCircle(circle, a, b))
    for key, gs in lines.items():
        if len(gs) == 1:
            out.append(gs[0][0])
            continue
        d = gs[0][1]
        base = gs[0][0].StartPoint
        spans = [tuple(sorted(((g.StartPoint - base).dot(d), (g.EndPoint - base).dot(d)))) for g, _ in gs]
        for a, b in odd(spans):
            out.append(Part.LineSegment(base + d * a, base + d * b))
    return out


class Builder:
    def __init__(self, doc, params, model):
        self.doc, self.params, self.model = doc, params, model
        self.sketches = {s["id"]: s for s in model["sketches"]}
        self.body = None
        self.bodies = []
        self.used = {}      # sketch id -> {frozenset of kept curves or None: the FreeCAD sketch}
        self.made = {}      # feature node -> the FreeCAD feature, for what patterns copy
        self.transforms = {}    # a pattern's FreeCAD name -> its model feature, for patterns of patterns
        self.ends = {}          # body name -> (body, its Tip at Inventor's End of Part)
        self.pieced = set()     # fillets and chamfers built in pieces or with profiles, which a pattern can't copy
        self.tolerance = {}     # feature name -> the loosest vertex tolerance of its solid when it was made

    def new_body(self):
        self.body = self.doc.addObject("PartDesign::Body", "Body")
        self.bodies.append(self.body)
        return self.body

    def sketch_for(self, f):
        """The sketch with only the profile's curves solid: FreeCAD extrudes every closed loop a sketch has."""
        data = self.sketches[f["sketch"]]
        f = filled_inside(f, data)
        keep = None
        if f.get("loops"):
            keep = set(x for loop in f["loops"] for x in loop)
        key = frozenset(keep) if keep is not None else None
        versions = self.used.setdefault(data["id"], {})
        if key in versions and versions[key].getParentGeoFeatureGroup() == self.body:
            return versions[key], data
        label = data["name"] or "Sketch"
        if versions:
            label = "%s (%s)" % (label, f["name"])
        b = SketchBuilder(self.doc, self.params, data, self.body, keep, label)
        # A full circle that the profile only uses part of looks closed, but extruding it would take the whole disc.
        # (Arcs of one circle alone in a loop are that circle stored in pieces; arcs of several bound a region between
        # them, as eight lenses between whole circles in one real part.)
        def one_circle(w):
            return len(set((round(e["r"], 5),) + tuple(round(x, 5) for x in e["a"]) for e in w)) == 1
        part_circle = any(any(e["kind"] == "circle" for e in w) and (len(set(e["kind"] for e in w)) > 1 or not one_circle(w)) for w in f.get("wires") or [])
        part_circle = part_circle and any(isinstance(g, Part.Circle) and not b.sk.getConstruction(i) for i, g in enumerate(b.sk.Geometry))
        if keep is not None and f.get("wires") and (part_circle or not all_closed(b.sk)):
            self.trimmed_outline(b, f["wires"], data)
        versions[key] = b.sk
        return b.sk, data

    def trimmed_outline(self, b, wires, data):
        """Inventor lets curves that cross bound a profile, trimming them where they meet; FreeCAD wants ends that meet.
        The file keeps the trimmed outline, so it goes in as the sketch's solid curves, with the originals (and their
        constraints) left as construction."""
        sk = b.sk
        inverse = App.Placement(matrix_of(data["matrix"])).inverse()
        def local(p):
            q = inverse.multVec(V(*p))
            # Some blocks keep the outline in sketch coordinates already.
            return q if abs(q.z) < 1e-6 else V(p[0], p[1], 0)
        made = []
        for loop in wires:
            for e in loop:
                g = None
                if e["kind"] == "line" and e.get("a") and e.get("b"):
                    x, y = local(e["a"]), local(e["b"])
                    if x.distanceToPoint(y) > 1e-7:
                        g = Part.LineSegment(x, y)
                elif e["kind"] == "circle":
                    c = local(e["a"])
                    if e["from"] == e["to"]:
                        g = Part.Circle(V(c.x, c.y, 0), V(0, 0, 1), e["r"])
                    else:
                        x, y = local(e["from"]), local(e["to"])
                        # Which way round: the curve's normal against the sketch's, and whether the edge runs against the curve.
                        n = inverse.Rotation.multVec(V(*e["b"]))
                        if (n.z > 0) == bool(e.get("rev")):
                            x, y = y, x
                        a0, a1 = math.atan2(x.y - c.y, x.x - c.x), math.atan2(y.y - c.y, y.x - c.x)
                        if a1 <= a0:
                            a1 += 2 * math.pi
                        g = Part.ArcOfCircle(Part.Circle(V(c.x, c.y, 0), V(0, 0, 1), e["r"]), a0, a1)
                elif e.get("a") and e.get("b"):
                    x, y = local(e["a"]), local(e["b"])
                    if x.distanceToPoint(y) > 1e-7:
                        # The outline gives only a spline's ends; the sketch's own spline between them has the shape.
                        for h in sk.Geometry:
                            if isinstance(h, Part.BSplineCurve) and any(h.StartPoint.distanceToPoint(p) < 1e-4 and h.EndPoint.distanceToPoint(q) < 1e-4 for p, q in ((x, y), (y, x))):
                                g = h
                        if g is None:
                            g = Part.LineSegment(x, y)
                            b.status.setdefault("outline_chords", 0)
                            b.status["outline_chords"] += 1
                if g is not None:
                    made.append(g)
        made = outer_pieces(made)
        for i in range(sk.GeometryCount):
            if not sk.getConstruction(i) and not isinstance(sk.Geometry[i], Part.Point):
                sk.toggleConstruction(i)
        # An outline curve that is exactly one of the sketch's own curves is that curve, with Inventor's constraints on it,
        # which hold a loop together far better than ties added afterwards. Only what's really trimmed is new.
        fresh = []
        reused = set()
        for g in made:
            k = same_curve(sk, g, reused)
            if k is None:
                fresh.append(g)
            else:
                reused.add(k)
                sk.toggleConstruction(k)
        b.status["trimmed_outline"] = True
        b.status["outline_reused"] = len(reused)
        first = sk.GeometryCount
        if fresh:
            sk.addGeometry(fresh, False)
        tied = self.tie_outline(sk, first, len(fresh))
        b.status["outline_tied"] = "%d of %d" % (tied, len(fresh))
        self.doc.recompute()

    def tie_outline(self, sk, first, count):
        """The trimmed outline's curves, held to the constrained curves they were cut from and to each other at the
        corners, so the dimensions reach the solid. Each tie is kept only if nothing moves."""
        def where(g):
            if isinstance(g, Part.LineSegment):
                return [g.StartPoint, g.EndPoint]
            if isinstance(g, Part.ArcOfCircle):
                return [g.StartPoint, g.EndPoint, g.Center]
            return [g.Center]
        def still(before):
            return all(p.distanceToPoint(q) < 1e-3 for k, pts in before.items() for p, q in zip(pts, where(sk.Geometry[k])))
        solid = [k for k in range(sk.GeometryCount) if not sk.getConstruction(k) and isinstance(sk.Geometry[k], (Part.LineSegment, Part.ArcOfCircle))]
        snapshot = {k: where(sk.Geometry[k]) for k in solid}
        def keep(*args):
            i = None
            try:
                i = sk.addConstraint(Sketcher.Constraint(*args))
                if sk.solve() == 0 and still(snapshot):
                    return True
            except Exception:
                pass
            if i is not None:
                sk.delConstraint(i)
                sk.solve()
            return False
        started = time.time()
        # The corners first, new curves to their neighbours: an open loop is the one failure nothing recovers from.
        ends = []
        for k in solid:
            g = sk.Geometry[k]
            ends += [(g.StartPoint, k, 1), (g.EndPoint, k, 2)]
        for i, a in enumerate(ends):
            if time.time() - started > SKETCH_RETRY_SECONDS:
                break
            for b in ends[i + 1:]:
                if a[1] != b[1] and a[0].distanceToPoint(b[0]) < 1e-6:
                    keep("Coincident", a[1], a[2], b[1], b[2])
        # Then the curve each was cut from, as far as the solver has room: with both ends held, an arc can take its source's
        # radius but not its centre as well.
        tied = 0
        originals = [(k, sk.Geometry[k]) for k in range(first) if not isinstance(sk.Geometry[k], Part.Point)]
        for k in range(first, first + count):
            if time.time() - started > 2 * SKETCH_RETRY_SECONDS:
                break
            g = sk.Geometry[k]
            done = False
            for o, h in originals:
                if isinstance(g, Part.LineSegment) and isinstance(h, Part.LineSegment):
                    d = h.EndPoint - h.StartPoint
                    if d.Length < 1e-9:
                        continue
                    d = d * (1.0 / d.Length)
                    if all(((p - h.StartPoint) - d * (p - h.StartPoint).dot(d)).Length < 1e-5 for p in (g.StartPoint, g.EndPoint)):
                        done = keep("Tangent", k, o)
                elif isinstance(g, (Part.ArcOfCircle, Part.Circle)) and isinstance(h, (Part.ArcOfCircle, Part.Circle)):
                    if g.Center.distanceToPoint(h.Center) < 1e-5 and abs(g.Radius - h.Radius) < 1e-5:
                        centred = keep("Coincident", k, 3, o, 3)
                        done = keep("Equal", k, o) or centred
                if done:
                    break
            tied += done
        return tied

    def expression(self, obj, prop, name):
        expr = self.params.ref(name) if name else None
        if expr:
            obj.setExpression(prop, expr)
        return expr

    def extrude(self, f, st):
        op = f["op"]
        if op in ("new",) or self.body is None:
            if op == "cut":
                st["status"], st["why"] = "skipped", "a cut with nothing to cut"
                return None
            self.new_body()
        if op == "surface":
            st["status"], st["why"] = "skipped", "surface extrudes aren't carried yet"
            return None
        if op == "intersect":
            return self.intersect(f, st)
        if op == "join" and f["extent"] in ("next", "distance"):
            socket = whole_circle_profile(f)
            if touching_loops(socket) is not None and "wide" not in str(f["sketch"]):
                return self.fill_then_cut(socket, touching_loops(socket), st)
        sk, data = self.sketch_for(f)
        cut = op == "cut"
        o = self.body.newObject("PartDesign::Pocket" if cut else "PartDesign::Pad", "Feature")
        o.Label = f["name"] or o.Name
        o.Profile = sk
        # The extrude runs along Inventor's axis (or against it when reversed); a pad runs along the sketch's normal, a
        # pocket against it.
        m = App.Placement(matrix_of(data["matrix"]))
        normal = m.Rotation.multVec(V(0, 0, 1))
        axis = V(*f["axis"]) if f.get("axis") else normal
        if axis.Length < 1e-9:
            axis = normal
        along = axis if f["dir"] != "negative" else axis * -1
        o.Reversed = (along.dot(normal) > 0) == cut

        ext = f["extent"]
        # A 2026 file's "to" face, resolved from the B-rep's naming, is used as the older files' copy of it is.
        if f.get("toFace") and not f.get("toPlane") and not f.get("toCylinder"):
            ref = next((a for alternatives in f["toFace"] for a in alternatives if a.get("kind") in ("plane", "cylinder")), None)
            if ref and ref["kind"] == "plane":
                f = dict(f, toPlane={"origin": ref["a"], "normal": ref["b"]})
            elif ref:
                f = dict(f, toCylinder={"origin": ref["a"], "axis": ref["b"], "r": ref["r"]})
        if f["dir"] == "symmetric":
            o.SideType = "Symmetric"
        elif f.get("extent2"):
            o.SideType = "Two sides"
        if ext == "distance":
            o.Type = "Length"
            o.Length = abs(f["length"]) if f["length"] is not None else 1
            if f["length"] is not None and f["length"] < 0:
                st["note"] = "negative length"
            self.expression(o, "Length", f.get("param"))
        elif ext == "through":
            o.Type = "ThroughAll" if cut else "UpToLast"
            base = getattr(o, "BaseFeature", None)
            base = base.Shape if base is not None and not base.Shape.isNull() else None
            if cut and base is not None:
                # FreeCAD sizes through all from the shape's box, which a spline's control points can blow up (an
                # imported body 385 mm across had a box of 200 m), and its cut with so long a tool comes out wrong (a
                # face turned inside out). Then a length right through it, from the tight box, both ways if symmetric.
                tight = base.optimalBoundingBox().DiagonalLength
                if base.BoundBox.DiagonalLength > 10 * tight:
                    o.Type = "Length"
                    o.Length = tight * (4 if f["dir"] == "symmetric" else 2)
                    st["note"] = "through all as %.0f mm: FreeCAD's through all is sized from a loose box here" % o.Length
        elif ext == "next":
            o.Type = "UpToFirst"
            self.body.Tip = o
            self.doc.recompute()
            if not o.isValid():
                d = self.distance_to_next(sk, along)
                if d is None:
                    st["status"], st["why"] = "skipped", "couldn't find the next face to extrude up to"
                    self.remove(o)
                    return None
                o.Type = "Length"
                o.Length = d
                st["status"], st["why"] = "approximate", "'to next' became a fixed distance of %.4f mm" % d
        elif ext == "to" and (f.get("toPlane") or f.get("toCylinder")):
            face = self.face_named(f, o.BaseFeature)
            done = False
            if face:
                # Up to the face itself, so it follows if that face moves.
                o.Type = "UpToFace"
                o.UpToFace = face
                self.body.Tip = o
                self.doc.recompute()
                done = o.isValid()
            if not done:
                if not f.get("toPlane"):
                    d = self.depth_to_cylinder(sk, along, f["toCylinder"], o.BaseFeature, cut)
                    if d is None:
                        st["status"], st["why"] = "skipped", "couldn't extrude up to the cylindrical face (%s)" % ("FreeCAD: " + " ".join(o.getStatusString().split())[:120] if face else "face not found")
                        self.remove(o)
                        return None
                    o.Type = "Length"
                    o.Length = d
                    st["status"], st["why"] = "approximate", "the 'to' face became a fixed distance of %.4f mm; it overshoots only into material already there" % d
                    return o
                plane = f["toPlane"]
                if plane.get("work"):
                    # Up to a work plane: a datum plane where it is, so the extrude ends on it as in Inventor.
                    o.Type = "UpToFace"
                    o.UpToFace = self.plane_datum(plane["origin"], plane["normal"], "%s to plane" % (f["name"] or "Extrude"))
                    self.body.Tip = o
                    self.doc.recompute()
                    if o.isValid():
                        return o
                n = V(*plane["normal"])
                d = abs((V(*plane["origin"]) - m.Base).dot(n) / along.dot(n)) if abs(along.dot(n)) > 1e-9 else None
                if d is None:
                    st["status"], st["why"] = "skipped", "the 'to' face is parallel to the extrude"
                    self.remove(o)
                    return None
                o.Type = "Length"
                o.Length = d
                st["status"], st["why"] = "approximate", "the 'to' face became a fixed distance of %.4f mm" % d
        elif ext == "fromto" and f.get("toPlane") and f.get("fromPlane"):
            # From one plane to another, wherever the sketch is: both sides of the sketch, the near one negative
            # (FreeCAD takes a negative second length as starting past the sketch).
            def reach(plane):
                n = V(*plane["normal"])
                return (V(*plane["origin"]) - m.Base).dot(n) / along.dot(n) if abs(along.dot(n)) > 1e-9 else None
            near, far = reach(f["fromPlane"]), reach(f["toPlane"])
            if near is None or far is None:
                st["status"], st["why"] = "skipped", "a plane of the from-to is parallel to the extrude"
                self.doc.removeObject(o.Name)
                return None
            if far < near:
                near, far = far, near
            o.SideType = "Two sides"
            o.Type, o.Type2 = "Length", "Length"
            o.Length, o.Length2 = far, -near
            st["status"], st["why"] = "approximate", "from-to as two fixed lengths (%.4f and %.4f mm), not tied to the planes" % (far, -near)
        else:
            st["status"], st["why"] = "skipped", "extent '%s' to %s isn't carried yet" % (ext, f.get("to"))
            self.doc.removeObject(o.Name)
            return None
        if f.get("extent2"):
            if f["extent2"] == "distance":
                o.Type2 = "Length"
                o.Length2 = abs(f["length2"]) if f["length2"] is not None else 1
                self.expression(o, "Length2", f.get("param2"))
            elif f["extent2"] == "through":
                o.Type2 = "ThroughAll" if cut else "UpToLast"
            elif f["extent2"] == "next":
                o.Type2 = "UpToFirst"
        if f.get("taper"):
            value = self.params.value(f.get("taperParam"))
            if value is not None:
                o.TaperAngle = value
                ref = self.expression(o, "TaperAngle", f.get("taperParam"))
                if f.get("extent2") == "distance":
                    # Inventor's taper runs on through the second side as one sloping wall; FreeCAD
                    # tapers each side from the sketch outward, so the second side takes the opposite angle.
                    o.TaperAngle2 = -value
                    if ref:
                        o.setExpression("TaperAngle2", "-(%s)" % ref)
        return o

    def fill_then_cut(self, f, outer, st):
        """A profile between a circle and a polygon touching it (a screw's socket) is slivers meeting at points, which FreeCAD
        can't extrude. With the circle a hair larger it's one region; the extra ring and the depth past the next face both lie
        in material already there, so the solid is the same."""
        data = copy.deepcopy(self.sketches[f["sketch"]])
        circle = f["loops"][outer][0]
        data["id"] = "%s wide" % data["id"]
        data["rules"] = [r for r in data["rules"] if circle not in r["items"]]
        for c in data["curves"]:
            if c["id"] == circle:
                c["r"] += 0.01
        self.sketches[data["id"]] = data
        normal = App.Placement(matrix_of(data["matrix"])).Rotation.multVec(V(0, 0, 1))
        axis = V(*f["axis"]) if f.get("axis") else normal
        if f["extent"] == "distance":
            st["note"] = "the sketch's circle is 0.01 mm larger and not tied to the polygon"
            return self.extrude(dict(f, sketch=data["id"]), st)
        depth = self.distance_to_next(None, axis if f["dir"] != "negative" else axis * -1, V(*f["wires"][outer][0]["a"]))
        if depth is None:
            st["status"], st["why"] = "skipped", "couldn't find the next face to extrude up to"
            return None
        st["note"] = "the sketch's circle is 0.01 mm larger and not tied to the polygon, and the depth is fixed at %.4f mm" % depth
        return self.extrude(dict(f, sketch=data["id"], extent="distance", length=depth, param=None), st)

    def distance_to_next(self, sk, along, start=None):
        """How far a profile travels along a direction before it meets the solid, from a point inside the profile."""
        tip = None
        for o in reversed(self.body.Group):
            if o.isDerivedFrom("PartDesign::Feature") and o.isValid() and o.Shape.Solids:
                tip = o
                break
        if tip is None:
            return None
        if start is None:
            sk.recompute()
            face = Part.makeFace(sk.Shape.Wires, "Part::FaceMakerBullseye")
            nodes, tris = face.tessellate(0.1)
            t = max(tris, key=lambda x: (nodes[x[1]] - nodes[x[0]]).cross(nodes[x[2]] - nodes[x[0]]).Length)
            start = (nodes[t[0]] + nodes[t[1]] + nodes[t[2]]) * (1.0 / 3)
        reach = tip.Shape.BoundBox.DiagonalLength * 2 + start.distanceToPoint(tip.Shape.BoundBox.Center)
        line = Part.makeLine(start, start + along.normalize() * reach)
        hits = [v.Point.distanceToPoint(start) for v in line.section(tip.Shape).Vertexes]
        hits = [d for d in hits if d > 1e-6]
        return min(hits) if hits else None

    def depth_to_cylinder(self, sk, along, c, base, cut):
        """A depth that does what extruding up to a cylinder does, where FreeCAD won't (the face touches the sketch): far
        enough for every point of the profile to reach the cylinder, as long as what lies past it is already solid (a
        join) or already empty (a cut). None if no depth does."""
        if base is None or base.Shape.isNull():
            return None
        sk.recompute()
        face = Part.makeFace(sk.Shape.Wires, "Part::FaceMakerBullseye")
        axis = V(*c["axis"]).normalize()
        size = base.Shape.BoundBox.DiagonalLength * 4 + 100
        cylinder = Part.makeCylinder(c["r"], 2 * size, V(*c["origin"]) - axis * size, axis)
        along = V(along).normalize()
        nodes, tris = face.tessellate(0.1)
        deepest, start = 0, None
        for p in nodes:
            line = Part.makeLine(p, p + along * size)
            hits = [v.Point.distanceToPoint(p) for v in line.section(cylinder.Faces[0]).Vertexes]
            if not hits:
                return None
            if min(hits) > deepest:
                deepest, start = min(hits), p
        if deepest < 1e-6:
            return None
        prism = face.extrude(along * deepest)
        # The side being filled: halfway along the longest run to the cylinder. What lies past the cylinder is the other side.
        filling_outside = not cylinder.isInside(start + along * (deepest / 2), 1e-9, False)
        past = prism.common(cylinder) if filling_outside else prism.cut(cylinder)
        stray = past.common(base.Shape).Volume if cut else past.cut(base.Shape).Volume
        return deepest if stray < 1e-6 * max(1.0, prism.Volume) else None

    def face_named(self, f, tip):
        """The face an extrude runs up to on the solid it starts from (the new extrude is already the body's tip, with no
        shape yet), found by the surface the file keeps a copy of."""
        if tip is None or tip.Shape.isNull():
            return None
        if f.get("toPlane"):
            want = {"kind": "plane", "a": f["toPlane"]["origin"], "b": f["toPlane"]["normal"]}
        else:
            c = f["toCylinder"]
            want = {"kind": "cylinder", "a": c["origin"], "b": c["axis"], "r": c["r"]}
        for i, face in enumerate(tip.Shape.Faces):
            if same_surface(want, face):
                return (tip, ["Face%d" % (i + 1)])
        return None

    def intersect(self, f, st):
        """Keep what's inside the extrude: the extrude as a body of its own, and a Boolean with it."""
        main = self.body
        if main is None or main.Tip is None:
            st["status"], st["why"] = "skipped", "nothing to intersect with"
            return None
        box = main.Shape.BoundBox
        tool_length = 2 * box.DiagonalLength + 10
        tool = self.doc.addObject("PartDesign::Body", "Body")
        tool.Label = (f["name"] or "Intersect") + " tool"
        tool.Visibility = False
        self.body = tool
        g = dict(f)
        g["op"] = "new"
        try:
            sk, data = self.sketch_for(g)
            m = App.Placement(matrix_of(data["matrix"]))
            normal = m.Rotation.multVec(V(0, 0, 1))
            axis = V(*f["axis"]) if f.get("axis") else normal
            if axis.Length < 1e-9:
                axis = normal
            along = axis if f["dir"] != "negative" else axis * -1
            pad = tool.newObject("PartDesign::Pad", "Feature")
            pad.Label = (f["name"] or "Intersect") + " tool"
            pad.Profile = sk
            pad.Reversed = along.dot(normal) < 0
            if f["dir"] == "symmetric":
                pad.SideType = "Symmetric"
            pad.Type = "Length"
            pad.Length = tool_length
            if f["extent"] == "distance" and f.get("length"):
                pad.Length = abs(f["length"])
                self.expression(pad, "Length", f.get("param"))
            tool.Tip = pad
            self.doc.recompute()
        finally:
            self.body = main
        o = main.newObject("PartDesign::Boolean", "Feature")
        o.Label = f["name"] or o.Name
        o.Type = "Common"
        o.setObjects([tool])
        return o

    def sweep(self, f, st):
        """A sweep: FreeCAD's Pipe, the profile along the path. Normal to the path is its Standard mode, parallel to the
        original profile its Fixed mode, a guide rail its Auxiliary mode. It has no taper or twist."""
        op = f["op"]
        if f.get("loops") is None or not f.get("path"):
            st["status"], st["why"] = "skipped", "its profile or path wasn't decoded"
            return None
        if op in ("new",) or self.body is None:
            if op == "cut":
                st["status"], st["why"] = "skipped", "a cut with nothing to cut"
                return None
            self.new_body()
        if op in ("intersect", "surface"):
            st["status"], st["why"] = "skipped", op + " sweeps aren't carried yet"
            return None
        sk, _ = self.sketch_for(f)
        # The path's curves alone solid, as drawn: no outline to trim.
        path, _ = self.sketch_for({"sketch": f["path"]["sketch"], "loops": [f["path"]["curves"]], "wires": None, "name": f["name"] + " path"})
        o = self.body.newObject("PartDesign::SubtractivePipe" if op == "cut" else "PartDesign::AdditivePipe", "Feature")
        o.Label = f["name"] or o.Name
        o.Profile = sk
        o.Spine = (path, ["Edge%d" % (i + 1) for i in range(len(path.Shape.Edges))])
        why = []
        if f.get("rail"):
            rail, _ = self.sketch_for({"sketch": f["rail"]["sketch"], "loops": [f["rail"]["curves"]], "wires": None, "name": f["name"] + " rail"})
            o.Mode = "Auxiliary"
            # Spelt AuxillerySpine before FreeCAD 1.0.
            setattr(o, "AuxiliarySpine" if hasattr(o, "AuxiliarySpine") else "AuxillerySpine", (rail, ["Edge%d" % (i + 1) for i in range(len(rail.Shape.Edges))]))
            if f.get("scaling") not in (None, "kNoProfileScaling"):
                why.append("the guide rail turns the profile but doesn't scale it")
        elif f.get("orientation") == "kParallelToOriginalProfile":
            o.Mode = "Fixed"
        else:
            o.Mode = "Standard"
        for what in ("taper", "twist"):
            value = f.get(what)
            if value and App.Units.Quantity(value).Value != 0:
                why.append("its %s (%s) isn't carried: FreeCAD's pipe has none" % (what, value))
        if why:
            st["status"], st["why"] = "approximate", "; ".join(why)
        return o

    def loft(self, f, st):
        """A loft through its sections. With a centerline, FreeCAD's pipe through the same sections along it, which keeps
        each section square to the line as Inventor's does; otherwise FreeCAD's loft. Rails, point sections and end
        conditions aren't carried."""
        op = f["op"]
        sections = f.get("sections") or []
        if len(sections) < 2 or any(s is None or (s.get("loops") is None and s.get("point") is None) for s in sections):
            st["status"], st["why"] = "skipped", "a section wasn't decoded"
            return None
        if op in ("new",) or self.body is None:
            if op == "cut":
                st["status"], st["why"] = "skipped", "a cut with nothing to cut"
                return None
            self.new_body()
        if op in ("intersect", "surface"):
            st["status"], st["why"] = "skipped", op + " lofts aren't carried yet"
            return None
        def section(s):
            if s.get("point") is None:
                return self.sketch_for(dict(s, name=f["name"]))[0]
            # A sketch point (a loft to a point): FreeCAD takes a sketch whose only solid geometry is that point, as a
            # Hole takes its centres.
            data = self.sketches[s["sketch"]]
            return SketchBuilder(self.doc, self.params, data, self.body, set(), "%s (%s)" % (data["name"] or "Sketch", f["name"]), [s["point"]]).sk
        sketches = [section(s) for s in sections]
        cut = op == "cut"
        line = f.get("centerline") if f.get("loftType") == "kLoftWithCenterline" else None
        if line is not None:
            if not line.get("loops"):
                st["status"], st["why"] = "skipped", "its centerline wasn't decoded"
                return None
            # The outline of an open line isn't wanted, only the curve: no wires, so the sketch keeps it as drawn.
            path, _ = self.sketch_for({"sketch": line["sketch"], "loops": line["loops"], "wires": None, "name": f["name"] + " centerline"})
            if trim_to_sections(path, sketches[0], sketches[-1]):
                self.doc.recompute()
                st["centerline_trimmed"] = True
            o = self.body.newObject("PartDesign::SubtractivePipe" if cut else "PartDesign::AdditivePipe", "Feature")
            o.Spine = (path, ["Edge%d" % (i + 1) for i in range(len(path.Shape.Edges))])
            o.Mode = "Standard"
        else:
            o = self.body.newObject("PartDesign::SubtractiveLoft" if cut else "PartDesign::AdditiveLoft", "Feature")
            o.Closed = bool(f.get("closed"))
        o.Label = f["name"] or o.Name
        o.Profile = sketches[0]
        o.Sections = sketches[1:]
        if line is not None:
            # A pipe only blends into its other sections when told to; by default it sweeps the first unchanged.
            o.Transformation = "Multisection"
        if f.get("merge"):
            o.Refine = True
        why = []
        if f.get("rails"):
            why.append("its %d rail(s) aren't carried" % len(f["rails"]))
        for end in ("first", "last"):
            c = (f.get(end) or {}).get("condition")
            # A sharp point is what FreeCAD's loft makes at a point anyway.
            if c and c not in ("kFreeLoftCondition", "kSharpPointLoftCondition"):
                why.append("the %s section's %s isn't carried" % (end, c[1:].replace("LoftCondition", "").lower()))
        if why:
            st["status"], st["why"] = "approximate", "; ".join(why)
        return o

    def revolve(self, f, st):
        op = f["op"]
        if op in ("new",) or self.body is None:
            if op == "cut":
                st["status"], st["why"] = "skipped", "a cut with nothing to cut"
                return None
            self.new_body()
        if op in ("intersect", "surface"):
            st["status"], st["why"] = "skipped", op + " revolves aren't carried yet"
            return None
        sk, data = self.sketch_for(f)
        cut = op == "cut"
        o = self.body.newObject("PartDesign::Groove" if cut else "PartDesign::Revolution", "Feature")
        o.Label = f["name"] or o.Name
        o.Profile = sk
        placement = App.Placement(matrix_of(data["matrix"]))
        inverse = placement.inverse()
        a, b = inverse.multVec(V(*f["axis"][0])), inverse.multVec(V(*f["axis"][1]))
        reference, direction = self.axis_in(sk, a, b)
        o.ReferenceAxis = reference
        along = direction.dot(b - a) > 0
        if f["extent"] == "full":
            o.Angle = 360
        else:
            value = self.params.value(f.get("param"), None)
            if value is None:
                value = App.Units.Quantity(f["angle"]).Value
            o.Angle = abs(value)
            self.expression(o, "Angle", f.get("param"))
            if f["dir"] == "symmetric":
                o.Midplane = True
            else:
                o.Reversed = (f["dir"] == "negative") != (not along)
        return o

    def axis_in(self, sk, a, b):
        """The sketch's own axis or a line of it, if one lies on the revolve's axis, else a construction line added for
        it; and which way that reference runs."""
        d = b - a
        d = d * (1.0 / d.Length)
        def on_axis(p):
            v = p - a
            return (v - d * v.dot(d)).Length < 1e-6
        if on_axis(V(0, 0, 0)):
            if abs(d.x) > 1 - 1e-9:
                return (sk, ["H_Axis"]), V(1, 0, 0)
            if abs(d.y) > 1 - 1e-9:
                return (sk, ["V_Axis"]), V(0, 1, 0)
        # A construction line on the axis. Solid lines aren't used: FreeCAD numbers "EdgeN" among the solid edges only.
        for i, g in enumerate(sk.Geometry):
            if isinstance(g, Part.LineSegment) and sk.getConstruction(i) and on_axis(g.StartPoint) and on_axis(g.EndPoint):
                index = [k for k, h in enumerate(sk.Geometry) if isinstance(h, Part.LineSegment) and sk.getConstruction(k)].index(i)
                return (sk, ["Axis%d" % index]), g.EndPoint - g.StartPoint
        i = sk.addGeometry(Part.LineSegment(a, b), True)
        self.doc.recompute()
        index = [k for k, h in enumerate(sk.Geometry) if isinstance(h, Part.LineSegment) and sk.getConstruction(k)].index(i)
        return (sk, ["Axis%d" % index]), b - a

    def remove(self, o):
        """Take back a feature that didn't compute, leaving the body as it was."""
        previous = getattr(o, "BaseFeature", None)
        self.doc.removeObject(o.Name)
        self.doc.recompute()
        if previous is not None and self.body.Tip != previous:
            self.body.Tip = previous
            self.doc.recompute()

    def edges_each(self, items, shape):
        """For each of Inventor's edge inputs, the FreeCAD edge names it lands on in this shape."""
        def matching(ref):
            return ["Edge%d" % (i + 1) for i, e in enumerate(shape.Edges) if edge_matches(ref, e, shape)]
        # An edge copied by a mirror is described as either of the two, and Inventor lists each as an input of its own
        # (the same pair twice in one real part; a pair, and one of it alone, in another). So an
        # input with a choice takes an edge no other input has: those with one description claim theirs first.
        claimed = set()
        for alternatives in items:
            if len(alternatives) == 1:
                claimed.update(matching(alternatives[0]))
        out = []
        for alternatives in items:
            found = []
            if len(alternatives) > 1:
                every = [matching(ref) for ref in alternatives]
                free = [f for f in every if f and not set(f) & claimed]
                found = free[0] if free else next((f for f in every if f), [])
                claimed.update(found)
            else:
                found = matching(alternatives[0])
            if not found:
                # A spline where two surfaces meet can end a little off where Inventor has it, the surfaces being the same.
                for ref in alternatives:
                    if ref.get("kind") == "curve":
                        found = ["Edge%d" % (i + 1) for i, e in enumerate(shape.Edges) if edge_matches(ref, e, shape, 0.15)]
                        if found:
                            break
            out.append(found)
        return out

    def edges_of(self, items, st):
        """FreeCAD edge names on the body's current shape for Inventor's edge inputs; items it can't place are counted."""
        if self.body is None or self.body.Tip is None:
            return None, [], ["what it works on wasn't built"]
        tip = self.body.Tip
        names, missing = [], []
        for alternatives, found in zip(items, self.edges_each(items, tip.Shape)):
            if found:
                for n in found:
                    if n not in names:
                        names.append(n)
            else:
                missing.append(alternatives[0].get("why") or alternatives[0].get("kind"))
        return tip, names, missing

    def sound(self, o):
        """Whether a feature computed to a solid that passes the checker. A feature can report success on a solid that
        doesn't, and everything after it then fails. FreeCAD's face merging is often what spoils it, so try without."""
        if not o.isValid():
            return False
        if o.Shape.isNull() or o.Shape.isValid():
            return True
        if getattr(o, "Refine", False):
            o.Refine = False
            self.doc.recompute()
            return o.isValid() and o.Shape.isValid()
        return False

    def fillet(self, f, st):
        made = None
        for k, s in enumerate(f["sets"]):
            if s["mode"] == 4:
                st["status"], st["why"] = "skipped", "fillets on all rounds aren't carried yet"
                continue
            made = self.fillet_set(f, s, k, st) or made
        return made

    def fillet_set(self, f, s, k, st):
        """One radius of a fillet. All the edges in one feature if FreeCAD will; otherwise as many pieces as it takes,
        finding the rest again on each new solid, which is often enough once a neighbour is rounded."""
        tip, names, missing = self.edges_of(s["edges"], st)
        if missing:
            st["status"] = "approximate" if names else "skipped"
            st["why"] = "%d of %d edges not found: %s" % (len(missing), len(s["edges"]), missing[0][:100])
        if not names:
            return None
        radius = s["radius"]
        pending = s["edges"]
        made, piece = None, 0
        started = time.time()
        # Inventor's edges that FreeCAD's fillet bends round a hole: done with a profile instead, the hole cut again.
        # Those where that didn't work are let be.
        force, let_be, turns = [], [], 0
        while pending:
            turns += 1
            tip = self.body.Tip
            each = self.edges_each(pending, tip.Shape)
            names = []
            for found in each:
                names += [n for n in found if n not in names]
            if not names:
                # What's left went with its tangent neighbours in an earlier piece. The final comparison checks this.
                pending = []
                break
            label = (f["name"] or "Fillet") + ("" if k == 0 else " (%d)" % (k + 1)) + ("" if piece == 0 else " piece %d" % (piece + 1))
            o, ease = None, 0
            forced = [(a, found) for a, found in zip(pending, each) if found and any(a is b for b in force)]
            if forced:
                a, found = forced[0]
                force = [b for b in force if b is not a]
                # On a solid of revolution the section is exact; else the profile along the edge.
                o = self.round_revolved(tip, found, radius, label)
                if o is not None:
                    st["revolved"] = st.get("revolved", 0) + len(found)
                else:
                    o = self.round_with_tool(tip, found[0], radius, s.get("param"), label) if len(found) == 1 else None
                    if o is not None:
                        st["round_a_hole"] = st.get("round_a_hole", 0) + 1
                if o is None:
                    let_be.append(a)
                    continue
                keep = found
            merged = None if o else merged_round(tip.Shape, names, radius)
            if merged:
                # A face exactly as wide as the two rounds (a full round): FreeCAD's own fillet a hair smaller fits,
                # meets whatever the edges run into as Inventor's does, and keeps its radius. The profile ends square
                # where the face does (on a real part's slot floors, 0.15 mm off where they run into a sphere).
                eased = next((e for e in (1e-4, 5e-4, radius * 1e-4) if rounds(tip.Shape, radius - e, names)), None)
                if eased is not None:
                    o = self.fillet_feature(tip, names, radius, eased, s.get("param"), label)
                    if o is not None:
                        keep, ease, merged = list(names), eased, None
            if merged:
                # Two rounds on a face narrower than they need become one, as Inventor makes them.
                keep, tool = merged
                o = self.round_with_tool(tip, None, radius, s.get("param"), label, tool)
                if o is not None:
                    st["merged"] = st.get("merged", 0) + 1
            if o is None:
                keep, ease = pick_edges(tip.Shape, radius, names, started)
                if keep and set(keep) != set(names) and not ease:
                    # FreeCAD would round these in pieces. On a solid of revolution the section does them all at once,
                    # exactly: a real part's second piece came out off in volume and broke every cut made after it.
                    o = self.round_revolved(tip, names, radius, label)
                    if o is not None:
                        keep = list(names)
                        st["revolved"] = st.get("revolved", 0) + len(names)
                if o is None:
                    o = self.fillet_feature(tip, keep, radius, ease, s.get("param"), label) if keep else None
                    if o is not None and newly_broken(tip.Shape, o.Shape):
                        # Faulted where the solid before was clean: the section's round, if there is one and it's
                        # clean, is exact. Else FreeCAD's stays: on a real part nothing after it went wrong.
                        self.remove(o)
                        alt = self.round_revolved(tip, keep, radius, label)
                        if alt is not None and bop_clean(alt.Shape) is not False:
                            o = alt
                            st["revolved"] = st.get("revolved", 0) + len(keep)
                        else:
                            if alt is not None:
                                self.remove(alt)
                            o = self.fillet_feature(tip, keep, radius, ease, s.get("param"), label)
                    bent = bent_round_hole(tip.Shape, o.Shape, keep, radius, self.model.get("expect")) if o is not None and not ease else []
                    odd = [a for a, found in zip(pending, each) if set(found) & set(bent) and not any(a is b for b in let_be)]
                    if odd and turns < 4 * len(s["edges"]):
                        self.remove(o)
                        force += odd
                        continue
                    if o is not None and not ease and set(keep) == set(names) and not agrees_with_section(tip.Shape, o.Shape, names, radius):
                        # On a solid of revolution the section is exact; FreeCAD's fillet can round an edge a bigger
                        # round has already met tangentially, where Inventor's leaves it (a real part: 0.6 mm³ off).
                        self.remove(o)
                        o = None
            if o is None:
                # On a solid of revolution, worked out in its section: exact, and the ball runs over faces too narrow
                # for it as Inventor's does.
                o = self.round_revolved(tip, names, radius, label)
                if o is not None:
                    keep = list(names)
                    st["revolved"] = st.get("revolved", 0) + len(names)
            if o is None:
                # FreeCAD's fillet can't run a round past the end of a face. A profile cut or added along the edge can.
                keep = []
                for n in names:
                    o = self.round_with_tool(tip, n, radius, s.get("param"), label)
                    if o is not None:
                        keep = [n]
                        st["by_tool"] = st.get("by_tool", 0) + 1
                        break
                if o is None:
                    break
            if ease:
                st["status"] = "approximate"
                st["why"] = "a full round: radius eased by %.4f mm, as FreeCAD can't close one exactly" % ease
            cleared = self.keep_holes(tip, o, label)
            if cleared is not o:
                st["holes_kept"] = st.get("holes_kept", 0) + 1
                o = cleared
            made, piece = o, piece + 1
            pending = [a for a, found in zip(pending, each) if found and not set(found) <= set(keep)]
            if piece > 3 * len(s["edges"]) or time.time() - started > 2 * FILLET_SEARCH_SECONDS:
                break
        if piece > 1:
            st["pieces"] = piece
        if st.get("revolved") and not st.get("by_tool"):
            st["status"] = "approximate"
            st["why"] = "%d edge(s) rounded in the revolved section, where FreeCAD's fillet refused or came out different: the shape is exact, its radius fixed" % st["revolved"]
        if st.get("by_tool"):
            # It can differ from Inventor's: one that adds material partly fills any hole it crosses.
            st["status"] = "approximate"
            st["why"] = "%d edge(s) rounded with a sketch profile, as FreeCAD's fillet refused them" % st["by_tool"]
        if pending and made is None:
            st["status"] = "failed"
            st["why"] = "FreeCAD couldn't round any of %d edge(s)" % len(s["edges"])
        elif pending:
            st["status"] = "failed"
            st["why"] = "FreeCAD rounded %d of %d edges, %d still refused" % (len(s["edges"]) - len(pending), len(s["edges"]), len(pending))
        return made

    def fillet_feature(self, tip, names, radius, ease, param, label):
        o = self.body.newObject("PartDesign::Fillet", "Feature")
        o.Label = label
        o.Radius = radius - ease
        ref = self.params.ref(param) if param else None
        if ref:
            o.setExpression("Radius", "(%s) - %r mm" % (ref, ease) if ease else ref)
        o.Base = (tip, names)
        self.body.Tip = o
        self.doc.recompute()
        if self.sound(o) and not o.Shape.isNull() and inside_box(o.Shape, tip.Shape) and right_way(tip.Shape, o.Shape, names, radius):
            return o
        self.remove(o)
        return None

    def keep_holes(self, tip, o, label):
        """A round that fills a corner can run over a hole there. Inventor keeps the hole open; FreeCAD's fillet and a
        profile added along the edge both fill its mouth (seen on real parts). Each hole the new material
        reaches into is cut through it again, the hole's own size and axis. Returns the last feature made."""
        try:
            added = o.Shape.cut(tip.Shape)
        except Exception:
            return o
        last = o
        for k, (r, axis, start, height) in enumerate(holes_to_reopen(tip.Shape, added)):
            cut = self.body.newObject("PartDesign::SubtractiveCylinder", "Feature")
            cut.Label = label + " hole kept open" + ("" if k == 0 else " %d" % (k + 1))
            cut.Radius, cut.Height = r, height
            cut.Placement = App.Placement(start, App.Rotation(V(0, 0, 1), axis))
            before = last.Shape.Volume
            self.body.Tip = cut
            self.doc.recompute()
            # Nor may it cut the part in pieces (a real part came out as three solids where the hole met a slot).
            if self.sound(cut) and not cut.Shape.isNull() and cut.Shape.Volume < before and len(cut.Shape.Solids) <= len(last.Shape.Solids):
                last = cut
            else:
                self.remove(cut)
        return last

    def round_revolved(self, tip, names, radius, label):
        """Rounds on a solid of revolution worked out in its half section (rounds.revolved_round): each piece a sketch,
        revolved on or grooved out. The pieces are fixed geometry: the round doesn't follow its radius parameter."""
        tool = revolved_round(tip.Shape, names, radius)
        if tool is None:
            return None
        made = None
        for kind in ("add", "cut"):
            for face in tool[kind]:
                before = self.body.Tip.Shape.Volume
                sk = self.body.newObject("Sketcher::SketchObject", "Sketch")
                sk.Label = label + " section"
                place_sketch(sk, self.body, tool["placement"])
                geometry = []
                for e in face.Edges:
                    a, b = e.valueAt(e.FirstParameter), e.valueAt(e.LastParameter)
                    if isinstance(e.Curve, Part.Line):
                        geometry.append(Part.LineSegment(a, b))
                    elif isinstance(e.Curve, Part.Circle):
                        geometry.append(Part.Arc(a, e.valueAt((e.FirstParameter + e.LastParameter) / 2), b))
                    else:
                        geometry.append(e.toNurbs().Edges[0].Curve)
                sk.addGeometry(geometry, False)
                o = self.body.newObject("PartDesign::Revolution" if kind == "add" else "PartDesign::Groove", "Feature")
                o.Profile = sk
                o.ReferenceAxis = (sk, ["V_Axis"])
                o.Angle = tool["angle"]
                o.Label = label
                self.body.Tip = o
                self.doc.recompute()
                if self.sound(o) and not o.Shape.isNull() and (o.Shape.Volume > before if kind == "add" else o.Shape.Volume < before):
                    made = o
                    continue
                self.remove(o)
                self.doc.removeObject(sk.Name)
                self.doc.recompute()
                return None if made is None else made
        return made

    def round_with_tool(self, tip, name, radius, param, label, tool=None):
        """One edge rounded by a sketch profile (the corner outside the rolling ball) cut along it, or added where the
        edge is concave. It runs on into whatever lies past the end of a face, as Inventor's does."""
        before = tip.Shape.Volume
        tool = tool or round_profile(tip.Shape, name, radius)
        if tool is None:
            return None
        sk = self.body.newObject("Sketcher::SketchObject", "Sketch")
        sk.Label = label + " profile"
        place_sketch(sk, self.body, tool["placement"])
        cx, cy = tool["center"]
        (a, b), rest = tool["arc"], tool["corner"]
        # Round the loop: arc start to end, then out to the corner and back.
        arc = Part.ArcOfCircle(Part.Circle(V(cx, cy, 0), V(0, 0, 1), radius), a, b)
        points = [arc.EndPoint] + [V(p[0], p[1], 0) for p in rest] + [arc.StartPoint]
        lines = [Part.LineSegment(points[i], points[i + 1]) for i in range(len(points) - 1)]
        # Each piece's (start, end) ends in the loop's order; an arc always runs counterclockwise, so may be backwards.
        ends = [(1, 2)] * len(lines)
        if tool.get("middle"):
            # The middle face is a hole's or boss's wall: the short way round it between the two edges.
            (mx, my), mr = tool["middle"]
            p, q = points[1], points[2]
            u, w = math.atan2(p.y - my, p.x - mx), math.atan2(q.y - my, q.x - mx)
            if (w - u) % (2 * math.pi) < math.pi:
                lines[1], ends[1] = Part.ArcOfCircle(Part.Circle(V(mx, my, 0), V(0, 0, 1), mr), u, u + (w - u) % (2 * math.pi)), (1, 2)
            else:
                lines[1], ends[1] = Part.ArcOfCircle(Part.Circle(V(mx, my, 0), V(0, 0, 1), mr), w, w + (u - w) % (2 * math.pi)), (2, 1)
        sk.addGeometry([arc] + lines, False)
        last = len(lines)
        sk.addConstraint(Sketcher.Constraint("Coincident", 0, 2, 1, ends[0][0]))
        for i in range(1, last):
            sk.addConstraint(Sketcher.Constraint("Coincident", i, ends[i - 1][1], i + 1, ends[i][0]))
        sk.addConstraint(Sketcher.Constraint("Coincident", last, ends[-1][1], 0, 1))
        size = sk.addConstraint(Sketcher.Constraint("Radius", 0, radius))
        for i, g in enumerate(tool["faces"]):
            # A straight neighbour stays as a fixed line that the arc sits tangent to, so the radius can change.
            if g is not None:
                line = sk.addGeometry(Part.LineSegment(V(g[0][0], g[0][1], 0), V(g[1][0], g[1][1], 0)), True)
                sk.addConstraint(Sketcher.Constraint("Block", line))
                sk.addConstraint(Sketcher.Constraint("Tangent", 0, 2 - i if tool["swapped"] else 1 + i, line))
        ref = self.params.ref(param) if param else None
        if ref:
            sk.renameConstraint(size, "Radius")
            sk.setExpression("Constraints.Radius", ref)
        self.doc.recompute()
        solved = sk.isValid() and sk.solve() == 0 and sk.Geometry[0].StartPoint.distanceToPoint(arc.StartPoint) < 1e-6 and sk.Geometry[0].EndPoint.distanceToPoint(arc.EndPoint) < 1e-6
        o = None
        if solved:
            if tool["kind"] == "line":
                o = self.body.newObject("PartDesign::Pocket" if tool["cut"] else "PartDesign::Pad", "Feature")
                o.Profile = sk
                o.Type = "Length"
                o.SideType = "Symmetric"
                o.Length = tool["length"]
            else:
                o = self.body.newObject("PartDesign::Groove" if tool["cut"] else "PartDesign::Revolution", "Feature")
                o.Profile = sk
                o.ReferenceAxis = (sk, ["V_Axis"])
                o.Angle = tool["angle"]
                o.Midplane = tool["angle"] < 360
            o.Label = label
            self.body.Tip = o
            self.doc.recompute()
            changed = self.sound(o) and not o.Shape.isNull() and (o.Shape.Volume < before if tool["cut"] else o.Shape.Volume > before)
            if changed:
                return o
            self.remove(o)
        self.doc.removeObject(sk.Name)
        self.doc.recompute()
        return None

    def chamfer_pieces(self, f, st, size):
        """An equal-distance chamfer FreeCAD refuses whole: as many pieces as it takes, as for fillets, finding the rest
        again on each new solid."""
        pending, made, piece = f["edges"], None, 0
        started = time.time()
        while pending:
            tip = self.body.Tip
            each = self.edges_each(pending, tip.Shape)
            names = []
            for found in each:
                names += [n for n in found if n not in names]
            if not names:
                pending = []
                break
            keep = []
            for n in names:
                if time.time() - started > FILLET_SEARCH_SECONDS:
                    break
                try:
                    out = tip.Shape.makeChamfer(size, [tip.Shape.Edges[int(m[4:]) - 1] for m in keep + [n]])
                    if not out.isNull() and out.isValid() and out.Volume > 0:
                        keep.append(n)
                except Exception:
                    pass
            if not keep:
                break
            o = self.body.newObject("PartDesign::Chamfer", "Feature")
            o.Label = (f["name"] or o.Name) + ("" if piece == 0 else " piece %d" % (piece + 1))
            o.Base = (tip, keep)
            o.Size = size
            self.expression(o, "Size", f.get("param"))
            self.body.Tip = o
            self.doc.recompute()
            if not (o.isValid() and self.sound(o)):
                self.remove(o)
                break
            made, piece = o, piece + 1
            pending = [a for a, found in zip(pending, each) if found and not set(found) <= set(keep)]
        if piece > 1:
            st["pieces"] = piece
        if pending:
            st["status"] = "failed"
            st["why"] = "FreeCAD chamfered %d of %d edges, %d still refused" % (len(f["edges"]) - len(pending), len(f["edges"]), len(pending)) if made else "BRep_API: command not done"
        return made

    def chamfer(self, f, st):
        tip, names, missing = self.edges_of(f["edges"], st)
        if missing:
            st["status"] = "approximate" if names else "skipped"
            st["why"] = "%d of %d edges not found: %s" % (len(missing), len(f["edges"]), missing[0][:100])
        if not names:
            return None
        o = self.body.newObject("PartDesign::Chamfer", "Feature")
        o.Label = f["name"] or o.Name
        o.Base = (tip, names)
        size = self.params.value(f.get("param"), 1)
        o.Size = size
        self.expression(o, "Size", f.get("param"))
        if f["kind"] == "kDistance":
            self.doc.recompute()
            if o.isValid():
                return o
            self.remove(o)
            return self.chamfer_pieces(f, st, size)
        if f["kind"] == "kDistanceAndAngle":
            o.ChamferType = "Distance and Angle"
            o.Angle = self.params.value(f.get("angleParam"), 45)
            self.expression(o, "Angle", f.get("angleParam"))
        elif f["kind"] == "kTwoDistances":
            o.ChamferType = "Two distances"
            o.Size2 = self.params.value(f.get("param2"), size)
            self.expression(o, "Size2", f.get("param2"))
        self.doc.recompute()
        if not o.isValid() or not right_way(tip.Shape, o.Shape, names, size, strict=True):
            # Eased off by 0.0001 mm a sliver of the face stays, and the chamfer comes out right (or at all: FreeCAD
            # can refuse one exactly as wide as the face).
            o.Size = size - 1e-4
            if f.get("param") and self.params.ref(f["param"]):
                o.setExpression("Size", "(%s) - 0.0001 mm" % self.params.ref(f["param"]))
            self.doc.recompute()
            if o.isValid() and right_way(tip.Shape, o.Shape, names, size, strict=True):
                st["status"] = "approximate"
                st["why"] = "eased by 0.0001 mm: at full size FreeCAD's chamfer, as wide as the face, refused or capped what lay past it"
            else:
                o.Size = size
                self.expression(o, "Size", f.get("param"))
                self.doc.recompute()
        # With an angle, Inventor measures the distance along the face it keeps with the chamfer. The surfaces can't tell: the
        # cone comes out at the same places. So measure how far the face was cut back, each way round.
        if f["kind"] == "kDistanceAndAngle" and o.isValid() and f.get("faces") and f["faces"][0]:
            point = tip.Shape.getElement(names[0]).valueAt(sum(tip.Shape.getElement(names[0]).ParameterRange) / 2)
            def error():
                width = cut_back(o.Shape, f["faces"][0][0], point)
                return None if width is None else abs(width - size)
            first = error()
            if first is not None and first > 1e-4:
                o.FlipDirection = True
                self.doc.recompute()
                second = error() if o.isValid() else None
                if second is None or second >= first:
                    o.FlipDirection = False
                    self.doc.recompute()
            return o
        # Which side gets which distance: FreeCAD's choice of face can't be set, only flipped; the final solid decides.
        if f["kind"] != "kDistance" and o.isValid() and surfaces_missing(self.model, o.Shape) > 0:
            before = surfaces_missing(self.model, o.Shape)
            o.FlipDirection = True
            self.doc.recompute()
            if not o.isValid() or surfaces_missing(self.model, o.Shape) >= before:
                o.FlipDirection = False
                self.doc.recompute()
        return o

    def hole(self, f, st):
        if self.body is None:
            st["status"], st["why"] = "skipped", "a hole with nothing to cut"
            return None
        # A hole's axis points out of the material: positive drills against it.
        along = V(*f["axis"]) if f.get("axis") else None
        if along is not None and f["dir"] != "negative":
            along = along * -1
        if f["centers"]:
            data = self.sketches[f["sketch"]]
            label = "%s (%s)" % (data["name"] or "Sketch", f["name"])
            sk = SketchBuilder(self.doc, self.params, data, self.body, set(), label, f["centers"]).sk
            normal = App.Placement(matrix_of(data["matrix"])).Rotation.multVec(V(0, 0, 1))
        elif f.get("position") and along is not None:
            # Placed from edges or on a point: a sketch of its own at that spot, looking back along the drill.
            rotation = App.Rotation(V(0, 0, 1), along * -1)
            m = App.Placement(V(*f["position"]), rotation).toMatrix()
            data = {"id": -f["id"], "name": f["name"] + " centre", "matrix": [m.A11, m.A12, m.A13, m.A14, m.A21, m.A22, m.A23, m.A24, m.A31, m.A32, m.A33, m.A34, 0, 0, 0, 1],
                    "curves": [{"id": 1, "kind": "point", "ref": False, "hidden": False, "x": 0, "y": 0, "r": 0, "start": -1, "end": -1, "center": -1, "on": []}], "rules": []}
            sk = SketchBuilder(self.doc, self.params, data, self.body, set(), data["name"], [1]).sk
            normal = along * -1
            st["why"] = "placed by a fixed position, not by the edges Inventor measured from"
            st["status"] = "approximate"
        else:
            st["status"], st["why"] = "skipped", "no position decoded"
            return None
        self.doc.recompute()
        o = self.body.newObject("PartDesign::Hole", "Feature")
        o.Label = f["name"] or o.Name
        o.Profile = sk
        if along is not None:
            o.Reversed = along.dot(normal) > 0
        o.Threaded = False
        value = self.params.value
        o.Diameter = value(f.get("diameter"), 1)
        self.expression(o, "Diameter", f.get("diameter"))
        if f["extent"] == "through":
            o.DepthType = "ThroughAll"
        else:
            o.DepthType = "Dimension"
            o.Depth = value(f.get("depth"), 1)
            self.expression(o, "Depth", f.get("depth"))
            # Inventor measures a spot-faced hole's depth from the bottom of the spot face, FreeCAD from the surface.
            if f["kind"] == "kSpotFaceHole":
                o.Depth = value(f.get("depth"), 1) + value(f.get("headDepth"), 0)
                if self.params.ref(f.get("depth")) and self.params.ref(f.get("headDepth")):
                    o.setExpression("Depth", "%s + %s" % (self.params.ref(f.get("depth")), self.params.ref(f.get("headDepth"))))
            if f["flat"]:
                o.DrillPoint = "Flat"
            else:
                o.DrillPoint = "Angled"
                o.DrillPointAngle = value(f.get("tip"), 118)
                self.expression(o, "DrillPointAngle", f.get("tip"))
                o.DrillForDepth = False
        kind = f["kind"]
        if kind in ("kCounterBoreHole", "kSpotFaceHole"):
            o.HoleCutType = "Counterbore"
            o.HoleCutCustomValues = True
            o.HoleCutDiameter = value(f.get("headDiameter"), 1)
            o.HoleCutDepth = value(f.get("headDepth"), 1)
            self.expression(o, "HoleCutDiameter", f.get("headDiameter"))
            self.expression(o, "HoleCutDepth", f.get("headDepth"))
        elif kind == "kCounterSinkHole":
            o.HoleCutType = "Countersink"
            o.HoleCutCustomValues = True
            o.HoleCutDiameter = value(f.get("headDiameter"), 1)
            o.HoleCutCountersinkAngle = value(f.get("headAngle"), 90)
            self.expression(o, "HoleCutDiameter", f.get("headDiameter"))
            self.expression(o, "HoleCutCountersinkAngle", f.get("headAngle"))
        if f.get("thread"):
            o.Label2 = "Thread " + f["thread"] + " (the hole is at its tap drill size)"
        return o

    def plane_datum(self, origin, normal, name):
        """An origin plane if it lies there, else a datum plane at a fixed place."""
        n = V(*normal)
        o = V(*origin)
        roles = {f.Role: f for f in self.body.Origin.OriginFeatures}
        if abs(o.dot(n)) < 1e-9:
            for role, axis in (("XY_Plane", V(0, 0, 1)), ("XZ_Plane", V(0, 1, 0)), ("YZ_Plane", V(1, 0, 0))):
                if abs(abs(n.dot(axis)) - 1) < 1e-9:
                    return (roles[role], [""])
        d = self.body.newObject("PartDesign::Plane", "Plane")
        d.Label = name
        d.MapMode = "Deactivated"
        d.Placement = App.Placement(o, App.Rotation(V(0, 0, 1), n))
        return (d, [""])

    def axis_datum(self, point, direction, name):
        d = V(*direction)
        p = V(*point) if point else V()
        roles = {f.Role: f for f in self.body.Origin.OriginFeatures}
        w = p - d * p.dot(d)
        if w.Length < 1e-9:
            for role, axis in (("X_Axis", V(1, 0, 0)), ("Y_Axis", V(0, 1, 0)), ("Z_Axis", V(0, 0, 1))):
                if abs(d.dot(axis)) > 1 - 1e-9:
                    return (roles[role], [""]), d.dot(axis) < 0
        line = self.body.newObject("PartDesign::Line", "Line")
        line.Label = name
        line.MapMode = "Deactivated"
        line.Placement = App.Placement(p, App.Rotation(V(0, 0, 1), d))
        return (line, [""]), False

    def transform(self, f, st):
        if self.body is None:
            st["status"], st["why"] = "skipped", "nothing to copy"
            return None
        kind = f["type"]
        if kind == "sketch":
            st["status"], st["why"] = "skipped", "sketch-driven patterns aren't carried yet"
            return None
        # FreeCAD copies a fillet made whole, but not one made in pieces or with sketch profiles: only its
        # last piece is a feature to copy (a real part's slot copies came out unrounded; gaps-pattern-fillet). Those are left
        # out of the pattern, and round_copies rounds each copy's edges afterwards.
        dressed = [p for p in f["parents"] if p in self.pieced]
        self.copy_rounds = [x for x in self.model["features"] if x.get("id") in dressed] if not f["ofBody"] else []
        originals = [self.made[p] for p in f["parents"] if p in self.made and p not in dressed]
        if not originals and dressed and not f["ofBody"]:
            st["status"], st["why"] = "skipped", "it copies only rounds, which FreeCAD can't copy"
            self.copy_rounds = []
            return None
        if f["ofBody"]:
            originals = []
        elif not originals:
            st["status"], st["why"] = "skipped", "what it copies wasn't built"
            return None
        elif len(originals) < len(f["parents"]) - len(dressed):
            st["status"], st["why"] = "approximate", "%d of %d features it copies weren't built" % (len(f["parents"]) - len(dressed) - len(originals), len(f["parents"]))
        # FreeCAD can't copy a copy. A copy of a pattern and what it copies is both patterns run one after the other.
        inner = [x for x in originals if x.Name in self.transforms]
        if inner:
            first = inner[0]
            if len(inner) > 1 or not all(x in first.Originals for x in originals if x is not first):
                st["status"], st["why"] = "skipped", "a copy of copies of different features isn't carried yet"
                return None
            o = self.body.newObject("PartDesign::MultiTransform", "Feature")
            o.Label = f["name"] or o.Name
            o.Originals = first.Originals
            steps = []
            for g in (self.transforms[first.Name], f):
                step = self.body.newObject(TRANSFORM_KINDS[g["type"]], "Feature")
                step.Label = "%s (%s)" % (f["name"], g["name"])
                self.configure_transform(step, g, st)
                steps.append(step)
            o.Transformations = steps
            return o
        if kind in ("circular", "rectangular") and f["mid"][0] and not f["ofBody"] and f["n"][0] and f["n"][0] > 2:
            return self.both_ways(f, originals, st)
        o = self.body.newObject(TRANSFORM_KINDS[kind], "Feature")
        o.Label = f["name"] or o.Name
        if f["ofBody"]:
            o.TransformMode = "Whole shape"
        else:
            o.Originals = originals
            self.transforms[o.Name] = f
        self.configure_transform(o, f, st)
        return o

    def both_ways(self, f, originals, st):
        """A mid-plane pattern: Inventor puts the copies at 0, +s, -s, +2s, -2s... from the original (its own report on
        the generated pattern-circ-mid and pattern-rect-mid parts). FreeCAD's patterns go one way, so it is two: one
        ahead over the copies on the plus side and one back over the rest, both copying the same features. Their counts
        and spacing are fixed numbers, not tied to Inventor's parameters."""
        kind, n = f["type"], f["n"][0]
        if kind == "circular":
            angle = f["angle"]
            s = angle if f["incremental"] else (angle / n if abs(abs(angle) - 360) < 1e-6 else angle / (n - 1))
            side = dict(f, incremental=True, angle=s, angleParam=None)
        else:
            s = f["step"][0] / (n - 1) if f["fitted"][0] else f["step"][0]
            side = dict(f, fitted=[False] + list(f["fitted"][1:]), step=[s] + list(f["step"][1:]), spacingParam=[None] + list(f["spacingParam"][1:]))
        side["countParam"] = [None] + list(f["countParam"][1:])
        side["mid"] = [False] + list(f["mid"][1:])
        ahead, back = 1 + n // 2, 1 + (n - 1) // 2
        made = None
        for count, reverse, label in ((ahead, False, f["name"]), (back, True, "%s (other side)" % f["name"])):
            g = dict(side, n=[count] + list(f["n"][1:]))
            if reverse:
                if kind == "circular":
                    g["reversed"] = not f["reversed"]
                else:
                    g["dir1"] = [-v for v in f["dir1"]]
            o = self.body.newObject(TRANSFORM_KINDS[kind], "Feature")
            o.Label = label or o.Name
            o.Originals = originals
            self.transforms[o.Name] = f
            self.configure_transform(o, g, st)
            self.body.Tip = o
            self.doc.recompute()
            made = o
        st["note"] = "mid-plane: two patterns, one each way, with fixed counts and spacing"
        return made

    def configure_transform(self, o, f, st):
        kind = f["type"]
        if kind == "mirror":
            o.MirrorPlane = self.plane_datum(f["origin"], f["normal"], o.Label + " plane")
        elif kind == "rectangular":
            self.linear_side(o, f, 0, "")
            if f["hasY"]:
                self.linear_side(o, f, 1, "2")
        else:
            axis, flipped = self.axis_datum(f["axisPoint"], f["axisDir"], o.Label + " axis")
            o.Axis = axis
            # Inventor turns copies about its axis the right-hand way; the reversed flag flips that.
            o.Reversed = flipped != f["reversed"]
            o.Occurrences = f["n"][0]
            if f.get("countParam") and f["countParam"][0]:
                self.expression(o, "Occurrences", f["countParam"][0])
            if f["incremental"]:
                o.Mode = "Spacing"
                o.Offset = f["angle"]
                self.expression(o, "Offset", f.get("angleParam"))
            else:
                o.Mode = "Extent"
                o.Angle = f["angle"]
                self.expression(o, "Angle", f.get("angleParam"))
            # (Two-way patterns are made by both_ways; with two copies, 0 and +s, mid-plane is one-sided anyway.)
            if f["mid"][0] and f["n"][0] and f["n"][0] > 2:
                st["status"], st["why"] = "approximate", "a mid-plane pattern was made one-sided"

    def linear_side(self, o, f, k, suffix):
        d = f["dir1"] if k == 0 else f["dir2"]
        axis, flipped = self.direction_ref(d, o.Label)
        setattr(o, "Direction" + suffix, axis)
        setattr(o, "Reversed" + suffix, flipped)
        setattr(o, "Occurrences" + suffix, f["n"][k])
        if f["countParam"][k]:
            self.expression(o, "Occurrences" + suffix, f["countParam"][k])
        if f["fitted"][k]:
            setattr(o, "Mode" + suffix, "Extent")
            setattr(o, "Length" + suffix, f["step"][k])
            self.expression(o, "Length" + suffix, f["spacingParam"][k])
        else:
            setattr(o, "Mode" + suffix, "Spacing")
            setattr(o, "Offset" + suffix, f["step"][k])
            self.expression(o, "Offset" + suffix, f["spacingParam"][k])
            # Per-copy spacings: -1 means "the common offset". The second direction's list starts at 0 and stays there.
            if suffix:
                setattr(o, "Spacings" + suffix, [-1.0] * max(1, f["n"][k] - 1))

    def direction_ref(self, d, label):
        """A pattern direction: an origin axis if it runs along one, else a datum line through the origin."""
        return self.axis_datum([0, 0, 0], d, label + " direction")

    def flange(self, f, st):
        """A flange at 90 degrees: the plate trimmed back along the edge, which also takes the bend of a flange met at a
        corner, then an L section along the edge. Sizes are fixed numbers. The stored edge is already cut short at corners."""
        if self.body is None or self.body.Tip is None or not f["edges"] or not f["edges"][0]:
            st["status"], st["why"] = "skipped", "what it works on wasn't built"
            return None
        e = next((c for c in f["edges"][0] if c.get("kind") == "line"), None)
        if e is None or f["dir"] != "positive" or abs(f["angle"] - 90) > 1e-6:
            st["status"], st["why"] = "skipped", "only straight edges, the positive side and 90 degrees are carried"
            return None
        a, b = V(*e["a"]), V(*e["b"])
        shape = self.body.Tip.Shape
        along = (b - a).normalize()
        mid = (a + b) * 0.5
        # The plate's thickness runs away from the sketched face the edge lies on; the bend goes the other way.
        found = None
        for n in (V(1, 0, 0), V(0, 1, 0), V(0, 0, 1)):
            for n in (n, n * -1):
                if abs(n.dot(along)) > 1e-6:
                    continue
                o = n.cross(along)
                for o in (o, o * -1):
                    # Plate on the +n side only, both at the edge and well in from it: a corner alone would pass at the edge.
                    if all(shape.isInside(mid - o * d + n * (f["thickness"] / 2), 1e-7, False) and not shape.isInside(mid - o * d - n * (f["thickness"] / 2), 1e-7, False) for d in (0.01, f["radius"] + f["thickness"] + 0.01)):
                        found = (n, o)
        if found is None:
            st["status"], st["why"] = "skipped", "couldn't tell which way the plate lies from its edge"
            return None
        n, o = found
        T, R, H = f["thickness"], f["radius"], f["height"]
        z = o.cross(n)
        m = App.Matrix(o.x, n.x, z.x, mid.x, o.y, n.y, z.y, mid.y, o.z, n.z, z.z, mid.z, 0, 0, 0, 1)
        def section(name, geometry):
            sk = self.body.newObject("Sketcher::SketchObject", "Sketch")
            sk.Label = "%s %s" % (f["name"], name)
            sk.Placement = App.Placement(m)
            sk.addGeometry(geometry, False)
            return sk
        def ring(points):
            return [Part.LineSegment(points[i], points[(i + 1) % len(points)]) for i in range(len(points))]
        out = R + T
        band = section("trim", ring([V(-out, T, 0), V(0, T, 0), V(0, -R, 0), V(-out, -R, 0)]))
        cut = self.body.newObject("PartDesign::Pocket", "Feature")
        cut.Label = "%s trim" % f["name"]
        cut.Profile = band
        cut.Type = "Length"
        cut.SideType = "Symmetric"
        cut.Length = a.distanceToPoint(b) + 2 * out
        self.body.Tip = cut
        self.doc.recompute()
        if not cut.isValid():
            st["status"], st["why"] = "failed", "couldn't trim the plate for the flange"
            self.remove(cut)
            return None
        c = V(-out, -R, 0)
        tip = H - out
        outer = Part.ArcOfCircle(Part.Circle(c, V(0, 0, 1), out), 0, math.pi / 2)
        inner = Part.ArcOfCircle(Part.Circle(c, V(0, 0, 1), R), 0, math.pi / 2)
        lines = [Part.LineSegment(V(-out, T, 0), V(-out, 0, 0)),
                 Part.LineSegment(V(0, -R, 0), V(0, -R - tip, 0)),
                 Part.LineSegment(V(0, -R - tip, 0), V(-T, -R - tip, 0)),
                 Part.LineSegment(V(-T, -R - tip, 0), V(-T, -R, 0))]
        sk = section("section", [outer, inner] + lines)
        o2 = self.body.newObject("PartDesign::Pad", "Feature")
        o2.Label = f["name"] or o2.Name
        o2.Profile = sk
        o2.Type = "Length"
        o2.SideType = "Symmetric"
        o2.Length = a.distanceToPoint(b)
        st["note"] = "sizes are fixed numbers; the bend is an ordinary rounded section"
        return o2

    def open_shell(self, f, st):
        """A shell with faces taken away: FreeCAD's Thickness, on the faces lying on the surfaces Inventor names."""
        if f["direction"] not in ("kInsideShellDirection", "kOutsideShellDirection") or f["sets"] > 1:
            st["status"], st["why"] = "skipped", "shells to both sides, or with more than one thickness, aren't carried yet"
            return None
        tip = self.body.Tip
        names = []
        for alternatives in f["faces"]:
            found = ["Face%d" % (i + 1) for i, face in enumerate(tip.Shape.Faces) if any(same_surface(ref, face) for ref in alternatives if ref.get("a"))]
            if not found:
                st["status"], st["why"] = "skipped", "a face it opens wasn't found"
                return None
            names += [n for n in found if n not in names]
        o = self.body.newObject("PartDesign::Thickness", "Feature")
        o.Label = f["name"] or o.Name
        o.Base = (tip, names)
        o.Value = self.params.value(f.get("param"), f["thickness"])
        self.expression(o, "Value", f.get("param"))
        # FreeCAD's wall goes outward unless reversed (shell-inside gave shell-outside's volume the other way round).
        # Inventor's corners stay sharp: the walls meet where they cross.
        o.Reversed = f["direction"] == "kInsideShellDirection"
        o.Join = "Intersection"
        self.body.Tip = o
        self.doc.recompute()
        if self.sound(o) and not o.Shape.isNull():
            if f["sets"]:
                st["status"], st["why"] = "approximate", "faces Inventor gives a thickness of their own have the common one: FreeCAD's Thickness has one"
            return o
        self.remove(o)
        st["status"], st["why"] = "failed", "FreeCAD couldn't make the walls"
        return None

    def shell(self, f, st):
        """A shell inward with no face taken away: a closed void inside. FreeCAD's Thickness needs an open face, so the
        solid offset inward by the thickness becomes a body of its own, cut from this one."""
        if self.body is None or self.body.Tip is None:
            st["status"], st["why"] = "skipped", "what it works on wasn't built"
            return None
        if f["faces"]:
            return self.open_shell(f, st)
        if f["sets"] or f["direction"] != "kInsideShellDirection":
            st["status"], st["why"] = "skipped", "only inward shells with no face removed are carried"
            return None
        main, tip = self.body, self.body.Tip
        thickness = self.params.value(f.get("param"), f["thickness"])
        def hollow(inside, breach=0.0):
            """The cut, kept if it leaves one more closed skin; or, for Inventor's own hollow, if what of it lies outside
            this rebuild is under the given share (the rebuild's outside can differ a little where its fillets do)."""
            inside.Label = "%s inside" % f["name"]
            inside.Visibility = False
            tool = self.doc.addObject("PartDesign::Body", "Body")
            tool.Label = "%s tool" % f["name"]
            tool.BaseFeature = inside
            tool.Visibility = False
            o = main.newObject("PartDesign::Boolean", "Feature")
            o.Label = f["name"] or o.Name
            o.Type = "Cut"
            o.setObjects([tool])
            main.Tip = o
            self.doc.recompute()
            if self.sound(o) and inside.isValid() and not inside.Shape.isNull():
                if len(o.Shape.Shells) == len(tip.Shape.Shells) + 1:
                    return o
                if breach and inside.Shape.cut(tip.Shape).Volume < breach * inside.Shape.Volume:
                    return o
            self.remove(o)
            # A body given a base feature makes an object of its own for it, which would be left behind.
            tool.removeObjectsFromDocument()
            self.doc.removeObject(tool.Name)
            self.doc.removeObject(inside.Name)
            self.doc.recompute()
            return None
        for join in ("Intersection", "Arc"):
            inside = self.doc.addObject("Part::Offset", "Offset")
            inside.Source = tip
            inside.Value = -thickness
            ref = self.params.ref(f["param"]) if f.get("param") else None
            if ref:
                inside.setExpression("Value", "-" + ref)
            inside.Join = join
            o = hollow(inside)
            if o is not None:
                return o
        # OpenCASCADE's offset gives up on many filleted solids. Inventor's solid holds the hollow as its inner skin: use
        # that, at its fixed size, when it is the only shell in the part.
        voids = []
        path = os.environ.get("CADCONVERT_STEP")
        if path and os.path.exists(path) and sum(x["type"] == "shell" for x in self.model["features"]) == 1:
            try:
                for solid in Part.read(path).Solids:
                    for skin in solid.Shells[1:]:
                        v = Part.Solid(skin)
                        voids.append(v if v.Volume > 0 else v.complement())
            except Exception:
                voids = []
        if voids:
            inside = self.doc.addObject("Part::Feature", "Hollow")
            inside.Shape = voids[0] if len(voids) == 1 else Part.makeCompound(voids)
            o = hollow(inside, 1e-3)
            if o is not None:
                st["status"], st["why"] = "approximate", "FreeCAD couldn't offset the solid inward: the hollow is Inventor's, at a fixed size"
                if len(o.Shape.Shells) != len(tip.Shape.Shells) + 1:
                    st["why"] += ", and it breaks through this rebuild's outside in places"
                return o
        st["status"], st["why"] = "failed", "FreeCAD couldn't offset the solid inward"
        return None

    def imported(self, f, st):
        """An imported body: no history in Inventor, so its shape as it was imported, which invre takes from the B-rep's
        history (….made.step beside the model), becomes the base of a new body; the features after it build on that."""
        path = os.environ.get("CADCONVERT_MODEL", "")
        path = path[:-len(".model.json")] + ".made.step" if path.endswith(".model.json") else None
        if not f.get("made") or not path or not os.path.exists(path):
            st["status"], st["why"] = "skipped", "not carried yet: the file has no record of its shape as imported"
            return None
        if any(b.Shape.Solids for b in self.bodies if not b.Shape.isNull()):
            # The history's first body is the one written: an imported body after other solids isn't it.
            st["status"], st["why"] = "skipped", "not carried yet: an imported body after other solids"
            return None
        shape = hollows_cut(Part.read(path))
        if not shape.Solids:
            st["status"], st["why"] = "failed", "its shape as imported has no solid"
            return None
        base = self.doc.addObject("Part::Feature", "Imported")
        base.Label = "%s (imported shape)" % (f["name"] or "Imported")
        base.Shape = shape.Solids[0] if len(shape.Solids) == 1 else Part.makeCompound(shape.Solids)
        base.Visibility = False
        body = self.new_body()
        body.BaseFeature = base
        self.doc.recompute()
        st["shape"] = "as imported, from the file's B-rep history"
        return None

    def run(self, f):
        st = {"name": f["name"], "type": f["type"], "status": "ok"}
        report["features"].append(st)
        self.run_one(f, st)
        if f.get("rolledBack") and f["computed"]:
            # How it built is kept, but it doesn't count either way: Inventor's solid hasn't got it.
            st["built"] = st["status"]
            st["status"] = "rolled back"
            st["why"] = "after End of Part in Inventor: in the history, not in the solid" + ("; built: %s" % st["why"] if st.get("why") else "")

    def run_one(self, f, st):
        if not f["computed"]:
            st["status"], st["why"] = "not computed", "suppressed or failing in Inventor, so not in its solid either"
            return
        if f.get("rolledBack") and self.body is not None and self.body.Name not in self.ends:
            # The first feature past Inventor's End of Part: the body's result stays where the marker is (end_of_part).
            self.ends[self.body.Name] = (self.body, self.body.Tip)
        makers = {"extrude": self.extrude, "revolve": self.revolve, "fillet": self.fillet, "chamfer": self.chamfer, "hole": self.hole,
                  "flange": self.flange, "shell": self.shell, "loft": self.loft, "sweep": self.sweep, "mirror": self.transform, "rectangular": self.transform, "circular": self.transform, "sketch": self.transform, "imported body": self.imported}
        if f["type"] == "sheet metal" and f.get("kind") in ("bend", "corner"):
            # Inventor keeps these as features of their own; the flange before them already holds their shape.
            if any(x["type"] == "flange" and x["status"] == "ok" for x in report["features"]):
                st["status"], st["why"] = "cosmetic", "made as part of its flange"
                return
        if f["type"] == "thread":
            # A cosmetic thread: it changes nothing in Inventor's solid either.
            st["status"], st["why"] = "cosmetic", "a thread drawn on the surface; the solid is the same without it"
            return
        if f["type"] not in makers:
            st["status"], st["why"] = "skipped", "not carried yet"
            return
        try:
            o = makers[f["type"]](f, st)
        except Exception as ex:
            st["status"], st["why"] = "failed", "%s: %s" % (type(ex).__name__, str(ex)[:200])
            report.setdefault("trace", []).append(traceback.format_exc())
            return
        if o is None:
            return
        self.body.Tip = o
        self.doc.recompute()
        self.unrefine_if_loosened(o, st)
        if not o.isValid():
            st["status"] = "failed"
            st["why"] = " ".join(o.getStatusString().split())[:200] if hasattr(o, "getStatusString") else "FreeCAD couldn't compute it"
            self.remove(o)
            return
        if not self.sound(o):
            st["solid_flawed"] = True
            note("%s leaves a solid that doesn't pass the checker" % (f["name"] or f["type"]))
        self.made[f["id"]] = o
        if f["type"] in ("fillet", "chamfer") and (st.get("pieces", 1) > 1 or st.get("by_tool")):
            self.pieced.add(f["id"])
        if f["type"] in TRANSFORM_KINDS and getattr(self, "copy_rounds", None):
            self.round_copies(f, st)
        if self.body.Shape.isNull():
            return
        st["volume"] = self.body.Shape.Volume
        self.tolerance[o.Name] = loosest(o.Shape)

    def unrefine_if_loosened(self, o, st):
        """FreeCAD's face merging (Refine) can spoil a solid and still pass every check but the full one. Cut in half
        through a turned part's axis, a real part lost 7.3 mm³ in the merge, and the merge also loosened its edges'
        and vertices' tolerances to 4.5 mm, in the solid it was built on too, as they share them: every feature after it
        went wrong. A solid far looser than the one it was built on gets the feature without merging, the body
        recomputed from the start so that what was loosened is made again."""
        base = getattr(o, "BaseFeature", None)
        if not getattr(o, "Refine", False) or base is None or base.Name not in self.tolerance or o.Shape.isNull():
            return
        if loosest(o.Shape) <= max(1e-3, 100 * self.tolerance[base.Name]):
            return
        def rebuild():
            for x in self.body.Group:
                x.touch()
            self.doc.recompute()
        o.Refine = False
        rebuild()
        if o.isValid() and not o.Shape.isNull() and o.Shape.isValid() and loosest(o.Shape) <= max(1e-3, 100 * self.tolerance[base.Name]):
            st["note"] = "faces left unmerged: FreeCAD's merge spoiled the solid"
            return
        o.Refine = True
        rebuild()

    def round_copies(self, f, st):
        """After a pattern or mirror: the fillets and chamfers it copies, on the copies' edges. Inventor's edges are moved
        as each copy is (both ways round for a circular pattern, since only real edges are kept), and the rounds made on
        whichever of them the patterned solid has."""
        todo, self.copy_rounds = self.copy_rounds, []
        moves = copy_moves(f)
        if not moves:
            st["status"], st["why"] = "approximate", "the rounds it copies weren't made on the copies"
            return
        made = 0
        for g in todo:
            shape = self.body.Tip.Shape
            def moved(items):
                out = []
                for alternatives in items:
                    for m in moves:
                        alt = [moved_edge(ref, m) for ref in alternatives]
                        if any(a is None for a in alt):
                            continue
                        if any(edge_matches(a, e, shape) for a in alt for e in shape.Edges) and alt not in out:
                            out.append(alt)
                return out
            sub = {"name": "%s (%s copies)" % (g["name"], f["name"]), "type": g["type"], "status": "ok"}
            if g["type"] == "fillet":
                sets = [dict(s, edges=moved(s["edges"])) for s in g["sets"]]
                sets = [s for s in sets if s["edges"]]
                if not sets:
                    continue
                o = self.fillet(dict(g, name=sub["name"], sets=sets), sub)
            else:
                edges = moved(g["edges"])
                if not edges:
                    continue
                o = self.chamfer(dict(g, name=sub["name"], edges=edges), sub)
                if o is not None and o is not self.body.Tip:
                    self.body.Tip = o
                    self.doc.recompute()
            if o is not None and self.body.Tip.isValid():
                made += 1
            st.setdefault("copied_rounds", []).append({"name": sub["name"], "status": sub["status"], "why": sub.get("why")})
            if sub["status"] not in ("ok",):
                st["status"], st["why"] = "approximate", "%s on the copies: %s" % (g["name"], sub.get("why") or sub["status"])
        if not made:
            st["status"], st["why"] = "approximate", "the rounds it copies weren't made on the copies"

    def end_of_part(self):
        """Inventor's End of Part marker as FreeCAD's Tip: what follows it is built, and the body's result is where the
        marker is (gaps-end-of-part)."""
        for body, tip in self.ends.values():
            if tip is not None and body.Tip != tip:
                body.Tip = tip
        if self.ends:
            self.doc.recompute()


# ---------------------------------------------------------------- finding edges by geometry

def rounds(shape, radius, names):
    """Whether these edges round to a solid that passes the checker, tried on the shape without making a feature."""
    try:
        out = shape.makeFillet(radius, [shape.Edges[int(n[4:]) - 1] for n in names])
    except Exception:
        return False
    return not out.isNull() and out.isValid() and out.Volume > 0 and inside_box(out, shape)


def inside_box(out, shape, tol=1e-3):
    """A round only ever takes material off or fills a corner, so it can't reach past the box round what it started from.
    FreeCAD sometimes accepts one that bulges out (where two rounds meet exactly)."""
    a, b = out.BoundBox, shape.BoundBox
    if a.XMin > b.XMin - tol and a.YMin > b.YMin - tol and a.ZMin > b.ZMin - tol and a.XMax < b.XMax + tol and a.YMax < b.YMax + tol and a.ZMax < b.ZMax + tol:
        return True
    # The quick box can stand well off curved faces; only the exact one tells.
    a, b = tight_box(out), tight_box(shape)
    return a.XMin > b.XMin - tol and a.YMin > b.YMin - tol and a.ZMin > b.ZMin - tol and a.XMax < b.XMax + tol and a.YMax < b.YMax + tol and a.ZMax < b.ZMax + tol


def copy_moves(f):
    """The moves that take a pattern's or mirror's original to its copies, as matrices. Both ways round (and both
    ways along) since which way Inventor's copies run is settled by which moved edges the solid really has."""
    moves = []
    if f["type"] == "circular":
        n, angle = f["n"][0], f["angle"]
        if not n or n < 2:
            return []
        step = angle if f.get("incremental") else (angle / n if abs(abs(angle) - 360) < 1e-6 else angle / (n - 1))
        p, d = V(*f["axisPoint"]), V(*f["axisDir"])
        for k in range(1, n):
            for sign in (1, -1):
                r = App.Placement(V(0, 0, 0), App.Rotation(d, sign * k * step))
                moves.append(App.Placement(p, App.Rotation()).multiply(r).multiply(App.Placement(p * -1, App.Rotation())).toMatrix())
    elif f["type"] == "mirror":
        o, n = V(*f["origin"]), V(*f["normal"])
        n.normalize()
        m = App.Matrix(1 - 2 * n.x * n.x, -2 * n.x * n.y, -2 * n.x * n.z, 0, -2 * n.y * n.x, 1 - 2 * n.y * n.y, -2 * n.y * n.z, 0,
                       -2 * n.z * n.x, -2 * n.z * n.y, 1 - 2 * n.z * n.z, 0, 0, 0, 0, 1)
        shift = o - m.multVec(o)
        m.A14, m.A24, m.A34 = shift.x, shift.y, shift.z
        moves.append(m)
    elif f["type"] == "rectangular":
        sides = []
        for k in (0, 1) if f.get("hasY") else (0,):
            n, d = f["n"][k], f["dir1"] if k == 0 else f["dir2"]
            if not n or d is None or f["step"][k] is None:
                return []
            s = f["step"][k] / (n - 1) if f["fitted"][k] and n > 1 else f["step"][k]
            sides.append([V(*d) * (s * i) for i in range(n)])
        for sign in (1, -1):
            for i, a in enumerate(sides[0]):
                for j, b in enumerate(sides[1] if len(sides) > 1 else [V(0, 0, 0)]):
                    if i or j:
                        t = (a + b) * sign
                        moves.append(App.Placement(t, App.Rotation()).toMatrix())
    return moves


def moved_edge(ref, m):
    """One of Inventor's edge descriptions, moved by the matrix m; None for one that has no geometry to move."""
    if ref.get("kind") not in ("line", "circle", "curve") or not ref.get("a"):
        return None
    turn = App.Matrix(m)
    turn.A14 = turn.A24 = turn.A34 = 0
    out = dict(ref)
    point = lambda p: list(m.multVec(V(*p)))
    out["a"] = point(ref["a"])
    if ref["kind"] == "circle":
        out["b"] = list(turn.multVec(V(*ref["b"])))
        for k in ("from", "to"):
            if ref.get(k):
                out[k] = point(ref[k])
    elif ref.get("b"):
        out["b"] = point(ref["b"])
    return out


def trim_to_sections(path, first, last):
    """Inventor's centerline loft runs from the first section's plane to the last's, while FreeCAD's pipe runs the whole
    line: a real part's line starts 1.5 mm short of one section and ends 0.8 mm past another. The line goes to
    construction and the piece between the planes is drawn beside it. Whether anything was trimmed."""
    solid = [i for i in range(path.GeometryCount) if not path.getConstruction(i) and not isinstance(path.Geometry[i], Part.Point)]
    if len(solid) != 1 or len(path.Shape.Edges) != 1:
        return False
    e = path.Shape.Edges[0]
    def cross(sk, at_start):
        # The parameter where the line meets the sketch's plane: the crossing nearest the start for the first section,
        # nearest the end for the last.
        pl = sk.getGlobalPlacement()
        base, n = pl.Base, pl.Rotation.multVec(V(0, 0, 1))
        d = lambda u: (e.valueAt(u) - base).dot(n)
        us = [e.FirstParameter + (e.LastParameter - e.FirstParameter) * k / 400.0 for k in range(401)]
        brackets = [(a, b) for a, b in zip(us, us[1:]) if d(a) == 0 or d(a) * d(b) < 0]
        if not brackets:
            return None
        a, b = brackets[0] if at_start else brackets[-1]
        for _ in range(60):
            m = (a + b) / 2
            if d(a) * d(m) <= 0:
                b = m
            else:
                a = m
        return (a + b) / 2
    u0, u1 = cross(first, True), cross(last, False)
    if u0 is None or u1 is None or abs(u1 - u0) < 1e-9:
        return False
    u0, u1 = sorted((u0, u1))
    span = e.LastParameter - e.FirstParameter
    if abs(u0 - e.FirstParameter) < 1e-9 * span and abs(u1 - e.LastParameter) < 1e-9 * span:
        return False
    g = path.Geometry[solid[0]]
    try:
        # Splines cut themselves down; a straight line or an arc (gaps-loft-long-centerline) is made again between the two.
        if isinstance(g, Part.LineSegment):
            piece = Part.LineSegment(g.value(u0), g.value(u1))
        elif isinstance(g, Part.ArcOfCircle):
            piece = Part.ArcOfCircle(g.Circle, u0, u1)
        else:
            piece = g.copy()
            piece.segment(u0, u1)
    except Exception:
        return False
    path.toggleConstruction(solid[0])
    path.addGeometry(piece, False)
    return True


def agrees_with_section(before, after, names, radius):
    """Whether a round on a solid of revolution changed its volume by what the section says (rounds.revolved_round,
    by Pappus: each piece's area times the way its centre goes round); True where there's no section to ask."""
    tool = revolved_round(before, names, radius)
    if tool is None:
        return True
    change = 2 * math.pi * (sum(f.Area * f.CenterOfMass.x for f in tool["add"]) - sum(f.Area * f.CenterOfMass.x for f in tool["cut"]))
    return abs((after.Volume - before.Volume) - change) <= 1e-5 * max(1.0, before.Volume)


def right_way(before, after, names, radius, strict=False):
    """A round on outside corners only takes material off, and on inside corners only adds it. FreeCAD's fillet can
    report success on one that does the opposite (on a real part: inside corners, 20 mm³ less). Strict, it's measured:
    nothing at all added at outside corners, nothing taken at inside ones. A chamfer as wide as the face it cuts into
    can come out with a cap over a recess past that face (a set screw's cup point filled), and still lose volume."""
    ways = set()
    eps = min(1e-3, radius / 100)
    for n in names:
        e = before.Edges[int(n[4:]) - 1]
        faces = before.ancestorsOfType(e, Part.Face)
        if len(faces) != 2:
            return True
        m = e.valueAt((e.FirstParameter + e.LastParameter) / 2)
        try:
            n1, n2 = [f.normalAt(*f.Surface.parameter(m)) for f in faces]
        except Exception:
            return True
        if (n1 - n2).Length < 1e-6:
            return True
        # Just off the edge, past one face and short of the other: air at an outside corner, material at an inside one.
        found = probe(before, m + (n1 - n2) * eps)
        if found is None:
            return True
        ways.add(found)
    change = after.Volume - before.Volume
    if ways not in ({True}, {False}):
        return True
    if (ways == {True} and change <= -1e-6) or (ways == {False} and change >= 1e-6):
        return False
    if strict:
        try:
            # Material found just off the edge (True) is an inside corner, where nothing may be taken; air, outside.
            wrong = (before.cut(after) if ways == {True} else after.cut(before)).Volume
        except Exception:
            return True
        return wrong < 1e-5 * max(1.0, before.Volume)
    return True


def loosest(shape):
    """The largest tolerance of a solid's vertices: OpenCASCADE widens it where geometry only nearly meets."""
    return max([v.Tolerance for v in shape.Vertexes] or [0.0])


BOP_CLEAN = {}


def bop_clean(shape):
    """Whether OpenCASCADE's full check (the one booleans rely on) finds nothing wrong; None where it can't say."""
    key = (round(shape.Volume, 6), len(shape.Faces), len(shape.Edges))
    if key not in BOP_CLEAN:
        try:
            shape.check(True)
            BOP_CLEAN[key] = True
        except ValueError:
            BOP_CLEAN[key] = False
        except Exception:
            BOP_CLEAN[key] = None
    return BOP_CLEAN[key]


def newly_broken(before, after):
    """A round FreeCAD made that passes isValid but that the full check faults where the solid before it was clean:
    self-intersecting. Every boolean after it can then go wrong (on real parts a pattern of grooves cut 50 mm³ where
    it should 20, and a pattern of slots came out empty)."""
    return bop_clean(before) is True and bop_clean(after) is False


def pick_edges(shape, radius, names, started):
    """The edges to round next and how far to ease the radius: all of them if they'll go together; next, all of them a
    hair smaller, since two rounds meeting exactly (a full round) defeat FreeCAD; else the largest run it accepts."""
    if rounds(shape, radius, names):
        return names, 0
    for ease in (1e-4, 5e-4, radius * 1e-4):
        if rounds(shape, radius - ease, names):
            return names, ease
    kept = []
    for n in names:
        if time.time() - started > FILLET_SEARCH_SECONDS:
            break
        if rounds(shape, radius, kept + [n]):
            kept.append(n)
    return kept, 0


def on_surface(want, p, tol=1e-3):
    """Whether a point lies on one of Inventor's surfaces. A cone's angle isn't kept, so none counts."""
    a = V(*want["a"]) if want.get("a") else None
    b = V(*want["b"]) if want.get("b") else None
    kind = want["kind"]
    if kind == "plane":
        return abs((p - a).dot(b * (1.0 / b.Length))) < tol
    if kind == "cylinder":
        n = b * (1.0 / b.Length)
        v = p - a
        return abs((v - n * v.dot(n)).Length - want["r"]) < tol
    if kind == "sphere":
        return abs(p.distanceToPoint(a) - want["r"]) < tol
    if kind == "torus":
        n = b * (1.0 / b.Length)
        v = p - a
        across = v - n * v.dot(n)
        return abs(math.hypot(across.Length - want["r"], v.dot(n)) - want["r2"]) < tol
    return False


def bent_round_hole(before, after, names, radius, want):
    """The edges whose round FreeCAD bent into a freeform face where a hole crosses it, a face that lies on none of
    Inventor's surfaces. Inventor rounds such a corner as if the hole weren't there and the hole stays cut through it
    (at a real part's arm roots). FreeCAD often writes a true cylinder as a freeform face; those are fine."""
    if not want:
        return []
    old = [f for f in before.Faces if isinstance(f.Surface, Part.BSplineSurface)]
    new = [f for f in after.Faces if isinstance(f.Surface, Part.BSplineSurface) and not any(abs(f.Area - g.Area) < 1e-6 and f.BoundBox.isInside(g.BoundBox.Center) for g in old)]
    def stray(f):
        u0, u1, v0, v1 = f.ParameterRange
        points = [f.valueAt(u0 + (u1 - u0) * i / 4.0, v0 + (v1 - v0) * j / 4.0) for i in (1, 2, 3) for j in (1, 2, 3)]
        return not any(all(on_surface(w, p) for p in points) for w in want["surfaces"])
    new = [f for f in new if stray(f)]
    if not new:
        return []
    holes = [f for f in before.Faces if isinstance(f.Surface, Part.Cylinder) and f.ParameterRange[1] - f.ParameterRange[0] > math.pi + 1e-6]
    bent = []
    for n in names:
        e = before.Edges[int(n[4:]) - 1]
        near = lambda f: e.distToShape(f)[0] < 2 * radius
        if any(near(f) for f in new) and any(near(h) for h in holes) and round_profile(before, n, radius) is not None:
            bent.append(n)
    return bent


def surfaces_missing(model, shape):
    want = model.get("expect")
    if not want:
        return 0
    return sum(1 for w in want["surfaces"] if not any(same_surface(w, f) for f in shape.Faces))


# ---------------------------------------------------------------- checking

def shape_report(doc, bodies):
    shapes = [b.Shape for b in bodies if not b.Shape.isNull()]
    if not shapes:
        return None
    all_faces = [f for s in shapes for f in s.Faces]
    box = shapes[0].BoundBox
    for s in shapes[1:]:
        box.add(s.BoundBox)
    return {"volume": sum(s.Volume for s in shapes), "faces": len(all_faces), "edges": sum(len(s.Edges) for s in shapes), "vertices": sum(len(s.Vertexes) for s in shapes),
            "bbox": [box.XMin, box.YMin, box.ZMin, box.XMax, box.YMax, box.ZMax], "valid": all(s.isValid() for s in shapes)}, all_faces


def describe_face(f):
    s = f.Surface
    words = type(s).__name__
    for attr in ("Position", "Center", "Axis", "Radius"):
        if hasattr(s, attr):
            v = getattr(s, attr)
            words += " %s=%s" % (attr, tuple(round(x, 4) for x in v) if hasattr(v, "x") else round(v, 4))
    return words


def compare(model, faces, result):
    want = model.get("expect")
    if not want:
        return
    cmp = {"inventor": {k: want[k] for k in ("faces", "edges", "vertices", "bbox")}}
    if want["bbox"]:
        cmp["bbox_off"] = max(abs(x - y) for x, y in zip(want["bbox"], result["bbox"]))
    missing = [w for w in want["surfaces"] if not any(same_surface(w, f) for f in faces)]
    extra = []
    for f in faces:
        if not any(same_surface(w, f) for w in want["surfaces"]):
            extra.append(type(f.Surface).__name__)
    cmp["surfaces"] = len(want["surfaces"])
    cmp["surfaces_missing"] = missing[:20]
    cmp["surfaces_extra"] = [describe_face(f) for f in faces if not any(same_surface(w, f) for w in want["surfaces"])][:20]
    cmp["surfaces_missing_count"] = len(missing)
    cmp["faces_unmatched"] = len(extra)
    report["compare"] = cmp


def tight_box(shape):
    """The shape's real extent: the plain box takes in a spline's control points, which can lie far outside it."""
    try:
        return shape.optimalBoundingBox(True, False)
    except Exception:
        return shape.BoundBox


def surface_points(shape, per_face=5):
    """A few points on each face, away from its edges where the face allows: from the middle of its mesh triangles,
    put back on the surface."""
    points = []
    for f in shape.Faces:
        try:
            # Coarser on big faces: only a few points are wanted, and a fine mesh of a big spline face takes seconds.
            # A copy: a face keeps the mesh it was first given, so without one the points (and the verdict on a part
            # near the limit) depend on what meshed it before.
            nodes, triangles = f.copy().tessellate(max(0.5, f.BoundBox.DiagonalLength / 200))
            step = max(1, len(triangles) // per_face)
            s = f.Surface
            for t in triangles[::step][:per_face]:
                middle = (nodes[t[0]] + nodes[t[1]] + nodes[t[2]]) * (1.0 / 3)
                points.append(s.value(*s.parameter(middle)))
        except Exception:
            continue
    return points


def surface_apart(a, b):
    """How far apart two solids' surfaces are: of points sampled on each, the share further than SURFACE_FAR_MM from the
    other, and the furthest. Stops sampling at the time limit, and counts only what it reached; None if it reached
    nothing."""
    started = time.time()
    far, seen, worst = 0, 0, 0.0
    for one, other in ((a, b), (b, a)):
        if time.time() - started > SURFACE_CHECK_SECONDS:
            break
        points = surface_points(one)
        shell = Part.Compound(other.Faces)
        for p in points:
            if time.time() - started > SURFACE_CHECK_SECONDS:
                break
            d = shell.distToShape(Part.Vertex(p))[0]
            seen += 1
            worst = max(worst, d)
            if d > SURFACE_FAR_MM:
                far += 1
    return (far / seen if seen else None), worst


def hollows_cut(shape):
    """Inventor's solid as read from its STEP, with any sealed hollow that touches the outside taken out. Inventor lets
    a hollow touch the outer skin along a line (the generated revolve-cut part: a ring cut inside a block, its rim on the
    top and bottom); STEP's readers then give the hollow back as a solid of its own, inside out, and the volumes add."""
    solids = shape.Solids
    if len(solids) < 2:
        return shape
    hollow = [s for s in solids if s.Shells and s.Shells[0].Orientation == "Reversed"]
    whole = [s for s in solids if not any(s.isSame(h) for h in hollow)]
    if not hollow or not whole:
        return shape
    out = []
    for w in whole:
        inside = [h for h in hollow if w.BoundBox.isInside(h.BoundBox)]
        for h in inside:
            try:
                w = w.cut(Part.Solid(h.Shells[0].reversed()))
            except Exception:
                return shape
        out.append(w)
    return out[0] if len(out) == 1 else Part.makeCompound(out)


def frozen_solid(doc, builder, result):
    """Inventor's exact solid, read from the STEP written beside this. It is the truth the rebuild is measured against;
    where the rebuild falls short it goes in the file too and is what shows, with the history kept beside it."""
    path = os.environ.get("CADCONVERT_STEP")
    if not path or not os.path.exists(path):
        return
    try:
        shape = hollows_cut(Part.read(path))
    except Exception as ex:
        report["step"] = {"error": str(ex)[:200]}
        return
    box = tight_box(shape)
    st = {"valid": shape.isValid(), "volume": shape.Volume, "faces": len(shape.Faces), "solids": len(shape.Solids),
          "bbox": [box.XMin, box.YMin, box.ZMin, box.XMax, box.YMax, box.ZMax]}
    report["step"] = st
    usable = st["valid"] and st["solids"] > 0 and st["volume"] > 0
    built = [b.Shape for b in builder.bodies if not b.Shape.isNull()]
    # The solid's own box beats the one from the B-rep's corners and arcs, which can't see a blend's bulge.
    if usable and result and built and "compare" in report:
        mine = tight_box(built[0] if len(built) == 1 else Part.makeCompound(built))
        report["compare"]["bbox_off"] = max(abs(x - y) for x, y in zip(st["bbox"], [mine.XMin, mine.YMin, mine.ZMin, mine.XMax, mine.YMax, mine.ZMax]))
    rebuilt = False
    if result and usable:
        off = abs(result["volume"] - st["volume"]) / st["volume"]
        st["volume_off"] = off
        # A feature left out doesn't decide it: the shape does, as below. Some are left out because nothing was left for them to do.
        rebuilt = off < 1e-3 and result["valid"]
        # The real test: subtract each from the other and measure what's left. Same volume isn't same shape.
        if rebuilt:
            whole = built[0] if len(built) == 1 else Part.makeCompound(built)
            # Subtracting often fails, and equal volumes prove nothing: copies turned to the wrong angle have the same
            # volume. Whether each solid's surface lies on the other is a check that always runs.
            st["surface_far"], st["surface_worst"] = surface_apart(whole, shape)
            # Nothing sampled in time: the subtraction below decides alone.
            rebuilt = st["surface_far"] is None or st["surface_far"] < SURFACE_FAR_LIMIT
        if rebuilt:
            try:
                left = whole.cut(shape).Volume + shape.cut(whole).Volume
                # What's left can't be less than the difference in volume, or half of either or more when the surfaces
                # already lie on each other (on a real part: 98% left of two solids 0.001% apart in volume);
                # if it is, the subtraction quietly failed.
                if 0.9 * abs(result["volume"] - st["volume"]) <= left < 0.5 * st["volume"]:
                    st["left_over"] = left / st["volume"]
                    rebuilt = st["left_over"] < 1e-3
                else:
                    st["left_over_error"] = "the subtraction failed"
            except Exception as ex:
                st["left_over_error"] = str(ex)[:120]
    report["rebuild_matches_solid"] = rebuilt
    if rebuilt or shape.isNull():
        return
    o = doc.addObject("Part::Feature", "Inventor_solid")
    o.Label = "Inventor solid (exact shape, no history)"
    o.Shape = shape
    report["frozen_added"] = True
    report["frozen_flawed"] = not usable
    if not usable:
        o.Label = "Inventor solid (has flaws: check it)"
    for b in builder.bodies:
        b.Visibility = False


# ----------------------------------------------------------------

def main():
    with open(os.environ["CADCONVERT_MODEL"], encoding="utf-8") as fh:
        model = json.load(fh)
    report["name"] = model["name"]
    report["freecad"] = ".".join(App.Version()[:3])
    doc = App.newDocument(re.sub(r"\W", "_", model["name"]) or "Part")
    # Edge names kept in full. Hashed, the names of edges a revolve or extrude makes change at the first recompute after
    # the file is opened, and every fillet and chamfer that refers to them breaks at the first parameter change.
    doc.UseHasher = False
    params = Parameters(doc, model)
    builder = Builder(doc, params, model)
    # Asked again after the rebuild ran out of time: only Inventor's solid, so the part still has a file.
    solid_only = os.environ.get("CADCONVERT_SOLID_ONLY") == "1"
    if solid_only:
        note("rebuilding the history ran out of time, so the file holds Inventor's exact solid only")
    for f in ([] if solid_only else model["features"]):
        builder.run(f)
    builder.end_of_part()
    # Sketches onto the faces and work planes Inventor drew them on, now nothing more will change under them.
    got = reattach_all(doc, builder.bodies)
    for s in report["sketches"]:
        obj = doc.getObjectsByLabel(s["name"])
        if got and obj and obj[0].Name in got:
            s["on"] = got[obj[0].Name]
    if got:
        report["sketches_attached"] = {k: list(got.values()).count(k) for k in ("face", "work plane")}
    elif got is not None:
        report["sketches_attached"] = "kept on origin planes: attached, the part came out differently"
    # Sketches no feature used still belong in the file.
    for s in ([] if solid_only else model["sketches"]):
        if s["id"] not in builder.used:
            if builder.body is None:
                builder.new_body()
            SketchBuilder(doc, params, s, builder.body, None, s["name"] or "Sketch")
    doc.recompute()
    sr = shape_report(doc, builder.bodies)
    if sr:
        report["result"] = sr[0]
        compare(model, sr[1], sr[0])
    report["recompute_errors"] = [o.Label for o in doc.Objects if not o.isValid()]
    frozen_solid(doc, builder, report.get("result"))
    # What shows on opening: Inventor's solid if it was added, else each body and its last feature.
    frozen = doc.getObject("Inventor_solid")
    shown = set()
    box = None
    if frozen is not None:
        shown.add(frozen.Name)
        box = frozen.Shape.BoundBox
    else:
        for b in builder.bodies:
            shown.add(b.Name)
            if b.Tip is not None:
                shown.add(b.Tip.Name)
            if not b.Shape.isNull():
                if box is None:
                    box = b.Shape.BoundBox
                else:
                    box.add(b.Shape.BoundBox)
    for o in doc.Objects:
        o.Visibility = o.Name in shown
    doc.saveAs(os.environ["CADCONVERT_OUT"])
    App.closeDocument(doc.Name)


try:
    main()
except Exception as ex:
    report["fatal"] = "%s: %s" % (type(ex).__name__, str(ex))
    report.setdefault("trace", []).append(traceback.format_exc())
with open(os.environ["CADCONVERT_REPORT"], "w", encoding="utf-8") as fh:
    json.dump(report, fh, indent=1)
