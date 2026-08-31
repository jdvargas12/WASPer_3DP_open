using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

using GH_IO.Serialization;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;

using Rhino;
using Rhino.Display;
using Rhino.Geometry;

using WASPer_3DP;
using WASPer_3DP.Painting;

namespace WASPer_3DP_Components._2_0_Geometry
{
    public sealed partial class wsp_Ge17_Paint_Mesh_Field : GH_Component
    {
        private const string PaintStateKey = "wsp_ge17_paint_state_gzip";
        private const int MaximumTextureLayers = 5;

        private readonly string _versionTag;
        private Mesh _sourceMesh;
        private Mesh _previewMesh;
        private Mesh _outputMesh;
        private bool _meshUpdateRequested;
        private Mesh _painterMesh;
        private readonly List<int> _painterSourceIndices = new List<int>();
        private readonly List<WasperPaintMarker> _atlasMarkers =
            new List<WasperPaintMarker>();
        private readonly List<WasperPaintMarker> _referenceMarkers =
            new List<WasperPaintMarker>();
        private readonly List<Curve> _seamCurves = new List<Curve>();
        private readonly List<Curve> _referenceEdgeCurves = new List<Curve>();
        private string _meshSignature = string.Empty;
        private string _topologySignature = string.Empty;
        private double[] _values = Array.Empty<double>();
        private double[] _appliedValues = Array.Empty<double>();
        private bool[] _eligible = Array.Empty<bool>();
        private WasperPaintState _pendingState;

        private double _radius = 10.0;
        private double _strength = 0.2;
        private double _smoothStrength = 0.5;
        private double? _uiRadiusOverride;
        private double? _uiStrengthOverride;
        private double? _uiSmoothOverride;
        private double _falloff = 2.0;
        private Interval _domain = new Interval(-5.0, 5.0);
        private Vector3d _previewMove = Vector3d.Zero;
        private WasperPaintTool _tool;
        private WasperSmoothRegionShape _smoothRegionShape =
            WasperSmoothRegionShape.Square;
        private bool _preview = true;
        private bool _live = true;
        private bool _atlasFlipMap;
        private int _atlasQuarterTurns;
        private int _painterVisualRevision;
        private int _rejectedMasks;
        private string _atlasNotice = string.Empty;

        private readonly List<WasperPaintTextureLayer> _textureLayers =
            Enumerable.Range(0, MaximumTextureLayers)
                .Select(_ => new WasperPaintTextureLayer())
                .ToList();
        private int _activeTextureLayer;
        internal WasperPaintTextureLayer ActiveTexture =>
            _textureLayers[_activeTextureLayer];

        private readonly Stack<Ge17v2PainterSnapshot> _undo =
            new Stack<Ge17v2PainterSnapshot>();
        private readonly Stack<Ge17v2PainterSnapshot> _redo =
            new Stack<Ge17v2PainterSnapshot>();
        private Ge17v2PainterSnapshot _textureTransformBefore;
        private Ge17v2PainterSnapshot _strokeBefore;
        private bool _strokeActive;
        private bool _strokeChanged;
        private Point3d _lastAtlasSample = Point3d.Unset;
        private long _lastStrokeVisualUpdateMs;
        private long _lastPreviewBrushRedrawMs;
        private bool _strokeVisualDirty;

        private WasperPaintConduit _conduit;
        private WasperEtoPaintForm _paintForm;
        private bool _hasHover;
        private int _hoverIndex = -1;

        public wsp_Ge17_Paint_Mesh_Field()
            : base(
                "wsp_Ge17_Paint Mesh Field",
                "Paint Mesh",
                "Paints signed displacement directly on mesh vertices through the shared WASPer " +
                "Painter. Pull moves vertices along their normals; Push moves them inward.\r\n\r\n" +
                "The painter uses existing mesh texture coordinates when available, otherwise it " +
                "creates an unwrapped atlas. Optional seam curves cut closed meshes before " +
                "unwrapping; optional edge curves orient matching colored atlas/mesh references. " +
                "Up to five image or colored-mesh textures can be positioned, layered, and " +
                "applied.\r\n\r\n" +
                "Uses the shared WASPer painting system for meshes.",
                WASPerPalette.DesignFabrication,
                "2.0_Geometry")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("71B9A063-BC7B-4C14-9D57-407B226687F4");

