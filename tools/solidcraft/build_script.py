# A neutral model (invre's .model.json) as SolidCraft commands, run one at a time through solidcraft-cli's MCP server.
#   build_script.py <model.json> <out folder> [--set name=factor]
# Writes <name>.script.json (the commands SolidCraft took), <name>.sc, <name>.step and <name>.left.json (what was refused
# or isn't carried, and why). A refused constraint or dimension is left out, so the part still builds, only less tied down.
import json, math, os, queue, re, subprocess, sys, threading, time, traceback

CLI = os.environ.get("CADCONVERT_SOLIDCRAFT") or r"C:\Program Files\SolidCraft\solidcraft-cli.exe"
BODY = "Body"
LENGTH_UNITS = {"mm", "cm", "m", "km", "in", "ft", "yd", "mil", "micron", "nm", "um"}
ANGLE_UNITS = {"deg", "rad", "gon", "grad"}
# In a command's params, never sent to SolidCraft (keys starting "_"): what to try instead, in turn, if SolidCraft refuses
# it: [(params, why)]; the file's description of each edge a fillet or chamfer takes.
ALTERNATIVES = "_alternatives"
# How long one SolidCraft command may take (a big fillet can take a minute) before it is given up on.
COMMAND_SECONDS = 150
SAVE_SECONDS = 300
REFS = "_refs"
# Faces the file describes ({kind, a, b, r} surfaces), each a list of alternatives: placed on SolidCraft's own faces just
# before the command, as a point on each, in "faces".
FACE_REFS = "_face_refs"
# Fillets and chamfers SolidCraft refuses whole, made in pieces and sketch profiles (see rescue). Off unless asked for: on
# the real test parts (2026-11-02) it brought no part to match, took 40% longer, and moved as many further off as closer.
RESCUE = os.environ.get("CADCONVERT_SOLIDCRAFT_RESCUE") == "1"


def mid(a, b):
    return [(x + y) / 2 for x, y in zip(a, b)]


def arc_frame(e):
    """An arc's centre, the two axes of its plane (the first towards its start) and how far round it runs."""
    c, n, p, q = e["a"], e["b"], e["from"], e.get("to") or e["from"]
    u = [x - y for x, y in zip(p, c)]
    v = [x - y for x, y in zip(q, c)]
    size = math.sqrt(sum(x * x for x in n)) or 1
    n = [x / size for x in n]
    w = [n[1] * u[2] - n[2] * u[1], n[2] * u[0] - n[0] * u[2], n[0] * u[1] - n[1] * u[0]]
    angle = math.atan2(sum(a * b for a, b in zip(w, v)), sum(a * b for a, b in zip(u, v)))
    if abs(angle) < 1e-6:
        angle = 2 * math.pi
    elif angle < 0:
        angle += 2 * math.pi
    if e.get("rev"):
        angle -= 2 * math.pi
    return c, u, w, angle


def edge_point(e):
    if e["kind"] == "line":
        return mid(e["a"], e["b"])
    if e["kind"] in ("circle", "arc") and e.get("from"):
        # Part way round, clear of the vertex where other edges may meet.
        c, u, w, angle = arc_frame(e)
        t = angle / 2 if abs(angle) < 6.28 else 1.0
        return [o + a * math.cos(t) + b * math.sin(t) for o, a, b in zip(c, u, w)]
    return None


def edge_samples(e, k=8):
    if e["kind"] == "line" and e.get("a") and e.get("b"):
        return [[x + (y - x) * i / k for x, y in zip(e["a"], e["b"])] for i in range(k + 1)]
    if e["kind"] in ("circle", "arc") and e.get("from"):
        c, u, w, angle = arc_frame(e)
        return [[o + a * math.cos(angle * i / k) + b * math.sin(angle * i / k) for o, a, b in zip(c, u, w)] for i in range(k + 1)]
    return [x for x in (e.get("a"), e.get("b")) if x]


SOCKET_GAP = 0.002


UNIT_SIZES = {"mm": 1, "cm": 10, "m": 1000, "km": 1e6, "in": 25.4, "ft": 304.8, "yd": 914.4, "mil": 0.0254, "micron": 1e-3, "um": 1e-3, "nm": 1e-6,
              "deg": 1, "rad": 180 / math.pi, "gon": 0.9, "grad": 0.9, "ul": 1}
FUNCTIONS = {"sin": lambda x: math.sin(math.radians(x)), "cos": lambda x: math.cos(math.radians(x)), "tan": lambda x: math.tan(math.radians(x)),
             "asin": lambda x: math.degrees(math.asin(x)), "acos": lambda x: math.degrees(math.acos(x)), "atan": lambda x: math.degrees(math.atan(x)),
             "sqrt": math.sqrt, "abs": abs, "floor": math.floor, "ceil": math.ceil, "round": round, "PI": math.pi, "E": math.e}


def evaluate(formula, values):
    """An Inventor formula's value in mm, degrees or a plain number, from the parameters' values, as far as plain
    arithmetic goes; None where it goes further."""
    out, last = [], None
    for t in re.findall(r"\d+\.?\d*(?:[eE][-+]?\d+)?|\.\d+|\w+|\S", formula):
        if re.match(r"[\d.]", t):
            out.append(t)
        elif re.match(r"[A-Za-z_]\w*$", t):
            if last is not None and re.match(r"[\d.)]", last) and t in UNIT_SIZES:
                out.append("*%r" % UNIT_SIZES[t])
            elif t in values:
                out.append("(%r)" % values[t])
            elif t in FUNCTIONS:
                out.append("F[%r]" % t)
            else:
                return None
        elif t in "+-*/()^,":
            out.append("**" if t == "^" else t)
        else:
            return None
        last = t
    try:
        return float(eval("".join(out), {"__builtins__": {}}, {"F": FUNCTIONS}))
    except Exception:
        return None


def bezier(c):
    """A sketch spline of one span and degree 2 to 5 as the control points of the same curve at degree 3 (up to 3) or 5,
    or None."""
    d, knots, flat = c.get("degree"), c.get("knots") or [], c.get("poles") or []
    points = [[flat[i], flat[i + 1]] for i in range(0, len(flat), 2)]
    if not d or not 2 <= d <= 5 or len(points) != d + 1 or len(knots) != 2 * (d + 1) or len(set(knots[:d + 1])) != 1 or len(set(knots[d + 1:])) != 1:
        return None
    target = 3 if d <= 3 else 5
    while d < target:
        # Degree elevation: the same curve with one more control point.
        points = [points[0]] + [[i / (d + 1) * a + (1 - i / (d + 1)) * b for a, b in zip(points[i - 1], points[i])] for i in range(1, d + 1)] + [points[-1]]
        d += 1
    return points


def socket(f):
    """A screw's socket: the polygon and a circle its corners all touch, stored either as those two or as the slivers
    between them, each a side and an arc. Then the same feature with its outline as the polygon and the whole circle,
    and a point in each sliver to pick them by: with the corners touching, SolidCraft doesn't take the polygon as a hole.
    (FreeCAD needs the circle a little larger; SolidCraft fails with that.) Otherwise None."""
    loops, wires = f.get("loops"), f.get("wires")
    if f["op"] != "join" or f["extent"] not in ("next", "distance") or not loops or not wires or len(loops) != len(wires) or len(wires) < 2:
        return None
    polygon = [i for i, w in enumerate(wires) if all(e["kind"] == "line" for e in w)]
    arcs = [e for w in wires for e in w if e["kind"] == "circle"]
    if len(polygon) != 1 or not arcs:
        return None
    rest = [w for i, w in enumerate(wires) if i != polygon[0]]
    if not (len(rest) == 1 and len(rest[0]) == 1 and rest[0][0]["from"] == rest[0][0]["to"]) and any(len(w) != 2 or sum(e["kind"] == "circle" for e in w) != 1 for w in rest):
        return None
    centre, r = arcs[0]["a"], arcs[0]["r"]
    corners = [e["a"] for e in wires[polygon[0]]]
    if any(abs(math.sqrt(dot(sub(p, centre), sub(p, centre))) - r) > 1e-6 for p in corners):
        return None
    whole = dict(arcs[0], to=arcs[0]["from"], r=r - SOCKET_GAP)
    picks = []
    for e in wires[polygon[0]]:
        m = sub(mid(e["a"], e["b"]), centre)
        d = math.sqrt(dot(m, m))
        picks.append([c + x * (d + r) / 2 / d for c, x in zip(centre, m)])
    return dict(f, wires=[[whole], wires[polygon[0]]], outline=True, picks=picks)


def profile_middle(f):
    """The middle of a profile's outline in world coordinates, from the outline the file keeps."""
    pts = [e[k] for w in f.get("wires") or [] for e in w for k in (("a", "b") if e["kind"] == "line" else ("from",)) if e.get(k)]
    return [sum(p[i] for p in pts) / len(pts) for i in range(3)] if pts else None


def depth_to_cylinder(f, d, c):
    """How far the profile must go along d for every point of its outline to reach the cylinder, or None if some miss."""
    deepest = 0.0
    for p in (p for w in f.get("wires") or [] for e in w for p in edge_samples(e)):
        hit = ray_cylinder(p, d, c)
        if hit is None:
            return None
        deepest = max(deepest, math.sqrt(sum((x - y) ** 2 for x, y in zip(hit, p))))
    return deepest or None


def ray_cylinder(p, d, c):
    """The first point a ray from p along d meets a cylinder {origin, axis, r}, or None."""
    if p is None:
        return None
    n = math.sqrt(sum(x * x for x in c["axis"]))
    a = [x / n for x in c["axis"]]
    def perp(v):
        k = sum(x * y for x, y in zip(v, a))
        return [x - k * y for x, y in zip(v, a)]
    w, u = perp([x - y for x, y in zip(p, c["origin"])]), perp(d)
    A, B, C = sum(x * x for x in u), 2 * sum(x * y for x, y in zip(w, u)), sum(x * x for x in w) - c["r"] ** 2
    if A < 1e-12 or B * B - 4 * A * C < 0:
        return None
    ts = [t for t in ((-B - math.sqrt(B * B - 4 * A * C)) / (2 * A), (-B + math.sqrt(B * B - 4 * A * C)) / (2 * A)) if t > 1e-9]
    return [x + min(ts) * y for x, y in zip(p, d)] if ts else None


