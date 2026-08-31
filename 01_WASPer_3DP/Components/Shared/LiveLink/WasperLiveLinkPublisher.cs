using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using Rhino.Geometry;

using WASPer.LiveLink;

namespace WASPer_3DP.Components.Shared.LiveLink
{
    /// <summary>One tree branch of loosely-typed input.</summary>
    internal sealed class WasperLiveBranchInput
    {
        public WasperLiveBranchInput(int[] path)
        {
            Path = path ?? Array.Empty<int>();
            Items = new List<object>();
        }

        public int[] Path { get; }
        public List<object> Items { get; }
    }

    internal sealed class WasperLivePublishInput
    {
        /// <summary>
        /// The primary input. Streamed natively rather than flattened into generic
        /// geometry, so roles, stroke continuity and bead widths reach the viewer.
        /// </summary>
        public WasperPrintPath Path { get; set; }

        public List<WasperLiveBranchInput> Geometry { get; } = new List<WasperLiveBranchInput>();

        /// <summary>
        /// Optional colour per geometry item, in the same tree shape as
        /// <see cref="Geometry"/>. Items are System.Drawing.Color.
        /// </summary>
        public List<WasperLiveBranchInput> GeometryColors { get; } = new List<WasperLiveBranchInput>();
        public List<WasperLiveBranchInput> Points { get; } = new List<WasperLiveBranchInput>();
        public List<WasperLiveBranchInput> Numbers { get; } = new List<WasperLiveBranchInput>();
        public List<WasperLiveBranchInput> Text { get; } = new List<WasperLiveBranchInput>();

        public double Tolerance { get; set; } = 0.01;
        public double UnitScaleToMetres { get; set; } = 1.0;

        /// <summary>True when a ref_plane pinned the frame origin explicitly.</summary>
        public bool HasExplicitOrigin { get; set; }

        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public double OriginZ { get; set; }

        /// <summary>
        /// Applied to coordinates and to bead dimensions. 1.0 sends model units
        /// unchanged; 0.001 converts millimetres to metres, in which case
        /// UnitScaleToMetres must be 1.0.
        /// </summary>
        public double GeometryScale { get; set; } = 1.0;
    }

    internal sealed class WasperLivePublishResult
    {
        public bool Published { get; set; }
        public long Revision { get; set; }
        public int PayloadBytes { get; set; }
        public double ConvertMilliseconds { get; set; }
        public double PublishMilliseconds { get; set; }
        public bool ReusedDisplayCache { get; set; }
        public bool ExplicitOrigin { get; set; }
        public int PathBranchCount { get; set; }

