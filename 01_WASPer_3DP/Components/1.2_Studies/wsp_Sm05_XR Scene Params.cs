using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

namespace WASPer_3DP.Components._1_2_Studies
{
    /// <summary>
    /// Bundles extra XR scene data -- context geometry, materials, and an optional
    /// externally-driven simulation parameter -- that Sm01 can pick up through its "xr_pack"
    /// input. Added 2026-08-19 so Sm01 can build a complete XR export/live-push package on its
    /// own, without a Gc07 component wired in just to carry these extras.
    /// </summary>
    public sealed class wsp_Sm05_XR_Scene_Params : GH_Component
    {
        private readonly string _version;
        private static Bitmap _icon;
        private string _cachedContextSignature = string.Empty;
        private List<GeometryBase> _cachedGeometryReferences = new List<GeometryBase>();
        private List<IGH_Goo> _cachedMaterialReferences = new List<IGH_Goo>();
        private List<WasperXrContextMeshData> _cachedContextMeshes =
            new List<WasperXrContextMeshData>();
        private List<string> _cachedMeshWarnings = new List<string>();
        private readonly WasperXrScenePack _cachedPack = new WasperXrScenePack();

        public wsp_Sm05_XR_Scene_Params()
            : base(
                "wsp_Sm05_XR Scene Params",
                "XR Scene Params",
                "Bundles context geometry, materials, and an optional simulation parameter for " +
                "Sm01's xr_pack input. Connect Gc05 WASPer Simulation's sim_par (or any " +
                "normalized 0-to-1 value) to have Sm01 treat the print position as externally " +
                "driven and disable the web viewer's own playback controls.",
                WASPerPalette.Performance,
                "1.2_Studies")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _version = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid =>
            new Guid("9AF17A9A-4E92-4F92-8907-995DE7CE3766");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon => _icon ??= CreateIcon();

        protected override void RegisterInputParams(GH_InputParamManager parameters)
        {
            parameters.AddGeometryParameter(
                "context_geo",
                "geo",
                "Optional context geometry to include in the exported XR scene (a build plate, " +
                "surrounding structure, reference objects) -- not part of the printed wsp_path " +
                "itself. Meshes pass through; Breps, surfaces, and extrusions are converted to " +
                "display meshes for the browser viewer.",
                GH_ParamAccess.list);
            parameters.AddGenericParameter(
                "materials",
                "mats",
                "Optional display colors/materials for context geometry. One item broadcasts to " +
                "all geometry; otherwise items are matched by list index. Grasshopper colors and " +
                "Rhino material diffuse color/transparency are supported. When omitted, mesh " +
                "vertex colors are detected and averaged as the object's display color.",
                GH_ParamAccess.list);
            parameters.AddNumberParameter(
                "sim_par",
                "sim_par",
                "Optional normalized simulation parameter, 0 to 1 -- typically Gc05 WASPer " +
                "Simulation's own sim_par. When connected, Sm01 treats the print position as " +
                "externally driven and disables the web viewer's Play/Stop/time-slider " +
                "controls rather than running a second, conflicting clock. For low-latency live " +
                "updates, connect Sm01 to the full stable wsp_path and send only sim_par here; " +
                "do not feed Sm01 the changing partial path from Gc05. Unconnected changes " +
                "nothing about Sm01's existing behavior.",
                GH_ParamAccess.item,
                1.0);
            for (int index = 0; index < parameters.ParamCount; index++)
                parameters[index].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager parameters)
        {
            parameters.AddGenericParameter(
                "xr_pack",
                "xr_pack",
                "Bundled context geometry, materials, and sim_par connection state. Connect to " +
                "Sm01's xr_pack input.",
                GH_ParamAccess.item);
            parameters.AddTextParameter(
                "summary",
                "summary",
                "Geometry/material counts and sim_par connection state.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess dataAccess)
        {
            var contextGeometry = new List<GeometryBase>();
            dataAccess.GetDataList(0, contextGeometry);
            List<GeometryBase> validContextGeometry = contextGeometry
                .Where(item => item != null && item.IsValid)
                .ToList();

            var materialGoos = new List<IGH_Goo>();
            dataAccess.GetDataList(1, materialGoos);
            List<IGH_Goo> materials = materialGoos
                .Where(goo => goo != null)
                .ToList();

            double simulationParameter = 1.0;
            dataAccess.GetData(2, ref simulationParameter);
            double clampedSimulationParameter = Math.Max(0.0, Math.Min(1.0, simulationParameter));
            if (Math.Abs(clampedSimulationParameter - simulationParameter) > 1e-12)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "sim_par was clamped to the 0-to-1 interval.");
            }

            // SourceCount, not the value, is what tells Sm01 whether an external source owns
            // playback -- an unconnected sim_par still reads back as its 1.0 default, which
            // must NOT be mistaken for "connected at full simulation."
            bool simulationParameterConnected = Params.Input[2].SourceCount > 0;
            bool sameInputObjects = SameReferences(
                validContextGeometry,
                _cachedGeometryReferences) &&
                SameReferences(materials, _cachedMaterialReferences);
            if (!sameInputObjects)
            {
                string contextSignature = WasperXrContextMeshBuilder.ComputeSignature(
                    validContextGeometry,
                    materials);
                if (!string.Equals(
                        contextSignature,
                        _cachedContextSignature,
                        StringComparison.Ordinal))
                {
                    _cachedContextMeshes = WasperXrContextMeshBuilder.Build(
                        validContextGeometry,
                        materials,
                        out _cachedMeshWarnings);
                    _cachedContextSignature = contextSignature;
                }

                _cachedGeometryReferences = validContextGeometry.ToList();
                _cachedMaterialReferences = materials.ToList();
            }

            List<WasperXrContextMeshData> contextMeshes = _cachedContextMeshes;
            List<string> meshWarnings = _cachedMeshWarnings;
            foreach (string warning in meshWarnings)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);