class Sketch:
    def __init__(self, data, keep, centers, left, values=None, name=None):
        self.data, self.left, self.values, self.images = data, left, values or {}, 0
        self.curves = {c["id"]: c for c in data["curves"]}
        self.keep, self.centers = keep, centers
        self.name = name
        self.drawn = set()
        self.points = {}
        self.names = {}     # curve id -> SolidCraft's name, where it isn't "c<id>"
        self.splines = 0
        self.out = []

    def cmd(self, command, **params):
        self.out.append({"command": command, "params": params})

    def plane(self):
        m = self.data["matrix"]
        return {"origin": [m[3], m[7], m[11]], "x_dir": [m[0], m[4], m[8]], "y_dir": [m[1], m[5], m[9]]}

    def used_points(self):
        used = set(self.centers)
        for c in self.data["curves"]:
            if c["kind"] == "line":
                used |= {c["start"], c["end"]}
        for r in self.data["rules"]:
            used |= set(r["items"] or [])
        return used

    def at(self, pid):
        # A point another curve already made is shared by name; otherwise its place.
        c = self.curves[pid]
        return self.points.get(pid, [c["x"], c["y"]])

    def own(self, pid, ref):
        self.points.setdefault(pid, ref)

    def construction(self, c):
        return c["id"] not in self.keep

    def geometry(self):
        used = self.used_points()
        for c in self.data["curves"]:
            k, name = c["kind"], "c%d" % c["id"]
            extra = {"construction": True} if self.construction(c) else {}
            if k == "line" and c["start"] in self.curves and c["end"] in self.curves:
                a, b = self.curves[c["start"]], self.curves[c["end"]]
                if math.hypot(a["x"] - b["x"], a["y"] - b["y"]) < 1e-7:
                    continue
                self.cmd("sketch.line", points=[self.at(c["start"]), self.at(c["end"])], ids=[name], **extra)
                self.drawn.add(c["id"])
                self.own(c["start"], name + ".start")
                self.own(c["end"], name + ".end")
            elif k == "circle" and c["center"] in self.curves:
                self.cmd("sketch.circle.center", center=self.at(c["center"]), radius=c["r"], id=name, **extra)
                self.drawn.add(c["id"])
                self.own(c["center"], name + ".center")
            elif k == "arc" and all(c[x] in self.curves for x in ("center", "start", "end")):
                a, b = self.curves[c["start"]], self.curves[c["end"]]
                if math.hypot(a["x"] - b["x"], a["y"] - b["y"]) < 1e-7:
                    continue
                self.drawn.add(c["id"])
                self.cmd("sketch.arc.center_point", center=self.at(c["center"]), start=self.at(c["start"]), end=self.at(c["end"]), id=name, **extra)
                self.own(c["center"], name + ".center")
                self.own(c["start"], name + ".start")
                self.own(c["end"], name + ".end")
            elif k == "spline" and bezier(c):
                # SolidCraft draws splines of degree 3 or 5 by their control points, one span when there are degree + 1 of
                # them, and names them itself, s1, s2, ... in each sketch. Inventor's (one span, degree 2 to 5) are raised
                # to one of those, which is the same curve; their ends join what meets them there.
                points = bezier(c)
                self.splines += 1
                self.names[c["id"]] = "s%d" % self.splines
                self.cmd("sketch.spline.control_point" if len(points) == 4 else "sketch.spline.control_point_5", points=points, **extra)
                self.drawn.add(c["id"])
            elif k not in ("point", "line", "circle", "arc"):
                self.left.append({"sketch": self.data["name"], "item": k, "why": "curve kind not carried"})
        for c in self.data["curves"]:
            if c["kind"] == "point" and c["id"] in used and c["id"] not in self.points and (not c["hidden"] or c["id"] in self.centers):
                self.cmd("sketch.point", point=[c["x"], c["y"]], id="p%d" % c["id"])
                self.points[c["id"]] = "p%d" % c["id"]

    def local(self, p):
        """A world point in this sketch's coordinates. Some blocks keep the outline in sketch coordinates already."""
        m = self.data["matrix"]
        d = [p[0] - m[3], p[1] - m[7], p[2] - m[11]]
        q = [d[0] * m[0] + d[1] * m[4] + d[2] * m[8], d[0] * m[1] + d[1] * m[5] + d[2] * m[9], d[0] * m[2] + d[1] * m[6] + d[2] * m[10]]
        return q[:2] if abs(q[2]) < 1e-6 else list(p[:2])

    def outline(self, wires):
        """Inventor lets curves that cross bound a profile, trimming them where they meet; SolidCraft wants the profile's
        own curves. The file keeps the trimmed outline: it goes in as the sketch's solid curves, tied to the curves they
        were cut from, with those (and their constraints) left as construction. Returns each loop's curve ids."""
        m = self.data["matrix"]
        normal_z = lambda n: n[0] * m[2] + n[1] * m[6] + n[2] * m[10]
        made, seen = [], {}
        for loop in wires:
            for e in loop:
                if e["kind"] == "line" and e.get("a") and e.get("b"):
                    a, b = self.local(e["a"]), self.local(e["b"])
                    if math.hypot(a[0] - b[0], a[1] - b[1]) > 1e-7:
                        made.append(("line", a, b))
                elif e["kind"] == "circle":
                    c = self.local(e["a"])
                    if e["from"] == e["to"]:
                        made.append(("circle", c, e["r"]))
                    else:
                        a, b = self.local(e["from"]), self.local(e["to"])
                        if (normal_z(e["b"]) > 0) == bool(e.get("rev")):
                            a, b = b, a
                        made.append(("arc", c, a, b, e["r"]))
                else:
                    return None
        # Regions that touch share an edge, listed once for each; the outline around them doesn't have it.
        def key(g):
            r = lambda p: (round(p[0], 4), round(p[1], 4))
            return (g[0], r(g[1]), round(g[2], 4)) if g[0] == "circle" else (g[0],) + tuple(sorted([r(g[-3] if g[0] == "arc" else g[1]), r(g[-2] if g[0] == "arc" else g[2])]))
        for g in made:
            seen.setdefault(key(g), []).append(g)
        made = [v[0] for v in seen.values() if len(v) % 2 == 1]
        ids = []
        for k, g in enumerate(made):
            name = "o%d" % (k + 1)
            if g[0] == "line":
                self.cmd("sketch.line", points=[g[1], g[2]], ids=[name])
                self.tie_line(name, g[1], g[2])
            elif g[0] == "circle":
                self.cmd("sketch.circle.center", center=g[1], radius=g[2], id=name)
                self.tie_arc(name, g[1], g[2])
            else:
                self.cmd("sketch.arc.center_point", center=g[1], start=g[2], end=g[3], id=name)
                self.tie_arc(name, g[1], g[4])
            ids.append((name, g))
        return ids

    def tie_line(self, name, a, b):
        for c in self.data["curves"]:
            if c["kind"] == "line" and c["id"] in self.drawn:
                s, e = self.curves[c["start"]], self.curves[c["end"]]
                dx, dy = e["x"] - s["x"], e["y"] - s["y"]
                n = math.hypot(dx, dy)
                if n > 1e-9 and all(abs((p[0] - s["x"]) * dy - (p[1] - s["y"]) * dx) / n < 1e-5 for p in (a, b)):
                    self.cmd("sketch.constraint.collinear", a=name, b="c%d" % c["id"])
                    return

    def tie_arc(self, name, centre, r):
        for c in self.data["curves"]:
            if c["kind"] in ("circle", "arc") and c["id"] in self.drawn:
                o = self.curves[c["center"]]
                if math.hypot(o["x"] - centre[0], o["y"] - centre[1]) < 1e-5 and abs(c["r"] - r) < 1e-5:
                    self.cmd("sketch.constraint.concentric", a=name, b="c%d" % c["id"])
                    self.cmd("sketch.constraint.equal", a=name, b="c%d" % c["id"])
                    return

    def ref(self, i):
        c = self.curves.get(i)
        if c is None:
            return None
        if c["kind"] == "point":
            return self.points.get(i)
        return self.names.get(i, "c%d" % i) if i in self.drawn else None

    def center_ref(self, i):
        # Inventor measures from a circle's centre; SolidCraft would measure from its edge.
        c = self.curves[i]
        return self.points.get(c["center"]) if c["kind"] in ("circle", "arc") else self.ref(i)

    def place(self, i):
        c = self.curves[i]
        return self.curves[c["center"]] if c["kind"] in ("circle", "arc") else c

    def gap(self, items):
        p, q = self.place(items[0]), self.place(items[1])
        d = math.hypot(p["x"] - q["x"], p["y"] - q["y"])
        r0, r1 = self.curves[items[0]]["r"], self.curves[items[1]]["r"]
        return abs(r0 - r1) if d < 1e-6 else abs(d - r0 - r1) if d > max(r0, r1) else abs(d + min(r0, r1) - max(r0, r1))

    def measure(self, kind, a, b):
        # Between places; to a line it's the distance from its whole length.
        p = self.place(a)
        if self.curves[b]["kind"] == "line":
            s, e = self.curves[self.curves[b]["start"]], self.curves[self.curves[b]["end"]]
            if kind == "DimDistance2D":
                dx, dy = e["x"] - s["x"], e["y"] - s["y"]
                return abs((p["x"] - s["x"]) * dy - (p["y"] - s["y"]) * dx) / math.hypot(dx, dy)
            q = s
        else:
            q = self.place(b)
        if self.curves[a]["kind"] == "line":
            p = self.curves[self.curves[a]["start"]]
        return {"DimDistance2D": math.hypot(p["x"] - q["x"], p["y"] - q["y"]), "DimDistanceH2D": abs(p["x"] - q["x"]), "DimDistanceV2D": abs(p["y"] - q["y"])}[kind]

    def mirror_of(self, c, axis):
        s, e = self.curves[axis["start"]], self.curves[axis["end"]]
        dx, dy = e["x"] - s["x"], e["y"] - s["y"]
        n = math.hypot(dx, dy)
        dx, dy = dx / n, dy / n
        vx, vy = c["x"] - s["x"], c["y"] - s["y"]
        along = vx * dx + vy * dy
        return [s["x"] + dx * 2 * along - vx, s["y"] + dy * 2 * along - vy]

    def symmetric(self, items, rs):
        # Lines of different lengths are still symmetric in Inventor: one's mirror image lies along the other. SolidCraft
        # mirrors the ends, so give each end a construction point on the other line.
        a, b, axis = (self.curves[i] for i in items)
        if a["kind"] != "line" or b["kind"] != "line":
            self.cmd("sketch.constraint.symmetry", a=rs[0], b=rs[1], line=rs[2])
            return
        for end in ("start", "end"):
            p = self.mirror_of(self.curves[a[end]], axis)
            q = self.curves[b["start"]], self.curves[b["end"]]
            if min(math.hypot(p[0] - x["x"], p[1] - x["y"]) for x in q) < 1e-6:
                other = "start" if math.hypot(p[0] - q[0]["x"], p[1] - q[0]["y"]) < 1e-6 else "end"
                self.cmd("sketch.constraint.symmetry", a="%s.%s" % (rs[0], end), b="%s.%s" % (rs[1], other), line=rs[2])
            else:
                self.images += 1
                name = "m%d_%d" % (self.data["id"], self.images)
                self.cmd("sketch.point", point=p, id=name)
                self.cmd("sketch.constraint.coincident", a=name, b=rs[1])
                self.cmd("sketch.constraint.symmetry", a="%s.%s" % (rs[0], end), b=name, line=rs[2])

    def is_end(self, line, point):
        c = self.curves[line]
        return point in (c["start"], c["end"], c["center"]) or self.points.get(point) in (self.points.get(c["start"]), self.points.get(c["end"]), self.points.get(c["center"]))

    def rules(self):
        later = []
        for r in self.data["rules"]:
            kind, items = r["kind"], r["items"] or []
            if any(i not in self.curves for i in items):
                self.left.append({"sketch": self.data["name"], "item": kind, "why": "refers to something not in the model"})
                continue
            if r["param"]:
                later.append(r)
            else:
                self.rule(kind, items)
        for c in self.data["curves"]:
            # Projected from elsewhere: stays where Inventor last had it.
            if c["ref"] and c["kind"] in ("line", "circle", "arc") and "c%d" % c["id"] in [x["params"].get("id") or (x["params"].get("ids") or [None])[0] for x in self.out]:
                self.cmd("sketch.constraint.fix", entity="c%d" % c["id"])
            elif c["ref"] and c["kind"] == "point" and c["id"] in self.points and self.points[c["id"]].startswith("p") and abs(c["x"]) + abs(c["y"]) > 1e-9:
                self.cmd("sketch.constraint.fix", entity=self.points[c["id"]])
        for r in later:
            self.dimension(r["kind"], r["items"], r["param"])

    def rule(self, kind, items):
        ks = [self.curves[i]["kind"] for i in items]
        rs = [self.ref(i) for i in items]
        if kind == "PolygonCenter2D":
            return
        if None in rs:
            self.left.append({"sketch": self.data["name"], "item": kind, "why": "refers to something not drawn"})
        elif kind in ("Coincident2D", "Concentric2D"):
            a, b = items[0], items[1]
            if ks[0] == "point" and ks[1] == "point":
                if rs[0] != rs[1]:
                    self.cmd("sketch.constraint.coincident", a=rs[0], b=rs[1])
            elif ks[0] != "point" and ks[1] == "point":
                if not self.is_end(a, b):
                    self.cmd("sketch.constraint.coincident", a=rs[1] if kind == "Coincident2D" else self.center_ref(a), b=rs[0] if kind == "Coincident2D" else rs[1])
            elif ks[1] != "point" and ks[0] == "point":
                if not self.is_end(b, a):
                    self.cmd("sketch.constraint.coincident", a=rs[0] if kind == "Coincident2D" else self.center_ref(b), b=rs[1] if kind == "Coincident2D" else rs[0])
            else:
                self.cmd("sketch.constraint.concentric", a=rs[0], b=rs[1])
        elif kind in ("Parallel2D", "Perpendicular2D", "Tangent2D", "Collinear2D"):
            self.cmd("sketch.constraint." + kind[:-2].lower(), a=rs[0], b=rs[1])
        elif kind in ("EqualLength2D", "EqualRadius2D"):
            self.cmd("sketch.constraint.equal", a=rs[0], b=rs[1])
        elif kind in ("Horizontal2D", "Vertical2D", "AlignHorizontal2D", "AlignVertical2D"):
            mode = "horizontal" if "Horizontal" in kind else "vertical"
            if len(items) == 1:
                self.cmd("sketch.constraint.horizontal_vertical", line=rs[0], mode=mode)
            else:
                self.cmd("sketch.constraint.horizontal_vertical", points=[self.center_ref(items[0]), self.center_ref(items[1])], mode=mode)
        elif kind == "SymmetryPoint2D":
            line, point = (items[0], items[1]) if ks[0] != "point" else (items[1], items[0])
            if self.curves[line]["kind"] == "line":
                self.cmd("sketch.constraint.midpoint", point=self.ref(point), line=self.ref(line))
            else:
                self.left.append({"sketch": self.data["name"], "item": kind, "why": "the middle of an arc: no matching rule"})
        elif kind == "SymmetryLine2D":
            self.symmetric(items, rs)
        else:
            self.left.append({"sketch": self.data["name"], "item": kind, "why": "no matching rule written"})

    def dimension(self, kind, items, param):
        ks = [self.curves[i]["kind"] for i in items]
        rs = [self.ref(i) for i in items]
        if None in rs:
            self.left.append({"sketch": self.data["name"], "item": kind + " " + param, "why": "refers to something not drawn"})
        elif kind == "DimDiameter2D":
            self.cmd("sketch.dimension", entities=rs, type="diameter", value=param)
        elif kind == "DimRadius2D":
            self.cmd("sketch.dimension", entities=rs, type="radius", value=param)
        elif kind == "DimAngle2Line2D":
            self.cmd("sketch.dimension", entities=rs, type="angle", value=param)
        elif kind in ("DimDistance2D", "DimDistanceH2D", "DimDistanceV2D"):
            how = {"DimDistance2D": "distance", "DimDistanceH2D": "horizontal", "DimDistanceV2D": "vertical"}[kind]
            if len(items) == 1:
                c = self.curves[items[0]]
                items = [c["start"], c["end"]]
                ks = ["point", "point"]
                rs = [self.ref(i) for i in items]
            # Which reading of the dimension is the one on the drawing: Inventor's value can be twice the distance (a
            # diameter dimensioned from a centre line), and between circles it can be the gap rather than the centres.
            ways = []
            if all(k in ("circle", "arc") for k in ks):
                ways.append((rs, "auto", self.gap(items)))
            if ks[0] == "line" and ks[1] == "line":
                ways.append(([rs[0] + ".start", rs[1]], how, self.measure(kind, self.curves[items[0]]["start"], items[1])))
            else:
                ways.append(([self.center_ref(i) for i in items], how, self.measure(kind, items[0], items[1])))
            value = self.values.get(param)
            for ents, kind_of, drawn in ways:
                if value is None or abs(drawn - value) < 1e-3:
                    self.cmd("sketch.dimension", entities=ents, type=kind_of, value=param)
                    return
                if abs(2 * drawn - value) < 1e-3:
                    self.cmd("sketch.dimension", entities=ents, type=kind_of, value=param + " / 2")
                    return
            self.left.append({"sketch": self.data["name"], "item": kind + " " + param, "why": "the value %s isn't what the drawing shows (%.4f)" % (value, ways[0][2])})
        else:
            self.left.append({"sketch": self.data["name"], "item": kind + " " + param, "why": "no matching dimension written"})

    def script(self, wires=None):
        """The sketch's commands; with wires, the profile is the trimmed outline drawn from them (see outline), and
        self.drawn_outline lists its curves."""
        self.cmd("sketch.create", plane=self.plane(), name=self.name)
        self.geometry()
        self.rules()
        self.drawn_outline = self.outline(wires) if wires else None
        self.cmd("sketch.finish")
        return self.out


