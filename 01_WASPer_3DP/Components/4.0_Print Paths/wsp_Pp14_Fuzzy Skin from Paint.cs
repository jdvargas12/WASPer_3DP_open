using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Newtonsoft.Json;

using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.UI;

using WASPer_3DP;

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public sealed partial class wsp_Pp14_Fuzzy_Skin_from_Paint : GH_Component
    {
        private const string PaintStateKey = "wsp_gc20_paint_state_gzip";
        // Legacy all-or-nothing key, still read for backward compatibility with saved files.
        private const string ShowOutputsKey = "wsp_gc20_show_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc20_visible_outputs_mask";
        private static readonly string[] OutputCatalog = WasperPathDebugOutputs.CoreNickNames
            .Concat(new[] { "paint_values" })
            .ToArray();
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;
        private const string ConstrainInfillKey = "wsp_gc20_constrain_infill";
        private const string PreviewKey = "wsp_gc20_paint_preview";
        private const string LiveKey = "wsp_gc20_paint_live";
        private const string ShellMeshKey = "wsp_gc20_shell_mesh";

        private readonly string _versionTag;
        private WasperPrintPath _source;
        private readonly List<PaintLocation> _locations = new List<PaintLocation>();
        private readonly Dictionary<string, List<int>> _eligibleByStack =
            new Dictionary<string, List<int>>();
        private readonly WasperPaintValueHistory _paintValues =
            new WasperPaintValueHistory();
        private readonly Stack<WasperPp13PainterUndoState> _painterUndo =
            new Stack<WasperPp13PainterUndoState>();
        private readonly Stack<WasperPp13PainterUndoState> _painterRedo =
            new Stack<WasperPp13PainterUndoState>();
        private WasperPp13PainterUndoState _textureTransformBefore;
        private double[] _values
        {
            get => _paintValues.Values;
            set => _paintValues.Values = value ?? Array.Empty<double>();
        }
        private double[] _appliedValues
        {
            get => _paintValues.AppliedValues;
            set => _paintValues.AppliedValues = value ?? Array.Empty<double>();
        }
        private string _signature = string.Empty;
        private string _topologySignature = string.Empty;
        private List<PaintBranchLayout> _paintBranchLayout =
            new List<PaintBranchLayout>();
        private WasperPrintPath _preparedSource;
        private string _preparationSignature = string.Empty;
        private OutputCacheEntry _outputCache;
        private readonly Dictionary<string, ShellDirection> _directionByPath =
            new Dictionary<string, ShellDirection>();
        private WasperPaintState _pendingState;
        private Mesh _previewMesh;
        private Mesh _outputShellMesh;
        private bool _shellMeshUpdateRequested;
        private readonly List<int> _previewSourceIndices = new List<int>();
        private readonly Dictionary<int, Point3d> _previewPoints =
            new Dictionary<int, Point3d>();
        private Mesh _painterMesh;
        private readonly List<int> _painterSourceIndices = new List<int>();
        private readonly Dictionary<int, Point3d> _atlasPoints =
            new Dictionary<int, Point3d>();
        private readonly List<WasperPaintMarker> _atlasMarkers = new List<WasperPaintMarker>();
        private readonly List<WasperPaintMarker> _referenceMarkers = new List<WasperPaintMarker>();
        private readonly Dictionary<string, double> _atlasRowWidths =
            new Dictionary<string, double>();
        private Plane _previewPlane = Plane.WorldXY;
        private Vector3d _previewMove = Vector3d.Zero;
        private double _atlasRmsDistortion;
        private double _atlasMaxDistortion;
        private int _atlasTileCount;
        private int _normalizedRowReversals;
        private int _normalizationAuthoritativePlanes;
        private int _normalizationFittedPlanes;
        private int _normalizationUnresolvedRows;
        private int _painterVisualRevision;
        private int _texturePreviewSignature = int.MinValue;
        private Color[] _texturePreviewColors = Array.Empty<Color>();
        private double[] _strokeBefore;
        private bool _strokeChanged;
        private bool _strokeActive;
        private bool _strokeSuspended;
        private Point3d _lastSample = Point3d.Unset;
        private int _lastHitIndex = -1;
        private string _lastHitStack = string.Empty;
        private Vector3d _lastStrokeNormal = Vector3d.Unset;

        private double _radius = 10.0;
        private double _strength = 0.2;
        private double _smoothStrength = 0.5;
        private double? _uiRadiusOverride;
        private double? _uiStrengthOverride;
        private double? _uiSmoothOverride;
        private double _falloff = 2.0;
        private Interval _domain = new Interval(-5.0, 5.0);
        private WasperPaintTool _tool;
        private WasperSmoothRegionShape _smoothRegionShape =
            WasperSmoothRegionShape.Square;
        private bool _preview = true;
        private bool _live = true;
        private int _visibleOutputsMask;
        private bool _constrainInfill = true;
        private const int MaximumInputTextureLayers = 5;
        private const int MaximumTextTextureLayers = 5;
        private const int MaximumTextureLayers =
            MaximumInputTextureLayers + MaximumTextTextureLayers;
        private readonly List<WasperPaintTextureLayer> _textureLayers =
            Enumerable.Range(0, MaximumTextureLayers)
                .Select(_ => new WasperPaintTextureLayer())
                .ToList();
        private int _activeTextureLayer;
        private WasperPaintTextureLayer ActiveTexture =>
            _textureLayers[_activeTextureLayer];
        private object _textureSource
        {
            get => ActiveTexture.Source;
            set => ActiveTexture.Source = value;
        }
        private string _textureSourceDescription
        {
            get => ActiveTexture.SourceDescription;
            set => ActiveTexture.SourceDescription = value;
        }
        private string _textureSourceKey
        {
            get => ActiveTexture.SourceKey;
            set => ActiveTexture.SourceKey = value;
        }
        private string _ignoredTextureSourceKey
        {
            get => ActiveTexture.IgnoredSourceKey;
            set => ActiveTexture.IgnoredSourceKey = value;
        }
        private Bitmap _textureBitmap
        {
            get => ActiveTexture.Bitmap;
            set => ActiveTexture.Bitmap = value;
        }
        private bool _textureVisible
        {
            get => ActiveTexture.Visible;
            set => ActiveTexture.Visible = value;
        }
        private bool _textureEditMode
        {
            get => ActiveTexture.EditMode;
            set => ActiveTexture.EditMode = value;
        }
        private bool _textureDistortMode
        {
            get => ActiveTexture.DistortMode;
            set => ActiveTexture.DistortMode = value;
        }
        private bool _textureRotateMode
        {
            get => ActiveTexture.RotateMode;
            set => ActiveTexture.RotateMode = value;
        }
        private bool _atlasFlipMap;
        private int _atlasQuarterTurns;
        private WasperPaintTexturePlacement _texturePlacement =>
            ActiveTexture.Placement;
        private bool _texturePlacementInitialized
        {
            get => _texturePlacement.Initialized;
            set => _texturePlacement.Initialized = value;
        }
        private double _textureMinX
        {
            get => _texturePlacement.MinX;
            set => _texturePlacement.MinX = value;
        }
        private double _textureMinY
        {
            get => _texturePlacement.MinY;
            set => _texturePlacement.MinY = value;
        }
        private double _textureMaxX
        {
            get => _texturePlacement.MaxX;
            set => _texturePlacement.MaxX = value;
        }
        private double _textureMaxY
        {
            get => _texturePlacement.MaxY;
            set => _texturePlacement.MaxY = value;
        }
        private Point2d[] _textureCorners => _texturePlacement.Corners;
        private Point2d[] _textureTransformStartCorners
        {
            get => _texturePlacement.TransformStartCorners;
            set => _texturePlacement.TransformStartCorners = value;
        }
        private Point2d _textureTransformStartPoint
        {
            get => _texturePlacement.TransformStartPoint;
            set => _texturePlacement.TransformStartPoint = value;
        }
        private int _textureTransformCorner
        {
            get => _texturePlacement.TransformCorner;
            set => _texturePlacement.TransformCorner = value;
        }
        private int _textureRevision
        {
            get => ActiveTexture.Revision;
            set => ActiveTexture.Revision = value;
        }

        private WasperPaintConduit _conduit;
        private WasperEtoPaintForm _paintForm;
        private bool _hasHover;
        private int _hoverIndex = -1;
        private Point3d _hoverPoint = Point3d.Unset;
        private Vector3d _hoverNormal = Vector3d.Unset;
        private long _lastHoverRedrawMs;
        private long _lastStrokeVisualUpdateMs;
        private bool _strokeVisualDirty;

        public wsp_Pp14_Fuzzy_Skin_from_Paint()
            : base(
                "wsp_Pp14_Fuzzy Skin from Paint",
                "Paint Fuzzy",
                "Paints signed fuzzy-skin displacement on wsp_path Shell points through a " +
                "flattened interactive atlas. Open Painter, choose Pull, Push, Smooth, or Erase, " +
                "then click or drag to paint. Pull is positive/outward; Push is negative/inward.\r\n\r\n" +
                "Up to five texture inputs and five editable text-texture slots form a bottom-to-top layer stack. Select one layer to " +
                "move, fit, edit, distort, hide, or remove. Apply Layer uses the selected texture; " +
                "Apply Composite alpha-blends all visible layers.\r\n\r\n" +
                "Live applies completed strokes immediately. Paused keeps changes pending until " +
                "Update. Preview controls the Rhino display twin; Update Mesh explicitly refreshes " +
                "shell_mesh. Save/Load preserves compatible painter sessions.\r\n\r\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("D78AD675-7859-4D83-A6BC-773538C66A20");

        public override GH_Exposure Exposure => GH_Exposure.secondary;
        protected override Bitmap Icon => PaintFuzzyIcon.Bitmap;

        internal WasperPaintTool ActiveTool => _tool;
        internal WasperSmoothRegionShape SmoothRegionShape =>
            _smoothRegionShape;
        internal bool PreviewEnabled => _preview;
        internal bool LiveEnabled => _live;
        internal bool UpdateEnabled => !_live;
        internal bool HasPendingUpdate =>
            !_live && !WasperPaintUtilities.ValuesEqual(_values, _appliedValues);
        internal Mesh PainterMesh => _painterMesh;
        internal Plane PainterPlane => _previewPlane;
        internal IList<WasperPaintMarker> PainterMarkers => _atlasMarkers;
        internal double PainterRadius => _radius;
        internal double PainterBrushStrength => _strength;
        internal double PainterSmoothStrength => _smoothStrength;
        internal bool PainterRadiusEditable => PainterInputEditable(1);
        internal bool PainterBrushStrengthEditable => PainterInputEditable(2);
        internal bool PainterSmoothStrengthEditable => PainterInputEditable(3);
        internal int PainterVisualRevision => _painterVisualRevision;
        internal bool CanUndoPaint => _painterUndo.Count > 0;
        internal bool CanRedoPaint => _painterRedo.Count > 0;
        internal int TextureLayerCount => MaximumInputTextureLayers;
        internal int ActiveTextureLayer => _activeTextureLayer;
        internal IList<WasperPaintTextureLayer> TextureLayers => _textureLayers;
        internal int TextTextureLayerCount => MaximumTextTextureLayers;
        internal int ActiveTextTextureLayer => _activeTextureLayer >= MaximumInputTextureLayers
            ? _activeTextureLayer - MaximumInputTextureLayers
            : 0;
        internal IList<WasperPaintTextureLayer> TextTextureLayers =>
            _textureLayers.Skip(MaximumInputTextureLayers).ToList();
        internal object TextureSource => _textureSource;
        internal string TextureSourceDescription => _textureSourceDescription;
        internal bool HasTextureSource => _textureBitmap != null;
        internal Bitmap TextureBitmap => _textureBitmap;
        internal bool TextureVisible => _textureVisible && _textureBitmap != null;
        internal bool TextureEditMode => _textureEditMode && TextureVisible;
        internal bool TextureDistortMode => _textureDistortMode && TextureVisible;
        internal bool TextureRotateMode => _textureRotateMode && TextureVisible;
        internal bool AtlasFlipMap => _atlasFlipMap;
        internal int AtlasQuarterTurns => _atlasQuarterTurns;
        internal bool TextureHandlesVisible =>
            (TextureEditMode || TextureDistortMode || TextureRotateMode) && TextureVisible;
        internal IList<Point2d> TextureCorners => _textureCorners;
        internal int TextureRevision => _textureRevision;
        internal RectangleF TextureBounds => RectangleF.FromLTRB(
            (float)_textureMinX,
            (float)_textureMinY,
            (float)_textureMaxX,
            (float)_textureMaxY);

        public override void CreateAttributes()
        {
            m_attributes = new PaintAttributes(this);
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path containing role-tagged Shell pt_planes. Plane origins are " +
                "the directly paintable texture samples. Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);
            p.AddNumberParameter(
                "brush radius",
                "radius",
                "Brush radius in model units. Propagation is restricted to the selected logical Shell stack.",
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
                "Fraction from 0 to 1 by which each Smooth dab approaches the " +
                "distance-weighted average inside the brush radius.",
                GH_ParamAccess.item,
                0.5);
            p.AddNumberParameter(
                "brush falloff",
                "falloff",
                "Positive falloff exponent. 1 is linear; larger values concentrate the brush.",
                GH_ParamAccess.item,
                2.0);
            int domainIndex = p.AddGenericParameter(
                "magnitude domain",
                "mag_domain",
                "Signed displacement limits in model units. A Domain such as [-5,8] permits " +
                "5 units inward and 8 outward. A text panel such as '-5 to 5' is also accepted. " +
                "One number x means [0,x]. Untouched points stay at zero.",
                GH_ParamAccess.item);
            p[domainIndex].Optional = true;
            int maskIndex = p.AddGeometryParameter(
                "mask",
                "mask",
                "Optional closed planar curves, solid Breps/Extrusions, or closed meshes that " +
                "limit paintable and displaced locations.",
                GH_ParamAccess.list);
            p[maskIndex].Optional = true;
            p.AddBooleanParameter(
                "invert",
                "invert",
                "Invert the optional mask selection.",
                GH_ParamAccess.item,
                false);
            int movePreviewIndex = p.AddVectorParameter(
                "move preview",
                "move_prev",
                "Optional translation vector for the offset 3D preview twin and its markers. " +
                "The popup painter keeps its own independent flat 2D atlas.",
                GH_ParamAccess.item);
            p[movePreviewIndex].Optional = true;
            int textureIndex = p.AddGenericParameter(
                "texture",
                "texture",
                "Optional texture layers prepared for placement in the painter. Supply up to five " +
                "image paths, System.Drawing Images/Bitmaps, or vertex-colored Meshes. List order " +
                "defines the layer stack from bottom to top. The Painter shows one editable layer " +
                "at a time with Edit, Distort, Fit, Apply Layer, and Remove. Apply Composite uses " +
                "all visible layers. The Painter's general " +
                "Flip Map control mirrors the atlas horizontally using a lightweight display transform.",
                GH_ParamAccess.list);
            p[textureIndex].Optional = true;

            for (int index = 1; index <= textureIndex; index++)
                p[index].DataMapping = GH_DataMapping.Flatten;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Path with the painted signed displacement applied to eligible plane origins.",
                GH_ParamAccess.item);
            p.AddMeshParameter(
                "shell mesh",
                "shell_mesh",
                "Manually generated colored mesh through the current paint-displaced Shell points. " +
                "This output changes only when " +
                "the component's Update Mesh button is pressed.",
                GH_ParamAccess.item);
            p.AddTextParameter(
                "summary",
                "summary",
                "Paint-state, displacement, direction, filtering, and Infill-constraint report.",
                GH_ParamAccess.item);
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(
                menu,
                "Constrain infill to modified Shell",
                (_, _) =>
                {
                    RecordUndoEvent("Toggle Infill constraint");
                    _constrainInfill = !_constrainInfill;
                    ExpireSolution(true);
                },
                true,
                _constrainInfill);
            WasperPathDebugOutputs.AppendOutputVisibilityMenu(
                this,
                menu,
                "Debug Outputs",
                OutputCatalog,
                () => _visibleOutputsMask,
                mask =>
                {
                    RecordUndoEvent("Toggle Pp14 debug outputs");
                    _visibleOutputsMask = mask;
                    RebuildOutputs();
                    ExpireSolution(true);
                },
                fixedOutputCount: 3);
        }

        private void RebuildOutputs()
        {
            EnsureFixedOutputLayout();
            WasperPathDebugOutputs.Rebuild(
                this,
                _visibleOutputsMask,
                "Paint-state, displacement, direction, filtering, and Infill-constraint report.",
                OutputCatalog,
                fixedOutputCount: 3,
                registerExtras: (component, isVisible) =>
                {
                    if (isVisible("paint_values"))
                        component.Params.RegisterOutputParam(new Param_Number
                        {
                            Name = "paint_values",
                            NickName = "paint_values",
                            Description = "Signed displacement value stored at every outgoing path plane.",
                            Access = GH_ParamAccess.tree
                        });
                });
        }

        private void EnsureFixedInputLayout()
        {
            string[] expected =
            {
                "wsp_path",
                "radius",
                "b_strength",
                "s_strength",
                "falloff",
                "mag_domain",
                "mask",
                "invert",
                "move_prev",
                "texture"
            };
            bool valid =
                Params.Input.Count == expected.Length &&
                expected
                    .Select((nickName, index) =>
                        string.Equals(
                            Params.Input[index].NickName,
                            nickName,
                            StringComparison.OrdinalIgnoreCase))
                    .All(item => item);
            if (valid)
            {
                Params.Input[9].Access = GH_ParamAccess.list;
                EnsureInputDataMapping();
                return;
            }

            List<IGH_Param> previous = Params.Input.ToList();
            var used = new HashSet<IGH_Param>();
            IGH_Param Find<TParam>(params string[] nickNames)
                where TParam : class, IGH_Param
            {
                IGH_Param match = previous.FirstOrDefault(parameter =>
                    !used.Contains(parameter) &&
                    parameter is TParam &&
                    nickNames.Any(nickName =>
                        string.Equals(
                            parameter.NickName,
                            nickName,
                            StringComparison.OrdinalIgnoreCase)));
                if (match != null)
                    used.Add(match);
                return match;
            }

            IGH_Param path = Find<Param_GenericObject>("wsp_path");
            IGH_Param radius = Find<Param_Number>("radius");
            IGH_Param brush = Find<Param_Number>("b_strength", "strength");
            IGH_Param smooth = Find<Param_Number>("s_strength");
            IGH_Param falloff = Find<Param_Number>("falloff");
            IGH_Param domain = Find<Param_GenericObject>("mag_domain");
            IGH_Param mask = Find<Param_Geometry>("mask");
            IGH_Param invert = Find<Param_Boolean>("invert");
            IGH_Param move = Find<Param_Vector>("move_prev");
            IGH_Param texture = Find<Param_GenericObject>("texture");

            if (path == null ||
                radius == null ||
                brush == null ||
                falloff == null ||
                domain == null ||
                mask == null ||
                invert == null ||
                move == null ||
                texture == null)
                return;

            if (smooth == null)
            {
                var parameter = new Param_Number
                {
                    Name = "smooth strength",
                    NickName = "s_strength",
                    Description =
                        "Fraction from 0 to 1 by which each Smooth dab approaches the " +
                        "distance-weighted average inside the brush radius.",
                    Access = GH_ParamAccess.item
                };
                parameter.PersistentData.Append(new GH_Number(0.5));
                smooth = parameter;
            }
            texture.Access = GH_ParamAccess.list;

            foreach (IGH_Param parameter in previous)
                Params.UnregisterInputParameter(parameter, false);
            foreach (IGH_Param parameter in new[]
                     {
                         path,
                         radius,
                         brush,
                         smooth,
                         falloff,
                         domain,
                         mask,
                         invert,
                         move,
                         texture
                     })
            {
                Params.RegisterInputParam(parameter);
            }

            brush.Name = "brush strength";
            brush.NickName = "b_strength";
            brush.Description = "Pull, Push, and Erase strength from 0 to 1.";
            smooth.Name = "smooth strength";
            smooth.NickName = "s_strength";
            smooth.Description =
                "Fraction from 0 to 1 by which each Smooth dab approaches the " +
                "distance-weighted average inside the brush radius.";
            EnsureInputDataMapping();
            Params.OnParametersChanged();
        }

        private void EnsureInputDataMapping()
        {
            for (int index = 0; index < Params.Input.Count; index++)
            {
                Params.Input[index].DataMapping = index == 0
                    ? GH_DataMapping.None
                    : GH_DataMapping.Flatten;
            }
        }

        private void EnsureFixedOutputLayout()
        {
            bool valid =
                Params.Output.Count >= 3 &&
                Params.Output[0] is Param_GenericObject &&
                Params.Output[1] is Param_Mesh &&
                Params.Output[2] is Param_String &&
                string.Equals(Params.Output[0].NickName, "wsp_path", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Params.Output[1].NickName, "shell_mesh", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Params.Output[2].NickName, "summary", StringComparison.OrdinalIgnoreCase);
            if (valid)
            {
                Params.Output[1].Description =
                    "Manually generated colored mesh through the current paint-displaced Shell points. Updated only by the " +
                    "component's Update Mesh button.";
                Params.Output[2].Description =
                    "Paint-state, displacement, direction, filtering, and Infill-constraint report.";
                return;
            }

            List<IGH_Param> previous = Params.Output.ToList();
            IGH_Param previousPath = previous.FirstOrDefault(parameter =>
                parameter is Param_GenericObject &&
                string.Equals(parameter.NickName, "wsp_path", StringComparison.OrdinalIgnoreCase))
                ?? previous.FirstOrDefault(parameter => parameter is Param_GenericObject);
            IGH_Param previousMesh = previous.FirstOrDefault(parameter => parameter is Param_Mesh);
            IGH_Param previousSummary = previous.FirstOrDefault(parameter => parameter is Param_String);

            var path = new Param_GenericObject
            {
                Name = "wsp_path",
                NickName = "wsp_path",
                Description = "Path with the painted signed displacement applied to eligible plane origins.",
                Access = GH_ParamAccess.item
            };
            var mesh = new Param_Mesh
            {
                Name = "shell mesh",
                NickName = "shell_mesh",
                Description =
                    "Manually generated colored mesh through the current paint-displaced Shell points. Updated only by the " +
                    "component's Update Mesh button.",
                Access = GH_ParamAccess.item
            };
            var summary = new Param_String
            {
                Name = "summary",
                NickName = "summary",
                Description =
                    "Paint-state, displacement, direction, filtering, and Infill-constraint report.",
                Access = GH_ParamAccess.item
            };
            Params.RegisterOutputParam(path);
            Params.RegisterOutputParam(mesh);
            Params.RegisterOutputParam(summary);

            TransferOutputRecipients(previousPath, path);
            TransferOutputRecipients(previousMesh, mesh);
            TransferOutputRecipients(previousSummary, summary);
            foreach (IGH_Param parameter in previous)
            {
                if (Params.Output.Contains(parameter))
                    Params.UnregisterOutputParameter(parameter, true);
            }
            Params.OnParametersChanged();
        }

        private static void TransferOutputRecipients(
            IGH_Param previous,
            IGH_Param replacement)
        {
            if (previous == null || replacement == null)
                return;
            foreach (IGH_Param recipient in previous.Recipients.ToList())
            {
                recipient.ReplaceSource(previous, replacement);
            }
        }

        private int FixedOutputIndex<TParam>(string nickName)
            where TParam : class, IGH_Param
        {
            for (int index = 0; index < Params.Output.Count; index++)
            {
                if (Params.Output[index] is TParam &&
                    string.Equals(
                        Params.Output[index].NickName,
                        nickName,
                        StringComparison.OrdinalIgnoreCase))
                    return index;
            }
            for (int index = 0; index < Params.Output.Count; index++)
            {
                if (Params.Output[index] is TParam)
                    return index;
            }
            return -1;
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var totalWatch = Stopwatch.StartNew();
            WasperPrintPath source = null;
            if (!WasperGcodeTreeUtil.TryGetPrintPath(da, 0, out source) ||
                source == null || !source.HasPlanes)
            {
                _shellMeshUpdateRequested = false;
                StopPainting();
                _locations.Clear();
                _eligibleByStack.Clear();
                _preparedSource = null;
                _preparationSignature = string.Empty;
                _outputCache = null;
                _directionByPath.Clear();
                if (_conduit != null)
                    _conduit.Enabled = false;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "A wsp_path containing pt_planes is required. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }

            double radius = 10.0;
            double strength = 0.2;
            double smoothStrength = 0.5;
            double falloff = 2.0;
            da.GetData(1, ref radius);
            da.GetData(2, ref strength);
            da.GetData(3, ref smoothStrength);
            da.GetData(4, ref falloff);
            if (PainterRadiusEditable && _uiRadiusOverride.HasValue)
                radius = _uiRadiusOverride.Value;
            else if (!PainterRadiusEditable)
                _uiRadiusOverride = null;
            if (PainterBrushStrengthEditable && _uiStrengthOverride.HasValue)
                strength = _uiStrengthOverride.Value;
            else if (!PainterBrushStrengthEditable)
                _uiStrengthOverride = null;
            if (PainterSmoothStrengthEditable && _uiSmoothOverride.HasValue)
                smoothStrength = _uiSmoothOverride.Value;
            else if (!PainterSmoothStrengthEditable)
                _uiSmoothOverride = null;
            if (!double.IsFinite(radius) || radius <= RhinoMath.ZeroTolerance)
                radius = 10.0;
            if (!double.IsFinite(strength))
                strength = 0.2;
            if (!double.IsFinite(smoothStrength))
                smoothStrength = 0.5;
            if (!double.IsFinite(falloff) || falloff <= 0.0)
                falloff = 2.0;
            _radius = radius;
            _strength = Math.Max(0.0, Math.Min(1.0, strength));
            _smoothStrength = Math.Max(0.0, Math.Min(1.0, smoothStrength));
            _falloff = falloff;

            if (!WasperPaintUtilities.TryGetDomain(da, 5, out Interval domain, out string domainError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, domainError);
                return;
            }
            _domain = domain;

            int shellRole = (int)global::WASPer_3DP.WasperPathRole.Shell;
            var targetRoles = new List<int> { shellRole };

            var rawMasks = new List<GeometryBase>();
            bool invert = false;
            Vector3d previewMove = Vector3d.Zero;
            da.GetDataList(6, rawMasks);
            da.GetData(7, ref invert);
            da.GetData(8, ref previewMove);
            var rawTextures = new List<object>();
            da.GetDataList(9, rawTextures);
            PrepareTextureSources(rawTextures, out string textureError);
            if (!string.IsNullOrEmpty(textureError))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    textureError);
            }
            if (!previewMove.IsValid)
            {
                previewMove = Vector3d.Zero;
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "move_prev was invalid and has been ignored.");
            }
            _previewMove = previewMove;
            _previewPlane = Plane.WorldXY;
            double tolerance = Math.Max(
                RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
                1e-9);
            List<WasperPaintMaskRegion> masks = WasperPaintMaskRegion.Build(
                rawMasks,
                tolerance,
                out int rejectedMasks);
            if (rejectedMasks > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{rejectedMasks} unsupported or open mask item(s) were ignored.");
            }

            List<PaintBranchLayout> branchLayout = BuildPaintBranchLayout(
                source,
                targetRoles);
            bool sameSourceContent =
                (ReferenceEquals(source, _source) ||
                 (!string.IsNullOrEmpty(source.ContentSignature) &&
                  string.Equals(
                      source.ContentSignature,
                      _source?.ContentSignature,
                      StringComparison.Ordinal))) &&
                !string.IsNullOrEmpty(_signature) &&
                !string.IsNullOrEmpty(_topologySignature);
            string signature = sameSourceContent
                ? _signature
                : ComputeSignature(branchLayout);
            string topologySignature = sameSourceContent
                ? _topologySignature
                : ComputeTopologySignature(branchLayout);
            bool hasPreviousLayout = _paintBranchLayout.Count > 0;
            bool sameLinearOrder = hasPreviousLayout &&
                                   SameLinearGeometryOrder(
                                       _paintBranchLayout,
                                       branchLayout);
            bool samePathOrder = hasPreviousLayout &&
                                 SamePathOrder(_paintBranchLayout, branchLayout);
            double[] remappedValues = null;
            double[] remappedAppliedValues = null;
            bool sameShellGeometry = hasPreviousLayout &&
                TryRemapBranchValues(
                    _paintBranchLayout,
                    branchLayout,
                    _values,
                    out remappedValues) &&
                TryRemapBranchValues(
                    _paintBranchLayout,
                    branchLayout,
                    _appliedValues,
                    out remappedAppliedValues);

            if (sameShellGeometry)
            {
                if (!sameLinearOrder)
                {
                    _values = remappedValues;
                    _appliedValues = remappedAppliedValues;
                    ClearPainterHistory();
                }
                if (!sameLinearOrder || !samePathOrder)
                {
                    _preparationSignature = string.Empty;
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        "Shell branches were renumbered; existing paint was preserved by geometry.");
                }
                _signature = signature;
                _topologySignature = topologySignature;
            }
            else
            {
                InitializePath(
                    source,
                    targetRoles,
                    signature,
                    topologySignature,
                    branchLayout);
            }
            _topologySignature = topologySignature;
            _paintBranchLayout = branchLayout;
            _source = source;

            string preparationSignature = ComputePreparationSignature(
                signature,
                targetRoles,
                rawMasks,
                invert,
                previewMove,
                tolerance);
            bool rebuildPreparation =
                !string.Equals(
                    preparationSignature,
                    _preparationSignature,
                    StringComparison.Ordinal);

            if (rebuildPreparation)
            {
                BuildLocations(source, targetRoles, masks, invert);
                Dictionary<string, List<Point3d>> infillByLayer = BuildInfillPoints(source);
                _directionByPath.Clear();
                foreach (GH_Path path in source.PtPlanes.Paths)
                {
                    ShellDirection direction = ShellDirection.Create(
                        source,
                        path,
                        infillByLayer,
                        tolerance);
                    if (direction != null)
                        _directionByPath[path.ToString()] = direction;
                }
                BuildSurfacePreviewMesh(tolerance, _directionByPath);
                BuildPainterAtlas(tolerance);
                _preparedSource = source;
                _preparationSignature = preparationSignature;
            }

            ClampValuesToDomain();
            if (_live)
                ApplyPreviewState();
            EnsureTexturePlacement();
            if (_shellMeshUpdateRequested)
            {
                CaptureShellMeshFromPaint(_directionByPath, tolerance);
                _shellMeshUpdateRequested = false;
            }
            if (_preview && _previewMesh == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "A Shell-sheet preview mesh could not be formed because no selected stack " +
                    "contained at least two corresponding path rows. Painting can still use the direct point fallback.");
            }
            if (_atlasMaxDistortion > 0.15)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"The Shell atlas is approximate: maximum local edge distortion is " +
                    $"{_atlasMaxDistortion:P1} (RMS {_atlasRmsDistortion:P1}). " +
                    "This is expected for strongly non-developable or irregularly stacked shells.");
            }
            if (Math.Max(_domain.T0, _domain.T1) <= tolerance)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "mag_domain has no positive range, so Pull cannot create outward displacement.");
            }
            if (Math.Min(_domain.T0, _domain.T1) >= -tolerance)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "mag_domain has no negative range, so Push cannot create inward displacement.");
            }
            UpdateConduit();

            bool outputCacheHit =
                _outputCache != null &&
                (ReferenceEquals(source, _outputCache.Source) ||
                 (!string.IsNullOrEmpty(source.ContentSignature) &&
                  string.Equals(
                      source.ContentSignature,
                      _outputCache.Source?.ContentSignature,
                      StringComparison.Ordinal))) &&
                string.Equals(
                    _preparationSignature,
                    _outputCache.PreparationSignature,
                    StringComparison.Ordinal) &&
                _constrainInfill == _outputCache.ConstrainInfill &&
                Math.Abs(tolerance - _outputCache.Tolerance) <= 1e-12 &&
                WasperPaintUtilities.ValuesEqual(
                    _appliedValues,
                    _outputCache.AppliedValues);

            int moved;
            int directionFallbacks;
            double maximum;
            bool cleared;
            WasperPrintPath result;
            WasperInfillConstraintReport constraintReport;

            if (outputCacheHit)
            {
                moved = _outputCache.Moved;
                directionFallbacks = _outputCache.DirectionFallbacks;
                maximum = _outputCache.Maximum;
                cleared = _outputCache.Cleared;
                result = _outputCache.Result;
                constraintReport = _outputCache.ConstraintReport;
            }
            else
            {
                bool hasAppliedDisplacement = _appliedValues.Any(
                    value => Math.Abs(value) > tolerance);
                DataTree<Plane> outputPlanes = hasAppliedDisplacement
                    ? DuplicatePlanes(source.PtPlanes)
                    : null;
                moved = 0;
                directionFallbacks = 0;
                maximum = 0.0;

                for (int index = 0; hasAppliedDisplacement && index < _locations.Count; index++)
                {
                    PaintLocation location = _locations[index];
                    if (!location.Eligible)
                        continue;
                    double displacement = index < _appliedValues.Length
                        ? _appliedValues[index]
                        : 0.0;
                    if (Math.Abs(displacement) <= tolerance)
                        continue;
                    IList<Plane> inputBranch = source.PtPlanes.Branch(location.Path);
                    IList<Plane> outputBranch = outputPlanes.Branch(location.Path);
                    if (inputBranch == null || outputBranch == null ||
                        location.Item < 0 || location.Item >= inputBranch.Count)
                        continue;

                    int logicalCount = IsDuplicateClosure(inputBranch, tolerance)
                        ? inputBranch.Count - 1
                        : inputBranch.Count;
                    Vector3d tangent = LocalTangent(inputBranch, location.Item, logicalCount);
                    Vector3d inward;
                    Plane plane = outputBranch[location.Item];
                    if (_directionByPath.TryGetValue(location.Path.ToString(), out ShellDirection shellDirection))
                    {
                        inward = shellDirection.InwardFromTangent(
                            plane.Origin,
                            tangent,
                            tolerance);
                    }
                    else
                    {
                        Vector3d lateral = Vector3d.CrossProduct(plane.ZAxis, tangent);
                        if (!lateral.Unitize())
                            continue;
                        inward = lateral;
                        directionFallbacks++;
                    }
                    if (!inward.Unitize())
                        continue;

                    plane.Origin -= inward * displacement;
                    outputBranch[location.Item] = plane;
                    moved++;
                    maximum = Math.Max(maximum, Math.Abs(displacement));
                }

                foreach (GH_Path path in outputPlanes?.Paths ?? Enumerable.Empty<GH_Path>())
                {
                    IList<Plane> original = source.PtPlanes.Branch(path);
                    IList<Plane> output = outputPlanes.Branch(path);
                    if (IsDuplicateClosure(original, tolerance) && output.Count > 1)
                    {
                        Plane closing = output[output.Count - 1];
                        closing.Origin = output[0].Origin;
                        output[output.Count - 1] = closing;
                    }
                }

                cleared = moved > 0 && HasDerivedData(source);
                result = moved == 0
                    ? source
                    : new WasperPrintPath(
                        points: null,
                        ptPlanes: outputPlanes,
                        flows: source.Flows,
                        layerH: source.LayerH,
                        printSpeed: source.PrintSpeed,
                        nozzleDiam: source.NozzleDiam,
                        layerW: source.LayerW,
                        layerWf: source.LayerWf,
                        printVol: null,
                        travelSpeed: source.TravelSpeed,
                        zHop: source.ZHop,
                        zHopSpeed: source.ZHopSpeed,
                        isPartial: source.IsPartial,
                        sourceCurves: BuildSourceCurves(outputPlanes, source.SourceCurves, tolerance),
                        pathRoles: source.PathRoles,
                        layerPlanes: source.LayerPlanes);

                constraintReport = null;
                if (_constrainInfill && moved > 0)
                {
                    result = WasperInfillShellConstraint.Apply(
                        source,
                        result,
                        tolerance,
                        out constraintReport);
                }

                _outputCache = new OutputCacheEntry
                {
                    Source = source,
                    Result = result,
                    PreparationSignature = _preparationSignature,
                    AppliedValues = (double[])_appliedValues.Clone(),
                    ConstrainInfill = _constrainInfill,
                    Tolerance = tolerance,
                    Moved = moved,
                    DirectionFallbacks = directionFallbacks,
                    Maximum = maximum,
                    Cleared = cleared,
                    ConstraintReport = constraintReport
                };
            }

            if (directionFallbacks > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{directionFallbacks} painted location(s) were on open or non-planar branches; " +
                    "their consistent local lateral direction was used because material-side classification was unavailable.");
            }
            if (cleared)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Paint deformation changed path geometry; deposited volume, analysis, motion, and KPI fields were cleared.");
            }

            totalWatch.Stop();
            string summary =
                $"Paint fuzzy skin | points={_locations.Count} | eligible={_locations.Count(item => item.Eligible)} | " +
                $"painted={_appliedValues.Count(value => Math.Abs(value) > tolerance)} | moved={moved} | " +
                $"paint range=[{_appliedValues.DefaultIfEmpty(0.0).Min():0.###}," +
                $"{_appliedValues.DefaultIfEmpty(0.0).Max():0.###}] | " +
                $"domain=[{Math.Min(domain.T0, domain.T1):0.###},{Math.Max(domain.T0, domain.T1):0.###}] | " +
                $"brush strength={_strength:0.###} | smooth strength={_smoothStrength:0.###} | " +
                $"max displacement={maximum:0.###} | preview={(_preview ? "on" : "off")} | " +
                $"move_prev=({_previewMove.X:0.###},{_previewMove.Y:0.###},{_previewMove.Z:0.###}) | " +
                $"textures={_textureLayers.Take(MaximumInputTextureLayers).Count(layer => layer.Bitmap != null)}/5 loaded, " +
                $"text textures={_textureLayers.Skip(MaximumInputTextureLayers).Count(layer => layer.Bitmap != null && layer.TextCommitted)}/5 committed, " +
                $"{_textureLayers.Count(layer => layer.Bitmap != null && layer.Visible)} visible, " +
                $"active={_activeTextureLayer + 1}; atlas flip={(_atlasFlipMap ? "on" : "off")}; " +
                $"atlas rotation={_atlasQuarterTurns * 90}° | " +
                $"shell_mesh={(_outputShellMesh == null ? "not captured" : $"{_outputShellMesh.Vertices.Count} vertices/{_outputShellMesh.Faces.Count} faces (manual)")} | " +
                $"atlas tiles={_atlasTileCount} | atlas RMS distortion={_atlasRmsDistortion:P1} | " +
                $"atlas max distortion={_atlasMaxDistortion:P1} | " +
                $"normalized rows reversed={_normalizedRowReversals}; " +
                $"orientation planes={_normalizationAuthoritativePlanes} authoritative/" +
                $"{_normalizationFittedPlanes} fitted/{_normalizationUnresolvedRows} unresolved | " +
                $"painter preparation={(rebuildPreparation ? "rebuilt" : "reused")} | " +
                $"path output={(outputCacheHit ? "reused" : "rebuilt")} | " +
                $"live={(_live ? "on" : "paused")} | pending update={(HasPendingUpdate ? "yes" : "no")} | " +
                $"tool={_tool} | visible-surface stroke lock=on | direction fallbacks={directionFallbacks} | " +
                $"{(_constrainInfill ? constraintReport?.Summary ?? "infill constraint: not applicable" : "infill constraint: off")}.\n" +
                $"performance [ms]: total={totalWatch.Elapsed.TotalMilliseconds:0.###}";
            int pathOutput = FixedOutputIndex<Param_GenericObject>("wsp_path");
            int meshOutput = FixedOutputIndex<Param_Mesh>("shell_mesh");
            int summaryOutput = FixedOutputIndex<Param_String>("summary");
            if (pathOutput >= 0)
                da.SetData(pathOutput, new WasperPrintPathGoo(result));
            if (meshOutput >= 0 && _outputShellMesh != null)
                da.SetData(meshOutput, _outputShellMesh);
            if (summaryOutput >= 0)
                da.SetData(summaryOutput, summary);
            WasperPathDebugOutputs.SetCore(da, this, result);
            int paintIndex = WasperPathDebugOutputs.OutputIndex(this, "paint_values");
            if (paintIndex >= 0)
            {
                DataTree<double> paintValues = BuildPaintTree(source, _appliedValues);
                da.SetDataTree(paintIndex, WasperGcodeTreeUtil.ToNumberStructure(paintValues));
            }
            Message = $"{_versionTag} | {(_live ? "Live" : HasPendingUpdate ? "Paused*" : "Paused")}";
        }

        private static string ComputePreparationSignature(
            string selectedPathSignature,
            IEnumerable<int> targetRoles,
            IEnumerable<GeometryBase> rawMasks,
            bool invert,
            Vector3d previewMove,
            double tolerance)
        {
            var builder = new StringBuilder();
            builder.Append(selectedPathSignature ?? string.Empty).Append('|');
            foreach (int role in targetRoles)
                builder.Append(role).Append(',');
            builder.Append('|').Append(invert ? '1' : '0');
            builder.Append('|').Append(BitConverter.DoubleToInt64Bits(previewMove.X));
            builder.Append('|').Append(BitConverter.DoubleToInt64Bits(previewMove.Y));
            builder.Append('|').Append(BitConverter.DoubleToInt64Bits(previewMove.Z));
            builder.Append('|').Append(BitConverter.DoubleToInt64Bits(tolerance));
            foreach (GeometryBase mask in rawMasks ?? Enumerable.Empty<GeometryBase>())
            {
                builder.Append('|').Append(mask?.GetType().FullName ?? "null");
                builder.Append(':').Append(mask?.DataCRC(0) ?? 0u);
            }
            return builder.ToString();
        }

        private sealed class OutputCacheEntry
        {
            internal WasperPrintPath Source;
            internal WasperPrintPath Result;
            internal string PreparationSignature;
            internal double[] AppliedValues;
            internal bool ConstrainInfill;
            internal double Tolerance;
            internal int Moved;
            internal int DirectionFallbacks;
            internal double Maximum;
            internal bool Cleared;
            internal WasperInfillConstraintReport ConstraintReport;
        }

    }
}
