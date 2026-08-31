#define USE_PARALLEL

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._2_2_Fields
{
    public class wsp_Fi08_Infill_Layers_to_Fields : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Fi08_Infill_Layers_to_Fields()
            : base(
                "wsp_Fi08_Infill Layers to Fields",
                "Infill2Fields",
                "Converts layer-organized infill/toolpath curves into Fi06-compatible 2D field slices.\n\n" +
                "Designed for wsp_In17_Multi-Infill: connect full_path or infill curves and optional la_planes.\n" +
                "Each layer becomes one 2D field grid where g = distance_to_layer_curves - path_width/2.\n" +
                "All slices use one array-wide bounding frame with a shared centre, size, and consistently aligned X/Y axes.\n" +
                "Negative values represent deposited/material regions; positive values represent void.\n" +
                "For closed TPMS regions, set closed_regions=true and path_width=0 to use the signed contour region directly.\n" +
                "The field_obj output can be connected directly to wsp_Fi06_3D Field from 2D Fields for volumetric meshing.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.2_Fields")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("763732CB-4632-4AD6-B957-71D3EE8F832A");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Fi08_Infill Layers to Fields.png"))
                        return stream != null ? new Bitmap(stream) : null;
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddCurveParameter(
                "layer_curves",
                "crvs",
                "Layer-organized curves, typically In17 full_path or infill.\n" +
                "If paths are {layer}, each branch becomes one field slice.\n" +
                "If paths are {layer;...}, all branches sharing the first path index are grouped into the same slice.",
                GH_ParamAccess.tree);

            p.AddPlaneParameter(
                "layer_planes",
                "la_planes",
                "Optional layer planes, typically In17 la_planes. One plane per layer is used when available; otherwise the plane is inferred from each layer's curves.",
                GH_ParamAccess.tree);
            p[1].Optional = true;

            p.AddNumberParameter(
                "path_width",
                "p_width",
                "Deposited path width in model units. Field values are g = distance_to_curves - path_width/2. Default 2.0.\n" +
                "Must be > 0 for path-band mode. Can be 0 only when closed_regions=true, where closed curves define signed filled regions directly.",
                GH_ParamAccess.item,
                2.0);

            p.AddNumberParameter(
                "resolution",
                "res",
                "2D grid cell size in model units. Smaller values capture more detail but are heavier. Default 2.0.",
                GH_ParamAccess.item,
                2.0);

            p.AddNumberParameter(
                "frame_size",
                "f_size",
                "Square sampling window side length in model units. If <= 0, it is inferred from the bounding box of the entire layer array plus path/resolution margins.\n" +
                "Every slice uses the same array-wide centre, size, and aligned X/Y directions so Fi06 grid indices remain spatially registered.",
                GH_ParamAccess.item,
                0.0);
            p[4].Optional = true;

            p.AddBooleanParameter(
                "invert_field",
                "invert",
                "Invert the resulting field values. false: printed paths are negative/material. true: printed paths are positive and void/background is negative.",
                GH_ParamAccess.item,
                false);
            p[5].Optional = true;

            p.AddBooleanParameter(
                "closed_regions",
                "regions",
                "If false, closed curves are treated as deposited path bands like open curves. If true, closed curves also define filled signed regions and path_width=0 is allowed. Default false for In17 toolpath workflows.",
                GH_ParamAccess.item,
                false);
            p[6].Optional = true;

            p.AddBooleanParameter(
                "mesh",
                "mesh?",
                "Build colored preview field meshes. Set false for faster field-only generation before Fi06.",
                GH_ParamAccess.item,
                true);
            p[7].Optional = true;

        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter(
                "field_mesh",
                "mesh",
                "Preview field meshes, one branch per layer. Vertex colors show negative/material as blue and positive/void as red.",
                GH_ParamAccess.tree);

            p.AddGenericParameter(
                "field_obj",
                "field",
                "Fi06-compatible 2D field slices, one branch per layer. Connect directly to wsp_Fi06_3D Field from 2D Fields.",
                GH_ParamAccess.tree);

            p.AddTextParameter(
                "summary",
                "summary",
                "Conversion diagnostics: layer count, global frame centre/size, grid size, path width, resolution, empty-material slices, frame spacing, and skipped layers.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            GH_Structure<GH_Curve> curveTree = null;
            GH_Structure<GH_Plane> planeTree = null;
            double pathWidth = 2.0;
            double resolution = 2.0;
            double frameSize = 0.0;
            bool invert = false;
            bool closedRegions = false;
            bool buildMesh = true;

            if (!da.GetDataTree(0, out curveTree) || curveTree == null || curveTree.PathCount == 0)
            {
                da.SetDataTree(0, new DataTree<Mesh>());
                da.SetDataTree(1, new DataTree<IGH_Goo>());
                da.SetData(2, "Provide layer_curves as a tree.");
                return;
            }

            da.GetDataTree(1, out planeTree);
            da.GetData(2, ref pathWidth);
            da.GetData(3, ref resolution);
            da.GetData(4, ref frameSize);
            da.GetData(5, ref invert);
            da.GetData(6, ref closedRegions);
            da.GetData(7, ref buildMesh);

            if (pathWidth < -RhinoMath.ZeroTolerance)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "path_width must be >= 0.");
                return;
            }
            if (pathWidth <= RhinoMath.ZeroTolerance)
            {
                pathWidth = 0.0;
                if (!closedRegions)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "path_width = 0 is only valid when closed_regions is true.");
                    return;
                }
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "path_width = 0: closed curves define signed regions directly; open curves are zero-width traces only.");
            }
            if (resolution <= RhinoMath.ZeroTolerance)
                resolution = 2.0;

            double tol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;
            var layers = GroupCurvesByLayer(curveTree);
            if (layers.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No valid curves found.");
                da.SetDataTree(0, new DataTree<Mesh>());
                da.SetDataTree(1, new DataTree<IGH_Goo>());
                da.SetData(2, "No valid curves found.");
                return;
            }

            if (closedRegions && pathWidth <= RhinoMath.ZeroTolerance && HasOpenCurves(layers))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Open curves detected in zero-width closed_regions mode. They will not create material volume unless path_width is > 0.");
            }
            if (closedRegions && pathWidth <= RhinoMath.ZeroTolerance && !HasClosedCurves(layers))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "path_width = 0 with closed_regions=true requires at least one closed curve.");
                return;
            }

            var planeMap = BuildPlaneMap(planeTree);
            var warnings = new List<string>();
            var layerSources = new List<LayerSource>(layers.Count);

            foreach (var kv in layers.OrderBy(k => k.Key))
            {
                int layerKey = kv.Key;
                List<Curve> sourceCurves = kv.Value;
                if (sourceCurves.Count == 0)
                    continue;

                Plane plane;
                if (!planeMap.TryGetValue(layerKey, out plane) || !plane.IsValid)
                {
                    if (!TryInferPlane(sourceCurves, out plane))
                    {
                        warnings.Add($"Layer {layerKey}: could not infer layer plane. Skipped.");
                        continue;
                    }
                }

                layerSources.Add(new LayerSource(layerKey, plane, sourceCurves));
            }

            if (layerSources.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No layers could be prepared.");
                da.SetDataTree(0, new DataTree<Mesh>());
                da.SetDataTree(1, new DataTree<IGH_Goo>());
                da.SetData(2, string.Join(Environment.NewLine, warnings));
                return;
            }

            // Build one array-wide frame system. The former implementation
            // recentered every layer independently, so the same (i,j) grid index
            // could drift in world space between consecutive Fi06 slices.
            Plane referencePlane = layerSources[0].Plane;
            BoundingBox arrayBox = BuildArrayBoundingBox(layerSources, referencePlane);
            if (!arrayBox.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not calculate the array-wide layer bounding box.");
                da.SetDataTree(0, new DataTree<Mesh>());
                da.SetDataTree(1, new DataTree<IGH_Goo>());
                da.SetData(2, "Could not calculate the array-wide layer bounding box.");
                return;
            }

            double globalCenterX = 0.5 * (arrayBox.Min.X + arrayBox.Max.X);
            double globalCenterY = 0.5 * (arrayBox.Min.Y + arrayBox.Max.Y);
            Point3d sharedCenterWorld = referencePlane.PointAt(globalCenterX, globalCenterY);
            var prepared = new List<LayerPrep>(layerSources.Count);
            double globalExtent = 0.0;

            foreach (LayerSource source in layerSources)
            {
                Plane alignedPlane = AlignPlaneToReference(source.Plane, referencePlane);
                alignedPlane.Origin = alignedPlane.ClosestPoint(sharedCenterWorld);

                var crv2D = ProjectCurvesToXY(source.Curves, alignedPlane);
                if (crv2D.Count == 0)
                {
                    warnings.Add($"Layer {source.LayerKey}: no valid curves after projection. Skipped.");
                    continue;
                }

                BoundingBox bb = BoundingBox.Empty;
                foreach (Curve c in crv2D)
                    bb.Union(c.GetBoundingBox(true));
                if (!bb.IsValid)
                {
                    warnings.Add($"Layer {source.LayerKey}: invalid projected bounding box. Skipped.");
                    continue;
                }

                double layerExtent = 2.0 * Math.Max(
                    Math.Max(Math.Abs(bb.Min.X), Math.Abs(bb.Max.X)),
                    Math.Max(Math.Abs(bb.Min.Y), Math.Abs(bb.Max.Y)));
                globalExtent = Math.Max(globalExtent, layerExtent);

                // The plane origin is now the shared array centre projected onto
                // this layer, so every slice uses an identical local grid centre.
                prepared.Add(new LayerPrep(
                    source.LayerKey,
                    alignedPlane,
                    crv2D,
                    new Point2d(0.0, 0.0)));
            }

            if (prepared.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No layers could be prepared in the shared frame system.");
                da.SetDataTree(0, new DataTree<Mesh>());
                da.SetDataTree(1, new DataTree<IGH_Goo>());
                da.SetData(2, string.Join(Environment.NewLine, warnings));
                return;
            }

            DiagnoseLayerFrames(prepared, referencePlane, resolution, tol, warnings);

            if (frameSize <= RhinoMath.ZeroTolerance)
            {
                double margin = Math.Max(2.0 * resolution, pathWidth * 0.5 + 2.0 * resolution);
                frameSize = Math.Max(resolution * 2.0, globalExtent + 2.0 * margin);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"frame_size auto: {frameSize:0.###}");
            }

            int cells = Math.Max(2, (int)Math.Ceiling(frameSize / resolution));
            double cellSize = frameSize / cells;
            int nxv = cells + 1;
            int nyv = cells + 1;
            int nPts = nxv * nyv;
            double isoOffset = pathWidth * 0.5;

            var meshTree = new DataTree<Mesh>();
            var fieldTree = new DataTree<IGH_Goo>();
            int skipped = 0;
            int built = 0;
            bool useParallel = Environment.ProcessorCount > 1 && nPts * prepared.Count >= 40000;

            foreach (LayerPrep prep in prepared)
            {
                Mesh mesh;
                InfillLayerFieldGrid2D field;
                if (!BuildLayerField(prep, frameSize, cellSize, nxv, nyv, isoOffset, tol, invert, closedRegions, buildMesh, out mesh, out field))
                {
                    skipped++;
                    warnings.Add($"Layer {prep.LayerKey}: field build failed.");
                    continue;
                }

                int negativeSamples = 0;
                int positiveSamples = 0;
                for (int i = 0; i < field.G.Length; i++)
                {
                    if (field.G[i] < 0.0) negativeSamples++;
                    else if (field.G[i] > 0.0) positiveSamples++;
                }
                if (negativeSamples == 0)
                    warnings.Add($"Layer {prep.LayerKey}: field contains no negative/material samples. Fi06 may create a horizontal break at this slice.");
                else if (positiveSamples == 0)
                    warnings.Add($"Layer {prep.LayerKey}: field contains no positive/background samples. The entire frame is classified as material.");

                GH_Path path = new GH_Path(prep.LayerKey);
                if (buildMesh && mesh != null)
                    meshTree.Add(mesh, path);
                fieldTree.Add(new GH_ObjectWrapper(field), path);
                built++;
            }

            string summary =
                $"wsp_Fi08_Infill Layers to Fields\n" +
                $"layers_in       : {layers.Count}\n" +
                $"layers_built    : {built}\n" +
                $"layers_skipped  : {skipped}\n" +
                $"grid            : {nxv} x {nyv} vertices\n" +
                "frame_mode      : array-wide aligned frames\n" +
                $"frame_center    : {sharedCenterWorld.X.ToString("0.###", CultureInfo.InvariantCulture)}, " +
                                     $"{sharedCenterWorld.Y.ToString("0.###", CultureInfo.InvariantCulture)}, " +
                                     $"{sharedCenterWorld.Z.ToString("0.###", CultureInfo.InvariantCulture)}\n" +
                $"frame_size      : {frameSize.ToString("0.###", CultureInfo.InvariantCulture)}\n" +
                $"cell_size       : {cellSize.ToString("0.###", CultureInfo.InvariantCulture)}\n" +
                $"path_width      : {pathWidth.ToString("0.###", CultureInfo.InvariantCulture)}\n" +
                $"iso_offset      : {isoOffset.ToString("0.###", CultureInfo.InvariantCulture)}\n" +
                $"invert_field    : {invert}\n" +
                $"closed_regions  : {closedRegions}\n" +
                $"mesh            : {buildMesh}\n" +
                $"parallel_ready  : {useParallel}\n" +
                (warnings.Count > 0 ? string.Join(Environment.NewLine, warnings) : "warnings        : none");

            if (warnings.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{warnings.Count} layer warning(s). See summary.");

            Message = $"{_versionTag} | {built} layers";
            da.SetDataTree(0, meshTree);
            da.SetDataTree(1, fieldTree);
            da.SetData(2, summary);
        }

        private static bool BuildLayerField(
            LayerPrep prep,
            double frameSize,
            double cellSize,
            int nxv,
            int nyv,
            double isoOffset,
            double tol,
            bool invert,
            bool closedRegions,
            bool buildPreviewMesh,
            out Mesh mesh,
            out InfillLayerFieldGrid2D field)
        {
            mesh = null;
            field = null;
            int nPts = nxv * nyv;
            double[] gFlat = new double[nPts];
            Point3d[] pts = new Point3d[nPts];
            double half = frameSize * 0.5;
            Transform xyToWorld = Transform.PlaneToPlane(Plane.WorldXY, prep.Plane);

            double minG = double.PositiveInfinity;
            double maxG = double.NegativeInfinity;

            Action<int> sample = idx =>
            {
                int j = idx / nxv;
                int i = idx - j * nxv;
                double x = prep.CenterXY.X - half + i * cellSize;
                double y = prep.CenterXY.Y - half + j * cellSize;
                Point3d pxy = new Point3d(x, y, 0.0);
                double g = DistanceFieldToCurvesXY(pxy, prep.Curves2D, tol, closedRegions) - isoOffset;
                if (invert)
                    g = -g;
                gFlat[idx] = g;
                Point3d pw = pxy;
                pw.Transform(xyToWorld);
                pts[idx] = pw;
            };

#if USE_PARALLEL
            if (Environment.ProcessorCount > 1 && nPts >= 40000)
                Parallel.For(0, nPts, sample);
            else
                for (int idx = 0; idx < nPts; idx++) sample(idx);
#else
            for (int idx = 0; idx < nPts; idx++) sample(idx);
#endif

            for (int i = 0; i < gFlat.Length; i++)
            {
                double g = gFlat[i];
                if (g < minG) minG = g;
                if (g > maxG) maxG = g;
            }

            if (buildPreviewMesh)
            {
                mesh = new Mesh();
                for (int i = 0; i < pts.Length; i++)
                    mesh.Vertices.Add(pts[i]);

                for (int j = 0; j < nyv - 1; j++)
                {
                    for (int i = 0; i < nxv - 1; i++)
                    {
                        int a = i + j * nxv;
                        int b = i + 1 + j * nxv;
                        int c = i + 1 + (j + 1) * nxv;
                        int d = i + (j + 1) * nxv;
                        mesh.Faces.AddFace(a, b, c, d);
                    }
                }

                mesh.Normals.ComputeNormals();
                mesh.VertexColors.CreateMonotoneMesh(Color.White);
                double amp = Math.Max(Math.Abs(minG), Math.Abs(maxG));
                if (amp < 1e-12) amp = 1.0;
                for (int i = 0; i < gFlat.Length && i < mesh.VertexColors.Count; i++)
                    mesh.VertexColors[i] = DivergingBlueWhiteRed(Math.Max(-1.0, Math.Min(1.0, gFlat[i] / amp)));
            }

            field = new InfillLayerFieldGrid2D(
                gFlat,
                nxv,
                nyv,
                prep.Plane,
                prep.CenterXY,
                frameSize,
                cellSize,
                isoOffset);

            return (!buildPreviewMesh || (mesh != null && mesh.IsValid)) && field.G != null && field.G.Length >= nxv * nyv;
        }

        private static Dictionary<int, List<Curve>> GroupCurvesByLayer(GH_Structure<GH_Curve> tree)
        {
            var result = new Dictionary<int, List<Curve>>();
            for (int pi = 0; pi < tree.PathCount; pi++)
            {
                GH_Path path = tree.Paths[pi];
                int layerKey = path.Length > 0 ? path[0] : pi;
                if (!result.TryGetValue(layerKey, out var list))
                {
                    list = new List<Curve>();
                    result[layerKey] = list;
                }

                foreach (GH_Curve goo in tree.Branches[pi])
                {
                    Curve c = goo?.Value?.DuplicateCurve();
                    if (c != null && c.IsValid)
                        list.Add(c);
                }
            }
            return result;
        }

        private static bool HasOpenCurves(Dictionary<int, List<Curve>> layers)
        {
            foreach (var kv in layers)
                foreach (Curve c in kv.Value)
                    if (c != null && c.IsValid && !c.IsClosed)
                        return true;
            return false;
        }

        private static bool HasClosedCurves(Dictionary<int, List<Curve>> layers)
        {
            foreach (var kv in layers)
                foreach (Curve c in kv.Value)
                    if (c != null && c.IsValid && c.IsClosed)
                        return true;
            return false;
        }

        private static Dictionary<int, Plane> BuildPlaneMap(GH_Structure<GH_Plane> tree)
        {
            var result = new Dictionary<int, Plane>();
            if (tree == null) return result;

            for (int pi = 0; pi < tree.PathCount; pi++)
            {
                GH_Path path = tree.Paths[pi];
                int layerKey = path.Length > 0 ? path[0] : pi;
                IList<GH_Plane> branch = tree.Branches[pi];
                if (branch == null || branch.Count == 0) continue;
                Plane p = branch[0].Value;
                if (p.IsValid && !result.ContainsKey(layerKey))
                    result[layerKey] = p;
            }
            return result;
        }

        private static BoundingBox BuildArrayBoundingBox(
            List<LayerSource> layers,
            Plane referencePlane)
        {
            BoundingBox result = BoundingBox.Empty;
            Transform worldToReference = Transform.PlaneToPlane(referencePlane, Plane.WorldXY);

            foreach (LayerSource layer in layers)
            {
                if (layer == null || layer.Curves == null) continue;
                foreach (Curve curve in layer.Curves)
                {
                    if (curve == null || !curve.IsValid) continue;
                    Curve local = curve.DuplicateCurve();
                    if (local == null || !local.Transform(worldToReference)) continue;
                    result.Union(local.GetBoundingBox(true));
                }
            }

            return result;
        }

        private static Plane AlignPlaneToReference(Plane source, Plane reference)
        {
            Vector3d normal = source.ZAxis;
            if (!normal.Unitize())
                normal = reference.ZAxis;
            if (normal * reference.ZAxis < 0.0)
                normal.Reverse();

            // Project the reference X axis onto the layer plane. This removes
            // per-layer X/Y rotations, swaps, and sign flips while retaining the
            // layer normal for tilted/non-planar stacks.
            Vector3d xAxis = reference.XAxis - (reference.XAxis * normal) * normal;
            if (!xAxis.Unitize())
            {
                xAxis = source.XAxis;
                xAxis -= (xAxis * normal) * normal;
                if (!xAxis.Unitize())
                    xAxis = Vector3d.CrossProduct(reference.YAxis, normal);
            }

            Vector3d yAxis = Vector3d.CrossProduct(normal, xAxis);
            if (!yAxis.Unitize())
                yAxis = source.YAxis;

            if (yAxis * reference.YAxis < 0.0)
            {
                xAxis.Reverse();
                yAxis.Reverse();
            }

            return new Plane(source.Origin, xAxis, yAxis);
        }

        private static void DiagnoseLayerFrames(
            List<LayerPrep> layers,
            Plane referencePlane,
            double resolution,
            double tolerance,
            List<string> warnings)
        {
            if (layers == null || layers.Count < 2) return;

            Vector3d stackAxis = referencePlane.ZAxis;
            if (!stackAxis.Unitize()) stackAxis = Vector3d.ZAxis;
            double spacingTol = Math.Max(tolerance * 10.0, 1e-7);
            var spacing = new List<double>(layers.Count - 1);
            int direction = 0;

            for (int i = 1; i < layers.Count; i++)
            {
                double previous = (layers[i - 1].Plane.Origin - referencePlane.Origin) * stackAxis;
                double current = (layers[i].Plane.Origin - referencePlane.Origin) * stackAxis;
                double delta = current - previous;

                if (Math.Abs(delta) <= spacingTol)
                {
                    warnings.Add(
                        $"Layers {layers[i - 1].LayerKey} and {layers[i].LayerKey}: duplicate or near-duplicate frame positions. Fi06 cannot form a stable volume between them.");
                    continue;
                }

                spacing.Add(Math.Abs(delta));
                int sign = delta > 0.0 ? 1 : -1;
                if (direction == 0)
                    direction = sign;
                else if (sign != direction)
                    warnings.Add(
                        $"Layer {layers[i].LayerKey}: frame order reverses along the reference normal. Fi06 expects monotonic slice ordering.");
            }

            if (spacing.Count >= 3)
            {
                double[] ordered = spacing.OrderBy(x => x).ToArray();
                double median = ordered[ordered.Length / 2];
                double largeGap = Math.Max(median * 3.0, median + Math.Max(resolution, spacingTol));
                for (int i = 0; i < spacing.Count; i++)
                {
                    if (spacing[i] > largeGap)
                        warnings.Add(
                            $"Unusually large distance between consecutive prepared frames ({spacing[i]:0.###}; median {median:0.###}). Fi06 interpolation may visibly stretch there.");
                }
            }
        }

        private static List<Curve> ProjectCurvesToXY(List<Curve> curves, Plane plane)
        {
            var result = new List<Curve>(curves.Count);
            Transform worldToXY = Transform.PlaneToPlane(plane, Plane.WorldXY);
            foreach (Curve c in curves)
            {
                if (c == null || !c.IsValid) continue;
                Curve projected = Curve.ProjectToPlane(c, plane);
                if (projected == null || !projected.IsValid) continue;
                projected.Transform(worldToXY);
                result.Add(projected);
            }
            return result;
        }

        private static bool TryInferPlane(List<Curve> curves, out Plane plane)
        {
            plane = Plane.Unset;
            var points = new List<Point3d>();
            foreach (Curve c in curves)
            {
                if (c == null || !c.IsValid) continue;
                double len = c.GetLength();
                int count = Math.Max(4, Math.Min(40, (int)Math.Ceiling(len / 10.0)));
                for (int i = 0; i < count; i++)
                {
                    double s = count == 1 ? 0.5 : (double)i / (count - 1);
                    if (!c.LengthParameter(s * len, out double t))
                        t = c.Domain.ParameterAt(s);
                    points.Add(c.PointAt(t));
                }
            }
            return points.Count >= 3 && Plane.FitPlaneToPoints(points, out plane) == PlaneFitResult.Success && plane.IsValid;
        }

        private static double DistanceFieldToCurvesXY(Point3d pxy, List<Curve> crv2D, double tol, bool closedRegions)
        {
            double openUnsignedMin = double.PositiveInfinity;
            double closedBoundaryMin = double.PositiveInfinity;
            bool insideClosed = false;

            foreach (Curve c in crv2D)
            {
                if (c == null || !c.IsValid) continue;
                if (!c.ClosestPoint(pxy, out double t)) continue;
                double dist = pxy.DistanceTo(c.PointAt(t));
                if (closedRegions && c.IsClosed)
                {
                    if (dist < closedBoundaryMin) closedBoundaryMin = dist;
                    PointContainment pc = c.Contains(pxy, Plane.WorldXY, tol);
                    if (pc == PointContainment.Inside || pc == PointContainment.Coincident)
                        insideClosed = !insideClosed;
                }
                else if (dist < openUnsignedMin)
                {
                    openUnsignedMin = dist;
                }
            }

            double result = double.PositiveInfinity;
            if (!double.IsInfinity(closedBoundaryMin))
            {
                double signedClosed = insideClosed ? -closedBoundaryMin : closedBoundaryMin;
                result = Math.Min(result, signedClosed);
            }
            if (!double.IsInfinity(openUnsignedMin))
                result = Math.Min(result, openUnsignedMin);
            return double.IsInfinity(result) ? 1e9 : result;
        }

        private static Color DivergingBlueWhiteRed(double t)
        {
            t = Math.Max(-1.0, Math.Min(1.0, t));
            if (t >= 0.0)
            {
                int r = 255;
                int g = (int)(255 * (1.0 - 0.5 * t));
                int b = (int)(255 * (1.0 - t));
                return Color.FromArgb(r, g, b);
            }
            else
            {
                double u = -t;
                int r = (int)(255 * (1.0 - u));
                int g = (int)(255 * (1.0 - 0.5 * u));
                int b = 255;
                return Color.FromArgb(r, g, b);
            }
        }

        public sealed class InfillLayerFieldGrid2D
        {
            public readonly double[] G;
            public readonly int NxVerts;
            public readonly int NyVerts;
            public readonly Plane Plane;
            public readonly Point2d CenterXY;
            public readonly double FrameSize;
            public readonly double CellSize;
            public readonly double IsoOffset;

            public InfillLayerFieldGrid2D(
                double[] g,
                int nxVerts,
                int nyVerts,
                Plane plane,
                Point2d centerXY,
                double frameSize,
                double cellSize,
                double isoOffset)
            {
                G = g;
                NxVerts = nxVerts;
                NyVerts = nyVerts;
                Plane = plane;
                CenterXY = centerXY;
                FrameSize = frameSize;
                CellSize = cellSize;
                IsoOffset = isoOffset;
            }
        }

        private sealed class LayerPrep
        {
            public readonly int LayerKey;
            public readonly Plane Plane;
            public readonly List<Curve> Curves2D;
            public readonly Point2d CenterXY;

            public LayerPrep(int layerKey, Plane plane, List<Curve> curves2D, Point2d centerXY)
            {
                LayerKey = layerKey;
                Plane = plane;
                Curves2D = curves2D;
                CenterXY = centerXY;
            }
        }

        private sealed class LayerSource
        {
            public readonly int LayerKey;
            public readonly Plane Plane;
            public readonly List<Curve> Curves;

            public LayerSource(int layerKey, Plane plane, List<Curve> curves)
            {
                LayerKey = layerKey;
                Plane = plane;
                Curves = curves;
            }
        }
    }
}