        public override GH_Exposure Exposure => GH_Exposure.secondary;
        protected override Bitmap Icon => Ge17v2Icon.Bitmap;

        public override void CreateAttributes()
        {
            m_attributes = new Ge17v2Attributes(this);
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddMeshParameter(
                "mesh",
                "mesh",
                "Mesh whose vertices receive the signed painted displacement.",
                GH_ParamAccess.item);
            p.AddNumberParameter(
                "brush radius",
                "radius",
                "Brush radius in model units.",
                GH_ParamAccess.item,
                10.0);
            p.AddNumberParameter(
                "brush strength",
                "b_strength",
                "Pull, Push, and Erase strength from 0 to 1.",
                GH_ParamAccess.item,
                0.2);
            p.AddNumberParameter(
                "smooth strength",
                "s_strength",
                "Fraction from 0 to 1 used by each Smooth dab.",
                GH_ParamAccess.item,
                0.5);
            p.AddNumberParameter(
                "brush falloff",
                "falloff",
                "Positive falloff exponent.",
                GH_ParamAccess.item,
                2.0);
            int domainIndex = p.AddGenericParameter(
                "magnitude domain",
                "mag_domain",
                "Signed normal-displacement limits. One number x means [0,x].",
                GH_ParamAccess.item);
            p[domainIndex].Optional = true;
            int maskIndex = p.AddGeometryParameter(
                "mask",
                "mask",
                "Optional closed planar curves, solid Breps/Extrusions, or closed meshes.",
                GH_ParamAccess.list);
            p[maskIndex].Optional = true;
            p.AddBooleanParameter(
                "invert",
                "invert",
                "Invert the optional mask selection.",
                GH_ParamAccess.item,
                false);
            int moveIndex = p.AddVectorParameter(
                "move preview",
                "move_prev",
                "Optional translation of the Rhino preview twin.",
                GH_ParamAccess.item);
            p[moveIndex].Optional = true;
            int textureIndex = p.AddGenericParameter(
                "texture",
                "texture",
                "Up to five image paths, Images/Bitmaps, or vertex-colored Meshes, bottom to top.",
                GH_ParamAccess.list);
            p[textureIndex].Optional = true;
            int seamIndex = p.AddCurveParameter(
                "seam",
                "seam",
                "Optional curve or curves defining atlas cuts. Curves are pulled to the mesh " +
                "and snapped to connected mesh topology-edge paths before unwrapping.",
                GH_ParamAccess.list);
            p[seamIndex].Optional = true;
            int edgesIndex = p.AddCurveParameter(
                "reference edges",
                "edges",
                "Optional ordered mesh edge curves used as atlas rows. They are aligned to the " +
                "seam and generate topology-oriented quarter references.",
                GH_ParamAccess.list);
            p[edgesIndex].Optional = true;

            for (int index = 1; index <= edgesIndex; index++)
                p[index].DataMapping = GH_DataMapping.Flatten;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter(
                "paint mesh",
                "paint_mesh",
                "Input mesh displaced at its original location. It refreshes when Update Mesh is " +
                "pressed, independently from the moved live Rhino preview.",
                GH_ParamAccess.item);
            p.AddTextParameter(
                "summary",
                "summary",
                "Mesh atlas, painting, texture, mask, and update report.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Mesh mesh = null;
            if (!da.GetData(0, ref mesh) || mesh == null ||
                !mesh.IsValid || mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
            {
                StopPainting();
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "A valid mesh containing vertices and faces is required.");
                return;
            }

            double radius = 10.0;
            double strength = 0.2;
            double smooth = 0.5;
            double falloff = 2.0;
            da.GetData(1, ref radius);
            da.GetData(2, ref strength);
            da.GetData(3, ref smooth);
            da.GetData(4, ref falloff);
            if (PainterInputEditable(1) && _uiRadiusOverride.HasValue)
                radius = _uiRadiusOverride.Value;
            else if (!PainterInputEditable(1))
                _uiRadiusOverride = null;
            if (PainterInputEditable(2) && _uiStrengthOverride.HasValue)
                strength = _uiStrengthOverride.Value;
            else if (!PainterInputEditable(2))
                _uiStrengthOverride = null;
            if (PainterInputEditable(3) && _uiSmoothOverride.HasValue)
                smooth = _uiSmoothOverride.Value;
            else if (!PainterInputEditable(3))
                _uiSmoothOverride = null;
            _radius = double.IsFinite(radius) && radius > RhinoMath.ZeroTolerance
                ? radius
                : 10.0;
            _strength = double.IsFinite(strength)
                ? Math.Max(0.0, Math.Min(1.0, strength))
                : 0.2;
            _smoothStrength = double.IsFinite(smooth)
                ? Math.Max(0.0, Math.Min(1.0, smooth))
                : 0.5;
            _falloff = double.IsFinite(falloff) && falloff > 0.0
                ? falloff
                : 2.0;
            if (!WasperPaintUtilities.TryGetDomain(
                    da,
                    5,
                    out Interval domain,
                    out string domainError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, domainError);
                return;
            }
            _domain = domain;

            var rawMasks = new List<GeometryBase>();
            bool invert = false;
            Vector3d move = Vector3d.Zero;
            var textures = new List<object>();
            var seams = new List<Curve>();
            var referenceEdges = new List<Curve>();
            da.GetDataList(6, rawMasks);
            da.GetData(7, ref invert);
            da.GetData(8, ref move);
            da.GetDataList(9, textures);
            da.GetDataList(10, seams);
            da.GetDataList(11, referenceEdges);
            _seamCurves.Clear();
            _seamCurves.AddRange(seams
                .Where(curve => curve != null && curve.IsValid)
                .Select(curve => curve.DuplicateCurve()));
            _referenceEdgeCurves.Clear();
            _referenceEdgeCurves.AddRange(referenceEdges
                .Where(curve => curve != null && curve.IsValid)
                .Select(curve => curve.DuplicateCurve()));
            _previewMove = move.IsValid ? move : Vector3d.Zero;
            PrepareTextureSources(textures, out string textureError);
            if (!string.IsNullOrEmpty(textureError))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, textureError);

            double tolerance = Math.Max(
                RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
                1e-9);
            List<WasperPaintMaskRegion> masks = WasperPaintMaskRegion.Build(
                rawMasks,
                tolerance,
                out _rejectedMasks);

            string signature = ComputeMeshSignature(mesh, false);
            string topology = ComputeMeshSignature(mesh, true);
            bool topologyChanged =
                !string.Equals(topology, _topologySignature, StringComparison.Ordinal) ||
                _values.Length != mesh.Vertices.Count;
            if (topologyChanged)
                InitializeMesh(mesh, signature, topology);
            else
            {
                if (!string.Equals(signature, _meshSignature, StringComparison.Ordinal))
                    _meshUpdateRequested = true;
                _meshSignature = signature;
                _sourceMesh = mesh.DuplicateMesh();
                EnsureNormals(_sourceMesh);
            }

            BuildEligibility(masks, invert);
            ClampValues();
            if (_live)
                ApplyWorkingValues();
            BuildAtlas(tolerance);
            EnsureTexturePlacement();
            RebuildDisplayMeshes();
            UpdateConduit();

            if (_rejectedMasks > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{_rejectedMasks} unsupported or open mask item(s) were ignored.");
            }
            if (!string.IsNullOrWhiteSpace(_atlasNotice))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, _atlasNotice);

