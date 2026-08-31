using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;
using Newtonsoft.Json;

using Rhino;
using Rhino.Geometry;

using WASPer_3DP.Painting;

namespace WASPer_3DP.Components._2_2_Fields
{
    /// <summary>
    /// Experimental painted Fi01 kept beside the original component while its
    /// field-painting workflow is validated.
    /// </summary>
    public sealed class wsp_Fi01_Paint_Distance_Field_2D :
        wsp_Fi01_Distance_Field_2D
    {
        private Mesh _painterMesh;
        private double[] _baseValues = Array.Empty<double>();
        private double[] _paintValues = Array.Empty<double>();
        private double[] _appliedPaintValues = Array.Empty<double>();
        private string _gridSignature = string.Empty;

        private double _radius = 10.0;
        private double _strength = 0.2;
        private double _smoothStrength = 0.5;
        private double _falloff = 2.0;
        private Interval _paintDomain = new Interval(-5.0, 5.0);
        private WasperPaintTool _tool;
        private WasperSmoothRegionShape _smoothRegionShape =
            WasperSmoothRegionShape.Square;
        private bool _preview = true;
        private bool _live = true;
        private bool _strokeActive;
        private bool _strokeChanged;
        private Point3d _lastStrokePoint = Point3d.Unset;
        private readonly List<Point3d> _zeroStrokePoints = new List<Point3d>();
        private double[] _strokeBefore;
        private int _hoverIndex = -1;
        private int _visualRevision;
        private int _atlasQuarterTurns;
        private bool _atlasFlip;

        private readonly Stack<PaintHistoryEntry> _undo =
            new Stack<PaintHistoryEntry>();
        private readonly Stack<PaintHistoryEntry> _redo =
            new Stack<PaintHistoryEntry>();
        private readonly List<WasperPaintMarker> _markers =
            new List<WasperPaintMarker>();
        private readonly List<WasperPaintTextureLayer> _textureLayers =
            Enumerable.Range(0, 5).Select(_ => new WasperPaintTextureLayer()).ToList();
        private WasperEtoPaintForm _paintForm;
        private WasperPaintConduit _conduit;
        private WasperPaintState _pendingState;
        private const string PaintStateKey = "wsp_fi01_paint_field_state";
        private const string FieldCollectionKey = "wsp_fi01_paint_field_collection";
        private FieldCollectionState _pendingCollection;
        private readonly List<FieldPage> _fields = new List<FieldPage>();
        private readonly HashSet<int> _removedSourceSlots = new HashSet<int>();
        private readonly List<(int Field, int Source)> _painterVertexMap =
            new List<(int Field, int Source)>();
        private int _activeFieldIndex;
        private bool _fieldArrangeMode;
        private int _draggedFieldIndex = -1;
        private double _fieldDragStartX;
        private double _fieldDragOriginalX;

        private sealed class FieldPage
        {
            internal double[] Base = Array.Empty<double>();
            internal double[] OriginalBase = Array.Empty<double>();
            internal double[] Paint = Array.Empty<double>();
            internal double[] Applied = Array.Empty<double>();
            internal int OriginalNx;
            internal int OriginalNy;
            internal double OriginalIsoOffset;
            internal int Nx;
            internal int Ny;
            internal Plane Plane = Plane.WorldXY;
            internal Point2d Center;
            internal double FrameSize = 100.0;
            internal double CellSize = 5.0;
            internal double IsoOffset;
            internal double PositionX;
            internal string Signature = string.Empty;
            internal bool Manual;
            internal int SourceSlot = -1;
            internal bool Customized;
        }

        private sealed class PaintHistoryEntry
        {
            internal FieldPage Page;
            internal double[] Base;
            internal double[] Values;
            internal double IsoOffset;
        }

        private sealed class FieldCollectionState
        {
            public int ActiveIndex;
            public List<int> RemovedSourceSlots = new List<int>();
            public List<FieldPageState> Fields = new List<FieldPageState>();
        }

        private sealed class FieldPageState
        {
            public double[] Base;
            public double[] OriginalBase;
            public double[] Paint;
            public double[] Applied;
            public int Nx;
            public int Ny;
            public int OriginalNx;
            public int OriginalNy;
            public double OriginalIsoOffset;
            public double[] Plane;
            public double CenterX;
            public double CenterY;
            public double FrameSize;
            public double CellSize;
            public double IsoOffset;
            public double PositionX;
            public bool Manual;
            public int SourceSlot;
            public bool Customized;
        }

        public wsp_Fi01_Paint_Distance_Field_2D()
            : base()
        {
            Name = "wsp_Fi01_Paint Distance Field 2D";
            NickName = "Paint DistField";
            Description =
                "Creates a blank or curve-based field, or accepts a packed 2D field, then adds a " +
                "non-destructive painted scalar layer. WorldXY at the origin is used when ref_pl is empty. " +
                "Add raises values, Subtract lowers them, Zero draws the contour value, Erase restores " +
                "the base field, closed Zero strokes fill their interior negative, and Smooth works " +
                "on square or freeform regions.";
        }

        public override Guid ComponentGuid =>
            new Guid("A0FD7A72-289F-4B67-8DD1-3D526356E918");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                using Bitmap source = base.Icon;
                if (source == null)
                    return null;
                var icon = new Bitmap(source);
                using Graphics graphics = Graphics.FromImage(icon);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var badge = new SolidBrush(Color.FromArgb(235, 250, 250, 250));
                using var badgeOutline = new Pen(Color.FromArgb(185, 40, 46, 56), 1f);
                graphics.FillEllipse(badge, 11.5f, 11.5f, 12f, 12f);
                graphics.DrawEllipse(badgeOutline, 11.5f, 11.5f, 12f, 12f);
                using var handle = new Pen(Color.FromArgb(105, 65, 35), 2.4f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                graphics.DrawLine(handle, 20.5f, 13.5f, 16.0f, 18.0f);
                using var ferrule = new Pen(Color.FromArgb(105, 120, 135), 2.8f)
                {
                    StartCap = LineCap.Square,
                    EndCap = LineCap.Square
                };
                graphics.DrawLine(ferrule, 16.2f, 17.8f, 14.6f, 19.4f);
                PointF[] bristles =
                {
                    new PointF(13.7f, 18.6f),
                    new PointF(16.0f, 20.9f),
                    new PointF(12.0f, 22.0f)
                };
                using var paint = new SolidBrush(Color.FromArgb(35, 145, 225));
                graphics.FillPolygon(paint, bristles);
                return icon;
            }
        }

