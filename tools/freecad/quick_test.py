# Tries a change to build_model.py on parts already decoded, without reading the Inventor files again.
#   python quick_test.py <results folder> <reports folder>[;<reports folder>...] [--seconds N] [--workers N] [part names, or all parts]
# Each part gets its own job object, so a part that hangs is ended along with anything FreeCAD started.
import concurrent.futures, ctypes, glob, json, os, subprocess, sys, time
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

TOOLS = os.path.dirname(os.path.abspath(__file__))
FREECAD = os.environ.get("CADCONVERT_FREECAD") or os.path.join(os.path.dirname(sys.executable), "FreeCADCmd.exe")
kernel = ctypes.windll.kernel32
kernel.CreateJobObjectW.restype = ctypes.c_void_p
kernel.OpenProcess.restype = ctypes.c_void_p
kernel.AssignProcessToJobObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
kernel.TerminateJobObject.argtypes = [ctypes.c_void_p, ctypes.c_uint]
kernel.CloseHandle.argtypes = [ctypes.c_void_p]


def one(name, model, step, out, seconds):
    report = os.path.join(out, name + ".report.json")
    if os.path.exists(report):
        os.remove(report)
    env = dict(os.environ, CADCONVERT_MODEL=model, CADCONVERT_OUT=os.path.join(out, name + ".FCStd"), CADCONVERT_REPORT=report, CADCONVERT_TOOLS=TOOLS, FREECAD_USER_HOME=os.path.join(out, "home", str(os.getpid())))
    if step:
        env["CADCONVERT_STEP"] = step
    started = time.time()
    job = kernel.CreateJobObjectW(None, None)
    p = subprocess.Popen([FREECAD, os.path.join(TOOLS, "build_model.py")], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    process = kernel.OpenProcess(0x1F0FFF, False, p.pid)
    kernel.AssignProcessToJobObject(job, process)
    kernel.CloseHandle(process)
    try:
        p.wait(seconds)
    except subprocess.TimeoutExpired:
        kernel.TerminateJobObject(job, 1)
        p.wait()
        kernel.CloseHandle(job)
        return name, None, time.time() - started
    kernel.CloseHandle(job)
    r = None
    if os.path.exists(report):
        with open(report, encoding="utf-8") as fh:
            r = json.load(fh)
    return name, r or {"fatal": "no report"}, time.time() - started


def main():
    args, seconds, workers, names = sys.argv[1:], 60, 6, []
    out, folders = args[0], args[1].split(";")
    rest = args[2:]
    while rest:
        a = rest.pop(0)
        if a == "--seconds":
            seconds = int(rest.pop(0))
        elif a == "--workers":
            workers = int(rest.pop(0))
        else:
            names.append(a)
    os.makedirs(os.path.join(out, "home"), exist_ok=True)
    jobs = {}
    for folder in folders:
        for model in glob.glob(os.path.join(folder, "*.model.json")):
            name = os.path.basename(model)[:-len(".model.json")]
            if name in jobs or (names and name not in names):
                continue
            with open(model, encoding="utf-8") as fh:
                if "occurrences" in json.load(fh):
                    continue
            step = os.path.join(os.path.dirname(folder.rstrip("\\/")), name + ".step")
            jobs[name] = (name, os.path.abspath(model), os.path.abspath(step) if os.path.exists(step) else None, os.path.abspath(out), seconds)
    started, matching, hung = time.time(), 0, 0
    with concurrent.futures.ThreadPoolExecutor(workers) as pool:
        for name, r, took in pool.map(lambda j: one(*j), jobs.values()):
            if r is None:
                hung += 1
                print("%-34s HUNG after %d s" % (name[:34], took), flush=True)
                continue
            matching += bool(r.get("rebuild_matches_solid"))
            features = r.get("features", [])
            bad = ["%s: %s %s" % (f["name"], f["status"], str(f.get("why") or "")[:70]) for f in features if f["status"] not in ("ok", "cosmetic", "not computed", "rolled back")]
            print("%-34s %-7s %3d s  vol %s vs %s  %d of %d features ok %s" % (name[:34], "MATCH" if r.get("rebuild_matches_solid") else "differs", took, round((r.get("result") or {}).get("volume") or 0, 1), round((r.get("step") or {}).get("volume") or 0, 1), len(features) - len(bad), len(features), r.get("fatal") or ""), flush=True)
            for b in bad:
                print("      " + b, flush=True)
    print("%d parts: %d match, %d hung, in %d s" % (len(jobs), matching, hung, time.time() - started))


main()
