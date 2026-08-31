#region Component Description
/*
    Component Name:
        wsp_Sl01_Slicer

    Nickname:
        Slicer

    Version:
        v1.0.5

    Category / Subcategory:
        WASPer_3DP / 3.0_Slicing

    Description:
        Slices Box, Mesh, Brep, Surface, or Extrusion geometry with layer
        planes. If no ref_curve is provided, planes are generated as horizontal
        World Z layers. If ref_curve is provided, candidate planes are generated
        along the curve at layer_h distance intervals using the selected
        slicing_mode, then culled to the planes that actually intersect the
        input geometry.
        Optional path offsetting is applied in each slicing plane after
        intersection. Positive values offset closed paths outward; negative
        values offset closed paths inward.

    Inputs:
        geometry : Box, Mesh, Brep, Surface, or Extrusion to be sliced
        ref_curve    : optional curve used to place slicing planes
        slicing_mode : 1 = planes perpendicular to ref_curve tangent,
                       2 = World XY planes located at ref_curve sample points
        layer_h      : spacing between slicing planes or along ref_curve
        offset       : optional path offset in model units; positive outward,
                       negative inward
        la_planes    : optional existing layer-plane tree; when supplied, these
                       planes replace generated World-Z/ref-curve planes and
                       preserve their branch paths for layer compatibility

    Outputs:
        printing_path : data tree of intersection curves, one branch per layer
        b_box         : world-aligned bounding box of the input geometry
        layer_planes  : tree of valid slicing planes, one branch per layer
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
#endregion

namespace WASPer_3DP.Components._3_0_Slicing
{
    public sealed class wsp_Sl01_Slicer : GH_Component
    {
        private const string NAME   = "wsp_Sl01_Slicer";
        private const string NICK   = "Slicer";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "3.0_Slicing";

        private readonly string _versionTag;

        public wsp_Sl01_Slicer()
            : base(
                NAME, NICK,
                "Slices Box, Mesh, Brep, Surface, or Extrusion geometry with World Z layers, reference-curve planes, or an optional existing la_planes tree for layer-compatible re-slicing.",
                CAT, SUBCAT)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("D645D4A7-F1C7-4D59-B932-16FAEFADE739");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Sl01_Slicer.png"))
                        return s != null ? new Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "geometry", "geo",
                "The input geometry to be sliced. Accepts Box, Mesh, Brep, Surface, or Extrusion. Curves are ignored.",
                GH_ParamAccess.item);

            pManager.AddCurveParameter(
                "ref_curve", "ref",
                "Optional reference curve used to place slicing planes.\n" +
                "If empty, the component slices the geometry with horizontal World XY planes along World Z.\n" +
                "If supplied, layer_h is measured as distance along this curve.",
                GH_ParamAccess.item);

            pManager.AddIntegerParameter(
                "slicing_mode", "mode",
                "Reference curve slicing mode:\n" +
                "1 = planes perpendicular to ref_curve tangent, with each plane Z axis following the curve tangent.\n" +
                "2 = World XY planes located at ref_curve sample points.\n" +
                "Ignored when ref_curve is empty.",
                GH_ParamAccess.item,
                1);

            pManager.AddNumberParameter(
                "layer_h", "la_h",
                "Layer spacing in model units. With ref_curve, this is distance along the curve. Without ref_curve, this is World Z spacing.",
                GH_ParamAccess.item,
                2.0);

            pManager.AddNumberParameter(
                "offset", "offset",
                "Optional offset applied to the sliced printing paths in each slicing plane.\n" +
                "Positive values offset closed paths outward; negative values offset closed paths inward.\n" +
                "For open curves, the sign follows Rhino's curve offset side relative to curve direction.",
                GH_ParamAccess.item,
                0.0);

            pManager.AddPlaneParameter(
                "layer_planes", "la_planes",
                "Optional existing layer-plane tree from a previous slicer. When valid planes are supplied, they replace World-Z/ref_curve plane generation and their original branch paths are preserved in p_path and la_planes outputs. This allows another object to be sliced on exactly compatible layers.",
                GH_ParamAccess.tree);

            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "printing_path", "p_path",
                "Data tree of intersection curves, one branch per slicing plane. Box, Brep, Surface, and Extrusion inputs preserve smooth intersection curves.",
                GH_ParamAccess.tree);

            pManager.AddBoxParameter(
                "b_box", "b_box",
                "World-aligned bounding box of the input geometry. In reference-curve modes this is still the geometry bounding box, not a curve-frame box.",
                GH_ParamAccess.item);

            pManager.AddPlaneParameter(
                "layer_planes", "la_planes",
                "Slicing planes per valid layer. In ref_curve mode these are candidate planes that intersect the input geometry, matching SlicerPlus behavior.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object inputGeometry = null;
            Curve refCurve = null;
            int slicingMode = 1;
            double layerH = 2.0;
            double offset = 0.0;
            GH_Structure<GH_Plane> suppliedPlaneTree = null;

            if (!DA.GetData(0, ref inputGeometry)) return;
            DA.GetData(1, ref refCurve);
            DA.GetData(2, ref slicingMode);
            DA.GetData(3, ref layerH);
            DA.GetData(4, ref offset);
            DA.GetDataTree(5, out suppliedPlaneTree);

            string geometryType;
            GeometryBase geometry = ToSupportedGeometry(inputGeometry, out geometryType);
            if (geometry == null || !geometry.IsValid)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Input geometry is null, invalid, or unsupported. Use Box, Mesh, Brep, Surface, or Extrusion.");
                return;
            }

            if (layerH <= 0)
                layerH = 2.0;
            slicingMode = slicingMode == 2 ? 2 : 1;

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            double minLen = Math.Max(2.0 * tol, RhinoMath.ZeroTolerance);

            BoundingBox worldBox = geometry.GetBoundingBox(true);
            Box outputBox = new Box(Plane.WorldXY, worldBox);

            List<Plane> candidatePlanes = new List<Plane>();
            List<GH_Path> candidatePaths = new List<GH_Path>();
            int ignoredSuppliedPlanes = ReadSuppliedPlanes(
                suppliedPlaneTree,
                candidatePlanes,
                candidatePaths);
            bool useSuppliedPlanes = candidatePlanes.Count > 0;
            if (!useSuppliedPlanes)
            {
                candidatePlanes = BuildSlicePlanes(refCurve, slicingMode, layerH, geometry, tol);
                candidatePaths.Clear();
            }
            else if (Params.Input[1].SourceCount > 0 ||
                     Params.Input[2].SourceCount > 0 ||
                     Params.Input[3].SourceCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "la_planes supplied: ref_curve, mode, and layer_h plane generation are ignored.");
            }
            if (ignoredSuppliedPlanes > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"Ignored {ignoredSuppliedPlanes} additional/invalid la_planes item(s); Sl01 uses the first valid plane in each source branch.");
            }
            if (candidatePlanes.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    suppliedPlaneTree != null && !suppliedPlaneTree.IsEmpty
                        ? "No valid supplied layer planes were found and fallback plane generation produced no candidates."
                        : refCurve != null && refCurve.IsValid
                        ? "No reference-curve slicing planes were generated. Check ref_curve length and layer_h."
                        : "Geometry has zero World Z extent or layer_h is too large. No slices created.");
                DA.SetData(1, outputBox);
                return;
            }

            List<Plane> slicingPlanes = new List<Plane>();
            List<GH_Path> slicingPaths = new List<GH_Path>();
            int culledPlaneCount = 0;
            for (int i = 0; i < candidatePlanes.Count; i++)
            {
                if (SliceGeometry(geometry, candidatePlanes[i], tol, minLen).Count == 0)
                {
                    culledPlaneCount++;
                    continue;
                }
                slicingPlanes.Add(candidatePlanes[i]);
                slicingPaths.Add(useSuppliedPlanes
                    ? new GH_Path(candidatePaths[i].Indices)
                    : new GH_Path(slicingPlanes.Count - 1));
            }
            if (slicingPlanes.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Reference planes were generated, but none intersected the input geometry. Check that ref_curve passes through or near the object.");
                DA.SetData(1, outputBox);
                DA.SetDataTree(2, new DataTree<Plane>());
                Message = $"{_versionTag} | {geometryType} | 0 valid / {candidatePlanes.Count} candidate";
                return;
            }

            var curvesByLayer = new List<Curve>[slicingPlanes.Count];
            var layerErrors = new string[slicingPlanes.Count];
            var offsetFailureCounts = new int[slicingPlanes.Count];

            Parallel.For(0, slicingPlanes.Count, i =>
            {
                try
                {
                    var sliced = SliceGeometry(geometry, slicingPlanes[i], tol, minLen);
                    curvesByLayer[i] = ApplyPathOffset(sliced, slicingPlanes[i], offset, tol, minLen, out offsetFailureCounts[i]);
                }
                catch (Exception ex)
                {
                    layerErrors[i] = ex.Message;
                }
            });

            var curveTree = new DataTree<Curve>();
            var planeTree = new DataTree<Plane>();
            int offsetFailures = 0;
            for (int i = 0; i < slicingPlanes.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(layerErrors[i]))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Slice {i} failed: {layerErrors[i]}");
                offsetFailures += offsetFailureCounts[i];

                var path = slicingPaths[i];
                planeTree.Add(slicingPlanes[i], path);

                var layerCurves = curvesByLayer[i];
                if (layerCurves == null || layerCurves.Count == 0) continue;

                foreach (Curve curve in layerCurves)
                    curveTree.Add(curve, path);
            }

            if (curveTree.DataCount == 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "No intersection curves produced. Check geometry validity and layer height.");
            if (offsetFailures > 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{offsetFailures} curve offset(s) failed and were passed through un-offset.");

            DA.SetDataTree(0, curveTree);
            DA.SetData(1, outputBox);
            DA.SetDataTree(2, planeTree);

            string modeTag = useSuppliedPlanes
                ? "input planes"
                : refCurve != null && refCurve.IsValid
                ? (slicingMode == 2 ? "ref XY" : "ref perp")
                : "world Z";
            string offsetTag = Math.Abs(offset) > tol ? $" | off {offset:0.###}" : string.Empty;
            Message = $"{_versionTag} | {geometryType} | {modeTag}{offsetTag} | {slicingPlanes.Count}/{candidatePlanes.Count} layers";
        }

        private static int ReadSuppliedPlanes(
            GH_Structure<GH_Plane> tree,
            List<Plane> planes,
            List<GH_Path> paths)
        {
            if (tree == null || tree.IsEmpty)
                return 0;

            int ignored = 0;
            for (int b = 0; b < tree.PathCount; b++)
            {
                Plane selected = Plane.Unset;
                IList<GH_Plane> branch = tree.Branches[b];
                if (branch != null)
                {
                    foreach (GH_Plane goo in branch)
                    {
                        if (selected.IsValid || goo == null || !goo.Value.IsValid)
                        {
                            ignored++;
                            continue;
                        }
                        selected = goo.Value;
                    }
                }

                if (!selected.IsValid)
                    continue;
                planes.Add(selected);
                paths.Add(new GH_Path(tree.Paths[b].Indices));
            }
            return ignored;
        }

        private static List<Plane> BuildSlicePlanes(Curve refCurve, int slicingMode, double layerH, GeometryBase geometry, double tol)
        {
            var result = new List<Plane>();
            if (layerH <= tol) return result;

            if (refCurve != null && refCurve.IsValid)
            {
                double length = refCurve.GetLength();
                if (length <= tol) return result;

                int count = (int)Math.Floor(length / layerH);
                for (int i = 1; i <= count; i++)
                {
                    double dist = i * layerH;
                    if (dist > length + tol) break;

                    double t;
                    if (!refCurve.LengthParameter(dist, out t)) continue;

                    Point3d origin = refCurve.PointAt(t);
                    if (slicingMode == 2)
                    {
                        Plane pl = Plane.WorldXY;
                        pl.Origin = origin;
                        result.Add(pl);
                    }
                    else
                    {
                        Vector3d tangent = refCurve.TangentAt(t);
                        if (!tangent.IsValid || tangent.Length <= tol) continue;
                        tangent.Unitize();
                        result.Add(OrthoPlane(origin, tangent));
                    }
                }

                return result;
            }

            BoundingBox bbox = geometry.GetBoundingBox(Plane.WorldXY);
            double boxHeight = bbox.Max.Z - bbox.Min.Z;
            if (boxHeight <= tol) return result;

            int numPlanes = (int)Math.Floor(boxHeight / layerH);
            for (int i = 1; i <= numPlanes; i++)
            {
                double z = bbox.Min.Z + layerH * i;
                Plane pl = Plane.WorldXY;
                pl.Origin = new Point3d(0.0, 0.0, z);
                result.Add(pl);
            }

            return result;
        }

        private static List<Plane> CullPlanesToGeometry(
            IEnumerable<Plane> candidates,
            GeometryBase geometry,
            double tol,
            double minLen,
            out int culledCount)
        {
            var result = new List<Plane>();
            culledCount = 0;

            if (candidates == null) return result;

            foreach (Plane plane in candidates)
            {
                var hits = SliceGeometry(geometry, plane, tol, minLen);
                if (hits.Count > 0)
                    result.Add(plane);
                else
                    culledCount++;
            }

            return result;
        }

        private static GeometryBase ToSupportedGeometry(object input, out string geometryType)
        {
            geometryType = "?";
            if (input == null) return null;

            if (input is GH_ObjectWrapper wrapper)
                return ToSupportedGeometry(wrapper.Value, out geometryType);

            if (input is IGH_Goo goo)
            {
                object scriptVariable = goo.ScriptVariable();
                if (!ReferenceEquals(scriptVariable, input))
                    return ToSupportedGeometry(scriptVariable, out geometryType);
            }

            if (input is Box box)
            {
                geometryType = "Box";
                if (!box.IsValid) return null;

                Brep boxBrep = Brep.CreateFromBox(box);
                return boxBrep != null && boxBrep.IsValid ? boxBrep : null;
            }

            GeometryBase geometry = input as GeometryBase;
            if (geometry == null || !geometry.IsValid) return null;

            if (geometry is Curve) return null;

            if (geometry is Mesh mesh)
            {
                geometryType = "Mesh";
                return mesh.DuplicateMesh();
            }

            if (geometry is Brep brep)
            {
                geometryType = "Brep";
                return brep.DuplicateBrep();
            }

            if (geometry is Surface)
            {
                geometryType = "Surface";
                return geometry;
            }

            if (geometry is Extrusion)
            {
                geometryType = "Extrusion";
                return geometry;
            }

            return null;
        }

        private static List<Curve> SliceGeometry(GeometryBase geometry, Plane plane, double tol, double minLen)
        {
            var result = new List<Curve>();
            if (geometry == null || !geometry.IsValid) return result;

            if (geometry is Mesh mesh)
            {
                Polyline[] polylines = Intersection.MeshPlane(mesh, plane);
                if (polylines == null) return result;

                foreach (Polyline polyline in polylines)
                {
                    if (!polyline.IsValid || polyline.Count < 2) continue;

                    var curve = new PolylineCurve(polyline);
                    if (curve.IsValid && curve.GetLength() >= minLen)
                        result.Add(curve);
                }

                return result;
            }

            Brep brep = null;
            if (geometry is Brep b) brep = b;
            if (geometry is Surface s) brep = Brep.CreateFromSurface(s);
            if (geometry is Extrusion e) brep = e.ToBrep();

            if (brep == null || !brep.IsValid) return result;

            Curve[] curves;
            Point3d[] points;
            if (!Intersection.BrepPlane(brep, plane, tol, out curves, out points) || curves == null)
                return result;

            foreach (Curve curve in curves)
            {
                if (curve == null || !curve.IsValid) continue;
                if (curve.GetLength() < minLen) continue;
                result.Add(curve.DuplicateCurve());
            }

            return result;
        }

        private static List<Curve> ApplyPathOffset(
            List<Curve> curves,
            Plane plane,
            double offset,
            double tol,
            double minLen,
            out int failureCount)
        {
            failureCount = 0;

            var result = new List<Curve>();
            if (curves == null || curves.Count == 0) return result;

            if (Math.Abs(offset) <= tol)
            {
                foreach (Curve curve in curves)
                {
                    if (curve == null || !curve.IsValid) continue;
                    result.Add(curve.DuplicateCurve());
                }
                return result;
            }

            foreach (Curve curve in curves)
            {
                if (curve == null || !curve.IsValid) continue;

                double signedOffset = ResolveOffsetDistance(curve, plane, offset);
                Curve[] offsetCurves = null;
                try
                {
                    offsetCurves = curve.Offset(plane, signedOffset, tol, CurveOffsetCornerStyle.Sharp);
                }
                catch
                {
                    offsetCurves = null;
                }

                if (offsetCurves == null || offsetCurves.Length == 0)
                {
                    failureCount++;
                    result.Add(curve.DuplicateCurve());
                    continue;
                }

                int added = 0;
                foreach (Curve offsetCurve in offsetCurves)
                {
                    if (offsetCurve == null || !offsetCurve.IsValid) continue;
                    if (offsetCurve.GetLength() < minLen)
                    {
                        offsetCurve.Dispose();
                        continue;
                    }

                    result.Add(offsetCurve);
                    added++;
                }

                if (added == 0)
                {
                    failureCount++;
                    result.Add(curve.DuplicateCurve());
                }
            }

            return result;
        }

        private static double ResolveOffsetDistance(Curve curve, Plane plane, double requestedOffset)
        {
            if (curve == null || !curve.IsClosed)
                return requestedOffset;

            CurveOrientation orientation = curve.ClosedCurveOrientation(plane);
            if (orientation == CurveOrientation.CounterClockwise)
                return -requestedOffset;

            return requestedOffset;
        }

        private static Plane OrthoPlane(Point3d origin, Vector3d z)
        {
            if (z.IsZero || !z.Unitize())
                z = Vector3d.ZAxis;

            Vector3d x = Vector3d.CrossProduct(z, Vector3d.XAxis);
            if (x.IsTiny())
                x = Vector3d.CrossProduct(z, Vector3d.YAxis);
            x.Unitize();

            Vector3d y = Vector3d.CrossProduct(z, x);
            y.Unitize();

            return new Plane(origin, x, y);
        }
    }
}
