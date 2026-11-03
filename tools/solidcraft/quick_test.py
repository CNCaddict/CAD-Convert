# Tries a change to build_script.py on parts already decoded: the SolidCraft rebuild only, checked against Inventor's solid
# as build_part.py checks it, with no import fallback and no viewer file.
#   python quick_test.py <results folder> <reports folder>[;<reports folder>...] [--seconds N] [--workers N] [part names, or all parts]
# Run with FreeCAD's python.exe (FreeCADCmd beside it, or CADCONVERT_FREECAD). Each part gets its own job object, so a part
# that hangs is ended along with the SolidCraft runs it started.
import concurrent.futures, ctypes, glob, json, os, subprocess, sys, time
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

TOOLS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FREECAD = os.environ.get("CADCONVERT_FREECAD") or os.path.join(os.path.dirname(sys.executable), "FreeCADCmd.exe")
kernel = ctypes.windll.kernel32
kernel.CreateJobObjectW.restype = ctypes.c_void_p
kernel.OpenProcess.restype = ctypes.c_void_p
kernel.AssignProcessToJobObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
kernel.TerminateJobObject.argtypes = [ctypes.c_void_p, ctypes.c_uint]
kernel.CloseHandle.argtypes = [ctypes.c_void_p]


def one(name, model, step, out, seconds):
    folder = os.path.join(out, name)
    os.makedirs(folder, exist_ok=True)
    report = os.path.join(folder, "result.json")
    if os.path.exists(report):
        os.remove(report)
    env = dict(os.environ, CADCONVERT_MODEL=model, CADCONVERT_OUT=os.path.join(out, name + ".sc"), CADCONVERT_REPORT=report, CADCONVERT_VIEW=os.path.join(folder, "view"),
               CADCONVERT_TOOLS=TOOLS, CADCONVERT_REBUILD_ONLY="1", FREECAD_USER_HOME=os.path.join(out, "home", str(os.getpid())))
    if step:
        env["CADCONVERT_STEP"] = step
    started = time.time()
    job = kernel.CreateJobObjectW(None, None)
    p = subprocess.Popen([FREECAD, os.path.join(TOOLS, "solidcraft", "build_part.py")], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
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
    args, seconds, workers, names = sys.argv[1:], 300, 8, []
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
                m = json.load(fh)
            if "occurrences" in m or not m.get("features"):
                continue
            step = os.path.join(os.path.dirname(folder.rstrip("\\/")), name + ".step")
            jobs[name] = (name, os.path.abspath(model), os.path.abspath(step) if os.path.exists(step) else None, os.path.abspath(out), seconds)
    started, matching, hung = time.time(), 0, 0
    summary = {}
    with concurrent.futures.ThreadPoolExecutor(workers) as pool:
        for name, r, took in pool.map(lambda j: one(*j), jobs.values()):
            if r is None:
                hung += 1
                summary[name] = "HUNG"
                print("%-34s HUNG after %d s" % (name[:34], took), flush=True)
                continue
            c = r.get("rebuild") or {}
            match = r.get("outcome") == "rebuilt"
            matching += match
            summary[name] = "MATCH" if match else "differs" if c else "no solid"
            features = [x for x in r.get("left", []) if x.get("feature")]
            print("%-34s %-8s %3d s  vol off %s%%  far %s/%s  %d features left out %s" % (name[:34], summary[name], took, None if c.get("volume_off") is None else round(c["volume_off"], 3),
                                                                                 c.get("far"), c.get("seen"), sum(not x.get("approximate") for x in features), r.get("fatal") or ""), flush=True)
            for x in features:
                print("      %s%s: %s" % ("~ " if x.get("approximate") else "", x["feature"], str(x["why"])[:90]), flush=True)
    with open(os.path.join(out, "summary.json"), "w", encoding="utf-8") as fh:
        json.dump(summary, fh, indent=0, sort_keys=True)
    print("%d parts with history: %d match, %d hung, in %d s" % (len(jobs), matching, hung, time.time() - started))


main()