def samples(c, curves):
    if c["kind"] == "line":
        return [curves[c["start"]], curves[c["end"]]]
    if c["kind"] in ("circle", "arc"):
        return [{"x": curves[c["center"]]["x"] + c["r"] * math.cos(a), "y": curves[c["center"]]["y"] + c["r"] * math.sin(a)} for a in (0, 1.57, 3.14, 4.71)]
    return [curves[c[x]] for x in ("start", "end") if c[x] in curves] or [{"x": 0, "y": 0}]


def box(loop, curves):
    pts = [p for i in loop for p in samples(curves[i], curves)]
    return min(p["x"] for p in pts), min(p["y"] for p in pts), max(p["x"] for p in pts), max(p["y"] for p in pts)


def outer_loops(loops, curves):
    # The model lists a hole as a loop of its own; SolidCraft takes the outer loop and finds the islands inside itself.
    keep = []
    for a in loops:
        ba = box(a, curves)
        inside = 0
        for b in loops:
            if a is b or set(a) & set(b):
                continue
            bb = box(b, curves)
            if bb[0] < ba[0] and bb[1] < ba[1] and bb[2] > ba[2] and bb[3] > ba[3]:
                inside += 1
        if inside % 2 == 0:
            keep.append(a)
    return keep


class Script:
    # hollow: (STEP file, [body names in it]) of the void inside Inventor's solid, for a closed shell; measure(point,
    # direction): how far from the point Inventor's solid's next face lies that way, or None (see build_part.py).
    def __init__(self, model, hollow=None, measure=None, solid=None, made=None, base=None):
        self.m, self.left, self.out = model, [], []
        self.base = base
        self.hollow, self.measure = hollow, measure
        # Inventor's solid (STEP), and the first imported body as it was imported (….made.step), where there are.
        self.solid, self.made = solid, made
        self.sketches = {s["id"]: s for s in model["sketches"]}
        self.bodies = []
        self.names = {}
        # Each parameter's value as Inventor last evaluated it (lengths stored in cm, angles in radians), so that formulas
        # count as well as plain numbers.
        self.values = {}
        for p in model["parameters"]:
            if p.get("value") is None:
                continue
            if p["unit"] in LENGTH_UNITS:
                self.values[p["name"]] = p["value"] * 10
            elif p["unit"] in ANGLE_UNITS:
                self.values[p["name"]] = math.degrees(p["value"])
            elif p["unit"] == "ul":
                self.values[p["name"]] = p["value"]

    def cmd(self, command, **params):
        self.out.append({"command": command, "params": params})

    def sketch(self, f):
        # A sketch of its own for each feature, as in the FreeCAD build: only the curves this feature's profile uses are
        # drawn solid, so the sketch's other curves can't cut the profile into regions. Where Inventor trimmed them, the
        # outline the file keeps is drawn instead, and every drawn profile is the feature's (self.outlined).
        data = self.sketches[f["sketch"]]
        name = "%s (%s)" % (data["name"], f["name"])
        keep = {i for l in f.get("loops") or [] for i in l}
        centers = set(f["centers"] or []) if f["type"] == "hole" else set()
        trimmed = f["type"] != "hole" and (f.get("outline") or self.trimmed(f, data))
        sk = Sketch(data, set() if trimmed else keep, centers, self.left, self.values, name)
        self.out += sk.script(f["wires"] if trimmed else None)
        self.curve_names = sk.names
        self.outlined = trimmed
        return name

    def trimmed(self, f, data):
        """Whether a profile's own curves don't bound it exactly: Inventor trimmed them where they cross, or it uses only
        part of a full circle. Only outlines of lines and circles can be drawn."""
        loops, wires = f.get("loops"), f.get("wires")
        if not loops or not wires or any(e["kind"] not in ("line", "circle") for w in wires for e in w):
            return False
        if len(loops) != len(wires) or any(len(l) != len(w) for l, w in zip(loops, wires)):
            return True
        curves = {c["id"]: c for c in data["curves"]}
        sk = Sketch(data, set(), set(), [])
        for loop, wire in zip(loops, wires):
            kinds = {curves[i]["kind"] for i in loop if i in curves}
            if "circle" in kinds and len(kinds) > 1:
                return True
            ends = [sorted([(round(curves[curves[i]["start"]]["x"], 4), round(curves[curves[i]["start"]]["y"], 4)), (round(curves[curves[i]["end"]]["x"], 4), round(curves[curves[i]["end"]]["y"], 4))])
                    for i in loop if i in curves and curves[i]["kind"] == "line" and curves[i]["start"] in curves and curves[i]["end"] in curves]
            for e in wire:
                if e["kind"] == "line":
                    a, b = sk.local(e["a"]), sk.local(e["b"])
                    if sorted([(round(a[0], 4), round(a[1], 4)), (round(b[0], 4), round(b[1], 4))]) not in ends:
                        return True
        return False

    def normal(self, sid):
        m = self.sketches[sid]["matrix"]
        return [m[2], m[6], m[10]]

    def params(self):
        # SolidCraft wants a parameter's references added before it; Inventor's table has no such order.
        rest = list(self.m["parameters"])
        names = {p["name"] for p in rest}
        done = set()
        while rest:
            ready = [p for p in rest if not (set(re.findall(r"[A-Za-z_]\w*", p["formula"])) & names) - done - {p["name"]}]
            for p in ready or rest:
                expression = re.sub(r"\s*\bul\b", "", p["formula"])
                if p["unit"] not in LENGTH_UNITS | ANGLE_UNITS | {"ul"} and p.get("shown"):
                    # SolidCraft knows no forces or masses (a stress analysis's load): kept as its plain number.
                    expression = p["shown"].split()[0]
                elif p["name"] in self.values:
                    # A formula can be stale (Inventor kept the value it last had, not what the formula now gives) or
                    # shown rounded: then the value, as FreeCAD's build does. One that can't be worked out here is kept.
                    got = evaluate(p["formula"], self.values)
                    want = self.values[p["name"]]
                    if got is not None and abs(got - want) > 1e-6 * max(1, abs(want)):
                        expression = "%r%s" % (want, " mm" if p["unit"] in LENGTH_UNITS else " deg" if p["unit"] in ANGLE_UNITS else "")
                        self.left.append({"parameter": p["name"], "why": "its formula %s gives %.6g, Inventor has %.6g: the value is used" % (p["formula"], got, want)})
                self.cmd("parameters.add", name=p["name"], expression=expression)
                done.add(p["name"])
            rest = [p for p in rest if p["name"] not in done]

    def body_name(self):
        self.bodies.append(BODY if not self.bodies else "%s %d" % (BODY, len(self.bodies) + 1))
        return self.bodies[-1]

    def operation(self, f):
        return {"new": "new", "join": "join", "cut": "cut", "intersect": "intersect"}.get(f["op"])

    def profile(self, f):
        if f.get("picks"):
            sk = Sketch(self.sketches[f["sketch"]], set(), set(), [])
            return [{"point": sk.local(p)} for p in f["picks"]]
        if self.outlined:
            # Only the outline is solid: SolidCraft's default, every drawn profile with loops inside as holes.
            return None
        curves = {c["id"]: c for c in self.sketches[f["sketch"]]["curves"]}
        return [{"curves": [self.curve_names.get(i, "c%d" % i) for i in l]} for l in outer_loops(f["loops"], curves)]

    def extrude(self, f):
        op = self.operation(f)
        if op is None:
            return "extrude kind %s not carried" % f["op"]
        # SolidCraft's sheet metal only where flanges follow: a flat plate with holes and rounds after it (two real
        # parts) went wrong as a sheet body (78% and 14% off) and is right as an extrude.
        if f.get("sheet") and op == "new" and f["extent"] == "distance" and any(x.get("type") == "flange" for x in self.m["features"]):
            return self.plate(f)
        f = socket(f) or f
        name = self.sketch(f)
        p = {"sketch": name, "profiles": self.profile(f), "operation": op, "name": f["name"]}
        if p["profiles"] is None:
            del p["profiles"]
        n = self.normal(f["sketch"])
        axis = f.get("axis") or n
        along = [-x for x in axis] if f["dir"] == "negative" else axis
        if f["dir"] == "symmetric":
            p["direction"] = "symmetric"
        elif sum(a * b for a, b in zip(along, n)) < 0:
            p["direction"] = "negative"
        if f["extent"] == "distance":
            p["distance"] = f["param"]
        elif f["extent"] == "next" and self.measure:
            # SolidCraft has no "to the next face": the depth from the profile's middle to Inventor's solid along the way.
            d = self.measure(profile_middle(f), along)
            if d is None:
                return "couldn't find the next face to extrude up to"
            p["distance"] = "%.6f mm" % d
            self.approximate(f, "'to next' became a fixed distance of %.4f mm, measured on Inventor's solid" % d)
        elif f["extent"] == "through":
            p["through_all"] = True
        elif f["extent"] == "to" and f.get("toPlane"):
            p["to"] = f["toPlane"]["origin"]
        elif f["extent"] == "to" and f.get("toCylinder"):
            # SolidCraft finds the face by a point on it: where the profile's middle meets the cylinder on its way.
            hit = ray_cylinder(profile_middle(f), along, f["toCylinder"])
            deepest = depth_to_cylinder(f, along, f["toCylinder"])
            if hit is None and deepest is None:
                return "the extrude doesn't meet the cylinder it runs up to"
            # SolidCraft can refuse the face (it fails where the face touches the profile's edges); then the depth that
            # takes every point of the outline to the cylinder, which the check against Inventor's solid then judges.
            fixed = dict(p, distance="%.6f mm" % deepest) if deepest is not None else None
            why = "the 'to' face became a fixed distance of %.4f mm" % (deepest or 0)
            if hit is None:
                p = fixed
                self.approximate(f, why)
            else:
                p["to"] = hit
                if fixed:
                    p[ALTERNATIVES] = [(fixed, why)]
        else:
            return "extent %s not carried" % f["extent"]
        if f.get("extent2") == "distance" and f.get("param2"):
            p["distance2"] = f["param2"]
        elif f.get("extent2"):
            self.approximate(f, "the second side, '%s', isn't carried" % f["extent2"])
        if f.get("taperParam"):
            p["taper"] = f["taperParam"]
        if op == "new":
            p["body_name"] = self.body_name()
            for params, _ in p.get(ALTERNATIVES, []):
                params["body_name"] = p["body_name"]
        self.cmd("solid.extrude", **p)

    def revolve(self, f):
        op = self.operation(f)
        if op is None:
            return "revolve kind %s not carried" % f["op"]
        name = self.sketch(f)
        a, b = f["axis"]
        p = {"sketch": name, "profiles": self.profile(f), "operation": op, "name": f["name"], "axis": {"origin": a, "dir": [y - x for x, y in zip(a, b)]}}
        if p["profiles"] is None:
            del p["profiles"]
        if f["extent"] != "full":
            p["angle"] = f["param"]
        if op == "new":
            p["body_name"] = self.body_name()
        self.cmd("solid.revolve", **p)

    def hole(self, f):
        # A hole's axis points out of the material: positive drills against it.
        along = f.get("axis")
        if along and f["dir"] != "negative":
            along = [-x for x in along]
        if f.get("centers"):
            name = self.sketch(f)
            # SolidCraft drills into the material on whichever side of the sketch it lies.
            p = {"sketch": name, "points": [self.ref_point(f["sketch"], i) for i in f["centers"]], "diameter": f["diameter"], "name": f["name"]}
        elif f.get("position") and along:
            p = {"position": f["position"], "direction": along, "diameter": f["diameter"], "name": f["name"]}
            self.approximate(f, "placed by a fixed position, not by the edges Inventor measured from")
        else:
            return "no position decoded"
        if f["extent"] != "through":
            # Inventor measures a spot-faced hole's depth from the bottom of the spot face, SolidCraft from the surface.
            p["depth"] = "(%s) + (%s)" % (f["depth"], f["headDepth"]) if f["kind"] == "kSpotFaceHole" else f["depth"]
            if not f.get("flat") and f.get("tip"):
                p["tip_angle"] = f["tip"]
                p["type"] = "drilled"
        if f["kind"] in ("kCounterBoreHole", "kSpotFaceHole"):
            p.update(type="counterbore", cb_diameter=f["headDiameter"], cb_depth=f["headDepth"])
        elif f["kind"] == "kCounterSinkHole":
            p.update(type="countersink", cs_diameter=f["headDiameter"], cs_angle=f["headAngle"])
        if p.get("tip_angle"):
            # SolidCraft can fail on the drill point's cone where it meets a hollow; a flat bottom is the fallback.
            flat = {k: v for k, v in p.items() if k != "tip_angle"}
            if flat.get("type") == "drilled":
                flat["type"] = "simple"
            p[ALTERNATIVES] = [(flat, "drilled with a flat bottom, not Inventor's drill point")]
        self.cmd("solid.hole", **p)

    def ref_point(self, sid, i):
        # The same rule the sketch used: a shared point is named after the first curve that has it.
        sk = Sketch(self.sketches[sid], set(), set(), [])
        sk.geometry()
        return sk.points.get(i, "p%d" % i)

    def on_edges(self, command, refs, **p):
        """A fillet or chamfer. SolidCraft picks edges by a point on them; the file describes each edge (a line, a circle, the
        ends of a curve, where two surfaces meet), and only some give a point by themselves. So SolidCraft's own edges are
        listed just before it and matched to them: see build()."""
        p["edges"] = [x for x in (edge_point(e[0]) for e in refs) if x]
        p[REFS] = refs
        self.cmd(command, **p)

    def chamfer(self, f):
        if not f["edges"]:
            return "no edges decoded"
        p = {"distance": f["param"], "name": f["name"]}
        if f["kind"] == "kTwoDistances":
            p["distance2"] = f["param2"]
        elif f["kind"] == "kDistanceAndAngle":
            p["angle"] = f["angleParam"]
        self.on_edges("solid.chamfer", f["edges"], **p)

    def fillet(self, f):
        for s in f["sets"]:
            if s["edges"]:
                self.on_edges("solid.fillet", s["edges"], radius=s["param"], name=f["name"])

    def mirror(self, f):
        what = self.originals(f)
        if not what:
            return "nothing to mirror"
        self.cmd("solid.mirror", plane={"origin": f["origin"], "normal": f["normal"]}, name=f["name"], **what)

    def originals(self, f):
        if f["ofBody"]:
            return {"bodies": list(self.bodies)} if self.bodies else None
        names = [self.names[i] for i in f["parents"] if i in self.names]
        return {"features": names} if names else None

    def circular(self, f):
        what = self.originals(f)
        if not what:
            return "nothing to copy"
        p = {"axis": {"origin": f["axisPoint"], "dir": [-x for x in f["axisDir"]] if f["reversed"] else f["axisDir"]}, "count": f["n"][0], "name": f["name"]}
        if f["incremental"]:
            p["angle"] = "(%s) * %d" % (f["angleParam"], f["n"][0] - 1)
        elif abs(f["angle"] - 360) > 1e-6:
            p["angle"] = f["angleParam"]
        self.cmd("solid.pattern.circular", **p, **what)

    def rectangular(self, f):
        what = self.originals(f)
        if not what:
            return "nothing to copy"

        def spacing(k):
            s = f["spacingParam"][k] or str(f["step"][k]) + " mm"
            return "(%s) / %d" % (s, f["n"][k] - 1) if f["fitted"][k] else s
        p = {"dir1": f["dir1"], "count1": f["n"][0], "spacing1": spacing(0), "name": f["name"]}
        if f["hasY"]:
            p.update(dir2=f["dir2"], count2=f["n"][1], spacing2=spacing(1))
        self.cmd("solid.pattern.rectangular", **p, **what)

    def shell(self, f):
        if f["faces"]:
            # Open: SolidCraft's shell takes the faces away and walls the rest, inward, outward or both ways.
            direction = {"kInsideShellDirection": "inside", "kOutsideShellDirection": "outside", "kBothSidesShellDirection": "both"}.get(f["direction"])
            if direction is None:
                return "shell direction %s not carried" % f["direction"]
            p = {"faces": [], "thickness": f["param"] or "%.6f mm" % f["thickness"], "direction": direction, FACE_REFS: f["faces"]}
            if self.bodies:
                p["body"] = self.bodies[-1]
            self.cmd("solid.shell", **p)
            if f["sets"]:
                # A flat face with a thickness of its own: its wall's inside face moved into the hollow (or out of it) by
                # the difference, with SolidCraft's Offset Face. Inward shells only, where that face lies the common
                # thickness in from it.
                common = f["thickness"]
                carried = direction == "inside" and len(f.get("faceSets") or []) == f["sets"]
                for fs in f.get("faceSets") or []:
                    if not carried or fs.get("thickness") is None:
                        break
                    for alternatives in fs["faces"]:
                        planes = [r for r in alternatives if r.get("kind") == "plane" and r.get("a") and r.get("b")]
                        if not planes:
                            carried = False
                            break
                        n = unit(planes[0]["b"])
                        inner = dict(planes[0], a=[x - y * common for x, y in zip(planes[0]["a"], n)])
                        q = {"faces": [], "distance": "%.6f mm" % (fs["thickness"] - common), FACE_REFS: [[inner]]}
                        if self.bodies:
                            q["body"] = self.bodies[-1]
                        self.cmd("solid.offset_face", **q)
                if not carried:
                    self.approximate(f, "faces Inventor gives a thickness of their own have the common one")
                else:
                    self.approximate(f, "the walls of faces with a thickness of their own are moved by fixed amounts")
            return None
        # SolidCraft's shell needs a face taken away. A closed inward one is a void inside, which Inventor's solid holds as
        # its inner skin: brought in and cut out, at its fixed size.
        if f["sets"] or f["direction"] != "kInsideShellDirection":
            return "only inward shells with no face removed are carried"
        if not self.hollow or not self.bodies:
            return "SolidCraft's shell needs a face removed, and Inventor's solid has no single hollow to use instead"
        path, names = self.hollow
        self.cmd("file.insert_step", path=path, name=f["name"] + " inside", names="product")
        self.cmd("solid.combine", target=self.bodies[-1], tools=names, operation="cut", name=f["name"])
        self.approximate(f, "SolidCraft's shell needs a face removed: the hollow is Inventor's, at a fixed size")

    def loft(self, f):
        op = self.operation(f)
        if op is None:
            return "loft kind %s not carried" % f["op"]
        sections = f.get("sections") or []
        if len(sections) < 2 or any(x is None or (x.get("loops") is None and x.get("point") is None) for x in sections):
            return "loft not carried: a section wasn't decoded"
        free = ("kFreeLoftCondition", "kSharpPointLoftCondition", None)
        conditioned = any((f.get(e) or {}).get("condition") not in free for e in ("first", "last"))
        # SolidCraft refuses lofting curved sections to a point ("not supported yet"): that the same way too.
        curved_to_point = any(x.get("point") is not None for x in sections) and any(e["kind"] != "line" for x in sections for w in x.get("wires") or [] for e in w)
        if f.get("closed") or conditioned or curved_to_point:
            # SolidCraft's loft has no end conditions and doesn't close round a ring: Inventor's own sides between the
            # sections, as the FreeCAD build makes them, brought in and joined (or cut).
            return self.loft_from_inventor(f, op)
        made = []
        for i, sec in enumerate(sections):
            data = self.sketches[sec["sketch"]]
            if sec.get("point") is not None:
                name = "%s (%s section %d)" % (data["name"], f["name"], i + 1)
                sk = Sketch(data, set(), {sec["point"]}, self.left, self.values, name)
                self.out += sk.script(None)
                if sec["point"] not in sk.points:
                    return "loft not carried: its point section wasn't drawn"
                made.append({"sketch": name, "point": sk.points[sec["point"]]})
                continue
            g = {"sketch": sec["sketch"], "loops": sec["loops"], "wires": sec.get("wires"), "name": "%s section %d" % (f["name"], i + 1), "type": "loft"}
            name = self.sketch(g)
            entry = {"sketch": name}
            profiles = self.profile(g)
            if profiles is not None:
                entry["profiles"] = profiles
            made.append(entry)
        p = {"sections": made, "operation": op, "name": f["name"]}
        rails = []
        for i, r in enumerate(f.get("rails") or []):
            g = {"sketch": r["sketch"], "loops": r["loops"], "wires": None, "name": "%s rail %d" % (f["name"], i + 1), "type": "loft"}
            name = self.sketch(g)
            rails.append({"sketch": name, "curves": [self.curve_names.get(c, "c%d" % c) for loop in r["loops"] for c in loop]})
        if rails:
            p["rails"] = rails
        if f.get("loftType") == "kLoftWithCenterline":
            self.approximate(f, "SolidCraft's loft has no centerline: lofted through the sections without it")
        if op == "new":
            p["body_name"] = self.body_name()
        self.cmd("solid.loft", **p)
        return None

    def sweep(self, f):
        op = self.operation(f)
        if op is None:
            return "sweep kind %s not carried" % f["op"]
        if f.get("loops") is None or not f.get("path"):
            return "sweep not carried: its profile or path wasn't decoded"
        taper = parse_angle(f.get("taper"))
        twist = parse_angle(f.get("twist"))
        if taper and (twist or f.get("rail")):
            return "sweep not carried: a taper with a twist or rail"
        if taper:
            # SolidCraft's sweep has a scale, not Inventor's taper (an offset of the outline); along one straight line
            # square to the profile a taper is an extrude's taper, as the FreeCAD build makes it.
            line = self.straight_path(f)
            if line is None:
                return "sweep not carried: a taper along a path that isn't one straight line square to the profile"
            name = self.sketch(f)
            p = {"sketch": name, "operation": op, "name": f["name"], "distance": "%.6f mm" % line[0], "taper": f.get("taperParam") or f["taper"]}
            profiles = self.profile(f)
            if profiles is not None:
                p["profiles"] = profiles
            if line[1] < 0:
                p["direction"] = "negative"
            if op == "new":
                p["body_name"] = self.body_name()
            self.cmd("solid.extrude", **p)
            self.approximate(f, "a sweep along a straight line, made as an extrude with the same taper")
            return None
        name = self.sketch(f)
        p = {"sketch": name, "operation": op, "name": f["name"]}
        profiles = self.profile(f)
        if profiles is not None:
            p["profiles"] = profiles
        path = {"sketch": f["path"]["sketch"], "loops": [f["path"]["curves"]], "wires": None, "name": f["name"] + " path", "type": "sweep"}
        p["path_sketch"] = self.sketch(path)
        p["path"] = [self.curve_names.get(c, "c%d" % c) for c in f["path"]["curves"]]
        if f.get("rail"):
            rail = {"sketch": f["rail"]["sketch"], "loops": [f["rail"]["curves"]], "wires": None, "name": f["name"] + " rail", "type": "sweep"}
            rail_sketch = self.sketch(rail)
            p["rails"] = [{"sketch": rail_sketch, "curves": [self.curve_names.get(c, "c%d" % c) for c in f["rail"]["curves"]]}]
            if f.get("scaling") == "kXProfileScaling":
                p["rail_fit"] = "stretch"
            elif f.get("scaling") == "kNoProfileScaling":
                self.approximate(f, "the guide rail's 'no scaling' isn't carried: SolidCraft's rail scales the profile")
        elif f.get("orientation") == "kParallelToOriginalProfile":
            p["orientation"] = "parallel"
        if twist:
            p["twist"] = f.get("twistParam") or f["twist"]
        if op == "new":
            p["body_name"] = self.body_name()
        self.cmd("solid.sweep", **p)
        return None

    def straight_path(self, f):
        # (length, +1 or -1 along the profile's normal) when the path is one line from the profile's plane square to it.
        data = self.sketches[f["path"]["sketch"]]
        curves = {c["id"]: c for c in data["curves"]}
        ids = f["path"]["curves"]
        if len(ids) != 1 or curves.get(ids[0], {}).get("kind") != "line":
            return None
        c = curves[ids[0]]
        pts = {x["id"]: x for x in data["curves"] if x["kind"] == "point"}
        m = data["matrix"]
        def world(pid):
            q = pts[pid]
            return [m[0] * q["x"] + m[1] * q["y"] + m[3], m[4] * q["x"] + m[5] * q["y"] + m[7], m[8] * q["x"] + m[9] * q["y"] + m[11]]
        try:
            a, b = world(c["start"]), world(c["end"])
        except KeyError:
            return None
        n = self.normal(f["sketch"])
        pm = self.sketches[f["sketch"]]["matrix"]
        origin = [pm[3], pm[7], pm[11]]
        if abs(dot(sub(a, origin), n)) > 1e-6:
            a, b = b, a
        d = sub(b, a)
        length = math.sqrt(dot(d, d))
        if length < 1e-9 or abs(dot(sub(a, origin), n)) > 1e-6 or abs(abs(dot(d, n)) / length - 1) > 1e-9:
            return None
        return length, 1 if dot(d, n) > 0 else -1

    def loft_from_inventor(self, f, op):
        if not self.solid or not self.base:
            return "loft not carried: its end conditions or closing need Inventor's solid"
        solid = inventor_loft(self.solid, f)
        if solid is None:
            return "loft not carried: Inventor's sides for it weren't found"
        import FreeCAD, Import
        doc = FreeCAD.newDocument("loft")
        o = doc.addObject("Part::Feature", "Loft")
        o.Label = "%s shape" % f["name"]
        o.Shape = solid
        path = "%s.%s.step" % (self.base, re.sub(r"[^\w]+", "_", f["name"]))
        Import.export([o], path)
        label = o.Label
        FreeCAD.closeDocument(doc.Name)
        self.cmd("file.insert_step", path=path, name=label, names="product")
        if op == "new":
            self.bodies.append(label)
        else:
            if not self.bodies:
                return "loft not carried: nothing to %s" % op
            self.cmd("solid.combine", target=self.bodies[-1], tools=[label], operation={"join": "join", "cut": "cut", "intersect": "intersect"}[op], name=f["name"])
        self.approximate(f, "its sides are Inventor's own surfaces between the sections, fixed: SolidCraft's loft has no end conditions or closing")
        return None

    def plate(self, f):
        # Sheet metal: SolidCraft's own, so its flanges, bends and corners follow its rule as Inventor's follow the part's
        # style. The rule takes the plate's thickness, the part's bend radius and the gap where flanges meet at a corner
        # (fixed numbers; on the generated sheet parts SolidCraft's flanges at 90 and 60 degrees, flipped, and two
        # meeting at a corner gave Inventor's volumes to 0.0002 mm3 with its defaults).
        thickness = abs(f.get("length") or 0)
        radius = self.values.get("BendRadius", thickness)
        rule = {"name": "Inventor", "thickness": "%.6f mm" % thickness, "bend_radius": "%.6f mm" % radius, "active": True}
        if self.values.get("GapSize") is not None:
            rule["gap"] = "%.6f mm" % self.values["GapSize"]
        self.cmd("sheet.manage_rules", **rule)
        name = self.sketch(f)
        p = {"type": "base", "sketch": name, "name": f["name"], "body_name": self.body_name()}
        profiles = self.profile(f)
        if profiles is not None:
            p["profiles"] = profiles
        n = self.normal(f["sketch"])
        axis = f.get("axis") or n
        along = [-x for x in axis] if f["dir"] == "negative" else axis
        if sum(a * b for a, b in zip(along, n)) < 0:
            p["flip"] = True
        self.cmd("sheet.flange", **p)
        self.sheet = True
        self.approximate(f, "sheet metal as SolidCraft's: thickness and bend radius are its rule's, fixed numbers")
        return None

    def flange(self, f):
        if not getattr(self, "sheet", False):
            return "flange not carried: no sheet metal plate before it"
        e = next((c for c in (f.get("edges") or [[]])[0] if c.get("kind") == "line"), None)
        if e is None:
            return "flange not carried: only straight edges"
        mid = [(a + b) / 2 for a, b in zip(e["a"], e["b"])]
        p = {"edges": [mid], "height": "%.6f mm" % f["height"], "angle": "%.6f deg" % f["angle"], "name": f["name"]}
        if f["dir"] == "negative":
            p["flip"] = True
        self.cmd("sheet.flange", **p)
        return None

    def sheet_metal(self, f):
        kind = f.get("kind")
        if kind in ("bend", "corner") and not f.get("edges"):
            return None if getattr(self, "sheet", False) else "sheet metal not carried"
        if kind == "corner feature":
            if f.get("cornerType") != "kCornerReverseOverlap":
                self.approximate(f, "corner %s isn't carried: SolidCraft closes flanges that meet at a corner its own way" % (f.get("cornerType") or "?"))
            return None
        return "sheet metal %s not carried" % (kind or "feature")

    def imported(self, f):
        # An imported body has no history: its shape comes in as SolidCraft's Import base feature. A part of imported
        # bodies only, with nothing after them, is Inventor's solid now (as the FreeCAD build takes it); otherwise the
        # first body as imported, which the file keeps, with the features after it built on it.
        imports = [x for x in self.m["features"] if x.get("type") in ("imported body", "imported surface")]
        only = all(x.get("type") in ("imported body", "imported surface") for x in self.m["features"] if x.get("computed", True))
        if only and self.solid:
            if f is not imports[0]:
                return None     # brought in with the first
            self.cmd("file.insert_step", path=self.solid, name=f["name"] or "Imported", names="solid")
            return None
        if f.get("made") and self.made and not self.out_has_solid():
            self.cmd("file.insert_step", path=self.made, name=f["name"] or "Imported", names="solid")
            self.approximate(f, "brought in as imported; the features after it may not find it as their body")
            return None
        return "imported body not carried: the file keeps no shape of it as imported"

    def out_has_solid(self):
        return any(c["command"].startswith("solid.") or c["command"].startswith("sheet.") for c in self.out)

    def approximate(self, f, why):
        # Carried, but not as Inventor has it: noted, not left out.
        self.left.append({"feature": f["name"], "why": why, "approximate": True})

    def build(self):
        self.params()
        makers = {"extrude": self.extrude, "revolve": self.revolve, "hole": self.hole, "chamfer": self.chamfer, "fillet": self.fillet, "mirror": self.mirror, "circular": self.circular, "rectangular": self.rectangular, "shell": self.shell,
                  "imported body": self.imported, "imported surface": self.imported, "flange": self.flange, "sheet metal": self.sheet_metal,
                  "loft": self.loft, "sweep": self.sweep}
        for f in self.m["features"]:
            # Past Inventor's End of Part: not in its solid, and SolidCraft has no marker to keep them behind.
            if not f["computed"] or f["type"] == "thread" or f.get("rolledBack"):
                continue
            self.names[f["id"]] = f["name"]
            if f["type"] not in makers:
                self.left.append({"feature": f["name"], "why": "%s not carried" % f["type"]})
                continue
            why = makers[f["type"]](f)
            if why:
                self.left.append({"feature": f["name"], "why": why})
        return self.out


