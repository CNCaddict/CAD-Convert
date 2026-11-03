# A converter assembly model as a SolidCraft design: each occurrence links its part's or sub-assembly's .sc, placed where
# Inventor has it, then a joint for each constraint SolidCraft has an equal for. A joint goes in only if nothing moves from
# where Inventor has it; the rest are left out and listed, and a part with no joint is simply free.
#   CADCONVERT_MODEL  the assembly .json     CADCONVERT_OUT  the .sc to write     CADCONVERT_REPORT  the result .json
import json, math, os, re, subprocess, time, traceback

CLI = os.environ.get("CADCONVERT_SOLIDCRAFT") or r"C:\Program Files\SolidCraft\solidcraft-cli.exe"
MOVED_MM = 0.05
MOVED_TURN = 1e-3
JOINT_SECONDS = 300


def run(commands, base, out=None):
    path = base + ".script.json"
    with open(path, "w", encoding="utf-8") as fh:
        json.dump({"commands": commands}, fh, indent=0)
    res = base + ".results.json"
    if os.path.exists(res):
        os.remove(res)
    args = [CLI, "run", path, "--results", "--json-out", res, "--quiet"]
    if out:
        args += ["--save", out]
    r = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=1800)
    results = None
    if r.returncode == 0 and os.path.exists(res):
        with open(res, encoding="utf-8") as fh:
            results = json.load(fh)
        if isinstance(results, dict):
            results = results.get("results", results)
    return r.returncode, " ".join(r.stderr.split())[:300], results


def failing(text):
    m = re.search(r"step (\d+)", text)
    return int(m.group(1)) - 1 if m else None


def parse(text):
    """A constraint's geometry, from its text, in mm."""
    w = text.split()
    if not w or w[0] == "?":
        return None
    n = [float(x) for x in w[1:] if re.match(r"^-?[\d.]+(E[-+]?\d+)?$", x, re.I)]
    g = {"kind": w[0], "p": [x * 10 for x in n[:3]]}
    if w[0] in ("plane", "line", "circle", "cylinder", "torus") and len(n) >= 6:
        size = math.sqrt(sum(x * x for x in n[3:6])) or 1
        g["d"] = [x / size for x in n[3:6]]
    return g


def world(m, p):
    return [m[i] * p[0] + m[i + 1] * p[1] + m[i + 2] * p[2] + m[i + 3] * 10 for i in (0, 4, 8)]


def turned(m, d):
    return [m[i] * d[0] + m[i + 1] * d[1] + m[i + 2] * d[2] for i in (0, 4, 8)]


def origin(occ, g, use_z=True):
    o = {"occurrence": occ, "point": g["p"]}
    if use_z and "d" in g:
        o["z"] = g["d"]
    return o


def joint_for(c, a, b, geos):
    """The command for one constraint, or why there's none. Built as the parts stand, so a joint holds them where they are."""
    t = c["type"]
    kinds = sorted(g["kind"] for g in geos)
    d = c["values"][0] * 10 if c["values"] else 0.0
    built = lambda kind, g: {"command": "joint.as_built", "params": {"type": kind, "a": a, "b": b, "at": origin(b, g), "name": c["name"]}}
    relation = lambda kind, ga, gb, **more: {"command": "joint.create", "params": dict({"type": kind, "a": origin(a, ga), "b": origin(b, gb), "name": c["name"], "flip": True}, **more)}
    lines = ("line", "circle", "cylinder")
    if t == 5 or t == 0x0E:
        return built("rigid" if t == 5 and c["settings"] & 0x2000 else "revolute", geos[0])
    if t in (0x10, 0x11, 0x0F, 0x12):
        return built({0x10: "rigid", 0x11: "ball", 0x0F: "slider", 0x12: "cylindrical"}[t], geos[0])
    if t in (1, 2):
        if kinds == ["plane", "plane"]:
            return built("planar", geos[0])
        if all(k in lines for k in kinds) and abs(d) < 1e-6:
            return built("cylindrical", geos[0])
        if kinds == ["point", "point"] and abs(d) < 1e-6:
            return built("ball", geos[0])
        if abs(d) > 1e-6:
            return "SolidCraft has no equal for this with an offset between %s and %s" % tuple(kinds)
        pair = sorted(zip([g["kind"] for g in geos], geos), key=lambda x: x[0] != "point")
        if pair[0][0] == "point" and pair[1][0] == "plane":
            return relation("point_on_plane", geos[0], geos[1])
        if pair[0][0] == "point" and pair[1][0] in lines:
            return relation("point_on_line", geos[0], geos[1])
        if "plane" in kinds and any(k in lines for k in kinds):
            return relation("line_on_plane", geos[0], geos[1])
        return "SolidCraft has no equal for a mate between %s and %s" % tuple(kinds)
    if t == 3:
        if any("d" not in g for g in geos):
            return "no direction to measure the angle from"
        dot = sum(x * y for x, y in zip(geos[0]["d"], geos[1]["d"]))
        return relation("angle", geos[0], geos[1], angle="%r deg" % math.degrees(math.acos(max(-1, min(1, dot)))))
    return "SolidCraft has no equal for a %s constraint" % c["kind"]


