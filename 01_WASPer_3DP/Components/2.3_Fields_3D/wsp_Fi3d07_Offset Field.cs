// wsp_Fi3d07_Offset Field.cs
// WASPer_3DP - Subcategory: 2.3_Fields_3D
//
// Offsets one or more WASPer 3D fields.
// Field convention: negative = material / selected region, positive = outside.
// Positive offset expands the negative/material region:
//   output(p) = field(p) - offset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_3_Fields_3D
{
    public class wsp_Fi3d07_OffsetField : GH_Component
    {
        private const string NAME = "wsp_Fi3d07_Offset Field";
        private const string NICK = "Offset Field";
        private const string CAT = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "2.3_Fields_3D";
        private const long MAX_SAMPLES_PER_FIELD = 20000000;

        private readonly string _versionTag;

        public wsp_Fi3d07_OffsetField()
            : base(
                NAME,
                NICK,
                "Offsets one or more WASPer 3D fields by subtracting an offset from the field value.\n\n" +
                "Field convention: negative = material / selected region, positive = outside.\n" +
                "Positive offset expands the negative region; negative offset shrinks it. " +
                "If bound_field is connected, the result is clipped with max(offset_field, bound_field).",
                CAT,
                SUBCAT)
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("5D1E0C7B-7A93-4ED2-8C59-4F676A0A4F27");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d07_Offset Field.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }

                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "fields",
                "fields",
                "WASPer 3D field or fields to offset.\n" +
                "Convention before offset: negative = material / selected region, positive = outside.",
                GH_ParamAccess.list);
            pManager[0].DataMapping = GH_DataMapping.Flatten;

            pManager.AddNumberParameter(
                "offset",
                "offset",
                "Offset distance(s) in model units.\n" +
                "Positive values expand the negative/material region. Negative values shrink it.\n" +
                "A single value offsets uniformly. With a curve wired, multiple values are\n" +
                "interpolated along the curve by normalized curve length.",
                GH_ParamAccess.list,
                1.0);

            pManager.AddGenericParameter(
                "bound_field",
                "bound_field",
                "Optional boundary field used to clip the offset region.\n" +
                "The boundary field should be negative inside the desired domain and positive outside.\n" +
                "When connected, the output is max(field - offset, bound_field).",
                GH_ParamAccess.item);
            pManager[2].Optional = true;

            pManager.AddCurveParameter(
                "curve",
                "curve",
                "Optional. When wired, the offset is applied only near this curve, fading to\n" +
                "zero at 'radius'. Leave unwired for a uniform offset over the whole field.",
                GH_ParamAccess.item);
            pManager[3].Optional = true;

            pManager.AddNumberParameter(
                "radius",
                "radius",
                "Influence radius around the curve in model units (used only when a curve is wired).",
                GH_ParamAccess.item,
                10.0);
            pManager[4].Optional = true;

            pManager.AddIntegerParameter(
                "falloff",
                "falloff",
                "Falloff type for the curve influence: 0 = linear, 1 = smooth, 2 = gaussian\n" +
                "(used only when a curve is wired).",
                GH_ParamAccess.item,
                1);
            pManager[5].Optional = true;

            pManager.AddNumberParameter(
                "res",
                "res",
                "Sampling resolution in model units for mesh_offset. Smaller values are more detailed but slower.",
                GH_ParamAccess.item,
                2.0);

            pManager.AddBooleanParameter(
                "mesh",
                "mesh?",
                "If true, extract mesh_offset immediately. If false, only output field_offset.",
                GH_ParamAccess.item,
                true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "field_offset",
                "field",
                "Offset WASPer field or fields. Negative values are inside the selected output region.",
                GH_ParamAccess.list);

            pManager.AddMeshParameter(
                "mesh_offset",
                "mesh",
                "Extracted mesh from the offset field or fields. Empty when mesh? is false.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "info",
                "info",
                "Offset field and meshing diagnostics.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var goos = new List<IGH_Goo>();
            if (!DA.GetDataList(0, goos) || goos.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No field input provided.");
                return;
            }

            var offsetValues = new List<double>();
            IGH_Goo boundGoo = null;
            double res = 2.0;
            bool makeMesh = true;
            Curve curve = null;
            double radius = 10.0;
            int falloff = 1;

            DA.GetDataList(1, offsetValues);
            DA.GetData(2, ref boundGoo);
            DA.GetData(3, ref curve);
            DA.GetData(4, ref radius);
            DA.GetData(5, ref falloff);
            DA.GetData(6, ref res);
            DA.GetData(7, ref makeMesh);

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            res = Math.Max(res, tol * 10.0);

            if (offsetValues.Count == 0) offsetValues.Add(1.0);

            bool curveMode = curve != null && curve.IsValid;
            if (curveMode)
            {
                radius = Math.Max(radius, tol * 10.0);
                if (falloff < 0 || falloff > 2)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "falloff out of range. Using 1 = smooth.");
                    falloff = 1;
                }
            }
            else if (offsetValues.Count > 1)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "Multiple offset values were provided without a curve. Using the first value uniformly.");
            }

            // Uniform offset magnitude when no curve is wired (preserves prior behaviour).
            double uniformOffset = offsetValues[0];
            double maxAbsOffset = 0.0;
            for (int k = 0; k < offsetValues.Count; k++)
                maxAbsOffset = Math.Max(maxAbsOffset, Math.Abs(offsetValues[k]));

            WasperField boundField = ExtractField(boundGoo);
            if (boundGoo != null && boundField == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "bound_field was connected but is not a valid WASPer field. It was ignored.");
            }

            var sources = new List<WasperField>();
            int rejected = 0;
            foreach (var goo in goos)
            {
                var field = ExtractField(goo);
                if (field != null && field.Evaluator != null) sources.Add(field);
                else rejected++;
            }

            if (rejected > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{rejected} input item(s) were not valid WASPer fields and were ignored.");

            if (sources.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid WASPer fields found.");
                return;
            }

            if (makeMesh && maxAbsOffset > 0.0 && boundField == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No bound_field connected. Mesh extraction uses each source field domain padded by offset + resolution.");
            }

            var outFields = new List<WasperFieldGoo>();
            var outMeshes = new List<Mesh>();
            var infoLines = new List<string>();
            var totalWatch = Stopwatch.StartNew();
            long totalSamples = 0;
            int totalFaces = 0;
            double[] offsetArray = offsetValues.ToArray();
            FastCurveQuery meshCurveQuery = (curveMode && makeMesh)
                ? FastCurveQuery.Create(curve, res, radius, tol)
                : null;

            for (int i = 0; i < sources.Count; i++)
            {
                WasperField source = sources[i];
                BoundingBox domain = curveMode
                    ? BuildCurveDomain(source, boundField, curve, radius, maxAbsOffset, res)
                    : BuildOutputDomain(source, boundField, uniformOffset, res);
                WasperField result = curveMode
                    ? WasperFieldOps.CurveOffset(source, curve, offsetValues, radius, falloff, boundField, domain)
                    : WasperFieldOps.Offset(source, uniformOffset, boundField, domain);
                if (result == null) continue;

                outFields.Add(new WasperFieldGoo(result));

                if (!makeMesh)
                {
                    infoLines.Add(BuildFieldInfo(i, source, domain, 0, 0, 0, 0, 0, 0, "mesh skipped"));
                    continue;
                }

                if (!domain.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Field {i} has no valid domain for mesh extraction.");
                    infoLines.Add(BuildFieldInfo(i, source, domain, 0, 0, 0, 0, 0, 0, "invalid domain"));
                    continue;
                }

                Box sampleBox = BoxFromBoundingBox(domain);
                int nx, ny, nz;
                BuildGridCounts(sampleBox, res, out nx, out ny, out nz);

                long samples = (long)nx * ny * nz;
                totalSamples += samples;

                if (samples > MAX_SAMPLES_PER_FIELD)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Field {i} would create {samples:N0} samples. Increase res or use a smaller bound_field.");
                    infoLines.Add(BuildFieldInfo(i, source, domain, nx, ny, nz, samples, 0, 0, "sample cap exceeded"));
                    continue;
                }

                var scalars = new double[(int)samples];
                var points = new Point3d[(int)samples];

                var sampleWatch = Stopwatch.StartNew();
                if (curveMode && meshCurveQuery != null)
                    SampleCurveOffsetField(source, boundField, meshCurveQuery, offsetArray, radius, falloff, sampleBox, nx, ny, nz, scalars, points);
                else
                    SampleField(result, sampleBox, nx, ny, nz, scalars, points);
                sampleWatch.Stop();

                Mesh mesh = null;
                var mcWatch = Stopwatch.StartNew();
                try
                {
                    int threads = Math.Max(1, Environment.ProcessorCount - 1);
                    mesh = WasperMarchingCubes.Extract(
                        scalars,
                        points,
                        nx,
                        ny,
                        nz,
                        0.0,
                        Math.Max(res * 1e-6, tol * 0.25),
                        threads);
                }
                catch (Exception ex)
                {
                    mcWatch.Stop();
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Marching Cubes failed on field {i} ({nx}x{ny}x{nz} grid): {ex.Message}");
                    infoLines.Add(BuildFieldInfo(i, source, domain, nx, ny, nz, samples, sampleWatch.ElapsedMilliseconds, mcWatch.ElapsedMilliseconds, "mc failed"));
                    continue;
                }
                mcWatch.Stop();

                if (mesh == null || mesh.Faces.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"No mesh was generated for field {i}. The result may be empty at this resolution.");
                    infoLines.Add(BuildFieldInfo(i, source, domain, nx, ny, nz, samples, sampleWatch.ElapsedMilliseconds, mcWatch.ElapsedMilliseconds, "empty mesh"));
                    continue;
                }

                // In curve mode, orient cleanup faces from the sampled scalar grid (trilinear)
                // instead of re-evaluating the curve-offset wrapper (which calls Rhino
                // Curve.ClosestPoint per face). In uniform mode the wrapper is cheap, so keep it.
                WasperField orientField = (curveMode && meshCurveQuery != null)
                    ? new WasperField(
                        p => GridTrilinear(scalars, nx, ny, nz, sampleBox, p),
                        domain,
                        source != null && !string.IsNullOrEmpty(source.Label) ? source.Label : "field",
                        "Source: Fi3d07 cleanup grid orientation field",
                        WasperFieldSdfQuality.ApproximateSdf)
                    : result;

                CleanResultMesh(mesh, orientField, Math.Max(res * 0.75, tol * 10.0));
                outMeshes.Add(mesh);
                totalFaces += mesh.Faces.Count;
                infoLines.Add(BuildFieldInfo(i, source, domain, nx, ny, nz, samples, sampleWatch.ElapsedMilliseconds, mcWatch.ElapsedMilliseconds, $"{mesh.Vertices.Count:N0} vertices / {mesh.Faces.Count:N0} faces"));
            }

            totalWatch.Stop();

            DA.SetDataList(0, outFields);
            DA.SetDataList(1, outMeshes);
            DA.SetData(2, BuildInfo(uniformOffset, offsetValues.Count, curveMode, radius, falloff, meshCurveQuery?.SegmentCount ?? 0, boundField != null, res, makeMesh, sources.Count, outFields.Count, outMeshes.Count, totalSamples, totalFaces, totalWatch.ElapsedMilliseconds, infoLines));

            Message = makeMesh
                ? $"{_versionTag} | {outMeshes.Count} mesh | {totalFaces:N0} f"
                : $"{_versionTag} | field";
        }

        private static BoundingBox BuildOutputDomain(
            WasperField source,
            WasperField boundField,
            double offset,
            double res)
        {
            double pad = Math.Abs(offset) + Math.Max(res, 1e-6);
            return WasperFieldOps.BuildDomain(source, boundField, pad);
        }

        private static WasperField ExtractField(IGH_Goo goo)
        {
            if (goo == null) return null;

            if (goo is WasperFieldGoo fg) return fg.Value;

            object sv = null;
            try { sv = goo.ScriptVariable(); } catch { sv = null; }

            if (sv is WasperField f) return f;
            if (sv is WasperFieldGoo fgoo) return fgoo.Value;

            var wrapper = goo as GH_ObjectWrapper;
            if (wrapper != null)
            {
                if (wrapper.Value is WasperField wf) return wf;
                if (wrapper.Value is WasperFieldGoo wg) return wg.Value;
            }

            return null;
        }

        private static Box BoxFromBoundingBox(BoundingBox bb)
        {
            return new Box(
                Plane.WorldXY,
                new Interval(bb.Min.X, bb.Max.X),
                new Interval(bb.Min.Y, bb.Max.Y),
                new Interval(bb.Min.Z, bb.Max.Z));
        }

        private static void BuildGridCounts(Box box, double res, out int nx, out int ny, out int nz)
        {
            double sx = Math.Abs(box.X.Length);
            double sy = Math.Abs(box.Y.Length);
            double sz = Math.Abs(box.Z.Length);

            nx = Math.Max(2, (int)Math.Ceiling(sx / res) + 1);
            ny = Math.Max(2, (int)Math.Ceiling(sy / res) + 1);
            nz = Math.Max(2, (int)Math.Ceiling(sz / res) + 1);
        }

        private static void SampleField(
            WasperField field,
            Box box,
            int nx,
            int ny,
            int nz,
            double[] scalars,
            Point3d[] points)
        {
            Parallel.For(0, nz, iz =>
            {
                double w = nz <= 1 ? 0.0 : (double)iz / (nz - 1);
                for (int iy = 0; iy < ny; iy++)
                {
                    double v = ny <= 1 ? 0.0 : (double)iy / (ny - 1);
                    for (int ix = 0; ix < nx; ix++)
                    {
                        double u = nx <= 1 ? 0.0 : (double)ix / (nx - 1);
                        int idx = Index(ix, iy, iz, nx, ny);
                        Point3d p = box.PointAt(u, v, w);
                        points[idx] = p;
                        scalars[idx] = WasperFieldOps.SafeEvaluate(field, p);
                    }
                }
            });
        }

        private static void CleanResultMesh(Mesh mesh, WasperField field, double gradientStep)
        {
            if (mesh == null || mesh.Faces.Count == 0) return;

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();

            WasperFieldNormalTools.OrientFacesByFieldGradient(mesh, field, gradientStep);
            mesh.UnifyNormals();
            WasperFieldNormalTools.OrientFacesByFieldGradient(mesh, field, gradientStep);

            mesh.Weld(Math.PI);
            mesh.Normals.ComputeNormals();
            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Compact();
        }

        private static string BuildInfo(
            double offset,
            int offsetCount,
            bool curveMode,
            double radius,
            int falloff,
            int curveSegments,
            bool hasBoundField,
            double res,
            bool makeMesh,
            int sourceCount,
            int fieldCount,
            int meshCount,
            long totalSamples,
            int totalFaces,
            long elapsedMs,
            List<string> lines)
        {
            string falloffName = falloff == 0 ? "linear" : falloff == 2 ? "gaussian" : "smooth";
            return
                "Offset Field\n" +
                $"version         : {(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown")}\n" +
                $"sources         : {sourceCount}\n" +
                $"fields_out      : {fieldCount}\n" +
                $"meshes_out      : {meshCount}\n" +
                $"mode            : {(curveMode ? "curve-local" : "uniform")}\n" +
                $"offset          : {offset:F4}{(curveMode && offsetCount > 1 ? $" (+{offsetCount - 1} along curve)" : "")}\n" +
                (curveMode
                    ? $"radius          : {radius:F4}\n" +
                      $"falloff         : {falloffName}\n" +
                      $"curve_segments  : {(curveSegments > 0 ? curveSegments.ToString("N0") : "n/a")}\n"
                    : "") +
                $"bound_field     : {hasBoundField}\n" +
                $"resolution      : {res:F4}\n" +
                $"mesh?           : {makeMesh}\n" +
                $"total_samples   : {totalSamples:N0}\n" +
                $"total_faces     : {totalFaces:N0}\n" +
                $"elapsed_ms      : {elapsedMs}\n" +
                string.Join("\n", lines);
        }

        private static string BuildFieldInfo(
            int index,
            WasperField source,
            BoundingBox domain,
            int nx,
            int ny,
            int nz,
            long samples,
            long sampleMs,
            long mcMs,
            string result)
        {
            string label = source == null || string.IsNullOrEmpty(source.Label) ? "field_" + index : source.Label;
            string domainText = domain.IsValid
                ? $"{domain.Min.X:F3},{domain.Min.Y:F3},{domain.Min.Z:F3} -> {domain.Max.X:F3},{domain.Max.Y:F3},{domain.Max.Z:F3}"
                : "(invalid)";

            return
                $"{label}: domain={domainText}, grid={(nx > 0 ? $"{nx}x{ny}x{nz}" : "(not sampled)")}, " +
                $"samples={samples:N0}, sample_ms={sampleMs}, mc_ms={mcMs}, result={result}";
        }

        private static int Index(int ix, int iy, int iz, int nx, int ny)
        {
            return ix + nx * (iy + ny * iz);
        }

        private static BoundingBox BuildCurveDomain(
            WasperField source,
            WasperField boundField,
            Curve curve,
            double radius,
            double maxAbsOffset,
            double res)
        {
            BoundingBox domain = WasperFieldOps.BuildCurveOffsetDomain(source, boundField, curve, Math.Max(radius, 1e-9), maxAbsOffset);
            if (domain.IsValid)
                domain.Inflate(Math.Max(res, 1e-6));
            return domain;
        }

        // Fast curve-local offset sampling on the RAW field (no gradient normalisation),
        // matching the Offset component's convention. One source/bound evaluation per voxel.
        private static void SampleCurveOffsetField(
            WasperField source,
            WasperField boundField,
            FastCurveQuery curveQuery,
            double[] offsetValues,
            double radius,
            int falloff,
            Box box,
            int nx,
            int ny,
            int nz,
            double[] scalars,
            Point3d[] points)
        {
            double influenceRadius = Math.Max(radius, 1e-9);
            bool hasBound = boundField != null && boundField.Evaluator != null;

            Parallel.For(0, nz, iz =>
            {
                double w = nz <= 1 ? 0.0 : (double)iz / (nz - 1);
                for (int iy = 0; iy < ny; iy++)
                {
                    double v = ny <= 1 ? 0.0 : (double)iy / (ny - 1);
                    for (int ix = 0; ix < nx; ix++)
                    {
                        double u = nx <= 1 ? 0.0 : (double)ix / (nx - 1);
                        int idx = Index(ix, iy, iz, nx, ny);
                        Point3d p = box.PointAt(u, v, w);
                        points[idx] = p;

                        double value = WasperFieldOps.SafeEvaluate(source, p);

                        double distance, curveU;
                        if (curveQuery.TryClosest(p, influenceRadius, out distance, out curveU))
                        {
                            double off = InterpolateValues(offsetValues, curveU);
                            double weight = FalloffWeight(distance, influenceRadius, falloff);
                            value -= off * weight;
                        }

                        if (hasBound)
                            value = Math.Max(value, WasperFieldOps.SafeEvaluate(boundField, p));

                        scalars[idx] = value;
                    }
                }
            });
        }

        // Trilinear interpolation of a scalar grid over an axis-aligned sample box. Used by
        // the curve-mode cleanup orientation field; non-finite corners are treated as a large
        // positive value (outside) so interior face orientation is unaffected.
        private static double GridTrilinear(double[] s, int nx, int ny, int nz, Box box, Point3d p)
        {
            if (s == null || nx < 2 || ny < 2 || nz < 2) return double.PositiveInfinity;

            Vector3d d = p - box.Plane.Origin;
            double lx = d * box.Plane.XAxis;
            double ly = d * box.Plane.YAxis;
            double lz = d * box.Plane.ZAxis;

            double xLen = Math.Abs(box.X.Length); if (xLen < 1e-12) xLen = 1e-12;
            double yLen = Math.Abs(box.Y.Length); if (yLen < 1e-12) yLen = 1e-12;
            double zLen = Math.Abs(box.Z.Length); if (zLen < 1e-12) zLen = 1e-12;

            double u = Clamp01((lx - box.X.T0) / xLen) * (nx - 1);
            double v = Clamp01((ly - box.Y.T0) / yLen) * (ny - 1);
            double w = Clamp01((lz - box.Z.T0) / zLen) * (nz - 1);

            int ix = Math.Min((int)Math.Floor(u), nx - 2); if (ix < 0) ix = 0;
            int iy = Math.Min((int)Math.Floor(v), ny - 2); if (iy < 0) iy = 0;
            int iz = Math.Min((int)Math.Floor(w), nz - 2); if (iz < 0) iz = 0;

            double tx = u - ix;
            double ty = v - iy;
            double tz = w - iz;

            double c000 = GridValue(s, ix,     iy,     iz,     nx, ny);
            double c100 = GridValue(s, ix + 1, iy,     iz,     nx, ny);
            double c010 = GridValue(s, ix,     iy + 1, iz,     nx, ny);
            double c110 = GridValue(s, ix + 1, iy + 1, iz,     nx, ny);
            double c001 = GridValue(s, ix,     iy,     iz + 1, nx, ny);
            double c101 = GridValue(s, ix + 1, iy,     iz + 1, nx, ny);
            double c011 = GridValue(s, ix,     iy + 1, iz + 1, nx, ny);
            double c111 = GridValue(s, ix + 1, iy + 1, iz + 1, nx, ny);

            double c00 = c000 + (c100 - c000) * tx;
            double c01 = c001 + (c101 - c001) * tx;
            double c10 = c010 + (c110 - c010) * tx;
            double c11 = c011 + (c111 - c011) * tx;

            double c0 = c00 + (c10 - c00) * ty;
            double c1 = c01 + (c11 - c01) * ty;

            return c0 + (c1 - c0) * tz;
        }

        private static double GridValue(double[] s, int ix, int iy, int iz, int nx, int ny)
        {
            double value = s[Index(ix, iy, iz, nx, ny)];
            return IsFinite(value) ? value : 1e9;
        }

        private static double InterpolateValues(double[] values, double u)
        {
            if (values == null || values.Length == 0) return 0.0;
            if (values.Length == 1) return values[0];

            double x = Clamp01(u) * (values.Length - 1);
            int i0 = (int)Math.Floor(x);
            if (i0 >= values.Length - 1) return values[values.Length - 1];
            int i1 = i0 + 1;
            double t = x - i0;
            return values[i0] + (values[i1] - values[i0]) * t;
        }

        private static double FalloffWeight(double distance, double radius, int falloff)
        {
            if (radius <= 1e-12) return 0.0;
            double x = Clamp01(1.0 - distance / radius);

            switch (falloff)
            {
                case 0:
                    return x;
                case 2:
                    {
                        double sigma = radius / 3.0;
                        double g = Math.Exp(-0.5 * distance * distance / (sigma * sigma));
                        return distance <= radius ? g : 0.0;
                    }
                case 1:
                default:
                    return x * x * (3.0 - 2.0 * x);
            }
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        private static bool IsFinite(double value)
        {
            return !(double.IsNaN(value) || double.IsInfinity(value));
        }

        // Polyline approximation of the guide curve for fast, thread-safe closest-point and
        // normalized-length queries during sampling (avoids per-voxel Rhino Curve.ClosestPoint).
        private sealed class FastCurveQuery
        {
            private readonly Point3d[] _points;
            private readonly double[] _lengths;
            private readonly double _totalLength;

            private FastCurveQuery(Point3d[] points, double[] lengths, double totalLength)
            {
                _points = points;
                _lengths = lengths;
                _totalLength = Math.Max(totalLength, 1e-9);
            }

            public int SegmentCount => Math.Max(0, _points.Length - 1);

            public static FastCurveQuery Create(Curve curve, double res, double radius, double tol)
            {
                if (curve == null || !curve.IsValid) return null;

                double length = Math.Max(curve.GetLength(), tol);
                double target = Math.Max(Math.Min(radius * 0.25, res * 2.0), tol * 10.0);
                int count = Math.Max(8, Math.Min(4096, (int)Math.Ceiling(length / Math.Max(target, tol))));

                double[] parameters = curve.DivideByCount(count, true);
                var points = new List<Point3d>();
                if (parameters != null && parameters.Length >= 2)
                {
                    for (int i = 0; i < parameters.Length; i++)
                        points.Add(curve.PointAt(parameters[i]));
                }
                else
                {
                    Interval domain = curve.Domain;
                    points.Add(curve.PointAt(domain.Min));
                    points.Add(curve.PointAt(domain.Max));
                }

                var lengths = new double[points.Count];
                double total = 0.0;
                for (int i = 1; i < points.Count; i++)
                {
                    total += points[i - 1].DistanceTo(points[i]);
                    lengths[i] = total;
                }

                return new FastCurveQuery(points.ToArray(), lengths, total);
            }

            public bool TryClosest(Point3d point, double radius, out double distance, out double u)
            {
                double radiusSq = radius * radius;
                double bestSq = double.PositiveInfinity;
                double bestU = 0.0;

                for (int i = 0; i < _points.Length - 1; i++)
                {
                    Point3d a = _points[i];
                    Point3d b = _points[i + 1];
                    Vector3d ab = b - a;
                    double lenSq = ab.SquareLength;
                    if (lenSq <= 1e-18) continue;

                    Vector3d ap = point - a;
                    double t = Math.Max(0.0, Math.Min(1.0, (ap * ab) / lenSq));
                    Point3d q = a + ab * t;
                    double dSq = point.DistanceToSquared(q);
                    if (dSq < bestSq)
                    {
                        bestSq = dSq;
                        double segmentLength = _points[i].DistanceTo(_points[i + 1]);
                        bestU = (_lengths[i] + segmentLength * t) / _totalLength;
                    }
                }

                if (bestSq <= radiusSq)
                {
                    distance = Math.Sqrt(bestSq);
                    u = Math.Max(0.0, Math.Min(1.0, bestU));
                    return true;
                }

                distance = double.PositiveInfinity;
                u = 0.0;
                return false;
            }
        }
    }
}
