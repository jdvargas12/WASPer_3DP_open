// wsp_Fi3d14_Dendro to WASPer Field.cs
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
    public class wsp_Fi3d14_DendroToWasperField : GH_Component
    {
        private const string Cat = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string Subcategory = "2.3_Fields_3D";
        private readonly string _versionTag;

        // Phase 4 (single-result cache), reworked into two tiers per review finding R5:
        //
        // - "Display" tier: gates the expensive work (TryRemeshDisplay / TryGetExistingDisplayMesh +
        //   DuplicateMesh). Keyed on the volume reference, settings connectivity, settings values (or
        //   the existing Display mesh's own reference when unconnected), and tolerance. Label is
        //   deliberately NOT part of this key.
        // - "Field-wrap" tier: sits on top of a display-tier hit and additionally tracks Label, so a
        //   label-only change re-runs only WasperField.FromMesh on the already-resolved stable mesh
        //   instead of either (a) returning a stale cached field with the old label (the R5 bug) or
        //   (b) redoing the entire display resolution just to relabel the output.
        private DisplayCacheKey _lastDisplayKey;
        private Mesh _cachedStableMesh;
        private bool _cachedMeshClosed;
        private string _cachedDisplaySource;
        private long _cachedDisplayMs;
        private bool _hasDisplayCache;

        private string _lastLabel;
        private WasperFieldGoo _cachedFieldGoo;
        private long _cachedFieldWrapMs;
        private bool _hasFieldCache;

        public wsp_Fi3d14_DendroToWasperField()
            : base(
                "wsp_Fi3d14_Dendro to WASPer Field",
                "Dendro -> WASPer",
                "Wraps a Dendro (OpenVDB) volume as a WASPer 3D signed distance field. " +
                "Dendro has no live per-point evaluator, so the volume's own iso-surface mesh (Display) is extracted and used as the field's SDF source. " +
                "When no settings are connected, the volume's already-meshed Display is reused as-is (no remeshing); when settings are connected, the volume " +
                "is remeshed with them every time, since that is an explicit request for that exact isovalue/adaptivity/voxel size/bandwidth. " +
                "Dendro must be installed and loaded in the current Grasshopper session.",
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
            new Guid("642511DD-D498-4C61-8B2F-37394E1623DB");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d14_Dendro to WASPer Field.png"))
                    using (var bitmap = stream != null ? new System.Drawing.Bitmap(stream) : null)
                        return bitmap != null ? new System.Drawing.Bitmap(bitmap) : null;
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "dendro_volume",
                "dendro",
                "Dendro Volume (DendroGH.DendroVolume) to wrap as a WASPer field.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "settings",
                "set",
                "Optional Dendro Settings controlling the quality of the meshed iso-surface used to build the field " +
                "(isovalue, adaptivity, voxel size, bandwidth). When connected, the volume is remeshed with these " +
                "settings every solve. When unconnected, the volume's own already-meshed Display is reused as-is. " +
                "Only when that Display is missing does WASPer generate one using VoxelSize 2.0.",
                GH_ParamAccess.item);
            pManager[1].Optional = true;

            pManager.AddTextParameter(
                "label",
                "label",
                "Optional WASPer field label.",
                GH_ParamAccess.item,
                "Dendro volume");
            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "mesh",
                "mesh",
                "Dendro's own iso-surface mesh (Display), used as the WASPer field's SDF source.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "wasper_field",
                "field",
                "WASPer 3D signed distance field, evaluated against the Dendro mesh (closest-point + inside/outside test).",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "source_type",
                "type",
                "Detected Dendro runtime type.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "info",
                "info",
                "Bridge diagnostics.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            IGH_Goo volumeGoo = null;
            IGH_Goo settingsGoo = null;
            string label = "Dendro volume";

            if (!DA.GetData(0, ref volumeGoo) || volumeGoo == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Connect a Dendro Volume.");
                return;
            }

            DA.GetData(1, ref settingsGoo);
            DA.GetData(2, ref label);
            if (string.IsNullOrWhiteSpace(label))
                label = "Dendro volume";

            if (!WasperDendroBridge.TryGetVolume(volumeGoo, out object volume, out string sourceType, out string volumeError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, volumeError);
                return;
            }

            object settingsCandidate = WasperDendroBridge.Unwrap(settingsGoo);
            bool settingsConnected = settingsCandidate != null;

            object settings = null;
            if (settingsConnected)
            {
                if (!WasperDendroBridge.TryGetOrCreateSettings(settingsGoo, volume.GetType().Assembly, out settings, out string settingsError))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, settingsError);
                    return;
                }
            }

            double tolerance = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

            var totalWatch = Stopwatch.StartNew();

            // ---- Display tier: resolve (or reuse) the mesh backing this volume's Display ----
            //
            // Settings-connected: the candidate key is built entirely from cheaply-available settings
            // values and checked BEFORE calling WasperDendroBridge.TryRemeshDisplay (which forces an
            // actual OpenVDB remesh), so an unchanged volume+settings combination never pays for a
            // remesh it doesn't need (review finding R1).
            //
            // Unconnected: reading the existing Display is already cheap (a property read, no remesh),
            // so it happens unconditionally; only the subsequent mesh duplication is skipped on a hit.
            var displayKeyCandidate = new DisplayCacheKey
            {
                VolumeRef = volume,
                SettingsConnected = settingsConnected,
                DisplayMeshRef = null,
                VoxelSize = settingsConnected ? WasperDendroBridge.GetDouble(settings, "VoxelSize", double.NaN) : 0.0,
                IsoValue = settingsConnected ? WasperDendroBridge.GetDouble(settings, "IsoValue", double.NaN) : 0.0,
                Adaptivity = settingsConnected ? WasperDendroBridge.GetDouble(settings, "Adaptivity", double.NaN) : 0.0,
                Bandwidth = settingsConnected ? WasperDendroBridge.GetDouble(settings, "Bandwidth", double.NaN) : 0.0,
                Tolerance = tolerance
            };

            DisplayCacheKey previousDisplayKey = _lastDisplayKey;
            bool hadDisplayCache = _hasDisplayCache;

            bool displayHit;
            Mesh stableMesh;
            bool meshClosed;
            string displaySource;
            long displayMs;

            if (settingsConnected)
            {
                if (hadDisplayCache && previousDisplayKey != null && previousDisplayKey.Matches(displayKeyCandidate))
                {
                    displayHit = true;
                    stableMesh = _cachedStableMesh;
                    meshClosed = _cachedMeshClosed;
                    displaySource = _cachedDisplaySource;
                    displayMs = _cachedDisplayMs;
                }
                else
                {
                    displayHit = false;
                    var displayWatch = Stopwatch.StartNew();
                    if (!WasperDendroBridge.TryRemeshDisplay(volume, settings, out Mesh rawMesh, out string displayError))
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, displayError);
                        return;
                    }
                    displayWatch.Stop();

                    if (rawMesh == null || rawMesh.Faces.Count == 0)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Dendro volume produced an empty mesh.");
                        return;
                    }

                    meshClosed = rawMesh.IsClosed;
                    stableMesh = rawMesh.DuplicateMesh();
                    displaySource = "remeshed";
                    displayMs = displayWatch.ElapsedMilliseconds;
                }
            }
            else
            {
                var displayWatch = Stopwatch.StartNew();
                Mesh existingMesh;
                string dSource;
                string displayError;

                if (WasperDendroBridge.TryGetExistingDisplayMesh(volume, out existingMesh, out displayError))
                {
                    dSource = "existing";
                }
                else
                {
                    if (!WasperDendroBridge.TryGetOrCreateSettings(
                            null,
                            volume.GetType().Assembly,
                            out object fallbackSettings,
                            out string fallbackSettingsError) ||
                        !WasperDendroBridge.TrySetDouble(
                            fallbackSettings,
                            "VoxelSize",
                            2.0,
                            out fallbackSettingsError))
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, fallbackSettingsError);
                        return;
                    }

                    if (!WasperDendroBridge.TryRemeshDisplay(volume, fallbackSettings, out existingMesh, out displayError))
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, displayError);
                        return;
                    }

                    dSource = "generated fallback (VoxelSize 2.0)";
                }
                displayWatch.Stop();

                if (existingMesh == null || existingMesh.Faces.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Dendro volume produced an empty mesh.");
                    return;
                }

                displayKeyCandidate.DisplayMeshRef = existingMesh;

                if (hadDisplayCache && previousDisplayKey != null && previousDisplayKey.Matches(displayKeyCandidate))
                {
                    displayHit = true;
                    stableMesh = _cachedStableMesh;
                    meshClosed = _cachedMeshClosed;
                    displaySource = _cachedDisplaySource;
                    displayMs = _cachedDisplayMs;
                }
                else
                {
                    displayHit = false;
                    meshClosed = existingMesh.IsClosed;
                    stableMesh = existingMesh.DuplicateMesh();
                    displaySource = dSource;
                    displayMs = displayWatch.ElapsedMilliseconds;
                }
            }

            if (!displayHit)
            {
                // Phase 4: keep one stable duplicated mesh for both the mesh output and the field's SDF
                // source, so a later Dendro-side mutation of the original Display can never invalidate an
                // already-built WasperField out from under downstream components holding onto it.
                _lastDisplayKey = displayKeyCandidate;
                _cachedStableMesh = stableMesh;
                _cachedMeshClosed = meshClosed;
                _cachedDisplaySource = displaySource;
                _cachedDisplayMs = displayMs;
                _hasDisplayCache = true;

                // The display changed, so any cached field wrap (built from the previous stable mesh)
                // is stale regardless of whether the label also changed.
                _hasFieldCache = false;
            }

            // ---- Field-wrap tier: only re-runs WasperField.FromMesh when the label actually changes ----
            // (review finding R5: the old single-tier cache key omitted Label entirely, so a label-only
            // change silently returned a stale cached field/metadata still carrying the old label.)
            bool fieldHit = displayHit && _hasFieldCache && _lastLabel == label;

            WasperFieldGoo fieldGoo;
            long fieldWrapMs;

            if (fieldHit)
            {
                fieldGoo = _cachedFieldGoo;
                fieldWrapMs = _cachedFieldWrapMs;
            }
            else
            {
                var wrapWatch = Stopwatch.StartNew();
                WasperField field = WasperField.FromMesh(
                    stableMesh,
                    tolerance,
                    label,
                    $"Source: Dendro volume [{sourceType}]",
                    WasperFieldSdfQuality.ApproximateSdf);
                wrapWatch.Stop();

                if (field == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not build a WASPer field from the Dendro mesh.");
                    return;
                }

                fieldGoo = new WasperFieldGoo(field);
                fieldWrapMs = wrapWatch.ElapsedMilliseconds;

                _lastLabel = label;
                _cachedFieldGoo = fieldGoo;
                _cachedFieldWrapMs = fieldWrapMs;
                _hasFieldCache = true;
            }

            totalWatch.Stop();

            if (!meshClosed)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Dendro mesh is not closed - inside/outside evaluation may be incorrect. Try a smaller voxel size or check the source volume.");

            string cacheStatus = displayHit && fieldHit
                ? "hit"
                : displayHit
                    ? "miss - label changed (display reused)"
                    : "miss - " + DescribeDisplayCacheMiss(previousDisplayKey, displayKeyCandidate, hadDisplayCache);

            BoundingBox bb = stableMesh.GetBoundingBox(true);
            string infoBase =
                "Dendro to WASPer Field\n" +
                $"source type   : {sourceType}\n" +
                "field quality : ApproximateSdf (closest-point on Dendro's meshed iso-surface)\n" +
                $"display_source: {displaySource}\n" +
                $"mesh v/f      : {stableMesh.Vertices.Count:N0} / {stableMesh.Faces.Count:N0}\n" +
                $"mesh closed   : {meshClosed}\n" +
                $"domain min    : {bb.Min.X:F3}, {bb.Min.Y:F3}, {bb.Min.Z:F3}\n" +
                $"domain max    : {bb.Max.X:F3}, {bb.Max.Y:F3}, {bb.Max.Z:F3}\n" +
                $"tolerance     : {tolerance}\n" +
                $"display_ms    : {displayMs}\n" +
                $"field_wrap_ms : {fieldWrapMs}\n" +
                $"elapsed_ms    : {totalWatch.ElapsedMilliseconds}\n" +
                $"cache         : {cacheStatus}";

            DA.SetData(0, stableMesh);
            DA.SetData(1, fieldGoo);
            DA.SetData(2, sourceType);
            DA.SetData(3, infoBase);
            Message = displayHit && fieldHit ? _versionTag + " | cached" : _versionTag + " | linked";
        }

        private static string DescribeDisplayCacheMiss(DisplayCacheKey previous, DisplayCacheKey current, bool hadCache)
        {
            if (!hadCache || previous == null)
                return "first run";

            if (!ReferenceEquals(previous.VolumeRef, current.VolumeRef))
                return "volume changed";

            if (previous.SettingsConnected != current.SettingsConnected)
                return "settings connectivity changed";

            if (previous.Tolerance != current.Tolerance)
                return "tolerance changed";

            if (current.SettingsConnected)
            {
                if (previous.VoxelSize != current.VoxelSize
                    || previous.IsoValue != current.IsoValue
                    || previous.Adaptivity != current.Adaptivity
                    || previous.Bandwidth != current.Bandwidth)
                    return "settings changed";
            }
            else if (!ReferenceEquals(previous.DisplayMeshRef, current.DisplayMeshRef))
            {
                return "display changed";
            }

            return "unknown";
        }

        /// <summary>
        /// Identifies everything that determines the resolved display mesh (NOT the final field/label -
        /// see the separate field-wrap tier above). When settings are connected, the volume is always
        /// remeshed, so the key tracks the volume reference plus the settings values that actually
        /// affect the mesh. When settings are unconnected, the existing Display mesh is reused as-is,
        /// so the key tracks the volume reference plus that Display mesh's own reference (a new Display
        /// instance from an upstream recompute means a genuinely new mesh to wrap).
        /// </summary>
        private sealed class DisplayCacheKey
        {
            public object VolumeRef;
            public bool SettingsConnected;
            public object DisplayMeshRef;
            public double VoxelSize;
            public double IsoValue;
            public double Adaptivity;
            public double Bandwidth;
            public double Tolerance;

            public bool Matches(DisplayCacheKey other)
            {
                if (other == null) return false;
                if (!ReferenceEquals(VolumeRef, other.VolumeRef)) return false;
                if (SettingsConnected != other.SettingsConnected) return false;
                if (Tolerance != other.Tolerance) return false;

                if (SettingsConnected)
                {
                    return VoxelSize == other.VoxelSize
                        && IsoValue == other.IsoValue
                        && Adaptivity == other.Adaptivity
                        && Bandwidth == other.Bandwidth;
                }

                return ReferenceEquals(DisplayMeshRef, other.DisplayMeshRef);
            }
        }
    }
}
