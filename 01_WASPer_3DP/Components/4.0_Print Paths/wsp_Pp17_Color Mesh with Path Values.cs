// wsp_Pp17_Color Mesh with Path Values.cs
// WASPer_3DP - Subcategory: 5.0_Gcode
//
// Colors mesh vertices from a Gcode point/value field.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;

using Rhino.Geometry;

namespace WASPer_3DP.Components._4_0_Print_Paths
{
    public class wsp_Pp17_Color_Mesh_With_Path_Values : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Pp17_Color_Mesh_With_Path_Values()
            : base(
                "wsp_Pp17_Color Mesh with Path Values",
                "Color Mesh Gcode",
                "Colors one or more meshes using Gcode scalar values from a Gcode point field.\n\n" +
                "Each mesh vertex is assigned a Gcode value from the closest Gcode point-field point, " +
                "or from an inverse-distance weighted average of nearby Gcode points. " +
                "The assigned value is normalized between v_min and v_max and converted " +
                "into a vertex color using a user-defined color gradient.\n\n" +
                "This component does not calculate a Gcode process. It only visualizes an existing Gcode scalar field such as flow, speed, layer height, or another point-based value.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("3A2D5D4C-6F7B-4A8C-9D1E-2B3C4D5E6F70");

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();

                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Gc12_Color Mesh with Gcode Values.png"))
                    {
                        if (s != null) return new Bitmap(s);
                    }

                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.16_heat_trasnfer.png"))
                    {
                        if (s != null) return new Bitmap(s);
                    }
                }
                catch { }

                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "meshes",
                "meshes",
                "Meshes to color.\n" +
                "Each mesh is duplicated internally before vertex colors are assigned.",
                GH_ParamAccess.list);
            pManager[0].DataMapping = GH_DataMapping.Flatten;

            pManager.AddPointParameter(
                "pts",
                "pts",
                "Gcode point-field locations.\n" +
                "The order must match values: pts[i] corresponds to values[i].",
                GH_ParamAccess.list);
            pManager[1].DataMapping = GH_DataMapping.Flatten;

            pManager.AddNumberParameter(
                "values",
                "values",
                "Gcode scalar values corresponding to pts.\n" +
                "values[i] is the Gcode scalar value at pts[i].",
                GH_ParamAccess.list);
            pManager[2].DataMapping = GH_DataMapping.Flatten;

            pManager.AddColourParameter(
                "colors",
                "colors",
                "Color gradient used to map Gcode values.\n" +
                "If empty, the default gradient is used: Blue -> Cyan -> Yellow -> Red.\n" +
                "If only one color is provided, that color is used for all vertices.",
                GH_ParamAccess.list);
            pManager[3].Optional = true;
            pManager[3].DataMapping = GH_DataMapping.Flatten;

            pManager.AddNumberParameter(
                "v_min",
                "v_min",
                "Minimum value for the color scale.\n" +
                "If NaN, infinite, or otherwise invalid, the minimum value from values is used.",
                GH_ParamAccess.item,
                double.NaN);
            pManager[4].Optional = true;

            pManager.AddNumberParameter(
                "v_max",
                "v_max",
                "Maximum value for the color scale.\n" +
                "If NaN, infinite, or otherwise invalid, the maximum value from values is used.",
                GH_ParamAccess.item,
                double.NaN);
            pManager[5].Optional = true;

            pManager.AddBooleanParameter(
                "use_average",
                "avg",
                "Gcode value assignment mode.\n" +
                "False = each mesh vertex uses the closest Gcode point.\n" +
                "True = inverse-distance weighted average of the nearest Gcode points.\n" +
                "The averaging mode uses k = 4 nearest Gcode points.",
                GH_ParamAccess.item,
                true);
            pManager[6].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "meshes_col",
                "meshes_col",
                "Colored mesh copies with vertex colors assigned.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "v_values",
                "v_values",
                "Gcode value assigned to each mesh vertex.\n" +
                "Tree structure: {mesh_index}.",
                GH_ParamAccess.tree);

            pManager.AddColourParameter(
                "v_colors",
                "v_colors",
                "Color assigned to each mesh vertex.\n" +
                "Tree structure: {mesh_index}.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "summary",
                "summary",
                "Report of the operation, including mesh count, vertex count, Gcode point count, color mode, value range, and warnings.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var meshes = new List<Mesh>();
            var pts = new List<Point3d>();
            var values = new List<double>();
            var gradient = new List<Color>();
            double tMinInput = double.NaN;
            double tMaxInput = double.NaN;
            bool useAverage = false;

            DA.GetDataList(0, meshes);
            DA.GetDataList(1, pts);
            DA.GetDataList(2, values);
            DA.GetDataList(3, gradient);
            DA.GetData(4, ref tMinInput);
            DA.GetData(5, ref tMaxInput);
            DA.GetData(6, ref useAverage);

            var coloredMeshes = new List<Mesh>();
            var vertexValuesTree = new DataTree<double>();
            var vertexColorsTree = new DataTree<Color>();
            var warnings = new List<string>();

            if (meshes == null || meshes.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "meshes is empty.");
                SetOutputs(DA, coloredMeshes, vertexValuesTree, vertexColorsTree, "ERROR: meshes is empty.");
                return;
            }

            if (pts == null || pts.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "pts is empty.");
                SetOutputs(DA, coloredMeshes, vertexValuesTree, vertexColorsTree, "ERROR: pts is empty.");
                return;
            }

            if (values == null || values.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "values is empty.");
                SetOutputs(DA, coloredMeshes, vertexValuesTree, vertexColorsTree, "ERROR: values is empty.");
                return;
            }

            if (pts.Count != values.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "pts.Count must match values.Count.");
                SetOutputs(DA, coloredMeshes, vertexValuesTree, vertexColorsTree,
                    $"ERROR: pts.Count ({pts.Count}) does not match values.Count ({values.Count}).");
                return;
            }

            var validPts = new List<Point3d>(pts.Count);
            var validTemps = new List<double>(values.Count);
            int skippedThermalPairs = 0;

            for (int i = 0; i < pts.Count; i++)
            {
                if (!pts[i].IsValid || !IsFinite(values[i]))
                {
                    skippedThermalPairs++;
                    continue;
                }

                validPts.Add(pts[i]);
                validTemps.Add(values[i]);
            }

            if (validPts.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid point/value pairs were found.");
                SetOutputs(DA, coloredMeshes, vertexValuesTree, vertexColorsTree,
                    "ERROR: No valid point/value pairs were found.");
                return;
            }

            if (skippedThermalPairs > 0)
                warnings.Add($"Skipped {skippedThermalPairs} invalid point/value pair(s).");

            bool usedDefaultGradient = false;
            if (gradient == null || gradient.Count == 0)
            {
                gradient = WASPer_3DP.WasperPointFieldColorMapper.DefaultGradient();
                usedDefaultGradient = true;
                warnings.Add("colors was empty. Using default gradient: Blue -> Cyan -> Yellow -> Red.");
            }

            bool singleColorMode = gradient.Count == 1;

            double autoMin = validTemps.Min();
            double autoMax = validTemps.Max();
            bool usedAutoMin = !IsFinite(tMinInput);
            bool usedAutoMax = !IsFinite(tMaxInput);

            double tMin = usedAutoMin ? autoMin : tMinInput;
            double tMax = usedAutoMax ? autoMax : tMaxInput;

            if (usedAutoMin) warnings.Add("v_min was invalid. Using minimum value from values.");
            if (usedAutoMax) warnings.Add("v_max was invalid. Using maximum value from values.");

            if (IsFinite(tMin) && IsFinite(tMax) && tMin > tMax)
            {
                warnings.Add("v_min was greater than v_max. Swapping the values.");
                double tmp = tMin;
                tMin = tMax;
                tMax = tmp;
            }

            bool flatTemperatureRange = Math.Abs(tMax - tMin) <= 1e-12;
            if (flatTemperatureRange)
            {
                singleColorMode = true;
                warnings.Add("v_min equals v_max. Using one fallback color for all vertices.");
            }

            int meshInputCount = meshes.Count;
            const int averageK = 4;
            WASPer_3DP.WasperPointFieldColorResult mapResult = WASPer_3DP.WasperPointFieldColorMapper.ColorMeshes(
                meshes,
                validPts,
                validTemps,
                gradient,
                tMin,
                tMax,
                useAverage,
                singleColorMode,
                averageK,
                warnings);

            coloredMeshes = mapResult.Meshes;
            vertexValuesTree = mapResult.VertexValues;
            vertexColorsTree = mapResult.VertexColors;

            foreach (string warning in warnings)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, warning);

            string summary = BuildSummary(
                meshInputCount,
                coloredMeshes.Count,
                mapResult.SkippedMeshCount,
                mapResult.TotalVertices,
                mapResult.ColoredVertices,
                validPts.Count,
                skippedThermalPairs,
                useAverage,
                averageK,
                gradient.Count,
                usedDefaultGradient,
                tMin,
                tMax,
                autoMin,
                autoMax,
                usedAutoMin,
                usedAutoMax,
                flatTemperatureRange,
                mapResult.ParallelWorkers,
                warnings);

            DA.SetDataList(0, coloredMeshes);
            DA.SetDataTree(1, vertexValuesTree);
            DA.SetDataTree(2, vertexColorsTree);
            DA.SetData(3, summary);

            Message = $"{_versionTag} | {coloredMeshes.Count} mesh";
        }

        private static void SetOutputs(
            IGH_DataAccess da,
            List<Mesh> meshes,
            DataTree<double> temps,
            DataTree<Color> colors,
            string summary)
        {
            da.SetDataList(0, meshes);
            da.SetDataTree(1, temps);
            da.SetDataTree(2, colors);
            da.SetData(3, summary);
        }

        private static string BuildSummary(
            int meshInputCount,
            int coloredMeshCount,
            int skippedMeshCount,
            int totalVertices,
            int coloredVertices,
            int thermalPointCount,
            int skippedThermalPairs,
            bool useAverage,
            int averageK,
            int gradientCount,
            bool usedDefaultGradient,
            double tMin,
            double tMax,
            double autoMin,
            double autoMax,
            bool usedAutoMin,
            bool usedAutoMax,
            bool flatTemperatureRange,
            int parallelWorkers,
            List<string> warnings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("wsp_Pp17_Color Mesh with Path Values");
            sb.AppendLine("--------------------------------------");
            sb.AppendLine("Mesh inputs [-]: " + meshInputCount);
            sb.AppendLine("Colored meshes [-]: " + coloredMeshCount);
            sb.AppendLine("Skipped meshes [-]: " + skippedMeshCount);
            sb.AppendLine("Total mesh vertices [-]: " + totalVertices);
            sb.AppendLine("Colored vertices [-]: " + coloredVertices);
            sb.AppendLine("Gcode points [-]: " + thermalPointCount);
            sb.AppendLine("Skipped Gcode point/value pairs [-]: " + skippedThermalPairs);
            sb.AppendLine("");
            sb.AppendLine("Mapping:");
            sb.AppendLine("  mode [-]: " + (useAverage ? "inverse-distance weighted average" : "closest point"));
            sb.AppendLine("  averaging_k [-]: " + (useAverage ? averageK.ToString() : "not used"));
            sb.AppendLine("  weighting [-]: 1 / d^2");
            sb.AppendLine("  spatial_index [-]: shared kd-tree nearest-neighbor search");
            sb.AppendLine("  parallel_workers [-]: " + parallelWorkers);
            sb.AppendLine("  parallelized_step [-]: per-mesh vertex value/color mapping");
            sb.AppendLine("");
            sb.AppendLine("Color scale:");
            sb.AppendLine("  gradient_colors [-]: " + gradientCount);
            sb.AppendLine("  default_gradient [-]: " + usedDefaultGradient);
            sb.AppendLine("  v_min [Gcode value units]: " + Format(tMin));
            sb.AppendLine("  v_max [Gcode value units]: " + Format(tMax));
            sb.AppendLine("  auto_value_min [Gcode value units]: " + Format(autoMin));
            sb.AppendLine("  auto_value_max [Gcode value units]: " + Format(autoMax));
            sb.AppendLine("  used_auto_v_min [-]: " + usedAutoMin);
            sb.AppendLine("  used_auto_v_max [-]: " + usedAutoMax);
            sb.AppendLine("  flat_value_range [-]: " + flatTemperatureRange);

            if (warnings != null && warnings.Count > 0)
            {
                sb.AppendLine("");
                sb.AppendLine("Warnings:");
                for (int i = 0; i < warnings.Count; i++)
                    sb.AppendLine("  - " + warnings[i]);
            }

            return sb.ToString();
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string Format(double value)
        {
            if (double.IsPositiveInfinity(value)) return "Infinity";
            if (double.IsNegativeInfinity(value)) return "-Infinity";
            if (double.IsNaN(value)) return "NaN";
            return value.ToString("0.####");
        }

    }
}



