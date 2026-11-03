// Node type names keyed by the first field of the type GUID.
// Source: InventorLoader (github.com/jmplonka/InventorLoader, importerDC.py), checked against our own files where noted in the notes.
using System.Collections.Generic;

namespace InvRe
{
    public static class NodeNames
    {
        public static readonly Dictionary<uint, string> Known = new Dictionary<uint, string>
        {
            // Documents and structure
            { 0x90874D16, "Document" }, { 0x90874D46, "AsmDocument" }, { 0x90874D11, "Sketch2D" },
            { 0x3AE9D8DA, "Sketch3D" }, { 0x90874D23, "Sketch2DPlacement" }, { 0x3D64CCF0, "Sketch2DPlacementPlane" },
            { 0x90874D62, "Group2D" }, { 0xDB04EB11, "Group3D" }, { 0x90874D74, "ObjectCollectionDef" },
            { 0x90874D91, "Feature" }, { 0x8F41FD24, "EndOfFeatures" }, { 0x90874D18, "Transformation" },
            { 0x90874D47, "SurfaceBody" }, { 0xD5DAAA83, "BodyCollection" }, { 0xD83EF271, "FeatureDimensions" },
            { 0x2148C03C, "Reference" }, { 0x7256922C, "Placement" }, { 0x2B241309, "BrowserFolder" },
            { 0x3C7F67AA, "BodiesFolder" }, { 0xB382A87C, "ProfileSelection" }, { 0x0E6870AE, "ObjectCollection" },
            { 0x90874D28, "Boolean" }, { 0x90874D40, "ParameterMappings" }, { 0x90874D48, "BodySet" },
            { 0x90874D51, "EdgeCollection" }, { 0x24BCB2F1, "FaceCollection" }, { 0x375C6982, "EdgeItem" },
            { 0x509FB5CC, "FaceItem" }, { 0x4ACA204D, "UserCoordinateSystem" }, { 0x4CF1124C, "Block2D" },
            { 0x90874D53, "EdgeId" }, { 0x03AA812C, "FaceId" }, { 0x748FBD64, "FaceSurfaceId" },
            { 0xFFD270B8, "SketchFragment" }, { 0xC4C14B90, "Face" }, { 0xF393AC92, "Scene" },
            { 0xA244457B, "DirectionEdge" }, { 0xA5977BAA, "DirectionFace" }, { 0xCE52DF40, "DirectionAxis" },
            { 0xD2D440C0, "DirectionPath" }, { 0xCE52DF42, "Plane" }, { 0xDFB2586A, "IntersectionCurve" },

            // Parameters and expressions
            { 0x90874D26, "Parameter" }, { 0xBB1DD5DF, "ParameterRDxVar" }, { 0x8367B125, "ParameterText" },
            { 0x0AA8AF46, "ParameterConstant" }, { 0xF8A77A03, "ParameterFunction" }, { 0xF8A77A04, "ParameterValue" },
            { 0xF8A77A05, "ParameterRef" }, { 0xF8A77A06, "Op+" }, { 0xF8A77A07, "Op-" }, { 0xF8A77A08, "Op*" },
            { 0xF8A77A09, "Op/" }, { 0xF8A77A0A, "Op%" }, { 0xF8A77A0B, "Op^" }, { 0xF8A77A0C, "OpNeg" },
            { 0xF8A77A0D, "OpPowIdent" },

            // 2D sketch geometry
            { 0xCE52DF35, "Point2D" }, { 0xCE52DF3A, "Line2D" }, { 0xCE52DF3B, "Circle2D" }, { 0x160915E2, "Arc2D" },
            { 0x317B7346, "Spline2D" }, { 0x3E55D947, "OffsetSpline2D" }, { 0x4507D460, "Ellipse2D" },
            { 0xA644E76A, "SplineHandle2D" }, { 0xF9372FD4, "BSplineCurve2D" }, { 0x18A9717E, "BlockPoint2D" },
            { 0x2510347F, "Text2D" }, { 0x8740B863, "EquationCurve2D" },

            // 3D sketch geometry
            { 0xCE52DF3E, "Point3D" }, { 0x8EF06C89, "Line3D" }, { 0x9E43716A, "Circle3D" }, { 0x9E43716B, "Ellipse3D" },
            { 0x0B86AD43, "Spline3D_Fixed" }, { 0x7C44ABDE, "Bezier3D" }, { 0x48C52258, "BSpline3D" },

            // 2D constraints and dimensions
            { 0x90874D94, "Coincident2D" }, { 0x90874D95, "Parallel2D" }, { 0x90874D96, "Perpendicular2D" },
            { 0x90874D97, "Tangent2D" }, { 0x90874D98, "Horizontal2D" }, { 0x90874D99, "Vertical2D" },
            { 0xC681C2E0, "EqualLength2D" }, { 0x442C7DD0, "EqualRadius2D" }, { 0x0F177BB0, "Fix2D" },
            { 0x5A9A7BE0, "Collinear2D" }, { 0x0DDD7C10, "SymmetryLine2D" }, { 0x21E870BF, "SymmetryPoint2D" },
            { 0x64DA5250, "AlignVertical2D" }, { 0x8F55A3C0, "AlignHorizontal2D" }, { 0x4E4B14BC, "Offset2D" },
            { 0xE1108C00, "Concentric2D" },   // InventorLoader: Geometric_Radius2D; ties a circle to its center point or to another circle
{ 0xFBDB891F, "PolygonEdge2D" }, { 0x52534838, "PolygonCenter2D" },
            { 0x75A6689B, "PolygonPattern2D" }, { 0x7C6D149E, "SplineFitPoint2D" }, { 0x7A98AD0E, "TextBox2D" },
            { 0x11058558, "DimDistance2D" }, { 0x00ACC000, "DimDistanceH2D" }, { 0x3683FF40, "DimDistanceV2D" },
            { 0x590D0A10, "DimAngle2Line2D" }, { 0xBF3B5C84, "DimAngle3Point2D" }, { 0x671BB700, "DimRadius2D" },
            { 0x74DF96E0, "DimDiameter2D" }, { 0xA789EEB0, "DimRadiusA2D" }, { 0xB4964E90, "DimRadiusB2D" },
            { 0x3F4FA55F, "DimOffsetSpline2D" }, { 0x4E86F047, "Constraint" },

            // 3D constraints and dimensions
            { 0xC5538931, "Coincident3D" }, { 0x33EC1003, "Parallel3D" }, { 0x63266191, "Perpendicular3D" },
            { 0x7457BB19, "Tangent3D" }, { 0xD13107FE, "Collinear3D" }, { 0x2574C505, "Radius3D" },
            { 0x10B6ADEF, "DimLength3D" }, { 0x6A3EEA31, "DimAngle2Planes3D" },

            // Feature enums
            { 0x729ABE28, "Enum PartFeatureOperation" }, { 0x92637D29, "Enum ExtentType" },
            { 0xC7A06AC2, "Enum PartFeatureExtentDirection" }, { 0x78F28827, "Enum FilletType" },
            { 0x7DAA0032, "Enum ChamferType" }, { 0x43CD7C11, "Enum HoleType" }, { 0xCA70D2C6, "Enum ThreadType" },

            // Features
            { 0xFAD9A9B5, "Mirror" }, { 0x06977131, "PatternPolar" }, { 0x20673244, "PatternRectangular" },
            { 0xA37B053C, "PatternSketchDriven" }, { 0x255D7ED7, "DerivedPart" }, { 0x27ECB60F, "DerivedAssembly" },
            { 0xC3DDDC08, "Flange" }, { 0xF3DBA9D8, "Unfold" }, { 0x3902E4D1, "Refold" },
            { 0x1F6D59F6, "DirectEdit" }, { 0x8C1A567D, "ThreadDefinition" }, { 0x936522B1, "HoleCenterPoints" },
            { 0x1B48E9DA, "FilletEdgeSetsConstantR" }, { 0xAAD64116, "FilletEdgeSetConstantR" },
        };
    }
}