def sub(a, b):
    return [x - y for x, y in zip(a, b)]


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def unit(a):
    n = math.sqrt(dot(a, a))
    return [x / n for x in a] if n > 1e-12 else None


def same_surface(want, face, tol=1e-3):
    """A surface the file describes ({kind, a, b, r}) against a face SolidCraft lists (body_topology)."""
    n = unit(want["b"]) if want.get("b") else None
    if want["kind"] == "plane" and face.get("plane_normal") and n:
        return abs(abs(dot(unit(face["plane_normal"]), n)) - 1) < 1e-6 and abs(dot(sub(face["point"], want["a"]), n)) < tol
    if want["kind"] == "cylinder" and face.get("cylinder") and n:
        c = face["cylinder"]
        v = sub(c["origin"], want["a"])
        off = sub(v, [x * dot(v, n) for x in n])
        return abs(abs(dot(unit(c["axis"]), n)) - 1) < 1e-6 and math.sqrt(dot(off, off)) < tol and abs(c["radius"] - want["r"]) < tol
    return False


def edge_matches(ref, e, faces, tol=1e-3):
    """Whether an edge SolidCraft lists ({start, end, mid, name}) is (a piece of) the edge the file describes."""
    kind = ref.get("kind")
    pts = [e["start"], e["mid"], e["end"]]
    if kind == "line" and ref.get("a") and ref.get("b"):
        d = unit(sub(ref["b"], ref["a"]))
        if d is None:
            return False
        for p in pts:
            w = sub(p, ref["a"])
            off = sub(w, [x * dot(w, d) for x in d])
            if math.sqrt(dot(off, off)) > tol:
                return False
        if ref.get("unbounded"):
            return True
        # The same stretch of the line, or a piece of it: SolidCraft may split or merge what Inventor has as one edge.
        t = sorted(dot(sub(p, ref["a"]), d) for p in (e["start"], e["end"]))
        return t[1] > tol and t[0] < math.sqrt(dot(sub(ref["b"], ref["a"]), sub(ref["b"], ref["a"]))) - tol
    if kind == "circle" and ref.get("a") and ref.get("b"):
        # Every point on the circle; where a round's radius was eased, the arc beside it can be a spline on the same circle.
        n = unit(ref["b"])
        for p in pts:
            w = sub(p, ref["a"])
            off = sub(w, [x * dot(w, n) for x in n])
            if abs(dot(w, n)) > tol or abs(math.sqrt(dot(off, off)) - ref["r"]) > tol:
                return False
        return True
    if kind == "curve" and ref.get("a") and ref.get("b"):
        near = lambda p, q: math.sqrt(dot(sub(p, q), sub(p, q))) < tol
        return (near(e["start"], ref["a"]) and near(e["end"], ref["b"])) or (near(e["start"], ref["b"]) and near(e["end"], ref["a"]))
    if kind == "meet":
        # An edge's name is its two faces' names with "|" between (a face's own name can hold "|").
        name = e.get("name") or ""
        for k, ch in enumerate(name):
            if ch == "|" and name[:k] in faces and name[k + 1:] in faces:
                x, y = faces[name[:k]], faces[name[k + 1:]]
                return (same_surface(ref["s1"], x) and same_surface(ref["s2"], y)) or (same_surface(ref["s1"], y) and same_surface(ref["s2"], x))
    return False


