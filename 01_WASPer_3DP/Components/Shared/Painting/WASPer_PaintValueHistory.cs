using System;
using System.Collections.Generic;

namespace WASPer_3DP.Painting
{
    internal sealed class WasperPaintValueHistory
    {
        private readonly Stack<double[]> _undo = new Stack<double[]>();
        private readonly Stack<double[]> _redo = new Stack<double[]>();

        internal double[] Values { get; set; } = Array.Empty<double>();
        internal double[] AppliedValues { get; set; } = Array.Empty<double>();
        internal bool CanUndo => _undo.Count > 0;
        internal bool CanRedo => _redo.Count > 0;

        internal void Reset(int count)
        {
            int safeCount = Math.Max(0, count);
            Values = new double[safeCount];
            AppliedValues = new double[safeCount];
            ClearHistory();
        }

        internal void Restore(double[] values, double[] appliedValues)
        {
            Values = values == null ? Array.Empty<double>() : (double[])values.Clone();
            AppliedValues = appliedValues != null && appliedValues.Length == Values.Length
                ? (double[])appliedValues.Clone()
                : (double[])Values.Clone();
            ClearHistory();
        }

        internal void PushUndo(double[] state)
        {
            if (state == null)
                return;
            _undo.Push(state);
            _redo.Clear();
        }

        internal bool TryUndo()
        {
            if (_undo.Count == 0)
                return false;
            _redo.Push((double[])Values.Clone());
            Values = _undo.Pop();
            return true;
        }

        internal bool TryRedo()
        {
            if (_redo.Count == 0)
                return false;
            _undo.Push((double[])Values.Clone());
            Values = _redo.Pop();
            return true;
        }

        internal void ApplyWorkingValues()
        {
            AppliedValues = Values == null
                ? Array.Empty<double>()
                : (double[])Values.Clone();
        }

        internal void ClearHistory()
        {
            _undo.Clear();
            _redo.Clear();
        }
    }
}
