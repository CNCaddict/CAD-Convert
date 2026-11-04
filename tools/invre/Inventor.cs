// Drives Inventor through its COM API (late-bound) to make controlled test files,
// and reports what the API says a file contains ("truth") for checking decoders.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace InvRe
{
    public static class InventorDriver
    {
        // Enum values read from Autodesk.Inventor.Interop (Inventor 2026).
        const int kPartDocumentObject = 12290, kAssemblyDocumentObject = 12291;
        const int kNoInference = 24833, kInferredLine = 24834, kUndirectedSolution = 78594, kAlignedSolutionType = 115458, kPlanarFaceCenterPointIntent = 57868;
        const int kRigidJointType = 102401, kRotationalJointType = 102402, kSlideJointType = 102403, kCylindricalJointType = 102404, kPlanarJointType = 102405, kBallJointType = 102406;
        const int kJoinOperation = 20481, kCutOperation = 20482, kIntersectOperation = 20483;
        const int kSurfaceOperation = 20484, kNewBodyOperation = 20485;
        const int kPositiveExtentDirection = 20993;
        const int kNegativeExtentDirection = 20994;
        const int kSymmetricExtentDirection = 20995;
        const int kHorizontalDim = 19201;
        const int kVerticalDim = 19202;
        const int kAlignedDim = 19203;
        const int kDefault = 33537, kFitted = 33538, kAdjustToDirection1 = 33794;
        const int kIdenticalCompute = 47361, kAdjustToModelCompute = 47362;
        const int kInsideShellDirection = 41217, kOutsideShellDirection = 41218, kBothSidesShellDirection = 41219;
        const int kSmoothSplineFit = 26369, kSweetSplineFit = 26370, kACADSplineFit = 26371;
        const int kHeightDatumOuter = 75521;
        const int kNormalToPath = 59649, kParallelToOriginalProfile = 59650, kPathSweepType = 104449, kDirectionLoftCondition = 34307;
        const int kTangentLoftCondition = 34306, kSmoothLoftCondition = 34308, kTangentToPlaneLoftCondition = 34310;

        public static string Template = @"C:\Users\Public\Documents\Autodesk\Inventor 2026\Templates\en-US\Metric\Standard (mm).ipt";
        const string InteropPath = @"C:\Program Files\Autodesk\Inventor 2026\Bin\Public Assemblies\Autodesk.Inventor.Interop.dll";

        const string InchTemplate = @"C:\Users\Public\Documents\Autodesk\Inventor 2026\Templates\en-US\English\Standard (in).ipt";
        const string SheetTemplate = @"C:\Users\Public\Documents\Autodesk\Inventor 2026\Templates\en-US\Metric\Sheet Metal (mm).ipt";
        const string AsmTemplate = @"C:\Users\Public\Documents\Autodesk\Inventor 2026\Templates\en-US\Metric\Standard (mm).iam";

        class Variant
        {
            public string Name;
            public Action<dynamic, dynamic> Build;   // (app, partDocument)
            public string Template;                  // null = the mm template
        }

        static Variant V(string name, Action<dynamic, dynamic> build, string template = null)
        {
            return new Variant { Name = name, Build = build, Template = template };
        }

        // Each experiment is a set of parts that differ in exactly one thing.
        static SortedDictionary<string, List<Variant>> Experiments()
        {
            SortedDictionary<string, List<Variant>> x = new SortedDictionary<string, List<Variant>>();
            Action<dynamic, dynamic> empty = (app, doc) => { };
            x["noise"] = new List<Variant> { V("a", empty), V("b", empty) };
            x["circle-diameter"] = new List<Variant> {
                V("d10", (app, doc) => Circle(app, doc, "10 mm")),
                V("d11", (app, doc) => Circle(app, doc, "11 mm")),
                V("d12_5", (app, doc) => Circle(app, doc, "12.5 mm")) };
            x["extrude-distance"] = new List<Variant> {
                V("L10", (app, doc) => Extrude(app, doc, "10 mm", "10 mm", kPositiveExtentDirection)),
                V("L11", (app, doc) => Extrude(app, doc, "10 mm", "11 mm", kPositiveExtentDirection)),
                V("L20", (app, doc) => Extrude(app, doc, "10 mm", "20 mm", kPositiveExtentDirection)) };
            x["extrude-direction"] = new List<Variant> {
                V("pos", (app, doc) => Extrude(app, doc, "10 mm", "10 mm", kPositiveExtentDirection)),
                V("neg", (app, doc) => Extrude(app, doc, "10 mm", "10 mm", kNegativeExtentDirection)),
                V("sym", (app, doc) => Extrude(app, doc, "10 mm", "10 mm", kSymmetricExtentDirection)) };
            x["user-parameter"] = new List<Variant> {
                V("literal", (app, doc) => Extrude(app, doc, "10 mm", "25 mm", kPositiveExtentDirection)),
                V("param", (app, doc) => {
                    doc.ComponentDefinition.Parameters.UserParameters.AddByExpression("Len", "25 mm", "mm");
                    Extrude(app, doc, "10 mm", "Len", kPositiveExtentDirection);
                }) };

            // Every variant has Alpha = Bravo = 25 mm, so the extrusion's value never changes; only the formula does.
            x["formula"] = new List<Variant> {
                V("lit", (app, doc) => Formula(app, doc, "25 mm", "25 mm")),
                V("alpha", (app, doc) => Formula(app, doc, "Alpha", "25 mm")),
                V("bravo", (app, doc) => Formula(app, doc, "Bravo", "25 mm")),
                V("mul1", (app, doc) => Formula(app, doc, "Alpha * 1", "25 mm")),
                V("sum", (app, doc) => Formula(app, doc, "Alpha + Bravo - 25 mm", "25 mm")),
                V("func", (app, doc) => Formula(app, doc, "sqrt(Alpha * Bravo)", "25 mm")),
                V("chain", (app, doc) => Formula(app, doc, "Bravo", "Alpha")) };

            // One entity alone in a sketch on the XY plane.
            x["sketch-entity"] = new List<Variant> {
                V("empty", (app, doc) => { NewSketch(doc); }),
                V("point", (app, doc) => { NewSketch(doc).SketchPoints.Add(P(app, 1, 2), false); }),
                V("line", (app, doc) => { NewSketch(doc).SketchLines.AddByTwoPoints(P(app, 0, 0), P(app, 1, 2)); }),
                V("circle", (app, doc) => { NewSketch(doc).SketchCircles.AddByCenterRadius(P(app, 1, 2), 0.5); }),
                V("arc", (app, doc) => { NewSketch(doc).SketchArcs.AddByCenterStartEndPoint(P(app, 1, 2), P(app, 2, 2), P(app, 1, 3), true); }) };

            // Same line, one coordinate changed (values in cm).
            x["sketch-line"] = new List<Variant> {
                V("l12", (app, doc) => { NewSketch(doc).SketchLines.AddByTwoPoints(P(app, 0, 0), P(app, 1, 2)); }),
                V("l13", (app, doc) => { NewSketch(doc).SketchLines.AddByTwoPoints(P(app, 0, 0), P(app, 1, 3)); }),
                V("s05", (app, doc) => { NewSketch(doc).SketchLines.AddByTwoPoints(P(app, 0.5, 0), P(app, 1, 2)); }) };

            // Three lines already in their constrained positions, so adding a constraint moves nothing:
            // l1 (0,0)-(2,0), l2 (2,0)-(2,2), l3 (0,2)-(2,2).
            x["sketch-constraint"] = new List<Variant> {
                V("none", (app, doc) => { Lines(app, doc); }),
                V("horizontal", (app, doc) => { dynamic[] s = Lines(app, doc); s[0].GeometricConstraints.AddHorizontal(s[1]); }),
                V("vertical", (app, doc) => { dynamic[] s = Lines(app, doc); s[0].GeometricConstraints.AddVertical(s[2]); }),
                V("perpendicular", (app, doc) => { dynamic[] s = Lines(app, doc); s[0].GeometricConstraints.AddPerpendicular(s[1], s[2]); }),
                V("parallel", (app, doc) => { dynamic[] s = Lines(app, doc); s[0].GeometricConstraints.AddParallel(s[1], s[3]); }),
                V("equal", (app, doc) => { dynamic[] s = Lines(app, doc); s[0].GeometricConstraints.AddEqualLength(s[1], s[2]); }),
                V("fix", (app, doc) => { dynamic[] s = Lines(app, doc); s[0].GeometricConstraints.AddGround(s[1]); }),
                V("dim", (app, doc) => {
                    dynamic[] s = Lines(app, doc);
                    s[0].DimensionConstraints.AddTwoPointDistance(s[1].StartSketchPoint, s[1].EndSketchPoint, kAlignedDim, P(app, 1, -0.5));
                }),
                V("hv", (app, doc) => {
                    dynamic[] s = Lines(app, doc);
                    s[0].GeometricConstraints.AddHorizontal(s[1]);
                    s[0].GeometricConstraints.AddVertical(s[2]);
                }) };

            // A richer sketch where every constraint below is already satisfied (see Shapes for the geometry).
            Func<Action<dynamic, dynamic, dynamic>, Action<dynamic, dynamic>> on = add => (app, doc) => {
                Dictionary<string, dynamic> s = Shapes(app, doc);
                add(s["sketch"].GeometricConstraints, s["sketch"].DimensionConstraints, s);
            };
            x["sketch-constraint2"] = new List<Variant> {
                V("base", on((g, dm, s) => { })),
                V("point-on-line", on((g, dm, s) => g.AddCoincident(s["p"], s["l1"]))),
                V("merge", on((g, dm, s) => s["l1"].EndSketchPoint.Merge(s["l2"].StartSketchPoint))),
                V("collinear", on((g, dm, s) => g.AddCollinear(s["l1"], s["l4"]))),
                V("tangent", on((g, dm, s) => g.AddTangent(s["l5"], s["c3"]))),
                V("concentric", on((g, dm, s) => g.AddConcentric(s["c1"], s["c3"]))),
                V("equal-radius", on((g, dm, s) => g.AddEqualRadius(s["c1"], s["c2"]))),
                V("midpoint", on((g, dm, s) => g.AddMidpoint(s["p"], s["l1"]))),
                V("symmetry", on((g, dm, s) => g.AddSymmetry(s["l7"], s["l2"], s["l6"]))),
                V("dim-horizontal", on((g, dm, s) => dm.AddTwoPointDistance(s["l1"].StartSketchPoint, s["l1"].EndSketchPoint, kHorizontalDim, P(s["app"], 1, -0.5)))),
                V("dim-vertical", on((g, dm, s) => dm.AddTwoPointDistance(s["l2"].StartSketchPoint, s["l2"].EndSketchPoint, kVerticalDim, P(s["app"], 2.5, 1)))),
                V("dim-aligned", on((g, dm, s) => dm.AddTwoPointDistance(s["l2"].StartSketchPoint, s["l2"].EndSketchPoint, kAlignedDim, P(s["app"], 2.5, 1)))),
                V("dim-angle", on((g, dm, s) => dm.AddTwoLineAngle(s["l1"], s["l2"], P(s["app"], 1.5, 0.5)))),
                V("dim-radius", on((g, dm, s) => dm.AddRadius(s["c1"], P(s["app"], 4.5, 1.5)))),
                V("dim-diameter", on((g, dm, s) => dm.AddDiameter(s["c1"], P(s["app"], 4.5, 1.5)))) };

            // A 10 mm circle extruded 5 mm from the XY plane into a 20 x 20 x 10 mm block (z 0..10), one setting changed.
            Action<dynamic, dynamic> five = (def, doc) => def.SetDistanceExtent("5 mm", kPositiveExtentDirection);
            x["extrude-op"] = new List<Variant> {
                V("join", Block(kJoinOperation, five)),
                V("cut", Block(kCutOperation, five)),
                V("intersect", Block(kIntersectOperation, five)),
                V("newbody", Block(kNewBodyOperation, five)),
                V("surface", Block(kSurfaceOperation, five)) };
            x["extrude-extent"] = new List<Variant> {
                V("distance", Block(kCutOperation, five)),
                V("through-all", Block(kCutOperation, (def, doc) => def.SetThroughAllExtent(kPositiveExtentDirection))),
                V("through-all-sym", Block(kCutOperation, (def, doc) => def.SetThroughAllExtent(kSymmetricExtentDirection))),
                V("to-next", Block(kCutOperation, (def, doc) => def.SetToNextExtent(kPositiveExtentDirection, doc.ComponentDefinition.SurfaceBodies[1]))),
                V("to-plane", Block(kJoinOperation, (def, doc) => def.SetToExtent(OffsetPlane(doc, "20 mm"), false))),
                V("to-face", Block(kCutOperation, (def, doc) => def.SetToExtent(PickFace(FaceAt(doc, 1.0)), false))),
                V("from-to", Block(kJoinOperation, (def, doc) => def.SetFromToExtent(OffsetPlane(doc, "15 mm"), false, OffsetPlane(doc, "20 mm"), false))),
                V("two-distances", Block(kCutOperation, (def, doc) => {
                    def.SetDistanceExtent("5 mm", kPositiveExtentDirection);
                    def.SetDistanceExtentTwo("3 mm");
                })),
                V("taper", Block(kCutOperation, (def, doc) => {
                    def.SetDistanceExtent("5 mm", kPositiveExtentDirection);
                    def.TaperAngle = "5 deg";
                })) };

            // A 5 x 5 mm square (x 5..10, y 0..5 mm) on the XY plane revolved about the Y axis, one setting changed.
            x["revolve"] = new List<Variant> {
                V("full", Ring((f, pr, ax) => f.AddFull(pr, ax, kNewBodyOperation))),
                V("angle", Ring((f, pr, ax) => f.AddByAngle(pr, ax, "90 deg", kPositiveExtentDirection, kNewBodyOperation))),
                V("angle-neg", Ring((f, pr, ax) => f.AddByAngle(pr, ax, "90 deg", kNegativeExtentDirection, kNewBodyOperation))),
                V("angle-sym", Ring((f, pr, ax) => f.AddByAngle(pr, ax, "90 deg", kSymmetricExtentDirection, kNewBodyOperation))),
                V("line-axis", Ring((f, pr, ax) => f.AddFull(pr, ax, kNewBodyOperation), axis: "line")),
                V("x-axis", Ring((f, pr, ax) => f.AddFull(pr, ax, kNewBodyOperation), axis: "x")),
                V("cut", Ring((f, pr, ax) => f.AddFull(pr, ax, kCutOperation), slab: true)),
                V("join", Ring((f, pr, ax) => f.AddByAngle(pr, ax, "45 deg", kPositiveExtentDirection, kJoinOperation), slab: true)) };

            // A 20 x 20 x 10 mm slab (z -5..5 mm) with a hole centre at (0, 0) on the XY plane. Every size differs, so
            // each parameter slot can be told apart: diameter 5, depth 6, head 9/2 (cbore), 8/82 deg (csink), 11/1 (spot face).
            x["hole"] = new List<Variant> {
                V("drilled", Drill((h, pl, app) => h.AddDrilledByDistanceExtent(pl, "5 mm", "6 mm", kPositiveExtentDirection))),
                V("drilled-neg", Drill((h, pl, app) => h.AddDrilledByDistanceExtent(pl, "5 mm", "6 mm", kNegativeExtentDirection))),
                V("flat", Drill((h, pl, app) => h.AddDrilledByDistanceExtent(pl, "5 mm", "6 mm", kPositiveExtentDirection, true))),
                V("tip", Drill((h, pl, app) => h.AddDrilledByDistanceExtent(pl, "5 mm", "6 mm", kPositiveExtentDirection, false, "120 deg"))),
                V("through", Drill((h, pl, app) => h.AddDrilledByThroughAllExtent(pl, "5 mm", kPositiveExtentDirection))),
                V("cbore", Drill((h, pl, app) => h.AddCBoreByDistanceExtent(pl, "5 mm", "6 mm", kPositiveExtentDirection, "9 mm", "2 mm"))),
                V("csink", Drill((h, pl, app) => h.AddCSinkByDistanceExtent(pl, "5 mm", "6 mm", kPositiveExtentDirection, "8 mm", "82 deg"))),
                V("spotface", Drill((h, pl, app) => h.AddSpotFaceByDistanceExtent(pl, "5 mm", "6 mm", kPositiveExtentDirection, "11 mm", "1 mm"))),
                V("tapped", Drill((h, pl, app) => h.AddDrilledByThroughAllExtent(pl,
                    h.CreateTapInfo(true, "ANSI Metric M Profile", "M6x1", "6H", true), kPositiveExtentDirection))),
                V("tapped-depth", Drill((h, pl, app) => h.AddDrilledByDistanceExtent(pl,
                    h.CreateTapInfo(true, "ANSI Metric M Profile", "M6x1", "6H", false, "4 mm"), "6 mm", kPositiveExtentDirection))),
                V("left-hand", Drill((h, pl, app) => h.AddDrilledByThroughAllExtent(pl,
                    h.CreateTapInfo(false, "ANSI Metric M Profile", "M6x1", "6H", true), kPositiveExtentDirection))),
                V("two-points", Drill((h, pl, app) => h.AddDrilledByDistanceExtent(pl, "5 mm", "6 mm", kPositiveExtentDirection), two: true)) };

            // Holes not placed on sketch points: on the slab's top face (z = 5 mm) by distances from two edges, at a
            // work point along the Z axis, and concentric with the top edge of a 20 mm cylinder.
            x["hole-placement"] = new List<Variant> {
                V("linear", (app, doc) => {
                    Slab(app, doc, 1, "10 mm");
                    dynamic face = FaceAt(doc, 0.5), holes = doc.ComponentDefinition.Features.HoleFeatures;
                    holes.AddDrilledByDistanceExtent(holes.CreateLinearPlacementDefinition(face, PickEdge(EdgeAlong(face, true)), "5 mm",
                        PickEdge(EdgeAlong(face, false)), "6 mm", app.TransientGeometry.CreatePoint(0, 0, 0.5)), "5 mm", "6 mm", kPositiveExtentDirection);
                }),
                V("point", (app, doc) => {
                    Slab(app, doc, 1, "10 mm");
                    dynamic cd = doc.ComponentDefinition, holes = cd.Features.HoleFeatures;
                    dynamic wp = cd.WorkPoints.AddFixed(app.TransientGeometry.CreatePoint(0.3, 0.2, 0.5));
                    holes.AddDrilledByDistanceExtent(holes.CreatePointPlacementDefinition(wp, cd.WorkAxes[3]), "5 mm", "6 mm", kPositiveExtentDirection);
                }),
                V("concentric", (app, doc) => {
                    dynamic sk = NewSketch(doc), features = doc.ComponentDefinition.Features;
                    sk.SketchCircles.AddByCenterRadius(P(app, 0, 0), 1);
                    dynamic def = features.ExtrudeFeatures.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kNewBodyOperation);
                    def.SetDistanceExtent("10 mm", kSymmetricExtentDirection);
                    features.ExtrudeFeatures.Add(def);
                    dynamic face = FaceAt(doc, 0.5), holes = features.HoleFeatures;
                    holes.AddDrilledByDistanceExtent(holes.CreateConcentricPlacementDefinition(face, PickEdge(face.Edges[1])), "5 mm", "6 mm", kPositiveExtentDirection);
                }) };

            // A 20 x 20 x 10 mm slab (z -5..5 mm) with its top edge along X filleted, or other edges; one option changed.
            // Options in AddSimple order: AllFillets, AllRounds, AutomaticEdgeChain, RollAlongSharpEdges,
            // RollingBallWherePossible, PreserveAllFeatures.
            x["fillet"] = new List<Variant> {
                V("edge", Edges((f, app, doc) => f.FilletFeatures.AddSimple(TopEdges(app, doc, false), "2 mm"))),
                V("edges4", Edges((f, app, doc) => f.FilletFeatures.AddSimple(TopEdges(app, doc, true), "2 mm"))),
                V("two-sets", Edges((f, app, doc) => {
                    dynamic def = f.FilletFeatures.CreateFilletDefinition();
                    def.AddConstantRadiusEdgeSet(TopEdges(app, doc, false), "2 mm");
                    def.AddConstantRadiusEdgeSet(EdgeSet(app, EdgeAlong(FaceAt(doc, -0.5), false)), "3 mm");
                    f.FilletFeatures.Add(def);
                })),
                V("no-chain", Edges((f, app, doc) => f.FilletFeatures.AddSimple(TopEdges(app, doc, false), "2 mm", false, false, false, false, true, false))),
                V("roll-sharp", Edges((f, app, doc) => f.FilletFeatures.AddSimple(TopEdges(app, doc, false), "2 mm", false, false, true, true, true, false))),
                V("no-rolling-ball", Edges((f, app, doc) => f.FilletFeatures.AddSimple(TopEdges(app, doc, false), "2 mm", false, false, true, false, false, false))),
                V("preserve", Edges((f, app, doc) => f.FilletFeatures.AddSimple(TopEdges(app, doc, false), "2 mm", false, false, true, false, true, true))),
                V("all-rounds", Edges((f, app, doc) => f.FilletFeatures.AddSimple(TopEdges(app, doc, false), "2 mm", false, true, true, false, true, false))),
                V("no-smooth", Edges((f, app, doc) => {
                    dynamic def = f.FilletFeatures.CreateFilletDefinition();
                    def.AddConstantRadiusEdgeSet(TopEdges(app, doc, false), "2 mm");
                    f.FilletFeatures.Add(def, true, false, false, true, false);
                })) };

            // Same slab; the chamfer's distance is measured on the top face where a face is needed.
            x["chamfer"] = new List<Variant> {
                V("distance", Edges((f, app, doc) => f.ChamferFeatures.AddUsingDistance(TopEdges(app, doc, false), "1 mm"))),
                V("edges4", Edges((f, app, doc) => f.ChamferFeatures.AddUsingDistance(TopEdges(app, doc, true), "1 mm"))),
                V("distance-angle", Edges((f, app, doc) => f.ChamferFeatures.AddUsingDistanceAndAngle(TopEdges(app, doc, false), PickFace(FaceAt(doc, 0.5)), "1.5 mm", "30 deg"))),
                V("two-distances", Edges((f, app, doc) => f.ChamferFeatures.AddUsingTwoDistances(TopEdges(app, doc, false), PickFace(FaceAt(doc, 0.5)), "1 mm", "2 mm"))),
                // Defaults (from "distance"): edge chain on, corner setback on, preserve features off.
                V("no-setback", Edges((f, app, doc) => f.ChamferFeatures.AddUsingDistance(TopEdges(app, doc, true), "1 mm", true, false, false))),
                V("no-chain", Edges((f, app, doc) => f.ChamferFeatures.AddUsingDistance(TopEdges(app, doc, false), "1 mm", false, true, false))),
                V("preserve", Edges((f, app, doc) => f.ChamferFeatures.AddUsingDistance(TopEdges(app, doc, false), "1 mm", true, true, true))),
                // A rod (radius 10 mm, y 0..20 mm, revolved about Y) with a cone recess in its end (4 mm across, 3 mm
                // deep), chamfered 6 mm at 45 deg: exactly the width of the flat ring round the recess, as on a set
                // screw's cup point. FreeCAD's chamfer at full size caps the recess.
                V("whole-face", (app, doc) => {
                    dynamic sk = NewSketch(doc);
                    dynamic l = sk.SketchLines;
                    dynamic[] p = { P(app, 0, 0.3), P(app, 0.4, 0), P(app, 1, 0), P(app, 1, 2), P(app, 0, 2) };
                    // Each line from the last one's end, so the outline closes.
                    dynamic first = l.AddByTwoPoints(p[0], p[1]), last = first;
                    for (int i = 2; i < p.Length; i++) last = l.AddByTwoPoints(last.EndSketchPoint, p[i]);
                    l.AddByTwoPoints(last.EndSketchPoint, first.StartSketchPoint);
                    dynamic f = doc.ComponentDefinition.Features;
                    string step = "revolve";
                    try
                    {
                        f.RevolveFeatures.AddFull(sk.Profiles.AddForSolid(), doc.ComponentDefinition.WorkAxes[2], kNewBodyOperation);
                        step = "end face";
                        dynamic end = FaceAt(doc, 0.0, 1);
                        step = "edge";
                        dynamic edges = EdgeSet(app, RoundEdge(end, 1.0));
                        step = "chamfer";
                        f.ChamferFeatures.AddUsingDistanceAndAngle(edges, PickFace(end), "6 mm", "45 deg");
                    }
                    catch (Exception ex) { throw new Exception(step + ": " + ex.Message); }
                }) };

            // A 4 mm square on the XY plane swept along a line up Z (30 mm), or that plus a quarter arc of 10 mm radius
            // bending toward +X; one setting changed.
            x["sweep"] = new List<Variant> {
                V("line", Swept((s, pr, path, app, doc) => s.AddUsingPath(pr, path, kNewBodyOperation))),
                V("curved", Swept((s, pr, path, app, doc) => s.AddUsingPath(pr, path, kNewBodyOperation), curved: true)),
                V("taper", Swept((s, pr, path, app, doc) => s.AddUsingPath(pr, path, kNewBodyOperation, kNormalToPath, "2 deg"))),
                V("parallel", Swept((s, pr, path, app, doc) => s.AddUsingPath(pr, path, kNewBodyOperation, kParallelToOriginalProfile), curved: true)),
                V("twist", Swept((s, pr, path, app, doc) => {
                    dynamic def = s.CreateSweepDefinition(kPathSweepType, pr, path, kNewBodyOperation);
                    def.TwistAngle = "45 deg";
                    s.Add(def);
                })),
                V("cut", Swept((s, pr, path, app, doc) => s.AddUsingPath(pr, path, kCutOperation), block: true)),
                V("rail", Swept((s, pr, path, app, doc) => {
                    // A guide rail from the square's edge at x = 2 mm out to x = 4 mm at the top.
                    dynamic sk = doc.ComponentDefinition.Sketches.Add(doc.ComponentDefinition.WorkPlanes[2]);
                    dynamic tg = app.TransientGeometry;
                    dynamic rail = sk.SketchLines.AddByTwoPoints(sk.ModelToSketchSpace(tg.CreatePoint(0.2, 0, 0)), sk.ModelToSketchSpace(tg.CreatePoint(0.4, 0, 3)));
                    s.AddUsingPathAndGuideRail(pr, path, s.CreatePath(rail), kNewBodyOperation);
                })) };

            // Sections on planes up Z: a 20 mm circle at z = 0, then others; one setting changed.
            x["loft"] = new List<Variant> {
                V("two", Lofted((l, c, app, doc) => l.Add(l.CreateLoftDefinition(c, kNewBodyOperation)), new[] { "c 1 0", "c 0.5 2" })),
                V("three", Lofted((l, c, app, doc) => l.Add(l.CreateLoftDefinition(c, kNewBodyOperation)), new[] { "c 1 0", "s 0.8 1.5", "c 0.5 3" })),
                V("point", Lofted((l, c, app, doc) => l.Add(l.CreateLoftDefinition(c, kNewBodyOperation)), new[] { "c 1 0", "p 0 2" })),
                V("cut", Lofted((l, c, app, doc) => l.Add(l.CreateLoftDefinition(c, kCutOperation)), new[] { "c 1 0", "c 0.5 2" }, block: true)),
                V("angle", Lofted((l, c, app, doc) => {
                    dynamic def = l.CreateLoftDefinition(c, kNewBodyOperation);
                    def.FirstSectionCondition = kDirectionLoftCondition;
                    def.FirstSectionAngle = "60 deg";
                    def.FirstSectionImpact = "2 ul";
                    l.Add(def);
                }, new[] { "c 1 0", "c 0.5 2" })),
                // Up from a sketch on the top of a block, whose edges the sections' edges lie on: conditions that need neighbouring faces.
                // Inventor has refused smooth and tangent so far, so those two make no file.
                V("from-face", (app, doc) => LoftFromFace(app, doc, null)),
                V("smooth", (app, doc) => LoftFromFace(app, doc, (Action<dynamic>)(def => { def.FirstSectionCondition = kSmoothLoftCondition; }))),
                V("tangent", (app, doc) => LoftFromFace(app, doc, (Action<dynamic>)(def => { def.FirstSectionCondition = kTangentLoftCondition; def.FirstSectionImpact = "2 ul"; }))),
                // To a point: tangent to a plane through it.
                V("tangent-plane", Lofted((l, c, app, doc) => {
                    dynamic def = l.CreateLoftDefinition(c, kNewBodyOperation);
                    def.LastSectionCondition = kTangentToPlaneLoftCondition;
                    def.LastSectionTangentPlane = c[2].Parent.PlanarEntity;
                    def.LastSectionImpact = "3 ul";
                    l.Add(def);
                }, new[] { "c 1 0", "p 0 2" })),
                V("rail", Lofted((l, c, app, doc) => {
                    dynamic def = l.CreateLoftDefinition(c, kNewBodyOperation);
                    dynamic sk = doc.ComponentDefinition.Sketches.Add(doc.ComponentDefinition.WorkPlanes[2]);
                    dynamic tg = app.TransientGeometry;
                    sk.SketchLines.AddByTwoPoints(sk.ModelToSketchSpace(tg.CreatePoint(1, 0, 0)), sk.ModelToSketchSpace(tg.CreatePoint(0.5, 0, 2)));
                    def.LoftRails.Add(sk.Profiles.AddForSurface());
                    l.Add(def);
                }, new[] { "c 1 0", "c 0.5 2" })),
                V("center", Lofted((l, c, app, doc) => {
                    dynamic def = l.CreateLoftDefinition(c, kNewBodyOperation);
                    dynamic sk = doc.ComponentDefinition.Sketches.Add(doc.ComponentDefinition.WorkPlanes[2]);
                    dynamic tg = app.TransientGeometry;
                    sk.SketchLines.AddByTwoPoints(sk.ModelToSketchSpace(tg.CreatePoint(0, 0, 0)), sk.ModelToSketchSpace(tg.CreatePoint(0, 0, 2)));
                    def.Centerline = sk.Profiles.AddForSurface();
                    l.Add(def);
                }, new[] { "c 1 0", "c 0.5 2" })),
                V("merge", Lofted((l, c, app, doc) => {
                    dynamic def = l.CreateLoftDefinition(c, kNewBodyOperation);
                    def.MergeTangentFaces = true;
                    l.Add(def);
                }, new[] { "c 1 0", "c 0.5 2" })),
                // Three circles on planes through the Y axis, at 0, 120 and 240 deg, closed into a ring.
                V("closed", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, tg = app.TransientGeometry, sections = app.TransientObjects.CreateObjectCollection();
                    for (int k = 0; k < 3; k++)
                    {
                        double a = 2 * Math.PI * k / 3;
                        dynamic plane = k == 0 ? cd.WorkPlanes[3] : cd.WorkPlanes.AddByLinePlaneAndAngle(cd.WorkAxes[2], cd.WorkPlanes[3], (120 * k) + " deg");
                        dynamic sk = cd.Sketches.Add(plane);
                        // On the plane whichever way its angle turned.
                        double[] n = Xyz(plane.Plane.Normal);
                        double side = Math.Abs(n[0] * Math.Cos(a) + n[2] * Math.Sin(a)) < 1e-6 ? 1 : -1;
                        sk.SketchCircles.AddByCenterRadius(sk.ModelToSketchSpace(tg.CreatePoint(2 * Math.Cos(a), 0, side * 2 * Math.Sin(a))), 0.5);
                        sections.Add(sk.Profiles.AddForSolid());
                    }
                    dynamic lofts = cd.Features.LoftFeatures, def = lofts.CreateLoftDefinition(sections, kNewBodyOperation);
                    def.Closed = true;
                    lofts.Add(def);
                }) };

            // Sketch curves on the XY plane (cm): one kind per part.
            Func<dynamic, double[], dynamic> pts = (app, xy) => {
                dynamic c = app.TransientObjects.CreateObjectCollection();
                for (int i = 0; i + 1 < xy.Length; i += 2) c.Add(P(app, xy[i], xy[i + 1]));
                return c;
            };
            double[] fit3 = { 0, 0, 1, 1, 2, 0 }, fit5 = { 0, 0, 1, 1, 2, 0, 3, 1.5, 4, 0.5 };
            x["curve"] = new List<Variant> {
                V("spline3", (app, doc) => NewSketch(doc).SketchSplines.Add(pts(app, fit3))),
                V("spline5", (app, doc) => NewSketch(doc).SketchSplines.Add(pts(app, fit5))),
                V("sweet", (app, doc) => NewSketch(doc).SketchSplines.Add(pts(app, fit5), kSweetSplineFit)),
                V("acad", (app, doc) => NewSketch(doc).SketchSplines.Add(pts(app, fit5), kACADSplineFit)),
                V("smooth", (app, doc) => NewSketch(doc).SketchSplines.Add(pts(app, fit5), kSmoothSplineFit)),
                V("closed", (app, doc) => {
                    dynamic s = NewSketch(doc).SketchSplines.Add(pts(app, new double[] { 0, 0, 2, 0, 1, 2 }));
                    s.Closed = true;
                }),
                V("rational", (app, doc) => {
                    double[] poles = { 0, 0, 1, 1, 2, 1, 3, 0 }, knots = { 0, 0, 0, 0, 1, 1, 1, 1 }, weights = { 1, 2, 3, 1 };
                    NewSketch(doc).SketchFixedSplines.Add(app.TransientGeometry.CreateBSplineCurve2d(4, ref poles, ref knots, ref weights, false));
                }),
                V("fixed", (app, doc) => {
                    double[] poles = { 0, 0, 1, 1, 2, 1, 3, 0 }, knots = { 0, 0, 0, 0, 1, 1, 1, 1 }, weights = new double[0];
                    NewSketch(doc).SketchFixedSplines.Add(app.TransientGeometry.CreateBSplineCurve2d(4, ref poles, ref knots, ref weights, false));
                }),
                V("control", (app, doc) => NewSketch(doc).SketchControlPointSplines.Add(pts(app, fit5))),
                V("ellipse", (app, doc) => NewSketch(doc).SketchEllipses.Add(P(app, 1, 2), app.TransientGeometry.CreateUnitVector2d(1, 0), 1.5, 0.5)),
                V("ellipse-rot", (app, doc) => NewSketch(doc).SketchEllipses.Add(P(app, 1, 2), app.TransientGeometry.CreateUnitVector2d(0.6, 0.8), 1.5, 0.5)),
                V("ellipse-arc", (app, doc) => NewSketch(doc).SketchEllipticalArcs.Add(P(app, 1, 2), app.TransientGeometry.CreateUnitVector2d(1, 0), 1.5, 0.5, 0.2, 2.0)),
                // A closed spline extruded 10 mm; its top edge projected into a new sketch at z = 10 mm.
                V("projected-closed", (app, doc) => {
                    dynamic sk = NewSketch(doc), cd = doc.ComponentDefinition;
                    dynamic s = sk.SketchSplines.Add(pts(app, new double[] { 0, 0, 2, 0, 1, 2 }));
                    s.Closed = true;
                    dynamic ex = cd.Features.ExtrudeFeatures, def = ex.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kNewBodyOperation);
                    def.SetDistanceExtent("10 mm", kPositiveExtentDirection);
                    ex.Add(def);
                    dynamic second = cd.Sketches.Add(OffsetPlane(doc, "10 mm"));
                    foreach (dynamic e in cd.SurfaceBodies[1].Edges)
                        if (EnumName("CurveTypeEnum", (int)e.CurveType) == "kBSplineCurve" && Math.Abs((double)e.StartVertex.Point.Z - 1) < 1e-6) { second.AddByProjectingEntity(e); break; }
                }),
                // A spline closed by a line, extruded 10 mm; its top spline edge projected into a new sketch at z = 10 mm.
                V("projected", (app, doc) => {
                    dynamic sk = NewSketch(doc), cd = doc.ComponentDefinition;
                    dynamic s = sk.SketchSplines.Add(pts(app, fit3));
                    sk.SketchLines.AddByTwoPoints(s.EndSketchPoint, s.StartSketchPoint);
                    dynamic ex = cd.Features.ExtrudeFeatures, def = ex.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kNewBodyOperation);
                    def.SetDistanceExtent("10 mm", kPositiveExtentDirection);
                    ex.Add(def);
                    dynamic second = cd.Sketches.Add(OffsetPlane(doc, "10 mm"));
                    foreach (dynamic e in cd.SurfaceBodies[1].Edges)
                        if (EnumName("CurveTypeEnum", (int)e.CurveType) == "kBSplineCurve" && Math.Abs((double)e.StartVertex.Point.Z - 1) < 1e-6) { second.AddByProjectingEntity(e); break; }
                }),
                V("projected-ellipse", (app, doc) => {
                    dynamic sk = NewSketch(doc), cd = doc.ComponentDefinition;
                    sk.SketchEllipses.Add(P(app, 1, 2), app.TransientGeometry.CreateUnitVector2d(0.6, 0.8), 1.5, 0.5);
                    dynamic ex = cd.Features.ExtrudeFeatures, def = ex.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kNewBodyOperation);
                    def.SetDistanceExtent("10 mm", kPositiveExtentDirection);
                    ex.Add(def);
                    dynamic second = cd.Sketches.Add(OffsetPlane(doc, "10 mm"));
                    foreach (dynamic e in cd.SurfaceBodies[1].Edges) { second.AddByProjectingEntity(e); }
                }),
                V("text", (app, doc) => NewSketch(doc).TextBoxes.AddFitted(P(app, 1, 2), "ABC")),
                V("text-box", (app, doc) => NewSketch(doc).TextBoxes.AddByRectangle(P(app, 1, 3), P(app, 4, 2), "ABC")),
                V("image", (app, doc) => NewSketch(doc).SketchImages.Add(TinyBitmap(), P(app, 1, 2), false)),
                V("text-rotated", (app, doc) => {
                    dynamic sk = NewSketch(doc), c = app.TransientObjects.CreateObjectCollection();
                    c.Add(sk.TextBoxes.AddByRectangle(P(app, 1, 3), P(app, 4, 2), "ABC"));
                    sk.RotateSketchObjects(c, P(app, 1, 3), 0.5);
                }),
                V("image-rotated", (app, doc) => {
                    dynamic sk = NewSketch(doc), c = app.TransientObjects.CreateObjectCollection();
                    c.Add(sk.SketchImages.Add(TinyBitmap(), P(app, 1, 2), false));
                    sk.RotateSketchObjects(c, P(app, 1, 2), 0.5);
                }) };

            // Assemblies. Parts first (a 20 x 10 x 5 mm box with a corner at the origin, a 6 mm pin 30 mm long along z), then
            // a sub-assembly, then assemblies that differ in one thing. Generate them in that order, one per call.
            x["asmparts"] = new List<Variant> {
                V("box", (app, doc) => {
                    dynamic sk = NewSketch(doc);
                    sk.SketchLines.AddAsTwoPointRectangle(P(app, 0, 0), P(app, 2, 1));
                    dynamic ex = doc.ComponentDefinition.Features.ExtrudeFeatures, def = ex.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kNewBodyOperation);
                    def.SetDistanceExtent("5 mm", kPositiveExtentDirection);
                    ex.Add(def);
                }),
                V("pin", (app, doc) => {
                    dynamic sk = NewSketch(doc);
                    sk.SketchCircles.AddByCenterRadius(P(app, 0, 0), 0.3);
                    dynamic ex = doc.ComponentDefinition.Features.ExtrudeFeatures, def = ex.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kNewBodyOperation);
                    def.SetDistanceExtent("30 mm", kPositiveExtentDirection);
                    ex.Add(def);
                }) };
            x["asmsub"] = new List<Variant> {
                V("sub", (app, doc) => { Place(app, doc, "box", 0, 0, 0); Place(app, doc, "pin", 1, 0.5, 0.5); }, AsmTemplate),
                V("sub2", (app, doc) => { Place(app, doc, "pin", 0, 0, 0); PlaceFile(app, doc, Generated("asmsub", "sub.iam"), 1, 2, 3, 0.4, 0, 0, 1); }, AsmTemplate) };
            Func<string, Action<dynamic, dynamic>, Variant> A = (name, build) => V(name, build, AsmTemplate);
            // Box A grounded at the origin, box B 30 mm up and across; constrain(cd, a, b).
            Func<string, Action<dynamic, dynamic, dynamic, dynamic>, Variant> C = (name, constrain) => A(name, (app, doc) => {
                dynamic a = Place(app, doc, "box", 0, 0, 0), b = Place(app, doc, "box", 3, 3, 3);
                a.Grounded = true;
                constrain(app, doc.ComponentDefinition, a, b);
            });
            // Pins A (grounded) and B likewise.
            Func<string, Action<dynamic, dynamic, dynamic, dynamic>, Variant> CP = (name, constrain) => A(name, (app, doc) => {
                dynamic a = Place(app, doc, "pin", 0, 0, 0), b = Place(app, doc, "pin", 3, 3, 3);
                a.Grounded = true;
                constrain(app, doc.ComponentDefinition, a, b);
            });
            // Boxes joined at their faces' centres: A's top to B's bottom. change(definition) sets one thing.
            Func<string, int, Action<dynamic, dynamic>, Action<dynamic>, Variant> JA = (name, type, change, after) => C(name, (app, cd, a, b) => {
                dynamic def = cd.Joints.CreateAssemblyJointDefinition(type, cd.CreateGeometryIntent(PlaneFace(a, 0, 0, 1), kPlanarFaceCenterPointIntent),
                    cd.CreateGeometryIntent(PlaneFace(b, 0, 0, -1), kPlanarFaceCenterPointIntent));
                if (change != null) change(def, cd);
                dynamic joint = cd.Joints.Add(def);
                if (after != null) after(joint);
            });
            Func<string, int, Action<dynamic, dynamic>, Variant> J = (name, type, change) => JA(name, type, change, null);
            // A part of each kind of feature, with a suppressed constraint on each of its faces and edges and some vertices, so that
            // every way a part names its entities is compared with what Inventor says the constraint grips.
            x["asmgrip"] = new List<Variant>();
            foreach (string part in new[] { "hole/cbore", "hole/csink", "hole/spotface", "thread/full", "fillet/edge", "fillet/edges4", "chamfer/distance", "sheet/flange", "sheet/bend", "revolve/full", "loft/angle", "sweep/curved", "mirror/identical", "pattern-rect/count5", "pattern-circ/z6", "shell/inside", "imported/block", "profile/revolve" })
            {
                string p = part;
                x["asmgrip"].Add(V(p.Replace('/', '-'), (app, doc) => GripEverything(app, doc, Generated(p.Substring(0, p.IndexOf('/')), p.Substring(p.IndexOf('/') + 1) + ".ipt")), AsmTemplate));
            }
            // iMates: a pin (6 mm, 30 mm long along z) with one iMate of each kind or setting, then assemblies joining them.
            Func<string, Action<dynamic, dynamic>, Variant> IM = (name, add) => V(name, (app, doc) => {
                dynamic sk = NewSketch(doc), cd = doc.ComponentDefinition;
                sk.SketchCircles.AddByCenterRadius(P(app, 0, 0), 0.3);
                dynamic ex = cd.Features.ExtrudeFeatures, def = ex.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kNewBodyOperation);
                def.SetDistanceExtent("30 mm", kPositiveExtentDirection);
                ex.Add(def);
                add(doc, cd.iMateDefinitions);
            });
            x["imate"] = new List<Variant> {
                IM("mate", (doc, im) => im.AddMateiMateDefinition(FaceAt(doc, 3.0), 0)),
                IM("mate-offset", (doc, im) => { im.AddMateiMateDefinition(FaceAt(doc, 3.0), "2 mm").Name = "Top"; }),
                IM("mate-axis", (doc, im) => im.AddMateiMateDefinition(CylinderFace(doc.ComponentDefinition), 0, kInferredLine)),
                IM("flush", (doc, im) => im.AddFlushiMateDefinition(FaceAt(doc, 3.0), "1 mm")),
                IM("angle", (doc, im) => im.AddAngleiMateDefinition(FaceAt(doc, 3.0), false, "30 deg")),
                IM("angle-reversed", (doc, im) => im.AddAngleiMateDefinition(FaceAt(doc, 3.0), true, "30 deg")),
                IM("tangent", (doc, im) => im.AddTangentiMateDefinition(CylinderFace(doc.ComponentDefinition), false, "1 mm")),
                IM("tangent-inside", (doc, im) => im.AddTangentiMateDefinition(CylinderFace(doc.ComponentDefinition), true, 0)),
                IM("insert", (doc, im) => im.AddInsertiMateDefinition(CircleEdgeOf(doc.ComponentDefinition), false, "1 mm")),
                IM("insert-opposed", (doc, im) => im.AddInsertiMateDefinition(CircleEdgeOf(doc.ComponentDefinition), true, 0)),
                IM("two", (doc, im) => { im.AddMateiMateDefinition(FaceAt(doc, 3.0), 0).Name = "First"; im.AddMateiMateDefinition(FaceAt(doc, 0.0), 0).Name = "Second"; }),
                IM("suppressed", (doc, im) => { im.AddMateiMateDefinition(FaceAt(doc, 3.0), 0).Suppressed = true; }),
                IM("matchlist", (doc, im) => { dynamic d = im.AddFlushiMateDefinition(FaceAt(doc, 3.0), 0); d.Name = "Listed"; d.MatchList = new[] { "Alpha", "Beta" }; }),
                IM("composite", (doc, im) => {
                    dynamic c = doc.Parent.TransientObjects.CreateObjectCollection();
                    c.Add(im.AddMateiMateDefinition(FaceAt(doc, 3.0), 0));
                    c.Add(im.AddInsertiMateDefinition(CircleEdgeOf(doc.ComponentDefinition), false, 0));
                    im.AddCompositeiMateDefinition(c, "Both");
                }) };
            Func<string, string, Variant> IA = (name, part) => A(name, (app, doc) => {
                dynamic cd = doc.ComponentDefinition, a = PlaceFile(app, doc, Generated("imate", part + ".ipt"), 0, 0, 0), b = PlaceFile(app, doc, Generated("imate", part + ".ipt"), 3, 3, 3);
                a.Grounded = true;
                cd.iMateResults.AddByTwoiMates(a.iMateDefinitions[1], b.iMateDefinitions[1]);
            });
            x["asmimate"] = new List<Variant> {
                A("placed", (app, doc) => PlaceFile(app, doc, Generated("imate", "mate.ipt"), 1, 2, 3)),
                IA("result-mate", "mate"),
                IA("result-flush", "flush"),
                IA("result-insert", "insert"),
                IA("result-composite", "composite"),
                A("result-entity", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, a = PlaceFile(app, doc, Generated("imate", "mate.ipt"), 3, 3, 3), b = Place(app, doc, "box", 0, 0, 0);
                    b.Grounded = true;
                    cd.iMateResults.AddByiMateAndEntity(a.iMateDefinitions[1], PlaneFace(b, 0, 0, 1));
                }),
                A("assembly-imate", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, a = Place(app, doc, "box", 0, 0, 0);
                    a.Grounded = true;
                    cd.iMateDefinitions.AddMateiMateDefinition(PlaneFace(a, 0, 0, 1), "1 mm").Name = "OnBox";
                }) };
            x["asm"] = new List<Variant> {
                A("one", (app, doc) => Place(app, doc, "box", 0, 0, 0)),
                A("moved", (app, doc) => Place(app, doc, "box", 1, 2, 3)),
                A("two", (app, doc) => { Place(app, doc, "box", 0, 0, 0); Place(app, doc, "box", 3, 4, 5); }),
                A("files", (app, doc) => { Place(app, doc, "box", 0, 0, 0); Place(app, doc, "pin", 3, 4, 5); }),
                A("turned", (app, doc) => Place(app, doc, "box", 1, 2, 3, 0.5, 0, 0, 1)),
                A("tilted", (app, doc) => Place(app, doc, "box", 1, 2, 3, 0.7, 1, 2, 3)),
                A("grounded", (app, doc) => { Place(app, doc, "box", 0, 0, 0).Grounded = true; }),
                A("invisible", (app, doc) => { Place(app, doc, "box", 0, 0, 0).Visible = false; }),
                A("suppressed", (app, doc) => { Place(app, doc, "box", 0, 0, 0); Place(app, doc, "box", 3, 4, 5).Suppress(); }),
                A("named", (app, doc) => { Place(app, doc, "box", 0, 0, 0).Name = "My Box"; }),
                A("sub", (app, doc) => { Place(app, doc, "box", 0, 0, 0); PlaceFile(app, doc, Generated("asmsub", "sub.iam"), 3, 4, 5, 0.5, 0, 0, 1); }),
                A("flexible", (app, doc) => { PlaceFile(app, doc, Generated("asmsub", "sub.iam"), 3, 4, 5).Flexible = true; }),
                A("flexible-moved", (app, doc) => {
                    dynamic s = PlaceFile(app, doc, Generated("asmsub", "sub.iam"), 3, 4, 5);
                    s.Flexible = true;
                    dynamic child = s.SubOccurrences[2], tg = app.TransientGeometry, m = child.Transformation;
                    m.SetTranslation(tg.CreateVector(6, 7, 8), false);
                    child.Transformation = m;
                }),
                A("mate-flexible", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, a = Place(app, doc, "box", 0, 0, 0), s = PlaceFile(app, doc, Generated("asmsub", "sub.iam"), 3, 4, 5);
                    a.Grounded = true;
                    s.Flexible = true;
                    dynamic child = s.SubOccurrences[2], tg = app.TransientGeometry, m = child.Transformation;
                    m.SetTranslation(tg.CreateVector(6, 7, 8), false);
                    child.Transformation = m;
                    cd.Constraints.AddMateConstraint(PlaneFace(a, 0, 0, 1), CylinderFace(child), "1 mm", kNoInference, kInferredLine);
                }),
                A("mate-flexible2", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, a = Place(app, doc, "box", 0, 0, 0), s = PlaceFile(app, doc, Generated("asmsub", "sub2.iam"), 3, 4, 5);
                    a.Grounded = true;
                    s.Flexible = true;
                    dynamic inner = s.SubOccurrences[2];
                    inner.Flexible = true;
                    dynamic child = inner.SubOccurrences[2], tg = app.TransientGeometry, m = child.Transformation;
                    m.SetTranslation(tg.CreateVector(6, 7, 8), false);
                    child.Transformation = m;
                    cd.Constraints.AddMateConstraint(PlaneFace(a, 0, 0, 1), CylinderFace(child), "1 mm", kNoInference, kInferredLine);
                }),
                // As in a real assembly: a flexible sub-assembly holding a flexible one, both moved, gripping the inner one's grounded part.
                A("mate-flexible3", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, a = Place(app, doc, "box", 0, 0, 0), s = PlaceFile(app, doc, Generated("asmsub", "sub2.iam"), 3, 4, 5);
                    a.Grounded = true;
                    s.Flexible = true;
                    dynamic inner = s.SubOccurrences[2], tg = app.TransientGeometry;
                    inner.Flexible = true;
                    dynamic m = inner.Transformation;
                    m.SetToRotation(0.3, tg.CreateVector(1, 1, 0), tg.CreatePoint(0, 0, 0));
                    m.SetTranslation(tg.CreateVector(9, 1, 2), false);
                    inner.Transformation = m;
                    dynamic pin = inner.SubOccurrences[2], n = pin.Transformation;
                    n.SetTranslation(tg.CreateVector(6, 7, 8), false);
                    pin.Transformation = n;
                    dynamic tilted = null;
                    foreach (dynamic f in inner.SubOccurrences[1].SurfaceBodies[1].Faces)
                        if (EnumName("SurfaceTypeEnum", (int)f.SurfaceType) == "kPlaneSurface") { tilted = f; break; }
                    cd.Constraints.AddMateConstraint(PlaneFace(a, 1, 0, 0), tilted, "1 mm");
                }),
                A("ungrounded", (app, doc) => { dynamic a = Place(app, doc, "box", 0, 0, 0), b = Place(app, doc, "box", 3, 4, 5); a.Grounded = false; b.Grounded = true; }),
                A("pattern-rect", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, c = app.TransientObjects.CreateObjectCollection();
                    c.Add(Place(app, doc, "box", 0, 0, 0));
                    cd.OccurrencePatterns.AddRectangularPattern(c, cd.WorkAxes[1], true, "30 mm", 3);
                }),
                A("pattern-rect2", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, c = app.TransientObjects.CreateObjectCollection();
                    c.Add(Place(app, doc, "box", 0, 0, 0));
                    cd.OccurrencePatterns.AddRectangularPattern(c, cd.WorkAxes[1], true, "30 mm", 3, cd.WorkAxes[2], false, "20 mm", 2);
                }),
                A("pattern-circ", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, c = app.TransientObjects.CreateObjectCollection();
                    c.Add(Place(app, doc, "box", 3, 0, 0));
                    cd.OccurrencePatterns.AddCircularPattern(c, cd.WorkAxes[3], true, "45 deg", 4);
                }),
                A("pattern-two", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, c = app.TransientObjects.CreateObjectCollection();
                    c.Add(Place(app, doc, "box", 0, 0, 0));
                    c.Add(Place(app, doc, "pin", 0, 2, 0));
                    cd.OccurrencePatterns.AddRectangularPattern(c, cd.WorkAxes[1], true, "30 mm", 2);
                }),
                A("pattern-independent", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, c = app.TransientObjects.CreateObjectCollection();
                    c.Add(Place(app, doc, "box", 0, 0, 0));
                    dynamic p = cd.OccurrencePatterns.AddRectangularPattern(c, cd.WorkAxes[1], true, "30 mm", 3);
                    p.OccurrencePatternElements[2].Independent = true;
                }),
                A("pattern-rect-rev", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, c = app.TransientObjects.CreateObjectCollection();
                    c.Add(Place(app, doc, "box", 0, 0, 0));
                    cd.OccurrencePatterns.AddRectangularPattern(c, cd.WorkAxes[2], false, "25 mm", 2, cd.WorkAxes[3], true, "15 mm", 3);
                }),
                A("pattern-circ-rev", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, c = app.TransientObjects.CreateObjectCollection();
                    c.Add(Place(app, doc, "box", 3, 0, 0));
                    cd.OccurrencePatterns.AddCircularPattern(c, cd.WorkAxes[3], false, "60 deg", 3);
                }),
                A("pattern-suppressed", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, c = app.TransientObjects.CreateObjectCollection();
                    c.Add(Place(app, doc, "box", 0, 0, 0));
                    dynamic p = cd.OccurrencePatterns.AddRectangularPattern(c, cd.WorkAxes[1], true, "30 mm", 3);
                    p.OccurrencePatternElements[3].Suppressed = true;
                }),
                A("pattern-formula", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, c = app.TransientObjects.CreateObjectCollection();
                    c.Add(Place(app, doc, "box", 0, 0, 0));
                    dynamic p = cd.OccurrencePatterns.AddRectangularPattern(c, cd.WorkAxes[1], true, "30 mm", 2);
                    p.ColumnCount.Expression = "2 ul + 1 ul";
                    p.ColumnOffset.Expression = "10 mm * 3";
                }),
                C("mate", (app, cd, a, b) => cd.Constraints.AddMateConstraint(PlaneFace(a, 0, 0, 1), PlaneFace(b, 0, 0, -1), 0)),
                A("mate-sub", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, a = Place(app, doc, "box", 0, 0, 0), s = PlaceFile(app, doc, Generated("asmsub", "sub.iam"), 3, 4, 5, 0.5, 0, 0, 1);
                    a.Grounded = true;
                    cd.Constraints.AddMateConstraint(PlaneFace(a, 0, 0, 1), PlaneFace(s.SubOccurrences[1], 0, 0, -1), "1 mm");
                }),
                A("mate-sub-pin", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, a = Place(app, doc, "box", 0, 0, 0), s = PlaceFile(app, doc, Generated("asmsub", "sub.iam"), 3, 4, 5, 0.5, 1, 0, 0);
                    a.Grounded = true;
                    cd.Constraints.AddInsertConstraint(CircleEdgeOf(s.SubOccurrences[2]), a.SurfaceBodies[1].Faces.Count > 0 ? CircleEdgeOf(Place(app, doc, "pin", 0, 0, 0)) : null, true, 0);
                }),
                A("mate-sub2", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, a = Place(app, doc, "box", 0, 0, 0), s = PlaceFile(app, doc, Generated("asmsub", "sub2.iam"), 3, 4, 5, 0.5, 0, 0, 1);
                    a.Grounded = true;
                    cd.Constraints.AddMateConstraint(PlaneFace(a, 0, 0, 1), PlaneFace(s.SubOccurrences[2].SubOccurrences[1], 0, 0, -1), "1 mm");
                }),
                C("mate-offset", (app, cd, a, b) => cd.Constraints.AddMateConstraint(PlaneFace(a, 0, 0, 1), PlaneFace(b, 0, 0, -1), "2 mm")),
                C("mate-suppressed", (app, cd, a, b) => { cd.Constraints.AddMateConstraint(PlaneFace(a, 0, 0, 1), PlaneFace(b, 0, 0, -1), 0).Suppressed = true; }),
                C("mate-named", (app, cd, a, b) => { cd.Constraints.AddMateConstraint(PlaneFace(a, 0, 0, 1), PlaneFace(b, 0, 0, -1), 0).Name = "My Mate"; }),
                CP("mate-axis", (app, cd, a, b) => cd.Constraints.AddMateConstraint(CylinderFace(a), CylinderFace(b), 0, kInferredLine, kInferredLine)),
                C("mate-aligned", (app, cd, a, b) => cd.Constraints.AddMateConstraint2(PlaneFace(a, 0, 0, 1), PlaneFace(b, 0, 0, 1), 0, kNoInference, kNoInference, kAlignedSolutionType)),
                C("mate-expression", (app, cd, a, b) => cd.Constraints.AddMateConstraint(PlaneFace(a, 0, 0, 1), PlaneFace(b, 0, 0, -1), "2 mm + 1 mm")),
                C("mate-vertex", (app, cd, a, b) => cd.Constraints.AddMateConstraint(a.SurfaceBodies[1].Vertices[1], b.SurfaceBodies[1].Vertices[1], 0)),
                C("mate-edge", (app, cd, a, b) => cd.Constraints.AddMateConstraint(a.SurfaceBodies[1].Edges[1], b.SurfaceBodies[1].Edges[1], 0)),
                C("mate-assembly-plane", (app, cd, a, b) => cd.Constraints.AddMateConstraint(cd.WorkPlanes[3], PlaneFace(b, 0, 0, -1), 0)),
                C("mate-assembly-axis", (app, cd, a, b) => cd.Constraints.AddMateConstraint(cd.WorkAxes[2], b.SurfaceBodies[1].Edges[1], 0)),
                C("mate-assembly-point", (app, cd, a, b) => cd.Constraints.AddMateConstraint(cd.WorkPoints[1], b.SurfaceBodies[1].Vertices[1], 0)),
                C("symmetry", (app, cd, a, b) => cd.Constraints.AddSymmetryConstraint(PlaneFace(a, 0, 0, 1), PlaneFace(b, 0, 0, 1), cd.WorkPlanes[1])),
                CP("insert-lock", (app, cd, a, b) => cd.Constraints.AddInsertConstraint2(CircleEdge(a, 3), CircleEdge(b, 0), true, 0, true)),
                A("rotate-translate", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, a = Place(app, doc, "pin", 0, 0, 0), b = Place(app, doc, "box", 3, 3, 3);
                    a.Grounded = true;
                    cd.Constraints.AddRotateTranslateConstraint(CylinderFace(a), PlaneFace(b, 0, 0, 1), "2 mm", true);
                }),
                A("tangent-inside", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, a = Place(app, doc, "box", 0, 0, 0), b = Place(app, doc, "pin", 3, 3, 3);
                    a.Grounded = true;
                    cd.Constraints.AddTangentConstraint(PlaneFace(a, 0, 0, 1), CylinderFace(b), true, "1 mm");
                }),
                C("flush", (app, cd, a, b) => cd.Constraints.AddFlushConstraint(PlaneFace(a, 0, 0, 1), PlaneFace(b, 0, 0, 1), "1 mm")),
                C("angle", (app, cd, a, b) => cd.Constraints.AddAngleConstraint(PlaneFace(a, 0, 0, 1), PlaneFace(b, 0, 0, 1), "30 deg")),
                C("angle-undirected", (app, cd, a, b) => cd.Constraints.AddAngleConstraint(PlaneFace(a, 0, 0, 1), PlaneFace(b, 0, 0, 1), "30 deg", kUndirectedSolution)),
                A("tangent", (app, doc) => {
                    dynamic cd = doc.ComponentDefinition, a = Place(app, doc, "box", 0, 0, 0), b = Place(app, doc, "pin", 3, 3, 3);
                    a.Grounded = true;
                    cd.Constraints.AddTangentConstraint(PlaneFace(a, 0, 0, 1), CylinderFace(b), false, 0);
                }),
                CP("insert",(app, cd, a, b) => cd.Constraints.AddInsertConstraint(CircleEdge(a, 3), CircleEdge(b, 0), true, 0)),
                CP("insert-aligned", (app, cd, a, b) => cd.Constraints.AddInsertConstraint(CircleEdge(a, 3), CircleEdge(b, 0), false, "1 mm")),
                CP("rotate", (app, cd, a, b) => cd.Constraints.AddRotateRotateConstraint(CylinderFace(a), CylinderFace(b), 2, true)),
                CP("rotate-back", (app, cd, a, b) => cd.Constraints.AddRotateRotateConstraint(CylinderFace(a), CylinderFace(b), 2, false)),
                J("rigid", kRigidJointType, null),
                J("rotational", kRotationalJointType, null),
                J("slide", kSlideJointType, null),
                J("cylindrical", kCylindricalJointType, null),
                J("planar", kPlanarJointType, null),
                J("ball", kBallJointType, null),
                J("joint-gap", kRigidJointType, (def, cd) => { def.Gap = "2 mm"; }),
                J("joint-flip", kRigidJointType, (def, cd) => { def.FlipOriginDirection = true; }),
                J("joint-flip-align", kRigidJointType, (def, cd) => { def.FlipAlignmentDirection = true; }),
                JA("joint-locked", kRotationalJointType, null, joint => { joint.Locked = true; }),
                JA("joint-suppressed", kRotationalJointType, null, joint => { joint.Suppressed = true; }),
                JA("joint-named", kRigidJointType, null, joint => { joint.Name = "My Joint"; }),
                J("joint-cyl-limits", kCylindricalJointType, (def, cd) => {
                    def.LinearPosition = "2 mm";
                    def.AngularPosition = "5 deg";
                    def.HasLinearPositionStartLimit = true;
                    def.LinearPositionStartLimit = "1 mm";
                    def.HasAngularPositionLimits = true;
                    def.AngularPositionStartLimit = "-30 deg";
                    def.AngularPositionEndLimit = "30 deg";
                }),
                J("joint-angle", kRotationalJointType, (def, cd) => { def.AngularPosition = "20 deg"; }),
                J("joint-limits", kRotationalJointType, (def, cd) => {
                    def.HasAngularPositionLimits = true;
                    def.AngularPositionStartLimit = "-10 deg";
                    def.AngularPositionEndLimit = "40 deg";
                }),
                J("joint-slide-limits", kSlideJointType, (def, cd) => {
                    def.HasLinearPositionStartLimit = true;
                    def.LinearPositionStartLimit = "1 mm";
                    def.HasLinearPositionEndLimit = true;
                    def.LinearPositionEndLimit = "9 mm";
                    def.LinearPosition = "4 mm";
                }) };

            // Sheet metal (the sheet-metal template, 0.5 mm thick): a 40 x 40 mm plate on the XY plane, one setting changed;
            // or two plates meeting at an edge, or apart and joined by a bend.
            x["sheet"] = new List<Variant> {
                V("plate", Plate((f, app, doc) => { }), SheetTemplate),
                V("reversed", Plate((f, app, doc) => { }, reversed: true), SheetTemplate),
                V("hole", Plate((f, app, doc) => { }, hole: true), SheetTemplate),
                // A second plate on the plane x = 25 mm, 5 mm clear of the first; a bend joins their facing top edges.
                V("bend", Plate((f, app, doc) => {
                    dynamic second = doc.ComponentDefinition.Sketches.Add(OffsetPlane(doc, "25 mm", 1));
                    dynamic tg = app.TransientGeometry;
                    second.SketchLines.AddAsTwoPointRectangle(second.ModelToSketchSpace(tg.CreatePoint(2.5, -2, 0.5)), second.ModelToSketchSpace(tg.CreatePoint(2.5, 2, 4)));
                    dynamic def = f.FaceFeatures.CreateFaceFeatureDefinition(second.Profiles.AddForSolid());
                    f.FaceFeatures.Add(def);
                    dynamic edges = app.TransientObjects.CreateEdgeCollection();
                    edges.Add(PickEdge(EdgeOnLine(doc, 2.0, 0.05)));
                    edges.Add(PickEdge(EdgeOnLine(doc, 2.5, 0.5)));
                    f.BendFeatures.Add(f.BendFeatures.CreateBendDefinition(edges));
                }), SheetTemplate),
                // A flange on the plate's top edge at x = 20 mm.
                V("flange", Plate((f, app, doc) => Flange(f, app, doc, "90 deg", "20 mm", false)), SheetTemplate),
                V("flange-height", Plate((f, app, doc) => Flange(f, app, doc, "90 deg", "30 mm", false)), SheetTemplate),
                V("flange-angle", Plate((f, app, doc) => Flange(f, app, doc, "60 deg", "20 mm", false)), SheetTemplate),
                V("flange-reversed", Plate((f, app, doc) => Flange(f, app, doc, "90 deg", "20 mm", true)), SheetTemplate),
                // Flanges on two neighbouring edges of the plate, then a corner where they meet. (Inventor refuses the ripped one.)
                V("corner", Plate((f, app, doc) => CornerOfTwoFlanges(f, app, doc, false, null)), SheetTemplate),
                V("corner-ripped", Plate((f, app, doc) => CornerOfTwoFlanges(f, app, doc, true, null)), SheetTemplate),
                V("corner-gap", Plate((f, app, doc) => CornerOfTwoFlanges(f, app, doc, false, (Action<dynamic>)(def => { def.GapWidth = "1 mm"; }))), SheetTemplate),
                V("corner-overlap", Plate((f, app, doc) => CornerOfTwoFlanges(f, app, doc, false, (Action<dynamic>)(def => { def.CornerType = 76033; }))), SheetTemplate),
                V("corner-reverse", Plate((f, app, doc) => CornerOfTwoFlanges(f, app, doc, false, (Action<dynamic>)(def => { def.CornerType = 76035; }))), SheetTemplate) };

            // Bodies with no history: a block made through the API, kept as a base feature.
            x["imported"] = new List<Variant> {
                V("block", (app, doc) => {
                    dynamic box = app.TransientGeometry.CreateBox();
                    box.MinPoint = app.TransientGeometry.CreatePoint(-1, -1, -1);
                    box.MaxPoint = app.TransientGeometry.CreatePoint(1, 1, 1);
                    doc.ComponentDefinition.Features.NonParametricBaseFeatures.Add(app.TransientBRep.CreateSolidBlock(box));
                }),
                // Features on the imported block (20 mm cube, z -10..10 mm): the converter rebuilds them on its shape as
                // imported, which it takes from the B-rep's history.
                V("block-cut", (app, doc) => { ImportedBlock(app, doc, 0); HoleInTop(app, doc); }),
                V("block-fillet", (app, doc) => {
                    ImportedBlock(app, doc, 0);
                    dynamic top = FaceAt(doc, 1.0);
                    doc.ComponentDefinition.Features.FilletFeatures.AddSimple(EdgeSet(app, top.Edges[1]), "3 mm", false, false, true, false, true, false);
                }),
                V("block-join", (app, doc) => {
                    ImportedBlock(app, doc, 0);
                    dynamic ex = doc.ComponentDefinition.Features.ExtrudeFeatures;
                    dynamic sk = doc.ComponentDefinition.Sketches.Add(PickFace(FaceAt(doc, 1.0)));
                    sk.SketchLines.AddAsTwoPointRectangle(P(app, -0.5, -0.3), P(app, 0.4, 0.5));
                    dynamic def = ex.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kJoinOperation);
                    def.SetDistanceExtent("5 mm", kPositiveExtentDirection);
                    ex.Add(def);
                }),
                V("block-cut-fillet", (app, doc) => {
                    ImportedBlock(app, doc, 0);
                    HoleInTop(app, doc);
                    doc.ComponentDefinition.Features.FilletFeatures.AddSimple(EdgeSet(app, RoundEdge(FaceAt(doc, 1.0), 0.4)), "1 mm", false, false, true, false, true, false);
                }),
                // Two imported blocks side by side: only the first is the history's first body.
                V("two-blocks", (app, doc) => { ImportedBlock(app, doc, 0); ImportedBlock(app, doc, 3); }) };

            // A 6 mm rod 20 mm long (z 0..20) threaded M6x1 from its top edge, or a 20 x 20 x 10 mm plate with a 5 mm hole
            // threaded inside; one setting changed. Thread(internal, rightHanded, fullDepth, depth, offset, reversed).
            x["thread"] = new List<Variant> {
                V("full", Thread(false, true, true, null, null, false)),
                V("depth", Thread(false, true, false, "10 mm", "2 mm", false)),
                V("reversed", Thread(false, true, false, "10 mm", "2 mm", true)),
                V("left", Thread(false, false, true, null, null, false)),
                V("internal", Thread(true, true, true, null, null, false)) };

            // A 40 x 40 x 20 mm block (z 0..20) shelled 2 mm with its top face removed; one setting changed.
            x["shell"] = new List<Variant> {
                V("inside", Shelled((app, doc, s) => s.Add(s.CreateShellDefinition(Faces(app, doc, 2.0), "2 mm", kInsideShellDirection)))),
                V("outside", Shelled((app, doc, s) => s.Add(s.CreateShellDefinition(Faces(app, doc, 2.0), "2 mm", kOutsideShellDirection)))),
                V("both", Shelled((app, doc, s) => s.Add(s.CreateShellDefinition(Faces(app, doc, 2.0), "2 mm", kBothSidesShellDirection)))),
                V("two-faces", Shelled((app, doc, s) => s.Add(s.CreateShellDefinition(Faces(app, doc, 2.0, 0.0), "2 mm", kInsideShellDirection)))),
                V("no-face", Shelled((app, doc, s) => s.Add(s.CreateShellDefinition(app.TransientObjects.CreateFaceCollection(), "2 mm", kInsideShellDirection)))),
                // The side face at x = 20 mm 4 mm thick.
                V("face-set", Shelled((app, doc, s) => {
                    dynamic def = s.CreateShellDefinition(Faces(app, doc, 2.0), "2 mm", kInsideShellDirection);
                    dynamic side = app.TransientObjects.CreateFaceCollection();
                    side.Add(PickFace(FaceAt(doc, 2.0, 0)));
                    def.AddThicknessFaceSet(side, "4 mm");
                    s.Add(def);
                })) };

            // A 40 x 40 mm square with a 10 mm circle inside it and another at (35, 0) mm, on the XY plane, extruded 5 mm;
            // each variant keeps different profile paths (by the path's first sketch entity).
            Func<double, Func<dynamic, bool>> circleAt = cx => e => TypeName((int)e.Type) == "kSketchCircleObject" && Math.Abs((double)e.CenterSketchPoint.Geometry.X - cx) < 1e-6;
            Func<dynamic, bool> isLine = e => TypeName((int)e.Type) == "kSketchLineObject";
            x["profile"] = new List<Variant> {
                V("all", Loops(null)),
                V("no-hole", Loops(e => !circleAt(0)(e))),
                V("outer-only", Loops(isLine)),
                V("inner-only", Loops(circleAt(0))),
                V("separate-only", Loops(circleAt(3.5))),
                V("no-combine", Loops(null, combine: false)),
                // Two overlapping 20 mm circles, centres 15 mm apart.
                V("overlap", Loops(null, overlap: true)),
                V("revolve", Loops(null, revolve: true)) };

            // A 40 x 40 x 10 mm slab (z -5..5 mm) with a 4 mm hole cut through at (-10, -10) mm (and a 4 mm boss at
            // (10, 10) mm, z 0..8, where named), then the hole mirrored or patterned; one setting changed.
            // Work planes 1..3 are YZ, XZ, XY; work axes 1..3 are X, Y, Z.
            x["mirror"] = new List<Variant> {
                V("yz", Copied((f, app, doc, c) => f.MirrorFeatures.Add(c, Plane(doc, 1)))),
                V("xz", Copied((f, app, doc, c) => f.MirrorFeatures.Add(c, Plane(doc, 2)))),
                V("offset", Copied((f, app, doc, c) => f.MirrorFeatures.Add(c, PickPlane(OffsetPlane(doc, "3 mm", 1))))),
                V("two", Copied((f, app, doc, c) => f.MirrorFeatures.Add(c, Plane(doc, 1)), boss: true)),
                // About the face at x = 0 of a step (x 0..20 mm raised to z = 15 mm), which the copy doesn't touch.
                V("face", Copied((f, app, doc, c) => f.MirrorFeatures.Add(c, PickFace(FaceAt(doc, 0, 0))), step: true)),
                V("adjust", Copied((f, app, doc, c) => f.MirrorFeatures.Add(c, Plane(doc, 1), false, kAdjustToModelCompute))),
                // The default compute type is optimized.
                V("identical", Copied((f, app, doc, c) => f.MirrorFeatures.Add(c, Plane(doc, 1), false, kIdenticalCompute))),
                V("body", (app, doc) => {
                    Slab(app, doc, 2, "10 mm");
                    dynamic c = app.TransientObjects.CreateObjectCollection();
                    c.Add(doc.ComponentDefinition.SurfaceBodies[1]);
                    doc.ComponentDefinition.Features.MirrorFeatures.Add(c, PickPlane(OffsetPlane(doc, "20 mm", 1)));
                }) };
            x["pattern-rect"] = new List<Variant> {
                V("x3", Copied((f, app, doc, c) => f.RectangularPatternFeatures.Add(c, Axis(doc, 1), true, "3", "4 mm"))),
                V("xy", Copied((f, app, doc, c) => f.RectangularPatternFeatures.Add(c, Axis(doc, 1), true, "3", "4 mm", kDefault, Type.Missing,
                    Axis(doc, 2), true, "2", "6 mm"))),
                V("reversed", Copied((f, app, doc, c) => f.RectangularPatternFeatures.Add(c, Axis(doc, 1), false, "3", "4 mm"))),
                V("fitted", Copied((f, app, doc, c) => f.RectangularPatternFeatures.Add(c, Axis(doc, 1), true, "3", "8 mm", kFitted))),
                V("edge", Copied((f, app, doc, c) => f.RectangularPatternFeatures.Add(c, PickEdge(EdgeAlong(FaceAt(doc, 0.5), true)), true, "3", "4 mm"))),
                V("count5", Copied((f, app, doc, c) => f.RectangularPatternFeatures.Add(c, Axis(doc, 1), true, "5", "4 mm"))),
                V("adjust", Copied((f, app, doc, c) => f.RectangularPatternFeatures.Add(c, Axis(doc, 1), true, "3", "4 mm", kDefault, Type.Missing,
                    Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, kAdjustToModelCompute))),
                // Optimized patterns leave their copies unnamed in the B-rep; adjust-to-model ones name them, which shows
                // how copies are numbered.
                V("x5-adjust", Copied((f, app, doc, c) => f.RectangularPatternFeatures.Add(c, Axis(doc, 1), true, "5", "4 mm", kDefault, Type.Missing,
                    Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, kAdjustToModelCompute))),
                V("xy-adjust", Copied((f, app, doc, c) => f.RectangularPatternFeatures.Add(c, Axis(doc, 1), true, "3", "4 mm", kDefault, Type.Missing,
                    Axis(doc, 2), true, "2", "6 mm", kDefault, Type.Missing, kAdjustToModelCompute))),
                V("mid", Copied((f, app, doc, c) => {
                    dynamic def = f.RectangularPatternFeatures.CreateDefinition(c, Axis(doc, 1), true, "3", "4 mm");
                    def.XDirectionMidPlanePattern = true;
                    f.RectangularPatternFeatures.AddByDefinition(def);
                })),
                V("orient", Copied((f, app, doc, c) => f.RectangularPatternFeatures.Add(c, Axis(doc, 1), true, "3", "4 mm", kDefault, Type.Missing,
                    Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, kAdjustToDirection1))),
                // The whole slab, a copy 40 mm along X (touching it).
                V("body", (app, doc) => {
                    Slab(app, doc, 2, "10 mm");
                    dynamic c = app.TransientObjects.CreateObjectCollection();
                    c.Add(doc.ComponentDefinition.SurfaceBodies[1]);
                    doc.ComponentDefinition.Features.RectangularPatternFeatures.Add(c, Axis(doc, 1), true, "2", "40 mm");
                }) };
            x["pattern-circ"] = new List<Variant> {
                V("z6", Copied((f, app, doc, c) => f.CircularPatternFeatures.Add(c, Axis(doc, 3), true, "6", "360 deg", true))),
                V("fit90", Copied((f, app, doc, c) => f.CircularPatternFeatures.Add(c, Axis(doc, 3), true, "4", "90 deg", true))),
                V("step30", Copied((f, app, doc, c) => f.CircularPatternFeatures.Add(c, Axis(doc, 3), true, "4", "30 deg", false))),
                V("reversed", Copied((f, app, doc, c) => f.CircularPatternFeatures.Add(c, Axis(doc, 3), false, "4", "90 deg", true))),
                V("adjust", Copied((f, app, doc, c) => f.CircularPatternFeatures.Add(c, Axis(doc, 3), true, "4", "90 deg", true, kAdjustToModelCompute))),
                V("mid", Copied((f, app, doc, c) => {
                    dynamic def = f.CircularPatternFeatures.CreateDefinition(c, Axis(doc, 3), true, "4", "90 deg", true);
                    def.MidPlanePattern = true;
                    f.CircularPatternFeatures.AddByDefinition(def);
                })),
                // Flips whatever LockRotation defaults to; the truth says which it became.
                V("lock", Copied((f, app, doc, c) => {
                    dynamic def = f.CircularPatternFeatures.CreateDefinition(c, Axis(doc, 3), true, "4", "90 deg", true);
                    def.LockRotation = !(bool)def.LockRotation;
                    f.CircularPatternFeatures.AddByDefinition(def);
                })) };

            // User parameters {name, expression, units}; a name says which unit it uses.
            x["unit"] = new List<Variant> {
                V("length", (app, doc) => Params(doc, new[] {
                    new[] { "U_mm", "2 mm", "mm" }, new[] { "U_cm", "2 cm", "cm" }, new[] { "U_m", "2 m", "m" },
                    new[] { "U_in", "2 in", "in" }, new[] { "U_ft", "2 ft", "ft" }, new[] { "U_mil", "2 mil", "mil" },
                    new[] { "U_um", "2 micron", "micron" } })),
                V("other", (app, doc) => Params(doc, new[] {
                    new[] { "U_deg", "2 deg", "deg" }, new[] { "U_rad", "2 rad", "rad" }, new[] { "U_ul", "2 ul", "ul" },
                    new[] { "U_s", "2 s", "s" }, new[] { "U_kg", "2 kg", "kg" }, new[] { "U_lbmass", "2 lbmass", "lbmass" },
                    new[] { "U_N", "2 N", "N" }, new[] { "U_mm2", "2 mm^2", "mm^2" }, new[] { "U_mm3", "2 mm^3", "mm^3" },
                    new[] { "U_mmps", "2 mm/s", "mm/s" }, new[] { "U_permm", "2 mm^-1", "mm^-1" } })),
                V("literal", (app, doc) => Params(doc, new[] {
                    new[] { "Len", "2 mm", "mm" }, new[] { "B_in", "1 in", "mm" }, new[] { "B_cm", "2.5 cm", "mm" },
                    new[] { "Ang", "1 rad", "deg" }, new[] { "Ratio", "Len / 1 mm", "ul" }, new[] { "Area", "Len * Len", "mm^2" },
                    new[] { "Speed", "Len / 1 s", "mm/s" }, new[] { "Mixed", "Len + 1 in", "mm" } })),
                // One user parameter per function, operator and constant, to map the function ids.
                V("functions", (app, doc) => Params(doc, new[] {
                    new[] { "Len", "2 mm", "mm" },
                    new[] { "F_sin", "sin(30 deg)", "ul" }, new[] { "F_cos", "cos(30 deg)", "ul" }, new[] { "F_tan", "tan(30 deg)", "ul" },
                    new[] { "F_asin", "asin(0.5 ul)", "deg" }, new[] { "F_acos", "acos(0.5 ul)", "deg" }, new[] { "F_atan", "atan(0.5 ul)", "deg" },
                    new[] { "F_sinh", "sinh(0.5 ul)", "ul" }, new[] { "F_cosh", "cosh(0.5 ul)", "ul" }, new[] { "F_tanh", "tanh(0.5 ul)", "ul" },
                    new[] { "F_asinh", "asinh(0.5 ul)", "ul" }, new[] { "F_acosh", "acosh(1.5 ul)", "ul" }, new[] { "F_atanh", "atanh(0.5 ul)", "ul" },
                    new[] { "F_sqrt", "sqrt(Len * Len)", "mm" }, new[] { "F_abs", "abs(-Len)", "mm" }, new[] { "F_sign", "sign(-2 ul)", "ul" },
                    new[] { "F_exp", "exp(1 ul)", "ul" }, new[] { "F_ln", "ln(2 ul)", "ul" }, new[] { "F_log", "log(100 ul)", "ul" },
                    new[] { "F_floor", "floor(2.5 ul)", "ul" }, new[] { "F_ceil", "ceil(2.5 ul)", "ul" }, new[] { "F_round", "round(2.5 ul)", "ul" },
                    new[] { "F_max", "max(Len; 3 mm)", "mm" }, new[] { "F_min", "min(Len; 3 mm)", "mm" }, new[] { "F_pow", "pow(2 ul; 3 ul)", "ul" },
                    new[] { "F_random", "random()", "ul" }, new[] { "F_isolate", "isolate(Len; mm; ul)", "ul" },
                    new[] { "F_pi", "PI * 1 ul", "ul" }, new[] { "F_e", "E * 1 ul", "ul" }, new[] { "F_neg", "-Len", "mm" },
                    new[] { "F_mod", "7 ul % 3 ul", "ul" }, new[] { "F_powop", "2 ul ^ 3 ul", "ul" }, new[] { "F_brackets", "( Len + 1 mm ) * 2 ul", "mm" } })),
                // Model parameters follow the document units: d0..d2 should come out in inches here.
                V("inch-doc", (app, doc) => {
                    Params(doc, new[] { new[] { "Len", "2 in", "in" }, new[] { "Ang", "30 deg", "deg" } });
                    Extrude(app, doc, "0.5 in", "Len", kPositiveExtentDirection);
                }, InchTemplate) };

            // The converter's test parts: each the smallest case of something a real part got wrong (case_test.py
            // checks them all in about two minutes).
            x["gaps"] = new List<Variant> {
                // A 1 mm round in the inside corner of a wall, on a flat only 0.5 mm wide (seen on a real part): with "roll along
                // sharp edges" the ball meets the corner beyond the flat; without, the round runs on past it.
                V("step-roll", (app, doc) => Step(app, doc, true)),
                V("step-plain", (app, doc) => Step(app, doc, false)),
                // The same corner with a 3 mm round: six times the flat (real parts' 20 mm rounds run on past a flat a hole narrows to 0.5 mm).
                V("step-roll-r3", (app, doc) => Step(app, doc, true, "3 mm")),
                V("step-plain-r3", (app, doc) => Step(app, doc, false, "3 mm")),
                // A hole with its mouth rounded, both copied by a circular pattern (seen on real parts).
                V("pattern-fillet", (app, doc) => {
                    Slab(app, doc, 2, "10 mm");
                    dynamic f = doc.ComponentDefinition.Features, c = app.TransientObjects.CreateObjectCollection();
                    dynamic sk = NewSketch(doc);
                    sk.SketchCircles.AddByCenterRadius(P(app, 1, 0), 0.3);
                    dynamic def = f.ExtrudeFeatures.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kCutOperation);
                    def.SetThroughAllExtent(kSymmetricExtentDirection);
                    c.Add(f.ExtrudeFeatures.Add(def));
                    c.Add(f.FilletFeatures.AddSimple(EdgeSet(app, RoundEdge(FaceAt(doc, 0.5), 0.3)), "1 mm"));
                    f.CircularPatternFeatures.Add(c, Axis(doc, 3), true, "4", "360 deg", true);
                }),
                // A boss mirrored, then the two base edges rounded in one fillet (seen on real parts).
                V("mirror-fillet", (app, doc) => {
                    Slab(app, doc, 2, "10 mm");
                    dynamic f = doc.ComponentDefinition.Features, c = app.TransientObjects.CreateObjectCollection();
                    dynamic sk = doc.ComponentDefinition.Sketches.Add(OffsetPlane(doc, "5 mm"));
                    sk.SketchCircles.AddByCenterRadius(P(app, 1, 0.5), 0.3);
                    dynamic def = f.ExtrudeFeatures.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kJoinOperation);
                    def.SetDistanceExtent("8 mm", kPositiveExtentDirection);
                    c.Add(f.ExtrudeFeatures.Add(def));
                    f.MirrorFeatures.Add(c, Plane(doc, 1));
                    dynamic edges = app.TransientObjects.CreateEdgeCollection(), top = FaceAt(doc, 0.5);
                    foreach (dynamic e in top.Edges)
                        if (EnumName("CurveTypeEnum", (int)e.CurveType) == "kCircleCurve") edges.Add(PickEdge(e));
                    f.FilletFeatures.AddSimple(edges, "1 mm");
                }),
                // A slab, a hole and a rounded mouth, rolled back with End of Part above the round (seen on a real part).
                V("end-of-part", (app, doc) => {
                    Slab(app, doc, 2, "10 mm");
                    dynamic f = doc.ComponentDefinition.Features;
                    dynamic sk = NewSketch(doc);
                    sk.SketchCircles.AddByCenterRadius(P(app, 0, 0), 0.5);
                    dynamic def = f.ExtrudeFeatures.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kCutOperation);
                    def.SetThroughAllExtent(kSymmetricExtentDirection);
                    f.ExtrudeFeatures.Add(def);
                    dynamic round = f.FilletFeatures.AddSimple(EdgeSet(app, RoundEdge(FaceAt(doc, 0.5), 0.5)), "1 mm");
                    round.SetEndOfPart(true);
                }),
                // A circle to a smaller one along a centerline that runs 2 mm past both (seen on a real part).
                V("loft-long-centerline", Lofted((l, c, app, doc) => {
                    dynamic def = l.CreateLoftDefinition(c, kNewBodyOperation);
                    dynamic sk = doc.ComponentDefinition.Sketches.Add(doc.ComponentDefinition.WorkPlanes[2]);
                    dynamic tg = app.TransientGeometry;
                    sk.SketchLines.AddByTwoPoints(sk.ModelToSketchSpace(tg.CreatePoint(0, 0, -0.2)), sk.ModelToSketchSpace(tg.CreatePoint(0, 0, 2.2)));
                    def.Centerline = sk.Profiles.AddForSurface();
                    l.Add(def);
                }, new[] { "c 1 0", "c 0.5 2" })) };
            return x;
        }

        // Parameters that Inventor rejects are skipped and reported, so one bad unit string doesn't lose the part.
        static List<string> warnings = new List<string>();

        static void Params(dynamic doc, string[][] list)
        {
            dynamic ups = doc.ComponentDefinition.Parameters.UserParameters;
            foreach (string[] p in list)
            {
                try { ups.AddByExpression(p[0], p[1], p[2]); }
                catch (Exception ex) { warnings.Add(p[0] + " (" + p[1] + ", " + p[2] + "): " + ex.Message); }
            }
        }

        // l1 (0,0)-(2,0), l2 (2,0)-(2,2), l3 (0,2)-(2,2), l4 (3,0)-(5,0) collinear with l1,
        // l5 (3,2)-(5,2) tangent to c3, l6 (1,-1)-(1,3) axis, l7 (0,0)-(0,2) = l2 mirrored about l6,
        // c1 (4,1) r0.5, c2 (6,1) r0.5, c3 (4,1) r1, p (1,0) = midpoint of l1.
        static Dictionary<string, dynamic> Shapes(dynamic app, dynamic doc)
        {
            dynamic sk = NewSketch(doc);
            dynamic lines = sk.SketchLines, circles = sk.SketchCircles;
            Dictionary<string, dynamic> s = new Dictionary<string, dynamic>();
            s["app"] = app;
            s["sketch"] = sk;
            s["l1"] = lines.AddByTwoPoints(P(app, 0, 0), P(app, 2, 0));
            s["l2"] = lines.AddByTwoPoints(P(app, 2, 0), P(app, 2, 2));
            s["l3"] = lines.AddByTwoPoints(P(app, 0, 2), P(app, 2, 2));
            s["l4"] = lines.AddByTwoPoints(P(app, 3, 0), P(app, 5, 0));
            s["l5"] = lines.AddByTwoPoints(P(app, 3, 2), P(app, 5, 2));
            s["l6"] = lines.AddByTwoPoints(P(app, 1, -1), P(app, 1, 3));
            s["l7"] = lines.AddByTwoPoints(P(app, 0, 0), P(app, 0, 2));
            s["c1"] = circles.AddByCenterRadius(P(app, 4, 1), 0.5);
            s["c2"] = circles.AddByCenterRadius(P(app, 6, 1), 0.5);
            s["c3"] = circles.AddByCenterRadius(P(app, 4, 1), 1);
            s["p"] = sk.SketchPoints.Add(P(app, 1, 0), false);
            return s;
        }

        static dynamic P(dynamic app, double x, double y)
        {
            return app.TransientGeometry.CreatePoint2d(x, y);
        }

        // Where this Generate call writes; assemblies find their generated parts from it.
        static string generatedRoot = "corpus/generated";

        static string Generated(string experiment, string file) { return Path.GetFullPath(Path.Combine(generatedRoot, experiment, file)); }

        static dynamic Place(dynamic app, dynamic doc, string part, double x, double y, double z, double angle = 0, double ax = 0, double ay = 0, double az = 1)
        {
            return PlaceFile(app, doc, Generated("asmparts", part + ".ipt"), x, y, z, angle, ax, ay, az);
        }

        // An occurrence of a file, turned by angle about the axis through the origin, then moved to (x, y, z) cm.
        static dynamic PlaceFile(dynamic app, dynamic doc, string file, double x, double y, double z, double angle = 0, double ax = 0, double ay = 0, double az = 1)
        {
            dynamic tg = app.TransientGeometry, m = tg.CreateMatrix();
            if (angle != 0) m.SetToRotation(angle, tg.CreateVector(ax, ay, az), tg.CreatePoint(0, 0, 0));
            m.SetTranslation(tg.CreateVector(x, y, z), false);
            return doc.ComponentDefinition.Occurrences.Add(file, m);
        }

        // An occurrence's outermost planar face in this direction (model axes): a plane's stored normal may point either way.
        static dynamic PlaneFace(dynamic occ, double nx, double ny, double nz)
        {
            dynamic best = null;
            double far = double.MinValue;
            foreach (dynamic f in occ.SurfaceBodies[1].Faces)
            {
                if (EnumName("SurfaceTypeEnum", (int)f.SurfaceType) != "kPlaneSurface") continue;
                dynamic n = f.Geometry.Normal;
                if (Math.Abs(Math.Abs((double)n.X * nx + (double)n.Y * ny + (double)n.Z * nz) - 1) > 1e-6) continue;
                dynamic p = f.PointOnFace;
                double along = (double)p.X * nx + (double)p.Y * ny + (double)p.Z * nz;
                if (along > far) { far = along; best = f; }
            }
            if (best == null) throw new InvalidOperationException("no face in that direction");
            return best;
        }

        static dynamic CylinderFace(dynamic occ)
        {
            foreach (dynamic f in occ.SurfaceBodies[1].Faces)
                if (EnumName("SurfaceTypeEnum", (int)f.SurfaceType) == "kCylinderSurface") return f;
            throw new InvalidOperationException("no cylinder");
        }

        // A pin occurrence's end circle, the one at this height along the pin (cm, from its own base at the occurrence's z).
        static dynamic CircleEdge(dynamic occ, double height)
        {
            double z = (double)occ.Transformation.Cell[3, 4] + height;
            foreach (dynamic e in occ.SurfaceBodies[1].Edges)
                if (EnumName("CurveTypeEnum", (int)e.CurveType) == "kCircleCurve" && Math.Abs((double)e.Geometry.Center.Z - z) < 1e-6) return e;
            throw new InvalidOperationException("no circle there");
        }

        // One suppressed constraint on each face and edge (up to 40 of each) and up to 10 vertices of a part, against a grounded box and pin.
        static void GripEverything(dynamic app, dynamic doc, string file)
        {
            dynamic cd = doc.ComponentDefinition, a = Place(app, doc, "box", 0, 0, 0), pin = Place(app, doc, "pin", 3, 3, 3), b = PlaceFile(app, doc, file, 6, 6, 6);
            a.Grounded = true;
            pin.Grounded = true;
            dynamic plane = PlaneFace(a, 0, 0, 1), axis = CylinderFace(pin), circle = CircleEdgeOf(pin), line = null, corner = a.SurfaceBodies[1].Vertices[1];
            foreach (dynamic e in a.SurfaceBodies[1].Edges)
                if (EnumName("CurveTypeEnum", (int)e.CurveType) == "kLineSegmentCurve") { line = e; break; }
            Action<Func<dynamic>> add = make => {
                try { make().Suppressed = true; }
                catch (Exception) { warnings.Add("a constraint would not build"); }
            };
            foreach (dynamic body in b.SurfaceBodies)
            {
                int n = 0;
                foreach (dynamic f in body.Faces)
                {
                    if (n++ >= 40) break;
                    string kind = EnumName("SurfaceTypeEnum", (int)f.SurfaceType);
                    if (kind == "kPlaneSurface") add(() => cd.Constraints.AddMateConstraint(f, plane, 0));
                    else if (kind == "kCylinderSurface" || kind == "kConeSurface") add(() => cd.Constraints.AddMateConstraint(f, axis, 0, kInferredLine, kInferredLine));
                    else add(() => cd.Constraints.AddTangentConstraint(plane, f, false, 0));
                }
                n = 0;
                foreach (dynamic e in body.Edges)
                {
                    if (n++ >= 40) break;
                    string kind = EnumName("CurveTypeEnum", (int)e.CurveType);
                    if (kind == "kLineSegmentCurve") add(() => cd.Constraints.AddMateConstraint(e, line, 0));
                    else if (kind == "kCircleCurve" || kind == "kCircularArcCurve") add(() => cd.Constraints.AddInsertConstraint(e, circle, true, 0));
                }
                n = 0;
                foreach (dynamic v in body.Vertices)
                {
                    if (n++ >= 10) break;
                    add(() => cd.Constraints.AddMateConstraint(v, corner, 0));
                }
            }
        }

        // Any of a pin's two end circles.
        static dynamic CircleEdgeOf(dynamic occ)
        {
            foreach (dynamic e in occ.SurfaceBodies[1].Edges)
                if (EnumName("CurveTypeEnum", (int)e.CurveType) == "kCircleCurve") return e;
            throw new InvalidOperationException("no circle");
        }

        // A 4 x 2 pixel bitmap in the temp folder, for sketch images.
        static string TinyBitmap()
        {
            byte[] b = new byte[54 + 32];
            b[0] = 0x42; b[1] = 0x4D; b[2] = (byte)b.Length; b[10] = 54; b[14] = 40; b[18] = 4; b[22] = 2; b[26] = 1; b[28] = 24; b[34] = 32;
            for (int i = 54; i < b.Length; i++) b[i] = (byte)(i * 7);
            string path = Path.Combine(Path.GetTempPath(), "invre-tiny.bmp");
            File.WriteAllBytes(path, b);
            return path;
        }

        static dynamic NewSketch(dynamic doc)
        {
            dynamic cd = doc.ComponentDefinition;
            return cd.Sketches.Add(cd.WorkPlanes[3]);   // XY plane
        }

        // {sketch, l1, l2, l3}
        static dynamic[] Lines(dynamic app, dynamic doc)
        {
            dynamic sk = NewSketch(doc);
            dynamic l1 = sk.SketchLines.AddByTwoPoints(P(app, 0, 0), P(app, 2, 0));
            dynamic l2 = sk.SketchLines.AddByTwoPoints(P(app, 2, 0), P(app, 2, 2));
            dynamic l3 = sk.SketchLines.AddByTwoPoints(P(app, 0, 2), P(app, 2, 2));
            return new dynamic[] { sk, l1, l2, l3 };
        }

        static void Formula(dynamic app, dynamic doc, string distance, string bravo)
        {
            dynamic ups = doc.ComponentDefinition.Parameters.UserParameters;
            ups.AddByExpression("Alpha", "25 mm", "mm");
            ups.AddByExpression("Bravo", bravo, "mm");
            Extrude(app, doc, "10 mm", distance, kPositiveExtentDirection);
        }

        static dynamic Circle(dynamic app, dynamic doc, string diameter)
        {
            dynamic sk = NewSketch(doc);
            dynamic c = sk.SketchCircles.AddByCenterRadius(P(app, 0, 0), 0.5);
            dynamic dim = sk.DimensionConstraints.AddDiameter(c, P(app, 1, 1));
            dim.Parameter.Expression = diameter;
            return sk;
        }

        static Action<dynamic, dynamic> Block(int operation, Action<dynamic, dynamic> extent)
        {
            return (app, doc) => OnBlock(app, doc, operation, extent);
        }

        // 20 x 20 x 10 mm block on the XY plane, then a 10 mm circle at (0, 0) extruded with the given operation;
        // extent(definition, document) sets its extent.
        static void OnBlock(dynamic app, dynamic doc, int operation, Action<dynamic, dynamic> extent)
        {
            dynamic features = doc.ComponentDefinition.Features.ExtrudeFeatures;
            dynamic sk = NewSketch(doc);
            sk.SketchLines.AddAsTwoPointRectangle(P(app, -1, -1), P(app, 1, 1));
            dynamic block = features.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kJoinOperation);
            block.SetDistanceExtent("10 mm", kPositiveExtentDirection);
            features.Add(block);
            dynamic circle = Circle(app, doc, "10 mm");
            dynamic def = features.CreateExtrudeDefinition(
                operation == kSurfaceOperation ? circle.Profiles.AddForSurface() : circle.Profiles.AddForSolid(), operation);
            extent(def, doc);
            features.Add(def);
        }

        // A square slab centred on the origin: half its side in cm, its thickness extruded symmetric about the XY plane.
        static void Slab(dynamic app, dynamic doc, double half, string thickness)
        {
            dynamic features = doc.ComponentDefinition.Features.ExtrudeFeatures;
            dynamic sk = NewSketch(doc);
            sk.SketchLines.AddAsTwoPointRectangle(P(app, -half, -half), P(app, half, half));
            dynamic def = features.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kNewBodyOperation);
            def.SetDistanceExtent(thickness, kSymmetricExtentDirection);
            features.Add(def);
        }

        // The square x 5..10, y 0..5 mm on the XY plane; add(revolveFeatures, profile, axis) revolves it about the Y
        // work axis, the X work axis ("x"), or a sketch line along Y in the same sketch ("line"). With slab, a
        // 40 x 40 x 20 mm slab comes first, so the revolve can cut or join.
        static Action<dynamic, dynamic> Ring(Action<dynamic, dynamic, dynamic> add, string axis = "y", bool slab = false)
        {
            return (app, doc) => {
                dynamic cd = doc.ComponentDefinition;
                if (slab) Slab(app, doc, 2, "20 mm");
                dynamic sk = NewSketch(doc);
                sk.SketchLines.AddAsTwoPointRectangle(P(app, 0.5, 0), P(app, 1, 0.5));
                dynamic ax = axis == "line" ? sk.SketchLines.AddByTwoPoints(P(app, 0, 0), P(app, 0, 1))
                    : cd.WorkAxes[axis == "x" ? 1 : 2];
                add(cd.Features.RevolveFeatures, sk.Profiles.AddForSolid(), ax);
            };
        }

        // A 20 x 20 x 10 mm slab (z -5..5 mm), then hole centres on a new XY sketch at (0, 0) (and (5, 5) mm with two);
        // add(holeFeatures, placement, app) makes the hole.
        static Action<dynamic, dynamic> Drill(Action<dynamic, dynamic, dynamic> add, bool two = false)
        {
            return (app, doc) => {
                Slab(app, doc, 1, "10 mm");
                dynamic sk = NewSketch(doc);
                dynamic centers = app.TransientObjects.CreateObjectCollection();
                centers.Add(sk.SketchPoints.Add(P(app, 0, 0), true));
                if (two) centers.Add(sk.SketchPoints.Add(P(app, 0.5, 0.5), true));
                dynamic holes = doc.ComponentDefinition.Features.HoleFeatures;
                add(holes, holes.CreateSketchPlacementDefinition(centers), app);
            };
        }

        // The first body's flat face lying at height z (cm), e.g. the block's top at 1.0; or at x or y = v (axis 0, 1).
        static dynamic FaceAt(dynamic doc, double v, int axis = 2)
        {
            dynamic faces = doc.ComponentDefinition.SurfaceBodies[1].Faces;
            for (int i = 1; i <= (int)faces.Count; i++)
            {
                dynamic box = faces[i].Evaluator.RangeBox;
                if (Math.Abs(Xyz(box.MinPoint)[axis] - v) < 1e-6 && Math.Abs(Xyz(box.MaxPoint)[axis] - v) < 1e-6) return faces[i];
            }
            throw new Exception("no flat face at " + "xyz"[axis] + " = " + v);
        }

        // A 20 x 20 x 10 mm slab (z -5..5 mm), then add(features, app, doc) adds the feature on its edges.
        static Action<dynamic, dynamic> Edges(Action<dynamic, dynamic, dynamic> add)
        {
            return (app, doc) => {
                Slab(app, doc, 1, "10 mm");
                add(doc.ComponentDefinition.Features, app, doc);
            };
        }

        // A block x -20..20, y -10..10 mm, z 0..10 with a wall on it (x 0..20 up to z = 20), and the block's top cut 0.5 mm
        // down except for a flat 0.5 mm wide beside the wall; then a 1 mm round in the corner at the wall's foot.
        static void Step(dynamic app, dynamic doc, bool rollSharp, string radius = "1 mm")
        {
            dynamic cd = doc.ComponentDefinition, ex = cd.Features.ExtrudeFeatures;
            dynamic a = NewSketch(doc);
            a.SketchLines.AddAsTwoPointRectangle(P(app, -2, -1), P(app, 2, 1));
            dynamic d1 = ex.CreateExtrudeDefinition(a.Profiles.AddForSolid(), kNewBodyOperation);
            d1.SetDistanceExtent("10 mm", kPositiveExtentDirection);
            ex.Add(d1);
            dynamic b = cd.Sketches.Add(OffsetPlane(doc, "10 mm"));
            b.SketchLines.AddAsTwoPointRectangle(P(app, 0, -1), P(app, 2, 1));
            dynamic d2 = ex.CreateExtrudeDefinition(b.Profiles.AddForSolid(), kJoinOperation);
            d2.SetDistanceExtent("10 mm", kPositiveExtentDirection);
            ex.Add(d2);
            dynamic c = cd.Sketches.Add(OffsetPlane(doc, "10 mm"));
            c.SketchLines.AddAsTwoPointRectangle(P(app, -2, -1), P(app, -0.05, 1));
            dynamic d3 = ex.CreateExtrudeDefinition(c.Profiles.AddForSolid(), kCutOperation);
            d3.SetDistanceExtent("0.5 mm", kNegativeExtentDirection);
            ex.Add(d3);
            // The flat at z = 10 mm, x -0.5..0: its edge along Y at the wall.
            dynamic flat = FaceAt(doc, 1.0), edge = null;
            foreach (dynamic e in flat.Edges)
            {
                dynamic p = e.StartVertex.Point, q = e.StopVertex.Point;
                if (Math.Abs((double)p.X) < 1e-6 && Math.Abs((double)q.X) < 1e-6) edge = e;
            }
            if (edge == null) throw new Exception("no edge at the wall's foot");
            cd.Features.FilletFeatures.AddSimple(EdgeSet(app, edge), radius, false, false, true, rollSharp, true, false);
        }

        // A 20 mm cube made through the API as an imported body (a non-parametric base feature), moved dx cm along X.
        static void ImportedBlock(dynamic app, dynamic doc, double dx)
        {
            dynamic box = app.TransientGeometry.CreateBox();
            box.MinPoint = app.TransientGeometry.CreatePoint(-1 + dx, -1, -1);
            box.MaxPoint = app.TransientGeometry.CreatePoint(1 + dx, 1, 1);
            doc.ComponentDefinition.Features.NonParametricBaseFeatures.Add(app.TransientBRep.CreateSolidBlock(box));
        }

        // An 8 mm hole through the block from a sketch on its top face, off centre.
        static void HoleInTop(dynamic app, dynamic doc)
        {
            dynamic ex = doc.ComponentDefinition.Features.ExtrudeFeatures;
            dynamic sk = doc.ComponentDefinition.Sketches.Add(PickFace(FaceAt(doc, 1.0)));
            sk.SketchCircles.AddByCenterRadius(P(app, 0.3, 0.2), 0.4);
            dynamic def = ex.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kCutOperation);
            def.SetThroughAllExtent(kSymmetricExtentDirection);
            ex.Add(def);
        }

        // The circular edge of this radius (cm) on a face.
        static dynamic RoundEdge(dynamic face, double r)
        {
            foreach (dynamic e in face.Edges)
                if (EnumName("CurveTypeEnum", (int)e.CurveType) == "kCircleCurve" && Math.Abs((double)e.Geometry.Radius - r) < 1e-6) return e;
            throw new Exception("no circle of radius " + r + " cm on the face");
        }

        // The edges and faces handed to features, in Naming's comparable form; written to the truth as "picked".
        static List<string> picked = new List<string>();

        static double[] Xyz(dynamic p) { return new[] { (double)p.X, (double)p.Y, (double)p.Z }; }

        static string EdgeShape(dynamic e)
        {
            try { dynamic g = e.Geometry; return Naming.Circle(Xyz(g.Center), (double)g.Radius); }
            catch (Exception) { return Naming.Line(Xyz(e.StartVertex.Point), Xyz(e.StopVertex.Point)); }
        }

        static dynamic PickEdge(dynamic e)
        {
            picked.Add("edge " + EdgeShape(e));
            return e;
        }

        static dynamic PickFace(dynamic f)
        {
            dynamic g = f.Geometry;
            if (EnumName("SurfaceTypeEnum", (int)f.SurfaceType) == "kCylinderSurface")
                picked.Add("face " + Naming.Cylinder(Xyz(g.BasePoint), Xyz(g.AxisVector), (double)g.Radius));
            else picked.Add("face " + Naming.Plane(Xyz(g.Normal), Xyz(g.RootPoint)));
            return f;
        }

        static dynamic EdgeSet(dynamic app, dynamic edge)
        {
            dynamic edges = app.TransientObjects.CreateEdgeCollection();
            edges.Add(PickEdge(edge));
            return edges;
        }

        // The slab's top face (z = 5 mm): its edge along X, or all four edges.
        static dynamic TopEdges(dynamic app, dynamic doc, bool all)
        {
            dynamic face = FaceAt(doc, 0.5);
            if (!all) return EdgeSet(app, EdgeAlong(face, true));
            dynamic edges = app.TransientObjects.CreateEdgeCollection();
            foreach (dynamic e in face.Edges) edges.Add(PickEdge(e));
            return edges;
        }

        // A straight edge of a face running along X (alongX) or along Y.
        static dynamic EdgeAlong(dynamic face, bool alongX)
        {
            foreach (dynamic e in face.Edges)
            {
                dynamic a = e.StartVertex.Point, b = e.StopVertex.Point;
                bool x = Math.Abs((double)a.Y - (double)b.Y) < 1e-6, y = Math.Abs((double)a.X - (double)b.X) < 1e-6;
                if (alongX ? x && !y : y && !x) return e;
            }
            throw new Exception("no edge along " + (alongX ? "X" : "Y"));
        }

        static dynamic OffsetPlane(dynamic doc, string offset, int from = 3)
        {
            dynamic planes = doc.ComponentDefinition.WorkPlanes;
            return planes.AddByPlaneAndOffset(planes[from], offset);
        }

        // Slab, hole and boss for the mirror and pattern tests (see Experiments); add(features, app, doc, parents)
        // mirrors or patterns the parents: the hole, or the hole and the boss (boss). With step, x 0..20 mm of the
        // slab is raised to z = 15 mm first.
        static Action<dynamic, dynamic> Copied(Action<dynamic, dynamic, dynamic, dynamic> add, bool boss = false, bool step = false)
        {
            return (app, doc) => {
                Slab(app, doc, 2, "10 mm");
                dynamic features = doc.ComponentDefinition.Features, extrudes = features.ExtrudeFeatures;
                dynamic parents = app.TransientObjects.CreateObjectCollection();
                if (step)
                {
                    dynamic sk = NewSketch(doc);
                    sk.SketchLines.AddAsTwoPointRectangle(P(app, 0, -2), P(app, 2, 2));
                    dynamic def = extrudes.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kJoinOperation);
                    def.SetDistanceExtent("15 mm", kPositiveExtentDirection);
                    extrudes.Add(def);
                }
                {
                    dynamic sk = NewSketch(doc);
                    sk.SketchCircles.AddByCenterRadius(P(app, -1, -1), 0.2);
                    dynamic def = extrudes.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kCutOperation);
                    def.SetThroughAllExtent(kSymmetricExtentDirection);
                    parents.Add(extrudes.Add(def));
                }
                if (boss)
                {
                    dynamic sk = NewSketch(doc);
                    sk.SketchCircles.AddByCenterRadius(P(app, 1, 1), 0.2);
                    dynamic def = extrudes.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kJoinOperation);
                    def.SetDistanceExtent("8 mm", kPositiveExtentDirection);
                    parents.Add(extrudes.Add(def));
                }
                add(features, app, doc, parents);
            };
        }

        // The sweep tests' profile and path (see Experiments); add(sweepFeatures, profile, path, app, doc) sweeps.
        // With block, a 40 x 40 x 40 mm block (z 0..40) comes first, for the sweep to cut.
        static Action<dynamic, dynamic> Swept(Action<dynamic, dynamic, dynamic, dynamic, dynamic> add, bool curved = false, bool block = false)
        {
            return (app, doc) => {
                dynamic cd = doc.ComponentDefinition, tg = app.TransientGeometry;
                if (block)
                {
                    dynamic b = NewSketch(doc);
                    b.SketchLines.AddAsTwoPointRectangle(P(app, -2, -2), P(app, 2, 2));
                    dynamic def = cd.Features.ExtrudeFeatures.CreateExtrudeDefinition(b.Profiles.AddForSolid(), kNewBodyOperation);
                    def.SetDistanceExtent("40 mm", kPositiveExtentDirection);
                    cd.Features.ExtrudeFeatures.Add(def);
                }
                dynamic prof = NewSketch(doc);
                prof.SketchLines.AddAsTwoPointRectangle(P(app, -0.2, -0.2), P(app, 0.2, 0.2));
                dynamic sk = cd.Sketches.Add(cd.WorkPlanes[2]);
                dynamic line = sk.SketchLines.AddByTwoPoints(sk.ModelToSketchSpace(tg.CreatePoint(0, 0, 0)), sk.ModelToSketchSpace(tg.CreatePoint(0, 0, 3)));
                if (curved)
                {
                    dynamic centre = sk.ModelToSketchSpace(tg.CreatePoint(1, 0, 3)), end = sk.ModelToSketchSpace(tg.CreatePoint(1, 0, 4));
                    // Whichever turning sense makes the arc a quarter (the sketch's normal decides it).
                    dynamic arc = sk.SketchArcs.AddByCenterStartEndPoint(centre, line.EndSketchPoint, end, true);
                    if ((double)arc.SweepAngle > 2) { arc.Delete(); sk.SketchArcs.AddByCenterStartEndPoint(centre, line.EndSketchPoint, end, false); }
                }
                dynamic sweeps = cd.Features.SweepFeatures;
                add(sweeps, prof.Profiles.AddForSolid(), sweeps.CreatePath(line), app, doc);
            };
        }

        // The loft tests' sections, each "c r z" (a circle), "s h z" (a square of half side h) or "p x z" (a sketch point
        // at (x, 0)), at height z (cm). add(loftFeatures, sections, app, doc) lofts. With block, a 40 x 40 x 20 mm block
        // (z 0..20) comes first.
        static Action<dynamic, dynamic> Lofted(Action<dynamic, dynamic, dynamic, dynamic> add, string[] sections, bool block = false)
        {
            return (app, doc) => {
                dynamic cd = doc.ComponentDefinition;
                if (block)
                {
                    dynamic b = NewSketch(doc);
                    b.SketchLines.AddAsTwoPointRectangle(P(app, -2, -2), P(app, 2, 2));
                    dynamic def = cd.Features.ExtrudeFeatures.CreateExtrudeDefinition(b.Profiles.AddForSolid(), kNewBodyOperation);
                    def.SetDistanceExtent("20 mm", kPositiveExtentDirection);
                    cd.Features.ExtrudeFeatures.Add(def);
                }
                dynamic c = app.TransientObjects.CreateObjectCollection();
                foreach (string s in sections)
                {
                    string[] w = s.Split(' ');
                    double size = double.Parse(w[1], CultureInfo.InvariantCulture), z = double.Parse(w[2], CultureInfo.InvariantCulture);
                    dynamic sk = z == 0 ? NewSketch(doc) : cd.Sketches.Add(OffsetPlane(doc, (z * 10).ToString(CultureInfo.InvariantCulture) + " mm"));
                    if (w[0] == "c") { sk.SketchCircles.AddByCenterRadius(P(app, 0, 0), size); c.Add(sk.Profiles.AddForSolid()); }
                    else if (w[0] == "s") { sk.SketchLines.AddAsTwoPointRectangle(P(app, -size, -size), P(app, size, size)); c.Add(sk.Profiles.AddForSolid()); }
                    else c.Add(sk.SketchPoints.Add(P(app, size, 0), false));
                }
                add(cd.Features.LoftFeatures, c, app, doc);
            };
        }

        static void LoftFromFace(dynamic app, dynamic doc, Action<dynamic> change)
        {
            dynamic cd = doc.ComponentDefinition, b = NewSketch(doc);
            b.SketchLines.AddAsTwoPointRectangle(P(app, -2, -2), P(app, 2, 2));
            dynamic e = cd.Features.ExtrudeFeatures.CreateExtrudeDefinition(b.Profiles.AddForSolid(), kNewBodyOperation);
            e.SetDistanceExtent("20 mm", kPositiveExtentDirection);
            cd.Features.ExtrudeFeatures.Add(e);
            dynamic face = FaceAt(doc, 2.0), onFace = cd.Sketches.Add(face);
            foreach (dynamic edge in face.Edges) onFace.AddByProjectingEntity(edge);
            dynamic top = cd.Sketches.Add(OffsetPlane(doc, "40 mm"));
            top.SketchLines.AddAsTwoPointRectangle(P(app, -1, -1), P(app, 1, 1));
            dynamic c = app.TransientObjects.CreateObjectCollection();
            c.Add(onFace.Profiles.AddForSolid());
            c.Add(top.Profiles.AddForSolid());
            dynamic def = cd.Features.LoftFeatures.CreateLoftDefinition(c, kJoinOperation);
            dynamic loft = cd.Features.LoftFeatures.Add(def);
            // These conditions are refused on a new definition, so they go on the feature's own, once it knows its neighbours.
            if (change != null) { dynamic own = loft.Definition; change(own); loft.Definition = own; }
        }

        // The sheet-metal tests' first plate: a 40 x 40 mm rectangle on the XY plane (with a 10 mm hole, or thickened the
        // other way); then more(features, app, doc).
        static Action<dynamic, dynamic> Plate(Action<dynamic, dynamic, dynamic> more, bool reversed = false, bool hole = false)
        {
            return (app, doc) => {
                dynamic features = doc.ComponentDefinition.Features, sk = NewSketch(doc);
                sk.SketchLines.AddAsTwoPointRectangle(P(app, -2, -2), P(app, 2, 2));
                if (hole) sk.SketchCircles.AddByCenterRadius(P(app, 0, 0), 0.5);
                dynamic def = features.FaceFeatures.CreateFaceFeatureDefinition(sk.Profiles.AddForSolid());
                if (reversed) def.Direction = kNegativeExtentDirection;
                features.FaceFeatures.Add(def);
                more(features, app, doc);
            };
        }

        // Flanges on the plate's edges at x = 20 mm and y = 20 mm, then a corner between their nearest side edges (the first pair that takes).
        static void CornerOfTwoFlanges(dynamic f, dynamic app, dynamic doc, bool ripped, Action<dynamic> change)
        {
            Flange(f, app, doc, "90 deg", "20 mm", false);
            dynamic second = app.TransientObjects.CreateEdgeCollection();
            foreach (dynamic body in doc.ComponentDefinition.SurfaceBodies)
                foreach (dynamic e in body.Edges)
                {
                    double[] a = Xyz(e.StartVertex.Point), b = Xyz(e.StopVertex.Point);
                    if ((int)second.Count == 0 && Math.Abs(a[1] - 2) < 1e-6 && Math.Abs(b[1] - 2) < 1e-6 && Math.Abs(a[2] - 0.05) < 1e-6 && Math.Abs(b[2] - 0.05) < 1e-6 && Math.Abs(a[0] - b[0]) > 1) second.Add(PickEdge(e));
                }
            f.FlangeFeatures.Add(f.FlangeFeatures.CreateFlangeDefinition(second, "90 deg", "20 mm"));
            List<dynamic> near = new List<dynamic>();
            foreach (dynamic body in doc.ComponentDefinition.SurfaceBodies)
                foreach (dynamic e in body.Edges)
                {
                    double[] a = Xyz(e.StartVertex.Point), b = Xyz(e.StopVertex.Point);
                    double reach = Math.Max(Math.Max(Math.Abs(a[0] - 2), Math.Abs(a[1] - 2)), Math.Max(Math.Abs(b[0] - 2), Math.Abs(b[1] - 2)));
                    if (reach < 0.5) near.Add(e);
                }
            Exception last = null;
            for (int i = 0; i < near.Count; i++)
                for (int j = i + 1; j < near.Count; j++)
                {
                    int mark = picked.Count;
                    try
                    {
                        dynamic edges = app.TransientObjects.CreateEdgeCollection();
                        edges.Add(PickEdge(near[i]));
                        edges.Add(PickEdge(near[j]));
                        dynamic def = f.CornerFeatures.CreateCornerDefinition(edges, ripped);
                        if (change != null) change(def);
                        f.CornerFeatures.Add(def);
                        return;
                    }
                    catch (Exception ex) { last = ex; picked.RemoveRange(mark, picked.Count - mark); }
                }
            throw new Exception("no pair of " + near.Count + " edges took a corner: " + (last == null ? "" : last.Message));
        }

        static void Flange(dynamic f, dynamic app, dynamic doc, string angle, string height, bool reversed)
        {
            dynamic edges = app.TransientObjects.CreateEdgeCollection();
            edges.Add(PickEdge(EdgeOnLine(doc, 2.0, 0.05)));
            dynamic def = f.FlangeFeatures.CreateFlangeDefinition(edges, angle, height);
            if (reversed) def.SetDistanceHeightExtent(height, kNegativeExtentDirection, kHeightDatumOuter);
            f.FlangeFeatures.Add(def);
        }

        // A straight edge along Y of the first body at x = x, z = z (cm).
        static dynamic EdgeOnLine(dynamic doc, double x, double z)
        {
            foreach (dynamic body in doc.ComponentDefinition.SurfaceBodies)
                foreach (dynamic e in body.Edges)
                {
                    double[] a = Xyz(e.StartVertex.Point), b = Xyz(e.StopVertex.Point);
                    if (Math.Abs(a[0] - x) < 1e-6 && Math.Abs(b[0] - x) < 1e-6 && Math.Abs(a[2] - z) < 1e-6 && Math.Abs(b[2] - z) < 1e-6 && Math.Abs(a[1] - b[1]) > 1e-6) return e;
                }
            throw new Exception("no edge along Y at x = " + x + ", z = " + z);
        }

        static Action<dynamic, dynamic> Thread(bool inside, bool rightHanded, bool fullDepth, string depth, string offset, bool reversed)
        {
            return (app, doc) => {
                dynamic features = doc.ComponentDefinition.Features, extrudes = features.ExtrudeFeatures;
                dynamic sk;
                if (inside)
                {
                    Slab(app, doc, 1, "10 mm");
                    sk = NewSketch(doc);
                    sk.SketchCircles.AddByCenterRadius(P(app, 0, 0), 0.25);
                    dynamic cut = extrudes.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kCutOperation);
                    cut.SetThroughAllExtent(kSymmetricExtentDirection);
                    extrudes.Add(cut);
                }
                else
                {
                    sk = NewSketch(doc);
                    sk.SketchCircles.AddByCenterRadius(P(app, 0, 0), 0.3);
                    dynamic def = extrudes.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kNewBodyOperation);
                    def.SetDistanceExtent("20 mm", kPositiveExtentDirection);
                    extrudes.Add(def);
                }
                // The cylindrical face, and its circular edge at the top (z = 20 mm, or 5 mm for the plate).
                dynamic face = null, edge = null;
                foreach (dynamic f in doc.ComponentDefinition.SurfaceBodies[1].Faces)
                    if (EnumName("SurfaceTypeEnum", (int)f.SurfaceType) == "kCylinderSurface") face = f;
                double top = inside ? 0.5 : 2.0;
                foreach (dynamic e in face.Edges)
                    if (Math.Abs((double)e.Geometry.Center.Z - top) < 1e-6) edge = e;
                dynamic threads = features.ThreadFeatures;
                dynamic info = threads.CreateStandardThreadInfo(inside, rightHanded, "ANSI Metric M Profile", "M6x1", inside ? "6H" : "6g");
                // Inventor keeps the start edge only for threads of a set depth (full-length ones store none).
                if (fullDepth) threads.Add(PickFace(face), edge, info, reversed, true);
                else threads.Add(PickFace(face), PickEdge(edge), info, reversed, false, depth, offset);
            };
        }

        // A 40 x 40 x 20 mm block (z 0..20); add(app, doc, shellFeatures) shells it.
        static Action<dynamic, dynamic> Shelled(Action<dynamic, dynamic, dynamic> add)
        {
            return (app, doc) => {
                dynamic sk = NewSketch(doc), extrudes = doc.ComponentDefinition.Features.ExtrudeFeatures;
                sk.SketchLines.AddAsTwoPointRectangle(P(app, -2, -2), P(app, 2, 2));
                dynamic def = extrudes.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kNewBodyOperation);
                def.SetDistanceExtent("20 mm", kPositiveExtentDirection);
                extrudes.Add(def);
                add(app, doc, doc.ComponentDefinition.Features.ShellFeatures);
            };
        }

        // Faces of the first body by height: the flat face at z = each value (cm), picked.
        static dynamic Faces(dynamic app, dynamic doc, params double[] zs)
        {
            dynamic c = app.TransientObjects.CreateFaceCollection();
            foreach (double z in zs) c.Add(PickFace(FaceAt(doc, z)));
            return c;
        }

        // The profile tests' sketch (see Experiments); keep(a path's first sketch entity) says which paths stay (all
        // if null). With overlap, two overlapping circles instead; with revolve, revolved about the Y axis (the square
        // and circles then sit at x 50..90 mm, clear of it).
        static Action<dynamic, dynamic> Loops(Func<dynamic, bool> keep, bool combine = true, bool overlap = false, bool revolve = false)
        {
            return (app, doc) => {
                dynamic sk = NewSketch(doc);
                double dx = revolve ? 7 : 0;
                if (overlap)
                {
                    sk.SketchCircles.AddByCenterRadius(P(app, 0, 0), 1);
                    sk.SketchCircles.AddByCenterRadius(P(app, 1.5, 0), 1);
                }
                else
                {
                    sk.SketchLines.AddAsTwoPointRectangle(P(app, dx - 2, -2), P(app, dx + 2, 2));
                    sk.SketchCircles.AddByCenterRadius(P(app, dx, 0), 0.5);
                    if (!revolve) sk.SketchCircles.AddByCenterRadius(P(app, 3.5, 0), 0.5);
                }
                dynamic profile = sk.Profiles.AddForSolid(combine);
                if (keep != null)
                    for (int i = (int)profile.Count; i >= 1; i--)
                        if (!keep(profile[i][1].SketchEntity)) profile[i].Delete();
                dynamic features = doc.ComponentDefinition.Features;
                if (revolve) { features.RevolveFeatures.AddFull(profile, doc.ComponentDefinition.WorkAxes[2], kNewBodyOperation); return; }
                dynamic def = features.ExtrudeFeatures.CreateExtrudeDefinition(profile, kNewBodyOperation);
                def.SetDistanceExtent("5 mm", kPositiveExtentDirection);
                features.ExtrudeFeatures.Add(def);
            };
        }

        // A profile's paths as "+" (adds material) or "-", then its sketch entities: "line (x1, y1) (x2, y2)" (ends
        // sorted) or "circle (cx, cy) r" for circles and arcs, sketch coordinates in mm to 6 places, each once,
        // sorted; paths sorted, "; ".
        static string ProfileText(dynamic profile)
        {
            List<string> paths = new List<string>();
            foreach (dynamic path in profile)
            {
                List<string> es = new List<string>();
                foreach (dynamic pe in path)
                {
                    dynamic e = pe.SketchEntity;
                    string type = TypeName((int)e.Type);
                    if (type == "kSketchLineObject")
                    {
                        string a = Xy2(e.StartSketchPoint.Geometry), b = Xy2(e.EndSketchPoint.Geometry);
                        es.Add("line " + (string.CompareOrdinal(a, b) <= 0 ? a + " " + b : b + " " + a));
                    }
                    else if (type == "kSketchCircleObject" || type == "kSketchArcObject")
                        es.Add("circle " + Xy2(e.CenterSketchPoint.Geometry) + " " + Num(Math.Round(10 * (double)e.Radius, 6)));
                    else es.Add(type);
                }
                // A sketch curve crossed by others is split into several profile entities; each entity is listed once.
                es = new List<string>(new SortedSet<string>(es, StringComparer.Ordinal));
                es.Sort(string.CompareOrdinal);
                paths.Add(((bool)path.AddsMaterial ? "+" : "-") + string.Join(", ", es.ToArray()));
            }
            paths.Sort(string.CompareOrdinal);
            return string.Join("; ", paths.ToArray());
        }

        static string Xy2(dynamic p) { return "(" + Num(Math.Round(10 * (double)p.X, 6)) + ", " + Num(Math.Round(10 * (double)p.Y, 6)) + ")"; }

        static dynamic Plane(dynamic doc, int i) { return PickPlane(doc.ComponentDefinition.WorkPlanes[i]); }

        static dynamic Axis(dynamic doc, int i)
        {
            dynamic a = doc.ComponentDefinition.WorkAxes[i];
            picked.Add("axis " + Entity(a));
            return a;
        }

        static dynamic PickPlane(dynamic wp)
        {
            picked.Add("plane " + Entity(wp));
            return wp;
        }

        // Where a mirror plane, pattern direction or axis is, in Naming's comparable form; the type name otherwise.
        static string Entity(dynamic e)
        {
            if (e == null) return null;
            string type = TypeName((int)e.Type);
            try
            {
                switch (type)
                {
                    case "kWorkPlaneObject": { dynamic g = e.Plane; return Naming.Plane(Xyz(g.Normal), Xyz(g.RootPoint)); }
                    case "kFaceObject": { dynamic g = e.Geometry; return Naming.Plane(Xyz(g.Normal), Xyz(g.RootPoint)); }
                    case "kEdgeObject": return EdgeShape(e);
                    case "kWorkAxisObject":
                    {
                        dynamic g = e.Line;
                        double[] p = Xyz(g.RootPoint), d = Xyz(g.Direction);
                        return new Naming.Shape { Kind = "line", A = p, B = new[] { p[0] + d[0], p[1] + d[1], p[2] + d[2] }, Unbounded = true }.Canonical();
                    }
                }
            }
            catch (Exception) { }
            return type;
        }

        static void Extrude(dynamic app, dynamic doc, string diameter, string distance, int direction)
        {
            dynamic sk = Circle(app, doc, diameter);
            dynamic features = doc.ComponentDefinition.Features.ExtrudeFeatures;
            dynamic def = features.CreateExtrudeDefinition(sk.Profiles.AddForSolid(), kJoinOperation);
            def.SetDistanceExtent(distance, direction);
            features.Add(def);
        }

        // Attaches to the Inventor the user has open. Never starts one: a COM launch is hidden, and a hidden
        // Inventor waits forever for a licence sign-in that needs its window (2026-10-28).
        public static dynamic Connect()
        {
            try { return Marshal.GetActiveObject("Inventor.Application"); }
            catch (COMException) { throw new Exception("Inventor is not running. Open it (signed in) and try again."); }
        }

        public static string Status()
        {
            dynamic app = Connect();
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Inventor {0}, ready={1}, visible={2}, open documents={3}\n",
                app.SoftwareVersion.DisplayVersion, app.Ready, app.Visible, app.Documents.Count);
            foreach (dynamic d in app.Documents)
                sb.AppendFormat("  {0} (windows={1}, unsaved changes={2})\n", (string)d.FullFileName, d.Views.Count, d.Dirty);
            return sb.ToString();
        }

        // Closes, without saving, documents under the given folder that have no window (ones this harness opened
        // invisibly and could not close). Older files show unsaved changes after the in-memory migration; nothing is written.
        public static string CloseUnder(string folder)
        {
            dynamic app = Connect();
            string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            List<dynamic> hits = new List<dynamic>();
            foreach (dynamic d in app.Documents)
                if (((string)d.FullFileName).StartsWith(root, StringComparison.OrdinalIgnoreCase) && (int)d.Views.Count == 0) hits.Add(d);
            StringBuilder sb = new StringBuilder();
            foreach (dynamic d in hits)
            {
                sb.AppendLine("  released " + (string)d.FullFileName);
                d.ReleaseReference();
            }
            // Closes only documents that no window or other document uses.
            if (hits.Count > 0) app.Documents.CloseAll(true);
            return sb.Length == 0 ? "  nothing to close\n" : sb.ToString();
        }

        // experiment: a name, "all", or "name/variant" for one part.
        public static string Generate(string experiment, string outDir)
        {
            dynamic app = Connect();
            StringBuilder log = new StringBuilder();
            bool any = false;
            string[] parts = experiment.Split('/');
            generatedRoot = outDir;
            foreach (KeyValuePair<string, List<Variant>> e in Experiments())
            {
                if (experiment != "all" && parts[0] != e.Key) continue;
                any = true;
                string dir = Path.Combine(outDir, e.Key);
                Directory.CreateDirectory(dir);
                foreach (Variant v in e.Value)
                {
                    if (parts.Length > 1 && parts[1] != v.Name) continue;
                    bool assembly = v.Template != null && v.Template.EndsWith(".iam");
                    string path = Path.GetFullPath(Path.Combine(dir, v.Name + (assembly ? ".iam" : ".ipt")));
                    dynamic doc = app.Documents.Add(assembly ? kAssemblyDocumentObject : kPartDocumentObject, v.Template ?? Template, false);
                    try
                    {
                        warnings.Clear();
                        picked.Clear();
                        v.Build(app, doc);
                        foreach (string w in warnings) log.AppendLine("  skipped " + e.Key + "/" + v.Name + " " + w);
                        doc.Update();
                        if (File.Exists(path)) File.Delete(path);
                        doc.SaveAs(path, false);
                        string truth = Truth(doc);
                        if (picked.Count > 0)
                            truth = truth.Substring(0, truth.LastIndexOf('}')).TrimEnd() + ",\n  \"picked\": ["
                                + string.Join(", ", picked.ConvertAll(Json).ToArray()) + "]\n}\n";
                        File.WriteAllText(Path.ChangeExtension(path, ".json"), truth);
                        log.AppendLine("  wrote " + path);
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine("  FAILED " + e.Key + "/" + v.Name + ": " + ex.Message);
                    }
                    finally
                    {
                        doc.Close(true);
                    }
                }
            }
            if (!any) log.AppendLine("Unknown experiment. Choose: all, " + string.Join(", ", Experiments().Keys));
            return log.ToString();
        }

        // Opens an existing file (without saving) and reports its truth JSON.
        public static string TruthOfFile(string file, bool entities)
        {
            dynamic app = Connect();
            // No dialogs: an unanswerable prompt (missing reference, migration) would block the harness.
            bool silent = app.SilentOperation;
            app.SilentOperation = true;
            try
            {
                dynamic doc = app.Documents.Open(Path.GetFullPath(file), false);
                try { return Truth(doc) + (entities ? SketchEntities(doc) : ""); }
                finally { doc.Close(true); }
            }
            finally { app.SilentOperation = silent; }
        }

        // Opens an assembly and never saves it: nudges each occurrence along and about each axis in memory, lets Inventor solve,
        // and reports what stayed. Put back after each. The same test build_assembly's joints get in FreeCAD.
        public static string NudgeOccurrences(string file)
        {
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            dynamic app = Connect();
            bool silent = app.SilentOperation;
            app.SilentOperation = true;
            try
            {
                dynamic doc = app.Documents.Open(Path.GetFullPath(file), false);
                try
                {
                    dynamic tg = app.TransientGeometry;
                    double step = 0.02, turn = 0.02;   // cm: 0.2 mm; radians
                    List<string> rows = new List<string>();
                    foreach (dynamic o in doc.ComponentDefinition.Occurrences)
                    {
                        if ((bool)o.Suppressed) continue;
                        bool grounded = (bool)o.Grounded;
                        List<string> moved = new List<string>(), turned = new List<string>();
                        string error = null;
                        if (!grounded)
                        {
                            try
                            {
                                dynamic home = o.Transformation;
                                double[] h = { (double)home.Cell[1, 4], (double)home.Cell[2, 4], (double)home.Cell[3, 4] };
                                dynamic box = o.RangeBox;
                                dynamic c = tg.CreatePoint(((double)box.MinPoint.X + (double)box.MaxPoint.X) / 2, ((double)box.MinPoint.Y + (double)box.MaxPoint.Y) / 2, ((double)box.MinPoint.Z + (double)box.MaxPoint.Z) / 2);
                                for (int a = 0; a < 6; a++)
                                {
                                    dynamic m = home.Copy();
                                    if (a < 3) m.Cell[a + 1, 4] = h[a] + step;
                                    else
                                    {
                                        dynamic spin = tg.CreateMatrix();
                                        spin.SetToRotation(turn, tg.CreateVector(a == 3 ? 1 : 0, a == 4 ? 1 : 0, a == 5 ? 1 : 0), c);
                                        m.PostMultiplyBy(spin);
                                    }
                                    o.Transformation = m;
                                    doc.Update();
                                    dynamic now = o.Transformation;
                                    if (a < 3) moved.Add(string.Format(inv, "[{0:R}, {1:R}, {2:R}]", ((double)now.Cell[1, 4] - h[0]) * 10, ((double)now.Cell[2, 4] - h[1]) * 10, ((double)now.Cell[3, 4] - h[2]) * 10));
                                    else
                                    {
                                        // The small turn between where it was and is now: half the skew part of R·Rhome^T.
                                        double[,] n = new double[3, 3], s = new double[3, 3];
                                        for (int i = 0; i < 3; i++)
                                            for (int j = 0; j < 3; j++) { n[i, j] = (double)now.Cell[i + 1, j + 1]; s[i, j] = (double)home.Cell[i + 1, j + 1]; }
                                        double[,] r = new double[3, 3];
                                        for (int i = 0; i < 3; i++)
                                            for (int j = 0; j < 3; j++)
                                                for (int k = 0; k < 3; k++) r[i, j] += n[i, k] * s[j, k];
                                        turned.Add(string.Format(inv, "[{0:R}, {1:R}, {2:R}]", (r[2, 1] - r[1, 2]) / 2, (r[0, 2] - r[2, 0]) / 2, (r[1, 0] - r[0, 1]) / 2));
                                    }
                                    o.Transformation = home;
                                    doc.Update();
                                }
                            }
                            catch (Exception ex) { error = ex.Message.Replace("\"", "'").Replace("\\", "/").Replace("\r", " ").Replace("\n", " "); }
                        }
                        rows.Add("{\"name\": \"" + ((string)o.Name).Replace("\"", "'") + "\", \"grounded\": " + (grounded ? "true" : "false") + ", \"moved\": [" + string.Join(", ", moved.ToArray()) + "], \"turned\": [" + string.Join(", ", turned.ToArray()) + "]" + (error == null ? "" : ", \"error\": \"" + error + "\"") + "}");
                    }
                    return "[" + string.Join(",\n", rows.ToArray()) + "]\n";
                }
                finally { doc.Close(true); }
            }
            finally { app.SilentOperation = silent; }
        }

        // Opens an assembly (without saving) and reports the freedom Inventor gives each occurrence.
        public static string OccurrenceFreedom(string file)
        {
            dynamic app = Connect();
            bool silent = app.SilentOperation;
            app.SilentOperation = true;
            try
            {
                dynamic doc = app.Documents.Open(Path.GetFullPath(file), false);
                try
                {
                    List<string> rows = new List<string>();
                    foreach (dynamic o in doc.ComponentDefinition.Occurrences)
                    {
                        string fields;
                        try
                        {
                            object[] args = { 0, new DispatchWrapper(null), 0, new DispatchWrapper(null), new DispatchWrapper(null) };
                            System.Reflection.ParameterModifier mods = new System.Reflection.ParameterModifier(5);
                            for (int i = 0; i < 5; i++) mods[i] = true;
                            ((object)o).GetType().InvokeMember("GetDegreesOfFreedom", System.Reflection.BindingFlags.InvokeMethod, null, o, args, new[] { mods }, null, null);
                            fields = "\"moves\": " + args[0] + ", \"turns\": " + args[2];
                        }
                        catch (Exception ex) { fields = "\"error\": \"" + (ex.InnerException ?? ex).Message.Replace("\"", "'").Replace("\\", "/") + "\""; }
                        string name = "?", flags = "";
                        try { name = ((string)o.Name).Replace("\"", "'"); } catch (Exception) { }
                        try { flags += "\"grounded\": " + ((bool)o.Grounded ? "true" : "false") + ", "; } catch (Exception) { }
                        rows.Add("{\"name\": \"" + name + "\", " + flags + fields + "}");
                    }
                    return "[" + string.Join(",\n", rows.ToArray()) + "]\n";
                }
                finally { doc.Close(true); }
            }
            finally { app.SilentOperation = silent; }
        }

        // Opens a part (without saving) and reports how pinned down Inventor says each sketch is, and its entities that aren't.
        public static string SketchFreedom(string file)
        {
            dynamic app = Connect();
            bool silent = app.SilentOperation;
            app.SilentOperation = true;
            try
            {
                dynamic doc = app.Documents.Open(Path.GetFullPath(file), false);
                try
                {
                    List<string> rows = new List<string>();
                    foreach (dynamic s in doc.ComponentDefinition.Sketches)
                    {
                        int free = 0, entities = 0;
                        List<string> points = new List<string>();
                        foreach (dynamic e in s.SketchEntities)
                        {
                            entities++;
                            try
                            {
                                if (!EnumName("ConstraintStatusEnum", (int)e.ConstraintStatus).Contains("Under")) continue;
                                free++;
                                if (points.Count < 8 && EnumName("ObjectTypeEnum", (int)e.Type) == "kSketchPointObject") points.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[{0:0.###}, {1:0.###}]", (double)e.Geometry.X * 10, (double)e.Geometry.Y * 10));
                            }
                            catch (Exception) { }
                        }
                        string status;
                        try { status = EnumName("ConstraintStatusEnum", (int)s.ConstraintStatus); } catch (Exception ex) { status = "? " + ex.Message.Replace("\"", "'"); }
                        rows.Add("{\"name\": \"" + ((string)s.Name).Replace("\"", "'") + "\", \"status\": \"" + status + "\", \"entities\": " + entities + ", \"free\": " + free + ", \"points\": [" + string.Join(", ", points.ToArray()) + "]}");
                    }
                    return "[" + string.Join(", ", rows.ToArray()) + "]\n";
                }
                finally { doc.Close(true); }
            }
            finally { app.SilentOperation = silent; }
        }

        // On a scratch copy only: multiplies one parameter by a factor, lets Inventor recompute, and saves the copy, so that its
        // new solid can be read like any other. Reports what happened, including features that stopped computing.
        public static string ChangeCopy(string file, string name, double factor)
        {
            string full = Path.GetFullPath(file);
            if (full.IndexOf("\\AppData\\Local\\Temp\\", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("change-copy only works on a copy in the temp folder, because it saves the file: " + full);
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            dynamic app = Connect();
            bool silent = app.SilentOperation;
            app.SilentOperation = true;
            try
            {
                dynamic doc = app.Documents.Open(full, false);
                try
                {
                    dynamic cd = doc.ComponentDefinition;
                    dynamic parameter = null;
                    foreach (dynamic p in cd.Parameters) if ((string)p.Name == name) { parameter = p; break; }
                    if (parameter == null) return "{\"error\": \"no such parameter\"}\n";
                    Dictionary<string, int> health = new Dictionary<string, int>();
                    foreach (dynamic f in cd.Features) health[(string)f.Name] = (int)f.HealthStatus;
                    double was = (double)parameter.Value;
                    string error = null;
                    try
                    {
                        parameter.Value = was * factor;
                        doc.Update();
                    }
                    catch (Exception ex) { error = ex.Message; }
                    List<string> failing = new List<string>();
                    foreach (dynamic f in cd.Features)
                    {
                        int h;
                        if (!health.TryGetValue((string)f.Name, out h) || h != (int)f.HealthStatus) failing.Add("\"" + ((string)f.Name).Replace("\"", "'") + ": " + EnumName("HealthStatusEnum", (int)f.HealthStatus) + "\"");
                    }
                    string result = string.Format(inv, "{{\"was\": {0:R}, \"now\": {1:R}, \"volume\": {2:R}, \"area\": {3:R}, \"failing\": [{4}], \"error\": {5}}}\n", was, (double)parameter.Value,
                        (double)cd.MassProperties.Volume * 1000, (double)cd.MassProperties.Area * 100, string.Join(", ", failing.ToArray()), error == null ? "null" : "\"" + error.Replace("\\", "/").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ") + "\"");
                    if (error == null && failing.Count == 0) doc.Save();
                    return result;
                }
                finally { doc.Close(true); }
            }
            finally { app.SilentOperation = silent; }
        }

        // Opens a part (without saving) and reports its solid: volume (mm3), entity counts, bounding box (mm).
        public static string Measure(string file)
        {
            dynamic app = Connect();
            bool silent = app.SilentOperation;
            app.SilentOperation = true;
            try
            {
                dynamic doc = app.Documents.Open(Path.GetFullPath(file), false);
                try
                {
                    dynamic cd = doc.ComponentDefinition;
                    int faces = 0, edges = 0, vertices = 0;
                    foreach (dynamic b in cd.SurfaceBodies) { faces += (int)b.Faces.Count; edges += (int)b.Edges.Count; vertices += (int)b.Vertices.Count; }
                    dynamic box = cd.RangeBox;
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{{\"volume\": {0:R}, \"faces\": {1}, \"edges\": {2}, \"vertices\": {3}, \"bodies\": {4}, \"bbox\": [{5:R}, {6:R}, {7:R}, {8:R}, {9:R}, {10:R}]}}\n",
                        (double)cd.MassProperties.Volume * 1000, faces, edges, vertices, (int)cd.SurfaceBodies.Count,
                        (double)box.MinPoint.X * 10, (double)box.MinPoint.Y * 10, (double)box.MinPoint.Z * 10, (double)box.MaxPoint.X * 10, (double)box.MaxPoint.Y * 10, (double)box.MaxPoint.Z * 10);
                }
                finally { doc.Close(true); }
            }
            finally { app.SilentOperation = silent; }
        }

        // Opens a file (without saving), writes its truth, then that of each document embedded in it, as they load with it. Each goes to
        // disk as soon as it is read, so a stall leaves what came before. Returns what it wrote.
        public static string TruthWithEmbedded(string file, string outDir, string baseName)
        {
            dynamic app = Connect();
            bool silent = app.SilentOperation;
            app.SilentOperation = true;
            StringBuilder log = new StringBuilder();
            try
            {
                dynamic doc = app.Documents.Open(Path.GetFullPath(file), false);
                try
                {
                    string own = Path.Combine(outDir, baseName + ".json");
                    File.WriteAllText(own, Truth(doc));
                    log.AppendLine("wrote " + own);
                    int n = 0;
                    foreach (dynamic d in doc.AllReferencedDocuments)
                    {
                        string name = (string)d.FullDocumentName;
                        if (name.IndexOf(Segment.Embedded, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        n++;
                        string text;
                        try { text = "// " + name + "\n" + Truth(d); }
                        catch (Exception ex) { log.AppendLine("  " + name + ": " + ex.Message); continue; }
                        string path = Path.Combine(outDir, baseName + ".embedded" + n + ".json");
                        File.WriteAllText(path, text);
                        log.AppendLine("wrote " + path + " for " + name);
                    }
                }
                finally { doc.Close(true); }
            }
            finally { app.SilentOperation = silent; }
            return log.ToString();
        }

        // An assembly's occurrences (with their children), constraints, joints and parameters.
        static string AssemblyTruth(dynamic doc)
        {
            dynamic cd = doc.ComponentDefinition;
            StringBuilder j = new StringBuilder();
            j.Append("{\n  \"file\": ").Append(Json((string)doc.FullFileName)).Append(",\n  \"parameters\": [");
            List<string> items = new List<string>();
            foreach (dynamic p in cd.Parameters)
                items.Add(string.Format("{{\"name\": {0}, \"expression\": {1}, \"value\": {2}, \"units\": {3}, \"kind\": {4}}}", Json((string)p.Name), Json((string)p.Expression), JsonValue((object)p.Value), Json(Convert.ToString(p.Units)), Json(TypeName((int)p.Type))));
            j.Append("\n    ").Append(string.Join(",\n    ", items.ToArray())).Append("\n  ],\n  \"occurrences\": [\n");
            List<string> occurrences = new List<string>();
            foreach (dynamic o in cd.Occurrences) OccurrenceTruth(o, "", occurrences);
            j.Append("    ").Append(string.Join(",\n    ", occurrences.ToArray())).Append("\n  ],\n  \"constraints\": [\n");
            items.Clear();
            foreach (dynamic c in cd.Constraints) items.Add(ConstraintTruth(c));
            j.Append("    ").Append(string.Join(",\n    ", items.ToArray())).Append("\n  ],\n  \"joints\": [\n");
            items.Clear();
            foreach (dynamic x in cd.Joints) items.Add(JointTruth(x));
            j.Append("    ").Append(string.Join(",\n    ", items.ToArray())).Append("\n  ],\n  \"patterns\": [\n");
            items.Clear();
            foreach (dynamic p in cd.OccurrencePatterns) items.Add(PatternTruth(p));
            j.Append("    ").Append(string.Join(",\n    ", items.ToArray())).Append("\n  ],\n  \"imates\": [\n    ").Append(ImateTruth(cd));
            j.Append("\n  ],\n  \"imateResults\": [\n    ").Append(ImateResultTruth(cd)).Append("\n  ]\n}\n");
            return j.ToString();
        }

        static string PatternTruth(dynamic p)
        {
            return "{\"name\": " + Json((string)p.Name) + ", \"type\": " + Json(TypeName((int)p.Type)) + Fields(add => {
                add("suppressed", () => Bool(p.Suppressed));
                add("columnCount", () => (string)p.ColumnCount.Expression);
                add("columnOffset", () => (string)p.ColumnOffset.Expression);
                add("columnNatural", () => Bool(p.ColumnEntityNaturalDirection));
                add("rowCount", () => (string)p.RowCount.Expression);
                add("rowOffset", () => (string)p.RowOffset.Expression);
                add("rowNatural", () => Bool(p.RowEntityNaturalDirection));
                add("elementCount", () => (string)p.ElementCount.Expression);
                add("angleOffset", () => (string)p.AngleOffset.Expression);
                add("axisNatural", () => Bool(p.AxisEntityNaturalDirection));
                add("columnEntity", () => TypeName((int)p.ColumnEntity.Type));
                add("rowEntity", () => TypeName((int)p.RowEntity.Type));
                add("axisEntity", () => TypeName((int)p.AxisEntity.Type));
                add("elements", () => {
                    List<string> es = new List<string>();
                    foreach (dynamic e in p.OccurrencePatternElements)
                    {
                        List<string> names = new List<string>();
                        foreach (dynamic o in e.Occurrences) names.Add((string)o.Name);
                        es.Add((int)e.Index + ((bool)e.Independent ? " independent " : " ") + ((bool)e.Suppressed ? "suppressed " : "") + string.Join(" + ", names.ToArray()));
                    }
                    return string.Join("; ", es.ToArray());
                });
            }) + "}";
        }

        // Every iMate of a part or assembly, then, in an assembly, the iMate joints between its occurrences.
        static string ImateTruth(dynamic cd)
        {
            List<string> items = new List<string>();
            foreach (dynamic d in cd.iMateDefinitions)
                items.Add("{\"name\": " + Json((string)d.Name) + ", \"type\": " + Json(TypeName((int)d.Type)) + Fields(add => {
                    add("index", () => ((int)d.SequenceIndex).ToString());
                    add("suppressed", () => Bool(d.Suppressed));
                    add("offset", () => (string)d.Offset.Expression);
                    add("angle", () => (string)d.Angle.Expression);
                    add("reversed", () => Bool(d.DirectionOneReversed));
                    add("inside", () => Bool(d.InsideTangency));
                    add("distance", () => (string)d.Distance.Expression);
                    add("opposed", () => Bool(d.AxesOpposed));
                    add("inferred", () => EnumName("InferredTypeEnum", (int)d.EntityInferredType));
                    add("grip", () => GripText(d.Entity));
                    add("matchList", () => { object m = d.MatchList; string[] s = m as string[]; return s == null ? null : string.Join(",", s); });
                    add("members", () => {
                        List<string> names = new List<string>();
                        foreach (dynamic k in d) names.Add((string)k.Name);
                        return string.Join(",", names.ToArray());
                    });
                }) + "}");
            return string.Join(",\n    ", items.ToArray());
        }

        static string ImateResultTruth(dynamic cd)
        {
            List<string> items = new List<string>();
            foreach (dynamic r in cd.iMateResults)
                items.Add("{\"name\": " + Json((string)r.Name) + ", \"type\": " + Json(TypeName((int)r.Type)) + Fields(add => {
                    add("suppressed", () => Bool(r.Suppressed));
                    add("composite", () => Bool(r.IsComposite));
                    add("constraints", () => {
                        List<string> names = new List<string>();
                        foreach (dynamic c in r.Constraints) names.Add((string)c.Name);
                        return string.Join(",", names.ToArray());
                    });
                    add("inputs", () => {
                        object one = null, two = null;
                        r.GetInputs(ref one, ref two);
                        dynamic a = one, b = two;
                        return (string)a.Name + " | " + (b == null ? "" : GripText(b));
                    });
                }) + "}");
            return string.Join(",\n    ", items.ToArray());
        }

        // One line per occurrence, depth first; path is its parents' names joined by "/".
        static void OccurrenceTruth(dynamic o, string path, List<string> lines)
        {
            string name = path + (string)o.Name;
            string fields = Fields(add => {
                add("matrix", () => MatrixText(o.Transformation));
                add("file", () => Path.GetFileName((string)o.ReferencedDocumentDescriptor.FullDocumentName));
                add("grounded", () => Bool(o.Grounded));
                add("visible", () => Bool(o.Visible));
                add("suppressed", () => Bool(o.Suppressed));
                add("adaptive", () => Bool(o.Adaptive));
                add("flexible", () => Bool(o.Flexible));
                add("documentType", () => EnumName("DocumentTypeEnum", (int)o.DefinitionDocumentType));
            });
            lines.Add("{\"name\": " + Json(name) + fields + "}");
            bool suppressed = o.Suppressed;
            if (suppressed) return;
            foreach (dynamic s in o.SubOccurrences) OccurrenceTruth(s, name + "/", lines);
        }

        static string MatrixText(dynamic m)
        {
            List<string> cells = new List<string>();
            for (int r = 1; r <= 3; r++)
                for (int c = 1; c <= 4; c++) cells.Add(Tok((double)m.Cell[r, c]));
            return string.Join(" ", cells.ToArray());
        }

        // Plane, line, point or cylinder in the assembly's axes, cm.
        static string GeometryText(dynamic g)
        {
            Func<dynamic, string> v3 = p => Tok((double)p.X) + " " + Tok((double)p.Y) + " " + Tok((double)p.Z);
            // These transient objects have no Type property: each form is tried in turn.
            List<Func<string>> forms = new List<Func<string>> {
                () => "plane " + v3(g.RootPoint) + " normal " + v3(g.Normal),
                () => "line " + v3(g.RootPoint) + " direction " + v3(g.Direction),
                () => "cylinder " + v3(g.BasePoint) + " axis " + v3(g.AxisVector) + " r " + Tok((double)g.Radius),
                () => "circle " + v3(g.Center) + " axis " + v3(g.Normal) + " r " + Tok((double)g.Radius),
                () => "torus " + v3(g.CenterPoint) + " axis " + v3(g.AxisVector) + " radii " + Tok((double)g.MajorRadius) + " " + Tok((double)g.MinorRadius),
                () => "sphere " + v3(g.CenterPoint) + " r " + Tok((double)g.Radius),
                () => "point " + v3(g) };
            foreach (Func<string> form in forms)
                try { return form(); } catch (Exception) { }
            return "?";
        }

        // The entity a constraint grips, in its part's own coordinates: down through the proxies to the part's own object.
        static string GripText(dynamic entity)
        {
            dynamic e = entity;
            for (int depth = 0; depth < 8; depth++)
            {
                string type = TypeName((int)e.Type);
                if (!type.Contains("Proxy")) break;
                e = e.NativeObject;
            }
            Func<dynamic, string> v3 = p => Tok((double)p.X) + " " + Tok((double)p.Y) + " " + Tok((double)p.Z);
            string kind = TypeName((int)e.Type);
            switch (kind)
            {
                case "kFaceObject": return "face " + EnumName("SurfaceTypeEnum", (int)e.SurfaceType) + " edges " + (int)e.Edges.Count + " point " + v3(e.PointOnFace);
                case "kEdgeObject": return "edge " + v3(e.StartVertex.Point) + " " + v3(e.StopVertex.Point);
                case "kVertexObject": return "vertex " + v3(e.Point);
                case "kWorkPlaneObject":
                case "kWorkAxisObject":
                case "kWorkPointObject": return kind + " " + (string)e.Name;
            }
            return kind;
        }

        static string ConstraintTruth(dynamic c)
        {
            return "{\"name\": " + Json((string)c.Name) + ", \"type\": " + Json(TypeName((int)c.Type)) + Fields(add => {
                add("suppressed", () => Bool(c.Suppressed));
                add("health", () => EnumName("HealthStatusEnum", (int)c.HealthStatus));
                add("occurrenceOne", () => (string)c.OccurrenceOne.Name);
                add("occurrenceTwo", () => (string)c.OccurrenceTwo.Name);
                add("entityOne", () => TypeName((int)c.EntityOne.Type));
                add("entityTwo", () => TypeName((int)c.EntityTwo.Type));
                add("geometryOne", () => { try { return GeometryText(c.GeometryOne); } catch (Exception ex) { return "error " + ex.Message; } });
                add("geometryTwo", () => { try { return GeometryText(c.GeometryTwo); } catch (Exception ex) { return "error " + ex.Message; } });
                add("gripOne", () => GripText(c.EntityOne));
                add("gripTwo", () => GripText(c.EntityTwo));
                add("inferredOne", () => EnumName("InferredTypeEnum", (int)c.EntityOneInferredType));
                add("inferredTwo", () => EnumName("InferredTypeEnum", (int)c.EntityTwoInferredType));
                add("offset", () => (string)c.Offset.Expression);
                add("angle", () => (string)c.Angle.Expression);
                add("distance", () => (string)c.Distance.Expression);
                add("ratio", () => (string)c.Ratio.Expression);
                add("mateSolution", () => EnumName("MateConstraintSolutionTypeEnum", (int)c.SolutionType));
                add("forward", () => Bool(c.ForwardDirection));
                add("axesOpposed", () => Bool(c.AxesOpposed));
                add("lockRotation", () => Bool(c.LockRotation));
                add("insideTangency", () => Bool(c.InsideTangency));
                add("directed", () => Bool(c.Directed));
            }) + "}";
        }

        static string JointTruth(dynamic x)
        {
            dynamic d = x.Definition;
            return "{\"name\": " + Json((string)x.Name) + Fields(add => {
                add("type", () => EnumName("AssemblyJointTypeEnum", (int)d.JointType));
                add("suppressed", () => Bool(x.Suppressed));
                add("locked", () => Bool(x.Locked));
                add("occurrenceOne", () => (string)x.OccurrenceOne.Name);
                add("occurrenceTwo", () => (string)x.OccurrenceTwo.Name);
                add("originOne", () => GeometryText(d.OriginOne.Point));
                add("originTwo", () => GeometryText(d.OriginTwo.Point));
                add("originOneOn", () => TypeName((int)d.OriginOne.Geometry.Type));
                add("originTwoOn", () => TypeName((int)d.OriginTwo.Geometry.Type));
                add("gripOne", () => GripText(d.OriginOne.Geometry));
                add("gripTwo", () => GripText(d.OriginTwo.Geometry));
                add("flipOrigin", () => Bool(d.FlipOriginDirection));
                add("flipAlignment", () => Bool(d.FlipAlignmentDirection));
                add("gap", () => (string)d.Gap.Expression);
                add("linear", () => (string)d.LinearPosition.Expression);
                add("hasLinearStart", () => Bool(d.HasLinearPositionStartLimit));
                add("linearStart", () => (string)d.LinearPositionStartLimit.Expression);
                add("hasLinearEnd", () => Bool(d.HasLinearPositionEndLimit));
                add("linearEnd", () => (string)d.LinearPositionEndLimit.Expression);
                add("angular", () => (string)d.AngularPosition.Expression);
                add("hasAngularLimits", () => Bool(d.HasAngularPositionLimits));
                add("angularStart", () => (string)d.AngularPositionStartLimit.Expression);
                add("angularEnd", () => (string)d.AngularPositionEndLimit.Expression);
            }) + "}";
        }

        public static string Truth(dynamic doc)
        {
            if ((int)doc.DocumentType == kAssemblyDocumentObject) return AssemblyTruth(doc);
            dynamic cd = doc.ComponentDefinition;
            StringBuilder j = new StringBuilder();
            j.Append("{\n  \"file\": ").Append(Json((string)doc.FullFileName)).Append(",\n  \"parameters\": [");
            dynamic ps = cd.Parameters;
            int n = (int)ps.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic p = ps[i];
                object val = p.Value;
                j.Append(i == 1 ? "\n" : ",\n");
                j.AppendFormat("    {{\"name\": {0}, \"expression\": {1}, \"value\": {2}, \"units\": {3}, \"kind\": {4}}}",
                    Json((string)p.Name), Json((string)p.Expression), JsonValue(val), Json(Convert.ToString(p.Units)),
                    Json(TypeName((int)p.Type)));
            }
            j.Append("\n  ],\n  \"features\": [");
            dynamic fs = cd.Features;
            n = (int)fs.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic f = fs[i];
                j.Append(i == 1 ? "\n" : ",\n");
                string type = TypeName((int)f.Type);
                string detail = type == "kExtrudeFeatureObject" ? ExtrudeDetail(f) : type == "kRevolveFeatureObject" ? RevolveDetail(f)
                    : type == "kHoleFeatureObject" ? HoleDetail(f) : type == "kFilletFeatureObject" ? FilletDetail(f)
                    : type == "kChamferFeatureObject" ? ChamferDetail(f) : type == "kMirrorFeatureObject" ? MirrorDetail(f)
                    : type == "kRectangularPatternFeatureObject" ? RectangularDetail(f)
                    : type == "kCircularPatternFeatureObject" ? CircularDetail(f) : type == "kThreadFeatureObject" ? ThreadDetail(f)
                    : type == "kShellFeatureObject" ? ShellDetail(f) : type == "kSweepFeatureObject" ? SweepDetail(f)
                    : type == "kLoftFeatureObject" ? LoftDetail(f) : type == "kFaceFeatureObject" ? PlateDetail(f)
                    : type == "kFlangeFeatureObject" ? FlangeDetail(f)
                    : type == "kBendFeatureObject" ? BendDetail(f) : type == "kCornerFeatureObject" ? CornerDetail(f)
                    : type == "kNonParametricBaseFeatureObject" ? BaseDetail(f) : "";
                // Whether Inventor could compute the feature, to tell decoding gaps from features that fail in Inventor.
                detail += Fields(add => add("health", () => EnumName("HealthStatusEnum", (int)f.HealthStatus)));
                j.AppendFormat("    {{\"name\": {0}, \"type\": {1}{2}}}", Json((string)f.Name), Json(type), detail);
            }
            j.Append("\n  ],\n  \"sketches\": [");
            dynamic sks = cd.Sketches;
            n = (int)sks.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic s = sks[i];
                j.Append(i == 1 ? "\n" : ",\n");
                j.AppendFormat("    {{\"name\": {0}, \"entities\": {1}, \"dimensions\": {2}, \"constraints\": {3}{4}}}",
                    Json((string)s.Name), (int)s.SketchEntities.Count, (int)s.DimensionConstraints.Count, (int)s.GeometricConstraints.Count, Curves(s));
            }
            j.Append("\n  ],\n  \"imates\": [\n    ").Append(ImateTruth(cd));
            j.Append("\n  ]\n}\n");
            return j.ToString();
        }

        // Feature settings as JSON fields; each is left out if the API doesn't give it (it throws or returns null).
        static string Fields(Action<Action<string, Func<string>>> fill)
        {
            StringBuilder sb = new StringBuilder();
            fill((key, get) => {
                try { string v = get(); if (v != null) sb.AppendFormat(", \"{0}\": {1}", key, Json(v)); }
                catch (Exception) { }
            });
            return sb.ToString();
        }

        static string Bool(dynamic b) { return (bool)b ? "true" : "false"; }

        static string ExtrudeDetail(dynamic f)
        {
            return Fields(add => {
                add("operation", () => EnumName("PartFeatureOperationEnum", (int)f.Operation));
                add("extent", () => EnumName("PartFeatureExtentEnum", (int)f.ExtentType));
                add("direction", () => EnumName("PartFeatureExtentDirectionEnum", (int)f.Extent.Direction));
                add("distance", () => (string)f.Extent.Distance.Expression);
                add("taper", () => (string)f.TaperAngle.Expression);
                add("extentTwo", () => EnumName("PartFeatureExtentEnum", (int)f.Definition.ExtentTwoType));
                add("distanceTwo", () => (string)f.Definition.ExtentTwo.Distance.Expression);
                add("taperTwo", () => (string)f.Definition.TaperAngleTwo.Expression);
                add("profile", () => ProfileText(f.Profile));
            });
        }

        static string RevolveDetail(dynamic f)
        {
            return Fields(add => {
                add("operation", () => EnumName("PartFeatureOperationEnum", (int)f.Operation));
                add("extent", () => EnumName("PartFeatureExtentEnum", (int)f.ExtentType));
                add("direction", () => EnumName("PartFeatureExtentDirectionEnum", (int)f.Extent.Direction));
                add("angle", () => (string)f.Extent.Angle.Expression);
                add("axis", () => TypeName((int)f.AxisEntity.Type));
                add("sketch", () => (string)f.Profile.Parent.Name);
                add("twoDirectional", () => Bool(f.IsTwoDirectional));
                add("extentTwo", () => (bool)f.IsTwoDirectional ? EnumName("PartFeatureExtentEnum", (int)f.ExtentTwoType) : null);
                add("angleTwo", () => (bool)f.IsTwoDirectional ? (string)f.ExtentTwo.Angle.Expression : null);
                add("profile", () => ProfileText(f.Profile));
            });
        }

        // Feature options, then per edge set i (from 1): radius<i> (+ its parameter name), edges<i>, allFillets<i>, allRounds<i>.
        static string FilletDetail(dynamic f)
        {
            return Fields(add => {
                add("filletType", () => EnumName("FilletTypeEnum", (int)f.FilletDefinition.FilletType));
                add("automaticEdgeChain", () => Bool(f.AutomaticEdgeChain));
                add("smoothRadiusTransition", () => Bool(f.SmoothRadiusTransition));
                add("rollAlongSharpEdges", () => Bool(f.RollAlongSharpEdges));
                add("rollingBallWherePossible", () => Bool(f.RollingBallWherePossible));
                add("preserveAllFeatures", () => Bool(f.PreserveAllFeatures));
                dynamic def = f.FilletDefinition;
                int n = 0;
                try { n = (int)def.EdgeSetCount; } catch (Exception) { }
                add("sets", () => n.ToString(CultureInfo.InvariantCulture));
                for (int i = 1; i <= n; i++)
                {
                    int k = i;
                    string s = k.ToString(CultureInfo.InvariantCulture);
                    add("radius" + s, () => (string)def.EdgeSetItem[k].Radius.Expression);
                    add("radius" + s + "Param", () => (string)def.EdgeSetItem[k].Radius.Name);
                    // The API fails (E_FAIL) on a saved fillet's Edges, so this is left out; kept in case a version allows it.
                    add("edges" + s, () => ((int)def.EdgeSetItem[k].Edges.Count).ToString(CultureInfo.InvariantCulture));
                    add("allFillets" + s, () => Bool(def.EdgeSetItem[k].AllFillets));
                    add("allRounds" + s, () => Bool(def.EdgeSetItem[k].AllRounds));
                }
            });
        }

        static string ChamferDetail(dynamic f)
        {
            return Fields(add => {
                Action<string, Func<dynamic>> size = (key, get) => {
                    add(key, () => (string)get().Expression);
                    add(key + "Param", () => (string)get().Name);
                };
                add("chamferType", () => EnumName("ChamferDefinitionTypeEnum", (int)f.DefinitionType));
                size("distance", () => f.Definition.Distance);
                size("angle", () => f.Definition.Angle);
                size("distanceOne", () => f.Definition.DistanceOne);
                size("distanceTwo", () => f.Definition.DistanceTwo);
                add("edges", () => ((int)f.ChamferedEdges.Count).ToString(CultureInfo.InvariantCulture));
                // Where the edges are, where the API gives them (older parts), as Naming's comparable form.
                add("edgesAt", () => {
                    List<string> at = new List<string>();
                    foreach (dynamic e in f.ChamferedEdges) at.Add(EdgeShape(e));
                    return string.Join("; ", at.ToArray());
                });
                add("face", () => f.Definition.Face == null ? null : "true");
                add("automaticEdgeChain", () => Bool(f.AutomaticEdgeChain));
                add("cornerSetback", () => Bool(f.Definition.CornerSetback));
                add("preserveAllFeatures", () => Bool(f.PreserveAllFeatures));
            });
        }

        // Each size also with its parameter name ("<key>Param"), to tie the property slots to the sizes.
        static string HoleDetail(dynamic f)
        {
            return Fields(add => {
                Action<string, Func<dynamic>> size = (key, get) => {
                    add(key, () => (string)get().Expression);
                    add(key + "Param", () => (string)get().Name);
                };
                add("holeType", () => EnumName("HoleTypeEnum", (int)f.HoleType));
                add("extent", () => EnumName("PartFeatureExtentEnum", (int)f.ExtentType));
                add("direction", () => EnumName("PartFeatureExtentDirectionEnum", (int)f.Extent.Direction));
                size("distance", () => f.Extent.Distance);
                size("diameter", () => f.HoleDiameter);
                size("cboreDiameter", () => f.CBoreDiameter);
                size("cboreDepth", () => f.CBoreDepth);
                size("csinkDiameter", () => f.CSinkDiameter);
                size("csinkAngle", () => f.CSinkAngle);
                size("spotFaceDiameter", () => f.SpotFaceDiameter);
                size("spotFaceDepth", () => f.SpotFaceDepth);
                size("tipAngle", () => f.BottomTipAngle);
                add("flatBottom", () => Bool(f.FlatBottom));
                add("drillPoint", () => EnumName("HoleDrillPointTypeEnum", (int)f.DrillPointType));
                add("placement", () => EnumName("HolePlacementTypeEnum", (int)f.PlacementType));
                add("centers", () => ((int)f.HoleCenterPoints.Count).ToString(CultureInfo.InvariantCulture));
                add("tapped", () => Bool(f.Tapped));
                add("threadType", () => (string)f.TapInfo.ThreadType);
                add("thread", () => (string)f.TapInfo.ThreadDesignation);
                add("threadClass", () => (string)f.TapInfo.Class);
                add("rightHanded", () => Bool(f.TapInfo.RightHanded));
                add("fullThreadDepth", () => Bool(f.TapInfo.FullThreadDepth));
                size("threadDepth", () => f.TapInfo.ThreadDepth);
            });
        }

        // What a mirror or pattern copies, and each copy (pattern element): its index, whether it is suppressed, and its
        // transform as rows of the 3 x 4 matrix (rotation, then translation in mm), e.g. "2: 1 0 0 8, 0 1 0 0, 0 0 1 0".
        static void Copies(Action<string, Func<string>> add, dynamic f)
        {
            add("parents", () => {
                List<string> names = new List<string>();
                foreach (dynamic p in f.ParentFeatures)
                {
                    string n;
                    try { n = (string)p.Name; } catch (Exception) { n = TypeName((int)p.Type); }
                    names.Add(n);
                }
                return string.Join(", ", names.ToArray());
            });
            add("computeType", () => EnumName("PatternComputeTypeEnum", (int)f.ComputeType));
            add("newBody", () => Bool(f.NewBodyOperation));
            add("elements", () => ((int)f.PatternElements.Count).ToString(CultureInfo.InvariantCulture));
            add("transforms", () => {
                List<string> all = new List<string>();
                foreach (dynamic el in f.PatternElements)
                {
                    dynamic m = el.Transform;
                    List<string> rows = new List<string>();
                    for (int r = 1; r <= 3; r++)
                        rows.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", Num((double)m.Cell[r, 1]), Num((double)m.Cell[r, 2]),
                            Num((double)m.Cell[r, 3]), Num(10 * (double)m.Cell[r, 4])));
                    string s = "";
                    try { if ((bool)el.Suppressed) s = " suppressed"; } catch (Exception) { }
                    all.Add(((int)el.Index).ToString(CultureInfo.InvariantCulture) + s + ": " + string.Join(", ", rows.ToArray()));
                }
                return string.Join("; ", all.ToArray());
            });
        }

        static string MirrorDetail(dynamic f)
        {
            return Fields(add => {
                add("plane", () => Entity(f.MirrorPlaneEntity));
                add("planeType", () => TypeName((int)f.MirrorPlaneEntity.Type));
                add("ofBody", () => Bool(f.MirrorOfBody));
                add("removeOriginal", () => Bool(f.Definition.RemoveOriginal));
                Copies(add, f);
            });
        }

        // Sizes with their parameter names ("<key>Param"), as for holes.
        static string RectangularDetail(dynamic f)
        {
            return Fields(add => {
                Action<string, Func<dynamic>> size = (key, get) => {
                    add(key, () => (string)get().Expression);
                    add(key + "Param", () => (string)get().Name);
                };
                add("xEntity", () => Entity(f.XDirectionEntity));
                add("xEntityType", () => TypeName((int)f.XDirectionEntity.Type));
                add("naturalX", () => Bool(f.NaturalXDirection));
                size("xCount", () => f.XCount);
                size("xSpacing", () => f.XSpacing);
                add("xSpacingType", () => EnumName("PatternSpacingTypeEnum", (int)f.XDirectionSpacingType));
                add("xMidPlane", () => Bool(f.XDirectionMidPlanePattern));
                add("yEntity", () => f.YDirectionEntity == null ? null : Entity(f.YDirectionEntity));
                add("naturalY", () => f.YDirectionEntity == null ? null : Bool(f.NaturalYDirection));
                size("yCount", () => f.YCount);
                size("ySpacing", () => f.YSpacing);
                add("ySpacingType", () => EnumName("PatternSpacingTypeEnum", (int)f.YDirectionSpacingType));
                add("yMidPlane", () => Bool(f.YDirectionMidPlanePattern));
                add("orientation", () => EnumName("PatternOrientationEnum", (int)f.OrientationMethod));
                add("ofBody", () => Bool(f.PatternOfBody));
                Copies(add, f);
            });
        }

        static string CircularDetail(dynamic f)
        {
            return Fields(add => {
                Action<string, Func<dynamic>> size = (key, get) => {
                    add(key, () => (string)get().Expression);
                    add(key + "Param", () => (string)get().Name);
                };
                add("axis", () => Entity(f.AxisEntity));
                add("axisType", () => TypeName((int)f.AxisEntity.Type));
                add("naturalAxis", () => Bool(f.NaturalAxisDirection));
                size("count", () => f.Count);
                size("angle", () => f.Angle);
                add("fitWithinAngle", () => Bool(f.FitWithinAngle));
                add("midPlane", () => Bool(f.MidPlanePattern));
                add("lockRotation", () => Bool(f.Definition.LockRotation));
                add("positioning", () => EnumName("PatternPositioningMethodEnum", (int)f.Definition.PositioningMethod));
                add("ofBody", () => Bool(f.PatternOfBody));
                Copies(add, f);
            });
        }

        // A path's or rail's sketch curves, in ProfileText's form, each once, sorted, "; " between; the sketch's name first.
        static string PathText(dynamic path)
        {
            List<string> es = new List<string>();
            string sketch = null;
            foreach (dynamic pe in path)
            {
                dynamic e = pe.SketchEntity;
                if (sketch == null) { try { sketch = (string)e.Parent.Name; } catch (Exception) { } }
                es.Add(EntityText(e));
            }
            return (sketch ?? "?") + ": " + string.Join(", ", new List<string>(new SortedSet<string>(es, StringComparer.Ordinal)).ToArray());
        }

        static string EntityText(dynamic e)
        {
            string type = TypeName((int)e.Type);
            if (type == "kSketchLineObject")
            {
                string a = Xy2(e.StartSketchPoint.Geometry), b = Xy2(e.EndSketchPoint.Geometry);
                return "line " + (string.CompareOrdinal(a, b) <= 0 ? a + " " + b : b + " " + a);
            }
            if (type == "kSketchCircleObject" || type == "kSketchArcObject") return "circle " + Xy2(e.CenterSketchPoint.Geometry) + " " + Num(Math.Round(10 * (double)e.Radius, 6));
            if (type == "kSketchPointObject") return "point " + Xy2(e.Geometry);
            return type;
        }

        static string SweepDetail(dynamic f)
        {
            return Fields(add => {
                dynamic def = f.Definition;
                add("operation", () => EnumName("PartFeatureOperationEnum", (int)f.Operation));
                add("sweepType", () => EnumName("SweepTypeEnum", (int)def.SweepType));
                add("orientation", () => EnumName("SweepProfileOrientationEnum", (int)def.ProfileOrientation));
                add("scaling", () => EnumName("SweepProfileScalingEnum", (int)def.ProfileScaling));
                add("taper", () => (string)def.TaperAngle.Expression);
                add("twist", () => (string)def.TwistAngle.Expression);
                add("sketch", () => (string)f.Profile.Parent.Name);
                add("profile", () => ProfileText(f.Profile));
                add("path", () => PathText(def.Path));
                add("guideRail", () => def.GuideRail == null ? null : PathText(def.GuideRail));
            });
        }

        // A section: "<sketch>: <profile>" for a profile, else a sketch point's or other entity's text.
        static string SectionText(dynamic s)
        {
            string type = TypeName((int)s.Type);
            return type == "kProfileObject" ? (string)s.Parent.Name + ": " + ProfileText(s) : type == "kSketchPointObject" ? (string)s.Parent.Name + ": " + EntityText(s) : type;
        }

        static string LoftDetail(dynamic f)
        {
            return Fields(add => {
                Action<string, Func<dynamic>> size = (key, get) => add(key, () => (string)get().Expression);
                add("operation", () => EnumName("PartFeatureOperationEnum", (int)f.Operation));
                add("loftType", () => EnumName("LoftTypeEnum", (int)f.LoftType));
                add("closed", () => Bool(f.Closed));
                add("mergeTangentFaces", () => Bool(f.MergeTangentFaces));
                add("firstCondition", () => EnumName("LoftConditionEnum", (int)f.FirstSectionCondition));
                add("lastCondition", () => EnumName("LoftConditionEnum", (int)f.LastSectionCondition));
                size("firstAngle", () => f.FirstSectionAngle);
                size("firstImpact", () => f.FirstSectionImpact);
                size("lastAngle", () => f.LastSectionAngle);
                size("lastImpact", () => f.LastSectionImpact);
                add("firstPlane", () => (string)f.FirstSectionTangentPlane.Name);
                add("lastPlane", () => (string)f.LastSectionTangentPlane.Name);
                add("firstReversed", () => Bool(f.FirstSectionDirectionReversed));
                add("lastReversed", () => Bool(f.LastSectionDirectionReversed));
                add("sections", () => {
                    List<string> all = new List<string>();
                    foreach (dynamic s in f.Sections) all.Add(SectionText(s));
                    return string.Join(" | ", all.ToArray());
                });
                add("rails", () => ((int)f.LoftRails.Count).ToString(CultureInfo.InvariantCulture));
                add("rail1", () => (int)f.LoftRails.Count == 0 ? null : SectionText(f.LoftRails[1].Rail));
                add("centerline", () => f.Centerline == null ? null : SectionText(f.Centerline));
            });
        }

        // A sketch's splines, ellipses and text as a JSON field, each curve as space-separated words and numbers (mm), so
        // they compare token by token: see CurveText.
        static string Curves(dynamic sketch)
        {
            List<string> all = new List<string>();
            foreach (dynamic e in sketch.SketchEntities)
            {
                string text = null;
                try { text = CurveText(e); } catch (Exception ex) { text = "error " + TypeName((int)e.Type) + " " + ex.Message; }
                if (text != null) all.Add(text);
            }
            foreach (dynamic b in sketch.TextBoxes)
            {
                try { all.Add("text \"" + (string)b.Text + "\" " + Tok(10 * (double)b.Origin.X) + " " + Tok(10 * (double)b.Origin.Y)); }
                catch (Exception ex) { all.Add("error text " + ex.Message); }
                // Separately from the words above, as they are compared with the decoded text.
                all[all.Count - 1] += TextBoxExtras(b);
            }
            foreach (dynamic i in sketch.SketchImages)
            {
                StringBuilder s = new StringBuilder("image");
                Action<string, Func<string>> add = (name, get) => { try { s.Append(" " + name + " " + get()); } catch (Exception) { s.Append(" " + name + " ?"); } };
                add("name", () => "'" + (string)i.Name + "'");
                add("width", () => Tok(10 * (double)i.Width));
                add("height", () => Tok(10 * (double)i.Height));
                add("position", () => Tok(10 * (double)i.Position.X) + " " + Tok(10 * (double)i.Position.Y));
                add("rotation", () => Tok((double)i.Rotation));
                add("link", () => ((bool)i.Linked).ToString());
                add("visible", () => ((bool)i.Visible).ToString());
                add("file", () => Path.GetFileName((string)i.FullFileName));
                all.Add(s.ToString());
            }
            return all.Count == 0 ? "" : ", \"curves\": " + Json(string.Join("; ", all.ToArray())).Replace("\\\"", "'");
        }

        static string Tok(double v) { return (Math.Round(v, 6) + 0.0).ToString("R", CultureInfo.InvariantCulture); }

        static string TextBoxExtras(dynamic b)
        {
            try
            {
                if ((bool)b.Fitted) return "";
                return " box " + Tok(10 * (double)b.Width) + " " + Tok(10 * (double)b.Height) + " rotation " + Tok((double)b.Rotation);
            }
            catch (Exception) { return " box ?"; }
        }

        static string CurveText(dynamic e)
        {
            string type = TypeName((int)e.Type);
            Func<dynamic, string> xy = p => Tok(10 * (double)p.X) + " " + Tok(10 * (double)p.Y);
            switch (type)
            {
                case "kSketchEllipseObject":
                case "kSketchEllipticalArcObject":
                {
                    string s = (type == "kSketchEllipseObject" ? "ellipse" : "ellipse-arc") + " center " + xy(e.CenterSketchPoint.Geometry) + " major " + Tok((double)e.MajorAxisVector.X) + " " + Tok((double)e.MajorAxisVector.Y)
                        + " radii " + Tok(10 * (double)e.MajorRadius) + " " + Tok(10 * (double)e.MinorRadius);
                    if (type == "kSketchEllipticalArcObject") s += " ends " + xy(e.StartSketchPoint.Geometry) + " " + xy(e.EndSketchPoint.Geometry);
                    return s + (RefFlag(e) ? " reference" : "");
                }
                case "kSketchSplineObject":
                case "kSketchFixedSplineObject":
                case "kSketchControlPointSplineObject":
                case "kSketchEquationCurveObject":
                {
                    string s = type.Replace("kSketch", "").Replace("Object", "").ToLowerInvariant();
                    if (type == "kSketchSplineObject")
                    {
                        // A projected spline has no fit points (the API fails on them): it is the bare curve.
                        string fit = null;
                        try
                        {
                            fit = " fit";
                            for (int i = 1; i <= (int)e.FitPointCount; i++) fit += " " + xy(e.FitPoint[i].Geometry);
                            fit += " method " + EnumName("SplineFitMethodEnum", (int)e.FitMethod);
                        }
                        catch (Exception) { fit = " projected"; }
                        s += fit;
                    }
                    if (type == "kSketchControlPointSplineObject")
                    {
                        s += " control";
                        for (int i = 1; i <= (int)e.ControlPointCount; i++) s += " " + xy(e.ControlPoint[i].Geometry);
                    }
                    return s + " " + BSplineText(e.Geometry) + (RefFlag(e) ? " reference" : "");
                }
            }
            return null;
        }

        static bool RefFlag(dynamic e) { try { return (bool)e.Reference; } catch (Exception) { return false; } }

        // "order O closed C rational R periodic P poles x y ... knots k ... weights w ..." (poles in mm).
        static string BSplineText(dynamic g)
        {
            int order = 0, nPoles = 0, nKnots = 0;
            bool rational = false, periodic = false, closed = false;
            g.GetBSplineInfo(ref order, ref nPoles, ref nKnots, ref rational, ref periodic, ref closed);
            double[] poles = new double[2 * nPoles], knots = new double[nKnots], weights = new double[rational ? nPoles : 0];
            g.GetBSplineData(ref poles, ref knots, ref weights);
            StringBuilder s = new StringBuilder("order " + order + " closed " + closed.ToString().ToLowerInvariant() + " rational " + rational.ToString().ToLowerInvariant() + " periodic " + periodic.ToString().ToLowerInvariant() + " poles");
            foreach (double p in poles) s.Append(" " + Tok(10 * p));
            s.Append(" knots");
            foreach (double k in knots) s.Append(" " + Tok(k));
            if (rational) { s.Append(" weights"); foreach (double w in weights) s.Append(" " + Tok(w)); }
            return s.ToString();
        }

        static string PlateDetail(dynamic f)
        {
            return Fields(add => {
                dynamic def = f.Definition;
                add("operation", () => EnumName("PartFeatureOperationEnum", (int)def.Operation));
                add("direction", () => EnumName("PartFeatureExtentDirectionEnum", (int)def.Direction));
                add("sketch", () => (string)def.Profile.Parent.Name);
                add("profile", () => ProfileText(def.Profile));
            });
        }

        static string FlangeDetail(dynamic f)
        {
            return Fields(add => {
                dynamic def = f.Definition;
                add("flangeAngle", () => (string)def.FlangeAngle.Expression);
                add("height", () => (string)def.HeightExtent.Distance.Expression);
                add("heightDirection", () => EnumName("PartFeatureExtentDirectionEnum", (int)def.HeightExtent.Direction));
                add("bendRadius", () => (string)def.BendRadius.Expression);
                add("edges", () => ((int)def.Edges.Count).ToString(CultureInfo.InvariantCulture));
            });
        }

        static string BendDetail(dynamic f)
        {
            return Fields(add => {
                dynamic def = f.Definition;
                add("bendRadius", () => (string)def.BendRadius.Expression);
                add("bendEdges", () => ((int)def.Edges.Count).ToString(CultureInfo.InvariantCulture));
            });
        }

        static string CornerDetail(dynamic f)
        {
            return Fields(add => {
                dynamic def = f.Definition;
                add("cornerType", () => EnumName("CornerTypeEnum", (int)def.CornerType));
                add("cornerEdges", () => ((int)def.Edges.Count).ToString(CultureInfo.InvariantCulture));
                add("ripped", () => Bool(def.IsRippedCorner));
                add("gapWidth", () => { object g = def.GapWidth; dynamic p = g; try { return (string)p.Expression; } catch (Exception) { return Convert.ToString(g, CultureInfo.InvariantCulture); } });
                add("definitionType", () => EnumName("CornerDefinitionTypeEnum", (int)def.CornerDefinitionType));
                add("alignedExtension", () => Bool(def.AlignedCornerExtension));
                add("overlap", () => ((double)def.PercentOverlap).ToString("R", CultureInfo.InvariantCulture));
                add("reliefShape", () => EnumName("CornerReliefShapeEnum", (int)def.CornerOptions.CornerReliefShape));
                add("twoBend", () => Bool(def.CornerOptions.IsTwoBend));
            });
        }

        static string BaseDetail(dynamic f)
        {
            return Fields(add => {
                add("isSolid", () => Bool(f.IsSolid));
                add("isComposite", () => Bool(f.IsComposite));
                add("baseFaces", () => ((int)f.BaseSolidBody.Faces.Count).ToString(CultureInfo.InvariantCulture));
            });
        }

        static string ThreadDetail(dynamic f)
        {
            return Fields(add => {
                Action<string, Func<dynamic>> size = (key, get) => {
                    add(key, () => (string)get().Expression);
                    add(key + "Param", () => (string)get().Name);
                };
                add("threadType", () => (string)f.ThreadInfo.ThreadType);
                add("thread", () => (string)f.ThreadInfo.ThreadDesignation);
                add("threadClass", () => (string)f.ThreadInfo.Class);
                add("internal", () => Bool(f.ThreadInfo.Internal));
                add("rightHanded", () => Bool(f.ThreadInfo.RightHanded));
                add("fullDepth", () => Bool(f.FullDepth));
                add("directionReversed", () => Bool(f.DirectionReversed));
                size("depth", () => f.ThreadDepth);
                size("offset", () => f.ThreadOffset);
                add("faceRadius", () => Num(10 * (double)f.ThreadedFace[1].Geometry.Radius));
            });
        }

        static string ShellDetail(dynamic f)
        {
            return Fields(add => {
                dynamic def = f.Definition;
                add("direction", () => EnumName("ShellDirectionEnum", (int)def.Direction));
                add("thickness", () => (string)def.Thickness.Expression);
                add("thicknessParam", () => (string)def.Thickness.Name);
                add("removedFaces", () => ((int)def.InputFaces.Count).ToString(CultureInfo.InvariantCulture));
                add("faceSets", () => ((int)def.FaceSetCount).ToString(CultureInfo.InvariantCulture));
                add("faceSet1", () => (int)def.FaceSetCount == 0 ? null : (string)def.FaceSetItem[1].Thickness.Expression
                    + " on " + ((int)def.FaceSetItem[1].Faces.Count).ToString(CultureInfo.InvariantCulture));
            });
        }

        static Dictionary<string, Dictionary<int, string>> enumNames = new Dictionary<string, Dictionary<int, string>>();

        static string EnumName(string enumName, int value)
        {
            Dictionary<int, string> names;
            if (!enumNames.TryGetValue(enumName, out names))
            {
                names = new Dictionary<int, string>();
                Type t = Assembly.ReflectionOnlyLoadFrom(InteropPath).GetType("Inventor." + enumName);
                if (t != null)
                    foreach (FieldInfo fi in t.GetFields())
                        if (fi.IsLiteral) names[Convert.ToInt32(fi.GetRawConstantValue())] = fi.Name;
                enumNames[enumName] = names;
            }
            string name;
            return names.TryGetValue(value, out name) ? name : value.ToString(CultureInfo.InvariantCulture);
        }

        public static string EnumValues(string enumName)
        {
            Type t = Assembly.ReflectionOnlyLoadFrom(InteropPath).GetType("Inventor." + enumName);
            if (t == null) return "unknown enum: " + enumName + "\n";
            StringBuilder sb = new StringBuilder();
            foreach (FieldInfo f in t.GetFields())
                if (f.IsLiteral) sb.AppendFormat("{0} = {1}\n", f.Name, f.GetRawConstantValue());
            return sb.ToString();
        }

        // Methods and properties of an Inventor API interface, with parameter names and types.
        public static string Members(string typeName)
        {
            Type t = Assembly.ReflectionOnlyLoadFrom(InteropPath).GetType("Inventor." + typeName);
            if (t == null) return "unknown type: " + typeName + "\n";
            StringBuilder sb = new StringBuilder();
            foreach (MethodInfo m in t.GetMethods())
            {
                if (m.IsSpecialName && !m.Name.StartsWith("get_")) continue;
                List<string> ps = new List<string>();
                foreach (ParameterInfo p in m.GetParameters())
                    ps.Add((p.IsOptional ? "[" : "") + p.ParameterType.Name + " " + p.Name + (p.IsOptional ? "]" : ""));
                string name = m.Name.StartsWith("get_") ? m.Name.Substring(4) : m.Name + "(" + string.Join(", ", ps.ToArray()) + ")";
                sb.AppendFormat("{0} : {1}\n", name, m.ReturnType.Name);
            }
            return sb.ToString();
        }

        // One line per sketch entity: type, reference/construction flags, and its defining coordinates (cm).
        public static string SketchEntities(dynamic doc)
        {
            StringBuilder sb = new StringBuilder();
            dynamic sks = doc.ComponentDefinition.Sketches;
            int n = (int)sks.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic s = sks[i];
                sb.AppendFormat("{0}\n", (string)s.Name);
                foreach (dynamic e in s.SketchEntities)
                {
                    string kind = TypeName((int)e.Type);
                    string flags = "";
                    try { if ((bool)e.Reference) flags += " reference"; } catch (Exception) { }
                    try { if ((bool)e.Construction) flags += " construction"; } catch (Exception) { }
                    string where = "";
                    try
                    {
                        if (kind == "kSketchPointObject") where = Xy(e.Geometry);
                        else if (kind == "kSketchLineObject") where = Xy(e.Geometry.StartPoint) + " -> " + Xy(e.Geometry.EndPoint);
                        else if (kind == "kSketchCircleObject" || kind == "kSketchArcObject")
                            where = "center " + Xy(e.CenterSketchPoint.Geometry) + " r=" + Num((double)e.Radius);
                    }
                    catch (Exception) { where = "?"; }
                    sb.AppendFormat("  {0,-24} {1}{2}\n", kind.Replace("Object", "").Replace("kSketch", ""), where, flags);
                }
            }
            return sb.ToString();
        }

        static string Num(double v) { return Math.Round(v, 9).ToString("R", CultureInfo.InvariantCulture); }

        static string Xy(dynamic p) { return "(" + Num((double)p.X) + ", " + Num((double)p.Y) + ")"; }

        static Dictionary<int, string> objectTypeNames;

        // ObjectTypeEnum value -> name, read from the interop assembly when available.
        static string TypeName(int value)
        {
            if (objectTypeNames == null)
            {
                objectTypeNames = new Dictionary<int, string>();
                try
                {
                    Type t = Assembly.ReflectionOnlyLoadFrom(InteropPath).GetType("Inventor.ObjectTypeEnum");
                    foreach (FieldInfo f in t.GetFields())
                        if (f.IsLiteral) objectTypeNames[Convert.ToInt32(f.GetRawConstantValue())] = f.Name;
                }
                catch (Exception) { }
            }
            string name;
            return objectTypeNames.TryGetValue(value, out name) ? name : value.ToString(CultureInfo.InvariantCulture);
        }

        static string JsonValue(object v)
        {
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is bool) return (bool)v ? "true" : "false";
            return Json(Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static string Json(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 32) sb.AppendFormat("\\u{0:X4}", (int)c);
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