def place_edges(refs, edges, faces):
    """Points on SolidCraft's edges for each of the file's edge inputs (each a list of alternatives), and how many found none."""
    points, missing = [], 0
    for alternatives in refs:
        found = []
        for tol in (1e-3, 0.15):
            for ref in alternatives:
                if tol > 1e-3 and ref.get("kind") != "curve":
                    # A spline where two surfaces meet can end a little off where Inventor has it.
                    continue
                # Not slivers a profile can leave, whose middle is a corner where several edges meet.
                found = [e["mid"] for e in edges if edge_matches(ref, e, faces, tol) and math.sqrt(dot(sub(e["end"], e["start"]), sub(e["end"], e["start"]))) + math.sqrt(dot(sub(e["mid"], e["start"]), sub(e["mid"], e["start"]))) > 1e-3]
                if found:
                    break
            if found:
                break
        missing += not found
        points += [p for p in found if p not in points]
    return points, missing


def parse_angle(text):
    # "45 deg" -> 45.0; nothing or zero -> 0.
    if not text:
        return 0.0
    m = re.match(r"\s*([-+0-9.eE]+)", str(text))
    return float(m.group(1)) if m else 0.0


def inventor_loft(step, f, near_mm=0.01):
    """A loft's solid from Inventor's own side surfaces (as tools/freecad/build_model.py's loft_surfaces): each
    non-analytic surface of Inventor's solid that runs through the first and last sections (round a ring, any two),
    closed with faces on its own edges at the end sections. Runs in FreeCAD's Python. None if they aren't found."""
    import FreeCAD, Part
    V = FreeCAD.Vector
    def points(sec):
        if sec.get("point") is not None or not sec.get("wires"):
            return []
        out = []
        for w in sec["wires"]:
            for e in w:
                if e["kind"] == "line" and e.get("a") and e.get("b"):
                    a, b = V(*e["a"]), V(*e["b"])
                    out += [a + (b - a) * (k / 6.0) for k in range(7)]
                elif e["kind"] == "circle" and e.get("a") and e.get("b"):
                    c = Part.Circle(V(*e["a"]), V(*e["b"]), e["r"])
                    out += [c.value(c.FirstParameter + (c.LastParameter - c.FirstParameter) * k / 12.0) for k in range(12)]
                elif e.get("from") and e.get("to"):
                    out += [V(*e["from"]), V(*e["to"])]
        return out
    def curves(sec):
        out = []
        for w in sec.get("wires") or []:
            for e in w:
                if e["kind"] == "line" and e.get("a") and e.get("b"):
                    out.append(Part.LineSegment(V(*e["a"]), V(*e["b"])).toShape())
                elif e["kind"] == "circle" and e.get("a") and e.get("b"):
                    out.append(Part.Circle(V(*e["a"]), V(*e["b"]), e["r"]).toShape())
        return out
    try:
        inv = Part.read(step)
        secs = f["sections"]
        pts = [points(x) for x in secs]
        first, last = pts[0], pts[-1]
        sides = []
        for face in inv.Faces:
            if isinstance(face.Surface, Part.Plane):
                continue
            # A cone or cylinder made whole would have no end: Inventor's face as it is (a loft to a point is a cone).
            whole = face.copy() if isinstance(face.Surface, (Part.Cylinder, Part.Cone, Part.Sphere, Part.Toroid)) else Part.Face(face.Surface)
            near = lambda ps: [q for q in ps if whole.distToShape(Part.Vertex(q))[0] < near_mm]
            if f.get("closed"):
                if sum(1 for ps in pts if ps and near(ps)) >= 2:
                    sides.append(whole)
            elif (not first or near(first)) and (not last or near(last)) and (first or last):
                sides.append(whole)
        if not sides:
            return None
        caps = []
        if not f.get("closed"):
            for ps in (first, last):
                if not ps:
                    continue
                section = Part.Compound([Part.Vertex(q) for q in ps] + curves(secs[0] if ps is first else secs[-1]))
                edges = [e for x in sides for e in x.Edges if e.Length > 1e-6
                         and all(section.distToShape(Part.Vertex(q))[0] < near_mm for q in e.discretize(6))]
                if not edges:
                    return None
                w = Part.Wire(Part.__sortEdges__(edges))
                if not w.isClosed():
                    return None
                caps.append(Part.Face(w))
        shell = Part.Shell(sides) if len(sides) == 1 and not caps else None
        if shell is None or not shell.isClosed():
            sewn = Part.Compound(sides + caps)
            sewn.sewShape(1e-4)
            if len(sewn.Shells) != 1 or not sewn.Shells[0].isClosed():
                return None
            shell = sewn.Shells[0]
        solid = Part.Solid(shell)
        if solid.Volume < 0:
            solid.reverse()
        return solid if solid.isValid() and solid.Volume > 1e-9 else None
    except Exception:
        return None


