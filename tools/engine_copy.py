# Copies the parts of a portable FreeCAD the converter uses into the package's engine folder (run by package.cmd with that
# FreeCAD's own Python):  python engine_copy.py <FreeCAD folder> <engine folder> <engine_keep.txt>
# Binaries (.dll .pyd .exe) only if engine_keep.txt lists them: the ones seen loaded. Python's standard library whole, the
# workbenches below whole, and of the other Python packages only those something imported. Data files stay, less the big
# folders nothing here reads. Compiled .pyc caches aren't copied; Python remakes them on first use.
import os, shutil, sys

WORKBENCHES = {"assembly", "import", "material", "measure", "mesh", "meshpart", "part", "partdesign", "show", "sketcher", "spreadsheet", "start", "surface", "tux"}
SKIP_DATA = {"share/doc", "share/gir-1.0", "share/man", "share/locale", "share/proj", "lib/cmake", "lib/vtk-9.3", "lib/tcl8.6", "lib/tk8.6", "data/examples", "include", "doc/html", "bin/scripts"}
STDLIB_SKIP = {"test", "idlelib", "tkinter", "turtledemo", "ensurepip", "lib2to3", "venv"}
BINARY = (".dll", ".pyd", ".exe")


def main(src, dest, listing):
    keep = set()
    with open(listing, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if line and not line.startswith("#"):
                keep.add(line.lower())
    packages = {k.split("/")[3] for k in keep if k.startswith("bin/lib/site-packages/") and k.count("/") > 3}
    copied = size = 0
    for root, dirs, files in os.walk(src):
        rel = os.path.relpath(root, src).replace(os.sep, "/").lower()
        rel = "" if rel == "." else rel
        parts = rel.split("/") if rel else []
        dirs[:] = [d for d in dirs if d != "__pycache__" and ((rel + "/" if rel else "") + d.lower()) not in SKIP_DATA]
        if len(parts) >= 2 and parts[0] == "mod" and parts[1] not in WORKBENCHES:
            dirs[:] = []
            continue
        if rel.startswith("bin/lib/site-packages/") and parts[3].split(".")[0] not in {p.split(".")[0] for p in packages}:
            dirs[:] = []
            continue
        if len(parts) >= 3 and parts[:2] == ["bin", "lib"] and parts[2] in STDLIB_SKIP:
            dirs[:] = []
            continue
        for f in files:
            path = (rel + "/" if rel else "") + f.lower()
            if f.lower().endswith(BINARY) and path not in keep:
                continue
            if f.lower().endswith((".pyc", ".pdb", ".lib")):
                continue
            # Loose files in site-packages belong to packages; keep only imported ones (and binaries listed above).
            if rel == "bin/lib/site-packages" and f.lower().endswith(".py") and path not in keep:
                continue
            target = os.path.join(dest, os.path.relpath(os.path.join(root, f), src))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copy2(os.path.join(root, f), target)
            copied += 1
            size += os.path.getsize(target)
    print("engine: %d files, %.0f MB" % (copied, size / 1e6))


if __name__ == "__main__":
    main(*sys.argv[1:4])