            da.SetData(0, _outputMesh?.DuplicateMesh());
            da.SetData(1, BuildSummary());
            Message = $"{_versionTag} | {_sourceMesh.Vertices.Count} vertices";
        }

        private void InitializeMesh(Mesh mesh, string signature, string topology)
        {
            StopPainting();
            _sourceMesh = mesh.DuplicateMesh();
            EnsureNormals(_sourceMesh);
            _meshSignature = signature;
            _topologySignature = topology;
            _values = new double[_sourceMesh.Vertices.Count];
            _appliedValues = new double[_values.Length];
            _eligible = Enumerable.Repeat(true, _values.Length).ToArray();
            _outputMesh = null;
            _meshUpdateRequested = true;
            _undo.Clear();
            _redo.Clear();
            if (_pendingState != null &&
                _pendingState.Values?.Length == _values.Length &&
                (string.Equals(
                     _pendingState.Signature,
                     signature,
                     StringComparison.Ordinal) ||
                 string.Equals(
                     _pendingState.TopologySignature,
                     topology,
                     StringComparison.Ordinal)))
            {
                _values = (double[])_pendingState.Values.Clone();
                _appliedValues = _pendingState.AppliedValues?.Length == _values.Length
                    ? (double[])_pendingState.AppliedValues.Clone()
                    : (double[])_values.Clone();
                _preview = _pendingState.Preview;
                _atlasFlipMap = _pendingState.AtlasFlipMap;
                _atlasQuarterTurns = ((_pendingState.AtlasQuarterTurns % 4) + 4) % 4;
                RestoreTextureState(_pendingState);
            }
            _pendingState = null;
        }