        public override void CreateAttributes()
        {
            m_attributes = new Fi01PaintAttributes(this);
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "field in",
                "field_in",
                "Optional packed Fi fields. Each item becomes one painter field; curve sampling is skipped.",
                GH_ParamAccess.list);
            p[0].Optional = true;
            base.RegisterInputParams(p);
            p[1].Optional = true;
            p[2].Optional = true;
            if (p[5] is Param_Number frameSize)
            {
                frameSize.PersistentData.Clear();
                frameSize.PersistentData.Append(new GH_Number(100.0));
            }
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            base.RegisterOutputParams(p);
            p[0].Access = GH_ParamAccess.list;
            p[1].Access = GH_ParamAccess.list;
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            StoreActivePage();
            var rawFields = new List<object>();
            bool hasFields = da.GetDataList(0, rawFields) && rawFields.Count > 0;
            var sourcePages = new List<FieldPage>();
            if (hasFields)
            {
                foreach (object raw in rawFields)
                {
                    object value = raw is IGH_Goo goo ? goo.ScriptVariable() : raw;
                    if (!(value is wsp_Fi01_Distance_Field_2D.WspFieldGrid2D field))
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Error,
                            "Every field_in item must be a packed 2D Fi field.");
                        return;
                    }
                    LoadBaseField(field);
                    FieldPage sourcePage = PageFromCurrent(false);
                    sourcePage.SourceSlot = sourcePages.Count;
                    sourcePages.Add(sourcePage);
                }
            }
            else
            {
                var curves = new List<Curve>();
                bool hasCurves = da.GetDataList(1, curves) &&
                    curves.Any(curve => curve != null && curve.IsValid);
                if (hasCurves)
                {
                    if (!SolveDistanceField(da, 1, true, false))
                        return;
                }
                else
                {
                    BuildBlankBaseField(da);
                }
                FieldPage sourcePage = PageFromCurrent(false);
                sourcePage.SourceSlot = 0;
                sourcePages.Add(sourcePage);
            }

            ReconcileSourcePages(sourcePages);
            if (_pendingCollection != null)
            {
                RestoreCollection(_pendingCollection);
                _pendingCollection = null;
            }
            _activeFieldIndex = Math.Max(
                0,
                Math.Min(_activeFieldIndex, _fields.Count - 1));
            LoadActivePage();
            RebuildPainterMesh();
            SetCombinedOutputs(da);
            UpdateConduit();
            Message = _live ? "Paint Field | Live" : "Paint Field | Paused";
        }

        private FieldPage PageFromCurrent(bool manual)
        {
            var page = new FieldPage
            {
                Base = (double[])LastFieldValues.Clone(),
                Nx = LastNxVerts,
                Ny = LastNyVerts,
                Plane = LastFieldPlane,
                Center = LastCenterXY,
                FrameSize = LastFrameSize,
                CellSize = LastCellSize,
                IsoOffset = LastIsoOffset,
                Manual = manual
            };
            page.Paint = new double[page.Base.Length];
            page.Applied = new double[page.Base.Length];
            page.OriginalBase = (double[])page.Base.Clone();
            page.OriginalNx = page.Nx;
            page.OriginalNy = page.Ny;
            page.OriginalIsoOffset = page.IsoOffset;
            page.Signature = PageSignature(page);
            return page;
        }

        private static void RefreshOriginalSource(
            FieldPage target,
            FieldPage incoming)
        {
            target.OriginalBase = (double[])incoming.Base.Clone();
            target.OriginalNx = incoming.Nx;
            target.OriginalNy = incoming.Ny;
            target.OriginalIsoOffset = incoming.IsoOffset;
        }

        private void ReconcileSourcePages(IList<FieldPage> sources)
        {
            FieldPage previouslyActive = _fields.Count > 0 &&
                _activeFieldIndex >= 0 && _activeFieldIndex < _fields.Count
                    ? _fields[_activeFieldIndex]
                    : null;
            Dictionary<int, FieldPage> existingSources = _fields
                .Where(page => !page.Manual && page.SourceSlot >= 0)
                .ToDictionary(page => page.SourceSlot, page => page);
            List<FieldPage> manualPages = _fields.Where(page => page.Manual).ToList();
            var rebuilt = new List<FieldPage>();
            for (int i = 0; i < sources.Count; i++)
            {
                FieldPage incoming = sources[i];
                if (_removedSourceSlots.Contains(incoming.SourceSlot))
                    continue;
                if (existingSources.TryGetValue(
                        incoming.SourceSlot,
                        out FieldPage existing) && existing.Customized)
                {
                    // A Customized page keeps its painted content across solves instead of
                    // being rebuilt from field_in/curves every time. But res/f_size are still
                    // live GH inputs: if they now produce different grid dimensions than this
                    // page currently has, resample the painted arrays onto the new grid (same
                    // approach UpdateFieldSettings already uses for its own resolution slider)
                    // rather than silently ignoring the input change.
                    bool gridChanged = existing.Nx != incoming.Nx ||
                                        existing.Ny != incoming.Ny ||
                                        Math.Abs(existing.FrameSize - incoming.FrameSize) > 1e-9 ||
                                        Math.Abs(existing.CellSize - incoming.CellSize) > 1e-9;
                    if (gridChanged)
                    {
                        existing.Base = Resample(
                            existing.Base, existing.Nx, existing.Ny, incoming.Nx, incoming.Ny);
                        existing.Paint = Resample(
                            existing.Paint, existing.Nx, existing.Ny, incoming.Nx, incoming.Ny);
                        existing.Applied = Resample(
                            existing.Applied, existing.Nx, existing.Ny, incoming.Nx, incoming.Ny);
                        existing.Nx = incoming.Nx;
                        existing.Ny = incoming.Ny;
                        existing.FrameSize = incoming.FrameSize;
                        existing.CellSize = incoming.CellSize;
                        existing.Signature = PageSignature(existing);
                    }
                    existing.Plane = incoming.Plane;
                    RefreshOriginalSource(existing, incoming);
                    rebuilt.Add(existing);
                }
                else if (existing != null &&
                         existing.Signature == incoming.Signature &&
                         existing.Base.Length == incoming.Base.Length)
                {
                    existing.Base = incoming.Base;
                    existing.Plane = incoming.Plane;
                    existing.IsoOffset = incoming.IsoOffset;
                    RefreshOriginalSource(existing, incoming);
                    rebuilt.Add(existing);
                }
                else
                {
                    rebuilt.Add(incoming);
                }
            }
            rebuilt.AddRange(manualPages);
            _fields.Clear();
            _fields.AddRange(rebuilt);
            _fields.Sort((a, b) => a.PositionX.CompareTo(b.PositionX));
            if (previouslyActive != null && _fields.Contains(previouslyActive))
                _activeFieldIndex = _fields.IndexOf(previouslyActive);
            LayoutUnpositionedFields();
        }

        private static string PageSignature(FieldPage page) => string.Join(
            "|", page.Nx, page.Ny, page.FrameSize.ToString("R"),
            page.CellSize.ToString("R"), page.Plane.OriginX.ToString("R"),
            page.Plane.OriginY.ToString("R"), page.Plane.OriginZ.ToString("R"));

        private void LayoutUnpositionedFields()
        {
            double cursor = 0.0;
            for (int i = 0; i < _fields.Count; i++)
            {
                FieldPage page = _fields[i];
                if (i == 0 && Math.Abs(page.PositionX) <= 1e-12)
                    page.PositionX = 0.0;
                else if (Math.Abs(page.PositionX) <= 1e-12)
                    page.PositionX = cursor + page.FrameSize * 0.5 + 20.0;
                cursor = page.PositionX + page.FrameSize * 0.5;
            }
        }

        private void StoreActivePage()
        {
            if (_fields.Count == 0 || _activeFieldIndex < 0 ||
                _activeFieldIndex >= _fields.Count || _baseValues.Length == 0)
                return;
            FieldPage page = _fields[_activeFieldIndex];
            page.Base = (double[])_baseValues.Clone();
            page.Paint = (double[])_paintValues.Clone();
            page.Applied = (double[])_appliedPaintValues.Clone();
            page.Nx = LastNxVerts;
            page.Ny = LastNyVerts;
            page.Plane = LastFieldPlane;
            page.Center = LastCenterXY;
            page.FrameSize = LastFrameSize;
            page.CellSize = LastCellSize;
            page.IsoOffset = LastIsoOffset;
            page.Signature = PageSignature(page);
        }

        private void LoadActivePage()
        {
            if (_fields.Count == 0)
                return;
            FieldPage page = _fields[_activeFieldIndex];
            _baseValues = (double[])page.Base.Clone();
            _paintValues = (double[])page.Paint.Clone();
            _appliedPaintValues = (double[])page.Applied.Clone();
            _gridSignature = page.Signature;
            LastFieldValues = (double[])page.Base.Clone();
            LastNxVerts = page.Nx;
            LastNyVerts = page.Ny;
            LastFieldPlane = page.Plane;
            LastCenterXY = page.Center;
            LastFrameSize = page.FrameSize;
            LastCellSize = page.CellSize;
            LastIsoOffset = page.IsoOffset;
            LastFieldMesh = BuildGridMesh(
                page.Nx, page.Ny, page.Plane, page.Center,
                page.FrameSize, page.CellSize);
        }

        private void BuildBlankBaseField(IGH_DataAccess da)
        {
            Plane plane = Plane.WorldXY;
            double isoOffset = 0.0;
            double resolution = 5.0;
            double frameSize = 0.0;
            da.GetData(2, ref plane);
            da.GetData(3, ref isoOffset);
            da.GetData(4, ref resolution);
            da.GetData(5, ref frameSize);
            if (!plane.IsValid || plane == Plane.Unset)
                plane = Plane.WorldXY;
            if (!double.IsFinite(resolution) || resolution <= RhinoMath.ZeroTolerance)
                resolution = 5.0;
            if (!double.IsFinite(frameSize) || frameSize <= RhinoMath.ZeroTolerance)
                frameSize = 100.0;
            int cells = Math.Max(1, (int)Math.Ceiling(frameSize / resolution));
            double cellSize = frameSize / cells;
            int vertices = cells + 1;
            LastFieldValues = Enumerable.Repeat(
                Math.Max(_paintDomain.T0, _paintDomain.T1),
                vertices * vertices).ToArray();
            LastNxVerts = vertices;
            LastNyVerts = vertices;
            LastFieldPlane = plane;
            LastCenterXY = new Point2d(0.0, 0.0);
            LastFrameSize = frameSize;
            LastCellSize = cellSize;
            LastIsoOffset = isoOffset;
            LastFieldMesh = BuildGridMesh(
                vertices,
                vertices,
                plane,
                LastCenterXY,
                frameSize,
                cellSize);
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Remark,
                $"Blank paint field at {(plane == Plane.WorldXY ? "WorldXY origin" : "ref_pl origin")}: " +
                $"{vertices}×{vertices} vertices, size={frameSize:0.###}, cell={cellSize:0.###}.");
        }

        public override bool Write(GH_IWriter writer)
        {
            StoreActivePage();
            if (_fields.Count > 0)
            {
                string json = JsonConvert.SerializeObject(CaptureCollection());
                writer.SetString(FieldCollectionKey, WasperPaintUtilities.Compress(json));
            }
            if (_paintValues.Length > 0)
                writer.SetString(
                    PaintStateKey,
                    WasperPaintPersistence.SerializeEmbedded(CaptureState()));
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            bool result = base.Read(reader);
            if (reader.ItemExists(FieldCollectionKey))
            {
                try
                {
                    _pendingCollection = JsonConvert.DeserializeObject<FieldCollectionState>(
                        WasperPaintUtilities.Decompress(reader.GetString(FieldCollectionKey)));
                }
                catch
                {
                    _pendingCollection = null;
                }
            }
            if (reader.ItemExists(PaintStateKey))
            {
                try
                {
                    _pendingState = WasperPaintPersistence.DeserializeEmbedded(
                        reader.GetString(PaintStateKey));
                }
                catch
                {
                    _pendingState = null;
                }
            }
            return result;
        }

        private FieldCollectionState CaptureCollection()
        {
            var state = new FieldCollectionState
            {
                ActiveIndex = _activeFieldIndex,
                RemovedSourceSlots = _removedSourceSlots.OrderBy(slot => slot).ToList()
            };
            foreach (FieldPage page in _fields)
            {
                state.Fields.Add(new FieldPageState
                {
                    Base = page.Base,
                    OriginalBase = page.OriginalBase,
                    Paint = page.Paint,
                    Applied = page.Applied,
                    Nx = page.Nx,
                    Ny = page.Ny,
                    OriginalNx = page.OriginalNx,
                    OriginalNy = page.OriginalNy,
                    OriginalIsoOffset = page.OriginalIsoOffset,
                    Plane = new[]
                    {
                        page.Plane.OriginX, page.Plane.OriginY, page.Plane.OriginZ,
                        page.Plane.XAxis.X, page.Plane.XAxis.Y, page.Plane.XAxis.Z,
                        page.Plane.YAxis.X, page.Plane.YAxis.Y, page.Plane.YAxis.Z
                    },
                    CenterX = page.Center.X,
                    CenterY = page.Center.Y,
                    FrameSize = page.FrameSize,
                    CellSize = page.CellSize,
                    IsoOffset = page.IsoOffset,
                    PositionX = page.PositionX,
                    Manual = page.Manual,
                    SourceSlot = page.SourceSlot,
                    Customized = page.Customized
                });
            }
            return state;
        }

        private void RestoreCollection(FieldCollectionState state)
        {
            if (state?.Fields == null || state.Fields.Count == 0)
                return;
            var restored = new List<FieldPage>();
            foreach (FieldPageState item in state.Fields)
            {
                if (item?.Base == null || item.Nx <= 0 || item.Ny <= 0 ||
                    item.Base.Length != item.Nx * item.Ny ||
                    item.Plane == null || item.Plane.Length < 9)
                    continue;
                var plane = new Plane(
                    new Point3d(item.Plane[0], item.Plane[1], item.Plane[2]),
                    new Vector3d(item.Plane[3], item.Plane[4], item.Plane[5]),
                    new Vector3d(item.Plane[6], item.Plane[7], item.Plane[8]));
                var page = new FieldPage
                {
                    Base = (double[])item.Base.Clone(),
                    OriginalBase = item.OriginalBase != null &&
                        item.OriginalNx > 0 && item.OriginalNy > 0 &&
                        item.OriginalBase.Length == item.OriginalNx * item.OriginalNy
                            ? (double[])item.OriginalBase.Clone()
                            : (double[])item.Base.Clone(),
                    Paint = item.Paint?.Length == item.Base.Length
                        ? (double[])item.Paint.Clone()
                        : new double[item.Base.Length],
                    Applied = item.Applied?.Length == item.Base.Length
                        ? (double[])item.Applied.Clone()
                        : new double[item.Base.Length],
                    Nx = item.Nx,
                    Ny = item.Ny,
                    OriginalNx = item.OriginalNx > 0 ? item.OriginalNx : item.Nx,
                    OriginalNy = item.OriginalNy > 0 ? item.OriginalNy : item.Ny,
                    OriginalIsoOffset = item.OriginalBase != null
                        ? item.OriginalIsoOffset
                        : item.IsoOffset,
                    Plane = plane.IsValid ? plane : Plane.WorldXY,
                    Center = new Point2d(item.CenterX, item.CenterY),
                    FrameSize = item.FrameSize,
                    CellSize = item.CellSize,
                    IsoOffset = item.IsoOffset,
                    PositionX = item.PositionX,
                    Manual = item.Manual,
                    SourceSlot = item.SourceSlot,
                    Customized = item.Customized
                };
                page.Signature = PageSignature(page);
                restored.Add(page);
            }
            if (restored.Count == 0)
                return;
            _fields.Clear();
            _fields.AddRange(restored);
            _removedSourceSlots.Clear();
            if (state.RemovedSourceSlots != null)
            {
                foreach (int slot in state.RemovedSourceSlots.Where(slot => slot >= 0))
                    _removedSourceSlots.Add(slot);
            }
            _activeFieldIndex = Math.Max(
                0,
                Math.Min(state.ActiveIndex, _fields.Count - 1));
        }

        private WasperPaintState CaptureState()
        {
            return new WasperPaintState
            {
                Version = 2,
                OwnerInstanceGuid = InstanceGuid.ToString("D"),
                SavedUtc = DateTime.UtcNow,
                Signature = _gridSignature,
                Values = (double[])_paintValues.Clone(),
                AppliedValues = (double[])_appliedPaintValues.Clone(),
                Preview = _preview,
                Radius = _radius,
                BrushStrength = _strength,
                SmoothStrength = _smoothStrength,
                Falloff = _falloff,
                AtlasFlipMap = _atlasFlip,
                AtlasQuarterTurns = _atlasQuarterTurns
            };
        }

        private bool RestoreState(WasperPaintState state)
        {
            if (state?.Values == null || state.Values.Length != _baseValues.Length)
                return false;
            _paintValues = (double[])state.Values.Clone();
            _appliedPaintValues = state.AppliedValues?.Length == _baseValues.Length
                ? (double[])state.AppliedValues.Clone()
                : (double[])_paintValues.Clone();
            _radius = Math.Max(0.001, state.Radius);
            _strength = Math.Max(0.0, Math.Min(1.0, state.BrushStrength));
            _smoothStrength = Math.Max(0.0, Math.Min(1.0, state.SmoothStrength));
            _falloff = Math.Max(0.001, state.Falloff);
            _preview = state.Preview;
            _atlasFlip = false;
            _atlasQuarterTurns = 0;
            return true;
        }

        private void LoadBaseField(wsp_Fi01_Distance_Field_2D.WspFieldGrid2D field)
        {
            LastFieldValues = field.G == null
                ? Array.Empty<double>()
                : (double[])field.G.Clone();
            LastNxVerts = field.NxVerts;
            LastNyVerts = field.NyVerts;
            LastFieldPlane = field.Plane;
            LastCenterXY = field.CenterXY;
            LastFrameSize = field.FrameSize;
            LastCellSize = field.CellSize;
            LastIsoOffset = field.IsoOffset;
            LastFieldMesh = BuildGridMesh(
                field.NxVerts,
                field.NyVerts,
                field.Plane,
                field.CenterXY,
                field.FrameSize,
                field.CellSize);
        }

        private static Mesh BuildGridMesh(
            int nx,
            int ny,
            Plane plane,
            Point2d center,
            double frameSize,
            double cellSize)
        {
            var mesh = new Mesh();
            double width = Math.Max(0.0, (nx - 1) * cellSize);
            double height = Math.Max(0.0, (ny - 1) * cellSize);
            if (frameSize > RhinoMath.ZeroTolerance && nx == ny)
                width = height = frameSize;
            double startX = center.X - width * 0.5;
            double startY = center.Y - height * 0.5;
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                    mesh.Vertices.Add(plane.PointAt(
                        startX + i * (nx <= 1 ? 0.0 : width / (nx - 1)),
                        startY + j * (ny <= 1 ? 0.0 : height / (ny - 1))));
            for (int j = 0; j < ny - 1; j++)
                for (int i = 0; i < nx - 1; i++)
                {
                    int a = i + j * nx;
                    mesh.Faces.AddFace(a, a + 1, a + nx + 1, a + nx);
                }
            mesh.Normals.ComputeNormals();
            return mesh;
        }

        private void EnsurePaintState()
        {
            string signature = string.Join(
                "|",
                LastNxVerts,
                LastNyVerts,
                LastFieldPlane.OriginX.ToString("R"),
                LastFieldPlane.OriginY.ToString("R"),
                LastFieldPlane.OriginZ.ToString("R"),
                LastFrameSize.ToString("R"),
                LastCellSize.ToString("R"));
            if (signature == _gridSignature &&
                _paintValues.Length == _baseValues.Length)
                return;
            _gridSignature = signature;
            _paintValues = new double[_baseValues.Length];
            _appliedPaintValues = new double[_baseValues.Length];
            if (_pendingState != null)
            {
                RestoreState(_pendingState);
                _pendingState = null;
            }
            _undo.Clear();
            _redo.Clear();
        }

        private void RebuildPainterMesh()
        {
            StoreActivePage();
            _painterMesh = new Mesh();
            _painterVertexMap.Clear();
            for (int fieldIndex = 0; fieldIndex < _fields.Count; fieldIndex++)
            {
                FieldPage page = _fields[fieldIndex];
                Mesh atlas = BuildGridMesh(
                    page.Nx,
                    page.Ny,
                    Plane.WorldXY,
                    new Point2d(page.PositionX, 0.0),
                    page.FrameSize,
                    page.CellSize);
                ApplyColors(atlas, Combine(page.Base, page.Paint));
                for (int source = 0; source < atlas.Vertices.Count; source++)
                    _painterVertexMap.Add((fieldIndex, source));
                _painterMesh.Append(atlas);
            }
            if (_painterMesh.Vertices.Count == 0)
            {
                _painterMesh = null;
                return;
            }
            _painterMesh.Normals.ComputeNormals();
            _painterMesh.Compact();
            _visualRevision++;
            _paintForm?.RefreshCanvas();
        }

        private void SetCombinedOutputs(IGH_DataAccess da)
        {
            StoreActivePage();
            var meshes = new List<Mesh>();
            var packedFields = new List<GH_ObjectWrapper>();
            foreach (FieldPage page in _fields)
            {
                double[] combined = Combine(page.Base, page.Applied);
                Mesh output = BuildGridMesh(
                    page.Nx, page.Ny, page.Plane, page.Center,
                    page.FrameSize, page.CellSize);
                ApplyColors(output, combined);
                meshes.Add(output);
                packedFields.Add(new GH_ObjectWrapper(
                    new wsp_Fi01_Distance_Field_2D.WspFieldGrid2D(
                        combined, page.Nx, page.Ny, page.Plane, page.Center,
                        page.FrameSize, page.CellSize, page.IsoOffset)));
            }
            da.SetDataList(0, meshes);
            da.SetDataList(1, packedFields);
        }

        private static double[] Combine(IList<double> baseValues, IList<double> paint)
        {
            int count = Math.Min(baseValues?.Count ?? 0, paint?.Count ?? 0);
            var result = new double[count];
            for (int i = 0; i < count; i++)
                result[i] = baseValues[i] + paint[i];
            return result;
        }

        private static void ApplyColors(Mesh mesh, IList<double> values)
        {
            if (mesh == null || values == null)
                return;
            double amplitude = values.Count == 0
                ? 1.0
                : Math.Max(1e-12, values.Max(value => Math.Abs(value)));
            mesh.VertexColors.Clear();
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                double normalized = i < values.Count
                    ? Math.Max(-1.0, Math.Min(1.0, values[i] / amplitude))
                    : 0.0;
                mesh.VertexColors.Add(DivergingBlueWhiteRed(normalized));
            }
        }

        internal void TogglePainterForm()
        {
            if (_painterMesh == null)
                return;
            if (_paintForm == null || _paintForm.IsClosed)
                _paintForm = new WasperEtoPaintForm(new Fi01PainterHost(this));
            _paintForm.ShowNearCursor();
        }

        internal void SetTool(WasperPaintTool tool)
        {
            _tool = _tool == tool ? WasperPaintTool.None : tool;
            ClearHover();
            UpdateConduit();
            _paintForm?.RefreshCanvas();
        }

        internal bool BeginStroke(Point3d point)
        {
            if (_tool == WasperPaintTool.None || _tool == WasperPaintTool.Smooth ||
                !TryPick(point, out int index, out int source, out int field, out Point3d snapped))
                return false;
            SwitchActiveField(field, false);
            _strokeBefore = (double[])_paintValues.Clone();
            _strokeActive = true;
            _strokeChanged = false;
            _lastStrokePoint = snapped;
            _zeroStrokePoints.Clear();
            if (_tool == WasperPaintTool.Zero)
                _zeroStrokePoints.Add(snapped);
            ApplyBrush(snapped, snapped);
            _hoverIndex = index;
            return true;
        }

        internal void ContinueStroke(Point3d point)
        {
            if (!_strokeActive ||
                !TryPick(point, out int index, out _, out int field, out Point3d snapped) ||
                field != _activeFieldIndex)
                return;
            ApplyBrush(_lastStrokePoint, snapped);
            _lastStrokePoint = snapped;
            if (_tool == WasperPaintTool.Zero &&
                (_zeroStrokePoints.Count == 0 ||
                 _zeroStrokePoints[_zeroStrokePoints.Count - 1]
                     .DistanceToSquared(snapped) > 1e-12))
                _zeroStrokePoints.Add(snapped);
            _hoverIndex = index;
        }

        internal void EndStroke()
        {
            if (!_strokeActive)
                return;
            _strokeActive = false;
            if (_tool == WasperPaintTool.Zero)
                FillClosedZeroStroke();
            if (_strokeChanged)
            {
                PushUndo(_strokeBefore);
                if (_live)
                {
                    _appliedPaintValues = (double[])_paintValues.Clone();
                    ExpireSolution(true);
                }
            }
            _strokeBefore = null;
            _zeroStrokePoints.Clear();
        }

        private void FillClosedZeroStroke()
        {
            if (_zeroStrokePoints.Count < 4 || _painterMesh == null)
                return;
            double cell = Math.Max(0.001, LastCellSize);
            double closeTolerance = Math.Max(
                cell * 1.5,
                Math.Min(_radius * 0.5, cell * 3.0));
            if (_zeroStrokePoints[0].DistanceTo(
                    _zeroStrokePoints[_zeroStrokePoints.Count - 1]) > closeTolerance)
                return;

            double twiceArea = 0.0;
            for (int i = 0; i < _zeroStrokePoints.Count; i++)
            {
                Point3d a = _zeroStrokePoints[i];
                Point3d b = _zeroStrokePoints[(i + 1) % _zeroStrokePoints.Count];
                twiceArea += a.X * b.Y - b.X * a.Y;
            }
            if (Math.Abs(twiceArea) < cell * cell * 2.0)
                return;

            var boundary = new List<Point3d>(_zeroStrokePoints);
            boundary[boundary.Count - 1] = boundary[0];
            double domainMin = Math.Min(_paintDomain.T0, _paintDomain.T1);
            double domainSpan = Math.Abs(_paintDomain.T1 - _paintDomain.T0);
            double negativeTarget = domainMin < 0.0
                ? domainMin
                : -Math.Max(1e-6, domainSpan * 1e-6);
            bool changed = false;
            for (int i = 0; i < _painterMesh.Vertices.Count; i++)
            {
                if (i >= _painterVertexMap.Count ||
                    _painterVertexMap[i].Field != _activeFieldIndex)
                    continue;
                Point3d sample = _painterMesh.Vertices.Point3dAt(i);
                if (!WasperPaintRegion.Contains(sample, boundary, Plane.WorldXY))
                    continue;
                int sourceIndex = _painterVertexMap[i].Source;
                double paint = negativeTarget - _baseValues[sourceIndex];
                if (Math.Abs(paint - _paintValues[sourceIndex]) <= 1e-12)
                    continue;
                _paintValues[sourceIndex] = paint;
                changed = true;
            }
            if (!changed)
                return;
            _strokeChanged = true;
            RebuildPainterMesh();
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private void ApplyBrush(Point3d start, Point3d end)
        {
            bool changed = false;
            for (int i = 0; i < _painterMesh.Vertices.Count; i++)
            {
                if (i >= _painterVertexMap.Count ||
                    _painterVertexMap[i].Field != _activeFieldIndex)
                    continue;
                int sourceIndex = _painterVertexMap[i].Source;
                Point3d sample = _painterMesh.Vertices.Point3dAt(i);
                double distance = DistanceToSegment(sample, start, end);
                if (distance > _radius)
                    continue;
                double influence = WasperPaintBrushKernel.Influence(
                    distance, _radius, _falloff);
                double oldPaint = _paintValues[sourceIndex];
                double amount = _strength * influence;
                double value;
                if (_tool == WasperPaintTool.Erase)
                {
                    value = WasperPaintUtilities.Lerp(oldPaint, 0.0, amount);
                }
                else
                {
                    double oldField = _baseValues[sourceIndex] + oldPaint;
                    double newField = WasperPaintBrushKernel.DirectionalValue(
                        oldField, _tool, _paintDomain, amount);
                    value = newField - _baseValues[sourceIndex];
                }
                if (Math.Abs(value - oldPaint) <= 1e-12)
                    continue;
                _paintValues[sourceIndex] = value;
                changed = true;
            }
            if (!changed)
                return;
            _strokeChanged = true;
            RebuildPainterMesh();
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void ApplySmoothRegion(IList<Point3d> boundary)
        {
            if (boundary == null || boundary.Count < 3 || _painterMesh == null)
                return;
            var selected = new HashSet<int>();
            for (int i = 0; i < _painterMesh.Vertices.Count; i++)
                if (i < _painterVertexMap.Count &&
                    _painterVertexMap[i].Field == _activeFieldIndex &&
                    WasperPaintRegion.Contains(
                        _painterMesh.Vertices.Point3dAt(i), boundary, Plane.WorldXY))
                    selected.Add(_painterVertexMap[i].Source);
            if (selected.Count == 0)
                return;
            double[] before = (double[])_paintValues.Clone();
            double[] source = Combine(_baseValues, _paintValues);
            bool changed = false;
            foreach (int index in selected)
            {
                int x = index % LastNxVerts;
                int y = index / LastNxVerts;
                var neighbors = new[]
                {
                    x > 0 ? index - 1 : -1,
                    x + 1 < LastNxVerts ? index + 1 : -1,
                    y > 0 ? index - LastNxVerts : -1,
                    y + 1 < LastNyVerts ? index + LastNxVerts : -1
                }.Where(candidate => candidate >= 0).ToList();
                double average = (source[index] + neighbors.Sum(i => source[i])) /
                                 (neighbors.Count + 1);
                double fieldValue = WasperPaintBrushKernel.SmoothValue(
                    source[index], average, _smoothStrength, _paintDomain);
                double value = fieldValue - _baseValues[index];
                if (Math.Abs(value - _paintValues[index]) <= 1e-12)
                    continue;
                _paintValues[index] = value;
                changed = true;
            }
            if (!changed)
                return;
            PushUndo(before);
            if (_live)
            {
                _appliedPaintValues = (double[])_paintValues.Clone();
                ExpireSolution(true);
            }
            RebuildPainterMesh();
            UpdateConduit();
        }

        private bool TryPick(
            Point3d point,
            out int index,
            out int source,
            out int field,
            out Point3d snapped)
        {
            index = -1;
            source = -1;
            field = -1;
            snapped = Point3d.Unset;
            MeshPoint hit = _painterMesh?.ClosestMeshPoint(
                point, Math.Max(_radius, LastCellSize) * 0.5);
            if (hit == null)
                return false;
            snapped = hit.Point;
            MeshFace face = _painterMesh.Faces[hit.FaceIndex];
            int[] vertices = face.IsQuad
                ? new[] { face.A, face.B, face.C, face.D }
                : new[] { face.A, face.B, face.C };
            Point3d hitPoint = snapped;
            index = vertices.OrderBy(vertex =>
                _painterMesh.Vertices.Point3dAt(vertex).DistanceToSquared(hitPoint)).First();
            if (index < 0 || index >= _painterVertexMap.Count)
                return false;
            (field, source) = _painterVertexMap[index];
            return field >= 0 && source >= 0;
        }

        private void SwitchActiveField(int fieldIndex, bool rebuild)
        {
            if (fieldIndex < 0 || fieldIndex >= _fields.Count ||
                fieldIndex == _activeFieldIndex)
                return;
            StoreActivePage();
            _activeFieldIndex = fieldIndex;
            LoadActivePage();
            if (rebuild)
                RebuildPainterMesh();
            UpdateConduit();
            _paintForm?.RefreshCanvas();
        }

        private static double DistanceToSegment(Point3d point, Point3d a, Point3d b)
        {
            Vector3d segment = b - a;
            double lengthSquared = segment.SquareLength;
            if (lengthSquared <= 1e-18)
                return point.DistanceTo(a);
            double t = Math.Max(0.0, Math.Min(1.0, ((point - a) * segment) / lengthSquared));
            return point.DistanceTo(a + segment * t);
        }

        private void PushUndo(double[] values)
        {
            if (values == null || _activeFieldIndex < 0 ||
                _activeFieldIndex >= _fields.Count)
                return;
            _undo.Push(new PaintHistoryEntry
            {
                Page = _fields[_activeFieldIndex],
                Base = (double[])_baseValues.Clone(),
                Values = (double[])values.Clone(),
                IsoOffset = LastIsoOffset
            });
            _redo.Clear();
            _paintForm?.RefreshCanvas();
        }

        internal void UndoPaint()
        {
            ApplyPaintHistory(_undo, _redo);
        }

        internal void RedoPaint()
        {
            ApplyPaintHistory(_redo, _undo);
        }

        private void ApplyPaintHistory(
            Stack<PaintHistoryEntry> source,
            Stack<PaintHistoryEntry> destination)
        {
            StoreActivePage();
            PaintHistoryEntry entry = null;
            while (source.Count > 0 && entry == null)
            {
                PaintHistoryEntry candidate = source.Pop();
                if (candidate?.Page != null && candidate.Values != null &&
                    candidate.Base != null &&
                    _fields.Contains(candidate.Page) &&
                    candidate.Values.Length == candidate.Page.Paint.Length &&
                    candidate.Base.Length == candidate.Page.Base.Length)
                    entry = candidate;
            }
            if (entry == null)
            {
                _paintForm?.RefreshCanvas();
                return;
            }
            destination.Push(new PaintHistoryEntry
            {
                Page = entry.Page,
                Base = (double[])entry.Page.Base.Clone(),
                Values = (double[])entry.Page.Paint.Clone(),
                IsoOffset = entry.Page.IsoOffset
            });
            entry.Page.Base = (double[])entry.Base.Clone();
            entry.Page.Paint = (double[])entry.Values.Clone();
            entry.Page.IsoOffset = entry.IsoOffset;
            if (_live)
                entry.Page.Applied = (double[])entry.Values.Clone();
            _activeFieldIndex = _fields.IndexOf(entry.Page);
            LoadActivePage();
            RebuildPainterMesh();
            UpdateConduit();
            if (_live)
                ExpireSolution(true);
        }

        internal void ClearPaint()
        {
            if (_fields.Count == 0 || _activeFieldIndex < 0 ||
                _activeFieldIndex >= _fields.Count)
                return;
            FieldPage page = _fields[_activeFieldIndex];
            double[] original = page.OriginalBase != null &&
                page.OriginalBase.Length == page.OriginalNx * page.OriginalNy &&
                page.OriginalNx > 0 && page.OriginalNy > 0
                    ? Resample(
                        page.OriginalBase,
                        page.OriginalNx,
                        page.OriginalNy,
                        page.Nx,
                        page.Ny)
                    : (double[])_baseValues.Clone();
            bool baseChanged = !WasperPaintUtilities.ValuesEqual(
                original, _baseValues);
            bool offsetChanged = Math.Abs(
                page.IsoOffset - page.OriginalIsoOffset) > 1e-12;
            if (!baseChanged &&
                !offsetChanged &&
                _paintValues.All(value => Math.Abs(value) <= 1e-12))
                return;
            PushUndo((double[])_paintValues.Clone());
            _baseValues = original;
            LastIsoOffset = page.OriginalIsoOffset;
            page.IsoOffset = page.OriginalIsoOffset;
            Array.Clear(_paintValues, 0, _paintValues.Length);
            ApplyLiveChange();
            RebuildPainterMesh();
            UpdateConduit();
        }

        internal void UpdateOutput()
        {
            _appliedPaintValues = (double[])_paintValues.Clone();
            ExpireSolution(true);
        }

        private void ApplyLiveChange()
        {
            if (!_live)
                return;
            _appliedPaintValues = (double[])_paintValues.Clone();
            ExpireSolution(true);
        }

        internal void SaveSession()
        {
            using var dialog = new Eto.Forms.SaveFileDialog
            {
                Title = "Save WASPer Field Paint Session",
                FileName = "Fi01_FieldPaint.wspaint"
            };
            dialog.Filters.Add(new Eto.Forms.FileFilter("WASPer paint session (*.wspaint)", ".wspaint"));
            if (dialog.ShowDialog(_paintForm) == Eto.Forms.DialogResult.Ok)
                WasperPaintPersistence.SaveSession(dialog.FileName, CaptureState());
        }

        internal void LoadSession()
        {
            using var dialog = new Eto.Forms.OpenFileDialog { Title = "Load WASPer Field Paint Session" };
            dialog.Filters.Add(new Eto.Forms.FileFilter("WASPer paint session (*.wspaint)", ".wspaint"));
            if (dialog.ShowDialog(_paintForm) != Eto.Forms.DialogResult.Ok)
                return;
            try
            {
                WasperPaintState state = WasperPaintPersistence.LoadSession(dialog.FileName);
                if (!RestoreState(state))
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        "The paint session grid dimensions do not match this field.");
                    return;
                }
                RebuildPainterMesh();
                UpdateConduit();
                if (_live)
                    ExpireSolution(true);
            }
            catch (Exception exception)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Could not load paint session: " + exception.Message);
            }
        }

        internal void ToggleLive()
        {
            _live = !_live;
            if (_live)
                UpdateOutput();
            _paintForm?.RefreshCanvas();
        }

        internal void AddNewField()
        {
            StoreActivePage();
            FieldPage reference = _fields.Count > 0
                ? _fields[_activeFieldIndex]
                : new FieldPage();
            int cells = Math.Max(
                1,
                (int)Math.Ceiling(reference.FrameSize /
                    Math.Max(0.001, reference.CellSize)));
            int vertices = cells + 1;
            var page = new FieldPage
            {
                Base = Enumerable.Repeat(
                    Math.Max(_paintDomain.T0, _paintDomain.T1),
                    vertices * vertices).ToArray(),
                Paint = new double[vertices * vertices],
                Applied = new double[vertices * vertices],
                Nx = vertices,
                Ny = vertices,
                Plane = reference.Plane,
                Center = new Point2d(0.0, 0.0),
                FrameSize = reference.FrameSize,
                CellSize = reference.FrameSize / cells,
                IsoOffset = reference.IsoOffset,
                Manual = true
            };
            page.OriginalBase = (double[])page.Base.Clone();
            page.OriginalNx = page.Nx;
            page.OriginalNy = page.Ny;
            page.OriginalIsoOffset = page.IsoOffset;
            double right = _fields.Count == 0
                ? 0.0
                : _fields.Max(item => item.PositionX + item.FrameSize * 0.5);
            page.PositionX = _fields.Count == 0
                ? 0.0
                : right + page.FrameSize * 0.5 + 20.0;
            page.Signature = PageSignature(page);
            _fields.Add(page);
            _activeFieldIndex = _fields.Count - 1;
            LoadActivePage();
            _undo.Clear();
            _redo.Clear();
            RebuildPainterMesh();
            UpdateConduit();
            _paintForm?.FitCanvas();
            ExpireSolution(true);
        }

        internal void DuplicateActiveField()
        {
            if (_fields.Count == 0 || _activeFieldIndex < 0 ||
                _activeFieldIndex >= _fields.Count)
                return;
            StoreActivePage();
            FieldPage source = _fields[_activeFieldIndex];
            double spacing = source.FrameSize + 20.0;
            foreach (FieldPage page in _fields)
            {
                if (!ReferenceEquals(page, source) &&
                    page.PositionX > source.PositionX)
                    page.PositionX += spacing;
            }
            var duplicate = new FieldPage
            {
                Base = (double[])source.Base.Clone(),
                OriginalBase = (double[])source.OriginalBase.Clone(),
                Paint = (double[])source.Paint.Clone(),
                Applied = (double[])source.Applied.Clone(),
                Nx = source.Nx,
                Ny = source.Ny,
                OriginalNx = source.OriginalNx,
                OriginalNy = source.OriginalNy,
                OriginalIsoOffset = source.OriginalIsoOffset,
                Plane = source.Plane,
                Center = source.Center,
                FrameSize = source.FrameSize,
                CellSize = source.CellSize,
                IsoOffset = source.IsoOffset,
                PositionX = source.PositionX + spacing,
                Manual = true,
                SourceSlot = -1,
                Customized = true
            };
            duplicate.Signature = PageSignature(duplicate);
            _fields.Add(duplicate);
            _fields.Sort((a, b) => a.PositionX.CompareTo(b.PositionX));
            _activeFieldIndex = _fields.IndexOf(duplicate);
            LoadActivePage();
            _undo.Clear();
            _redo.Clear();
            RebuildPainterMesh();
            UpdateConduit();
            ExpireSolution(true);
        }

        internal void RemoveActiveField()
        {
            if (_fields.Count <= 1 || _activeFieldIndex < 0 ||
                _activeFieldIndex >= _fields.Count)
                return;
            StoreActivePage();
            int removedIndex = _activeFieldIndex;
            FieldPage removed = _fields[removedIndex];
            if (!removed.Manual && removed.SourceSlot >= 0)
                _removedSourceSlots.Add(removed.SourceSlot);
            _fields.RemoveAt(removedIndex);
            _activeFieldIndex = Math.Min(removedIndex, _fields.Count - 1);
            LoadActivePage();
            _undo.Clear();
            _redo.Clear();
            RebuildPainterMesh();
            UpdateConduit();
            _paintForm?.FitCanvas();
            ExpireSolution(true);
        }

        internal void SelectPreviousField()
        {
            if (_fields.Count == 0)
                return;
            SwitchActiveField(
                (_activeFieldIndex - 1 + _fields.Count) % _fields.Count,
                true);
        }

        internal void SelectNextField()
        {
            if (_fields.Count == 0)
                return;
            SwitchActiveField((_activeFieldIndex + 1) % _fields.Count, true);
        }

        internal void MoveActiveField(int direction)
        {
            if (_fields.Count < 2)
                return;
            StoreActivePage();
            int target = Math.Max(
                0,
                Math.Min(_fields.Count - 1, _activeFieldIndex + direction));
            if (target == _activeFieldIndex)
                return;
            FieldPage active = _fields[_activeFieldIndex];
            double targetX = _fields[target].PositionX;
            _fields[target].PositionX = active.PositionX;
            active.PositionX = targetX;
            _fields.RemoveAt(_activeFieldIndex);
            _fields.Insert(target, active);
            _activeFieldIndex = target;
            LoadActivePage();
            RebuildPainterMesh();
            ExpireSolution(true);
        }

        internal void UpdateFieldSettings(
            double offset,
            double resolution,
            double frameSize,
            bool commit)
        {
            resolution = Math.Max(0.001, resolution);
            frameSize = Math.Max(0.1, frameSize);
            StoreActivePage();
            if (_fields.Count == 0 || _activeFieldIndex < 0 ||
                _activeFieldIndex >= _fields.Count)
                return;
            FieldPage active = _fields[_activeFieldIndex];
            double offsetDelta = offset - active.IsoOffset;
            int cells = Math.Max(1, (int)Math.Ceiling(frameSize / resolution));
            int newCount = cells + 1;
            double actualCellSize = frameSize / cells;
            bool gridChanged = _fields.Any(page =>
                page.Nx != newCount || page.Ny != newCount ||
                Math.Abs(page.FrameSize - frameSize) > 1e-9 ||
                Math.Abs(page.CellSize - actualCellSize) > 1e-9);
            if (!gridChanged && Math.Abs(offsetDelta) <= 1e-12)
            {
                if (commit)
                    ExpireSolution(true);
                return;
            }
            if (Math.Abs(offsetDelta) > 1e-12)
            {
                for (int i = 0; i < active.Base.Length; i++)
                    active.Base[i] -= offsetDelta;
                active.IsoOffset = offset;
                active.Customized = true;
                _undo.Clear();
                _redo.Clear();
            }
            if (gridChanged)
            {
                foreach (FieldPage page in _fields)
                {
                    page.Base = Resample(
                        page.Base, page.Nx, page.Ny, newCount, newCount);
                    page.Paint = Resample(
                        page.Paint, page.Nx, page.Ny, newCount, newCount);
                    page.Applied = Resample(
                        page.Applied, page.Nx, page.Ny, newCount, newCount);
                    page.Nx = newCount;
                    page.Ny = newCount;
                    page.FrameSize = frameSize;
                    page.CellSize = actualCellSize;
                    page.Customized = true;
                    page.Signature = PageSignature(page);
                }
                _undo.Clear();
                _redo.Clear();
            }
            LoadActivePage();
            RebuildPainterMesh();
            UpdateConduit();
            if (commit)
                ExpireSolution(true);
        }

        private static double[] Resample(
            IList<double> source,
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            var result = new double[targetWidth * targetHeight];
            if (source == null || sourceWidth <= 0 || sourceHeight <= 0)
                return result;
            for (int y = 0; y < targetHeight; y++)
            {
                double sy = targetHeight <= 1
                    ? 0.0
                    : y * (sourceHeight - 1.0) / (targetHeight - 1.0);
                int y0 = Math.Max(0, Math.Min(sourceHeight - 1, (int)Math.Floor(sy)));
                int y1 = Math.Min(sourceHeight - 1, y0 + 1);
                double ty = sy - y0;
                for (int x = 0; x < targetWidth; x++)
                {
                    double sx = targetWidth <= 1
                        ? 0.0
                        : x * (sourceWidth - 1.0) / (targetWidth - 1.0);
                    int x0 = Math.Max(0, Math.Min(sourceWidth - 1, (int)Math.Floor(sx)));
                    int x1 = Math.Min(sourceWidth - 1, x0 + 1);
                    double tx = sx - x0;
                    double a = WasperPaintUtilities.Lerp(
                        source[x0 + y0 * sourceWidth],
                        source[x1 + y0 * sourceWidth], tx);
                    double b = WasperPaintUtilities.Lerp(
                        source[x0 + y1 * sourceWidth],
                        source[x1 + y1 * sourceWidth], tx);
                    result[x + y * targetWidth] =
                        WasperPaintUtilities.Lerp(a, b, ty);
                }
            }
            return result;
        }

        internal void ToggleFieldArrangeMode()
        {
            _fieldArrangeMode = !_fieldArrangeMode;
            _tool = WasperPaintTool.None;
            _paintForm?.RefreshCanvas();
        }

        internal bool SelectFieldAt(Point3d point)
        {
            if (!TryPick(point, out _, out _, out int field, out _))
                return false;
            SwitchActiveField(field, false);
            return true;
        }

        internal bool BeginFieldDrag(Point3d point)
        {
            for (int i = _fields.Count - 1; i >= 0; i--)
            {
                FieldPage page = _fields[i];
                if (point.X < page.PositionX - page.FrameSize * 0.5 ||
                    point.X > page.PositionX + page.FrameSize * 0.5 ||
                    point.Y < -page.FrameSize * 0.5 ||
                    point.Y > page.FrameSize * 0.5)
                    continue;
                SwitchActiveField(i, false);
                _draggedFieldIndex = i;
                _fieldDragStartX = point.X;
                _fieldDragOriginalX = page.PositionX;
                return true;
            }
            return false;
        }

        internal void MoveFieldDrag(Point3d point)
        {
            if (_draggedFieldIndex < 0 || _draggedFieldIndex >= _fields.Count)
                return;
            _fields[_draggedFieldIndex].PositionX =
                _fieldDragOriginalX + point.X - _fieldDragStartX;
            RebuildPainterMesh();
        }

        internal void EndFieldDrag()
        {
            if (_draggedFieldIndex < 0)
                return;
            StoreActivePage();
            FieldPage active = _fields[_activeFieldIndex];
            _fields.Sort((a, b) => a.PositionX.CompareTo(b.PositionX));
            _activeFieldIndex = _fields.IndexOf(active);
            _draggedFieldIndex = -1;
            LoadActivePage();
            RebuildPainterMesh();
            ExpireSolution(true);
        }

        internal void Hover(Point3d point)
        {
            if (_tool == WasperPaintTool.None || _tool == WasperPaintTool.Smooth ||
                !TryPick(point, out int index, out _, out int field, out _))
                _hoverIndex = -1;
            else
            {
                SwitchActiveField(field, false);
                _hoverIndex = index;
            }
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void ClearHover()
        {
            _hoverIndex = -1;
            UpdateConduit();
        }

        private void UpdateConduit()
        {
            if (_conduit == null)
                _conduit = new WasperPaintConduit();
            _conduit.IsActiveDocument = () =>
                ReferenceEquals(OnPingDocument(), Instances.ActiveCanvas?.Document);
            _conduit.PreviewMesh = _painterMesh;
            _conduit.ShowField = _preview;
            _conduit.HasHit = _hoverIndex >= 0 && _painterMesh != null &&
                _tool != WasperPaintTool.Smooth;
            if (_conduit.HasHit)
            {
                _conduit.HitPoint = _painterMesh.Vertices.Point3dAt(_hoverIndex) +
                    LastFieldPlane.ZAxis * Math.Max(0.001, LastCellSize * 0.01);
                _conduit.HitNormal = LastFieldPlane.ZAxis;
            }
            _conduit.Radius = _radius;
            _conduit.Tool = _tool;
            _conduit.ToolColorOverride = _tool == WasperPaintTool.Pull
                ? DivergingBlueWhiteRed(1.0)
                : _tool == WasperPaintTool.Push
                    ? DivergingBlueWhiteRed(-1.0)
                    : _tool == WasperPaintTool.Zero
                        ? Color.White
                    : (Color?)null;
            _conduit.Enabled = _preview || _tool != WasperPaintTool.None;
            _paintForm?.RefreshCanvas();
        }

        internal Point3d TransformAtlasPoint(Point3d point) =>
            WasperPaintAtlasTransform.Transform(
                point, Plane.WorldXY, _painterMesh, _atlasFlip, _atlasQuarterTurns);

        internal Point3d InverseAtlasPoint(Point3d point) =>
            WasperPaintAtlasTransform.Inverse(
                point, Plane.WorldXY, _painterMesh, _atlasFlip, _atlasQuarterTurns);

        internal double AtlasCenterX
        {
            get
            {
                BoundingBox box = _painterMesh?.GetBoundingBox(Plane.WorldXY) ?? BoundingBox.Empty;
                return box.IsValid ? (box.Min.X + box.Max.X) * 0.5 : 0.0;
            }
        }

        internal void DisposePainter()
        {
            if (_paintForm != null && !_paintForm.IsClosed)
                _paintForm.Close();
            if (_conduit != null)
                _conduit.Enabled = false;
            foreach (WasperPaintTextureLayer layer in _textureLayers)
                layer.Dispose();
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            DisposePainter();
            base.RemovedFromDocument(document);
        }

        private sealed class Fi01PainterHost : IWasperPainterHost
        {
            private readonly wsp_Fi01_Paint_Distance_Field_2D _owner;
            internal Fi01PainterHost(wsp_Fi01_Paint_Distance_Field_2D owner) { _owner = owner; }
            public string PainterTitle => "WASPer 2D Field Painter";
            public string PullToolLabel => "Add";
            public string PushToolLabel => "Subtract";
            public Color PullToolColor => DivergingBlueWhiteRed(1.0);
            public Color PushToolColor => DivergingBlueWhiteRed(-1.0);
            public string PainterLegend =>
                "Subtract  BLUE    \u2190    Zero  WHITE    \u2192    Add  ORANGE";
            public bool SupportsZeroTool => true;
            public Color ZeroToolColor => Color.White;
            public WasperPaintTool ActiveTool => _owner._tool;
            public WasperSmoothRegionShape SmoothRegionShape => _owner._smoothRegionShape;
            public bool PreviewEnabled => _owner._preview;
            public bool LiveEnabled => _owner._live;
            public bool UpdateEnabled => !_owner._live;
            public bool HasPendingUpdate => !WasperPaintUtilities.ValuesEqual(
                _owner._paintValues, _owner._appliedPaintValues);
            public Mesh PainterMesh => _owner._painterMesh;
            public Plane PainterPlane => Plane.WorldXY;
            public IList<WasperPaintMarker> PainterMarkers => _owner._markers;
            public bool ShowAtlasDimensions => true;
            public IList<WasperPaintAtlasBounds> AtlasDimensionBounds =>
                _owner._fields.Select(page => new WasperPaintAtlasBounds
                {
                    MinX = page.PositionX - page.FrameSize * 0.5,
                    MinY = -page.FrameSize * 0.5,
                    MaxX = page.PositionX + page.FrameSize * 0.5,
                    MaxY = page.FrameSize * 0.5
                }).ToList();
            public double PainterRadius => _owner._radius;
            public double PainterBrushStrength => _owner._strength;
            public double PainterSmoothStrength => _owner._smoothStrength;
            public bool PainterRadiusEditable => true;
            public bool PainterBrushStrengthEditable => true;
            public bool PainterSmoothStrengthEditable => true;
            public int PainterVisualRevision => _owner._visualRevision;
            public bool CanUndoPaint => _owner._undo.Count > 0;
            public bool CanRedoPaint => _owner._redo.Count > 0;
            public bool SupportsTextures => false;
            public bool SupportsTextTextures => false;
            public bool SupportsFieldCollection => true;
            public bool SupportsAtlasTransforms => false;
            public int FieldCount => _owner._fields.Count;
            public int ActiveFieldIndex => _owner._activeFieldIndex;
            public double FieldOffset => _owner.LastIsoOffset;
            public double FieldResolution => _owner.LastCellSize;
            public double FieldFrameSize => _owner.LastFrameSize;
            public bool FieldArrangeMode => _owner._fieldArrangeMode;
            public int TextureLayerCount => 5;
            public int ActiveTextureLayer => 0;
            public IList<WasperPaintTextureLayer> TextureLayers => _owner._textureLayers;
            public int TextTextureLayerCount => 0;
            public int ActiveTextTextureLayer => 0;
            public IList<WasperPaintTextureLayer> TextTextureLayers =>
                Array.Empty<WasperPaintTextureLayer>();
            public bool HasTextureSource => false;
            public Bitmap TextureBitmap => null;
            public bool TextureVisible => false;
            public bool TextureEditMode => false;
            public bool TextureDistortMode => false;
            public bool TextureRotateMode => false;
            public bool TextureHandlesVisible => false;
            public bool SupportsTextureEdgeHandles => false;
            public bool TextureIsDistorted => false;
            public int TextureRevision => 0;
            public bool AtlasFlipMap => _owner._atlasFlip;
            public int AtlasQuarterTurns => _owner._atlasQuarterTurns;
            public double AtlasMirrorCenterX => _owner.AtlasCenterX;
            public IList<Point2d> TextureCorners => _owner._textureLayers[0].Placement.Corners;
            public void TogglePreview() { _owner._preview = !_owner._preview; _owner.UpdateConduit(); }
            public void ToggleLive() => _owner.ToggleLive();
            public void UpdateAlgorithm() => _owner.UpdateOutput();
            public void UndoPaint() => _owner.UndoPaint();
            public void RedoPaint() => _owner.RedoPaint();
            public void ClearPaint() => _owner.ClearPaint();
            public void PreviewPainterSettings(double radius, double brush, double smooth)
            { _owner._radius = Math.Max(0.001, radius); _owner._strength = Math.Max(0, Math.Min(1, brush)); _owner._smoothStrength = Math.Max(0, Math.Min(1, smooth)); _owner.UpdateConduit(); }
            public void CommitPainterSettings(double radius, double brush, double smooth) =>
                PreviewPainterSettings(radius, brush, smooth);
            public void SetPainterTool(WasperPaintTool tool) => _owner.SetTool(tool);
            public void SetSmoothRegionShape(WasperSmoothRegionShape shape)
            { _owner._smoothRegionShape = shape; _owner._tool = WasperPaintTool.Smooth; _owner.UpdateConduit(); }
            public void ApplySmoothRegion(IList<Point3d> boundary) => _owner.ApplySmoothRegion(boundary);
            public bool PainterBeginStroke(Point3d point) => _owner.BeginStroke(point);
            public void PainterContinueStroke(Point3d point) => _owner.ContinueStroke(point);
            public void PainterEndStroke() => _owner.EndStroke();
            public void PainterHover(Point3d point) => _owner.Hover(point);
            public void ClearPainterHover() => _owner.ClearHover();
            public void AddNewField() => _owner.AddNewField();
            public void DuplicateActiveField() => _owner.DuplicateActiveField();
            public void RemoveActiveField() => _owner.RemoveActiveField();
            public void SelectPreviousField() => _owner.SelectPreviousField();
            public void SelectNextField() => _owner.SelectNextField();
            public void MoveActiveFieldUp() => _owner.MoveActiveField(-1);
            public void MoveActiveFieldDown() => _owner.MoveActiveField(1);
            public void PreviewFieldSettings(double offset, double resolution, double frameSize) =>
                _owner.UpdateFieldSettings(offset, resolution, frameSize, false);
            public void CommitFieldSettings(double offset, double resolution, double frameSize) =>
                _owner.UpdateFieldSettings(offset, resolution, frameSize, true);
            public void ToggleFieldArrangeMode() => _owner.ToggleFieldArrangeMode();
            public bool SelectFieldAt(Point3d atlasPoint) =>
                _owner.SelectFieldAt(atlasPoint);
            public bool BeginFieldDrag(Point3d atlasPoint) => _owner.BeginFieldDrag(atlasPoint);
            public void MoveFieldDrag(Point3d atlasPoint) => _owner.MoveFieldDrag(atlasPoint);
            public void EndFieldDrag() => _owner.EndFieldDrag();
            public void SavePainterSession() => _owner.SaveSession();
            public void LoadPainterSession() => _owner.LoadSession();
            public void SavePainterBitmap(Bitmap bitmap)
            {
                using var dialog = new Eto.Forms.SaveFileDialog { Title = "Save WASPer Field Paint Bitmap" };
                dialog.Filters.Add(new Eto.Forms.FileFilter("PNG image (*.png)", ".png"));
                if (dialog.ShowDialog(_owner._paintForm) == Eto.Forms.DialogResult.Ok)
                    WasperPaintPersistence.SaveBitmap(dialog.FileName, bitmap);
            }
            public void ToggleTextureVisibility() { }
            public void ToggleTextureLayerVisibility(int layerIndex) { }
            public void ToggleTextureEdit() { }
            public void ToggleTextureDistort() { }
            public void ToggleTextureRotate() { }
            public void ToggleAtlasFlipMap() { }
            public void RotateAtlasClockwise() { }
            public void FitTextureToAtlas() { }
            public void ApplyTextureToPaint() { }
            public void ApplyTextureCompositeToPaint() { }
            public void RemoveTextureOverlay() { }
            public void SelectTextureLayer(int layerIndex) { }
            public void SelectTextTextureLayer(int layerIndex) { }
            public void ToggleTextTextureLayerVisibility(int layerIndex) { }
            public void PreviewTextTexture(string text, string fontName, double fontSize) { }
            public void CommitTextTexture(string text, string fontName, double fontSize) { }
            public void DuplicateTextTextureLayer() { }
            public void RemoveTextTextureLayer() { }
            public void MoveTextTextureLayer(int direction) { }
            public void BeginTextureTransform(int corner) { }
            public void BeginTextureMove(Point3d atlasPoint) { }
            public void MoveTextureCorner(int corner, Point3d atlasPoint, bool ortho) { }
            public void MoveTexture(Point3d atlasPoint) { }
            public void EndTextureTransform() { }
            public Point3d MirrorAtlasPoint(Point3d point) => _owner.TransformAtlasPoint(point);
            public Point3d TransformAtlasPoint(Point3d point) => _owner.TransformAtlasPoint(point);
            public Point3d InverseTransformAtlasPoint(Point3d point) => _owner.InverseAtlasPoint(point);
        }
    }

    internal sealed class Fi01PaintAttributes : GH_ComponentAttributes
    {
        private RectangleF _buttonBounds;
        private bool _pressed;
        private wsp_Fi01_Paint_Distance_Field_2D Component =>
            Owner as wsp_Fi01_Paint_Distance_Field_2D;

        internal Fi01PaintAttributes(wsp_Fi01_Paint_Distance_Field_2D owner) : base(owner) { }

        protected override void Layout()
        {
            base.Layout();
            Rectangle bounds = GH_Convert.ToRectangle(Bounds);
            _buttonBounds = new RectangleF(bounds.X + 3, bounds.Bottom, bounds.Width - 6, 20);
            bounds.Height += 21;
            Bounds = bounds;
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects)
                return;
            using GH_Capsule capsule = GH_Capsule.CreateTextCapsule(
                _buttonBounds, _buttonBounds, GH_Palette.Black, "Open Painter",
                GH_FontServer.StandardAdjusted, 3, _pressed ? 0 : 8);
            capsule.Render(graphics, false, Owner.Locked, false);
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (!Owner.Locked && e.Button == MouseButtons.Left &&
                _buttonBounds.Contains(e.CanvasLocation))
            {
                _pressed = true;
                sender.Invalidate();
                return GH_ObjectResponse.Capture;
            }
            return base.RespondToMouseDown(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (!_pressed)
                return base.RespondToMouseUp(sender, e);
            _pressed = false;
            sender.Invalidate();
            if (_buttonBounds.Contains(e.CanvasLocation))
                Component.TogglePainterForm();
            return GH_ObjectResponse.Release;
        }
    }
}
