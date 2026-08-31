using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

using Newtonsoft.Json;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

using WASPer_3DP.PatternEditing;

namespace WASPer_3DP.Components._4_0_Print_Paths
{
    public partial class wsp_Pp01_PathsFromCurves_v2 : IWasperShellSeamEditorHost
    {
        private const string SeamOverridesKey = "WASPer.Pp01v3.ShellSeamOverrides";
        private const string SeamScopeKey = "WASPer.Pp01v3.ShellSeamScope";
        private const string SeamFromKey = "WASPer.Pp01v3.ShellSeamFrom";
        private const string SeamToKey = "WASPer.Pp01v3.ShellSeamTo";
        private const string SeamDisplayKey = "WASPer.Pp01v3.ShellSeamDisplay";
        private const string SeamOverrideSourcesKey = "WASPer.Pp01v3.ShellSeamOverrideSources";

        private readonly Dictionary<int, WasperShellSeamSettings> _seamOverrides =
            new Dictionary<int, WasperShellSeamSettings>();
        private readonly Dictionary<int, string> _seamOverrideSources =
            new Dictionary<int, string>();
        private readonly Dictionary<int, string> _seamInputSignatures =
            new Dictionary<int, string>();
        private readonly Dictionary<int, WasperShellSeamSettings> _seamDefaults =
            new Dictionary<int, WasperShellSeamSettings>();
        private IReadOnlyList<IReadOnlyList<IReadOnlyList<PointF>>> _shellEditorLayers =
            Array.Empty<IReadOnlyList<IReadOnlyList<PointF>>>();
        private IReadOnlyList<IReadOnlyList<IReadOnlyList<PointF>>> _partitionEditorLayers =
            Array.Empty<IReadOnlyList<IReadOnlyList<PointF>>>();
        private IReadOnlyList<IReadOnlyList<Curve>> _shellBaseLayers3d =
            Array.Empty<IReadOnlyList<Curve>>();
        private IReadOnlyList<IReadOnlyList<Curve>> _partitionLayers3d =
            Array.Empty<IReadOnlyList<Curve>>();
        private IReadOnlyList<Plane> _seamLayerPlanes = Array.Empty<Plane>();
        private WasperEtoShellSeamEditorForm _seamEditor;
        private Pp01SeamConduit _seamConduit;
        private WasperGuideLayerScope _seamScope = WasperGuideLayerScope.All;
        private int _seamFrom;
        private int _seamTo;
        private int _seamDisplay;
        private int _seamRevision;
        private Dictionary<int, WasperShellSeamSettings> _seamEditBefore;

        public override void CreateAttributes()
        {
            m_attributes = new Pp01SeamEditorAttributes(this);
        }

        internal bool CanOpenSeamEditor => GuideLayerCount > 0 &&
            _shellEditorLayers.Any(layer => layer != null && layer.Count > 0);

