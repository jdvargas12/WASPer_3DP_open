using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace WASPer_3DP
{
    public enum WasperChartHitKind
    {
        Point,
        Segment,
        Cell,
        Axis,
        Legend,

        /// <summary>A drawn point label, so a host can let the user drag it clear of others.</summary>
        Label
    }

    /// <summary>
    /// Screen-space interaction record emitted by a renderer. It maps pixels back to the
    /// originating individual, series, and numeric values without coupling the renderer to a UI.
    /// </summary>
    public sealed class WasperChartHitTarget
    {
        public WasperChartHitKind Kind { get; set; } = WasperChartHitKind.Point;
        public int IndividualId { get; set; } = -1;
        public int DataIndex { get; set; } = -1;
        public string SeriesKey { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public double XValue { get; set; } = double.NaN;
        public double YValue { get; set; } = double.NaN;
        public RectangleF Bounds { get; set; } = RectangleF.Empty;
        public PointF Anchor { get; set; }
        public PointF End { get; set; }
        public Dictionary<string, string> Metadata { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal double DistanceTo(PointF location)
        {
            if (Kind == WasperChartHitKind.Cell ||
                Kind == WasperChartHitKind.Axis ||
                Kind == WasperChartHitKind.Legend ||
                Kind == WasperChartHitKind.Label)
            {
                return Bounds.Contains(location)
                    ? 0.0
                    : DistanceToRectangle(location, Bounds);
            }
            if (Kind == WasperChartHitKind.Segment)
                return DistanceToSegment(location, Anchor, End);
            double dx = location.X - Anchor.X;
            double dy = location.Y - Anchor.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static double DistanceToRectangle(PointF point, RectangleF rectangle)
        {
            float dx = Math.Max(rectangle.Left - point.X, Math.Max(0f, point.X - rectangle.Right));
            float dy = Math.Max(rectangle.Top - point.Y, Math.Max(0f, point.Y - rectangle.Bottom));
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static double DistanceToSegment(PointF point, PointF start, PointF end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSquared = (dx * dx) + (dy * dy);
            if (lengthSquared <= 1e-12)
            {
                dx = point.X - start.X;
                dy = point.Y - start.Y;
                return Math.Sqrt((dx * dx) + (dy * dy));
            }
            double parameter = Math.Max(
                0.0,
                Math.Min(1.0, (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) /
                    lengthSquared));
            double nearestX = start.X + (parameter * dx);
            double nearestY = start.Y + (parameter * dy);
            dx = point.X - nearestX;
            dy = point.Y - nearestY;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }
    }

    public static class WasperChartHitTester
    {
        /// <summary>
        /// Returns the nearest eligible target within tolerance. Targets containing the pointer
        /// win over merely nearby targets; otherwise the shortest screen-space distance wins.
        /// </summary>
        public static WasperChartHitTarget FindNearest(
            IEnumerable<WasperChartHitTarget> targets,
            PointF location,
            float tolerancePixels = 8f,
            Func<WasperChartHitTarget, bool> filter = null)
        {
            double tolerance = Math.Max(0f, tolerancePixels);
            return (targets ?? Enumerable.Empty<WasperChartHitTarget>())
                .Where(target => target != null && (filter == null || filter(target)))
                .Select(target => new { Target = target, Distance = target.DistanceTo(location) })
                .Where(candidate => candidate.Distance <= tolerance)
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Target.Kind == WasperChartHitKind.Point ? 0 : 1)
                .Select(candidate => candidate.Target)
                .FirstOrDefault();
        }
    }

    public sealed class WasperChartSelectionChangedEventArgs : EventArgs
    {
        internal WasperChartSelectionChangedEventArgs(
            IReadOnlyList<int> selectedIds,
            int? primaryId)
        {
            SelectedIds = selectedIds;
            PrimaryId = primaryId;
        }

        public IReadOnlyList<int> SelectedIds { get; }
        public int? PrimaryId { get; }
    }

    /// <summary>
    /// Shared selection state for linked Dashboard charts and detail views.
    /// </summary>
    public sealed class WasperChartSelection
    {
        private readonly HashSet<int> _selectedIds = new HashSet<int>();

        public event EventHandler<WasperChartSelectionChangedEventArgs> SelectionChanged;

        public IReadOnlyList<int> SelectedIds => _selectedIds.OrderBy(id => id).ToList();
        public int? PrimaryId { get; private set; }

        public bool IsSelected(int individualId) => _selectedIds.Contains(individualId);

        public void SelectOnly(int individualId)
        {
            _selectedIds.Clear();
            _selectedIds.Add(individualId);
            PrimaryId = individualId;
            RaiseChanged();
        }

        public void SetSelection(IEnumerable<int> individualIds, int? primaryId = null)
        {
            _selectedIds.Clear();
            foreach (int id in individualIds ?? Enumerable.Empty<int>())
                _selectedIds.Add(id);
            PrimaryId = primaryId.HasValue && _selectedIds.Contains(primaryId.Value)
                ? primaryId
                : _selectedIds.Count > 0
                    ? _selectedIds.OrderBy(id => id).First()
                    : (int?)null;
            RaiseChanged();
        }

        public void Toggle(int individualId)
        {
            if (!_selectedIds.Add(individualId))
                _selectedIds.Remove(individualId);
            PrimaryId = _selectedIds.Contains(individualId)
                ? individualId
                : _selectedIds.Count > 0
                    ? _selectedIds.OrderBy(id => id).First()
                    : (int?)null;
            RaiseChanged();
        }

        public void Clear()
        {
            if (_selectedIds.Count == 0 && !PrimaryId.HasValue)
                return;
            _selectedIds.Clear();
            PrimaryId = null;
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            SelectionChanged?.Invoke(
                this,
                new WasperChartSelectionChangedEventArgs(SelectedIds, PrimaryId));
        }
    }
}