        private void BuildEligibility(
            IList<WasperPaintMaskRegion> masks,
            bool invert)
        {
            _eligible = new bool[_sourceMesh.Vertices.Count];
            bool hasMasks = masks != null && masks.Count > 0;
            for (int index = 0; index < _eligible.Length; index++)
            {
                Point3d point = _sourceMesh.Vertices.Point3dAt(index);
                bool inside = hasMasks && masks.Any(mask => mask.Contains(point));
                _eligible[index] = !hasMasks || (invert ? !inside : inside);
            }
        }

        private void ClampValues()
        {
            double min = Math.Min(_domain.T0, _domain.T1);
            double max = Math.Max(_domain.T0, _domain.T1);
            for (int index = 0; index < _values.Length; index++)
                _values[index] = Math.Max(min, Math.Min(max, _values[index]));
            for (int index = 0; index < _appliedValues.Length; index++)
                _appliedValues[index] = Math.Max(min, Math.Min(max, _appliedValues[index]));
        }

        private static void EnsureNormals(Mesh mesh)
        {
            if (mesh == null)
                return;
            if (mesh.Normals.Count != mesh.Vertices.Count)
                mesh.Normals.ComputeNormals();
            mesh.Normals.UnitizeNormals();
        }

        private static string ComputeMeshSignature(Mesh mesh, bool topologyOnly)
        {
            using var hash = SHA256.Create();
            var text = new StringBuilder();
            text.Append(mesh.Vertices.Count).Append('|').Append(mesh.Faces.Count);
            foreach (MeshFace face in mesh.Faces)
                text.Append('|').Append(face.A).Append(',').Append(face.B)
                    .Append(',').Append(face.C).Append(',').Append(face.D);
            if (!topologyOnly)
            {
                foreach (Point3f vertex in mesh.Vertices)
                    text.Append('|').Append(vertex.X.ToString("R"))
                        .Append(',').Append(vertex.Y.ToString("R"))
                        .Append(',').Append(vertex.Z.ToString("R"));
            }
            return Convert.ToHexString(
                hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }

        private string BuildSummary()
        {
            int painted = _appliedValues.Count(value => Math.Abs(value) > 1e-12);
            int visibleTextures = _textureLayers.Count(layer =>
                layer.Bitmap != null && layer.Visible);
            return
                $"Ge17 mesh painter | vertices: {_values.Length} | eligible: " +
                $"{_eligible.Count(value => value)} | painted: {painted} | " +
                $"domain: [{_domain.T0:G5}, {_domain.T1:G5}] | " +
                $"textures: {visibleTextures}/{_textureLayers.Count(layer => layer.Bitmap != null)} | " +
                $"seams: {_seamCurves.Count} | " +
                $"reference edges: {_referenceEdgeCurves.Count} | " +
                $"atlas rotation: {_atlasQuarterTurns * 90}° | " +
                $"mode: {(_live ? "Live" : "Paused")}.";
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            StopPainting();
            if (_paintForm != null && !_paintForm.IsClosed)
                _paintForm.Close();
            foreach (WasperPaintTextureLayer layer in _textureLayers)
                layer.Dispose();
            if (_conduit != null)
                _conduit.Enabled = false;
            base.RemovedFromDocument(document);
        }
    }