        internal void ToggleSeamEditor()
        {
            if (_seamEditor != null && !_seamEditor.IsClosed)
            {
                _seamEditor.ActivateEditor();
                return;
            }
            if (!CanOpenSeamEditor)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "No Shell curves are available. Assign WASPer.PathRole=Shell before Pp01 to enable seam editing.");
                return;
            }
            try
            {
                _seamEditor = new WasperEtoShellSeamEditorForm(this);
                _seamConduit = new Pp01SeamConduit(this) { Enabled = true };
                _seamEditor.ActivateEditor();
                RhinoDoc.ActiveDoc?.Views.Redraw();
            }
            catch (Exception exception)
            {
                _seamEditor = null;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Could not open Shell Seam Editor: {exception.Message}");
            }
        }

        private int ApplySeamMetadataToLayers(
            SortedDictionary<int, List<LayerCurveInput>> layers,
            Dictionary<int, Plane> suppliedPlanes,
            Plane referencePlane,
            double tolerance)
        {
            var shellLayers = new List<IReadOnlyList<IReadOnlyList<PointF>>>();
            var partitionLayers = new List<IReadOnlyList<IReadOnlyList<PointF>>>();
            var shellLayers3d = new List<IReadOnlyList<Curve>>();
            var partitionLayers3d = new List<IReadOnlyList<Curve>>();
            var layerPlanes = new List<Plane>();
            _seamDefaults.Clear();
            int metadataCount = 0;
            int order = 0;
            foreach (KeyValuePair<int, List<LayerCurveInput>> layer in layers)
            {
                string seamInputSignature = BuildSeamInputSignature(
                    layer.Value,
                    out bool hasIncomingSeamMetadata);
                _seamInputSignatures[order] = seamInputSignature;
                if (_seamOverrides.ContainsKey(order))
                {
                    bool tracked = _seamOverrideSources.TryGetValue(order, out string sourceSignature);
                    if ((tracked && !string.Equals(sourceSignature, seamInputSignature, StringComparison.Ordinal)) ||
                        (!tracked && hasIncomingSeamMetadata))
                    {
                        // A Pp01 override is only valid against the upstream seam state
                        // from which it was edited. In10 changes must become the new
                        // baseline instead of being hidden by a stale saved override.
                        _seamOverrides.Remove(order);
                        _seamOverrideSources.Remove(order);
                    }
                }

                var shellBases = new List<Curve>();
                var partitions = new List<Curve>();
                Plane plane = suppliedPlanes != null && suppliedPlanes.TryGetValue(layer.Key, out Plane supplied) && supplied.IsValid
                    ? StableXYPlane(supplied, referencePlane)
                    : FitLayerPlane(layer.Value, referencePlane);
                WasperShellSeamSettings firstSettings = null;

                foreach (LayerCurveInput input in layer.Value)
                {
                    if (input?.Curve == null || !input.Curve.IsValid)
                        continue;
                    if (input.Role == WasperPathRole.Partition)
                    {
                        partitions.Add(input.Curve.DuplicateCurve());
                        continue;
                    }
                    if (input.Role != WasperPathRole.Shell)
                        continue;

                    bool hasRecord = WasperShellSeamMetadata.TryGet(
                        input.Curve,
                        out WasperShellSeamRecord record);
                    Curve canonical = hasRecord
                        ? record.CreateBaseCurve()
                        : input.Curve.DuplicateCurve();
                    if (canonical == null || !canonical.IsValid)
                        canonical = input.Curve.DuplicateCurve();
                    WasperShellSeamSettings inherited = hasRecord
                        ? record.ToSettings()
                        : new WasperShellSeamSettings();
                    if (firstSettings == null)
                        firstSettings = inherited.Clone();
                    WasperShellSeamSettings active = _seamOverrides.TryGetValue(order, out WasperShellSeamSettings overridden)
                        ? overridden
                        : inherited;
                    PolylineCurve effective = WasperShellSeamMetadata.Apply(
                        canonical,
                        active,
                        plane,
                        tolerance);
                    if (effective != null && effective.IsValid)
                    {
                        WasperPathRoleMetadata.Set(effective, WasperPathRole.Shell);
                        WasperShellSeamMetadata.Set(effective, canonical, active, true);
                        input.Curve = effective;
                        metadataCount++;
                    }
                    shellBases.Add(canonical.DuplicateCurve());
                }

                _seamDefaults[order] = firstSettings ?? new WasperShellSeamSettings();
                shellLayers.Add(ProjectCurves(shellBases, plane));
                partitionLayers.Add(ProjectCurves(partitions, plane));
                shellLayers3d.Add(shellBases.Select(curve => curve.DuplicateCurve()).ToList());
                partitionLayers3d.Add(partitions.Select(curve => curve.DuplicateCurve()).ToList());
                layerPlanes.Add(plane);
                order++;
            }

            foreach (int staleLayer in _seamInputSignatures.Keys.Where(key => key >= order).ToList())
                _seamInputSignatures.Remove(staleLayer);
            foreach (int staleLayer in _seamOverrideSources.Keys.Where(key => key >= order).ToList())
                _seamOverrideSources.Remove(staleLayer);

            _shellEditorLayers = shellLayers;
            _partitionEditorLayers = partitionLayers;
            _shellBaseLayers3d = shellLayers3d;
            _partitionLayers3d = partitionLayers3d;
            _seamLayerPlanes = layerPlanes;
            NormalizeScope(order);
            _seamRevision++;
            if (_seamConduit?.Enabled == true) RhinoDoc.ActiveDoc?.Views.Redraw();
            return metadataCount;
        }

        private static string BuildSeamInputSignature(
            IEnumerable<LayerCurveInput> inputs,
            out bool hasMetadata)
        {
            global::WASPer_3DP.WasperCacheSignature signature =
                global::WASPer_3DP.WasperCacheSignature.Create();
            hasMetadata = false;
            int shellCount = 0;
            foreach (LayerCurveInput input in inputs ?? Enumerable.Empty<LayerCurveInput>())
            {
                if (input?.Curve == null || input.Role != WasperPathRole.Shell)
                    continue;
                shellCount++;
                signature.Add(input.Curve);
                string metadata = input.Curve.GetUserString(WasperShellSeamMetadata.MetadataKey);
                signature.Add(metadata);
                hasMetadata |= !string.IsNullOrWhiteSpace(metadata);
            }
            signature.Add(shellCount);
            return signature.Finish();
        }

        private static Plane FitLayerPlane(IEnumerable<LayerCurveInput> inputs, Plane fallback)
        {
            var points = new List<Point3d>();
            foreach (LayerCurveInput input in inputs ?? Enumerable.Empty<LayerCurveInput>())
            {
                Curve curve = input?.Curve;
                if (curve == null || !curve.IsValid) continue;
                if (curve.TryGetPolyline(out Polyline polyline))
                    points.AddRange(polyline);
                else
                {
                    points.Add(curve.PointAtStart);
                    points.Add(curve.PointAtNormalizedLength(0.5));
                    points.Add(curve.PointAtEnd);
                }
            }
            if (points.Count >= 3 && Plane.FitPlaneToPoints(points, out Plane fitted) == PlaneFitResult.Success)
            {
                if (fitted.Normal * fallback.Normal < 0.0) fitted.Flip();
                return StableXYPlane(fitted, fallback);
            }
            return StableXYPlane(fallback, Plane.WorldXY);
        }

        private static Plane StableXYPlane(Plane plane, Plane fallback)
        {
            if (!plane.IsValid) plane = fallback.IsValid ? fallback : Plane.WorldXY;
            Vector3d normal = plane.Normal;
            if (!normal.Unitize()) normal = Vector3d.ZAxis;
            Vector3d x = Vector3d.XAxis - Vector3d.Multiply(Vector3d.XAxis * normal, normal);
            if (!x.Unitize())
            {
                x = Vector3d.YAxis - Vector3d.Multiply(Vector3d.YAxis * normal, normal);
                if (!x.Unitize()) x = plane.XAxis;
            }
            Vector3d y = Vector3d.CrossProduct(normal, x);
            if (!y.Unitize()) y = plane.YAxis;
            return new Plane(plane.Origin, x, y);
        }

        private static IReadOnlyList<IReadOnlyList<PointF>> ProjectCurves(
            IEnumerable<Curve> curves,
            Plane plane)
        {
            var result = new List<IReadOnlyList<PointF>>();
            foreach (Curve curve in curves ?? Enumerable.Empty<Curve>())
            {
                if (curve == null || !curve.IsValid) continue;
                Polyline polyline;
                if (!curve.TryGetPolyline(out polyline))
                {
                    polyline = new Polyline();
                    for (int i = 0; i <= 64; i++)
                        polyline.Add(curve.PointAtNormalizedLength(i / 64.0));
                }
                var projected = new List<PointF>(polyline.Count);
                foreach (Point3d point in polyline)
                {
                    plane.ClosestParameter(point, out double x, out double y);
                    // Keep the same layer-plane coordinates used by In10. The
                    // shared canvas performs the single screen-Y inversion when
                    // mapping these model coordinates into WinForms pixels.
                    projected.Add(new PointF((float)x, (float)y));
                }
                if (projected.Count >= 2) result.Add(projected);
            }
            return result;
        }

        private WasperShellSeamSettings SettingsForLayer(int layer)
        {
            if (_seamOverrides.TryGetValue(layer, out WasperShellSeamSettings value))
                return value;
            if (_seamDefaults.TryGetValue(layer, out value))
                return value;
            return new WasperShellSeamSettings();
        }

        private IEnumerable<int> ScopedLayers()
        {
            int count = GuideLayerCount;
            if (_seamScope == WasperGuideLayerScope.All)
                return Enumerable.Range(0, count);
            if (_seamScope == WasperGuideLayerScope.Single)
                return count > 0 ? new[] { Math.Max(0, Math.Min(count - 1, _seamDisplay)) } : Array.Empty<int>();
            int from = Math.Max(0, Math.Min(count - 1, Math.Min(_seamFrom, _seamTo)));
            int to = Math.Max(0, Math.Min(count - 1, Math.Max(_seamFrom, _seamTo)));
            return count > 0 ? Enumerable.Range(from, to - from + 1) : Array.Empty<int>();
        }

        private void ChangeScopedSettings(Action<WasperShellSeamSettings> change, bool solve)
        {
            foreach (int layer in ScopedLayers())
            {
                WasperShellSeamSettings settings = SettingsForLayer(layer).Clone();
                change(settings);
                _seamOverrides[layer] = settings;
                if (_seamInputSignatures.TryGetValue(layer, out string inputSignature))
                    _seamOverrideSources[layer] = inputSignature;
            }
            _seamRevision++;
            if (_seamConduit?.Enabled == true) RhinoDoc.ActiveDoc?.Views.Redraw();
            if (solve) ExpireSolution(true);
        }

        private void NormalizeScope(int count)
        {
            int max = Math.Max(0, count - 1);
            _seamFrom = Math.Max(0, Math.Min(max, _seamFrom));
            _seamTo = Math.Max(0, Math.Min(max, _seamTo));
            _seamDisplay = Math.Max(0, Math.Min(max, _seamDisplay));
            if (_seamFrom > _seamTo) (_seamFrom, _seamTo) = (_seamTo, _seamFrom);
        }

        private static Dictionary<int, WasperShellSeamSettings> CloneSettings(
            Dictionary<int, WasperShellSeamSettings> source) =>
            source.ToDictionary(pair => pair.Key, pair => pair.Value?.Clone() ?? new WasperShellSeamSettings());

        private void WriteSeamEditorState(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString(SeamOverridesKey, JsonConvert.SerializeObject(_seamOverrides));
            writer.SetString(
                SeamOverrideSourcesKey,
                JsonConvert.SerializeObject(_seamOverrideSources));
            writer.SetInt32(SeamScopeKey, (int)_seamScope);
            writer.SetInt32(SeamFromKey, _seamFrom);
            writer.SetInt32(SeamToKey, _seamTo);
            writer.SetInt32(SeamDisplayKey, _seamDisplay);
        }

        private void ReadSeamEditorState(GH_IO.Serialization.GH_IReader reader)
        {
            _seamOverrides.Clear();
            _seamOverrideSources.Clear();
            if (reader.ItemExists(SeamOverridesKey))
            {
                try
                {
                    var values = JsonConvert.DeserializeObject<Dictionary<int, WasperShellSeamSettings>>(
                        reader.GetString(SeamOverridesKey));
                    if (values != null)
                        foreach (var pair in values)
                            if (pair.Key >= 0 && pair.Value != null)
                                _seamOverrides[pair.Key] = pair.Value;
                }
                catch { }
            }
            if (reader.ItemExists(SeamOverrideSourcesKey))
            {
                try
                {
                    var values = JsonConvert.DeserializeObject<Dictionary<int, string>>(
                        reader.GetString(SeamOverrideSourcesKey));
                    if (values != null)
                        foreach (var pair in values)
                            if (pair.Key >= 0 && !string.IsNullOrWhiteSpace(pair.Value))
                                _seamOverrideSources[pair.Key] = pair.Value;
                }
                catch { }
            }
            if (reader.ItemExists(SeamScopeKey)) _seamScope = (WasperGuideLayerScope)reader.GetInt32(SeamScopeKey);
            if (reader.ItemExists(SeamFromKey)) _seamFrom = reader.GetInt32(SeamFromKey);
            if (reader.ItemExists(SeamToKey)) _seamTo = reader.GetInt32(SeamToKey);
            if (reader.ItemExists(SeamDisplayKey)) _seamDisplay = reader.GetInt32(SeamDisplayKey);
        }

        string IWasperShellSeamEditorHost.GuideEditorTitle => "WASPer Pp01 Shell Seam Editor";
        int IWasperShellSeamEditorHost.GuideVisualRevision => _seamRevision;
        IReadOnlyList<IReadOnlyList<PointF>> IWasperShellSeamEditorHost.ShellEditorCurves =>
            _seamDisplay >= 0 && _seamDisplay < _shellEditorLayers.Count
                ? _shellEditorLayers[_seamDisplay]
                : Array.Empty<IReadOnlyList<PointF>>();
        IReadOnlyList<IReadOnlyList<PointF>> IWasperShellSeamEditorHost.ShellPartitionEditorCurves =>
            _seamDisplay >= 0 && _seamDisplay < _partitionEditorLayers.Count
                ? _partitionEditorLayers[_seamDisplay]
                : Array.Empty<IReadOnlyList<PointF>>();
        WasperShellSeamSettings IWasperShellSeamEditorHost.ShellSeamSettings => SettingsForLayer(_seamDisplay);
        int IWasperShellSeamEditorHost.GuideLayerCount => _shellEditorLayers.Count;
        WasperGuideLayerScope IWasperShellSeamEditorHost.GuideLayerScope => _seamScope;
        int IWasperShellSeamEditorHost.GuideLayerFrom => _seamFrom;
        int IWasperShellSeamEditorHost.GuideLayerTo => _seamTo;
        int IWasperShellSeamEditorHost.GuideDisplayLayer => _seamDisplay;

        void IWasperShellSeamEditorHost.SetGuideLayerScope(WasperGuideLayerScope scope, int from, int to, int display)
        {
            _seamScope = Enum.IsDefined(typeof(WasperGuideLayerScope), scope) ? scope : WasperGuideLayerScope.All;
            _seamFrom = from; _seamTo = to; _seamDisplay = display;
            NormalizeScope(GuideLayerCount);
            _seamRevision++;
            if (_seamConduit?.Enabled == true) RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        void IWasperShellSeamEditorHost.GuideEditorClosed()
        {
            _seamEditor = null;
            if (_seamConduit != null) _seamConduit.Enabled = false;
            _seamConduit = null;
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }
        void IWasperShellSeamEditorHost.BeginShellSeamEdit()
        {
            if (_seamEditBefore != null) return;
            RecordUndoEvent("Edit shell seam");
            _seamEditBefore = CloneSettings(_seamOverrides);
        }
        void IWasperShellSeamEditorHost.PreviewShellSeam(double seamU) =>
            ChangeScopedSettings(settings => settings.SeamU = seamU, false);
        void IWasperShellSeamEditorHost.PreviewShellOffset(bool start, double inward, double tangent) =>
            ChangeScopedSettings(settings =>
            {
                if (start) { settings.StartOffset = inward; settings.StartTangentialOffset = tangent; }
                else { settings.EndOffset = inward; settings.EndTangentialOffset = tangent; }
            }, false);
        void IWasperShellSeamEditorHost.CommitShellSeamEdit()
        {
            _seamEditBefore = null;
            ExpireSolution(true);
        }
        void IWasperShellSeamEditorHost.CancelShellSeamEdit()
        {
            if (_seamEditBefore == null) return;
            _seamOverrides.Clear();
            foreach (var pair in _seamEditBefore) _seamOverrides[pair.Key] = pair.Value;
            _seamEditBefore = null;
            _seamRevision++;
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }
        void IWasperShellSeamEditorHost.SetShellXSeam(bool enabled)
        {
            RecordUndoEvent("Toggle X seam");
            ChangeScopedSettings(settings => settings.XSeam = enabled, true);
        }
        void IWasperShellSeamEditorHost.SetShellFilletRadius(double radius)
        {
            RecordUndoEvent("Set seam fillet radius");
            ChangeScopedSettings(settings => settings.FilletRadius = Math.Max(0.0, radius), true);
        }
        void IWasperShellSeamEditorHost.ResetShellSeam()
        {
            RecordUndoEvent("Reset shell seam to input metadata");
            foreach (int layer in ScopedLayers())
            {
                // Pp01 is an editor layered over the seam intent arriving from In10.
                // Resetting must reveal that inherited intent; storing a zero-valued
                // override here would keep masking all later In10 seam changes.
                _seamOverrides.Remove(layer);
                _seamOverrideSources.Remove(layer);
            }
            _seamRevision++;
            if (_seamConduit?.Enabled == true) RhinoDoc.ActiveDoc?.Views.Redraw();
            ExpireSolution(true);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            if (_seamEditor != null && !_seamEditor.IsClosed) _seamEditor.Close();
            if (_seamConduit != null) _seamConduit.Enabled = false;
            _seamConduit = null;
            base.RemovedFromDocument(document);
        }

        private int GuideLayerCount => _shellEditorLayers?.Count ?? 0;

        internal BoundingBox SeamEditorBoundingBox()
        {
            BoundingBox bounds = BoundingBox.Empty;
            foreach (Curve curve in EffectiveDisplayShells().Concat(DisplayPartitions()))
                if (curve != null && curve.IsValid)
                    bounds.Union(curve.GetBoundingBox(false));
            return bounds;
        }

        internal void DrawSeamEditorReferences(DisplayPipeline display)
        {
            if (display == null) return;
            var shellColor = Color.FromArgb(40, 145, 85);
            var partitionColor = Color.FromArgb(125, 125, 125);
            List<Curve> shells = EffectiveDisplayShells().ToList();
            foreach (Curve partition in DisplayPartitions())
                if (partition != null && partition.IsValid)
                    display.DrawCurve(partition, partitionColor, 1);
            foreach (Curve shell in shells)
                if (shell != null && shell.IsValid)
                    display.DrawCurve(shell, shellColor, 2);

            Curve canonical = DisplayCanonicalShells().FirstOrDefault(curve => curve != null && curve.IsValid);
            if (canonical == null) return;
            WasperShellSeamSettings settings = SettingsForLayer(_seamDisplay);
            double u = settings.SeamU - Math.Floor(settings.SeamU);
            if (!canonical.NormalizedLengthParameter(u, out double parameter))
                parameter = canonical.Domain.ParameterAt(u);
            display.DrawPoint(
                canonical.PointAt(parameter),
                PointStyle.ControlPoint,
                5,
                Color.FromArgb(220, 80, 150));
            if (settings.XSeam && shells.Count > 0)
            {
                Curve active = shells[0];
                display.DrawPoint(active.PointAtStart, PointStyle.ControlPoint, 5, Color.FromArgb(55, 145, 190));
                display.DrawPoint(active.PointAtEnd, PointStyle.ControlPoint, 5, Color.FromArgb(185, 75, 135));
            }
        }

        private IEnumerable<Curve> DisplayCanonicalShells()
        {
            return _seamDisplay >= 0 && _seamDisplay < _shellBaseLayers3d.Count
                ? _shellBaseLayers3d[_seamDisplay]
                : Enumerable.Empty<Curve>();
        }

        private IEnumerable<Curve> DisplayPartitions()
        {
            return _seamDisplay >= 0 && _seamDisplay < _partitionLayers3d.Count
                ? _partitionLayers3d[_seamDisplay]
                : Enumerable.Empty<Curve>();
        }

        private IEnumerable<Curve> EffectiveDisplayShells()
        {
            Plane plane = _seamDisplay >= 0 && _seamDisplay < _seamLayerPlanes.Count
                ? _seamLayerPlanes[_seamDisplay]
                : Plane.WorldXY;
            WasperShellSeamSettings settings = SettingsForLayer(_seamDisplay);
            foreach (Curve canonical in DisplayCanonicalShells())
            {
                Curve effective = WasperShellSeamMetadata.Apply(canonical, settings, plane, 1e-6);
                if (effective != null && effective.IsValid) yield return effective;
            }
        }
    }

    internal sealed class Pp01SeamConduit : DisplayConduit
    {
        private readonly wsp_Pp01_PathsFromCurves_v2 _component;

        internal Pp01SeamConduit(wsp_Pp01_PathsFromCurves_v2 component)
        {
            _component = component;
        }

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            BoundingBox bounds = _component?.SeamEditorBoundingBox() ?? BoundingBox.Empty;
            if (bounds.IsValid) e.IncludeBoundingBox(bounds);
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            _component?.DrawSeamEditorReferences(e.Display);
        }
    }

    internal sealed class Pp01SeamEditorAttributes : GH_ComponentAttributes
    {
        private RectangleF _button;
        private bool _pressed;
        private wsp_Pp01_PathsFromCurves_v2 Component => Owner as wsp_Pp01_PathsFromCurves_v2;

        internal Pp01SeamEditorAttributes(wsp_Pp01_PathsFromCurves_v2 owner) : base(owner) { }

        protected override void Layout()
        {
            base.Layout();
            Rectangle bounds = GH_Convert.ToRectangle(Bounds);
            _button = new RectangleF(bounds.X + 3, bounds.Bottom, bounds.Width - 6, 18);
            bounds.Height += 21;
            Bounds = bounds;
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects) return;
            bool disabled = Owner.Locked || Component == null || !Component.CanOpenSeamEditor;
            using GH_Capsule capsule = GH_Capsule.CreateTextCapsule(
                _button, _button, GH_Palette.Black, "Open Shell Seam Editor",
                GH_FontServer.StandardAdjusted, 3, _pressed ? 0 : 8);
            capsule.Render(graphics, false, disabled, false);
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (!Owner.Locked && Component != null && Component.CanOpenSeamEditor &&
                e.Button == MouseButtons.Left && _button.Contains(e.CanvasLocation))
            {
                _pressed = true;
                sender.Invalidate();
                return GH_ObjectResponse.Capture;
            }
            return base.RespondToMouseDown(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (!_pressed) return base.RespondToMouseUp(sender, e);
            _pressed = false;
            sender.Invalidate();
            if (_button.Contains(e.CanvasLocation)) Component?.ToggleSeamEditor();
            return GH_ObjectResponse.Release;
        }
    }
}
