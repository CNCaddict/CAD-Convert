# The fast check: small test parts made in Inventor (corpus\generated, by `invre gen`), each showing one feature or one
# awkward case, rebuilt in FreeCAD and compared with the solid Inventor stored, as quick_test.py does for real parts.
#   python case_test.py <corpus folder> <work folder> [--decode] [--seconds N] [--workers N] [--save-baseline] [names...]
# Run with FreeCAD's python.exe. Each part is decoded once (invre model, invre step) into <work>\reports and <work>;
# --decode does it again, after a change to invre. Names are "<experiment>-<variant>" and may end in * to match
# several. The result is compared with cases_baseline.json beside this script: a part that matched there and doesn't
# now is listed first. --save-baseline writes the new results there (only after checking the list).
import glob, json, os, subprocess, sys, time
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

TOOLS = os.path.dirname(os.path.abspath(__file__))
INVRE = os.path.join(os.path.dirname(TOOLS), "bin", "invre.exe")
BASELINE = os.path.join(TOOLS, "cases_baseline.json")


def wanted(name, names):
    return not names or any(name == n or (n.endswith("*") and name.startswith(n[:-1])) for n in names)


def decode(corpus, work, again, names):
    reports = os.path.join(work, "reports")
    os.makedirs(reports, exist_ok=True)
    done = failed = 0
    for ipt in sorted(glob.glob(os.path.join(corpus, "*", "*.ipt"))):
        name = os.path.basename(os.path.dirname(ipt)) + "-" + os.path.splitext(os.path.basename(ipt))[0]
        if not wanted(name, names):
            continue
        model, step = os.path.join(reports, name + ".model.json"), os.path.join(work, name + ".step")
        if not again and os.path.exists(model) and os.path.getmtime(model) >= os.path.getmtime(ipt):
            continue
        for args, out in ((["model", ipt, model], model), (["step", ipt, step], step)):
            if os.path.exists(out):
                os.remove(out)
            subprocess.run([INVRE] + args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
        done += 1
        failed += not os.path.exists(model)
    return done, failed


def summary(results, names):
    out = {}
    for path in glob.glob(os.path.join(results, "*.report.json")):
        name = os.path.basename(path)[:-len(".report.json")]
        if not wanted(name, names):
            continue
        with open(path, encoding="utf-8") as fh:
            r = json.load(fh)
        step = r.get("step") or {}
        if not step.get("volume"):
            kind = "no solid"
        elif not r.get("features"):
            kind = "no history"
        else:
            kind = "match" if r.get("rebuild_matches_solid") else "differs"
        out[name] = {"kind": kind, "volume_off": step.get("volume_off"), "surface_far": step.get("surface_far"),
                     "bad": [f["name"] + ": " + f["status"] for f in r.get("features", []) if f["status"] not in ("ok", "cosmetic", "not computed", "rolled back")]}
    return out


def main():
    args = sys.argv[1:]
    corpus, work, rest = args[0], args[1], args[2:]
    again = save = False
    seconds, workers, names = 120, 6, []
    while rest:
        a = rest.pop(0)
        if a == "--decode":
            again = True
        elif a == "--save-baseline":
            save = True
        elif a == "--seconds":
            seconds = int(rest.pop(0))
        elif a == "--workers":
            workers = int(rest.pop(0))
        else:
            names.append(a)
    started = time.time()
    done, failed = decode(corpus, work, again, names)
    print("decoded %d parts (%d failed) in %.0f s" % (done, failed, time.time() - started), flush=True)
    results = os.path.join(work, "results")
    picked = [n for n in (os.path.basename(p)[:-len(".model.json")] for p in glob.glob(os.path.join(work, "reports", "*.model.json"))) if wanted(n, names)]
    subprocess.run([sys.executable, os.path.join(TOOLS, "quick_test.py"), results, os.path.join(work, "reports"), "--seconds", str(seconds), "--workers", str(workers)] + picked,
                   stdout=subprocess.DEVNULL)
    now = summary(results, names)
    before = {}
    if os.path.exists(BASELINE):
        with open(BASELINE, encoding="utf-8") as fh:
            before = json.load(fh)
    lost = sorted(n for n in now if before.get(n, {}).get("kind") == "match" and now[n]["kind"] != "match")
    gained = sorted(n for n in now if now[n]["kind"] == "match" and n in before and before[n]["kind"] != "match")
    new = sorted(n for n in now if n not in before)
    for n in lost:
        print("LOST    %-34s %s" % (n, "; ".join(now[n]["bad"])[:120]))
    for n in gained:
        print("GAINED  %s" % n)
    for n in sorted(now):
        if now[n]["kind"] == "differs":
            off = now[n]["volume_off"]
            print("differs %-34s volume %s  %s" % (n, "?" if off is None else "%.3f%%" % (100 * off), "; ".join(now[n]["bad"])[:100]))
    counts = {}
    for v in now.values():
        counts[v["kind"]] = counts.get(v["kind"], 0) + 1
    print("%d parts: %s; %d lost, %d gained, %d new since the baseline, in %.0f s" % (len(now), ", ".join("%d %s" % (c, k) for k, c in sorted(counts.items())), len(lost), len(gained), len(new), time.time() - started))
    if save:
        before.update(now)
        with open(BASELINE, "w", encoding="utf-8") as fh:
            json.dump(before, fh, indent=1, sort_keys=True)
        print("baseline saved:", BASELINE)


if __name__ == "__main__":
    main()
