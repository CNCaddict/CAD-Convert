# Writes what the converter's viewer shows of one converted part, in its own process so that a shape FreeCAD can't mesh
# costs a picture and never a conversion. Inventor's solid comes from the STEP read out of the Inventor file; the history
# comes from the .FCStd, one mesh per feature, as FreeCAD stored it.
#   CADCONVERT_FC  the .FCStd     CADCONVERT_STEP  the .step     CADCONVERT_REPORT  the report     CADCONVERT_VIEW  the file to write
#
# The file: "CCVW", gzip blocks one after another, the description as JSON, then the JSON's offset as 8 bytes. A block is
# u32 faces, each u32 points, u32 triangles, float how far its mesh strays, floats, u32 indexes; then u32 lines, each u32 points, floats.
import gzip, json, os, re, struct, time, traceback
from array import array
import FreeCAD as App
import Part

FINE_MM = 0.02
ROUGH_MM = 0.05
STAGE_SECONDS = 90


def stray(face, nodes, triangles):
    """How far a face's mesh strays from its surface. FreeCAD's limit is only a request: on long thin faces the mesh goes
    well past it. The middle of a triangle is about 2/3 as far off as the most it can be, hence the 1.5. Differences
    smaller than this can't be told from the mesh, and the viewer allows for it."""
    try:
        s = face.Surface
        if isinstance(s, Part.Plane):
            return 0.0
        worst = 0.0
        for t in triangles[::max(1, len(triangles) // 48)]:
            m = (nodes[t[0]] + nodes[t[1]] + nodes[t[2]]) * (1.0 / 3)
            worst = max(worst, s.value(*s.parameter(m)).distanceToPoint(m))
        return worst * 1.5
    except Exception:
        return 0.0


def block(shape, tol, faces=True):
    out = bytearray()
    good = []
    if faces:
        try:
            shape.tessellate(tol)
        except Exception:
            pass
        for f in shape.Faces:
            try:
                nodes, triangles = f.tessellate(tol)
            except Exception:
                continue
            if nodes and triangles:
                good.append((nodes, triangles, stray(f, nodes, triangles) if tol == FINE_MM else 0.0))
    out += struct.pack("<I", len(good))
    for nodes, triangles, off in good:
        out += struct.pack("<IIf", len(nodes), len(triangles), off)
        out += array("f", [c for n in nodes for c in (n.x, n.y, n.z)]).tobytes()
        out += array("I", [i for t in triangles for i in t]).tobytes()
    lines = []
    for e in shape.Edges:
        try:
            points = e.discretize(Deflection=tol * 2)
        except Exception:
            continue
        if len(points) > 1:
            lines.append(points)
    out += struct.pack("<I", len(lines))
    for points in lines:
        out += struct.pack("<I", len(points))
        out += array("f", [c for p in points for c in (p.x, p.y, p.z)]).tobytes()
    return gzip.compress(bytes(out), 6)


class Writer:
    def __init__(self, path):
        self.fh = open(path, "wb")
        self.fh.write(b"CCVW")
        self.blocks = []

    def add(self, data):
        self.blocks.append([self.fh.tell(), len(data)])
        self.fh.write(data)
        return len(self.blocks) - 1

    def close(self, head):
        head["blocks"] = self.blocks
        at = self.fh.tell()
        self.fh.write(json.dumps(head).encode("utf-8"))
        self.fh.write(struct.pack("<Q", at))
        self.fh.close()


def solid_features(body):
    return [o for o in body.Group if o.isDerivedFrom("PartDesign::Feature") and not o.Shape.isNull()]


def preview(target):
    """Before any conversion: Inventor's solid alone, to look at. Nothing to rebuild or compare with yet."""
    temp = target + ".tmp"
    w = Writer(temp)
    block_id = w.add(block(Part.read(os.environ["CADCONVERT_STEP"]), FINE_MM))
    w.close({"kind": "part", "preview": True, "name": os.environ.get("CADCONVERT_NAME"), "fine_mm": FINE_MM, "rough_mm": ROUGH_MM, "inventor": block_id, "parameters": [], "nodes": [], "bodies": 0})
    os.replace(temp, target)


def main():
    target = os.environ["CADCONVERT_VIEW"]
    if not os.environ.get("CADCONVERT_FC"):
        return preview(target)
    path = os.environ["CADCONVERT_FC"]
    with open(os.environ["CADCONVERT_REPORT"], encoding="utf-8") as fh:
        report = json.load(fh)
    started = time.time()
    temp = target + ".tmp"
    w = Writer(temp)
    step = report.get("step") or {}
    head = {"kind": "part", "name": report.get("name"), "fine_mm": FINE_MM, "rough_mm": ROUGH_MM, "inventor": None,
            "parameters": [{"name": p["name"], "formula": p.get("formula"), "shown": p.get("shown")} for p in report.get("parameters", [])],
            "matches": bool(report.get("rebuild_matches_solid")), "exact_added": bool(report.get("frozen_added")), "flawed": bool(report.get("frozen_flawed")),
            "volume_off": step.get("volume_off"), "surface_far": step.get("surface_far"), "surface_worst": step.get("surface_worst"),
            "left_over": step.get("left_over"), "volume": step.get("volume"), "notes": report.get("notes", [])}

    step_path = os.environ.get("CADCONVERT_STEP")
    if step_path and os.path.exists(step_path):
        try:
            head["inventor"] = w.add(block(Part.read(step_path), FINE_MM))
        except Exception as ex:
            head["inventor_error"] = str(ex)[:200]

    doc = App.openDocument(path)
    bodies = [o for o in doc.Objects if o.TypeId == "PartDesign::Body" and not o.Label.endswith(" tool")]
    head["bodies"] = len(bodies)
    # Each feature's objects, in Inventor's order: the feature's own name, then any pieces it had to be split into.
    pending = [(i, o) for i, b in enumerate(bodies) for o in solid_features(b)]
    nodes = []
    for f in report.get("features", []):
        node = {"name": f.get("name"), "type": f.get("type"), "status": f.get("status"), "why": f.get("why"), "volume": f.get("volume"), "body": None, "block": None, "object": None, "sketches": []}
        nodes.append(node)
        if f.get("status") in ("skipped", "not computed", "cosmetic"):
            continue
        # A fillet can be marked failed with some of its edges done: those pieces are in the file, so they belong to its row.
        name = f.get("name")
        mine = [(i, o) for i, o in pending if o.Label == name or (name and o.Label.startswith(name + " piece"))]
        # FreeCAD numbers a label already taken: a feature Inventor calls Body is Body001 beside the Body that holds it.
        if name and not mine:
            mine = [(i, o) for i, o in pending if re.fullmatch(re.escape(name) + r"\d{3}", o.Label)][:1]
        if not name and pending:
            mine = pending[:1]
        for pair in mine:
            pending.remove(pair)
        if mine:
            node["body"], last = mine[-1]
            node["object"] = last.Name
            for i, o in mine:
                profile = getattr(o, "Profile", None)
                if profile and profile[0] is not None and profile[0].isDerivedFrom("Sketcher::SketchObject"):
                    if profile[0].Label not in [s["name"] for s in node["sketches"]]:
                        node["sketches"].append({"name": profile[0].Label, "object": profile[0].Name, "block": None})
    head["unplaced"] = [o.Label for i, o in pending]
    # The last row of each body has to be the feature the file itself says is last.
    head["tip_differs"] = []
    for i, b in enumerate(bodies):
        rows = [n for n in nodes if n["body"] == i]
        if b.Tip is not None and rows and rows[-1]["object"] != b.Tip.Name:
            head["tip_differs"].append(b.Label)

    # The last state of each body first, finely: the comparison needs it. Then the steps before it, latest first, as far as time allows.
    finals = {}
    for n in nodes:
        if n["object"]:
            finals[n["body"]] = n
    for n in finals.values():
        n["block"] = w.add(block(doc.getObject(n["object"]).Shape, FINE_MM))
    head["stages_left_out"] = 0
    for n in reversed(nodes):
        if not n["object"] or n["block"] is not None:
            continue
        if time.time() - started > STAGE_SECONDS:
            head["stages_left_out"] += 1
            continue
        n["block"] = w.add(block(doc.getObject(n["object"]).Shape, ROUGH_MM))
    for n in nodes:
        for s in n["sketches"]:
            if time.time() - started > STAGE_SECONDS:
                break
            sk = doc.getObject(s["object"])
            if sk is not None and not sk.Shape.isNull():
                s["block"] = w.add(block(sk.Shape, ROUGH_MM, faces=False))
    head["nodes"] = nodes
    head["seconds"] = time.time() - started
    w.close(head)
    App.closeDocument(doc.Name)
    os.replace(temp, target)


# Borrowed for its Writer and block by the SolidCraft build, which does its own main.
if "CADCONVERT_NO_MAIN" not in os.environ:
    try:
        main()
    except Exception:
        with open(os.environ["CADCONVERT_VIEW"] + ".error.txt", "w", encoding="utf-8") as fh:
            fh.write(traceback.format_exc())