    internal sealed class Ge17v2PainterHost : IWasperPainterHost
    {
        private readonly wsp_Ge17_Paint_Mesh_Field _component;

        internal Ge17v2PainterHost(wsp_Ge17_Paint_Mesh_Field component)
        {
            _component = component;
        }

        public string PainterTitle => "WASPer Mesh Painter";
        public string PullToolLabel => "Pull";
        public string PushToolLabel => "Push";
        public Color PullToolColor => WasperPaintColors.Pulled;
        public Color PushToolColor => WasperPaintColors.Pushed;
        public string PainterLegend =>
            "Push  GREEN    \u2190    Zero  BLUE    \u2192    Pull  RED";
        public bool SupportsZeroTool => false;
        public Color ZeroToolColor => WasperPaintColors.Neutral;
        public WasperPaintTool ActiveTool => _component.ActiveTool;
        public WasperSmoothRegionShape SmoothRegionShape =>
            _component.SmoothRegionShape;
        public bool PreviewEnabled => _component.PreviewEnabled;
        public bool LiveEnabled => _component.LiveEnabled;
        public bool UpdateEnabled => _component.UpdateEnabled;
        public bool HasPendingUpdate => _component.HasPendingUpdate;
        public Mesh PainterMesh => _component.PainterMesh;
        public Plane PainterPlane => Plane.WorldXY;
        public IList<WasperPaintMarker> PainterMarkers => _component.AtlasMarkers;
        public bool ShowAtlasDimensions => false;
        public IList<WasperPaintAtlasBounds> AtlasDimensionBounds => null;
        public double PainterRadius => _component.PainterRadius;
        public double PainterBrushStrength => _component.PainterBrushStrength;
        public double PainterSmoothStrength => _component.PainterSmoothStrength;
        public bool PainterRadiusEditable => _component.PainterInputEditable(1);
        public bool PainterBrushStrengthEditable => _component.PainterInputEditable(2);
        public bool PainterSmoothStrengthEditable => _component.PainterInputEditable(3);
        public int PainterVisualRevision => _component.PainterVisualRevision;
        public bool CanUndoPaint => _component.CanUndo;
        public bool CanRedoPaint => _component.CanRedo;
        public bool SupportsTextures => true;
        public bool SupportsTextTextures => false;
        public bool SupportsFieldCollection => false;
        public bool SupportsAtlasTransforms => true;
        public int FieldCount => 1;
        public int ActiveFieldIndex => 0;
        public double FieldOffset => 0.0;
        public double FieldResolution => 1.0;
        public double FieldFrameSize => 1.0;
        public bool FieldArrangeMode => false;
        public int TextureLayerCount => _component.TextureLayers.Count;
        public int ActiveTextureLayer => _component.ActiveTextureLayer;
        public IList<WasperPaintTextureLayer> TextureLayers => _component.TextureLayers;
        public int TextTextureLayerCount => 0;
        public int ActiveTextTextureLayer => 0;
        public IList<WasperPaintTextureLayer> TextTextureLayers =>
            Array.Empty<WasperPaintTextureLayer>();
        public bool HasTextureSource => _component.ActiveTexture.Bitmap != null;
        public Bitmap TextureBitmap => _component.ActiveTexture.Bitmap;
        public bool TextureVisible => _component.TextureVisible;
        public bool TextureEditMode => _component.TextureEditMode;
        public bool TextureDistortMode => _component.TextureDistortMode;
        public bool TextureRotateMode => _component.TextureRotateMode;
        public bool TextureHandlesVisible => _component.TextureHandlesVisible;
        public bool SupportsTextureEdgeHandles => false;
        public bool TextureIsDistorted => _component.ActiveTexture.Placement.IsDistorted;
        public int TextureRevision => _component.ActiveTexture.Revision;
        public bool AtlasFlipMap => _component.AtlasFlipMap;
        public int AtlasQuarterTurns => _component.AtlasQuarterTurns;
        public double AtlasMirrorCenterX => _component.AtlasMirrorCenterX;
        public IList<Point2d> TextureCorners => _component.ActiveTexture.Placement.Corners;