            if (materials.Count > 1 && materials.Count != validContextGeometry.Count)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Received {materials.Count} materials for {validContextGeometry.Count} context objects. " +
                    "Materials are matched by index and the last material is reused when needed.");
            }

            // Keep one pack identity while only sim_par changes. Sm01 can then recognize that
            // the heavy scene payload is unchanged and send only the lightweight playback
            // message instead of rebuilding the full XR package.
            _cachedPack.ContextGeometry = validContextGeometry;
            _cachedPack.Materials = materials;
            _cachedPack.ContextMeshes = contextMeshes;
            _cachedPack.SimulationParameterConnected = simulationParameterConnected;
            _cachedPack.SimulationParameter = clampedSimulationParameter;

            dataAccess.SetData(0, new WasperXrScenePackGoo(_cachedPack));
            dataAccess.SetData(1, BuildSummary(
                validContextGeometry.Count,
                materials.Count,
                contextMeshes.Count,
                simulationParameterConnected,
                clampedSimulationParameter));
            Message = _version;
        }

        private static bool SameReferences<T>(
            IReadOnlyList<T> current,
            IReadOnlyList<T> cached)
            where T : class
        {
            if (current == null || cached == null || current.Count != cached.Count)
                return false;
            for (int index = 0; index < current.Count; index++)
            {
                if (!ReferenceEquals(current[index], cached[index]))
                    return false;
            }
            return true;
        }

        private static string BuildSummary(
            int geometryCount,
            int materialCount,
            int meshCount,
            bool simulationParameterConnected,
            double simulationParameter)
        {
            string simText = simulationParameterConnected
                ? $"sim_par {simulationParameter:0.00} (external -- web playback will be disabled)"
                : "sim_par not connected (Sm01 keeps its own playback)";
            return $"{geometryCount} context geometry object(s) -> {meshCount} display mesh(es), " +
                $"{materialCount} material(s), {simText}.";
        }

        private static Bitmap CreateIcon()
        {
            var bitmap = new Bitmap(24, 24);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            // An isometric "package" cube (context geo + materials bundled together) with a
            // small play-triangle badge in the corner (sim_par), same visual language as the
            // rest of the Sm-family icons (rounded shapes, a warm accent color).
            using var faceBrush = new SolidBrush(Color.FromArgb(255, 236, 201));
            using var sideBrush = new SolidBrush(Color.FromArgb(242, 200, 140));
            using var darkPen = new Pen(Color.FromArgb(55, 55, 55), 1.3f);
            using var badgeBrush = new SolidBrush(Color.FromArgb(242, 166, 44));
            using var whiteBrush = new SolidBrush(Color.White);

            var top = new PointF(12f, 3.5f);
            var left = new PointF(3.5f, 8f);
            var right = new PointF(20.5f, 8f);
            var bottom = new PointF(12f, 12.5f);
            var frontLeft = new PointF(3.5f, 15f);
            var frontRight = new PointF(20.5f, 15f);
            var frontBottom = new PointF(12f, 19.5f);

            graphics.FillPolygon(faceBrush, new[] { top, right, bottom, left });
            graphics.FillPolygon(sideBrush, new[] { left, bottom, frontBottom, frontLeft });
            graphics.FillPolygon(faceBrush, new[] { right, bottom, frontBottom, frontRight });
            graphics.DrawPolygon(darkPen, new[] { top, right, bottom, left });
            graphics.DrawPolygon(darkPen, new[] { left, bottom, frontBottom, frontLeft });
            graphics.DrawPolygon(darkPen, new[] { right, bottom, frontBottom, frontRight });
            graphics.DrawLine(darkPen, bottom.X, bottom.Y, bottom.X, bottom.Y + 7f);

            graphics.FillEllipse(badgeBrush, 13.5f, 12.5f, 9f, 9f);
            var playTriangle = new[]
            {
                new PointF(16.6f, 15.3f),
                new PointF(16.6f, 19.7f),
                new PointF(20.2f, 17.5f)
            };
            graphics.FillPolygon(whiteBrush, playTriangle);

            return bitmap;
        }
    }
}
