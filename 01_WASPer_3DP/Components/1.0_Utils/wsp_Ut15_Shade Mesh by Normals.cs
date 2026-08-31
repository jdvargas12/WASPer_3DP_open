using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Rhino.Geometry;

namespace WASPer_3DP.Components._1_0_Utils
{
    /// <summary>
    /// Adds orientation-based brightness to a mesh while preserving its geometry
    /// and underlying color mapping.
    /// </summary>
    public sealed class wsp_Ut15_ShadeMeshByNormals :
        GH_Component,
        IGH_VariableParameterComponent
    {
        private enum OptionalInput
        {
            Colors,
            LightDirection,
            Ambient,
            ShadeStrength,
            RecomputeNormals
        }

        private const string ShowAllOutputsKey = "wsp_ut15_show_all_outputs";

        private static readonly string[] OptionalInputNames =
        {
            "colors",
            "light_dir",
            "ambient",
            "shade_strength",
            "recompute_normals"
        };

        private bool _showAllOutputs;

        public wsp_Ut15_ShadeMeshByNormals()
            : base(
                "wsp_Ut15_Shade Mesh by Normals",
                "NormalShade",
                "Applies subtle directional shading to a mesh using vertex normals. " +
                "Inspired by the nNormalShader component created by Federico Borello (Encode). " +
                "Existing vertex colors are preserved unless an optional colors list is supplied. " +
                "Right-click the component to expose shading controls and diagnostic outputs.",
                WASPer_3DP.WASPerPalette.DesignFabrication,
                "1.0_Utils")
        {
            Message = "v1.0.5";
        }

        public override Guid ComponentGuid => new Guid("D8E4A1F2-6B73-4C90-9E15-2A7D5B8C4310");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon
        {
            get
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(
                    "WASPer_3DP.Resources.Icons.wsp_Ut15_Shade Mesh by Normals.png"))
                {
                    return stream == null ? null : new Bitmap(stream);
                }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddMeshParameter(
                "mesh",
                "M",
                "Mesh to shade. Its geometry is duplicated and left unchanged.",
                GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter(
                "mesh",
                "M",
                "Duplicated mesh with shaded vertex colors and recomputed vertex normals.",
                GH_ParamAccess.item);
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Colors Input", Toggle(OptionalInput.Colors), true, Has(OptionalInput.Colors));
            Menu_AppendItem(menu, "Light Direction Input", Toggle(OptionalInput.LightDirection), true, Has(OptionalInput.LightDirection));
            Menu_AppendItem(menu, "Ambient Input", Toggle(OptionalInput.Ambient), true, Has(OptionalInput.Ambient));
            Menu_AppendItem(menu, "Shade Strength Input", Toggle(OptionalInput.ShadeStrength), true, Has(OptionalInput.ShadeStrength));
            Menu_AppendItem(menu, "Recompute Normals Input", Toggle(OptionalInput.RecomputeNormals), true, Has(OptionalInput.RecomputeNormals));
            Menu_AppendSeparator(menu);
            Menu_AppendItem(
                menu,
                "Show all outputs",
                (sender, args) =>
                {
                    RecordUndoEvent("Toggle Ut15 outputs");
                    _showAllOutputs = !_showAllOutputs;
                    RebuildOutputs();
                    ExpireSolution(true);
                },
                true,
                _showAllOutputs);
        }

        private EventHandler Toggle(OptionalInput input)
        {
            return (sender, args) =>
            {
                RecordUndoEvent("Toggle " + OptionalInputNames[(int)input]);
                IGH_Param parameter = Find(input);
                if (parameter == null)
                    Params.RegisterInputParam(CreateInput(input), InsertIndex(input));
                else
                    Params.UnregisterInputParameter(parameter, true);
                Params.OnParametersChanged();
                ExpireSolution(true);
            };
        }

        private bool Has(OptionalInput input) => Find(input) != null;

        private IGH_Param Find(OptionalInput input) => Params.Input.FirstOrDefault(
            parameter => parameter.Name == OptionalInputNames[(int)input]);

        private int InputIndex(OptionalInput input) => Params.Input.FindIndex(
            parameter => parameter.Name == OptionalInputNames[(int)input]);

        private int InsertIndex(OptionalInput input)
        {
            int target = (int)input;
            for (int index = 1; index < Params.Input.Count; index++)
            {
                int other = Array.IndexOf(OptionalInputNames, Params.Input[index].Name);
                if (other > target)
                    return index;
            }
            return Params.Input.Count;
        }

        private static IGH_Param CreateInput(OptionalInput input)
        {
            switch (input)
            {
                case OptionalInput.Colors:
                    return new Param_Colour
                    {
                        Name = "colors",
                        NickName = "C",
                        Description = "Optional color list. Unwired preserves the mesh vertex colors. " +
                            "One color is broadcast; multiple colors are interpolated across the mesh vertices.",
                        Access = GH_ParamAccess.list,
                        Optional = true
                    };
                case OptionalInput.LightDirection:
                    return new Param_Vector
                    {
                        Name = "light_dir",
                        NickName = "L",
                        Description = "Direction in which virtual light rays travel. (0,0,-1) shines " +
                            "downward from above. Unwired uses the WASPer display setting.",
                        Access = GH_ParamAccess.item,
                        Optional = true
                    };
                case OptionalInput.Ambient:
                    return new Param_Number
                    {
                        Name = "ambient",
                        NickName = "A",
                        Description = "Minimum brightness retained in shadowed areas, from 0 to 1. " +
                            "Unwired uses the WASPer display setting.",
                        Access = GH_ParamAccess.item,
                        Optional = true
                    };
                case OptionalInput.ShadeStrength:
                    return new Param_Number
                    {
                        Name = "shade_strength",
                        NickName = "S",
                        Description = "Directional shading contribution, from 0 to 1. Unwired uses " +
                            "the WASPer display setting.",
                        Access = GH_ParamAccess.item,
                        Optional = true
                    };
                case OptionalInput.RecomputeNormals:
                    return new Param_Boolean
                    {
                        Name = "recompute_normals",
                        NickName = "N*",
                        Description = "Recompute and unify mesh normals before shading. Unwired false " +
                            "reuses valid input normals for faster shading on large meshes.",
                        Access = GH_ParamAccess.item,
                        Optional = true
                    };
                default:
                    throw new ArgumentOutOfRangeException(nameof(input));
            }
        }

        private void RebuildOutputs()
        {
            IGH_Param normals = Params.Output.FirstOrDefault(parameter => parameter.Name == "normals");
            IGH_Param info = Params.Output.FirstOrDefault(parameter => parameter.Name == "info");
            if (!_showAllOutputs)
            {
                if (normals != null)
                    Params.UnregisterOutputParameter(normals, true);
                if (info != null)
                    Params.UnregisterOutputParameter(info, true);
            }
            else
            {
                if (normals == null)
                {
                    Params.RegisterOutputParam(new Param_Vector
                    {
                        Name = "normals",
                        NickName = "N",
                        Description = "Vertex normals used for the shading calculation.",
                        Access = GH_ParamAccess.list
                    });
                }
                if (info == null)
                {
                    Params.RegisterOutputParam(new Param_String
                    {
                        Name = "info",
                        NickName = "info",
                        Description = "Summary of the shading operation.",
                        Access = GH_ParamAccess.item
                    });
                }
            }
            Params.OnParametersChanged();
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
            // saved wires to normals/info are silently dropped on file open).
            bool legacyOutputs = Params.Output.Any(
                parameter => parameter.Name == "normals" || parameter.Name == "info");
            _showAllOutputs = reader.ItemExists(ShowAllOutputsKey)
                ? reader.GetBoolean(ShowAllOutputsKey)
                : legacyOutputs;
            RebuildOutputs();
            return base.Read(reader);
        }

        bool IGH_VariableParameterComponent.CanInsertParameter(GH_ParameterSide side, int index) => false;
        bool IGH_VariableParameterComponent.CanRemoveParameter(GH_ParameterSide side, int index) => false;
        IGH_Param IGH_VariableParameterComponent.CreateParameter(GH_ParameterSide side, int index) => null;
        bool IGH_VariableParameterComponent.DestroyParameter(GH_ParameterSide side, int index) => false;
        void IGH_VariableParameterComponent.VariableParameterMaintenance() { }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Mesh source = null;
            var inputColors = new List<Color>();
            Vector3d lightDir = global::WASPer_3DP.WasperPrintPathPreviewSettings.LightDirection;
            double ambient = global::WASPer_3DP.WasperPrintPathPreviewSettings.Ambient;
            double strength = global::WASPer_3DP.WasperPrintPathPreviewSettings.ShadeStrength;
            bool recomputeNormals = false;

            if (!DA.GetData(0, ref source) || source == null)
                return;
            int colorsIndex = InputIndex(OptionalInput.Colors);
            int lightIndex = InputIndex(OptionalInput.LightDirection);
            int ambientIndex = InputIndex(OptionalInput.Ambient);
            int strengthIndex = InputIndex(OptionalInput.ShadeStrength);
            int normalsIndex = InputIndex(OptionalInput.RecomputeNormals);
            if (colorsIndex >= 0)
                DA.GetDataList(colorsIndex, inputColors);
            if (lightIndex >= 0)
                DA.GetData(lightIndex, ref lightDir);
            if (ambientIndex >= 0)
                DA.GetData(ambientIndex, ref ambient);
            if (strengthIndex >= 0)
                DA.GetData(strengthIndex, ref strength);
            if (normalsIndex >= 0)
                DA.GetData(normalsIndex, ref recomputeNormals);

            if (!lightDir.Unitize())
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "light_dir has zero length. Using downward rays from above.");
                lightDir = -Vector3d.ZAxis;
            }

            Vector3d towardLight = -lightDir;

            ambient = Clamp01(ambient);
            strength = Clamp01(strength);

            var mesh = source.DuplicateMesh();
            if (recomputeNormals || mesh.Normals.Count != mesh.Vertices.Count)
            {
                mesh.UnifyNormals();
                mesh.Normals.ComputeNormals();
            }

            var baseColors = BuildBaseColors(mesh, inputColors);
            var shadedColors = new Color[mesh.Vertices.Count];
            var normals = new List<Vector3d>(mesh.Vertices.Count);

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                Vector3d normal = i < mesh.Normals.Count ? mesh.Normals[i] : Vector3d.ZAxis;
                double lengthSquared = normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z;
                if (lengthSquared <= 1e-20)
                    normal = Vector3d.ZAxis;
                else if (Math.Abs(lengthSquared - 1.0) > 1e-8)
                    normal /= Math.Sqrt(lengthSquared);
                normals.Add(normal);

                double diffuse = Math.Max(0.0, normal * towardLight);
                double brightness = Clamp01(ambient + strength * diffuse);
                shadedColors[i] = ScaleColor(baseColors[i], brightness);
            }

            mesh.VertexColors.Clear();
            for (int i = 0; i < shadedColors.Length; i++)
                mesh.VertexColors.Add(shadedColors[i]);

            DA.SetData(0, mesh);
            int normalsOutput = Params.Output.FindIndex(parameter => parameter.Name == "normals");
            int infoOutput = Params.Output.FindIndex(parameter => parameter.Name == "info");
            if (normalsOutput >= 0)
                DA.SetDataList(normalsOutput, normals);
            if (infoOutput >= 0)
            {
                DA.SetData(infoOutput,
                    string.Format(
                        "Shaded {0} vertices; ambient={1:0.##}, strength={2:0.##}, colors={3}.",
                        mesh.Vertices.Count,
                        ambient,
                        strength,
                        inputColors.Count == 0 ? "mesh vertex colors" : "interpolated input colors"));
            }
        }

        private static Color[] BuildBaseColors(Mesh mesh, IList<Color> inputColors)
        {
            int count = mesh.Vertices.Count;
            var colors = new Color[count];

            if (inputColors != null && inputColors.Count > 0)
            {
                for (int i = 0; i < count; i++)
                    colors[i] = InterpolateColors(inputColors, count <= 1 ? 0.0 : (double)i / (count - 1));
                return colors;
            }

            if (mesh.VertexColors.Count == count)
            {
                for (int i = 0; i < count; i++)
                    colors[i] = mesh.VertexColors[i];
                return colors;
            }

            for (int i = 0; i < count; i++)
                colors[i] = Color.White;
            return colors;
        }

        private static Color InterpolateColors(IList<Color> colors, double t)
        {
            if (colors.Count == 1) return colors[0];
            double scaled = Clamp01(t) * (colors.Count - 1);
            int i0 = Math.Min(colors.Count - 2, (int)Math.Floor(scaled));
            double f = scaled - i0;
            Color a = colors[i0];
            Color b = colors[i0 + 1];
            return Color.FromArgb(
                Lerp(a.A, b.A, f),
                Lerp(a.R, b.R, f),
                Lerp(a.G, b.G, f),
                Lerp(a.B, b.B, f));
        }

        private static Color ScaleColor(Color color, double factor)
        {
            return Color.FromArgb(
                color.A,
                ClampByte(color.R * factor),
                ClampByte(color.G * factor),
                ClampByte(color.B * factor));
        }

        private static int Lerp(int a, int b, double t) =>
            ClampByte(a + (b - a) * t);

        private static int ClampByte(double value) =>
            (int)Math.Round(Math.Max(0.0, Math.Min(255.0, value)));

        private static double Clamp01(double value) =>
            Math.Max(0.0, Math.Min(1.0, value));
    }
}