        public void TogglePreview() => _component.TogglePreview();
        public void ToggleLive() => _component.ToggleLive();
        public void UpdateAlgorithm() => _component.UpdateAlgorithm();
        public void UndoPaint() => _component.UndoPaint();
        public void RedoPaint() => _component.RedoPaint();
        public void ClearPaint() => _component.ClearPaint();
        public void PreviewPainterSettings(double radius, double brushStrength, double smoothStrength) =>
            _component.PreviewPainterSettings(radius, brushStrength, smoothStrength);
        public void CommitPainterSettings(double radius, double brushStrength, double smoothStrength) =>
            _component.CommitPainterSettings(radius, brushStrength, smoothStrength);
        public void SetPainterTool(WasperPaintTool tool) => _component.SetPainterTool(tool);
        public void SetSmoothRegionShape(WasperSmoothRegionShape shape) =>
            _component.SetSmoothRegionShape(shape);
        public void ApplySmoothRegion(IList<Point3d> boundary) =>
            _component.ApplySmoothRegion(boundary);
        public bool PainterBeginStroke(Point3d point) => _component.PainterBeginStroke(point);
        public void PainterContinueStroke(Point3d point) => _component.PainterContinueStroke(point);
        public void PainterEndStroke() => _component.PainterEndStroke();
        public void PainterHover(Point3d point) => _component.PainterHover(point);
        public void ClearPainterHover() => _component.ClearPainterHover();
        public void AddNewField() { }
        public void DuplicateActiveField() { }
        public void RemoveActiveField() { }
        public void SelectPreviousField() { }
        public void SelectNextField() { }
        public void MoveActiveFieldUp() { }
        public void MoveActiveFieldDown() { }
        public void PreviewFieldSettings(double offset, double resolution, double frameSize) { }
        public void CommitFieldSettings(double offset, double resolution, double frameSize) { }
        public void ToggleFieldArrangeMode() { }
        public bool SelectFieldAt(Point3d atlasPoint) => false;
        public bool BeginFieldDrag(Point3d atlasPoint) => false;
        public void MoveFieldDrag(Point3d atlasPoint) { }
        public void EndFieldDrag() { }
        public void SavePainterSession() => _component.SavePainterSession();
        public void LoadPainterSession() => _component.LoadPainterSession();
        public void SavePainterBitmap(Bitmap bitmap) => _component.SavePainterBitmap(bitmap);
        public void ToggleTextureVisibility() => _component.ToggleTextureVisibility();
        public void ToggleTextureLayerVisibility(int layer) => _component.ToggleTextureLayerVisibility(layer);
        public void ToggleTextureEdit() => _component.ToggleTextureEdit();
        public void ToggleTextureDistort() => _component.ToggleTextureDistort();
        public void ToggleTextureRotate() => _component.ToggleTextureRotate();
        public void ToggleAtlasFlipMap() => _component.ToggleAtlasFlipMap();
        public void RotateAtlasClockwise() => _component.RotateAtlasClockwise();
        public void FitTextureToAtlas() => _component.FitTextureToAtlas();
        public void ApplyTextureToPaint() => _component.ApplyTextureToPaint();
        public void ApplyTextureCompositeToPaint() => _component.ApplyTextureCompositeToPaint();
        public void RemoveTextureOverlay() => _component.RemoveTextureOverlay();
        public void SelectTextureLayer(int layer) => _component.SelectTextureLayer(layer);
        public void SelectTextTextureLayer(int layerIndex) { }
        public void ToggleTextTextureLayerVisibility(int layerIndex) { }
        public void PreviewTextTexture(string text, string fontName, double fontSize) { }
        public void CommitTextTexture(string text, string fontName, double fontSize) { }
        public void DuplicateTextTextureLayer() { }
        public void RemoveTextTextureLayer() { }
        public void MoveTextTextureLayer(int direction) { }
        public void BeginTextureTransform(int corner) => _component.BeginTextureTransform(corner);
        public void BeginTextureMove(Point3d point) => _component.BeginTextureMove(point);
        public void MoveTextureCorner(int corner, Point3d point, bool ortho) =>
            _component.MoveTextureCorner(corner, point);
        public void MoveTexture(Point3d point) => _component.MoveTexture(point);
        public void EndTextureTransform() => _component.EndTextureTransform();
        public Point3d MirrorAtlasPoint(Point3d point) => _component.MirrorAtlasPoint(point);
        public Point3d TransformAtlasPoint(Point3d point) =>
            _component.TransformAtlasPoint(point);
        public Point3d InverseTransformAtlasPoint(Point3d point) =>
            _component.InverseTransformAtlasPoint(point);
    }