def sent(commands):
    """The script as SolidCraft gets it: without the keys that are only for this program."""
    return {"commands": [{"command": c["command"], "params": {k: v for k, v in c["params"].items() if not k.startswith("_")}} for c in commands]}


class Hung(Exception):
    pass


class Crashed(Hung):
    # SolidCraft's process ended in the middle of a command.
    pass


class Session:
    """solidcraft-cli's MCP server, headless: one design, built a command at a time. A command SolidCraft refuses leaves
    the design as it was, so nothing is run twice. It may read and write only in the folders given."""

    def __init__(self, folders):
        args = [CLI, "mcp", "--headless"]
        for f in folders:
            args += ["--allow-path", f]
        self.p = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf-8", errors="replace")
        # Answers are read on a thread of their own, so that a command that never returns can be given up on.
        self.lines = queue.Queue()
        threading.Thread(target=lambda: [self.lines.put(l) for l in self.p.stdout] + [self.lines.put("")], daemon=True).start()
        self.n = 0
        self.rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "CAD Convert", "version": "1"}}, 60)
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def send(self, msg):
        self.p.stdin.write(json.dumps(msg) + "\n")
        self.p.stdin.flush()

    def rpc(self, method, params, seconds):
        self.n += 1
        self.send({"jsonrpc": "2.0", "id": self.n, "method": method, "params": params})
        until = time.time() + seconds
        while True:
            try:
                line = self.lines.get(timeout=max(0.1, until - time.time()))
            except queue.Empty:
                raise Hung()
            if not line:
                raise Crashed()
            r = json.loads(line)
            if r.get("id") == self.n:
                return r

    def tool(self, name, seconds=COMMAND_SECONDS, **args):
        """(whether it worked, its answer: the result's text, or what SolidCraft said was wrong). Hung if it takes longer."""
        r = self.rpc("tools/call", {"name": name, "arguments": args}, seconds)
        if "error" in r:
            return False, str(r["error"].get("message"))
        res = r.get("result") or {}
        return not res.get("isError"), "".join(c.get("text", "") for c in res.get("content", []))

    def execute(self, c, seconds=COMMAND_SECONDS):
        return self.tool("execute", seconds, command=c["command"], params=sent([c])["commands"][0]["params"], verbose=False)

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(10)
        except Exception:
            self.p.kill()


