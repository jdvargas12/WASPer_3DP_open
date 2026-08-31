// wsp_Fi3d15_WASPer to Dendro Field.cs
// WASPer_3DP - Subcategory: 2.3_Fields_3D

using System;
using System.Diagnostics;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_3_Fields_3D
{
    public class wsp_Fi3d15_WasperToDendroField : GH_Component
    {
        private const string Cat = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string Subcategory = "2.3_Fields_3D";
        private const long MaxSamples = 20000000;
        private const double DefaultVoxelSize = 2.0;
        private readonly string _versionTag;

        // Phase 2 (single-result cache): holds the last successfully computed Dendro volume so that
        // repeat solves with unchanged inputs (cache hit) or run=false (held) never re-mesh/re-voxelize.
        private CacheKey _lastKey;
        private object _cachedOutputVolume;
        private string _cachedVolumeType;
        private string _cachedInfoBase;
        private bool _hasCache;

        public wsp_Fi3d15_WasperToDendroField()
            : base(
                "wsp_Fi3d15_WASPer to Dendro Field",
                "WASPer -> Dendro",
                "Converts a WASPer 3D field into a Dendro (OpenVDB) volume. " +
                "The field is meshed (marching cubes) on the supplied domain - or, when no domain is connected, on the " +
                "field's own bounding box inflated by a margin - at the resolution given by the connected Dendro " +
                "Settings' VoxelSize (2.0 when unconnected); the resulting mesh is then voxelized into a real Dendro " +
                "level-set volume using those same settings. Dendro must be installed and loaded in the current " +
                "Grasshopper session. Set run to true to compute; while run is false the last computed volume (if any) " +
                "is held and inputs can be changed freely without recomputing.",
                Cat,
                Subcategory)
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("5E003CE6-D6BF-4B23-BB00-055F8C35F904");

        // Experimental bridge retained for existing definitions, but hidden from the public toolbar
        // while its necessity and conversion contract are reconsidered.
        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override System.Drawing.Bitmap Icon
        {
            get 
            {
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d15_WASPer to Dendro Field.png"))
                    using (var bitmap = stream != null ? new System.Drawing.Bitmap(stream) : null)
                        return bitmap != null ? new System.Drawing.Bitmap(bitmap) : null;
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "wasper_field",
                "field",
                "WASPer 3D field to convert into a Dendro volume.",
                GH_ParamAccess.item);

            pManager.AddBoxParameter(
                "domain",
                "domain",
                "Finite sampling domain used to mesh the field before voxelizing it into a Dendro volume. " +
                "When unconnected, defaults to the field's own Domain bounding box, inflated by a margin so the " +
                "iso-surface is not clipped at the boundary.",
                GH_ParamAccess.item);
            pManager[1].Optional = true;

            pManager.AddGenericParameter(
                "settings",
                "set",
                "Optional Dendro Settings controlling the resulting volume. VoxelSize also drives the marching-cubes " +
                "sampling resolution used to mesh the field, so there is a single resolution knob shared by both steps. " +
                "When unconnected, WASPer creates Dendro settings with VoxelSize 2.0.",
                GH_ParamAccess.item);
            pManager[2].Optional = true;

            pManager.AddTextParameter(
                "label",
                "label",
                "Optional label, used for diagnostics only.",
                GH_ParamAccess.item,
                "WASPer field");
            pManager[3].Optional = true;

            pManager.AddBooleanParameter(
                "run",
                "run",
                "Set to true to (re)compute the Dendro volume. While false, the last computed volume (if any) is held " +
                "unchanged so upstream inputs can be edited without triggering expensive recomputation.",
                GH_ParamAccess.item,
                false);
            pManager[4].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "dendro_volume",
                "dendro",
                "Dendro Volume voxelized from the WASPer field's iso-surface mesh.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "volume_type",
                "type",
                "Created Dendro volume runtime type.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "info",
                "info",
                "Bridge and meshing diagnostics.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            IGH_Goo fieldGooRaw = null;
            Box domain = Box.Unset;
            IGH_Goo settingsGoo = null;
            string label = "WASPer field";
            bool run = false;

            if (!DA.GetData(0, ref fieldGooRaw) || fieldGooRaw == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Connect a WASPer 3D field.");
                return;
            }

            DA.GetData(1, ref domain);
            DA.GetData(2, ref settingsGoo);
            DA.GetData(3, ref label);
            DA.GetData(4, ref run);
            if (string.IsNullOrWhiteSpace(label))
                label = "WASPer field";

            WasperField field = ExtractField(fieldGooRaw);
            if (field?.Evaluator == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a valid WASPer 3D field.");
                return;
            }

            // Review finding R3: the idle/held gate must be checked before ANY Dendro-dependent work -
            // including settings resolution, which requires Dendro to be loaded - so that run=false keeps
            // this component safely inert even when Dendro isn't loaded or its settings aren't wired up.
            if (!run)
            {
                if (_hasCache)
                {
                    DA.SetData(0, _cachedOutputVolume);
                    DA.SetData(1, _cachedVolumeType);
                    DA.SetData(2, _cachedInfoBase + $"\ncache           : held - run is false");
                    Message = _versionTag + " | held";
                }
                else
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        "run is false - set run to true to compute the Dendro volume.");
                    Message = _versionTag + " | idle";
                }
                return;
            }

            double tolerance = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

            var totalWatch = Stopwatch.StartNew();

            var settingsWatch = Stopwatch.StartNew();
            bool settingsConnected = WasperDendroBridge.Unwrap(settingsGoo) != null;
            if (!WasperDendroBridge.TryGetOrCreateSettings(settingsGoo, out object settings, out string settingsError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, settingsError);
                return;
            }

            if (!settingsConnected &&
                !WasperDendroBridge.TrySetDouble(settings, "VoxelSize", DefaultVoxelSize, out string defaultSettingsError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, defaultSettingsError);
                return;
            }
            settingsWatch.Stop();

            double resolution = WasperDendroBridge.GetDouble(settings, "VoxelSize", DefaultVoxelSize);
            resolution = Math.Max(resolution, tolerance * 10.0);
            double bandwidth = WasperDendroBridge.GetDouble(settings, "Bandwidth", 1.0);
            double isoValue = WasperDendroBridge.GetDouble(settings, "IsoValue", 0.01);
            double adaptivity = WasperDendroBridge.GetDouble(settings, "Adaptivity", 0.1);

            string domainSource;
            double domainMargin = 0.0;
            bool domainConnected = domain.IsValid;

            if (!domainConnected)
            {
                BoundingBox fieldBox = field.Domain;
                if (!fieldBox.IsValid)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        "No domain connected, and the field has no valid bounding box of its own. Connect a domain box.");
                    return;
                }

                // Narrow-band-aware margin: Dendro's level set keeps a band of `bandwidth` voxels either
                // side of the surface, so the sampling domain needs at least that much room beyond the
                // field's own extents or the iso-surface can be clipped at the domain boundary.
                double narrowBandMargin = resolution * Math.Max(2.0, bandwidth + 1.0);
                domainMargin = Math.Max(narrowBandMargin, tolerance * 10.0);
                fieldBox.Inflate(domainMargin);
                domain = new Box(fieldBox);
                domainSource = "field bounding box (auto)";

                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"No domain connected - defaulted to the field's own bounding box, inflated by {domainMargin:F3}.");
            }
            else
            {
                domainSource = "connected";
            }

            var key = new CacheKey
            {
                Field = field,
                Origin = domain.Plane.Origin,
                XAxis = domain.Plane.XAxis,
                YAxis = domain.Plane.YAxis,
                ZAxis = domain.Plane.ZAxis,
                Xi = domain.X,
                Yi = domain.Y,
                Zi = domain.Z,
                VoxelSize = resolution,
                Bandwidth = bandwidth,
                IsoValue = isoValue,
                Adaptivity = adaptivity,
                Tolerance = tolerance
            };

            bool sameAsLastKey = _hasCache && _lastKey != null && _lastKey.Matches(key);

            if (sameAsLastKey)
            {
                DA.SetData(0, _cachedOutputVolume);
                DA.SetData(1, _cachedVolumeType);
                DA.SetData(2, _cachedInfoBase + "\ncache           : hit");
                Message = _versionTag + " | cached";
                return;
            }

            string cacheReason = DescribeCacheMiss(_hasCache ? _lastKey : null, key);

            var meshOptions = new WasperUniformFieldMeshOptions
            {
                Resolution = resolution,
                IsoLevel = 0.0,
                KeyTolerance = Math.Max(tolerance * 0.25, 1e-7),
                SlabDepth = 24,
                HardMaxSamples = MaxSamples,
                SealDomainBoundary = true
            };

            Mesh mesh;
            WasperUniformFieldMeshStats meshStats;
            try
            {
                if (!WasperUniformFieldMesher.TryExtract(field, domain, meshOptions, out mesh, out meshStats, out string meshError))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, meshError);
                    return;
                }
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "WASPer field sampling failed: " + WasperUniformFieldMesher.InnermostMessage(ex));
                return;
            }

            if (mesh == null || mesh.Faces.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "No zero iso-surface was found. Check that the field crosses zero inside the domain.");
                return;
            }

            if (!mesh.IsValid)
            {
                mesh.IsValidWithLog(out string validationLog);
                string detail = string.IsNullOrWhiteSpace(validationLog)
                    ? "Rhino did not provide additional validation details."
                    : validationLog.Trim();
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Extracted mesh is not valid - cannot voxelize into Dendro. " + detail);
                return;
            }

            // Review finding R4: an open mesh cannot be safely voxelized into a valid Dendro level-set
            // volume, so this is a hard rejection (not a warning-and-continue) per the plan's contract.
            bool meshClosed = mesh.IsClosed;
            if (!meshClosed)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Extracted mesh is not closed - cannot voxelize into a valid Dendro volume. Try a finer " +
                    "resolution (smaller VoxelSize) or a larger domain margin.");
                return;
            }

            var voxelizeWatch = Stopwatch.StartNew();
            if (!WasperDendroBridge.TryCreateVolume(mesh, settings, out object volume, out string volumeType, out string volumeError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, volumeError);
                return;
            }
            voxelizeWatch.Stop();

            var wrapWatch = Stopwatch.StartNew();
            object outputVolume = WasperDendroBridge.WrapAsGoo(volume);
            wrapWatch.Stop();

            totalWatch.Stop();

            long fullGridSamples = meshStats.Samples;
            double fullGridMiB = WasperUniformFieldMesher.EstimateGridManagedBytes(fullGridSamples) / (1024.0 * 1024.0);
            double peakSlabMiB = meshStats.EstimatedPeakManagedBytes / (1024.0 * 1024.0);

            string infoBase =
                "WASPer to Dendro Field\n" +
                $"label           : {label}\n" +
                $"volume type     : {volumeType}\n" +
                $"domain_source   : {domainSource}\n" +
                $"domain_margin   : {domainMargin:F4}\n" +
                "domain_sealed   : True (finite clipping boundary)\n" +
                $"voxel_size      : {resolution:F4}  (Dendro settings.VoxelSize)\n" +
                $"bandwidth       : {bandwidth:F4}\n" +
                $"grid            : {meshStats.Nx}x{meshStats.Ny}x{meshStats.Nz}\n" +
                $"samples         : {fullGridSamples:N0}\n" +
                $"estimated_grid  : {fullGridMiB:F1} MiB (full) / {peakSlabMiB:F1} MiB (peak, {meshStats.SlabCount} slabs of {meshStats.SlabDepthUsed})\n" +
                $"mesh v/f        : {mesh.Vertices.Count:N0} / {mesh.Faces.Count:N0}\n" +
                $"mesh closed     : {meshClosed}\n" +
                $"settings_ms     : {settingsWatch.ElapsedMilliseconds}\n" +
                $"sampling_ms     : {meshStats.SampleMs}\n" +
                $"marching_cubes_ms: {meshStats.MeshMs}\n" +
                $"dendro_voxelize_ms: {voxelizeWatch.ElapsedMilliseconds}\n" +
                $"wrap_ms         : {wrapWatch.ElapsedMilliseconds}\n" +
                $"elapsed_ms      : {totalWatch.ElapsedMilliseconds}";

            _lastKey = key;
            _cachedOutputVolume = outputVolume;
            _cachedVolumeType = volumeType;
            _cachedInfoBase = infoBase;
            _hasCache = true;

            DA.SetData(0, outputVolume);
            DA.SetData(1, volumeType);
            DA.SetData(2, infoBase + $"\ncache           : {cacheReason}");
            Message = _versionTag + " | linked";
        }

        private static WasperField ExtractField(IGH_Goo goo)
        {
            object value = WasperDendroBridge.Unwrap(goo);

            if (value is WasperField field)
                return field;

            if (value is WasperFieldGoo fieldGoo)
                return fieldGoo.Value;

            return null;
        }

        private static string DescribeCacheMiss(CacheKey oldKey, CacheKey newKey)
        {
            if (oldKey == null) return "miss - first run";
            if (!ReferenceEquals(oldKey.Field, newKey.Field)) return "miss - field changed";

            if (oldKey.Origin != newKey.Origin || oldKey.XAxis != newKey.XAxis ||
                oldKey.YAxis != newKey.YAxis || oldKey.ZAxis != newKey.ZAxis ||
                !oldKey.Xi.Equals(newKey.Xi) || !oldKey.Yi.Equals(newKey.Yi) || !oldKey.Zi.Equals(newKey.Zi))
                return "miss - domain changed";

            if (oldKey.VoxelSize != newKey.VoxelSize || oldKey.Bandwidth != newKey.Bandwidth ||
                oldKey.IsoValue != newKey.IsoValue || oldKey.Adaptivity != newKey.Adaptivity)
                return "miss - settings changed";

            if (oldKey.Tolerance != newKey.Tolerance) return "miss - tolerance changed";

            return "miss - inputs changed";
        }

        /// <summary>
        /// Identifies everything that determines this component's output: the field by reference
        /// (not value - two structurally-identical WasperField instances still get re-voxelized,
        /// since field equality can't be checked cheaply), the domain box geometry, the Dendro
        /// settings values that actually influence the result, and model tolerance.
        /// </summary>
        private sealed class CacheKey
        {
            public WasperField Field;
            public Point3d Origin;
            public Vector3d XAxis;
            public Vector3d YAxis;
            public Vector3d ZAxis;
            public Interval Xi;
            public Interval Yi;
            public Interval Zi;
            public double VoxelSize;
            public double Bandwidth;
            public double IsoValue;
            public double Adaptivity;
            public double Tolerance;

            public bool Matches(CacheKey other)
            {
                if (other == null) return false;
                return ReferenceEquals(Field, other.Field)
                    && Origin == other.Origin
                    && XAxis == other.XAxis
                    && YAxis == other.YAxis
                    && ZAxis == other.ZAxis
                    && Xi.Equals(other.Xi)
                    && Yi.Equals(other.Yi)
                    && Zi.Equals(other.Zi)
                    && VoxelSize == other.VoxelSize
                    && Bandwidth == other.Bandwidth
                    && IsoValue == other.IsoValue
                    && Adaptivity == other.Adaptivity
                    && Tolerance == other.Tolerance;
            }
        }
    }
}