    internal sealed class Ge17v2Attributes : GH_ComponentAttributes
    {
        private RectangleF _painterButtonBounds;
        private RectangleF _meshButtonBounds;
        private int _pressedButton = -1;
        private wsp_Ge17_Paint_Mesh_Field Component =>
            Owner as wsp_Ge17_Paint_Mesh_Field;

        internal Ge17v2Attributes(wsp_Ge17_Paint_Mesh_Field owner) : base(owner) { }

        protected override void Layout()
        {
            base.Layout();
            Rectangle bounds = GH_Convert.ToRectangle(Bounds);
            _painterButtonBounds = new RectangleF(
                bounds.X + 3,
                bounds.Bottom,
                bounds.Width - 6,
                18);
            _meshButtonBounds = _painterButtonBounds;
            _meshButtonBounds.Y += 20;
            bounds.Height += 41;
            Bounds = bounds;
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects)
                return;
            using GH_Capsule painter = GH_Capsule.CreateTextCapsule(
                _painterButtonBounds,
                _painterButtonBounds,
                GH_Palette.Black,
                "Open Painter",
                GH_FontServer.StandardAdjusted,
                3,
                _pressedButton == 0 ? 0 : 8);
            painter.Render(graphics, false, Owner.Locked, false);
            using GH_Capsule update = GH_Capsule.CreateTextCapsule(
                _meshButtonBounds,
                _meshButtonBounds,
                GH_Palette.Black,
                "Update Mesh",
                GH_FontServer.StandardAdjusted,
                3,
                _pressedButton == 1 ? 0 : 8);
            update.Render(graphics, false, Owner.Locked, false);
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (!Owner.Locked && e.Button == MouseButtons.Left)
            {
                if (_painterButtonBounds.Contains(e.CanvasLocation))
                    _pressedButton = 0;
                else if (_meshButtonBounds.Contains(e.CanvasLocation))
                    _pressedButton = 1;
                if (_pressedButton >= 0)
                {
                    sender.Invalidate();
                    return GH_ObjectResponse.Capture;
                }
            }
            return base.RespondToMouseDown(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_pressedButton < 0)
                return base.RespondToMouseUp(sender, e);
            int released = _pressedButton;
            _pressedButton = -1;
            sender.Invalidate();
            if (released == 0 &&
                _painterButtonBounds.Contains(e.CanvasLocation))
                Component.TogglePainterForm();
            else if (released == 1 &&
                     _meshButtonBounds.Contains(e.CanvasLocation))
                Component.UpdateMeshOutput();
            return GH_ObjectResponse.Release;
        }
    }

    internal static class Ge17v2Icon
    {
        internal static Bitmap Bitmap => WasperPaintComponentIcons.Mesh;
    }
}
