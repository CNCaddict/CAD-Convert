# Reads STEP files with FreeCAD's own reader and reports each one's solid. CADCONVERT_LIST: a file with one path per line.
import json, os
import FreeCAD as App
import Part

out = {}
with open(os.environ["CADCONVERT_LIST"], encoding="utf-8") as fh:
    paths = [l.strip() for l in fh if l.strip()]
for p in paths:
    try:
        s = Part.read(p)
        b = s.BoundBox
        out[p] = {"volume": s.Volume, "valid": s.isValid(), "solids": len(s.Solids), "shells": len(s.Shells), "faces": len(s.Faces), "edges": len(s.Edges), "vertices": len(s.Vertexes),
                  "bbox": [b.XMin, b.YMin, b.ZMin, b.XMax, b.YMax, b.ZMax], "closed": all(x.isClosed() for x in s.Shells) if s.Shells else False}
    except Exception as ex:
        out[p] = {"error": str(ex)[:200]}
with open(os.environ["CADCONVERT_REPORT"], "w", encoding="utf-8") as fh:
    json.dump(out, fh, indent=1)