        /// <summary>
        /// Bounding box diagonal of the display inputs in source units, before any
        /// scaling. Zero when the display cache was reused and nothing was measured.
        /// Used to sanity-check the declared units against the modelled magnitudes.
        /// </summary>
        public double ModelDiagonal { get; set; }
        public int MeshedObjectCount { get; set; }
        public int MeshCount { get; set; }
        public int MeshesWithVertexColors { get; set; }
        public string Blocks { get; set; }
        public IReadOnlyList<string> Skipped { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Owns the transport handle, the geometry cache, and change detection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Publishing is synchronous, on the Grasshopper thread. That is only
    /// defensible because the frame budget is small: at roughly 2 MB, serialization
    /// and the copy into shared memory together cost a few milliseconds, which buys
    /// the removal of a background thread, a hand-off queue, a drop policy, and the
    /// class of bugs where the solver mutates geometry the writer is still reading.
    /// </para>
    /// <para>
    /// The expensive step is meshing, not serialization, so display geometry and
    /// points are cached together as one unit keyed by reference identity. A camera
    /// update arrives on the Numbers channel and therefore never touches it.
    /// </para>
    /// </remarks>
    internal sealed class WasperLiveLinkPublisher : IDisposable
    {
        private readonly WasperLiveLinkGeometry _geometry = new WasperLiveLinkGeometry();

        private WasperLiveLinkWriter _writer;
        private int _requestedSlotBytes = -1;

        private long _displaySignature;
        private long _numbersSignature;
        private long _textSignature;
        private bool _hasPublished;

        private List<WasperLiveChannel> _cachedDisplayChannels;
        private int _cachedPathBranchCount;
        private double _cachedDiagonal;
        private double _cachedOriginX;
        private double _cachedOriginY;
        private double _cachedOriginZ;

        public bool IsOpen => _writer != null;

        public string Channel => _writer?.Channel;
        public string MappingName => _writer?.MappingName;
        public string MutexName => _writer?.MutexName;
        public int SlotBytes => _writer?.SlotBytes ?? 0;
        public long Revision => _writer?.Revision ?? 0;
        public bool RecoveredAbandonedChannel => _writer?.RecoveredAbandonedChannel ?? false;

        /// <summary>
        /// Opens, or reopens when the channel settings changed. Returns false with a
        /// message rather than throwing: a channel collision is a user-facing
        /// condition, not an exceptional one.
        /// </summary>
        public bool EnsureOpen(string channel, int slotBytes, bool global, out string error)
        {
            error = null;

            int requested = WasperLiveLinkProtocol.ClampSlotBytes(slotBytes);

            // Compare against what was requested, not against the writer's
            // effective slot size. When an existing mapping is adopted from a
            // crashed publisher the two legitimately differ, and comparing the
            // effective size would tear the channel down and rebuild it on every
            // solution.
            if (_writer != null &&
                string.Equals(_writer.Channel, channel, StringComparison.Ordinal) &&
                _writer.IsGlobalNamespace == global &&
                _requestedSlotBytes == requested)
            {
                return true;
            }

            Close();

            try
            {
                _writer = new WasperLiveLinkWriter(channel, requested, global);
                _requestedSlotBytes = requested;
                return true;
            }
            catch (WasperLiveLinkException ex)
            {
                error = ex.Message;
                return false;
            }
            catch (PlatformNotSupportedException ex)
            {
                error = "Named shared memory is only available on Windows. " + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "Could not open channel '" + channel + "': " + ex.Message;
                return false;
            }
        }

        public void Close()
        {
            _writer?.Dispose();
            _writer = null;
            _requestedSlotBytes = -1;

            _hasPublished = false;
            _cachedDisplayChannels = null;
            _geometry.Clear();
        }

        public void Heartbeat() => _writer?.Heartbeat();

        public WasperLivePublishResult Publish(WasperLivePublishInput input)
        {
            var result = new WasperLivePublishResult();

            if (_writer == null)
            {
                result.Error = "Channel is not open.";
                return result;
            }

            long displaySignature = Signature(
                PathSignature(input.Path), input.Geometry, input.GeometryColors,
                input.Points, input.Tolerance,
                input.HasExplicitOrigin ? input.OriginX : double.NaN,
                input.HasExplicitOrigin ? input.OriginY : double.NaN,
                input.HasExplicitOrigin ? input.OriginZ : double.NaN,
                input.GeometryScale);
            long numbersSignature = Signature(input.Numbers);
            long textSignature = Signature(input.Text);

            if (_hasPublished &&
                displaySignature == _displaySignature &&
                numbersSignature == _numbersSignature &&
                textSignature == _textSignature)
            {
                // Report what the retained frame actually holds. Leaving these at
                // zero made an unchanged frame read as an empty one, which sends
                // you looking for a fault upstream that is not there.
                _writer.Heartbeat();
                result.Revision = _writer.Revision;
                result.PayloadBytes = _writer.LastFrameBytes;
                result.PathBranchCount = _cachedPathBranchCount;
                result.ModelDiagonal = _cachedDiagonal;
                result.ReusedDisplayCache = true;
                result.Blocks = _writer.DescribeLastFrameBlocks();
                return result;
            }

            var convertClock = Stopwatch.StartNew();

            bool reuseDisplay =
                _hasPublished &&
                displaySignature == _displaySignature &&
                _cachedDisplayChannels != null;

            double originX, originY, originZ;
            double diagonal = 0.0;

            if (reuseDisplay)
            {
                originX = _cachedOriginX;
                originY = _cachedOriginY;
                originZ = _cachedOriginZ;
            }
            else
            {
                BoundingBox box = MeasureInputs(input);
                diagonal = box.IsValid ? box.Diagonal.Length : 0.0;

                if (input.HasExplicitOrigin)
                {
                    originX = input.OriginX;
                    originY = input.OriginY;
                    originZ = input.OriginZ;
                }
                else if (box.IsValid)
                {
                    WasperLiveFrameBuilder.SuggestOrigin(
                        box.Min.X, box.Min.Y, box.Min.Z,
                        box.Max.X, box.Max.Y, box.Max.Z,
                        out originX, out originY, out originZ);
                }
                else
                {
                    originX = originY = originZ = 0.0;
                }
            }

            var builder = new WasperLiveFrameBuilder(
                input.UnitScaleToMetres, originX, originY, originZ, input.GeometryScale);
            WasperLiveFrame frame = builder.Build();

            if (reuseDisplay)
            {
                for (int i = 0; i < _cachedDisplayChannels.Count; i++)
                    builder.AddPrebuiltChannel(_cachedDisplayChannels[i]);

                result.ReusedDisplayCache = true;
                result.PathBranchCount = _cachedPathBranchCount;
                result.ModelDiagonal = _cachedDiagonal;
            }
            else
            {
                _geometry.BeginFrame(input.Tolerance);

                // wsp_path first: it is the primary payload and its branch paths
                // define the structure the attribute block is zipped against.
                if (input.Path != null)
                {
                    result.PathBranchCount =
                        WasperLiveLinkPathAdapter.Append(builder, input.Path, input.Tolerance);
                }

                foreach (WasperLiveBranchInput branch in input.Geometry)
                    foreach (object item in branch.Items)
                        _geometry.Append(builder, branch.Path, item, input.Tolerance);

                // Colours are written in the same branch and order as the meshes, so
                // the receiver pairs them positionally without needing a key.
                foreach (WasperLiveBranchInput branch in input.GeometryColors)
                {
                    foreach (object item in branch.Items)
                    {
                        if (!(item is System.Drawing.Color c)) continue;

                        builder.AddMeshColor(
                            branch.Path, c.R / 255.0, c.G / 255.0, c.B / 255.0, c.A / 255.0);
                    }
                }

                foreach (WasperLiveBranchInput branch in input.Points)
                {
                    foreach (object item in branch.Items)
                    {
                        if (item is Point3d point)
                            builder.AddPoint(branch.Path, point.X, point.Y, point.Z);
                        else
                            _geometry.Append(builder, branch.Path, item, input.Tolerance);
                    }
                }

                _geometry.EndFrame();

                // Everything appended so far came from the display inputs, so the
                // current channel list is exactly the cacheable prefix.
                _cachedDisplayChannels = new List<WasperLiveChannel>(frame.Channels);
                _cachedPathBranchCount = result.PathBranchCount;
                _cachedDiagonal = diagonal;
                result.ModelDiagonal = diagonal;
                _cachedOriginX = originX;
                _cachedOriginY = originY;
                _cachedOriginZ = originZ;

                result.MeshedObjectCount = _geometry.MeshedObjectCount;
                result.MeshCount = _geometry.MeshCount;
                result.MeshesWithVertexColors = _geometry.MeshesWithVertexColors;
                result.Skipped = _geometry.SkippedSummary;
            }

            foreach (WasperLiveBranchInput branch in input.Numbers)
                foreach (object item in branch.Items)
                    if (item is double value) builder.AddNumber(branch.Path, value);

            foreach (WasperLiveBranchInput branch in input.Text)
                foreach (object item in branch.Items)
                    builder.AddText(branch.Path, item as string ?? item?.ToString() ?? string.Empty);

            convertClock.Stop();

            var publishClock = Stopwatch.StartNew();
            try
            {
                result.Revision = _writer.Publish(frame);
            }
            catch (WasperLiveLinkException ex)
            {
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                publishClock.Stop();
            }

            _displaySignature = displaySignature;
            _numbersSignature = numbersSignature;
            _textSignature = textSignature;
            _hasPublished = true;

            result.Published = true;
            result.ExplicitOrigin = input.HasExplicitOrigin;
            result.PayloadBytes = _writer.LastFrameBytes;
            result.Blocks = _writer.DescribeLastFrameBlocks();
            result.ConvertMilliseconds = convertClock.Elapsed.TotalMilliseconds;
            result.PublishMilliseconds = publishClock.Elapsed.TotalMilliseconds;
            return result;
        }

        private static BoundingBox MeasureInputs(WasperLivePublishInput input)
        {
            var box = BoundingBox.Empty;

            AccumulatePath(ref box, input.Path);
            Accumulate(ref box, input.Geometry);
            Accumulate(ref box, input.Points);

            return box;
        }

        private static void AccumulatePath(ref BoundingBox box, WasperPrintPath path)
        {
            if (path == null || !path.HasPoints) return;

            for (int b = 0; b < path.Points.BranchCount; b++)
            {
                List<Point3d> branch = path.Points.Branches[b];
                if (branch == null) continue;

                for (int i = 0; i < branch.Count; i++)
                    box.Union(branch[i]);
            }
        }

        private static void Accumulate(ref BoundingBox box, List<WasperLiveBranchInput> branches)
        {
            foreach (WasperLiveBranchInput branch in branches)
            {
                foreach (object item in branch.Items)
                {
                    switch (item)
                    {
                        case GeometryBase geometry:
                            box.Union(geometry.GetBoundingBox(false));
                            break;
                        case Point3d point:
                            box.Union(point);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Cheap change detection. Reference identity is the fast path and holds
        /// whenever upstream did not recompute; value types are hashed by content
        /// because boxing gives them a fresh identity on every solution.
        /// </summary>
        /// <summary>
        /// ContentSignature is a deterministic producer signature, so it detects a
        /// genuinely new path far more reliably than reference identity — a
        /// recomputed but identical path keeps its signature and the cache holds.
        /// Reference identity is the fallback for paths built before signatures
        /// were populated.
        /// </summary>
        private static object PathSignature(WasperPrintPath path)
        {
            if (path == null) return "no-path";

            return string.IsNullOrEmpty(path.ContentSignature)
                ? (object)RuntimeHelpers.GetHashCode(path)
                : path.ContentSignature;
        }

        private static long Signature(params object[] sections)
        {
            long hash = 1469598103934665603L;

            foreach (object section in sections)
            {
                switch (section)
                {
                    case string signature:
                        hash = Mix(hash, signature.GetHashCode());
                        break;

                    case int identity:
                        hash = Mix(hash, identity);
                        break;

                    case double value:
                        hash = Mix(hash, value.GetHashCode());
                        break;

                    case List<WasperLiveBranchInput> branches:
                        foreach (WasperLiveBranchInput branch in branches)
                        {
                            for (int i = 0; i < branch.Path.Length; i++)
                                hash = Mix(hash, branch.Path[i]);

                            hash = Mix(hash, branch.Items.Count);

                            foreach (object item in branch.Items)
                                hash = Mix(hash, ItemHash(item));
                        }
                        break;
                }
            }

            return hash;
        }

        private static int ItemHash(object item)
        {
            switch (item)
            {
                case null:
                    return 0;

                // Reference types keep their identity across a solution when
                // upstream did not recompute, which is exactly the signal we want.
                case GeometryBase geometry:
                    return RuntimeHelpers.GetHashCode(geometry);

                case Point3d point:
                    return point.GetHashCode();

                case double value:
                    return value.GetHashCode();

                case string text:
                    return text.GetHashCode();

                default:
                    return item.GetHashCode();
            }
        }

        private static long Mix(long hash, int value)
        {
            hash ^= value;
            hash *= 1099511628211L;
            return hash;
        }

        public void Dispose() => Close();
    }
}
