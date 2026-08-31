// wsp_Fa04_Map UV Cells To Surface.cs
// WASPer_3DP — Subcategory: 2.1_Facades
//
// Maps normalized UV cell domains from Fa03 onto a treated/wavy facade surface,
// using a reference surface to preserve projected panel proportions.
//
// Designed to read:
//   uv_cell  = DataTree of normalized UV domains from wsp_Fa03_Facade Panelling
//   uv_bool  = Boolean keep mask from wsp_Fa03_Facade Panelling
//
// Core logic:
//   1. Treat each uv_cell branch as a rectangle in a normalized flat reference facade.
//      Each branch must contain:
//          u0, u1, v0, v1
//   2. Generate a normalized point grid inside that reference rectangle.
//   3. Convert normalized coordinates into points on srf_ref.
//   4. Ray-project those points onto the treated/wavy surface.
//   5. Build a mesh panel from the projected points.
//   6. Use uv_bool to separate valid facade panels from void/opening/outside cells.
//
// Important:
//   - This component preserves projected panel proportions from Fa03.
//   - The wavy surface only changes the 3D shape/area of each panel.
//   - Panels can be output as meshes, surfaces, or breps.
//   - Trimmed/L-shaped opening-adjacent panels are represented by their rectangular
//     UV cell domain in this version.
//
// Author: Juan Diego Vargas
// Created/revised: 2026-05-08

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace WASPer_3DP.Components._2_1_Facades
{
    public class wsp_Fa04_MapUVCellsToSurface : GH_Component
    {
        // ─── Version ───────────────────────────────────────────────────────────────

        private readonly string _versionTag;

        // ─── Constructor ───────────────────────────────────────────────────────────

        public wsp_Fa04_MapUVCellsToSurface()
            : base(
                "wsp_Fa04_Map UV Cells To Surface",
                "MapUVCells",
                "Maps normalized UV cell domains from a reference surface onto a treated or wavy facade surface.\n\n" +
                "Use this after wsp_Fa03_Facade Panelling.\n\n" +
                "srf_ref defines the flat/reference facade proportions. srf_target defines\n" +
                "the treated surface receiving the projected panels.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.1_Facades")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        // ─── Identity ──────────────────────────────────────────────────────────────

        public override Guid ComponentGuid
        {
            get { return new Guid("C7D5F1E9-8C7F-4F51-8D2B-1E4A91E83B3C"); }
        }

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fa04_Map UV Cells To Surface.png"))
                    {
                        if (s != null) return new Bitmap(s);
                    }
                }
                catch { }
                return null;
            }
        }

        // ─── Params ────────────────────────────────────────────────────────────────

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter(
                "Target Surface",
                "srf_target",
                "Target treated/wavy facade surface as a single-face Brep.\n" +
                "If multiple faces are provided, face 0 is used.",
                GH_ParamAccess.item);

            pManager.AddBrepParameter(
                "Reference Surface",
                "srf_ref",
                "Reference facade surface used to place the normalized UV cells before projection.\n" +
                "This should match the surface used to generate the Fa03 UV domains.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "UV Cell Domains",
                "uv_cell",
                "DataTree of normalized UV cell domains from Fa03.\n" +
                "Each branch must contain four numbers: u0, u1, v0, v1.",
                GH_ParamAccess.tree);

            pManager.AddBooleanParameter(
                "UV Keep Mask",
                "uv_bool",
                "Boolean list from Fa03, aligned with uv_cell.\n" +
                "True = valid facade material cell.\n" +
                "False = void/opening/outside cell.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "Geometry Mode",
                "geo_mode",
                "Output geometry mode.\n" +
                "0 = quad mesh\n" +
                "1 = Nurbs surface\n" +
                "2 = Brep",
                GH_ParamAccess.item,
                1);

            pManager.AddIntegerParameter(
                "Resolution U",
                "res_u",
                "Number of subdivisions along each panel's local U direction.\n" +
                "1 creates a single quad. Higher values follow wavy treatments better.",
                GH_ParamAccess.item,
                8);

            pManager.AddIntegerParameter(
                "Resolution V",
                "res_v",
                "Number of subdivisions along each panel's local V direction.\n" +
                "1 creates a single quad. Higher values follow wavy treatments better.",
                GH_ParamAccess.item,
                8);

            pManager.AddNumberParameter(
                "Tolerance",
                "tol",
                "Model tolerance used for domain validation and ray intersection.",
                GH_ParamAccess.item,
                0.001);

            pManager[3].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;
            pManager[6].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGeometryParameter(
                "All Panels",
                "pa_all",
                "Panel geometry for every uv_cell domain.\n" +
                "This output remains aligned with uv_cell, grid, pa_types, and uv_bool.",
                GH_ParamAccess.list);

            pManager.AddGeometryParameter(
                "Valid Panels",
                "pa_valid",
                "Panel geometry where uv_bool is true.",
                GH_ParamAccess.list);

            pManager.AddGeometryParameter(
                "Void Panels",
                "pa_void",
                "Panel geometry where uv_bool is false.\n" +
                "These may represent openings, void cells, or outside-facade cells depending on Fa03.",
                GH_ParamAccess.list);

            pManager.AddCurveParameter(
                "All Panel Edges",
                "edge_all",
                "Boundary curves for all generated panels.",
                GH_ParamAccess.list);

            pManager.AddCurveParameter(
                "Valid Panel Edges",
                "edge_valid",
                "Boundary curves for valid panels only.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "All Areas",
                "areas_all",
                "Area of every generated panel, aligned with pa_all.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "Valid Areas",
                "areas_valid",
                "Area of valid panels only.",
                GH_ParamAccess.list);

            pManager.AddPlaneParameter(
                "Frames",
                "frames",
                "Approximate center frame for each generated panel, aligned with pa_all.",
                GH_ParamAccess.list);

            pManager.AddVectorParameter(
                "Normals",
                "normals",
                "Surface normal at the center of each generated panel, aligned with pa_all.",
                GH_ParamAccess.list);

            pManager.AddPlaneParameter(
                "Reference Frame",
                "ref_frame",
                "Reference frame used to generate the projected panel layout.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "Info",
                "info",
                "Summary of generated panels and skipped/invalid domains.",
                GH_ParamAccess.item);
        }

        // ─── Solve ─────────────────────────────────────────────────────────────────

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── 1. Inputs ─────────────────────────────────────────────────────────

            Brep targetBrep = null;
            Brep referenceBrep = null;
            GH_Structure<GH_Number> uvTree = null;
            var keepMask = new List<bool>();

            int geoMode = 0;
            int resU = 8;
            int resV = 8;

            double tol = 0.001;

            if (!DA.GetData(0, ref targetBrep) || targetBrep == null) return;
            if (!DA.GetData(1, ref referenceBrep) || referenceBrep == null) return;
            if (!DA.GetDataTree(2, out uvTree) || uvTree == null) return;

            DA.GetDataList(3, keepMask);
            DA.GetData(4, ref geoMode);
            DA.GetData(5, ref resU);
            DA.GetData(6, ref resV);
            DA.GetData(7, ref tol);

            if (tol <= 0.0)
            {
                tol = Rhino.RhinoDoc.ActiveDoc != null
                    ? Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                    : 0.001;
            }

            resU = Math.Max(1, resU);
            resV = Math.Max(1, resV);
            geoMode = (int)Clamp(geoMode, 0, 2);

            if (targetBrep.Faces.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "srf_target has no faces.");
                return;
            }

            if (referenceBrep.Faces.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "srf_ref has no faces.");
                return;
            }

            if (targetBrep.Faces.Count > 1)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"srf_target has {targetBrep.Faces.Count} faces. Only face 0 is used.");
            }

            if (referenceBrep.Faces.Count > 1)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"srf_ref has {referenceBrep.Faces.Count} faces. Only face 0 is used.");
            }

            BrepFace targetFace = targetBrep.Faces[0];
            BrepFace referenceFace = referenceBrep.Faces[0];

            // ── 2. Build reference frame ─────────────────────────────────────────

            Plane refPlane = EstimateSurfaceFrame(referenceFace);
            ReferenceBox refBox = BuildReferenceBox(referenceFace, refPlane, 0.0, 0.0);

            if (refBox.Width <= tol || refBox.Height <= tol)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Reference width or height is near zero.");
                return;
            }

            double projDepth = EstimateProjectionDepth(referenceBrep, targetBrep, refBox);

            Vector3d projectionNormal = refBox.Plane.ZAxis;
            if (!projectionNormal.Unitize())
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid reference normal.");
                return;
            }

            // ── 3. Read UV domains ───────────────────────────────────────────────

            var domains = ReadNormalizedDomains(uvTree, out int invalidBranches);

            if (domains.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid UV domains were read from uv_cell.");
                return;
            }

            if (keepMask.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "uv_bool was not supplied. All UV cells will be treated as valid.");
            }
            else if (keepMask.Count != domains.Count)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"uv_bool count ({keepMask.Count}) does not match uv_cell count ({domains.Count}). Missing values will be treated as false.");
            }

            // ── 4. Build mapped panel geometry ───────────────────────────────────

            var allPanels = new List<GeometryBase>();
            var validPanels = new List<GeometryBase>();
            var voidPanels = new List<GeometryBase>();

            var allEdges = new List<Curve>();
            var validEdges = new List<Curve>();

            var allAreas = new List<double>();
            var validAreas = new List<double>();

            var frames = new List<Plane>();
            var normals = new List<Vector3d>();

            int skippedCollapsed = 0;
            int failedMeshes = 0;
            int projectionFallbacksTotal = 0;
            int validCount = 0;
            int voidCount = 0;

            for (int i = 0; i < domains.Count; i++)
            {
                NormalizedDomain nd = CleanDomain(domains[i]);

                if (nd.IsCollapsed(tol))
                {
                    skippedCollapsed++;
                    continue;
                }

                bool keep = keepMask.Count == 0
                    ? true
                    : (i < keepMask.Count && keepMask[i]);

                int projectionFallbacks;
                Mesh mesh = BuildPanelMeshByProjection(
                    targetBrep,
                    targetFace,
                    refBox,
                    projectionNormal,
                    projDepth,
                    nd,
                    resU,
                    resV,
                    false,
                    false,
                    false,
                    false,
                    tol,
                    out projectionFallbacks);

                projectionFallbacksTotal += projectionFallbacks;

                if (mesh == null || !mesh.IsValid || mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
                {
                    failedMeshes++;
                    continue;
                }

                mesh.Normals.ComputeNormals();
                mesh.Compact();

                GeometryBase panel = PanelGeometryFromMesh(mesh, resU, resV, geoMode);
                if (panel == null || !panel.IsValid)
                {
                    failedMeshes++;
                    continue;
                }

                double area = PanelArea(panel, mesh);
                List<Curve> edges = BuildPanelBoundaryCurvesFromMesh(mesh, resU, resV);

                Plane frame = GetMeshCenterFrame(mesh, false);
                Vector3d normal = frame.ZAxis;

                allPanels.Add(panel);
                allEdges.AddRange(edges);
                allAreas.Add(area);
                frames.Add(frame);
                normals.Add(normal);

                if (keep)
                {
                    validPanels.Add(panel);
                    validEdges.AddRange(edges);
                    validAreas.Add(area);
                    validCount++;
                }
                else
                {
                    voidPanels.Add(panel);
                    voidCount++;
                }
            }

            // ── 5. Outputs ───────────────────────────────────────────────────────

            DA.SetDataList(0, allPanels);
            DA.SetDataList(1, validPanels);
            DA.SetDataList(2, voidPanels);

            DA.SetDataList(3, allEdges);
            DA.SetDataList(4, validEdges);

            DA.SetDataList(5, allAreas);
            DA.SetDataList(6, validAreas);

            DA.SetDataList(7, frames);
            DA.SetDataList(8, normals);

            DA.SetData(9, refBox.Plane);

            string info =
                $"UV domains: {domains.Count}  |  " +
                $"All panels: {allPanels.Count}  |  Valid: {validCount}  Void/false: {voidCount}  |  " +
                $"geo_mode: {GeometryModeName(geoMode)}  |  " +
                $"Resolution: {resU} × {resV}  |  " +
                $"Reference size: {refBox.Width:F3} × {refBox.Height:F3}  |  Projection depth: {projDepth:F3}  |  " +
                $"Invalid branches: {invalidBranches}  Collapsed/skipped: {skippedCollapsed}  Failed meshes: {failedMeshes}  |  " +
                $"Projection fallbacks: {projectionFallbacksTotal}";

            DA.SetData(10, info);
        }

        // ─── Data structures ──────────────────────────────────────────────────────

        private struct NormalizedDomain
        {
            public double U0;
            public double U1;
            public double V0;
            public double V1;

            public NormalizedDomain(double u0, double u1, double v0, double v1)
            {
                U0 = u0;
                U1 = u1;
                V0 = v0;
                V1 = v1;
            }

            public bool IsCollapsed(double tol)
            {
                return Math.Abs(U1 - U0) <= tol || Math.Abs(V1 - V0) <= tol;
            }
        }

        private struct ReferenceBox
        {
            public Plane Plane;
            public double Width;
            public double Height;

            public ReferenceBox(Plane plane, double width, double height)
            {
                Plane = plane;
                Width = width;
                Height = height;
            }
        }

        // ─── Reference frame helpers ──────────────────────────────────────────────

        private static Plane EstimateSurfaceFrame(BrepFace face)
        {
            Interval domU = face.Domain(0);
            Interval domV = face.Domain(1);

            double u = domU.Mid;
            double v = domV.Mid;

            Plane frame;
            if (face.FrameAt(u, v, out frame))
            {
                return frame;
            }

            Point3d p = face.PointAt(u, v);
            Vector3d normal = face.NormalAt(u, v);

            if (!normal.IsValid || normal.IsTiny())
            {
                normal = Vector3d.ZAxis;
            }

            normal.Unitize();

            Vector3d xAxis = Vector3d.XAxis;
            if (Math.Abs(xAxis * normal) > 0.95)
            {
                xAxis = Vector3d.YAxis;
            }

            Vector3d yAxis = Vector3d.CrossProduct(normal, xAxis);
            yAxis.Unitize();

            xAxis = Vector3d.CrossProduct(yAxis, normal);
            xAxis.Unitize();

            return new Plane(p, xAxis, yAxis);
        }

        private static ReferenceBox BuildReferenceBox(
            BrepFace face,
            Plane basePlane,
            double inputWidth,
            double inputHeight)
        {
            var samplePts = SampleFacePoints(face, 16, 16);

            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;

            foreach (Point3d p in samplePts)
            {
                Vector3d rel = p - basePlane.Origin;
                double x = rel * basePlane.XAxis;
                double y = rel * basePlane.YAxis;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            double width = inputWidth > 0.0 ? inputWidth : maxX - minX;
            double height = inputHeight > 0.0 ? inputHeight : maxY - minY;

            Plane refPlane;

            if (inputWidth > 0.0 && inputHeight > 0.0)
            {
                refPlane = basePlane;
            }
            else
            {
                Point3d lowerLeft = basePlane.Origin + basePlane.XAxis * minX + basePlane.YAxis * minY;
                refPlane = new Plane(lowerLeft, basePlane.XAxis, basePlane.YAxis);
            }

            return new ReferenceBox(refPlane, width, height);
        }

        private static double EstimateProjectionDepth(
            Brep referenceBrep,
            Brep targetBrep,
            ReferenceBox refBox)
        {
            double size = Math.Max(refBox.Width, refBox.Height);

            BoundingBox refBb = referenceBrep != null
                ? referenceBrep.GetBoundingBox(true)
                : BoundingBox.Empty;

            BoundingBox targetBb = targetBrep != null
                ? targetBrep.GetBoundingBox(true)
                : BoundingBox.Empty;

            double centerDistance = 0.0;
            double diagonal = size;

            if (refBb.IsValid && targetBb.IsValid)
            {
                centerDistance = refBb.Center.DistanceTo(targetBb.Center);
                diagonal = Math.Max(refBb.Diagonal.Length, targetBb.Diagonal.Length);
            }

            return Math.Max(size * 2.0, centerDistance + diagonal * 2.0);
        }

        private static List<Point3d> SampleFacePoints(BrepFace face, int countU, int countV)
        {
            var pts = new List<Point3d>();

            Interval domU = face.Domain(0);
            Interval domV = face.Domain(1);

            countU = Math.Max(2, countU);
            countV = Math.Max(2, countV);

            for (int j = 0; j < countV; j++)
            {
                double tv = (double)j / (countV - 1);
                double v = domV.ParameterAt(tv);

                for (int i = 0; i < countU; i++)
                {
                    double tu = (double)i / (countU - 1);
                    double u = domU.ParameterAt(tu);

                    pts.Add(face.PointAt(u, v));
                }
            }

            return pts;
        }

        // ─── UV domain helpers ────────────────────────────────────────────────────

        private static List<NormalizedDomain> ReadNormalizedDomains(
            GH_Structure<GH_Number> tree,
            out int invalidBranches)
        {
            var domains = new List<NormalizedDomain>();
            invalidBranches = 0;

            if (tree == null) return domains;

            foreach (GH_Path path in tree.Paths)
            {
                IList<GH_Number> branch = tree.get_Branch(path).Cast<GH_Number>().ToList();

                if (branch == null || branch.Count < 4)
                {
                    invalidBranches++;
                    continue;
                }

                double u0 = branch[0].Value;
                double u1 = branch[1].Value;
                double v0 = branch[2].Value;
                double v1 = branch[3].Value;

                if (double.IsNaN(u0) || double.IsNaN(u1) || double.IsNaN(v0) || double.IsNaN(v1) ||
                    double.IsInfinity(u0) || double.IsInfinity(u1) || double.IsInfinity(v0) || double.IsInfinity(v1))
                {
                    invalidBranches++;
                    continue;
                }

                domains.Add(new NormalizedDomain(u0, u1, v0, v1));
            }

            return domains;
        }

        private static NormalizedDomain CleanDomain(NormalizedDomain d)
        {
            double u0 = Clamp(d.U0, 0.0, 1.0);
            double u1 = Clamp(d.U1, 0.0, 1.0);
            double v0 = Clamp(d.V0, 0.0, 1.0);
            double v1 = Clamp(d.V1, 0.0, 1.0);

            if (u1 < u0)
            {
                double temp = u0;
                u0 = u1;
                u1 = temp;
            }

            if (v1 < v0)
            {
                double temp = v0;
                v0 = v1;
                v1 = temp;
            }

            return new NormalizedDomain(u0, u1, v0, v1);
        }

        private static void TransformNormalizedUv(
            double uIn,
            double vIn,
            bool transposeUv,
            bool flipU,
            bool flipV,
            out double uOut,
            out double vOut)
        {
            double u = uIn;
            double v = vIn;

            if (transposeUv)
            {
                double temp = u;
                u = v;
                v = temp;
            }

            if (flipU) u = 1.0 - u;
            if (flipV) v = 1.0 - v;

            uOut = Clamp(u, 0.0, 1.0);
            vOut = Clamp(v, 0.0, 1.0);
        }

        private static Point3d ReferencePoint(
            ReferenceBox refBox,
            double uNorm,
            double vNorm,
            bool transposeUv,
            bool flipU,
            bool flipV)
        {
            TransformNormalizedUv(
                uNorm,
                vNorm,
                transposeUv,
                flipU,
                flipV,
                out double u,
                out double v);

            return refBox.Plane.Origin
                + refBox.Plane.XAxis * (u * refBox.Width)
                + refBox.Plane.YAxis * (v * refBox.Height);
        }

        // ─── Projection helpers ───────────────────────────────────────────────────

        private static Point3d ProjectReferencePointToSurface(
            Point3d refPoint,
            Vector3d projectionNormal,
            double projectionDepth,
            Brep targetBrep,
            BrepFace face,
            double tol,
            out bool usedFallback)
        {
            usedFallback = false;

            Point3d a = refPoint - projectionNormal * projectionDepth;
            Point3d b = refPoint + projectionNormal * projectionDepth;

            var rayCurve = new LineCurve(a, b);

            Curve[] overlapCurves;
            Point3d[] intersectionPoints;

            bool hit = Intersection.CurveBrep(
                rayCurve,
                targetBrep,
                tol,
                out overlapCurves,
                out intersectionPoints);

            if (hit && intersectionPoints != null && intersectionPoints.Length > 0)
            {
                Point3d best = intersectionPoints[0];
                double bestDist = best.DistanceToSquared(refPoint);

                for (int i = 1; i < intersectionPoints.Length; i++)
                {
                    double d = intersectionPoints[i].DistanceToSquared(refPoint);
                    if (d < bestDist)
                    {
                        best = intersectionPoints[i];
                        bestDist = d;
                    }
                }

                return best;
            }

            usedFallback = true;

            double u;
            double v;

            if (face.ClosestPoint(refPoint, out u, out v))
            {
                return face.PointAt(u, v);
            }

            return refPoint;
        }

        // ─── Mesh construction ────────────────────────────────────────────────────

        private static Mesh BuildPanelMeshByProjection(
            Brep targetBrep,
            BrepFace face,
            ReferenceBox refBox,
            Vector3d projectionNormal,
            double projectionDepth,
            NormalizedDomain domain,
            int resU,
            int resV,
            bool transposeUv,
            bool flipU,
            bool flipV,
            bool flipNorm,
            double tol,
            out int fallbackCount)
        {
            fallbackCount = 0;

            if (targetBrep == null || face == null) return null;

            var mesh = new Mesh();

            for (int j = 0; j <= resV; j++)
            {
                double tv = (double)j / resV;
                double v = Lerp(domain.V0, domain.V1, tv);

                for (int i = 0; i <= resU; i++)
                {
                    double tu = (double)i / resU;
                    double u = Lerp(domain.U0, domain.U1, tu);

                    Point3d refPoint = ReferencePoint(
                        refBox,
                        u,
                        v,
                        transposeUv,
                        flipU,
                        flipV);

                    bool usedFallback;
                    Point3d projected = ProjectReferencePointToSurface(
                        refPoint,
                        projectionNormal,
                        projectionDepth,
                        targetBrep,
                        face,
                        tol,
                        out usedFallback);

                    if (usedFallback) fallbackCount++;

                    mesh.Vertices.Add(projected);
                }
            }

            int row = resU + 1;

            for (int j = 0; j < resV; j++)
            {
                for (int i = 0; i < resU; i++)
                {
                    int a = j * row + i;
                    int b = j * row + i + 1;
                    int c = (j + 1) * row + i + 1;
                    int d = (j + 1) * row + i;

                    if (flipNorm)
                    {
                        mesh.Faces.AddFace(a, d, c, b);
                    }
                    else
                    {
                        mesh.Faces.AddFace(a, b, c, d);
                    }
                }
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();

            return mesh;
        }

        private static GeometryBase PanelGeometryFromMesh(
            Mesh mesh,
            int resU,
            int resV,
            int geoMode)
        {
            if (mesh == null || !mesh.IsValid) return null;

            if (geoMode == 0)
            {
                return mesh.DuplicateMesh();
            }

            NurbsSurface surface = SurfaceFromPanelMesh(mesh, resU, resV);
            if (surface == null || !surface.IsValid)
            {
                return mesh.DuplicateMesh();
            }

            if (geoMode == 1)
            {
                return surface;
            }

            Brep brep = surface.ToBrep();
            return brep != null && brep.IsValid ? (GeometryBase)brep : surface;
        }

        private static NurbsSurface SurfaceFromPanelMesh(Mesh mesh, int resU, int resV)
        {
            if (mesh == null) return null;

            int uCount = resU + 1;
            int vCount = resV + 1;
            int expected = uCount * vCount;

            if (mesh.Vertices.Count < expected) return null;

            var pts = new List<Point3d>(expected);
            for (int i = 0; i < expected; i++)
            {
                Point3f p = mesh.Vertices[i];
                pts.Add(new Point3d(p.X, p.Y, p.Z));
            }

            int uDegree = Math.Max(1, Math.Min(3, resU));
            int vDegree = Math.Max(1, Math.Min(3, resV));

            try
            {
                return NurbsSurface.CreateFromPoints(pts, uCount, vCount, uDegree, vDegree);
            }
            catch
            {
                return null;
            }
        }

        private static List<Curve> BuildPanelBoundaryCurvesFromMesh(
            Mesh mesh,
            int resU,
            int resV)
        {
            var edges = new List<Curve>();

            if (mesh == null) return edges;

            int row = resU + 1;

            var bottom = new List<Point3d>();
            var right = new List<Point3d>();
            var top = new List<Point3d>();
            var left = new List<Point3d>();

            for (int i = 0; i <= resU; i++)
            {
                bottom.Add(mesh.Vertices[i]);
                top.Add(mesh.Vertices[resV * row + i]);
            }

            for (int j = 0; j <= resV; j++)
            {
                left.Add(mesh.Vertices[j * row]);
                right.Add(mesh.Vertices[j * row + resU]);
            }

            edges.Add(new Polyline(bottom).ToNurbsCurve());
            edges.Add(new Polyline(right).ToNurbsCurve());

            top.Reverse();
            left.Reverse();

            edges.Add(new Polyline(top).ToNurbsCurve());
            edges.Add(new Polyline(left).ToNurbsCurve());

            return edges;
        }

        // ─── Area / frame helpers ─────────────────────────────────────────────────

        private static double MeshArea(Mesh mesh)
        {
            if (mesh == null) return 0.0;

            AreaMassProperties amp = AreaMassProperties.Compute(mesh);
            if (amp == null) return 0.0;

            return Math.Abs(amp.Area);
        }

        private static double PanelArea(GeometryBase panel, Mesh fallbackMesh)
        {
            if (panel == null) return MeshArea(fallbackMesh);

            AreaMassProperties amp = null;

            var mesh = panel as Mesh;
            if (mesh != null) amp = AreaMassProperties.Compute(mesh);

            var brep = panel as Brep;
            if (brep != null) amp = AreaMassProperties.Compute(brep);

            var surface = panel as Surface;
            if (surface != null) amp = AreaMassProperties.Compute(surface);

            if (amp != null) return Math.Abs(amp.Area);

            return MeshArea(fallbackMesh);
        }

        private static string GeometryModeName(int geoMode)
        {
            if (geoMode == 1) return "1 Surface";
            if (geoMode == 2) return "2 Brep";
            return "0 Mesh";
        }

        private static Plane GetMeshCenterFrame(Mesh mesh, bool flipNorm)
        {
            if (mesh == null || mesh.Vertices.Count == 0)
            {
                return Plane.WorldXY;
            }

            Point3d center = Point3d.Origin;
            foreach (Point3f v in mesh.Vertices)
            {
                center += new Vector3d(v.X, v.Y, v.Z);
            }
            center /= mesh.Vertices.Count;

            Vector3d normal = Vector3d.Zero;

            if (mesh.Normals.Count == mesh.Vertices.Count)
            {
                foreach (Vector3f n in mesh.Normals)
                {
                    normal += new Vector3d(n.X, n.Y, n.Z);
                }
            }

            if (!normal.IsValid || normal.IsTiny())
            {
                normal = Vector3d.ZAxis;
            }

            if (flipNorm)
            {
                normal.Reverse();
            }

            normal.Unitize();

            Vector3d xAxis = Vector3d.XAxis;
            if (Math.Abs(xAxis * normal) > 0.95)
            {
                xAxis = Vector3d.YAxis;
            }

            Vector3d yAxis = Vector3d.CrossProduct(normal, xAxis);
            yAxis.Unitize();

            xAxis = Vector3d.CrossProduct(yAxis, normal);
            xAxis.Unitize();

            return new Plane(center, xAxis, yAxis);
        }

        // ─── Utility helpers ──────────────────────────────────────────────────────

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
