#region Component Description
/*
    Component Name:
      wsp_Gc06_Printing Path from Gcode

    Description:
      Parses a 3D-printing G-code file or text and reconstructs the
      toolpath as polylines, split into printing paths (with extrusion)
      and travel moves (without extrusion).

      The component:
        - Accepts either a file path to a .gcode file or raw G-code text
          (e.g. from a generator component). If both are provided,
          gcode_text takes precedence.
        - Detects absolute/relative position mode (G90/G91).
        - Detects absolute/relative extrusion mode (M82/M83).
        - Tracks the extruder position E and classifies each motion
          segment as printing (extruding) or travel (non-extruding),
          using a small internal E tolerance.
        - Clusters Z positions into layers using an internal Z tolerance.
        - Emits printing and travel polylines in a {layer; curve} tree
          where each branch corresponds to one continuous path.

      Notes:
        - Only G0/G1 linear moves are used to build paths.
        - G92 E... is supported (extruder reset).
        - The component does NOT reconstruct bead width/height or flux;
          it only outputs geometric paths, which can be visualised or
          processed by other components (e.g. bead mesh visualisers).

    Inputs:
      0) gcode_file (Text, item)
           Optional full file path to a .gcode file. Used only if
           gcode_text is empty or not supplied.

      1) gcode_text (Text, list)
           Optional raw G-code text, e.g. from another Grasshopper
           component. Can be a single long string or a list of lines;
           if present and non-empty, this input takes precedence over
           gcode_file.

      2) include_travels (Boolean, item, default = true)
           If true, the component also outputs travel paths (moves
           without extrusion). If false, gcode_travels is an empty tree.

    Outputs:
      0) gcode_p_path (Tree<Curve>)
           Printing toolpaths (with extrusion). Each branch in the tree
           represents one continuous extruding path as a PolylineCurve,
           organised as {layer; curve} where:
             - layer: integer layer index inferred from Z,
             - curve: index of the printing path within that layer.

      1) gcode_travels (Tree<Curve>)
           Travel toolpaths (without extrusion), organised similarly as
           {layer; curve}. Includes repositioning moves, Z-hops, and
           retraction moves with motion. Empty if include_travels is
           false.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using WASPer_3DP;
#endregion

namespace WASPer_3DP.Components._5_0_Gcode
{
    public class wsp_Gc06_WASPer_Path_from_Gcode : GH_Component
    {
        private readonly string _versionTag;
        private bool _showAllOutputs;
        private const string ShowAllOutputsKey = "wsp_gc13_show_all_outputs";

        // --------------------------------------------------------------------
        // Constructor / metadata
        // --------------------------------------------------------------------
        public wsp_Gc06_WASPer_Path_from_Gcode()
          : base(
                "wsp_Gc06_WASPer Path from Gcode",
                "Gc06 PPath",
                "Parses G-code and reconstructs a partial WASPer Print Path plus printing/travel toolpaths organized by layer.\r\n\r\n" +
                "Plain G-code can recover geometry, approximate planes, feedrate, and motion only; process fields such as flows, layer_h, and layer_w are not reliably encoded.\r\n\r\n" +
                "Right-click Show all outputs to inspect recovered outgoing-path fields; unavailable metadata remains empty.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.0_Gcode")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null
                ? $"v{v.Major}.{v.Minor}.{v.Build}"
                : "v1.0.x";

            this.Message = _versionTag;
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("6BD9C5B8-1A1F-46C5-9C8B-0D2A2A4D36F8"); }
        }

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override void AppendAdditionalComponentMenuItems(
            ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(
                menu,
                "Show all outputs",
                (sender, args) =>
                {
                    RecordUndoEvent("Toggle all outputs");
                    _showAllOutputs = !_showAllOutputs;
                    RebuildOutputs();
                    ExpireSolution(true);
                },
                true,
                _showAllOutputs);
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetBoolean(ShowAllOutputsKey, _showAllOutputs);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            // Rebuild the output topology BEFORE base.Read() restores saved wires, so the
            // optional outputs already exist when Grasshopper reconnects them (otherwise
            // saved wires to them are silently dropped on file open).
            _showAllOutputs =
                reader.ItemExists(ShowAllOutputsKey) &&
                reader.GetBoolean(ShowAllOutputsKey);
            RebuildOutputs();
            return base.Read(reader);
        }

        private void RebuildOutputs()
        {
            const int fixedOutputCount = 4;
            while (Params.Output.Count > fixedOutputCount)
                Params.UnregisterOutputParameter(
                    Params.Output[Params.Output.Count - 1],
                    true);
            if (_showAllOutputs)
                WasperPathDebugOutputs.RegisterCore(this);
            Params.OnParametersChanged();
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Gc13_Printing_Path_from_Gcode.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        // --------------------------------------------------------------------
        // IO registration
        // --------------------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter(
                "gcode_file",
                "gcode_file",
                "Optional full path to a .gcode file. Used only if gcode_text is empty.",
                GH_ParamAccess.item);
            pManager[0].Optional = true;

            pManager.AddTextParameter(
                "gcode_text",
                "gcode_text",
                "Optional G-code text. Can be a single long string or a list of lines. If supplied and non-empty, this overrides gcode_file.",
                GH_ParamAccess.list);
            pManager[1].Optional = true;

            pManager.AddBooleanParameter(
                "include_travels",
                "travels",
                "If TRUE, also outputs non-extruding travel paths. If FALSE, gcode_travels will be an empty tree.",
                GH_ParamAccess.item,
                true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "Partial WASPer Print Path reconstructed from G-code. Contains canonical approximated point planes whose origins recover the printing path, per-location feedrate, and a motion plan. G-code does not reliably contain flow, layer_h, layer_w, material, or printability fields.",
                GH_ParamAccess.item);

            pManager.AddCurveParameter(
                "gcode_p_path",
                "p_path",
                "Printing toolpaths (with extrusion) as PolylineCurves organised in a {layer; curve} data tree.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "gcode_travels",
                "travels",
                "Travel toolpaths (without extrusion) as PolylineCurves organised in a {layer; curve} data tree.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "summary",
                "summary",
                "Summary of the reconstructed partial WASPer path and the information that plain G-code cannot reliably recover.",
                GH_ParamAccess.item);
        }

        // --------------------------------------------------------------------
        // Main solve
        // --------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            this.Message = _versionTag;

            // --- Inputs ---
            string filePath = null;
            DA.GetData(0, ref filePath);

            var textList = new List<string>();
            DA.GetDataList(1, textList);

            bool includeTravels = true;
            DA.GetData(2, ref includeTravels);

            // --- Acquire G-code lines ---
            string[] lines = null;

            // If gcode_text has anything non-empty, use that
            var aggregatedText = new List<string>();
            foreach (var s in textList)
            {
                if (string.IsNullOrWhiteSpace(s))
                    continue;

                // Split by newline just in case a single item has multiple lines
                var split = s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                foreach (var part in split)
                {
                    if (!string.IsNullOrWhiteSpace(part))
                        aggregatedText.Add(part);
                }
            }

            if (aggregatedText.Count > 0)
            {
                lines = aggregatedText.ToArray();
            }
            else if (!string.IsNullOrWhiteSpace(filePath))
            {
                if (!File.Exists(filePath))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"G-code file not found: '{filePath}'.");
                    return;
                }

                try
                {
                    lines = File.ReadAllLines(filePath);
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Failed to read G-code file: {ex.Message}");
                    return;
                }
            }
            else
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "No G-code input provided. Supply either gcode_file or gcode_text.");
                return;
            }

            if (lines == null || lines.Length == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "G-code input appears to be empty.");
                var emptyPath = new WasperPrintPath(
                    new DataTree<Point3d>(),
                    null,
                    null,
                    null,
                    isPartial: true);
                DA.SetData(0, new WasperPrintPathGoo(emptyPath));
                DA.SetDataTree(1, new GH_Structure<GH_Curve>());
                DA.SetDataTree(2, new GH_Structure<GH_Curve>());
                DA.SetData(3, "G-code input appears to be empty.");
                WasperPathDebugOutputs.SetCore(DA, this, emptyPath);
                return;
            }

            // ----------------------------------------------------------------
            // Parser state
            // ----------------------------------------------------------------
            const double E_TOL = 1e-6;
            const double Z_TOL = 1e-4;
            const double DIST_TOL = 1e-9;
            const double CURVE_LEN_TOL = 1e-6; // discard almost-zero-length paths

            var culture = CultureInfo.InvariantCulture;

            // Position & extrusion state
            Point3d currentPos = Point3d.Unset;
            bool hasCurrentPos = false;

            double currentE = 0.0;
            bool hasCurrentE = false;

            bool absPos = true;      // G90 / G91
            bool relExtrusion = false; // M82 (false) / M83 (true)
            double activeFeedrate = 0.0; // Marlin F value, mm/min

            // Layer state: cluster printing Z heights into layer indices
            // We ONLY use printing segments to define layers; travels are
            // assigned to the last known printing layer.
            var printLayerZ = new List<double>();  // unique Z per printing layer
            int currentPrintLayerIdx = -1;         // last printing layer index

            int MapPrintZToLayer(double z)
            {
                for (int i = 0; i < printLayerZ.Count; i++)
                {
                    if (Math.Abs(z - printLayerZ[i]) <= Z_TOL)
                        return i;
                }

                // New layer Z
                printLayerZ.Add(z);
                return printLayerZ.Count - 1;
            }

            // Helper class for polylines
            var printingBuilders = new List<PolyBuilder>();
            var travelBuilders = new List<PolyBuilder>();

            PolyBuilder currentPrint = null;
            PolyBuilder currentTravel = null;

            void CloseBuilder(ref PolyBuilder builder, List<PolyBuilder> list)
            {
                if (builder == null)
                    return;

                // Need at least 2 points to form a segment
                if (builder.Points.Count >= 2)
                {
                    // Remove exact duplicate last point if coincident with previous
                    int last = builder.Points.Count - 1;
                    if (builder.Points[last].DistanceToSquared(builder.Points[last - 1]) < DIST_TOL * DIST_TOL)
                        builder.Points.RemoveAt(last);

                    if (builder.Points.Count >= 2)
                    {
                        // Compute total length; discard negligible curves
                        double length = 0.0;
                        for (int i = 1; i < builder.Points.Count; i++)
                            length += builder.Points[i - 1].DistanceTo(builder.Points[i]);

                        if (length > CURVE_LEN_TOL)
                            list.Add(builder);
                    }
                }

                builder = null;
            }

            // ----------------------------------------------------------------
            // G-code line loop
            // ----------------------------------------------------------------
            foreach (var raw in lines)
            {
                if (raw == null)
                    continue;

                string line = raw.Trim();
                if (line.Length == 0)
                    continue;

                // Strip comments (everything after ';')
                int semi = line.IndexOf(';');
                if (semi >= 0)
                    line = line.Substring(0, semi).Trim();

                if (line.Length == 0)
                    continue;

                string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    continue;

                // Parse command and parameters
                int gCode = -1;
                int mCode = -1;

                double xVal = 0, yVal = 0, zVal = 0, eVal = 0, fVal = 0;
                bool hasX = false, hasY = false, hasZ = false, hasEVal = false, hasF = false;

                bool isMotion = false;

                foreach (string t in tokens)
                {
                    if (t.Length < 2)
                        continue;

                    char c = t[0];
                    string rest = t.Substring(1);

                    if (c == 'G' || c == 'g')
                    {
                        if (int.TryParse(rest, NumberStyles.Integer, culture, out int g))
                        {
                            gCode = g;

                            if (g == 0 || g == 1)
                                isMotion = true;
                            else if (g == 90)
                                absPos = true;
                            else if (g == 91)
                                absPos = false;
                            else if (g == 92)
                            {
                                // G92: position set; we only care if it sets E
                                // Parse E parameter in this same line later.
                            }
                        }
                    }
                    else if (c == 'M' || c == 'm')
                    {
                        if (int.TryParse(rest, NumberStyles.Integer, culture, out int m))
                        {
                            mCode = m;
                            if (m == 82)
                                relExtrusion = false; // absolute E
                            else if (m == 83)
                                relExtrusion = true;  // relative E
                        }
                    }
                    else if (c == 'X' || c == 'x')
                    {
                        if (double.TryParse(rest, NumberStyles.Float, culture, out double v))
                        {
                            xVal = v;
                            hasX = true;
                        }
                    }
                    else if (c == 'Y' || c == 'y')
                    {
                        if (double.TryParse(rest, NumberStyles.Float, culture, out double v))
                        {
                            yVal = v;
                            hasY = true;
                        }
                    }
                    else if (c == 'Z' || c == 'z')
                    {
                        if (double.TryParse(rest, NumberStyles.Float, culture, out double v))
                        {
                            zVal = v;
                            hasZ = true;
                        }
                    }
                    else if (c == 'E' || c == 'e')
                    {
                        if (double.TryParse(rest, NumberStyles.Float, culture, out double v))
                        {
                            eVal = v;
                            hasEVal = true;
                        }
                    }
                    else if (c == 'F' || c == 'f')
                    {
                        if (double.TryParse(rest, NumberStyles.Float, culture, out double v))
                        {
                            fVal = v;
                            hasF = true;
                        }
                    }
                }

                // Handle G92 E... (extruder reset) separately (no motion)
                if (gCode == 92 && hasEVal)
                {
                    currentE = eVal;
                    hasCurrentE = true;
                    continue;
                }

                // If this is not a motion command, skip
                if (!isMotion)
                    continue;

                // If we don't have a current position yet, initialise it
                if (!hasCurrentPos)
                {
                    currentPos = new Point3d(0, 0, 0);
                    hasCurrentPos = true;
                }

                // Build new position
                Point3d newPos = currentPos;

                if (absPos)
                {
                    if (hasX) newPos.X = xVal;
                    if (hasY) newPos.Y = yVal;
                    if (hasZ) newPos.Z = zVal;
                }
                else
                {
                    if (hasX) newPos.X += xVal;
                    if (hasY) newPos.Y += yVal;
                    if (hasZ) newPos.Z += zVal;
                }

                // Compute segment distance
                double dist = currentPos.DistanceTo(newPos);
                if (dist < DIST_TOL && !hasEVal)
                {
                    // No movement and no extrusion change -> ignore
                    continue;
                }

                // Extruder update
                double newE = currentE;
                if (hasEVal)
                {
                    if (relExtrusion)
                        newE = currentE + eVal;
                    else
                        newE = eVal;
                }

                double eSeg = hasCurrentE ? (newE - currentE) : 0.0;
                if (!hasCurrentE && hasEVal)
                {
                    // First time we see E, just initialise, don't treat as extrusion yet
                    hasCurrentE = true;
                    eSeg = 0.0;
                }

                bool isExtruding = (gCode == 1) && (eSeg > E_TOL) && (dist > DIST_TOL);
                if (hasF && double.IsFinite(fVal) && fVal > 0.0)
                    activeFeedrate = fVal;

                // If there is effectively no spatial movement, don't record geometry
                if (dist < DIST_TOL)
                {
                    currentPos = newPos;
                    currentE = newE;
                    hasCurrentE = true;
                    continue;
                }

                // ----------------------------------------------------------------
                // Build polylines with proper layer mapping
                // ----------------------------------------------------------------
                if (isExtruding)
                {
                    // Map printing Z to a stable layer index
                    // (we use the target Z of the segment; start and end Z are
                    // equal for normal sliced layers)
                    double printZ = newPos.Z;
                    int layerIdx = MapPrintZToLayer(printZ);
                    currentPrintLayerIdx = layerIdx;

                    // Close travel curve if any
                    CloseBuilder(ref currentTravel, travelBuilders);

                    if (currentPrint == null || currentPrint.Layer != layerIdx)
                    {
                        // Start new printing polyline with current position
                        CloseBuilder(ref currentPrint, printingBuilders);
                        currentPrint = new PolyBuilder(layerIdx);
                        currentPrint.Points.Add(currentPos);
                        currentPrint.Speeds.Add(activeFeedrate);
                    }

                    // Append new position to printing polyline
                    currentPrint.Points.Add(newPos);
                    currentPrint.Speeds.Add(activeFeedrate);
                }
                else
                {
                    if (includeTravels)
                    {
                        // Assign travels to the last known printing layer.
                        // If we haven't printed yet, put them in layer 0.
                        int layerIdx = currentPrintLayerIdx >= 0 ? currentPrintLayerIdx : 0;

                        // Close printing curve if any
                        CloseBuilder(ref currentPrint, printingBuilders);

                        if (currentTravel == null || currentTravel.Layer != layerIdx)
                        {
                            CloseBuilder(ref currentTravel, travelBuilders);
                            currentTravel = new PolyBuilder(layerIdx);
                            currentTravel.Points.Add(currentPos);
                            currentTravel.Speeds.Add(activeFeedrate);
                        }

                        currentTravel.Points.Add(newPos);
                        currentTravel.Speeds.Add(activeFeedrate);
                    }
                }


                // Update state
                currentPos = newPos;
                currentE = newE;
                hasCurrentE = true;
            }

            // Close any open polylines
            CloseBuilder(ref currentPrint, printingBuilders);
            CloseBuilder(ref currentTravel, travelBuilders);

            // ----------------------------------------------------------------
            // Build GH trees
            // ----------------------------------------------------------------
            var pTree = new GH_Structure<GH_Curve>();
            var tTree = new GH_Structure<GH_Curve>();
            var pPoints = new DataTree<Point3d>();
            var pPlanes = new DataTree<Plane>();
            var tPlanes = new DataTree<Plane>();
            var pSpeed = new DataTree<double>();
            var motionPlanItems = new List<WasperMotion>();

            // Printing paths
            {
                // layer -> next curve index
                var layerCounter = new Dictionary<int, int>();

                foreach (var pb in printingBuilders)
                {
                    if (pb.Points.Count < 2)
                        continue;

                    if (!layerCounter.TryGetValue(pb.Layer, out int idx))
                        idx = 0;

                    var path = new GH_Path(pb.Layer, idx);
                    layerCounter[pb.Layer] = idx + 1;

                    var pl = new Polyline(pb.Points);
                    if (pl.Count >= 2)
                    {
                        var crv = new PolylineCurve(pl);
                        pTree.Append(new GH_Curve(crv), path);
                        for (int i = 0; i < pb.Points.Count; i++)
                        {
                            pPoints.Add(pb.Points[i], path);
                            pPlanes.Add(PlaneAtPolylinePoint(pb.Points, i), path);
                            pSpeed.Add(GetSpeed(pb, i), path);
                        }
                        for (int i = 1; i < pb.Points.Count; i++)
                        {
                            motionPlanItems.Add(new WasperMotion(
                                pb.Points[i - 1], pb.Points[i], GetSpeed(pb, i),
                                WasperMotionType.Print, pb.Layer, idx, i));
                        }
                    }
                }
            }

            // Travel paths
            if (includeTravels)
            {
                var layerCounter = new Dictionary<int, int>();

                foreach (var tb in travelBuilders)
                {
                    if (tb.Points.Count < 2)
                        continue;

                    if (!layerCounter.TryGetValue(tb.Layer, out int idx))
                        idx = 0;

                    var path = new GH_Path(tb.Layer, idx);
                    layerCounter[tb.Layer] = idx + 1;

                    var pl = new Polyline(tb.Points);
                    if (pl.Count >= 2)
                    {
                        var crv = new PolylineCurve(pl);
                        tTree.Append(new GH_Curve(crv), path);
                        for (int i = 0; i < tb.Points.Count; i++)
                            tPlanes.Add(PlaneAtPolylinePoint(tb.Points, i), path);
                        for (int i = 1; i < tb.Points.Count; i++)
                        {
                            motionPlanItems.Add(new WasperMotion(
                                tb.Points[i - 1], tb.Points[i], GetSpeed(tb, i),
                                IsMostlyVertical(tb.Points[i - 1], tb.Points[i]) ? WasperMotionType.ZHop : WasperMotionType.Travel,
                                tb.Layer, idx, i));
                        }
                    }
                }
            }

            if (pTree.IsEmpty)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No printing paths (extrusion moves) were detected in the provided G-code.");
            }

            var partialPath = new WasperPrintPath(
                pPoints,
                pPlanes,
                null,
                null,
                pSpeed.DataCount > 0 ? pSpeed : null,
                motionPlan: motionPlanItems.Count > 0 ? new WasperMotionPlan(motionPlanItems) : null,
                isPartial: true);

            string summary =
                $"Recovered from G-code: {pPoints.DataCount} print points, {pTree.PathCount} print paths, {tTree.PathCount} travel paths, {pSpeed.DataCount} speed values.\n" +
                "Partial wsp_path contains canonical p_planes, p_speed, and motion_plan only; path points are derived from plane origins.\n" +
                "Missing by nature of plain G-code: flows, layer_h, layer_w, layer_wf, material, nozzle_diam unless encoded elsewhere, and Pr01/Pr03/Pr04 KPIs.";

            DA.SetData(0, new WasperPrintPathGoo(partialPath));
            DA.SetDataTree(1, pTree);
            DA.SetDataTree(2, tTree);
            DA.SetData(3, summary);
            WasperPathDebugOutputs.SetCore(DA, this, partialPath);
            Message = $"{_versionTag} | partial wsp_path";
            if (pPoints.DataCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Gc06 reconstructed a partial wsp_path: geometry, approximate planes, p_speed, and motion_plan only. Plain G-code does not reliably carry flows, layer_h, layer_w/layer_wf, material/nozzle metadata, or Pr01/Pr03/Pr04 KPIs.");
            }
        }

        private static double GetSpeed(PolyBuilder builder, int index)
        {
            if (builder == null || builder.Speeds == null || builder.Speeds.Count == 0) return 0.0;
            return builder.Speeds[Math.Min(index, builder.Speeds.Count - 1)];
        }

        private static bool IsMostlyVertical(Point3d a, Point3d b)
        {
            Vector3d v = b - a;
            if (!v.Unitize()) return false;
            return Math.Abs(Vector3d.Multiply(v, Vector3d.ZAxis)) > 0.98;
        }

        private static Plane PlaneAtPolylinePoint(IList<Point3d> points, int index)
        {
            if (points == null || points.Count == 0) return Plane.WorldXY;
            int i = Math.Max(0, Math.Min(index, points.Count - 1));
            Vector3d tangent = Vector3d.Unset;
            if (i > 0) tangent += points[i] - points[i - 1];
            if (i + 1 < points.Count) tangent += points[i + 1] - points[i];
            return PlaneFromDirection(points[i], tangent);
        }

        private static Plane PlaneFromDirection(Point3d origin, Vector3d direction)
        {
            Vector3d x = direction;
            if (!x.Unitize() || Math.Abs(Vector3d.Multiply(x, Vector3d.ZAxis)) > 0.98)
                x = Vector3d.XAxis;
            x.Z = 0.0;
            if (!x.Unitize()) x = Vector3d.XAxis;
            Vector3d y = Vector3d.CrossProduct(Vector3d.ZAxis, x);
            if (!y.Unitize()) y = Vector3d.YAxis;
            return new Plane(origin, x, y);
        }

        // --------------------------------------------------------------------
        // Helper class
        // --------------------------------------------------------------------
        private class PolyBuilder
        {
            public int Layer;
            public List<Point3d> Points;
            public List<double> Speeds;

            public PolyBuilder(int layer)
            {
                Layer = layer;
                Points = new List<Point3d>();
                Speeds = new List<double>();
            }
        }
    }
}