def profile_commands(tool, radius, param, label):
    """SolidCraft commands for a round made as a sketch profile (rounds.round_profile or merged_profile): the corner
    outside the rolling ball, cut along the edge or added at a concave one, its arc's radius tied to the parameter."""
    import FreeCAD
    rot = tool["placement"].Rotation
    o, x, y = tool["placement"].Base, rot.multVec(FreeCAD.Vector(1, 0, 0)), rot.multVec(FreeCAD.Vector(0, 1, 0))
    name = label + " profile"
    (cx, cy), (a0, a1) = tool["center"], tool["arc"]
    at = lambda a: [cx + radius * math.cos(a), cy + radius * math.sin(a)]
    out = [{"command": "sketch.create", "params": {"plane": {"origin": [o.x, o.y, o.z], "x_dir": [x.x, x.y, x.z], "y_dir": [y.x, y.y, y.z]}, "name": name}},
           {"command": "sketch.arc.center_point", "params": {"center": [cx, cy], "start": at(a0), "end": at(a1), "id": "r1"}}]
    points = [at(a1)] + [list(p) for p in tool["corner"]] + [at(a0)]
    if tool.get("middle"):
        # The face between two merged rounds is a hole's wall: the short way round it.
        (mx, my), mr = tool["middle"]
        p, q = points[1], points[2]
        u, w = math.atan2(p[1] - my, p[0] - mx), math.atan2(q[1] - my, q[0] - mx)
        out.append({"command": "sketch.line", "params": {"points": points[:2], "ids": ["l1"]}})
        out.append({"command": "sketch.arc.center_point", "params": {"center": [mx, my], "start": p, "end": q, "clockwise": (w - u) % (2 * math.pi) > math.pi, "id": "r2"}})
        out.append({"command": "sketch.line", "params": {"points": points[2:], "ids": ["l2"]}})
    else:
        out.append({"command": "sketch.line", "params": {"points": points}})
    if param:
        out.append({"command": "sketch.dimension", "params": {"entities": ["r1"], "type": "radius", "value": param}, "optional": True})
    out.append({"command": "sketch.finish", "params": {}})
    op = "cut" if tool["cut"] else "join"
    if tool["kind"] == "line":
        out.append({"command": "solid.extrude", "params": {"sketch": name, "distance": "%r mm" % tool["length"], "direction": "symmetric", "operation": op, "name": label}})
    else:
        p = {"sketch": name, "axis": "y", "angle": "%r deg" % tool["angle"], "operation": op, "name": label}
        if tool["angle"] < 360:
            p["direction"] = "symmetric"
        out.append({"command": "solid.revolve", "params": p})
    return out


def snapshot(session, base, seconds):
    """(volume, whether it reads back as a valid solid) of the part as SolidCraft has it now, or None."""
    import Part
    path = os.path.abspath(base + ".check.step")
    try:
        if not session.tool("export", seconds, path=path)[0]:
            return None
        shape = Part.read(path)
        return shape.Volume, shape.isValid() and len(shape.Solids) > 0
    except Hung:
        raise
    except Exception:
        return None
    finally:
        if os.path.exists(path):
            os.remove(path)


def rescue(session, s, c, base, done, budget):
    """A fillet or chamfer SolidCraft refused whole. Fillets: the part so far is read into FreeCAD; rounds that meet
    become one profile, as in the FreeCAD build; the rest are tried together again, then one by one, and one SolidCraft
    still refuses is made as a profile. Chamfers: one edge at a time. Returns how many of the edges were done, or None
    when nothing could be tried."""
    p = c["params"]
    label = p.get("name") or c["command"]
    started = time.time()
    said = [""]

    def run(cmd):
        if time.time() - started >= budget:
            said[0] = "out of time"
            return False
        ok, said[0] = session.execute(cmd, min(COMMAND_SECONDS, max(30, budget - (time.time() - started))))
        return ok
    if c["command"] == "solid.chamfer":
        n = 0
        for k, point in enumerate(p.get("edges") or []):
            one = {"command": c["command"], "params": dict(p, edges=[point], name="%s piece %d" % (label, k + 1))}
            if run(one):
                done.append(one)
                n += 1
        return n if n else None
    try:
        import FreeCAD
        import Part
        import rounds
    except ImportError:
        return None
    radius = s.values.get(p["radius"]) if isinstance(p["radius"], str) else p["radius"]
    if not radius:
        return None
    path = os.path.abspath(base + ".partial.step")
    try:
        if not session.tool("export", min(COMMAND_SECONDS, budget), path=path)[0]:
            return None
        shape = Part.read(path)
    finally:
        if os.environ.get("CADCONVERT_KEEP_PARTIAL") and os.path.exists(path):
            os.replace(path, "%s.before %s.step" % (base, label))
        if os.path.exists(path):
            os.remove(path)
    # Each of the file's edges on the solid that has it.
    solids = shape.Solids
    edges = []
    for alternatives in p[REFS]:
        for solid in solids:
            names = ["Edge%d" % (i + 1) for i, e in enumerate(solid.Edges) if any(rounds.edge_matches(r, e, solid) for r in alternatives)]
            if names:
                edges += [(solid, n) for n in names if not any(so is solid and m == n for so, m in edges)]
                break
    if not edges:
        return None
    param = p["radius"] if isinstance(p["radius"], str) else None
    middle = lambda solid, n: (lambda e: list(e.valueAt((e.FirstParameter + e.LastParameter) / 2)))(solid.Edges[int(n[4:]) - 1])
    n_done, piece = 0, 0

    def run_all(cmds, quiet=False):
        """Runs them in order and keeps what ran (it is in the design now); False once one that's needed fails."""
        for cmd in cmds:
            if run(cmd):
                done.append(cmd)
                continue
            if cmd["command"].startswith("solid.") and not quiet:
                s.left.append({"feature": cmd["params"].get("name"), "why": " ".join(said[0].split())[:200]})
            if not cmd.get("optional"):
                return False
        return True

    def by_profile(tool, solid):
        nonlocal piece
        piece += 1
        name = "%s profile %d" % (label, piece)
        if not run_all(profile_commands(tool, radius, param, name)):
            return False
        if tool["cut"]:
            return True
        # Material added over a hole fills its mouth, where Inventor keeps the hole open: cut through it again.
        try:
            reopen = rounds.holes_to_reopen(solid, rounds.profile_solid(tool, radius).cut(solid))
        except Exception:
            reopen = []
        for k, (r, axis, start, height) in enumerate(reopen):
            x = axis.cross(FreeCAD.Vector(1, 0, 0) if abs(axis.x) < 0.9 else FreeCAD.Vector(0, 1, 0)).normalize()
            y = axis.cross(x)
            hole = "%s hole kept open %d" % (name, k + 1)
            # The cylinder lies on the hole's own wall, which SolidCraft's boolean can refuse: then 0.001 mm wider.
            for wider in (0, 0.001):
                sketch = hole + (" sketch wider" if wider else " sketch")
                if run_all([{"command": "sketch.create", "params": {"plane": {"origin": list(start), "x_dir": list(x), "y_dir": list(y)}, "name": sketch}},
                            {"command": "sketch.circle.center", "params": {"center": [0, 0], "radius": r + wider, "id": "h1"}},
                            {"command": "sketch.finish", "params": {}},
                            {"command": "solid.extrude", "params": {"sketch": sketch, "distance": "%r mm" % height, "operation": "cut", "name": hole}}], quiet=not wider):
                    if wider:
                        s.left.append({"feature": hole, "why": "cut 0.001 mm wider than the hole, as SolidCraft refused it on the hole's own wall", "approximate": True})
                    break
        return True
    left = list(edges)
    for solid in solids:
        names = [n for so, n in left if so is solid]
        while len(names) > 1:
            merged = rounds.merged_round(solid, names, radius)
            if not merged or not by_profile(merged[1], solid):
                break
            left = [(so, n) for so, n in left if not (so is solid and n in merged[0])]
            names = [n for n in names if n not in merged[0]]
            n_done += 2
    if left and n_done:
        whole = {"command": "solid.fillet", "params": {"edges": [middle(so, n) for so, n in left], "radius": p["radius"], "name": label + " rest"}}
        if run(whole):
            done.append(whole)
            return n_done + len(left)
    for solid, n in left:
        one = {"command": "solid.fillet", "params": {"edges": [middle(solid, n)], "radius": p["radius"], "name": "%s edge %s" % (label, n[4:])}}
        if run(one):
            done.append(one)
            n_done += 1
            continue
        tool = rounds.round_profile(solid, n, radius)
        if tool and by_profile(tool, solid):
            n_done += 1
            s.left.append({"feature": label, "why": "an edge rounded with a sketch profile, as SolidCraft refused the fillet", "approximate": True})
    return n_done if n_done else None