def wanted(m):
    return [[m[0], m[1], m[2], m[3] * 10], [m[4], m[5], m[6], m[7] * 10], [m[8], m[9], m[10], m[11] * 10]]


def moved(results, want):
    """How far the occurrences are from where they were put, from the listing at the end of the script."""
    mm = turn = 0.0
    listing = next((r for r in reversed(results or []) if isinstance(r, dict) and "components" in r), None)
    if listing is None:
        return None, None
    have = {o["id"]: o.get("transform") for comp in listing["components"] for o in comp.get("occurrences", [])}
    for occ, m in want.items():
        tr = have.get(occ) or [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]]
        w = wanted(m)
        mm = max(mm, math.sqrt(sum((tr[3][i] - w[i][3]) ** 2 for i in range(3))))
        turn = max(turn, max(abs(tr[j][i] - w[i][j]) for i in range(3) for j in range(3)))
    return mm, turn


def main(report):
    with open(os.environ["CADCONVERT_MODEL"], encoding="utf-8") as fh:
        model = json.load(fh)
    report["name"] = model["name"]
    out = os.environ["CADCONVERT_OUT"]
    base = os.environ["CADCONVERT_REPORT"][:-len(".result.json")]
    occs = model["occurrences"]
    # The links first: SolidCraft gives each a number, and everything after refers to it.
    inserts, slots, seen = [], [], set()
    for i, occ in enumerate(occs):
        report["occurrences"] += 1
        if occ.get("suppressed"):
            report["suppressed"] += 1
            continue
        path = occ.get("target")
        if not path or not os.path.exists(path):
            report["missing"].append("%s (%s)" % (occ["name"], occ.get("file")))
            continue
        if not occ.get("matrix"):
            report["notes"].append("%s has no placement" % occ["name"])
            occ["matrix"] = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
        inserts.append({"command": "component.insert_design", "params": {"path": path, "name": os.path.splitext(os.path.basename(path))[0], "reuse": path in seen}})
        slots.append(i)
        seen.add(path)
    while True:
        code, err, results = run(inserts, base)
        if code == 0:
            break
        k = failing(err)
        if k is None or k >= len(inserts):
            raise Exception(err)
        report["missing"].append("%s (SolidCraft refused its file: %s)" % (occs[slots[k]]["name"], err[:120]))
        del inserts[k], slots[k]
    ids = {slots[n]: r["occurrence"] for n, r in enumerate(results or [])}
    want = {ids[i]: occs[i]["matrix"] for i in ids}
    report["linked"] = len(ids)
    moves = []
    who = []
    for i, occ_id in ids.items():
        moves.append({"command": "occurrence.move", "params": {"occurrence": occ_id, "space": "world", "matrix": wanted(occs[i]["matrix"]), "move_grounded": True}})
        who.append(i)
    # SolidCraft grounds the first thing inserted, and Inventor may not have.
    held = [i for i in ids if occs[i].get("grounded")]
    if not held and ids:
        held = [next(iter(ids))]
        report["notes"].append("nothing is grounded in Inventor, so %s is, to hold the assembly in place" % occs[held[0]]["name"])
    grounds = [{"command": "occurrence.ground", "params": {"occurrence": ids[i], "grounded": i in held}} for i in ids]
    hidden = [i for i in ids if not occs[i].get("visible", True)]
    report["hidden"] = len(hidden)
    if hidden:
        report["notes"].append("%d parts hidden in Inventor show here" % len(hidden))
    # Joints as far as the model has an equal.
    joints, why = [], []
    total = 0
    for c in model.get("constraints", []):
        total += 1
        label = "%s (%s)" % (c["name"], c["kind"])
        if c["suppressed"]:
            why.append(label + ": suppressed in Inventor")
            continue
        if c["failing"]:
            why.append(label + ": failing in Inventor")
            continue
        if len(c["occurrences"]) != 2 or len(c["geometry"]) != 2:
            why.append(label + ": no equal, with %d arguments" % len(c["occurrences"]))
            continue
        geos = [parse(g) for g in c["geometry"]]
        ends = []
        for n, (i, g, axis) in enumerate(zip(c["occurrences"], geos, c.get("axes") or [None, None])):
            if i >= 0 and i not in ids:
                ends = None
                break
            ends.append(0 if i < 0 else ids[i])
            if g is not None and i >= 0 and axis:
                g["d"] = turned(occs[i]["matrix"], axis)
                g["p"] = world(occs[i]["matrix"], g["p"])
        if ends is None or None in geos:
            why.append(label + ": one of its parts isn't in the file")
            continue
        if ends[0] == ends[1]:
            why.append(label + ": both ends are in one rigid sub-assembly")
            continue
        j = joint_for(c, ends[0], ends[1], geos)
        if isinstance(j, str):
            why.append(label + ": " + j)
        else:
            j["label"] = label
            joints.append(j)
    report["joints_total"] = total

    def script(chosen):
        return inserts + moves + grounds + [{"command": x["command"], "params": x["params"]} for x in chosen] + [{"command": "joint.solve", "params": {}}, {"command": "joint.list", "params": {}}, {"command": "component.list", "params": {}}]

    started = time.time()

    def trial(chosen):
        """Runs with these joints, dropping what SolidCraft refuses; says whether everything stayed put."""
        chosen = list(chosen)
        fixed = len(inserts) + len(moves) + len(grounds)
        while True:
            code, err, results = run(script(chosen), base)
            if code == 0:
                break
            k = failing(err)
            if k is None:
                raise Exception(err)
            if k < fixed:
                if k >= len(inserts) and k < len(inserts) + len(moves):
                    bad = who[k - len(inserts)]
                    report["notes"].append("%s couldn't be placed (mirrored?): %s" % (occs[bad]["name"], err[:120]))
                    want.pop(ids[bad], None)
                    del moves[k - len(inserts)], who[k - len(inserts)]
                    fixed -= 1
                    continue
                raise Exception(err)
            why.append("%s: SolidCraft refused it: %s" % (chosen[k - fixed]["label"], err.split("): ", 1)[-1][:120]))
            del chosen[k - fixed]
        listing = next((r for r in reversed(results or []) if isinstance(r, dict) and "joints" in r), {})
        clash = {x["name"]: x["why"] for x in listing.get("conflicts") or []}
        if clash:
            for x in chosen:
                if x["params"]["name"] in clash:
                    why.append("%s: conflicts with the others: %s" % (x["label"], clash[x["params"]["name"]]))
            rest = [x for x in chosen if x["params"]["name"] not in clash]
            if len(rest) < len(chosen):
                return trial(rest)
        mm, turn = moved(results, want)
        return chosen, mm, turn

    def settle(chosen):
        # All together if that holds; otherwise split, until each joint that moves things stands alone and goes.
        kept, mm, turn = trial(chosen)
        if mm is not None and mm < MOVED_MM and turn < MOVED_TURN:
            return kept
        if len(kept) == 1:
            why.append("%s: it would move parts %.3f mm" % (kept[0]["label"], mm or 0))
            return []
        if not kept:
            return []
        if time.time() - started > JOINT_SECONDS:
            for x in kept:
                why.append(x["label"] + ": out of time")
            return []
        half = len(kept) // 2
        return settle_on(kept[:half]) + settle_on(kept[half:])

    def settle_on(chosen):
        return settle(chosen) if chosen else []

    kept, mm, turn = trial([])
    if mm is None or mm >= MOVED_MM or turn >= MOVED_TURN:
        report["notes"].append("placing the parts alone left them up to %.3f mm from Inventor's places" % (mm or -1))
    kept = settle(joints)
    code, err, results = run(script(kept), base, out)
    if code != 0:
        raise Exception(err)
    mm, turn = moved(results, want)
    listing = next((r for r in reversed(results or []) if isinstance(r, dict) and "joints" in r), {})
    report["conflicts"] = listing.get("conflicts")
    report["errors"] = listing.get("errors")
    report["dof"] = listing.get("dof")
    report["joints"] = {"total": total, "carried": len(kept), "left_out": total - len(kept), "moved_mm": mm or 0.0, "why": why, "seconds": round(time.time() - started, 1)}


result = {"occurrences": 0, "linked": 0, "suppressed": 0, "hidden": 0, "missing": [], "notes": []}
try:
    main(result)
except Exception as ex:
    result["fatal"] = "%s: %s" % (type(ex).__name__, str(ex)[:300])
    result["trace"] = traceback.format_exc()
with open(os.environ["CADCONVERT_REPORT"], "w", encoding="utf-8") as fh:
    json.dump(result, fh, indent=1)
