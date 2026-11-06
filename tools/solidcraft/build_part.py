# One part into SolidCraft: rebuilt from its history if that gives Inventor's solid, otherwise Inventor's solid imported.
# Runs under FreeCADCmd, whose kernel does the comparing and the viewer's meshes.
#   CADCONVERT_MODEL  the model     CADCONVERT_STEP  Inventor's solid     CADCONVERT_OUT  the .sc to write
#   CADCONVERT_REPORT  the result .json     CADCONVERT_VIEW  the viewer's file     CADCONVERT_TOOLS  the tools folder
# The rebuild stays as reports\<name>.sc when it falls short, so its history isn't lost.
import json, os, shutil, subprocess, sys, time, traceback
os.environ["CADCONVERT_NO_MAIN"] = "1"
tools = os.environ["CADCONVERT_TOOLS"]
sys.path.insert(0, os.path.join(tools, "solidcraft"))
sys.path.insert(0, os.path.join(tools, "freecad"))
import FreeCAD
import Part
import build_script
import export_view

FAR_MM = 0.05
SAME_VOLUME = 0.05
SOLID_VOLUME = 0.5
COMPARE_SECONDS = 60
SOLIDCRAFT_SECONDS = 600


def points(shape, per_face=5):
    res = []
    for f in shape.Faces:
        try:
            nodes, tris = f.tessellate(0.5)
            s = f.Surface
            for t in tris[::max(1, len(tris) // per_face)][:per_face]:
                m = (nodes[t[0]] + nodes[t[1]] + nodes[t[2]]) * (1.0 / 3)
                res.append(s.value(*s.parameter(m)))
        except Exception:
            continue
    return res


def compare(path, inventor):
    """How a written solid stands against Inventor's: volume, and points of each surface measured to the other."""
    shape = Part.read(path)
    r = {"volume": shape.Volume, "valid": shape.isValid(), "solids": len(shape.Solids), "volume_off": 100 * (shape.Volume - inventor.Volume) / inventor.Volume if inventor.Volume else None}
    started = time.time()
    far = seen = 0
    worst = 0.0
    for pts, other in ((points(shape), inventor), (points(inventor), shape)):
        hull = Part.Compound(other.Faces)
        for p in pts:
            if time.time() - started > COMPARE_SECONDS:
                r["out_of_time"] = True
                break
            d = hull.distToShape(Part.Vertex(p))[0]
            seen += 1
            worst = max(worst, d)
            far += d > FAR_MM
    r.update(far=far, seen=seen, worst=worst)
    return r


def closed(sc, solids=None):
    """SolidCraft's own opinion of the file: an open body has no volume there, whatever its STEP reads as elsewhere.
    With solids (how many Inventor's solid holds), open bodies beyond them are its surface bodies and don't count:
    every one of the solids must be closed."""
    try:
        r = subprocess.run([build_script.CLI, "eval", sc], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=SOLIDCRAFT_SECONDS)
    except subprocess.TimeoutExpired:
        return None
    bodies = json.loads(r.stdout)["bodies"]
    if solids:
        return sum(1 for b in bodies if b["closed"]) == solids
    return bool(bodies) and all(b["closed"] for b in bodies)


def bring_in(step, out, back):
    """Inventor's solid into SolidCraft as a plain body. Returns what SolidCraft said if it refused."""
    try:
        r = subprocess.run([build_script.CLI, "run", step, "--save", out, "--out", back, "--quiet"], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=SOLIDCRAFT_SECONDS)
    except subprocess.TimeoutExpired:
        return "out of time after %d s" % SOLIDCRAFT_SECONDS
    return " ".join(r.stderr.split())[:200] if not (os.path.exists(out) and os.path.exists(back)) else None


def in_pieces(shape):
    """A ball or O-ring is one face with no edges of its own, which SolidCraft won't import: the same solid, its face cut by three planes."""
    c = shape.BoundBox.Center
    size = shape.BoundBox.DiagonalLength * 2
    planes = [Part.Face(Part.Wire(Part.makeCircle(size, c, FreeCAD.Vector(*n)))) for n in ((1, 0, 0), (0, 1, 0), (0, 0, 1))]
    return Part.Solid(Part.Shell(shape.Faces[0].generalFuse(planes)[1][0]))


def without_degenerate_tori(step, shape, out):
    """SolidCraft drops a torus whose tube is wider than its ring (STEP's DEGENERATE_TOROIDAL_SURFACE, which Inventor
    uses for some rounds), leaving the body open. The same solid with those faces as splines, written to out; None if
    there are none or it can't be done. On real parts the result read back closed in SolidCraft, volumes within 0.25%."""
    with open(step, encoding="utf-8", errors="replace") as fh:
        if "DEGENERATE_TOROIDAL_SURFACE" not in fh.read():
            return None
    try:
        bad = [f for f in shape.Faces if isinstance(f.Surface, Part.Toroid) and f.Surface.MinorRadius >= f.Surface.MajorRadius - 1e-9]
        if not bad:
            return None
        solids = []
        for solid in shape.Solids:
            new = solid.replaceShape([(f, f.toNurbs().Faces[0]) for f in bad if any(f.isSame(g) for g in solid.Faces)])
            new.sewShape(1e-4)
            # All its skins, so a hollow inside stays hollow (a solid can hold a skin round an enclosed void).
            if not new.Shells or not all(x.isClosed() for x in new.Shells):
                return None
            made = Part.makeSolid(new)
            made = made.Solids[0] if made.Solids else None
            if made is None:
                return None
            if made.Volume < 0:
                made.reverse()
            if not made.isValid() or abs(made.Volume - solid.Volume) > 1e-4 * abs(solid.Volume):
                return None
            solids.append(made)
        (solids[0] if len(solids) == 1 else Part.Compound(solids)).exportStep(out)
        return out
    except Exception:
        return None


def hollow_of(model, inventor, base):
    """For a part with one closed shell: the void inside Inventor's solid (its inner skins) as a STEP of its own, and the
    names SolidCraft will give its bodies, or None."""
    if inventor is None or sum(f["type"] == "shell" for f in model.get("features", [])) != 1:
        return None
    voids = []
    for solid in inventor.Solids:
        for skin in solid.Shells[1:]:
            v = Part.Solid(skin)
            voids.append(v if v.Volume > 0 else v.complement())
    if not voids:
        return None
    doc = FreeCAD.newDocument("hollow")
    objects = []
    for i, v in enumerate(voids):
        o = doc.addObject("Part::Feature", "Void")
        o.Label = "Inside %d" % (i + 1)
        o.Shape = v
        objects.append(o)
    import Import
    path = base + ".inside.step"
    Import.export(objects, path)
    names = [o.Label for o in objects]
    FreeCAD.closeDocument(doc.Name)
    return path, names


def measurer(inventor):
    """How far from a point Inventor's solid's next face lies along a direction, or None."""
    if inventor is None:
        return None
    def measure(point, along):
        if point is None:
            return None
        start, d = FreeCAD.Vector(*point), FreeCAD.Vector(*along)
        if d.Length < 1e-12:
            return None
        d.normalize()
        reach = inventor.BoundBox.DiagonalLength * 2 + start.distanceToPoint(inventor.BoundBox.Center)
        hits = [v.Point.distanceToPoint(start) for v in Part.makeLine(start, start + d * reach).section(inventor).Vertexes]
        hits = [h for h in hits if h > 1e-6]
        return min(hits) if hits else None
    return measure


def fault(c):
    if c["closed"] is None:
        return "SolidCraft ran out of time checking it"
    if not c["closed"]:
        return "the body is open"
    if not c["valid"]:
        return "the body isn't valid"
    if c["far"]:
        return "%d of %d surface points are off, by up to %.2f mm" % (c["far"], c["seen"], c["worst"])
    return "volume off by %.2f%%" % (c["volume_off"] or 0)


def same(c):
    return c["valid"] and c["far"] == 0 and c["seen"] > 0 and not c.get("out_of_time") and c["volume_off"] is not None and abs(c["volume_off"]) < SAME_VOLUME


def write_view(model, result, rebuilt_step, inventor_step, target):
    temp = target + ".tmp"
    w = export_view.Writer(temp)
    check = result.get("rebuild") or {}
    # The viewer reads these two as fractions; the volume's is its size, whichever way it's off.
    off = abs(check["volume_off"]) / 100 if check.get("volume_off") is not None else None
    far = check["far"] / check["seen"] if check.get("seen") else None
    head = {"kind": "part", "name": model["name"], "output": "SolidCraft", "fine_mm": export_view.FINE_MM, "rough_mm": export_view.ROUGH_MM, "inventor": None,
            "parameters": [{"name": p["name"], "formula": p.get("formula"), "shown": None} for p in model.get("parameters", [])],
            "matches": result["outcome"] == "rebuilt", "exact_added": result["outcome"] == "solid", "flawed": result["outcome"] == "flawed",
            "volume_off": off, "surface_far": far, "surface_worst": check.get("worst"),
            "left_over": None, "volume": check.get("volume"), "notes": result["notes"], "bodies": 0, "unplaced": [], "tip_differs": [], "stages_left_out": 0}
    if inventor_step:
        head["inventor"] = w.add(export_view.block(Part.read(inventor_step), export_view.FINE_MM))
    nodes = []
    if rebuilt_step:
        head["bodies"] = 1
        nodes.append({"name": "SolidCraft rebuild", "type": "rebuild", "status": "ok" if result["outcome"] == "rebuilt" else "approximate", "why": None if result["outcome"] == "rebuilt" else "differs from Inventor's solid", "volume": head["volume"], "body": 0,
                      "block": w.add(export_view.block(Part.read(rebuilt_step), export_view.FINE_MM)), "object": "rebuild", "sketches": []})
    for x in result["left"]:
        if x.get("feature"):
            nodes.append({"name": x["feature"], "type": "feature", "status": "approximate" if x.get("approximate") else "skipped", "why": x["why"], "volume": None, "body": None, "block": None, "object": None, "sketches": []})
    head["nodes"] = nodes
    w.close(head)
    os.replace(temp, target)


def main():
    with open(os.environ["CADCONVERT_MODEL"], encoding="utf-8") as fh:
        model = json.load(fh)
    out = os.environ["CADCONVERT_OUT"]
    step = os.environ.get("CADCONVERT_STEP") or None
    reports = os.path.dirname(os.environ["CADCONVERT_REPORT"])
    base = os.path.join(reports, model["name"])
    for old in (out, base + ".sc", base + ".step", base + ".solid.step"):
        if os.path.exists(old):
            os.remove(old)
    result = {"name": model["name"], "outcome": "none", "notes": [], "rebuild": None, "solid": None, "left": []}
    inventor = Part.read(step) if step and os.path.exists(step) else None
    try:
        made = os.environ["CADCONVERT_MODEL"][:-len(".model.json")] + ".made.step" if os.environ["CADCONVERT_MODEL"].endswith(".model.json") else None
        built, result["left"] = build_script.build(model, base, hollow=hollow_of(model, inventor, base), measure=measurer(inventor),
                                                   solid=step, made=made if made and os.path.exists(made) else None) if model.get("features") else (False, [])
    except subprocess.TimeoutExpired:
        built = False
        result["notes"].append("SolidCraft ran out of time rebuilding its history")
    built = built and os.path.exists(base + ".step")
    sheet = [x["feature"] for x in result["left"] if "sheet metal" in x.get("why", "") or "flange" in x.get("why", "")]
    if sheet:
        result["notes"].append("sheet metal isn't carried to SolidCraft (%d features)" % len(sheet))
    if built and inventor is not None:
        result["rebuild"] = compare(base + ".step", inventor)
        if same(result["rebuild"]) and closed(base + ".sc", len(inventor.Solids)):
            result["outcome"] = "rebuilt"
            shutil.copyfile(base + ".sc", out)
    if os.environ.get("CADCONVERT_REBUILD_ONLY"):
        # quick_test.py: only whether the history rebuild matches.
        return result
    if result["outcome"] == "none" and inventor is not None:
        splined = without_degenerate_tori(step, inventor, base + ".splined.step")
        refused = bring_in(splined or step, out, base + ".solid.step")
        if splined and not refused:
            result["notes"].append("rounds Inventor stores as a torus wider than its ring are brought in as splines, which SolidCraft needs; same shape")
        if refused and len(inventor.Faces) == 1:
            cut = base + ".pieces.step"
            in_pieces(inventor).exportStep(cut)
            refused = bring_in(cut, out, base + ".solid.step")
            if not refused:
                result["notes"].append("its one round face is cut into pieces, which SolidCraft needs; same shape")
        if not refused:
            c = compare(base + ".solid.step", inventor)
            c["closed"] = closed(out, len(inventor.Solids))
            result["solid"] = c
            good = c["closed"] and c["valid"] and c["far"] == 0 and c["volume_off"] is not None and abs(c["volume_off"]) < SOLID_VOLUME
            result["outcome"] = "solid" if good else "flawed"
            if not good:
                result["notes"].append("Inventor's solid doesn't come through SolidCraft cleanly: " + fault(c))
        else:
            result["notes"].append("SolidCraft couldn't read Inventor's solid: " + refused)
    if result["outcome"] == "none" and built:
        # Nothing to check it against, so it can't be called right.
        shutil.copyfile(base + ".sc", out)
        result["outcome"] = "flawed"
        result["notes"].append("Inventor's file holds no solid to check the rebuild against")
    if result["outcome"] != "none":
        write_view(model, result, base + ".step" if built else None, step if inventor is not None else None, os.environ["CADCONVERT_VIEW"])
    return result


try:
    answer = main()
except Exception as ex:
    answer = {"outcome": "none", "fatal": "%s: %s" % (type(ex).__name__, str(ex)[:300]), "trace": traceback.format_exc(), "notes": [], "left": []}
with open(os.environ["CADCONVERT_REPORT"], "w", encoding="utf-8") as fh:
    json.dump(answer, fh, indent=1)