def build(model, base, change=None, seconds=360, hollow=None, measure=None, solid=None, made=None):
    """Writes base.sc and base.step if SolidCraft ends up with a solid. Returns whether it did, and what was left out.
    A command SolidCraft refuses is tried with its alternatives, else left out (a sketch's rule or dimension leaves the
    part less tied down; a feature leaves it short), and the rest go on."""
    s = Script(model, hollow, measure, solid, made, base)
    commands = s.build()
    if change:
        name, factor = change.split("=")
        for c in commands:
            if c["command"] == "parameters.add" and c["params"]["name"] == name:
                c["params"]["expression"] = "(%s) * %s" % (c["params"]["expression"], factor)
    started = time.time()
    folders = {os.path.dirname(os.path.abspath(base))} | ({os.path.dirname(os.path.abspath(hollow[0]))} if hollow else set())         | {os.path.dirname(os.path.abspath(p)) for p in (solid, made) if p}
    session = Session(sorted(folders))
    done = []
    built = False
    try:
        for c in commands:
            left = seconds - (time.time() - started)
            if left <= 0:
                s.left.append({"why": "out of time after %d s" % seconds})
                break
            try:
                if FACE_REFS in c["params"]:
                    # SolidCraft's own faces just before: a point on each that lies on a surface the file names.
                    faces = []
                    for b in s.bodies:
                        ok, text = session.tool("body_topology", body=b)
                        if ok:
                            faces += json.loads(text).get("faces") or []
                    points, missing = [], 0
                    for alternatives in c["params"][FACE_REFS]:
                        hit = [x for x in faces if any(same_surface(r, x) for r in alternatives if r.get("a"))]
                        if hit:
                            points += [x["point"] for x in hit if x["point"] not in points]
                        else:
                            missing += 1
                    c["params"]["faces"] = points
                    c["faces_unplaced"] = missing
                if REFS in c["params"]:
                    # SolidCraft's own edges just before: those that are the edges the file describes.
                    edges, faces = [], {}
                    for b in s.bodies:
                        ok, text = session.tool("body_topology", body=b)
                        if ok:
                            t = json.loads(text)
                            edges += t.get("edges") or []
                            faces.update({f["name"]: f for f in t.get("faces") or [] if f.get("name")})
                    if edges:
                        points, c["unplaced"] = place_edges(c["params"][REFS], edges, faces)
                        c["placed"] = True
                        if points:
                            c["params"]["edges"] = points
                ok, reason = session.execute(c, min(COMMAND_SECONDS, left + 30))
                reason = " ".join(reason.split())[:300]
                if not ok:
                    for params, why in c["params"].get(ALTERNATIVES, []):
                        instead = dict(c, params=params)
                        if session.execute(instead, min(COMMAND_SECONDS, left + 30))[0]:
                            s.left.append({"feature": params.get("name"), "why": "%s (SolidCraft refused it as Inventor has it: %s)" % (why, reason[:120]), "approximate": True})
                            c, ok = instead, True
                            break
                if not ok and RESCUE and c["command"] in ("solid.fillet", "solid.chamfer") and REFS in c["params"] and len(c["params"].get("edges") or []) > 0:
                    total = len(c["params"][REFS])
                    n_done, n_left = len(done), len(s.left)
                    fillet = c["command"] == "solid.fillet"
                    before = snapshot(session, base, min(COMMAND_SECONDS, left)) if fillet else None
                    try:
                        got = rescue(session, s, c, base, done, min(4 * COMMAND_SECONDS, left))
                    except Hung:
                        raise
                    except Exception as ex:
                        # FreeCAD can fail on the part as SolidCraft exports it; what the rescue made is in done already.
                        s.left.append({"feature": c["params"].get("name"), "why": "working out the rest in FreeCAD failed: %s %s" % (type(ex).__name__, str(ex)[:120])})
                        got = None
                    after = snapshot(session, base, min(COMMAND_SECONDS, left)) if before and len(done) > n_done else None
                    if after and ((before[1] and not after[1]) or abs(after[0] - before[0]) > 0.05 * before[0]):
                        # SolidCraft's booleans sometimes come out wrong (a real part lost a quarter of itself, no
                        # longer a valid solid). A round moves a part's volume by little: otherwise the rescue is undone.
                        del done[n_done:]
                        del s.left[n_left:]
                        s.left.append({"feature": c["params"].get("name"), "why": "made in pieces, but SolidCraft's result came out %s (volume %.0f to %.0f mm3): taken back. Refused whole: %s" % ("an invalid solid" if not after[1] else "wrong", before[0], after[0], reason[:120])})
                        session.close()
                        session = Session(sorted(folders))
                        if done and not session.tool("batch", max(60, seconds - (time.time() - started)), commands=sent(done)["commands"], verbose=False)[0]:
                            s.left.append({"why": "SolidCraft couldn't rebuild the part after taking back %s" % c["params"].get("name")})
                            break
                        continue
                    if got:
                        # What was made is in done already; c itself was never accepted.
                        if got < total:
                            s.left.append({"feature": c["params"].get("name"), "why": "%d of %d edges done in pieces; SolidCraft refused the rest: %s" % (got, total, reason[:150])})
                        continue
            except Hung as ex:
                # SolidCraft working on one command for minutes, or stopping in it: start it again, rebuild what it had,
                # and go on without it.
                ok, reason = False, "SolidCraft stopped in it" if isinstance(ex, Crashed) else "SolidCraft was still working on it after %d s" % COMMAND_SECONDS
                session.close()
                session = Session(sorted(folders))
                if done and not session.tool("batch", max(60, seconds - (time.time() - started)), commands=sent(done)["commands"], verbose=False)[0]:
                    s.left.append({"why": "SolidCraft couldn't rebuild the part after leaving out %s" % c["params"].get("name", c["command"])})
                    break
            if ok:
                done.append(c)
            elif c["command"].startswith("sketch."):
                s.left.append({"command": c["command"], "params": sent([c])["commands"][0]["params"], "why": reason})
            else:
                s.left.append({"feature": c["params"].get("name"), "why": reason})
        for path in (base + ".sc", base + ".step"):
            if os.path.exists(path):
                os.remove(path)
        # Saving a big part can take SolidCraft minutes.
        saved, why = session.tool("save", SAVE_SECONDS, path=os.path.abspath(base + ".sc"))
        exported, why2 = session.tool("export", SAVE_SECONDS, path=os.path.abspath(base + ".step"))
        built = saved and exported and os.path.exists(base + ".step")
        if not built:
            s.left.append({"why": " ".join((why2 if saved else why).split())[:300]})
    except Crashed:
        s.left.append({"why": "SolidCraft stopped while saving the part"})
    except Hung:
        s.left.append({"why": "SolidCraft was still saving the part after %d s" % SAVE_SECONDS})
    except Exception as ex:
        s.left.append({"why": "SolidCraft stopped: %s %s" % (type(ex).__name__, str(ex)[:200]), "trace": traceback.format_exc()[-1500:]})
    finally:
        session.close()
    for c in done:
        if c.get("faces_unplaced"):
            s.left.append({"feature": c["params"].get("name"), "why": "%d of %d faces not found" % (c["faces_unplaced"], len(c["params"][FACE_REFS]))})
        if REFS in c["params"]:
            missing = c.get("unplaced") if c.get("placed") else sum(edge_point(e[0]) is None for e in c["params"][REFS])
            if missing:
                s.left.append({"feature": c["params"].get("name"), "why": "%d of %d edges not found" % (missing, len(c["params"][REFS]))})
    with open(base + ".script.json", "w", encoding="utf-8") as fh:
        json.dump(sent(done), fh, indent=0)
    with open(base + ".left.json", "w", encoding="utf-8") as fh:
        json.dump(s.left, fh, indent=0)
    return built, s.left


def main():
    model = json.load(open(sys.argv[1], encoding="utf-8"))
    folder = sys.argv[2]
    os.makedirs(folder, exist_ok=True)
    built, left = build(model, os.path.join(folder, model["name"]), sys.argv[sys.argv.index("--set") + 1] if "--set" in sys.argv else None)
    print(model["name"], "built" if built else "no solid", "left out", len(left))


if __name__ == "__main__":
    main()
