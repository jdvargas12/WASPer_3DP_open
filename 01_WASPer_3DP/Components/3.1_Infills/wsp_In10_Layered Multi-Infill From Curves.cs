#region Component Description
/*
	Component Name:
		wsp_In10_Layered Multi-Infill (From Curves)

	Nickname:
		Multi-Infill

	Version:
		Assembly version

	Category / Subcategory:
		WASPer_3DP / 3.1_Infills

	Description:
		Generic guide-domain infill generator. One typed infill_params object broadcasts
		to all domains; multiple objects assign/cycle domain-by-domain. Accepts typed
		TPMS, 2D, and Turtle infill parameters from wsp_In11, wsp_In12, and wsp_In13.
		Includes a Guide + Shell Editor using the actual selected-layer curve shapes.
		Normalized arc-length guide edits and shell-seam edits share the same All/Range/
		Single layer scope. X seam adds independently offset start/end arms around a
		user-defined seam fillet radius; X-seam shell paths are intentionally open.
		Live applies completed editor changes immediately. Pause keeps guide and seam
		edits pending in the editor and Rhino preview without recomputing the Grasshopper
		outputs; Update applies all pending edits in one solution.
		Guide stations support Ctrl-click and rectangular multi-selection. Dragging any
		selected station moves the group by one constrained normalized arc-length delta;
		Ctrl-drag adds a region and Esc clears the selection.
		Guide clearances are geometric layer-plane offsets: clear_guide offsets the
		outer guides and clear_in offsets both sides of every partition. clear_long
		offsets the lateral shell segments inward and clips the infill guides against
		those shifted boundaries. With close_shell off, equivalent hypothetical
		segments are constructed directly from the raw guide endpoints.

		res controls both shell sampling and requested field-grid spacing. The TPMS grid
		refines automatically to retain at least approximately 12 samples per period.

		trim_layers (bool, default true):
			false	original topology:	{layer ; domain ; path}
			true	flat topology:		{layer}  (one branch per layer, all contours listed)

		full_path collects shell, infill, and partitions in that order. It is a path
		collection and does not insert travel moves or join disconnected curves.
		Every final curve carries hidden WASPer.PathRole metadata (Shell, Infill,
		or Partition), matching Slicer Plus v3 even when trim_layers collapses the
		role branches into one branch per layer. Shell curves additionally carry a
		versioned WASPer.ShellSeam record with their seam settings and canonical
		pre-seam loop so downstream path components can preserve and re-edit it.

		la_planes: each layer's plane is fit to that layer's guide curves, then its
		origin is recentred (2026-08-05) to the centre of that layer's full_path
		bounding box (shells + partitions + infill), computed in the plane's own
		local X/Y so the origin stays exactly on the fitted plane. Axes are
		unchanged. Layers with no full_path geometry keep the unmodified fitted
		plane.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;
using WASPer_3DP.PatternEditing;
#endregion

namespace WASPer_3DP.Components._3_1_Infills
{
    public partial class wsp_In10_Layered_Multi_Infill_From_Curves : GH_Component, IWasperGuideWarpEditorHost
    {
        private readonly string _versionTag;
        private const int PARALLEL_THRESHOLD = 8;
        private const string GuideWarpStorageKey = "WASPer.In10v2.GuideWarp";
        private const string GuideDensityStorageKey = "WASPer.In10v2.GuideDensity";
        private const string GuideLayerWarpStorageKey = "WASPer.In10v2.LayerGuideWarp";
        private const string GuideScopeStorageKey = "WASPer.In10v2.LayerScope";
        private const string GuideScopeFromStorageKey = "WASPer.In10v2.LayerFrom";
        private const string GuideScopeToStorageKey = "WASPer.In10v2.LayerTo";
        private const string GuideDisplayLayerStorageKey = "WASPer.In10v2.DisplayLayer";
        private const string ShellSeamStorageKey = "WASPer.In10v2.ShellSeam";
        private const string LayerShellSeamStorageKey = "WASPer.In10v2.LayerShellSeam";
        private const string GuideLiveStorageKey = "WASPer.In10v2.Live";
        private const string AppliedGuideStateStorageKey = "WASPer.In10v2.AppliedState";
        private readonly WasperGuideWarpState _guideWarp = new WasperGuideWarpState();
        private readonly Dictionary<int, Dictionary<int, List<double>>> _layerGuideWarps =
            new Dictionary<int, Dictionary<int, List<double>>>();
        private readonly Stack<GuideWarpHistorySnapshot> _guideUndo =
            new Stack<GuideWarpHistorySnapshot>();
        private readonly Stack<GuideWarpHistorySnapshot> _guideRedo =
            new Stack<GuideWarpHistorySnapshot>();
        private GuideWarpHistorySnapshot _guideEditBefore;
        private GuideWarpHistorySnapshot _appliedGuideState;
        private bool _appliedGuideStateInitialized;
        private bool _guideLive = true;
        private bool _hasPendingGuideUpdate;
        private string _lastTypeTag = string.Empty;
        private WasperEtoGuideWarpEditorForm _guideEditor;
        private int _guideDomainCount;
        private int _guideVisualRevision;
        private string _lastGuideTreeSignature;
        private bool _guideAutoResetOnLastSolve;
        private bool _hasMixedGuideTopology;
        private List<int> _guideAnchorCounts = new List<int>();
        private readonly Dictionary<int, int> _guideDensityOverrides =
            new Dictionary<int, int>();
        private List<int> _guideAutomaticDensities = new List<int>();
        private IReadOnlyList<IReadOnlyList<bool>> _guidePrimaryStations =
            Array.Empty<IReadOnlyList<bool>>();
        private IReadOnlyList<IReadOnlyList<double>> _guideSourceStations =
            Array.Empty<IReadOnlyList<double>>();
        private IReadOnlyList<IReadOnlyList<PointF>> _guideEditorCurves =
            Array.Empty<IReadOnlyList<PointF>>();
        private IReadOnlyList<Curve> _guidePreviewCurves = Array.Empty<Curve>();
        private IReadOnlyList<IReadOnlyList<IReadOnlyList<PointF>>> _layerGuideEditorCurves =
            Array.Empty<IReadOnlyList<IReadOnlyList<PointF>>>();
        private IReadOnlyList<IReadOnlyList<Curve>> _layerGuidePreviewCurves =
            Array.Empty<IReadOnlyList<Curve>>();
        private IReadOnlyList<IReadOnlyList<IReadOnlyList<PointF>>> _layerShellEditorCurves =
            Array.Empty<IReadOnlyList<IReadOnlyList<PointF>>>();
        private IReadOnlyList<IReadOnlyList<Curve>> _layerShellPreviewCurves =
            Array.Empty<IReadOnlyList<Curve>>();
        private IReadOnlyList<IReadOnlyList<PointF>> _shellEditorCurves =
            Array.Empty<IReadOnlyList<PointF>>();
        private IReadOnlyList<IReadOnlyList<IReadOnlyList<PointF>>> _layerPartitionEditorCurves =
            Array.Empty<IReadOnlyList<IReadOnlyList<PointF>>>();
        private IReadOnlyList<IReadOnlyList<PointF>> _partitionEditorCurves =
            Array.Empty<IReadOnlyList<PointF>>();
        private WasperShellSeamSettings _shellSeam = new WasperShellSeamSettings();
        private readonly Dictionary<int, WasperShellSeamSettings> _layerShellSeams =
            new Dictionary<int, WasperShellSeamSettings>();
        private int _activeGuideIndex;
        private WasperGuideLayerScope _guideLayerScope = WasperGuideLayerScope.All;
        private int _guideLayerFrom;
        private int _guideLayerTo;
        private int _guideDisplayLayer;
        private In10v2GuideConduit _guideConduit;
        private readonly Dictionary<int, LayerShellCacheEntry> _layerShellCache =
            new Dictionary<int, LayerShellCacheEntry>();
        private readonly object _layerShellCacheLock = new object();

        public wsp_In10_Layered_Multi_Infill_From_Curves()
            : base(
                "wsp_In10_Layered Multi-Infill (From Curves)",
                "Layered Multi-Infill",
                "Generates printable infill paths between ordered guide curves. Each consecutive guide pair is one domain. Connect one typed infill_params object to broadcast it, or several objects to assign/cycle them domain-by-domain. Supports TPMS, lightweight 2D centreline, and Turtle Infill Params from wsp_In11, wsp_In12, and wsp_In13.\n" +
                "Guide correspondence preserves the supplied geometry. Open-Open pairs use the actual open curve pieces and normalized arc length; Closed-Closed pairs align their cyclic seams; and a direct Open-Closed pair trims only the closed guide to the arc matching the open guide endpoints. Open curves are never projected onto a cross-layer reference or cropped to an inferred phase interval. The Guide + Shell Editor is temporarily disabled whenever corresponding guides change between open and closed topology.\n" +
                "Open the Guide + Shell Editor from the component button to stretch or compress the pattern independently along each guide and edit the shell seam. The shared layer scope targets all layers, an inclusive range, or one selected layer. Shell controls move the seam around closed shell curves; X seam adds independently draggable start/end arms separated by the seam fillet radius. X-seam paths are intentionally open at their printing start/end. The infill and shell views rotate around their own local origins.\n" +
                "The editor starts in Live mode. Switch it to Paused to adjust guides or seams while retaining the last applied Grasshopper outputs, then press Update to apply every pending edit in one recomputation. Pending edits remain visible in the editor and Rhino viewport.\n" +
                "Guide stations support multi-selection: Ctrl-click toggles individual stations, dragging empty space selects a region, Ctrl-drag adds a region, and Esc clears the selection. Drag any selected station to move the group together by a constrained normalized arc-length delta; endpoints remain fixed and station ordering is preserved.\n" +
                "Guide clearances use model-space offsets in each estimated layer plane: clear_guide offsets outer guides toward the domain and clear_in offsets both sides of intermediate partitions. clear_long is measured perpendicular to the two lateral shell segments, offsets those edges inward, and clips both infill guides against the shifted boundaries. With close_shell enabled the lateral references include the shell's half-path-width end inset; with close_shell disabled equivalent hypothetical segments are built from the raw guide endpoints without that inset. If Rhino cannot construct valid planar offsets or intersections, that domain reports an explicit fallback diagnostic.\n" +
                "For Square-S 2D infill, both transverse lines of every interval are exposed as primary guide controls: the cell boundary and the half-cell transition.\n" +
                "All final path curves carry hidden WASPer.PathRole metadata, so Shell/Infill/Partition identity remains available when trim_layers collapses full_path.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.1_Infills")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("048862FD-1FFF-41DA-BFBA-046AE00FB2D5");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        public override void CreateAttributes()
        {
            m_attributes = new In10v2GuideEditorAttributes(this);
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In10_Layered Multi-Infill From Curves.png"))
                        return s != null ? new System.Drawing.Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            base.DrawViewportWires(args);
        }

        internal void DrawGuideEditorReferences(Rhino.Display.DisplayPipeline display)
        {
            int previewLayer = ResolveGuideDisplayLayer();
            IReadOnlyList<Curve> previewCurves =
                previewLayer >= 0 && previewLayer < _layerGuidePreviewCurves.Count
                    ? _layerGuidePreviewCurves[previewLayer]
                    : Array.Empty<Curve>();
            if (previewCurves == null || previewCurves.Count == 0)
                return;
            double tolerance = RhinoDoc.ActiveDoc != null
                ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                : 1e-6;
            for (int guide = 0; guide < previewCurves.Count; guide++)
            {
                Curve curve = previewCurves[guide];
                if (curve == null || !curve.IsValid)
                    continue;
                bool selected = guide == _activeGuideIndex;
                display.DrawCurve(
                    curve,
                    selected ? Color.FromArgb(45, 105, 170) : Color.FromArgb(115, 135, 150),
                    selected ? 4 : 2);
                int anchorCount = guide < _guideAnchorCounts.Count
                    ? _guideAnchorCounts[guide]
                    : WasperGuideWarpState.DefaultAnchorCount;
                IReadOnlyList<double> sourceStations = guide < _guideSourceStations.Count
                    ? _guideSourceStations[guide]
                    : Enumerable.Range(0, anchorCount)
                        .Select(i => (double)i / Math.Max(1, anchorCount - 1))
                        .ToArray();
                IReadOnlyList<double> values = GetEffectiveGuideWarp(
                    previewLayer,
                    guide,
                    sourceStations);
                double length = curve.GetLength();
                for (int anchor = 0; anchor < values.Count; anchor++)
                {
                    double originalU = anchor < sourceStations.Count
                        ? sourceStations[anchor]
                        : (double)anchor / Math.Max(1, values.Count - 1);
                    Point3d original = PointAtNormalizedLength(curve, length, originalU, tolerance);
                    Point3d edited = PointAtNormalizedLength(curve, length, values[anchor], tolerance);
                    display.DrawPoint(
                        original,
                        Rhino.Display.PointStyle.RoundControlPoint,
                        3,
                        Color.FromArgb(145, 145, 145));
                    display.DrawPoint(
                        edited,
                        anchor == 0 || anchor == values.Count - 1
                            ? Rhino.Display.PointStyle.RoundControlPoint
                            : Rhino.Display.PointStyle.ActivePoint,
                        selected ? 7 : 5,
                        selected
                            ? Color.FromArgb(235, 132, 35)
                            : Color.FromArgb(75, 145, 185));
                }
            }

            IReadOnlyList<Curve> shellCurves =
                previewLayer >= 0 && previewLayer < _layerShellPreviewCurves.Count
                    ? _layerShellPreviewCurves[previewLayer]
                    : Array.Empty<Curve>();
            foreach (Curve shell in shellCurves)
                if (shell != null && shell.IsValid)
                    display.DrawCurve(shell, Color.FromArgb(45, 145, 95), 3);

            Curve activeShell = shellCurves.FirstOrDefault(shell =>
                shell != null && shell.IsValid && shell.IsClosed);
            if (activeShell != null)
            {
                WasperShellSeamSettings settings = GetEffectiveShellSeam(previewLayer);
                double shellLength = activeShell.GetLength();
                double seamU = Wrap01(settings.SeamU);
                Point3d seam = PointAtNormalizedLength(
                    activeShell, shellLength, seamU, tolerance);
                display.DrawPoint(
                    seam,
                    Rhino.Display.PointStyle.ActivePoint,
                    9,
                    Color.FromArgb(190, 45, 155));

                if (settings.XSeam && shellLength > tolerance)
                {
                    activeShell.NormalizedLengthParameter(seamU, out double seamT);
                    Vector3d tangent = activeShell.TangentAt(seamT);
                    if (!tangent.Unitize()) tangent = Vector3d.XAxis;
                    Plane shellPlane;
                    if (!activeShell.TryGetPlane(out shellPlane, tolerance))
                        shellPlane = Plane.WorldXY;
                    Vector3d inward = Vector3d.CrossProduct(shellPlane.Normal, tangent);
                    if (!inward.Unitize()) inward = shellPlane.YAxis;
                    Polyline polyline;
                    Point3d center = activeShell.GetBoundingBox(false).Center;
                    if (activeShell.TryGetPolyline(out polyline) && polyline.Count > 0)
                        center = new Point3d(
                            polyline.Average(point => point.X),
                            polyline.Average(point => point.Y),
                            polyline.Average(point => point.Z));
                    if (Vector3d.Multiply(center - seam, inward) < 0.0)
                        inward.Reverse();
                    double du = Math.Min(
                        0.24,
                        Math.Max(0.0, settings.FilletRadius) / shellLength);
                    Point3d attachStart = PointAtNormalizedLength(
                        activeShell, shellLength, Wrap01(seamU + du), tolerance);
                    Point3d attachEnd = PointAtNormalizedLength(
                        activeShell, shellLength, Wrap01(seamU - du), tolerance);
                    Point3d start = seam + inward * settings.StartOffset +
                        tangent * settings.StartTangentialOffset;
                    Point3d end = seam + inward * settings.EndOffset +
                        tangent * settings.EndTangentialOffset;
                    display.DrawLine(new Line(start, attachStart), Color.FromArgb(60, 150, 205), 3);
                    display.DrawLine(new Line(attachEnd, end), Color.FromArgb(205, 75, 145), 3);
                    display.DrawPoint(start, Rhino.Display.PointStyle.ActivePoint, 8, Color.FromArgb(60, 150, 205));
                    display.DrawPoint(end, Rhino.Display.PointStyle.ActivePoint, 8, Color.FromArgb(205, 75, 145));
                }
            }
        }

        internal BoundingBox GuideEditorReferenceBoundingBox()
        {
            int previewLayer = ResolveGuideDisplayLayer();
            IReadOnlyList<Curve> previewCurves =
                previewLayer >= 0 && previewLayer < _layerGuidePreviewCurves.Count
                    ? _layerGuidePreviewCurves[previewLayer]
                    : Array.Empty<Curve>();
            var bounds = BoundingBox.Empty;
            if (previewCurves == null)
                return bounds;
            foreach (Curve curve in previewCurves)
            {
                if (curve == null || !curve.IsValid)
                    continue;
                BoundingBox curveBounds = curve.GetBoundingBox(false);
                if (curveBounds.IsValid)
                    bounds.Union(curveBounds);
            }
            IReadOnlyList<Curve> shellCurves =
                previewLayer >= 0 && previewLayer < _layerShellPreviewCurves.Count
                    ? _layerShellPreviewCurves[previewLayer]
                    : Array.Empty<Curve>();
            foreach (Curve shell in shellCurves)
            {
                if (shell == null || !shell.IsValid)
                    continue;
                BoundingBox shellBounds = shell.GetBoundingBox(false);
                if (shellBounds.IsValid)
                    bounds.Union(shellBounds);
            }
            if (bounds.IsValid)
            {
                WasperShellSeamSettings seamSettings = GetEffectiveShellSeam(previewLayer);
                double endpointReach = seamSettings.XSeam
                    ? Math.Max(
                        Math.Sqrt(
                            seamSettings.StartOffset * seamSettings.StartOffset +
                            seamSettings.StartTangentialOffset * seamSettings.StartTangentialOffset),
                        Math.Sqrt(
                            seamSettings.EndOffset * seamSettings.EndOffset +
                            seamSettings.EndTangentialOffset * seamSettings.EndTangentialOffset))
                    : 0.0;
                double padding = Math.Max(
                    Math.Max(1e-6, bounds.Diagonal.Length * 0.01),
                    endpointReach + Math.Max(0.0, seamSettings.FilletRadius));
                bounds.Inflate(padding);
            }
            return bounds;
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "guide_curves", "guides",
                "Guide curves as a DataTree. Each branch = one layer.\n" +
                "Each branch must contain >= 2 curves ordered from one boundary to the other.\n" +
                "Domains are formed between consecutive pairs: [0+1], [1+2], etc.\n" +
                "Open-Open pairs use the supplied open curve pieces directly and align direction by the smallest endpoint pairing. Closed-Closed pairs align their cyclic seams. For a direct Open-Closed pair, the open endpoints are projected onto the closed curve and only the geometrically best-matching closed arc is used; the open curve itself is never cropped or remapped.\n" +
                "A guide is treated as closed when its start/end distance is <= res/2 (or the document tolerance). All curves in a branch are auto-aligned to the same direction.\n" +
                "Guide Editor assumes corresponding guide indices identify the same logical guide across layer branches. It is disabled when a guide index changes between open and closed topology anywhere in the layer tree, or when one domain directly pairs an open and a closed guide. This restriction affects editing only; it does not modify the input geometry. Otherwise its scope can apply an edit to all layers, an inclusive range, or one layer.",
                GH_ParamAccess.tree);

            pManager.AddGenericParameter(
                "infill_params", "infill_p",
                "Typed WASPer infill parameter objects. One object broadcasts to every domain; multiple objects assign/cycle domain-by-domain. Accepts wsp_In11 TPMS, wsp_In12 2D, and wsp_In13 Turtle Infill Params.",
                GH_ParamAccess.list);
            pManager[1].DataMapping = GH_DataMapping.Flatten;

            pManager.AddNumberParameter(
                "w_shell", "w_shell",
                "Shell line width in model units. Shell paths are offset inward from each outermost guide,\n" +
                "centred at (i - 0.5) × w_shell for i = 1 … n_shell (Slicer Plus convention).\n" +
                "Default 5. 0 = no shell output. LIST access. Cycles per domain. Must be >= 0.",
                GH_ParamAccess.list, 5.0);

            pManager.AddIntegerParameter(
                "n_shell", "n_shell",
                "Number of shell lines per outermost guide. Minimum 1. LIST access. Cycles per domain.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "longitudinal_clearance", "clear_long",
                "Clearance measured inward from the two actual lateral shell segments (model units).\n" +
                "The start-to-start and end-to-end shell edges are offset inward by this distance, then both infill guides are clipped against those parallel boundaries.\n" +
                "When close_shell is false, hypothetical lateral edges are constructed from the raw guide endpoints without the half-path-width end inset.\n" +
                "Only affects infill; shell paths use the full guides. Default 0.\n" +
                "Suggested default when shell is active: max(0, w_shell × n_shell - w_shell / 2).",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "guide_clearance", "clear_guide",
                "True model-space offset from the two OUTERMOST guide curves [0] and [-1] toward their adjacent domains (model units).\n" +
                "Computed in the estimated layer plane rather than projected along point-to-point chords. Only affects infill; shell paths are built first. Default 0.\n" +
                "Suggested default when shell is active: max(0, w_shell × n_shell - w_shell / 2).",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "clearance_infill", "clear_in",
                "True model-space offset from both sides of every INTERMEDIATE partition guide [1..n-2] (model units).\n" +
                "Each adjacent domain receives its own inward planar offset, producing a regular gap around curved or jagged partitions. Ignored when only 2 curves are provided. Default 0.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "resolution", "res",
                "Requested sampling distance in model units for TPMS contours and shell paths. The field grid refines internally when needed to resolve the selected period counts. Default 2.0.",
                GH_ParamAccess.item, 2.0);

            pManager.AddIntegerParameter(
                "min_pts", "min",
                "Discard contours with fewer polyline vertices than this. Default 2.",
                GH_ParamAccess.item);

            pManager.AddBooleanParameter(
                "trim_layers", "trim_la",
                "Output tree structure for full_path, shell, infill, partitions, and points.\n" +
                "False = {layer;domain;path}, True = {layer}. Default true.",
                GH_ParamAccess.item, true);

            pManager.AddBooleanParameter(
                "close_shell", "close_shell",
                "If true, shell paths are closed according to guide topology. Open-Open outer guides retain the existing paired perimeter closure. Closed outer guides remain independent loops and do not receive an artificial connector across their seam. In mixed Open-Closed outer topology, the open shell is closed independently while the already closed shell remains its own loop.\n" +
                "A reference guide counts as closed when dist(start,end) <= res/2 (or document tolerance). Default true.",
                GH_ParamAccess.item, true);

            for (int i = 1; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "full_path", "full_path",
                "Complete printable curve collection ordered as shell, infill, then partitions. trim=true gives {layer}; trim=false uses {layer;0;shell}, {layer;1;domain;path}, and {layer;2;partition}. Every curve carries hidden WASPer.PathRole metadata (Shell, Infill, or Partition). Shell curves also carry versioned WASPer.ShellSeam metadata containing the active settings and canonical pre-seam loop for downstream preservation and re-editing. Curves are collected, not automatically joined.",
                GH_ParamAccess.tree);

            pManager.AddPlaneParameter(
                "layer_planes", "la_planes",
                "Source plane for each generated layer, fit to that layer's guide curves. The origin is " +
                "recentred to the middle of that layer's full_path bounding box (shells + partitions + " +
                "infill), measured in the plane's own X/Y so the origin stays exactly on the fitted plane; " +
                "axes are unchanged. Falls back to the unmodified fitted plane for layers with no full_path " +
                "geometry. One branch per layer.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "shell", "shell",
                "Shell polylines offset inward from outermost guides (n_shell lines per guide, Slicer Plus convention). With close_shell enabled, Open-Open sides use paired perimeter closure, while already closed guides remain independent loops without an artificial cross-domain connector. The Guide + Shell Editor can relocate a printable loop's start seam when the guide topology is editor-compatible. X seam adds adjustable start/end arms separated by the seam fillet radius and intentionally returns an open printing path. Curves retain WASPer.PathRole=Shell plus versioned WASPer.ShellSeam metadata, including the canonical pre-seam loop. One branch per layer.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "infill", "infill",
                "Generated infill paths as PolylineCurves carrying WASPer.PathRole=Infill metadata.\n" +
                "trim_layers=false: {layer;domain;path}\n" +
                "trim_layers=true : {layer}.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "partitions", "parts",
                "Inner guide curves carrying WASPer.PathRole=Partition metadata that partition the space into domains ([1..n-2]). One branch per layer. Empty when each layer has exactly 2 guides.",
                GH_ParamAccess.tree);

            pManager.AddPointParameter(
                "contour_pts", "pts",
                "Contour polyline vertices. Same topology as infill.",
                GH_ParamAccess.tree);

            pManager.AddNumberParameter(
                "porosity_layer", "φ_layer",
                "Estimated porosity per layer (0–1). 1 = fully void, 0 = fully solid.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "porosity_avg", "φ_avg",
                "Average estimated porosity across all layers (0–1).",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "info", "info",
                "Diagnostics, parameter assignment, effective resolution, and per-branch statistics.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "infill_kpis", "in_kpis",
                "Global infill KPI set for the WASPer Study Manager. Includes the full and " +
                "short cell names, X/Y/Z cell counts, guide-domain dimensions measured in the " +
                "first valid layer plane, and domain count when more than one guide domain is " +
                "present. Multiple infill_p definitions are comma-separated in assignment order.",
                GH_ParamAccess.item);
        }

        /// <summary>
        /// Lightweight fingerprint of the guide_curves tree, used to detect when the user has
        /// rewired fundamentally different guide geometry rather than the ordinary layer-to-
        /// layer shape variation that guide warp is deliberately designed to tolerate (warp is
        /// stored as normalized station positions specifically so it stays meaningful across a
        /// tapering stack of layers). Built from one representative branch (the first non-empty
        /// one) rather than every layer, so it stays cheap and only reacts to changes that are
        /// visible at that branch - closed/open flips or a materially different curve count or
        /// length signal "this is a different guide", not "this layer is slightly smaller".
        /// </summary>
        private static string ComputeGuideTreeSignature(GH_Structure<GH_Curve> guideTree)
        {
            if (guideTree == null || guideTree.PathCount == 0)
                return string.Empty;
            var signature = new StringBuilder();
            signature.Append(guideTree.PathCount).Append('|');
            List<GH_Curve> reference = guideTree.Branches
                .FirstOrDefault(branch => branch != null && branch.Count > 0);
            if (reference == null)
                return signature.ToString();
            signature.Append(reference.Count).Append('|');
            foreach (GH_Curve ghCurve in reference)
            {
                Curve curve = ghCurve?.Value;
                if (curve == null || !curve.IsValid)
                {
                    signature.Append("x|");
                    continue;
                }
                signature
                    .Append(curve.IsClosed ? 'C' : 'O')
                    .Append(':')
                    .Append(Math.Round(curve.GetLength(), 2).ToString("0.##"))
                    .Append('|');
            }
            return signature.ToString();
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            _guideDomainCount = 0;
            _guideAnchorCounts.Clear();
            _guideAutomaticDensities.Clear();
            _guidePrimaryStations = Array.Empty<IReadOnlyList<bool>>();
            _guideSourceStations = Array.Empty<IReadOnlyList<double>>();
            _guideEditorCurves = Array.Empty<IReadOnlyList<PointF>>();
            _guidePreviewCurves = Array.Empty<Curve>();
            _layerGuideEditorCurves = Array.Empty<IReadOnlyList<IReadOnlyList<PointF>>>();
            _layerGuidePreviewCurves = Array.Empty<IReadOnlyList<Curve>>();
            _layerShellEditorCurves = Array.Empty<IReadOnlyList<IReadOnlyList<PointF>>>();
            _layerShellPreviewCurves = Array.Empty<IReadOnlyList<Curve>>();
            _shellEditorCurves = Array.Empty<IReadOnlyList<PointF>>();
            _layerPartitionEditorCurves = Array.Empty<IReadOnlyList<IReadOnlyList<PointF>>>();
            _partitionEditorCurves = Array.Empty<IReadOnlyList<PointF>>();
            GH_Structure<GH_Curve> guideTree = null;

            var wShellList  = new List<double>();
            var nShellList  = new List<int>();
            double clearLong    = 0.0;
            double clearGuide   = 0.0;
            double clearIn      = 0.0;
            double sampleSpacing = 2.0;
            int minPts = 2;
            bool trimLayers = true;
            bool closeShell = true;
            var rawParamValues = new List<IGH_Goo>();
            var infillParams = new List<IWasperInfillParams>();

            if (!DA.GetDataTree(0, out guideTree) || guideTree == null || guideTree.PathCount == 0)
            {
                DA.SetDataTree(0, new DataTree<Curve>());
                DA.SetDataTree(1, new DataTree<Plane>());
                DA.SetDataTree(2, new DataTree<Curve>());
                DA.SetDataTree(3, new DataTree<Curve>());
                DA.SetDataTree(4, new DataTree<Curve>());
                DA.SetDataTree(5, new DataTree<Point3d>());
                DA.SetDataList(6, new List<double>());
                DA.SetData(7, 0.0);
                DA.SetData(8, "Provide guide_curves as a DataTree. Each branch must have >= 2 curves.");
                DA.SetData(9, new WasperKpiSetGoo(new WasperKpiSet
                {
                    SourceComponent = Name,
                    SourceVersion = _versionTag
                }, this));
                return;
            }

            DA.GetDataList(1, rawParamValues);
            foreach (IGH_Goo raw in rawParamValues)
            {
                IWasperInfillParams parsed = WasperInfillParamsTools.Unwrap(raw);
                if (parsed != null)
                {
                    string validation = parsed.Validate();
                    if (validation != null)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Invalid {parsed.InfillKind} infill_params: {validation}");
                        return;
                    }
                    if (parsed is WasperTurtleInfillParams
                        || parsed is WasperTpmsInfillParams
                        || parsed is WasperInfill2DParams)
                    {
                        infillParams.Add(parsed);
                    }
                    else
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                            $"Unsupported {parsed.InfillKind} infill_params object. Connect wsp_In11 TPMS, wsp_In12 2D, or wsp_In13 Turtle Infill Params.");
                        return;
                    }
                }
                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        "Unsupported infill_params object. Connect wsp_In11 TPMS, wsp_In12 2D, or wsp_In13 Turtle Infill Params.");
                    return;
                }
            }
            if (infillParams.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Connect at least one wsp_In11 TPMS, wsp_In12 2D, or wsp_In13 Turtle Infill Params object.");
                return;
            }

            DA.GetDataList(2, wShellList);
            DA.GetDataList(3, nShellList);
            bool clearLongExplicit  = DA.GetData(4, ref clearLong);
            bool clearGuideExplicit = DA.GetData(5, ref clearGuide);
            DA.GetData(6, ref clearIn);
            DA.GetData(7, ref sampleSpacing);
            DA.GetData(8, ref minPts);
            DA.GetData(9, ref trimLayers);
            DA.GetData(10, ref closeShell);

            if (wShellList.Count == 0) wShellList.Add(5.0);
            if (nShellList.Count == 0) nShellList.Add(1);
            for (int i = 0; i < wShellList.Count; i++) if (wShellList[i] < 0.0) wShellList[i] = 0.0;
            for (int i = 0; i < nShellList.Count; i++) if (nShellList[i] < 1)   nShellList[i] = 1;
            // Dynamic default: (w_shell × n_shell) - (w_shell / 2) using first list entries
            {
                double w0 = wShellList[0];
                int    n0 = nShellList[0];
                double dynDef14 = Math.Max(0.0, w0 * n0 - w0 * 0.5);
                if (!clearLongExplicit)  clearLong  = dynDef14;
                if (!clearGuideExplicit) clearGuide = dynDef14;
            }
            if (clearLong  < 0.0) clearLong  = 0.0;
            if (clearGuide < 0.0) clearGuide = 0.0;
            if (clearIn    < 0.0) clearIn    = 0.0;
            if (sampleSpacing <= 0.0) sampleSpacing = 2.0;
            if (minPts < 2) minPts = 2;

            string typeTag = BuildListTag(infillParams, ParamTag);
            _lastTypeTag = typeTag;
            UpdateLiveMessage();

            double tol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;
            double eps = Math.Max(1e-9, tol * 0.1);
            double guideClosureTolerance = Math.Max(tol, sampleSpacing * 0.5);
            const int GRID_MIN = 4;
            const int GRID_MAX = 2000;
            double TWO_PI = 2.0 * Math.PI;
            int branchCount = guideTree.PathCount;
            _guideDomainCount = guideTree.Branches
                .Where(branch => branch != null)
                .Select(branch => Math.Max(0, branch.Count))
                .DefaultIfEmpty(0)
                .Max();
            _hasMixedGuideTopology = HasMixedGuideTopology(
                guideTree,
                guideClosureTolerance);
            if (_hasMixedGuideTopology && _guideEditor != null && !_guideEditor.IsClosed)
                _guideEditor.Close();

            // Guide warp is deliberately stored as normalized station positions so it keeps
            // making sense across ordinary layer-to-layer variation (a tapering stack, for
            // instance). But if the guide_curves tree changes shape in a way visible even at
            // this coarse fingerprint - a different curve count, or a guide flipping open/
            // closed, or a materially different length - any stored edits were made against
            // geometry that no longer exists, so silently reapplying them (via
            // FitWarpToStations) produces a distorted mapping instead of the user's intended
            // edit. Detect that case and drop stored warp automatically rather than carrying
            // it forward forever.
            string guideTreeSignature = ComputeGuideTreeSignature(guideTree);
            bool guideTreeChanged =
                !string.IsNullOrEmpty(_lastGuideTreeSignature) &&
                !string.Equals(_lastGuideTreeSignature, guideTreeSignature, StringComparison.Ordinal);
            _guideAutoResetOnLastSolve = false;
            if (guideTreeChanged && (_guideWarp.Snapshot().Count > 0 || _layerGuideWarps.Count > 0))
            {
                RecordUndoEvent("Reset shared infill guides (guide curves changed)");
                _guideUndo.Push(CaptureGuideWarpHistory());
                _guideRedo.Clear();
                _guideWarp.Restore(null);
                _layerGuideWarps.Clear();
                _appliedGuideState = null;
                _appliedGuideStateInitialized = false;
                _guideVisualRevision++;
                _guideAutoResetOnLastSolve = true;
            }
            _lastGuideTreeSignature = guideTreeSignature;

            if (!_appliedGuideStateInitialized)
                ApplyCurrentGuideState();
            GuideWarpHistorySnapshot calculationState = _guideLive
                ? CaptureGuideWarpHistory()
                : CloneGuideWarpHistory(_appliedGuideState);
            IReadOnlyDictionary<int, int> calculationDensity =
                calculationState?.Density ?? _guideDensityOverrides;
            _guideSourceStations = BuildGuideSourceStations(
                _guideDomainCount,
                infillParams,
                calculationDensity,
                out IReadOnlyList<IReadOnlyList<bool>> guidePrimaryStations);
            _guidePrimaryStations = guidePrimaryStations;
            _guideAutomaticDensities = BuildGuideAutomaticDensities(
                _guideDomainCount,
                infillParams);
            _guideAnchorCounts = _guideSourceStations.Select(stations => stations.Count).ToList();
            Dictionary<int, List<double>> globalGuideWarpSnapshot = Enumerable
                .Range(0, _guideDomainCount)
                .ToDictionary(
                    guide => guide,
                    guide => calculationState != null &&
                        calculationState.Global != null &&
                        calculationState.Global.TryGetValue(guide, out List<double> values)
                            ? FitWarpToStations(values, _guideSourceStations[guide]).ToList()
                            : _guideSourceStations[guide].ToList());
            Dictionary<int, Dictionary<int, List<double>>> layerGuideWarpSnapshot =
                CloneLayerGuideWarps(calculationState?.Layers);
            WasperShellSeamSettings shellSeamSnapshot =
                calculationState?.ShellGlobal?.Clone() ?? new WasperShellSeamSettings();
            Dictionary<int, WasperShellSeamSettings> layerShellSeamSnapshot =
                CloneLayerShellSeams(calculationState?.ShellLayers);
            _layerGuideEditorCurves = BuildLayerGuideEditorCurves(
                guideTree,
                tol,
                out IReadOnlyList<IReadOnlyList<Curve>> previewGuideLayers);
            _layerGuidePreviewCurves = previewGuideLayers;
            NormalizeGuideLayerScope(branchCount);
            SelectGuideDisplayLayer(_guideDisplayLayer);
            _activeGuideIndex = Clamp(_activeGuideIndex, 0, Math.Max(0, _guideDomainCount - 1));
            _guideVisualRevision++;

            var perBranchShells = new List<PolylineCurve>[branchCount];
            var perBranchShellBases = new List<PolylineCurve>[branchCount];
            var perBranchParts  = new List<(GH_Path path, Curve crv)>[branchCount];
            var perBranchCurves = new List<(GH_Path path, PolylineCurve crv)>[branchCount];
            var perBranchPts    = new List<(GH_Path path, Point3d pt)>[branchCount];
            var perBranchLog    = new string[branchCount];
            var perBranchPor    = new double[branchCount];
            var branchPlanes    = new Plane[branchCount];

            for (int i = 0; i < branchCount; i++)
            {
                perBranchShells[i] = new List<PolylineCurve>();
                perBranchShellBases[i] = new List<PolylineCurve>();
                perBranchParts [i] = new List<(GH_Path, Curve)>();
                perBranchCurves[i] = new List<(GH_Path, PolylineCurve)>();
                perBranchPts   [i] = new List<(GH_Path, Point3d)>();
                perBranchLog   [i] = "";
                branchPlanes[i] = WasperLayerPlaneTools.EstimateLayerPlaneFromGhCurves(
                    guideTree.Branches[i],
                    tol);
            }

            int skipped = 0;
            int totalContours = 0;
            int shellCacheHits = 0;
            int seamlessDomains = 0;
            int cxSnappedDomains = 0;
            int mixedDomainsMapped = 0;
            int mixedDomainsSkipped = 0;

            Action<int> processBranch = bi =>
            {
                var localLog    = new StringBuilder();
                var localShells = new List<PolylineCurve>();
                var localShellBases = new List<PolylineCurve>();
                var localParts  = new List<(GH_Path, Curve)>();
                var localCrvs   = new List<(GH_Path, PolylineCurve)>();
                var localPts    = new List<(GH_Path, Point3d)>();

                GH_Path brPath = guideTree.Paths[bi];
                GH_Path layerPath = trimLayers ? new GH_Path(bi) : brPath;
                List<GH_Curve> br = guideTree.Branches[bi];

                void Bail(string msg)
                {
                    if (msg != null) localLog.AppendLine(msg);
                    Interlocked.Increment(ref skipped);
                    perBranchShells[bi] = localShells;
                    perBranchShellBases[bi] = localShellBases;
                    perBranchParts [bi] = localParts;
                    perBranchCurves[bi] = localCrvs;
                    perBranchPts   [bi] = localPts;
                    perBranchLog   [bi] = localLog.ToString();
                }
                ;

                if (br == null || br.Count < 2)
                {
                    Bail($"Branch {brPath}: needs >= 2 curves. Skipped.");
                    return;
                }

                int nCurves = br.Count;
                int nDomains = nCurves - 1;
                var rawCurves = new Curve[nCurves];

                for (int ci = 0; ci < nCurves; ci++)
                {
                    Curve c = br[ci]?.Value?.DuplicateCurve();
                    if (c == null || !c.IsValid)
                    {
                        Bail($"Branch {brPath}: curve [{ci}] invalid. Skipped.");
                        return;
                    }
                    rawCurves[ci] = c;
                }

                AlignGuideDirections(rawCurves, guideClosureTolerance);

                // Shell paths + porosity area tracking. This stage is independent
                // from infill parameters and can be reused while only infill changes.
                double branchPrintArea = 0.0;
                double branchTotalArea = 0.0;
                {
                    int    lastDi  = Math.Max(0, nCurves - 2);
                    double wShell0 = wShellList.Count > 0 ? Math.Max(0.0, wShellList[0 % wShellList.Count]) : 0.0;
                    int    nShell0 = nShellList.Count  > 0 ? Math.Max(1,   nShellList[0 % nShellList.Count])  : 1;
                    double wShellN = wShellList.Count > 0 ? Math.Max(0.0, wShellList[lastDi % wShellList.Count]) : 0.0;
                    int    nShellN = nShellList.Count  > 0 ? Math.Max(1,   nShellList[lastDi % nShellList.Count])  : 1;

                    WasperShellSeamSettings effectiveShellSeam =
                        layerShellSeamSnapshot.TryGetValue(bi, out WasperShellSeamSettings layerSeam)
                            ? layerSeam
                            : shellSeamSnapshot;
                    string shellCacheKey = BuildLayerShellCacheKey(
                        brPath,
                        rawCurves,
                        branchPlanes[bi],
                        wShell0,
                        nShell0,
                        wShellN,
                        nShellN,
                        sampleSpacing,
                        closeShell,
                        effectiveShellSeam,
                        tol);

                    LayerShellCacheEntry cachedShell;
                    lock (_layerShellCacheLock)
                        _layerShellCache.TryGetValue(bi, out cachedShell);
                    if (cachedShell != null &&
                        string.Equals(cachedShell.Key, shellCacheKey, StringComparison.Ordinal))
                    {
                        localShells.AddRange(DuplicatePolylineCurves(cachedShell.Shells));
                        localShellBases.AddRange(DuplicatePolylineCurves(cachedShell.ShellBases));
                        Interlocked.Increment(ref shellCacheHits);
                    }
                    else
                    {
                        bool firstOuterClosed = IsEffectivelyClosed(
                            rawCurves[0],
                            guideClosureTolerance);
                        bool lastOuterClosed = IsEffectivelyClosed(
                            rawCurves[nCurves - 1],
                            guideClosureTolerance);
                        if (closeShell && wShell0 > tol && wShellN > tol &&
                            !firstOuterClosed && !lastOuterClosed)
                        {
                            int nS = Math.Min(nShell0, nShellN);
                            var sp0 = BuildShellPaths(rawCurves[0], rawCurves[1], nS, wShell0, sampleSpacing, tol, true);
                            var spN = BuildShellPaths(rawCurves[nCurves - 1], rawCurves[nCurves - 2], nS, wShellN, sampleSpacing, tol, true);
                            foreach (var c in CloseShellPairs(sp0, spN, tol))
                                localShells.Add(c);
                        }
                        else
                        {
                            if (wShell0 > tol)
                            {
                                var sp0 = BuildShellPaths(rawCurves[0], rawCurves[1], nShell0, wShell0, sampleSpacing, tol);
                                foreach (var sp in sp0)
                                    localShells.Add(closeShell
                                        ? EnsureClosedShellPath(sp, tol)
                                        : sp);
                            }
                            if (wShellN > tol)
                            {
                                var spN = BuildShellPaths(rawCurves[nCurves - 1], rawCurves[nCurves - 2], nShellN, wShellN, sampleSpacing, tol);
                                foreach (var sp in spN)
                                    localShells.Add(closeShell
                                        ? EnsureClosedShellPath(sp, tol)
                                        : sp);
                            }
                        }

                        localShellBases.AddRange(localShells
                            .Where(shell => shell != null && shell.IsValid)
                            .Select(shell => new PolylineCurve(shell.ToPolyline())));
                        localShells = localShells
                            .Select(shell => TryApplyShellSeam(
                                shell,
                                effectiveShellSeam,
                                branchPlanes[bi],
                                tol))
                            .Where(shell => shell != null && shell.IsValid)
                            .ToList();

                        lock (_layerShellCacheLock)
                            _layerShellCache[bi] = new LayerShellCacheEntry(
                                shellCacheKey,
                                localShells,
                                localShellBases);
                    }
                    branchPrintArea += rawCurves[0].GetLength() * wShell0 * nShell0;
                    branchPrintArea += rawCurves[nCurves - 1].GetLength() * wShellN * nShellN;
                }

                // Inner guides as partitions — shortened by (w_shell × n_shell - w_shell / 3)
                {
                    double w0p = wShellList.Count > 0 ? Math.Max(0.0, wShellList[0]) : 0.0;
                    int    n0p = nShellList.Count  > 0 ? Math.Max(1,   nShellList[0]) : 1;
                    double partShorten14 = Math.Max(0.0, w0p * n0p - w0p / 3.0);
                    for (int ci = 1; ci < nCurves - 1; ci++)
                    {
                        Curve partCrv = rawCurves[ci].DuplicateCurve();
                        if (partShorten14 > tol)
                            partCrv = TrimCurveEnds(partCrv, partShorten14, tol);
                        if (partCrv != null && partCrv.IsValid)
                            localParts.Add((layerPath, partCrv));
                    }
                }

                var curves = new Curve[nCurves];
                Array.Copy(rawCurves, curves, nCurves);

                var lengths = new double[nCurves];
                for (int ci = 0; ci < nCurves; ci++)
                {
                    lengths[ci] = curves[ci].GetLength();
                    if (lengths[ci] <= tol)
                    {
                        Bail($"Branch {brPath}: curve [{ci}] too short after shorten. Skipped.");
                        return;
                    }
                }

                double n01 = (branchCount <= 1) ? 0.0 : (double)bi / (double)(branchCount - 1);

                for (int di = 0; di < nDomains; di++)
                {
                    Curve originalA = curves[di];
                    Curve originalB = curves[di + 1];

                    if (!TryPrepareGuidePair(
                            originalA,
                            originalB,
                            guideClosureTolerance,
                            tol,
                            out Curve sourceA,
                            out Curve sourceB,
                            out GuidePairTopology guideTopology,
                            out string topologyNote))
                    {
                        if (guideTopology == GuidePairTopology.OpenClosed ||
                            guideTopology == GuidePairTopology.ClosedOpen)
                            Interlocked.Increment(ref mixedDomainsSkipped);
                        localLog.AppendLine(
                            $"Branch {brPath} domain [{di}]: guide correspondence failed ({topologyNote}). Skipped.");
                        continue;
                    }
                    bool mixedGuidePair =
                        guideTopology == GuidePairTopology.OpenClosed ||
                        guideTopology == GuidePairTopology.ClosedOpen;
                    if (mixedGuidePair)
                    {
                        Interlocked.Increment(ref mixedDomainsMapped);
                        localLog.AppendLine(
                            $"Branch {brPath} domain [{di}]: {guideTopology} correspondence; {topologyNote}. " +
                            "Stored Guide Editor warp is ignored for this mixed domain.");
                    }

                    // ── Closed-guide detection ────────────────────────────────────────
                    // Both guides forming loops (circles, racetracks, …) means the
                    // s01 = 0 and s01 = 1 stations are the SAME physical location, so the
                    // domain has no free ends along the guide direction.
                    bool guidesFormLoop = guideTopology == GuidePairTopology.ClosedClosed;

                    // A longitudinal clearance cuts a gap at the seam parameter, which on
                    // a loop is an arbitrary location rather than a real end. Only an
                    // explicitly supplied clear_long is honoured there, as a deliberate
                    // request to open the loop; the shell-width heuristic default is for
                    // open guides and is dropped.
                    bool openLoopByClearance =
                        guidesFormLoop && clearLongExplicit && clearLong > tol;
                    bool wrapX = guidesFormLoop && !openLoopByClearance;
                    double domainClearLong = wrapX ? 0.0 : clearLong;

                    double insetA = (di == 0) ? clearGuide : clearIn;
                    double insetB = (di == nDomains - 1) ? clearGuide : clearIn;
                    double domainPathWidth = wShellList.Count > 0
                        ? Math.Max(0.0, wShellList[di % wShellList.Count])
                        : 0.0;
                    // A printed closed shell begins/ends half a path width away from the
                    // raw guide endpoints. With close_shell off, the same construction is
                    // virtual and intentionally uses the un-inset raw lateral segments.
                    double lateralShellEndInset = closeShell
                        ? 0.5 * domainPathWidth
                        : 0.0;
                    bool geometricClearance = TryCreateClearanceGuidePair(
                        sourceA,
                        sourceB,
                        branchPlanes[bi],
                        insetA,
                        insetB,
                        domainClearLong,
                        lateralShellEndInset,
                        tol,
                        out Curve cA,
                        out Curve cB,
                        out string clearanceNote);
                    double effectiveInsetA = 0.0;
                    double effectiveInsetB = 0.0;
                    if (!geometricClearance)
                    {
                        // The lateral-boundary construction is intentionally the preferred
                        // clear_long method, but it can be impossible when the requested
                        // distance reaches/passes the local domain centre or when a boundary
                        // plane has no stable intersection with a strongly curved guide.
                        // Do not retry that identical failing operation: first retain the
                        // transverse guide offsets, then fall back to the former independent
                        // arc-length end trim. A clearance limitation must never erase an
                        // otherwise valid open layer.
                        bool retainedGuideOffsets = TryCreateClearanceGuidePair(
                            sourceA,
                            sourceB,
                            branchPlanes[bi],
                            insetA,
                            insetB,
                            0.0,
                            lateralShellEndInset,
                            tol,
                            out cA,
                            out cB,
                            out string offsetFallbackNote);
                        if (!retainedGuideOffsets)
                        {
                            cA = sourceA.DuplicateCurve();
                            cB = sourceB.DuplicateCurve();
                            effectiveInsetA = insetA;
                            effectiveInsetB = insetB;
                            if (!string.IsNullOrWhiteSpace(offsetFallbackNote))
                                clearanceNote += $"; guide-offset fallback: {offsetFallbackNote}";
                        }

                        if (domainClearLong > tol)
                        {
                            Curve fallbackA = TrimCurveEnds(cA, domainClearLong, tol);
                            Curve fallbackB = TrimCurveEnds(cB, domainClearLong, tol);
                            if (fallbackA == null || fallbackB == null)
                            {
                                cA = null;
                                cB = null;
                                clearanceNote +=
                                    $"; arc-length fallback {domainClearLong:0.###} removed the domain";
                            }
                            else
                            {
                                cA = fallbackA;
                                cB = fallbackB;
                                clearanceNote += "; used safe arc-length end-trim fallback";
                            }
                        }
                        localLog.AppendLine(
                            $"Branch {brPath} domain [{di}] clearance fallback: {clearanceNote}");
                    }
                    if (cA == null || cB == null || !cA.IsValid || !cB.IsValid)
                    {
                        localLog.AppendLine(
                            $"Branch {brPath} domain [{di}]: clearances removed the complete domain. Skipped.");
                        continue;
                    }

                    double lenA = cA.GetLength();
                    double lenB = cB.GetLength();
                    if (lenA <= tol || lenB <= tol)
                    {
                        localLog.AppendLine(
                            $"Branch {brPath} domain [{di}]: clearance guide too short. Skipped.");
                        continue;
                    }
                    double avgGap = EstimateAverageGap(cA, cB, lenA, lenB);
                    branchTotalArea += avgGap * lenA;
                    localLog.AppendLine(
                        $"Branch {brPath} domain [{di}] guide_lengths: " +
                        $"source={sourceA.GetLength():0.###}/{sourceB.GetLength():0.###} " +
                        $"cleared={lenA:0.###}/{lenB:0.###}");
                    if (!sourceA.IsClosed && !sourceB.IsClosed)
                    {
                        localLog.AppendLine(
                            $"Branch {brPath} domain [{di}] lateral_reference: " +
                            (closeShell
                                ? $"printed shell segments (end inset {lateralShellEndInset:0.###})"
                                : "hypothetical raw-endpoint segments (no shell end inset)"));
                        localLog.AppendLine(
                            $"Branch {brPath} domain [{di}] endpoint_clearance: " +
                            $"A={sourceA.PointAtStart.DistanceTo(cA.PointAtStart):0.###}/" +
                            $"{sourceA.PointAtEnd.DistanceTo(cA.PointAtEnd):0.###} " +
                            $"B={sourceB.PointAtStart.DistanceTo(cB.PointAtStart):0.###}/" +
                            $"{sourceB.PointAtEnd.DistanceTo(cB.PointAtEnd):0.###} " +
                            "(start/end)");
                    }

                    IWasperInfillParams domainDefinition = infillParams[di % infillParams.Count];
                    IReadOnlyList<double> guideSourceA = _guideSourceStations[di];
                    IReadOnlyList<double> guideSourceB = _guideSourceStations[di + 1];
                    IReadOnlyList<double> guideWarpA = GuideWarpForLayer(
                        globalGuideWarpSnapshot,
                        layerGuideWarpSnapshot,
                        bi,
                        di,
                        guideSourceA);
                    IReadOnlyList<double> guideWarpB = GuideWarpForLayer(
                        globalGuideWarpSnapshot,
                        layerGuideWarpSnapshot,
                        bi,
                        di + 1,
                        guideSourceB);
                    if (mixedGuidePair || _hasMixedGuideTopology)
                    {
                        guideWarpA = guideSourceA;
                        guideWarpB = guideSourceB;
                    }
                    if (domainDefinition is WasperTurtleInfillParams turtle)
                    {
                        int made = GenerateTurtleDomain(
                            turtle, cA, cB, lenA, lenB, effectiveInsetA, effectiveInsetB, n01, branchCount,
                            tol, eps, trimLayers, layerPath, brPath, di,
                            localCrvs, localPts, ref branchPrintArea, localLog,
                            guideSourceA, guideWarpA, guideSourceB, guideWarpB);
                        Interlocked.Add(ref totalContours, made);
                        continue;
                    }

                    if (domainDefinition is WasperInfill2DParams infill2D)
                    {
                        int made = GenerateInfill2DDomain(
                            infill2D, cA, cB, lenA, lenB, effectiveInsetA, effectiveInsetB, sampleSpacing,
                            tol, eps, trimLayers, layerPath, brPath, di,
                            localCrvs, localPts, ref branchPrintArea, localLog,
                            guideSourceA, guideWarpA, guideSourceB, guideWarpB);
                        Interlocked.Add(ref totalContours, made);
                        continue;
                    }

                    WasperTpmsInfillParams domParams = (WasperTpmsInfillParams)domainDefinition;
                    int domType = domParams.Type;
                    double domLevel = domParams.Level;
                    double domCx = domParams.CountX;
                    double domCy = domParams.CountY;
                    double domCz = domParams.CountZ;
                    double domPx = domParams.PhaseX;
                    double domPy = domParams.PhaseY;
                    double domPz = domParams.PhaseZ;
                    bool closeTpms = domParams.CloseTpms;
                    bool invertTpms = domParams.InvertTpms;

                    // wrapX (computed above with the clearance) suppresses the along-guide
                    // half of close_tpms: on a loop it would raise a cap wall right across
                    // the seam, splitting a pattern that should run continuously around it.
                    //
                    // Across the seam the TPMS phase advances by 2*PI*domCx, so the
                    // pattern only meets itself when domCx is a whole number of periods.
                    // Snap it and report the adjustment rather than emitting a visible
                    // phase jump at the seam.
                    if (wrapX)
                    {
                        Interlocked.Increment(ref seamlessDomains);
                        double snappedCx = Math.Max(1.0, Math.Round(domCx));
                        if (Math.Abs(snappedCx - domCx) > 1e-9)
                        {
                            localLog.AppendLine(
                                $"Branch {brPath} domain [{di}]: closed guides need a whole number of " +
                                $"periods along the guide. count_x {domCx:0.###} snapped to {snappedCx:0.###}.");
                            Interlocked.Increment(ref cxSnappedDomains);
                            domCx = snappedCx;
                        }
                    }

                    double usableGap = Math.Max(0.0, avgGap - effectiveInsetA - effectiveInsetB);
                    double effectiveSpacing = sampleSpacing;
                    effectiveSpacing = Math.Min(effectiveSpacing, lenA / Math.Max(1.0, domCx * 12.0));
                    if (usableGap > tol)
                        effectiveSpacing = Math.Min(effectiveSpacing, usableGap / Math.Max(1.0, domCy * 12.0));
                    effectiveSpacing = Math.Max(tol, effectiveSpacing);
                    int nx = Clamp((int)Math.Round(lenA / effectiveSpacing), GRID_MIN, GRID_MAX);
                    int ny = Clamp((int)Math.Round(usableGap / effectiveSpacing), GRID_MIN, GRID_MAX);

                    double[,] F = new double[ny + 1, nx + 1];
                    Point3d[,] P3 = new Point3d[ny + 1, nx + 1];
                    var boundaryA = new Point3d[nx + 1];
                    var boundaryB = new Point3d[nx + 1];
                    for (int ix = 0; ix <= nx; ix++)
                    {
                        double s01 = (double)ix / nx;
                        double mappedA = MapGuideWarp(guideSourceA, guideWarpA, s01);
                        double mappedB = MapGuideWarp(guideSourceB, guideWarpB, s01);
                        Point3d pA = PointAtNormalizedLength(cA, lenA, mappedA, tol);
                        Point3d pB = PointAtNormalizedLength(cB, lenB, mappedB, tol);
                        Vector3d across = pB - pA;
                        double gap = across.Length;
                        if (gap > tol)
                        {
                            across.Unitize();
                            double iA = Math.Min(
                                effectiveInsetA,
                                Math.Max(0.0, gap * 0.5 - eps));
                            double iB = Math.Min(
                                effectiveInsetB,
                                Math.Max(0.0, gap * 0.5 - eps));
                            pA += across * iA;
                            pB -= across * iB;
                        }
                        boundaryA[ix] = pA;
                        boundaryB[ix] = pB;
                    }

                    for (int iy = 0; iy <= ny; iy++)
                    {
                        double t01 = (double)iy / (double)ny;
                        double yPhase = TWO_PI * domCy * (t01 + domPy);
                        double zPhase = TWO_PI * domCz * (n01 + domPz);

                        for (int ix = 0; ix <= nx; ix++)
                        {
                            double s01 = (double)ix / (double)nx;
                            Point3d pA = boundaryA[ix];
                            Point3d pB = boundaryB[ix];
                            P3[iy, ix] = pA + (pB - pA) * t01;
                            double xPhase = TWO_PI * domCx * (s01 + domPx);
                            double fieldValue = TPMSValue(domType, xPhase, yPhase, zPhase) - domLevel;
                            if (invertTpms)
                                fieldValue = -fieldValue;
                            if (closeTpms)
                            {
                                // Across the gap the domain always has two real edges.
                                double boundary = Math.Max(-t01 * usableGap, (t01 - 1.0) * usableGap);

                                // Along the guide only an open domain has free ends to cap.
                                if (!wrapX)
                                    boundary = Math.Max(
                                        boundary,
                                        Math.Max(-s01 * lenA, (s01 - 1.0) * lenA));

                                double gradientScale = TWO_PI * Math.Max(
                                    domCx / Math.Max(lenA, tol),
                                    domCy / Math.Max(usableGap, tol));
                                fieldValue = Math.Max(fieldValue, boundary * gradientScale);

                                bool onClosedEdge =
                                    iy == 0 || iy == ny ||
                                    (!wrapX && (ix == 0 || ix == nx));
                                if (onClosedEdge)
                                    fieldValue = Math.Max(fieldValue, 1e-9);
                            }
                            F[iy, ix] = fieldValue;
                        }
                    }

                    // On a loop, column nx is the same station as column 0. Force them to
                    // agree bit-for-bit so the last marching-squares cell straddles the
                    // seam cleanly and ChainSegmentsHashed welds the contour into one
                    // closed polyline instead of leaving a hairline gap.
                    if (wrapX)
                    {
                        for (int iy = 0; iy <= ny; iy++)
                        {
                            P3[iy, nx] = P3[iy, 0];
                            F[iy, nx] = F[iy, 0];
                        }
                    }

                    var segments = new List<(Point3d A, Point3d B)>();
                    for (int iy = 0; iy < ny; iy++)
                    {
                        for (int ix = 0; ix < nx; ix++)
                        {
                            double f0 = F[iy, ix];
                            double f1 = F[iy, ix + 1];
                            double f2 = F[iy + 1, ix + 1];
                            double f3 = F[iy + 1, ix];
                            Point3d p0 = P3[iy, ix];
                            Point3d p1 = P3[iy, ix + 1];
                            Point3d p2 = P3[iy + 1, ix + 1];
                            Point3d p3 = P3[iy + 1, ix];

                            int code = (f0 > 0 ? 1 : 0) | (f1 > 0 ? 2 : 0) | (f2 > 0 ? 4 : 0) | (f3 > 0 ? 8 : 0);
                            if (code == 0 || code == 15) continue;

                            Point3d EP(double fa, double fb, Point3d pa, Point3d pb)
                            {
                                double d = fb - fa;
                                double tt = Math.Abs(d) < 1e-14 ? 0.5 : -fa / d;
                                tt = tt < 0.0 ? 0.0 : tt > 1.0 ? 1.0 : tt;
                                return pa + (pb - pa) * tt;
                            }

                            Point3d eB, eR, eT, eL;
                            switch (code)
                            {
                                case 1: case 14: eB = EP(f0, f1, p0, p1); eL = EP(f0, f3, p0, p3); segments.Add((eB, eL)); break;
                                case 2: case 13: eB = EP(f0, f1, p0, p1); eR = EP(f1, f2, p1, p2); segments.Add((eB, eR)); break;
                                case 3: case 12: eL = EP(f0, f3, p0, p3); eR = EP(f1, f2, p1, p2); segments.Add((eL, eR)); break;
                                case 4: case 11: eR = EP(f1, f2, p1, p2); eT = EP(f3, f2, p3, p2); segments.Add((eR, eT)); break;
                                case 5:
                                    eB = EP(f0, f1, p0, p1);
                                    eR = EP(f1, f2, p1, p2);
                                    eT = EP(f3, f2, p3, p2);
                                    eL = EP(f0, f3, p0, p3);
                                    segments.Add((eB, eL));
                                    segments.Add((eR, eT));
                                    break;
                                case 6: case 9: eB = EP(f0, f1, p0, p1); eT = EP(f3, f2, p3, p2); segments.Add((eB, eT)); break;
                                case 7: case 8: eL = EP(f0, f3, p0, p3); eT = EP(f3, f2, p3, p2); segments.Add((eL, eT)); break;
                                case 10:
                                    eB = EP(f0, f1, p0, p1);
                                    eR = EP(f1, f2, p1, p2);
                                    eT = EP(f3, f2, p3, p2);
                                    eL = EP(f0, f3, p0, p3);
                                    segments.Add((eB, eR));
                                    segments.Add((eT, eL));
                                    break;
                            }
                        }
                    }

                    int contourIndex = 0;
                    foreach (var chain in ChainSegmentsHashed(segments, tol))
                    {
                        if (chain == null || chain.Count < minPts) continue;
                        if (!TryMakeValidPolyline(chain, tol, out Polyline pl)) continue;
                        pl.CollapseShortSegments(tol);
                        if (!pl.IsValid || pl.Count < 2 || pl.Length <= tol) continue;

                        var plc = new PolylineCurve(pl);
                        GH_Path outPath = trimLayers ? layerPath : new GH_Path(brPath.AppendElement(di).AppendElement(contourIndex));
                        localCrvs.Add((outPath, plc));
                        for (int p = 0; p < pl.Count; p++) localPts.Add((outPath, pl[p]));
                        branchPrintArea += pl.Length * effectiveSpacing;
                        contourIndex++;
                        Interlocked.Increment(ref totalContours);
                    }

                    localLog.AppendLine($"Branch {brPath} domain [{di}]: grid {nx}x{ny} type={TPMSTag(domType)} cx={domCx:0.###} cy={domCy:0.###} cz={domCz:0.###} close={closeTpms} invert={invertTpms} wrap_x={wrapX} effective_res={effectiveSpacing:0.###}");
                }

                perBranchPor   [bi] = branchTotalArea > 0.0
                    ? Math.Max(0.0, Math.Min(1.0, 1.0 - branchPrintArea / branchTotalArea))
                    : 0.0;
                perBranchShells[bi] = localShells;
                perBranchShellBases[bi] = localShellBases;
                perBranchParts [bi] = localParts;
                perBranchCurves[bi] = localCrvs;
                perBranchPts   [bi] = localPts;
                perBranchLog   [bi] = localLog.ToString();
            };

            if (branchCount < PARALLEL_THRESHOLD)
            {
                for (int bi = 0; bi < branchCount; bi++) processBranch(bi);
            }
            else
            {
                Parallel.For(0, branchCount, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, bi => processBranch(bi));
            }

            lock (_layerShellCacheLock)
            {
                foreach (int staleIndex in _layerShellCache.Keys.Where(index => index >= branchCount).ToList())
                    _layerShellCache.Remove(staleIndex);
            }

            _layerShellEditorCurves = BuildLayerShellEditorCurves(
                perBranchShellBases,
                guideTree,
                tol);
            _layerShellPreviewCurves = perBranchShellBases
                .Select(layer => (IReadOnlyList<Curve>)(layer ?? new List<PolylineCurve>())
                    .Where(shell => shell != null && shell.IsValid)
                    .Select(shell => shell.DuplicateCurve())
                    .ToList())
                .ToList();
            _layerPartitionEditorCurves = BuildLayerPartitionEditorCurves(
                perBranchParts,
                guideTree,
                tol);
            SelectGuideDisplayLayer(_guideDisplayLayer);

            var outFull   = new DataTree<Curve>();
            var outShells = new DataTree<Curve>();
            var outParts  = new DataTree<Curve>();
            var outCrvs   = new DataTree<Curve>();
            var outPts    = new DataTree<Point3d>();
            var outPlanes = new DataTree<Plane>();
            var porLayer  = new List<double>(branchCount);
            int nonPlanarLayers = 0;
            double maxLayerPlaneDeviation = 0.0;

            for (int bi = 0; bi < branchCount; bi++)
            {
                GH_Path layerPath = trimLayers ? new GH_Path(bi) : guideTree.Paths[bi];
                Plane layerPlane = branchPlanes[bi];
                double layerPlaneDev = WasperLayerPlaneTools.MaxDeviationFromPlane(guideTree.Branches[bi], layerPlane);
                maxLayerPlaneDeviation = Math.Max(maxLayerPlaneDeviation, layerPlaneDev);
                if (layerPlaneDev > WasperLayerPlaneTools.PlanarityWarningTolerance(tol))
                    nonPlanarLayers++;
                Plane outputPlane = CenterPlaneOnFullPathBoundingBox(
                    layerPlane,
                    perBranchShells[bi],
                    perBranchParts[bi],
                    perBranchCurves[bi]);
                outPlanes.Add(outputPlane, layerPath);
                for (int si = 0; si < perBranchShells[bi].Count; si++)
                {
                    Curve shell = perBranchShells[bi][si];
                    Curve shellBase = si < perBranchShellBases[bi].Count
                        ? perBranchShellBases[bi][si]
                        : shell;
                    WasperShellSeamSettings effectiveShellSeam =
                        layerShellSeamSnapshot.TryGetValue(bi, out WasperShellSeamSettings layerSeam)
                            ? layerSeam
                            : shellSeamSnapshot;
                    global::WASPer_3DP.WasperPathRoleMetadata.Set(
                        shell,
                        global::WASPer_3DP.WasperPathRole.Shell);
                    WasperShellSeamMetadata.Set(
                        shell,
                        shellBase,
                        effectiveShellSeam,
                        appliedToGeometry: true);
                    outShells.Add(shell, layerPath);
                    outFull.Add(shell, trimLayers ? new GH_Path(bi) : new GH_Path(bi, 0, si));
                }
                foreach (var item in perBranchParts[bi])
                {
                    global::WASPer_3DP.WasperPathRoleMetadata.Set(
                        item.crv,
                        global::WASPer_3DP.WasperPathRole.Partition);
                    outParts.Add(item.crv, item.path);
                }
                foreach (var item in perBranchCurves[bi])
                {
                    global::WASPer_3DP.WasperPathRoleMetadata.Set(
                        item.crv,
                        global::WASPer_3DP.WasperPathRole.Infill);
                    outCrvs.Add(item.crv, item.path);
                    if (trimLayers)
                    {
                        outFull.Add(item.crv, new GH_Path(bi));
                    }
                    else
                    {
                        int prefixLength = guideTree.Paths[bi].Length;
                        var indices = new List<int> { bi, 1 };
                        for (int pi = prefixLength; pi < item.path.Length; pi++)
                            indices.Add(item.path[pi]);
                        outFull.Add(item.crv, new GH_Path(indices.ToArray()));
                    }
                }
                for (int pi = 0; pi < perBranchParts[bi].Count; pi++)
                    outFull.Add(perBranchParts[bi][pi].crv,
                        trimLayers ? new GH_Path(bi) : new GH_Path(bi, 2, pi));
                foreach (var item in perBranchPts   [bi]) outPts   .Add(item.pt,  item.path);
                porLayer.Add(perBranchPor[bi]);
            }
            double porAvg = porLayer.Count > 0 ? porLayer.Average() : 0.0;

            var infoSb = new StringBuilder();
            infoSb.AppendLine("wsp_In10_Layered Multi-Infill (From Curves)");
            infoSb.AppendLine($"branches_in    : {branchCount}");
            infoSb.AppendLine($"skipped        : {skipped}");
            infoSb.AppendLine($"contours_made  : {totalContours}");
            infoSb.AppendLine($"non_planar_layers : {nonPlanarLayers}");
            infoSb.AppendLine($"seamless_domains  : {seamlessDomains} (closed guides, along-guide closing suppressed)");
            infoSb.AppendLine($"mixed_pair_domains_mapped : {mixedDomainsMapped}");
            infoSb.AppendLine($"mixed_pair_domains_skipped: {mixedDomainsSkipped}");
            infoSb.AppendLine($"guide_editor_topology: {(_hasMixedGuideTopology ? "disabled (Open/Closed transition; input pieces preserved)" : "enabled")}");
            infoSb.AppendLine($"count_x_snapped   : {cxSnappedDomains}");
            infoSb.AppendLine($"max_plane_deviation: {maxLayerPlaneDeviation:0.###}");
            infoSb.AppendLine($"infill_params  : {infillParams.Count}");
            infoSb.AppendLine($"parameter_sets : {BuildListTag(infillParams, p => p.ToString())}");
            infoSb.AppendLine($"clear_long     : {clearLong:0.###}{(clearLongExplicit ? " (explicit)" : " (dynamic default, ignored on closed guides)")}");
            infoSb.AppendLine($"clear_guide    : {clearGuide:0.###}");
            infoSb.AppendLine($"clear_in       : {clearIn:0.###}");
            infoSb.AppendLine("clearance_mode : planar guide offsets + inward offsets of actual lateral shell segments");
            infoSb.AppendLine($"requested_res  : {sampleSpacing:0.###}");
            infoSb.AppendLine($"min_pts        : {minPts}");
            infoSb.AppendLine($"trim_layers    : {trimLayers}");
            infoSb.AppendLine($"close_shell    : {closeShell}");
            infoSb.AppendLine($"shell_cache    : {shellCacheHits}/{branchCount} layer(s) reused");
            infoSb.AppendLine($"guide_editor_update: {(_guideLive ? "live" : _hasPendingGuideUpdate ? "paused (pending)" : "paused")}");
            infoSb.AppendLine($"guide_editor_scope: {_guideLayerScope} [{_guideLayerFrom}..{_guideLayerTo}], display={_guideDisplayLayer}");
            infoSb.AppendLine($"guide_editor_global: {calculationState?.Global?.Count ?? 0} applied guide(s)");
            infoSb.AppendLine($"guide_editor_layer_overrides: {calculationState?.Layers?.Sum(layer => layer.Value?.Count ?? 0) ?? 0} applied layer-guide map(s)");
            infoSb.AppendLine($"guide_editor_auto_reset: {(_guideAutoResetOnLastSolve ? "guide_curves changed - stored warp cleared" : "no change")}");
            int displaySeamLayer = ResolveGuideDisplayLayer();
            WasperShellSeamSettings displayShellSeam =
                calculationState?.ShellLayers != null &&
                calculationState.ShellLayers.TryGetValue(
                    displaySeamLayer,
                    out WasperShellSeamSettings appliedLayerSeam)
                    ? appliedLayerSeam
                    : calculationState?.ShellGlobal ?? new WasperShellSeamSettings();
            infoSb.AppendLine($"shell_seam: u={displayShellSeam.SeamU:0.###} x={displayShellSeam.XSeam} start_in={displayShellSeam.StartOffset:0.###} start_along={displayShellSeam.StartTangentialOffset:0.###} end_in={displayShellSeam.EndOffset:0.###} end_along={displayShellSeam.EndTangentialOffset:0.###} fillet_radius={displayShellSeam.FilletRadius:0.###}");
            infoSb.AppendLine($"shell_seam_layer_overrides: {calculationState?.ShellLayers?.Count ?? 0} applied");
            infoSb.AppendLine($"path_role_metadata: {WasperPathRoleMetadata.RoleKey}");
            infoSb.AppendLine($"shell_seam_metadata: {WasperShellSeamMetadata.MetadataKey} schema={WasperShellSeamMetadata.CurrentSchemaVersion}");
            for (int bi = 0; bi < branchCount; bi++) if (!string.IsNullOrEmpty(perBranchLog[bi])) infoSb.Append(perBranchLog[bi]);

            if (cxSnappedDomains > 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"count_x was snapped to a whole number of periods in {cxSnappedDomains} domain(s). " +
                    "Closed guide curves need an integer count_x so the TPMS pattern meets itself at the seam. " +
                    "Set an integer count_x, or add a clear_long > 0 to open the loop and close the sides instead.");

            if (_hasMixedGuideTopology)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"Guide Editor disabled: corresponding guides change between open and closed topology. " +
                    "Open guide pieces are used directly without cross-layer projection or cropping; " +
                    $"{mixedDomainsMapped} direct Open-Closed pair(s) trim only the closed guide to its matching arc. " +
                    "Stored painting warps are ignored while this mixed layer topology is present.");
            if (mixedDomainsSkipped > 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Could not establish Open-Closed guide correspondence in {mixedDomainsSkipped} domain(s). " +
                    "See info for the per-domain reason.");

            DA.SetDataTree(0, outFull);
            DA.SetDataTree(1, outPlanes);
            DA.SetDataTree(2, outShells);
            DA.SetDataTree(3, outCrvs);
            DA.SetDataTree(4, outParts);
            DA.SetDataTree(5, outPts);
            DA.SetDataList(6, porLayer);
            DA.SetData(7, porAvg);
            DA.SetData(8, infoSb.ToString());
            EstimateGuideDimensions(
                guideTree,
                branchPlanes.FirstOrDefault(plane => plane.IsValid),
                out double dimensionX,
                out double dimensionY,
                out double dimensionZ);
            DA.SetData(
                9,
                new WasperKpiSetGoo(
                    WasperInfillKpiFactory.Create(
                        Name,
                        _versionTag,
                        infillParams.Select(parameters =>
                            WasperInfillKpiFactory.FromParameters(
                                parameters,
                                dimensionX,
                                dimensionY,
                                dimensionZ)),
                        Math.Max(0, _guideDomainCount - 1)),
                    this));
        }

        /// <summary>
        /// Relocates a fitted layer plane's origin to the centre of the bounding box of that
        /// layer's full_path curves (shells + partitions + infill), measured in the plane's own
        /// local X/Y coordinates so the new origin still lies exactly on the fitted plane. Axes
        /// (X/Y/normal) are unchanged; only the origin moves. Falls back to the unmodified source
        /// plane when the plane is invalid or the layer has no full_path geometry (e.g. a skipped
        /// branch), so la_planes always has one valid-or-fallback entry per layer.
        /// </summary>
        private static Plane CenterPlaneOnFullPathBoundingBox(
            Plane sourcePlane,
            List<PolylineCurve> shells,
            List<(GH_Path path, Curve crv)> partitions,
            List<(GH_Path path, PolylineCurve crv)> infillCurves)
        {
            if (!sourcePlane.IsValid)
                return sourcePlane;

            BoundingBox box = BoundingBox.Empty;
            bool any = false;

            void Union(Curve c)
            {
                if (c == null || !c.IsValid) return;
                BoundingBox local = c.GetBoundingBox(sourcePlane);
                if (!local.IsValid) return;
                box = any ? BoundingBox.Union(box, local) : local;
                any = true;
            }

            if (shells != null)
                foreach (PolylineCurve c in shells) Union(c);
            if (partitions != null)
                foreach (var item in partitions) Union(item.crv);
            if (infillCurves != null)
                foreach (var item in infillCurves) Union(item.crv);

            if (!any) return sourcePlane;

            double cx = 0.5 * (box.Min.X + box.Max.X);
            double cy = 0.5 * (box.Min.Y + box.Max.Y);

            Point3d worldCenter = sourcePlane.PointAt(cx, cy);
            return new Plane(worldCenter, sourcePlane.XAxis, sourcePlane.YAxis);
        }

        private static string BuildLayerShellCacheKey(
            GH_Path path,
            IEnumerable<Curve> guideCurves,
            Plane layerPlane,
            double firstWidth,
            int firstCount,
            double lastWidth,
            int lastCount,
            double sampleSpacing,
            bool closeShell,
            WasperShellSeamSettings seam,
            double tolerance)
        {
            WasperCacheSignature signature = WasperCacheSignature.Create();
            signature.Add(path?.ToString());
            signature.Add(layerPlane);
            int curveCount = 0;
            if (guideCurves != null)
            {
                foreach (Curve curve in guideCurves)
                {
                    signature.Add(curve);
                    curveCount++;
                }
            }
            signature.Add(curveCount);
            signature.Add(firstWidth);
            signature.Add(firstCount);
            signature.Add(lastWidth);
            signature.Add(lastCount);
            signature.Add(sampleSpacing);
            signature.Add(closeShell);
            signature.Add(tolerance);
            if (seam != null)
            {
                signature.Add(seam.SeamU);
                signature.Add(seam.XSeam);
                signature.Add(seam.StartOffset);
                signature.Add(seam.StartTangentialOffset);
                signature.Add(seam.EndOffset);
                signature.Add(seam.EndTangentialOffset);
                signature.Add(seam.FilletRadius);
            }
            return signature.Finish();
        }

        private static List<PolylineCurve> DuplicatePolylineCurves(IEnumerable<PolylineCurve> curves)
        {
            return curves == null
                ? new List<PolylineCurve>()
                : curves
                    .Where(curve => curve != null && curve.IsValid)
                    .Select(curve => new PolylineCurve(curve.ToPolyline()))
                    .ToList();
        }

        private sealed class LayerShellCacheEntry
        {
            internal readonly string Key;
            internal readonly List<PolylineCurve> Shells;
            internal readonly List<PolylineCurve> ShellBases;

            internal LayerShellCacheEntry(
                string key,
                IEnumerable<PolylineCurve> shells,
                IEnumerable<PolylineCurve> shellBases)
            {
                Key = key;
                Shells = DuplicatePolylineCurves(shells);
                ShellBases = DuplicatePolylineCurves(shellBases);
            }
        }

        private static void EstimateGuideDimensions(
            GH_Structure<GH_Curve> guideTree,
            Plane referencePlane,
            out double dimensionX,
            out double dimensionY,
            out double dimensionZ)
        {
            dimensionX = 0.0;
            dimensionY = 0.0;
            dimensionZ = 0.0;
            if (guideTree == null || guideTree.DataCount == 0)
                return;

            Plane plane = referencePlane.IsValid ? referencePlane : Plane.WorldXY;
            Transform toLocal = Transform.PlaneToPlane(plane, Plane.WorldXY);
            BoundingBox bounds = BoundingBox.Empty;
            foreach (GH_Curve goo in guideTree.AllData(true))
            {
                Curve source = goo?.Value;
                if (source == null || !source.IsValid)
                    continue;
                Curve local = source.DuplicateCurve();
                if (local == null || !local.Transform(toLocal))
                    continue;
                BoundingBox curveBounds = local.GetBoundingBox(true);
                if (curveBounds.IsValid)
                    bounds.Union(curveBounds);
            }

            if (!bounds.IsValid)
                return;
            dimensionX = Math.Abs(bounds.Max.X - bounds.Min.X);
            dimensionY = Math.Abs(bounds.Max.Y - bounds.Min.Y);
            dimensionZ = Math.Abs(bounds.Max.Z - bounds.Min.Z);
        }

        private static double Wrap01(double value)
        {
            value -= Math.Floor(value);
            if (value >= 1.0) return 0.0;
            if (value < 0.0) return 0.0;
            return value;
        }

        private static double EffectiveRes(double sampleRes, double lenA, double lenB, double tol)
        {
            double len = Math.Max(lenA, lenB);
            double res = sampleRes;
            if (double.IsNaN(res) || double.IsInfinity(res) || res <= tol)
                res = Math.Max(tol * 10.0, len / 63.0);
            return Math.Max(tol * 10.0, res);
        }

        private static Point3d PointAtNormalizedLength(Curve curve, double length, double s01, double tol)
        {
            double clamped = s01 < 0.0 ? 0.0 : s01 > 1.0 ? 1.0 : s01;
            double target = clamped * Math.Max(0.0, length);
            if (!curve.LengthParameter(target, out double t))
                t = curve.Domain.ParameterAt(clamped);
            return curve.PointAt(t);
        }

        /// <summary>
        /// Pairs open shell polylines from two opposite outermost guides into closed loops.
        /// side-0 forward ? end cap ? side-N reversed ? start cap ? close.
        /// Produces Math.Min(side0.Count, sideN.Count) closed curves.
        /// Each successive loop is shorter because it sits further inward (higher shell index).
        /// </summary>
        private static List<List<Point3d>> ChainSegmentsHashed(List<(Point3d A, Point3d B)> segs, double tol)
        {
            var result = new List<List<Point3d>>();
            if (segs == null || segs.Count == 0) return result;

            double invTol = 1.0 / Math.Max(tol, 1e-12);
            long Key(Point3d p)
            {
                long xi = (long)Math.Round(p.X * invTol);
                long yi = (long)Math.Round(p.Y * invTol);
                long zi = (long)Math.Round(p.Z * invTol);
                unchecked { long h = xi * 1000003L + yi; h = h * 1000003L + zi; return h; }
            }

            var endMap = new Dictionary<long, List<int>>(segs.Count * 2);
            void Register(int idx, Point3d pt)
            {
                long k = Key(pt);
                if (!endMap.TryGetValue(k, out var lst))
                    endMap[k] = lst = new List<int>(2);
                lst.Add(idx);
            }
            void Unregister(int idx, Point3d pt)
            {
                long k = Key(pt);
                if (endMap.TryGetValue(k, out var lst))
                {
                    lst.Remove(idx);
                    if (lst.Count == 0)
                        endMap.Remove(k);
                }
            }

            var alive = new bool[segs.Count];
            for (int i = 0; i < segs.Count; i++) { alive[i] = true; Register(i, segs[i].A); Register(i, segs[i].B); }

            int FindNeighbour(Point3d pt)
            {
                long k = Key(pt);
                if (!endMap.TryGetValue(k, out var lst)) return -1;
                for (int j = lst.Count - 1; j >= 0; j--)
                {
                    int idx = lst[j];
                    if (!alive[idx]) { lst.RemoveAt(j); continue; }
                    return idx;
                }
                return -1;
            }

            for (int i = 0; i < segs.Count; i++)
            {
                if (!alive[i]) continue;
                var chain = new List<Point3d> { segs[i].A, segs[i].B };
                alive[i] = false; Unregister(i, segs[i].A); Unregister(i, segs[i].B);

                bool extended = true;
                while (extended)
                {
                    extended = false;
                    Point3d tail = chain[chain.Count - 1];
                    int ni = FindNeighbour(tail);
                    if (ni >= 0)
                    {
                        Point3d a = segs[ni].A; Point3d b = segs[ni].B; alive[ni] = false; Unregister(ni, a); Unregister(ni, b);
                        chain.Add(tail.DistanceToSquared(a) < tail.DistanceToSquared(b) ? b : a);
                        extended = true;
                    }
                    Point3d head = chain[0];
                    ni = FindNeighbour(head);
                    if (ni >= 0)
                    {
                        Point3d a = segs[ni].A; Point3d b = segs[ni].B; alive[ni] = false; Unregister(ni, a); Unregister(ni, b);
                        chain.Insert(0, head.DistanceToSquared(a) < head.DistanceToSquared(b) ? b : a);
                        extended = true;
                    }
                }
                result.Add(chain);
            }

            return result;
        }

        private static bool TryMakeValidPolyline(List<Point3d> pts, double tol, out Polyline pl)
        {
            pl = new Polyline();
            if (pts == null || pts.Count < 2) return false;
            var dedup = new List<Point3d>(pts.Count);
            dedup.Add(pts[0]);
            for (int i = 1; i < pts.Count; i++) if (pts[i].IsValid && pts[i].DistanceTo(dedup[dedup.Count - 1]) > tol) dedup.Add(pts[i]);
            if (dedup.Count < 2) return false;
            pl = new Polyline(dedup);
            return pl.IsValid && pl.Count >= 2 && pl.Length > tol;
        }

        private static double TPMSValue(int type, double x, double y, double z)
        {
            switch (type)
            {
                case 0: return Math.Cos(x) + Math.Cos(y) + Math.Cos(z);
                case 1: return Math.Sin(x) * Math.Sin(y) * Math.Sin(z) + Math.Sin(x) * Math.Cos(y) * Math.Cos(z) + Math.Cos(x) * Math.Sin(y) * Math.Cos(z) + Math.Cos(x) * Math.Cos(y) * Math.Sin(z);
                case 2: return Math.Sin(x) * Math.Cos(y) + Math.Sin(y) * Math.Cos(z) + Math.Sin(z) * Math.Cos(x);
                case 3: return -2.0 * (Math.Cos(x) * Math.Cos(y) + Math.Cos(y) * Math.Cos(z) + Math.Cos(z) * Math.Cos(x)) + (Math.Cos(2 * x) + Math.Cos(2 * y) + Math.Cos(2 * z));
                case 4: return 3.0 * (Math.Cos(x) + Math.Cos(y) + Math.Cos(z)) + 4.0 * Math.Cos(x) * Math.Cos(y) * Math.Cos(z);
                case 5: return 0.5 * (Math.Sin(2 * x) * Math.Cos(y) * Math.Sin(z) + Math.Sin(2 * y) * Math.Cos(z) * Math.Sin(x) + Math.Sin(2 * z) * Math.Cos(x) * Math.Sin(y)) - 0.5 * (Math.Cos(2 * x) * Math.Cos(2 * y) + Math.Cos(2 * y) * Math.Cos(2 * z) + Math.Cos(2 * z) * Math.Cos(2 * x));
                case 6: return Math.Sin(x) * Math.Cos(y) * Math.Cos(2 * z) + Math.Cos(2 * x) * Math.Sin(y) * Math.Cos(z) + Math.Cos(x) * Math.Cos(2 * y) * Math.Sin(z);
                case 7: return Math.Sin(x) * Math.Sin(y) * Math.Sin(z) + Math.Cos(x) * Math.Cos(y) * Math.Cos(z) + Math.Sin(2 * x) * Math.Sin(y) + Math.Cos(x) * Math.Sin(2 * y) + Math.Sin(x) * Math.Sin(2 * z) + Math.Sin(2 * x) * Math.Cos(z) + Math.Sin(2 * y) * Math.Sin(z) + Math.Cos(y) * Math.Sin(2 * z);
                default: return 0.0;
            }
        }

        private static string TPMSTag(int t)
        {
            switch (t)
            {
                case 0: return "Prim";
                case 1: return "Diam";
                case 2: return "Gyr";
                case 3: return "IWP";
                case 4: return "Neo";
                case 5: return "Lidi";
                case 6: return "FK-S";
                case 7: return "FK-Y";
                default: return "?";
            }
        }

        private static string ParamTag(IWasperInfillParams p)
        {
            if (p is WasperTpmsInfillParams tpms)
                return TPMSTag(tpms.Type);
            if (p is WasperInfill2DParams infill2D)
                return $"2D:{WasperInfill2DParams.Tag(infill2D.Type)}";
            return p?.InfillKind ?? "?";
        }

        private static string BuildListTag<T>(List<T> list, Func<T, string> fmt)
        {
            if (list == null || list.Count == 0) return "-";
            if (list.Count == 1) return fmt(list[0]);
            var sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(fmt(list[i]));
            }
            sb.Append("]");
            return sb.ToString();
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString(
                GuideWarpStorageKey,
                JsonConvert.SerializeObject(_guideWarp.Snapshot()));
            writer.SetString(
                GuideDensityStorageKey,
                JsonConvert.SerializeObject(_guideDensityOverrides));
            writer.SetString(
                GuideLayerWarpStorageKey,
                JsonConvert.SerializeObject(_layerGuideWarps));
            writer.SetInt32(GuideScopeStorageKey, (int)_guideLayerScope);
            writer.SetInt32(GuideScopeFromStorageKey, _guideLayerFrom);
            writer.SetInt32(GuideScopeToStorageKey, _guideLayerTo);
            writer.SetInt32(GuideDisplayLayerStorageKey, _guideDisplayLayer);
            writer.SetString(ShellSeamStorageKey, JsonConvert.SerializeObject(_shellSeam));
            writer.SetString(
                LayerShellSeamStorageKey,
                JsonConvert.SerializeObject(_layerShellSeams));
            writer.SetBoolean(GuideLiveStorageKey, _guideLive);
            if (_appliedGuideStateInitialized && _appliedGuideState != null)
                writer.SetString(
                    AppliedGuideStateStorageKey,
                    JsonConvert.SerializeObject(_appliedGuideState));
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            bool result = base.Read(reader);
            if (reader.ItemExists(GuideWarpStorageKey))
            {
                try
                {
                    _guideWarp.Restore(JsonConvert.DeserializeObject<Dictionary<int, List<double>>>(
                        reader.GetString(GuideWarpStorageKey)));
                }
                catch
                {
                    _guideWarp.Restore(null);
                }
            }
            _guideDensityOverrides.Clear();
            if (reader.ItemExists(GuideDensityStorageKey))
            {
                try
                {
                    Dictionary<int, int> density =
                        JsonConvert.DeserializeObject<Dictionary<int, int>>(
                            reader.GetString(GuideDensityStorageKey));
                    if (density != null)
                        foreach (KeyValuePair<int, int> pair in density)
                            if (pair.Key >= 0)
                                _guideDensityOverrides[pair.Key] = Clamp(pair.Value, 1, 32);
                }
                catch
                {
                    _guideDensityOverrides.Clear();
                }
            }
            _layerGuideWarps.Clear();
            if (reader.ItemExists(GuideLayerWarpStorageKey))
            {
                try
                {
                    Dictionary<int, Dictionary<int, List<double>>> layers =
                        JsonConvert.DeserializeObject<Dictionary<int, Dictionary<int, List<double>>>>(
                            reader.GetString(GuideLayerWarpStorageKey));
                    if (layers != null)
                        foreach (KeyValuePair<int, Dictionary<int, List<double>>> layer in layers)
                            if (layer.Key >= 0 && layer.Value != null)
                                _layerGuideWarps[layer.Key] = layer.Value.ToDictionary(
                                    pair => pair.Key,
                                    pair => pair.Value?.ToList() ?? new List<double>());
                }
                catch
                {
                    _layerGuideWarps.Clear();
                }
            }
            if (reader.ItemExists(GuideScopeStorageKey))
                _guideLayerScope = (WasperGuideLayerScope)Clamp(
                    reader.GetInt32(GuideScopeStorageKey), 0, 2);
            _guideLayerFrom = reader.ItemExists(GuideScopeFromStorageKey)
                ? reader.GetInt32(GuideScopeFromStorageKey)
                : 0;
            _guideLayerTo = reader.ItemExists(GuideScopeToStorageKey)
                ? reader.GetInt32(GuideScopeToStorageKey)
                : 0;
            _guideDisplayLayer = reader.ItemExists(GuideDisplayLayerStorageKey)
                ? reader.GetInt32(GuideDisplayLayerStorageKey)
                : 0;
            _shellSeam = new WasperShellSeamSettings();
            if (reader.ItemExists(ShellSeamStorageKey))
            {
                try
                {
                    _shellSeam = JsonConvert.DeserializeObject<WasperShellSeamSettings>(
                        reader.GetString(ShellSeamStorageKey)) ?? new WasperShellSeamSettings();
                }
                catch { _shellSeam = new WasperShellSeamSettings(); }
            }
            _layerShellSeams.Clear();
            if (reader.ItemExists(LayerShellSeamStorageKey))
            {
                try
                {
                    Dictionary<int, WasperShellSeamSettings> layers =
                        JsonConvert.DeserializeObject<Dictionary<int, WasperShellSeamSettings>>(
                            reader.GetString(LayerShellSeamStorageKey));
                    if (layers != null)
                        foreach (KeyValuePair<int, WasperShellSeamSettings> layer in layers)
                            if (layer.Key >= 0 && layer.Value != null)
                                _layerShellSeams[layer.Key] = layer.Value;
                }
                catch { _layerShellSeams.Clear(); }
            }
            _guideLive = !reader.ItemExists(GuideLiveStorageKey) ||
                reader.GetBoolean(GuideLiveStorageKey);
            _appliedGuideState = null;
            _appliedGuideStateInitialized = false;
            if (reader.ItemExists(AppliedGuideStateStorageKey))
            {
                try
                {
                    _appliedGuideState = CloneGuideWarpHistory(
                        JsonConvert.DeserializeObject<GuideWarpHistorySnapshot>(
                            reader.GetString(AppliedGuideStateStorageKey)));
                    _appliedGuideStateInitialized = _appliedGuideState != null;
                }
                catch
                {
                    _appliedGuideState = null;
                    _appliedGuideStateInitialized = false;
                }
            }
            if (!_appliedGuideStateInitialized)
                ApplyCurrentGuideState();
            _hasPendingGuideUpdate = !_guideLive &&
                !GuideWarpHistoriesEqual(
                    CaptureGuideWarpHistory(),
                    _appliedGuideState);
            UpdateLiveMessage();
            _guideVisualRevision++;
            return result;
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            if (_guideConduit != null)
                _guideConduit.Enabled = false;
            if (_guideEditor != null && !_guideEditor.IsClosed)
                _guideEditor.Close();
            base.RemovedFromDocument(document);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }
    }

    internal sealed class In10v2GuideConduit : Rhino.Display.DisplayConduit
    {
        private readonly wsp_In10_Layered_Multi_Infill_From_Curves _component;

        internal In10v2GuideConduit(
            wsp_In10_Layered_Multi_Infill_From_Curves component)
        {
            _component = component;
        }

        protected override void CalculateBoundingBox(
            Rhino.Display.CalculateBoundingBoxEventArgs e)
        {
            BoundingBox bounds = _component != null
                ? _component.GuideEditorReferenceBoundingBox()
                : BoundingBox.Empty;
            if (bounds.IsValid)
                e.IncludeBoundingBox(bounds);
        }

        protected override void PostDrawObjects(Rhino.Display.DrawEventArgs e)
        {
            _component?.DrawGuideEditorReferences(e.Display);
        }
    }

    internal sealed class In10v2GuideEditorAttributes : GH_ComponentAttributes
    {
        private RectangleF _buttonBounds;
        private bool _pressed;
        private wsp_In10_Layered_Multi_Infill_From_Curves Component =>
            Owner as wsp_In10_Layered_Multi_Infill_From_Curves;

        internal In10v2GuideEditorAttributes(
            wsp_In10_Layered_Multi_Infill_From_Curves owner)
            : base(owner)
        {
        }

        protected override void Layout()
        {
            base.Layout();
            Rectangle bounds = GH_Convert.ToRectangle(Bounds);
            _buttonBounds = new RectangleF(
                bounds.X + 3,
                bounds.Bottom,
                bounds.Width - 6,
                18);
            bounds.Height += 21;
            Bounds = bounds;
        }

        protected override void Render(
            GH_Canvas canvas,
            Graphics graphics,
            GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects)
                return;
            bool disabled = Owner.Locked || Component == null || !Component.CanOpenGuideEditor;
            using GH_Capsule capsule = GH_Capsule.CreateTextCapsule(
                _buttonBounds,
                _buttonBounds,
                GH_Palette.Black,
                "Open Guide Editor",
                GH_FontServer.StandardAdjusted,
                3,
                _pressed ? 0 : 8);
            capsule.Render(graphics, false, disabled, false);
        }

        public override GH_ObjectResponse RespondToMouseDown(
            GH_Canvas sender,
            GH_CanvasMouseEvent e)
        {
            if (!Owner.Locked && Component != null && Component.CanOpenGuideEditor &&
                e.Button == MouseButtons.Left && _buttonBounds.Contains(e.CanvasLocation))
            {
                _pressed = true;
                sender.Invalidate();
                return GH_ObjectResponse.Capture;
            }
            return base.RespondToMouseDown(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(
            GH_Canvas sender,
            GH_CanvasMouseEvent e)
        {
            if (!_pressed)
                return base.RespondToMouseUp(sender, e);
            _pressed = false;
            sender.Invalidate();
            if (_buttonBounds.Contains(e.CanvasLocation))
                Component?.ToggleGuideEditor();
            return GH_ObjectResponse.Release;
        }
    }

    /// <summary>
    /// Hidden compatibility alias for Grasshopper definitions saved while the
    /// interactive implementation temporarily used a separate v2 GUID. Both
    /// GUIDs now resolve to the same enhanced In10 implementation.
    /// </summary>
    public sealed class wsp_In10_Layered_Multi_Infill_From_Curves_v2_Compatibility :
        wsp_In10_Layered_Multi_Infill_From_Curves
    {
        public override Guid ComponentGuid =>
            new Guid("8BF5F4CE-E69F-48F5-BBFF-F4B1A68C9E4D");

        public override GH_Exposure Exposure => GH_Exposure.hidden;
    }
}
