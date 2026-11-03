# The finishing pass, run by FreeCAD's GUI program with no screen (invre finish): FreeCADCmd has no GUI, so the files it
# saves say nothing about what shows, and they open with everything hidden. This opens each one, shows what should show,
# fits the view and saves, so FreeCAD writes its own display data. Nothing about the model changes.
#   CADCONVERT_LIST  a file with one .FCStd per line     CADCONVERT_REPORT  written as it goes, "done" last
import json, os
import FreeCAD as App, FreeCADGui as Gui

status = {"files": {}, "done": False}
App.ParamGet("User parameter:BaseApp/Preferences/Document").SetBool("CreateBackupFiles", False)


def flush():
    with open(os.environ["CADCONVERT_REPORT"], "w", encoding="utf-8") as fh:
        json.dump(status, fh, indent=1)


def hidden_occurrences(path):
    model = os.path.join(os.path.dirname(path), "reports", os.path.splitext(os.path.basename(path))[0] + ".model.json")
    try:
        with open(model, encoding="utf-8") as fh:
            return {o["name"] for o in json.load(fh).get("occurrences", []) if not o.get("visible", True)}
    except Exception:
        return set()


def to_show(doc, path):
    assembly = doc.getObject("Assembly")
    if assembly is not None:
        hidden = hidden_occurrences(path)
        return [o for o in doc.Objects if o.TypeId in ("App::Part", "Assembly::AssemblyObject") or (o.TypeId == "App::Link" and o.Label not in hidden)]
    frozen = doc.getObject("Inventor_solid")
    if frozen is not None:
        return [frozen]
    shown = []
    for b in doc.Objects:
        if b.TypeId == "PartDesign::Body" and not b.Label.endswith(" tool"):
            shown.append(b)
            if b.Tip is not None:
                shown.append(b.Tip)
    return shown


def finish(path):
    before = set(App.listDocuments().keys())
    doc = App.openDocument(path)
    try:
        Gui.updateGui()
        g = Gui.getDocument(doc.Name)
        shown = {o.Name for o in to_show(doc, path)}
        for o in doc.Objects:
            vp = g.getObject(o.Name)
            if vp is not None:
                vp.Visibility = o.Name in shown
                # A joint made with no window has no view of its own: this gives it its icon and lets it be edited.
                if hasattr(o, "JointType") or hasattr(o, "ObjectToGround"):
                    import JointObject
                    if not isinstance(vp.Proxy, (JointObject.ViewProviderJoint, JointObject.ViewProviderGroundedJoint)):
                        (JointObject.ViewProviderJoint if hasattr(o, "JointType") else JointObject.ViewProviderGroundedJoint)(vp)
        Gui.updateGui()
        view = g.activeView()
        if view is not None:
            view.viewIsometric()
            view.fitAll()
            Gui.updateGui()
        doc.save()
    finally:
        for name in list(App.listDocuments().keys()):
            if name not in before:
                App.closeDocument(name)
    return "ok"


with open(os.environ["CADCONVERT_LIST"], encoding="utf-8") as fh:
    paths = [l.strip() for l in fh if l.strip()]
for p in paths:
    # A second try: a file an assembly had just linked to can still be on its way out when it's opened again.
    for attempt in (1, 2):
        try:
            status["files"][p] = finish(p)
            break
        except Exception as ex:
            status["files"][p] = "%s: %s" % (type(ex).__name__, str(ex)[:160])
    flush()
status["done"] = True
flush()
if os.environ.get("CADCONVERT_FINISHING") == "1":
    Gui.getMainWindow().close()
