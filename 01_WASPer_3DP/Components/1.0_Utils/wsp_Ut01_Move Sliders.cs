#region Component Description
/*
    Component Name:
        wsp_Ut01_Move Sliders

    Nickname:
        Move sliders

    Version:
        v1.0.5

    Category / Subcategory:
        WASPer_3DP / 1.0_Utils

    Description:
        Links Grasshopper number sliders by nickname prefix and iterates through
        every discrete TickCount state combination. Works with integer and floating-
        point sliders — TickCount is read directly from each slider (e.g. a 0.00→1.00
        slider with 2 decimal places yields 101 ticks). Iteration stops automatically
        after the last combination; toggle link off / on to reset.

    Inputs:
        pref_sliders : prefix used to find GH_NumberSlider nicknames
        link         : true links sliders; false releases / resets state
        run          : true starts / continues iteration; false stops it

    Output:
        outMsg       : status and iteration diagnostics

    Notes:
        Total combinations are capped by MAX_COMBOS to avoid runaway definitions.
        After all combinations are exhausted the component stops and will not
        restart until 'link' is toggled off then on again.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
#endregion

namespace WASPer_3DP.Components._1_0_Utils
{
    public sealed class wsp_Ut01_Move_Sliders : GH_Component
    {
        private const string NAME      = "wsp_Ut01_Move Sliders";
        private const string NICK      = "Move sliders";
        private const string CAT       = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT    = "1.0_Utils";
        private readonly string _versionTag;
        private const long   MAX_COMBOS = 100_000;

        // ── iteration state (shared across solves) ───────────────────────────
        private static bool   _hasLinked;
        private static bool   _isDone;
        private static bool   _isIterating;
        private static int    _currentIndex = -1;
        private static long   _totalCombos  = -1;
        private static bool   _combosTooHigh;
        private static string _currentValString;

        private static readonly List<GH_NumberSlider> _linkedSliders  = new List<GH_NumberSlider>();
        private static          List<List<int>>       _allTickCombos  = new List<List<int>>();

        public wsp_Ut01_Move_Sliders()
            : base(
                NAME, NICK,
                "Automates parametric studies by iterating through all discrete slider\n" +
                "states based on TickCount. Works with integer and floating-point sliders.\n" +
                "Stops automatically after the last combination.",
                CAT, SUBCAT)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("0F2B4476-1C6C-41A2-9A6A-39C14C2BA901");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut01_Move Sliders.png"))
                        return s != null ? new Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        // ── inputs ────────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter(
                "pref_sliders", "pref",
                "Prefix used to detect GH_NumberSlider nicknames.\n" +
                "Any slider whose nickname starts with this text is linked.",
                GH_ParamAccess.item, string.Empty);

            p.AddBooleanParameter(
                "link", "link",
                "True scans and links matching sliders.\n" +
                "False releases linked sliders and clears iteration state.",
                GH_ParamAccess.item, false);

            p.AddBooleanParameter(
                "run", "run",
                "True starts iteration through all linked slider tick combinations.\n" +
                "False stops iteration. After completion, toggle 'link' off/on to reset.",
                GH_ParamAccess.item, false);

            p[0].Optional = true;
            p[1].Optional = true;
            p[2].Optional = true;
        }

        // ── outputs ───────────────────────────────────────────────────────────
        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter(
                "outMsg", "msg",
                "Human-readable status and debug information.",
                GH_ParamAccess.item);
        }

        // ── solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string prefix = string.Empty;
            bool   link   = false;
            bool   run    = false;

            DA.GetData(0, ref prefix);
            DA.GetData(1, ref link);
            DA.GetData(2, ref run);
            prefix = prefix ?? string.Empty;

            string outMsg;

            // ── link / unlink ────────────────────────────────────────────────
            if (link && !_hasLinked)
            {
                LinkSliders(prefix, out outMsg);
                DA.SetData(0, outMsg);
                UpdateMessage();
                return;
            }

            if (!link && _hasLinked)
            {
                ResetState();
                outMsg = "Released all linked sliders and cleared iteration state.";
                DA.SetData(0, outMsg);
                UpdateMessage();
                return;
            }

            // ── run / stop ───────────────────────────────────────────────────
            if (run && !_isIterating)
            {
                if (_isDone)
                {
                    outMsg = $"Done — all {_allTickCombos.Count} combinations completed.\n" +
                             "Toggle 'link' off then on to reset and run again.";
                    DA.SetData(0, outMsg);
                    UpdateMessage();
                    return;
                }

                StartIteration(out outMsg);
                DA.SetData(0, outMsg);
                UpdateMessage();
                return;
            }

            if (!run && _isIterating)
            {
                _isIterating = false;
                outMsg = "Stopped iteration prematurely.";
                DA.SetData(0, outMsg);
                UpdateMessage();
                return;
            }

            DA.SetData(0, CurrentStatus());
            UpdateMessage();
        }

        // ── link ──────────────────────────────────────────────────────────────
        private void LinkSliders(string prefix, out string outMsg)
        {
            ResetState();

            var doc = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            if (doc == null) { outMsg = "Error: No Grasshopper document found."; return; }

            foreach (var slider in doc.Objects.OfType<GH_NumberSlider>())
            {
                string nick = slider.NickName ?? string.Empty;
                if (nick.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    _linkedSliders.Add(slider);
            }

            _linkedSliders.Sort(CompareSlidersByCanvasPosition);
            _hasLinked = true;

            if (_linkedSliders.Count == 0)
            {
                outMsg = $"No sliders found with prefix '{prefix}'.";
                return;
            }

            _totalCombos = ComputeTotalCombinations(_linkedSliders);
            if (_totalCombos > MAX_COMBOS)
            {
                _combosTooHigh = true;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Total combinations = {_totalCombos} exceeds cap of {MAX_COMBOS}. " +
                    "Iteration will be blocked.");
            }

            var sb = new StringBuilder();
            sb.AppendLine("Linked sliders:");
            foreach (var s in _linkedSliders)
            {
                int ticks = Math.Max(1, s.Slider?.TickCount ?? 1);
                sb.AppendLine($"  {s.NickName}  " +
                              $"[{s.Slider.Minimum:0.####} → {s.Slider.Maximum:0.####}]  " +
                              $"× {ticks} steps");
            }
            sb.AppendLine();
            sb.AppendLine($"Total combinations: {_totalCombos}");
            if (_combosTooHigh)
                sb.AppendLine($"WARNING: Exceeds cap ({MAX_COMBOS}) — iteration blocked.");

            outMsg = sb.ToString().TrimEnd();
        }

        // ── start ─────────────────────────────────────────────────────────────
        private void StartIteration(out string outMsg)
        {
            if (_linkedSliders.Count == 0)
            {
                outMsg = "No sliders linked. Toggle 'link' first.";
                return;
            }

            if (_combosTooHigh || _totalCombos > MAX_COMBOS)
            {
                outMsg = $"Too many combinations: {_totalCombos} > {MAX_COMBOS}. Iteration blocked.";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, outMsg);
                return;
            }

            _allTickCombos    = BuildAllTickCombinations(_linkedSliders);
            _currentIndex     = -1;
            _currentValString = null;
            _isDone           = false;
            _isIterating      = true;

            outMsg = $"Starting iteration over {_allTickCombos.Count} combinations.";
            ScheduleNextIteration();
        }

        // ── scheduler ─────────────────────────────────────────────────────────
        private void ScheduleNextIteration()
        {
            var doc = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            if (doc == null) return;

            doc.ScheduleSolution(1, _ =>
            {
                if (!_isIterating) return;

                _currentIndex++;

                // ── all combinations exhausted ────────────────────────────
                if (_currentIndex >= _allTickCombos.Count)
                {
                    _isIterating = false;
                    _isDone      = true;
                    ExpireSolution(false);
                    return;
                }

                // ── apply this tick combination ───────────────────────────
                var tickCombo  = _allTickCombos[_currentIndex];
                var displayVals = new List<string>(_linkedSliders.Count);

                for (int i = 0; i < _linkedSliders.Count; i++)
                {
                    var slider = _linkedSliders[i];
                    if (slider?.Slider == null) continue;

                    int tickCount    = Math.Max(1, slider.Slider.TickCount);
                    int maxTickIndex = tickCount - 1;
                    int tickIndex    = Math.Max(0, Math.Min(maxTickIndex, tickCombo[i]));

                    decimal value = SliderValueAtTick(slider, tickIndex);
                    slider.SetSliderValue(value);
                    slider.ExpireSolution(false);
                    displayVals.Add(slider.Slider.Value.ToString("0.####"));
                }

                _currentValString = string.Join(" ; ", displayVals);

                // ── schedule next or mark done ────────────────────────────
                if (_currentIndex < _allTickCombos.Count - 1)
                    ScheduleNextIteration();
                else
                {
                    _isIterating = false;
                    _isDone      = true;
                }

                ExpireSolution(false);
            });
        }

        // ── status ────────────────────────────────────────────────────────────
        private string CurrentStatus()
        {
            if (_isIterating)
                return $"Iterating… {_currentIndex + 1} / {_allTickCombos.Count}" +
                       (!string.IsNullOrEmpty(_currentValString)
                            ? $"\nValues: {_currentValString}" : "");

            if (_isDone)
                return $"Done — all {_allTickCombos.Count} combinations completed.\n" +
                       (!string.IsNullOrEmpty(_currentValString)
                            ? $"Last values: {_currentValString}\n" : "") +
                       "Toggle 'link' off then on to reset.";

            if (_linkedSliders.Count == 0)
                return "Idle. No sliders linked.";

            return $"Idle. {_linkedSliders.Count} sliders linked" +
                   (_totalCombos > 0 ? $" ({_totalCombos} combinations)." : ".") +
                   (!string.IsNullOrEmpty(_currentValString)
                        ? $"\nLast values: {_currentValString}" : "") +
                   (_combosTooHigh ? $"\nWARNING: combinations > cap ({MAX_COMBOS})." : "");
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private static List<List<int>> BuildAllTickCombinations(List<GH_NumberSlider> sliders)
        {
            var tickLists = sliders.Select(GetSliderTicks).ToList();
            return CartesianProductInt(tickLists);
        }

        private static List<int> GetSliderTicks(GH_NumberSlider slider)
        {
            int count = Math.Max(1, slider?.Slider?.TickCount ?? 1);
            var ticks = new List<int>(count);
            for (int i = 0; i < count; i++)
                ticks.Add(i);
            return ticks;
        }

        /// <summary>
        /// Returns the actual slider value (decimal) at a given tick index.
        /// Uses the slider's own TickValue method via reflection, falling back
        /// to linear interpolation. This correctly handles integer, float, odd,
        /// and even slider types.
        /// </summary>
        private static decimal SliderValueAtTick(GH_NumberSlider slider, int tickIndex)
        {
            int tickCount    = Math.Max(1, slider?.Slider?.TickCount ?? 1);
            int maxTickIndex = Math.Max(0, tickCount - 1);
            tickIndex = Math.Max(0, Math.Min(maxTickIndex, tickIndex));

            // Try native TickValue method first (handles all slider types correctly)
            var method = slider.Slider.GetType().GetMethod("TickValue", new[] { typeof(int) });
            if (method != null)
            {
                object raw = method.Invoke(slider.Slider, new object[] { tickIndex });
                if (raw is decimal dec) return dec;
                if (raw is double  dbl) return (decimal)dbl;
                if (raw is float   flt) return (decimal)flt;
                if (raw is int     igs) return igs;
            }

            // Linear interpolation fallback
            decimal min = slider.Slider.Minimum;
            decimal max = slider.Slider.Maximum;
            return maxTickIndex == 0
                ? min
                : min + (max - min) * tickIndex / maxTickIndex;
        }

        private static List<List<int>> CartesianProductInt(List<List<int>> lists)
        {
            if (lists.Count == 0) return new List<List<int>>();

            var result = new List<List<int>> { new List<int>() };
            foreach (var list in lists)
            {
                var next = new List<List<int>>();
                foreach (var existing in result)
                foreach (int value in list)
                {
                    var combo = new List<int>(existing) { value };
                    next.Add(combo);
                }
                result = next;
            }
            return result;
        }

        private static long ComputeTotalCombinations(List<GH_NumberSlider> sliders)
        {
            double total = 1.0;
            foreach (var slider in sliders)
            {
                int ticks = Math.Max(1, slider?.Slider?.TickCount ?? 1);
                total *= ticks;
                if (total > long.MaxValue) return long.MaxValue;
            }
            return (long)Math.Round(total);
        }

        private static int CompareSlidersByCanvasPosition(GH_NumberSlider a, GH_NumberSlider b)
        {
            float ay = a?.Attributes?.Bounds.Y ?? float.MaxValue;
            float by = b?.Attributes?.Bounds.Y ?? float.MaxValue;
            int cmp = ay.CompareTo(by);
            if (cmp != 0) return cmp;

            float ax = a?.Attributes?.Bounds.X ?? float.MaxValue;
            float bx = b?.Attributes?.Bounds.X ?? float.MaxValue;
            return ax.CompareTo(bx);
        }

        private static void ResetState()
        {
            _linkedSliders.Clear();
            _allTickCombos.Clear();
            _hasLinked        = false;
            _isIterating      = false;
            _isDone           = false;
            _currentIndex     = -1;
            _currentValString = null;
            _totalCombos      = -1;
            _combosTooHigh    = false;
        }

        private void UpdateMessage()
        {
            string msg = _versionTag;
            if (_isDone)
                msg += " | done";
            else if (_totalCombos > 0)
                msg += $" | {_totalCombos} combos";
            Message = msg;
        }
    }
}
