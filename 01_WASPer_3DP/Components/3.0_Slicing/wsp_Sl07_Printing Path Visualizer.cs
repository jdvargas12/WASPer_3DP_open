#region Component Description
/*
    Component Name:
      wsp_Sl07_PPath Visualizer (Bead, Parallel)

    Description:
      Visualizes planar 3D-printing paths as solid filament meshes.

      The component:
        - Accepts curves in a tree or list (treated as {layer; curve} structure).
        - Divides each curve into segments of length = layer_w / 2.
        - At each sampled point builds a rounded-square (superellipse) bead
          cross-section placed entirely *below* the curve (in -Z).
        - Lofts these sections into a closed mesh for each curve.
        - Closed curves (IsClosed == true, or whose endpoints are within
          half a bead-width of each other) are lofted as seamless rings
          with no flat end-caps.
        - Parallelizes per-curve processing for better performance.
        - Supports a high_res flag to switch between coarse and fine profiles.

      Geometry assumptions:
        - Input curves represent the centerline of the printing path.
        - Cross-section:
            width  = layer_w  (perpendicular to path, in plan);
            height = layer_h  (vertical, extending from the curve downwards).
        - The curve lies on the *top* of the bead (no material above it).

    Inputs:
      - p_path_curves (Tree<Curve>):
          Printing-path curves. Can be provided as a list or a data tree.
      - layer_h (Double):
          Filament height [model units]. Must be > 0.
      - layer_w (Double):
          Filament width [model units]. Must be > 0.
      - high_res (Boolean):
          If True, generates a higher-resolution bead profile (24 segments).
          If False, uses a coarser profile (10 segments) for faster computation.
          Default = False.
       - role_colors (Optional Colour list):
          Colours in [Shell, Infill, Partition, Support, Transition, Undefined] order. Each input
          curve's role is read from the shared WASPer.PathRole user-string tag
          (WasperPathRoleMetadata / WasperPathRole; written by Sl02 SlicerPlus,
          In10, and Gc01 v2). 1 colour = flat for all roles, 2-3 = interpolated
          across the 4 roles, 4 = exact one-to-one mapping. Extra colours beyond
           4 are ignored. Left empty, the default palette selected via right-click
           (Classic / Vivid / Grayscale) is used instead.
       - mesh? (Boolean):
           If True, also builds the conventional closed filament meshes.
           If False, keeps only the meshless GPU ray-marched preview.

    Outputs:
      - p_path_mesh (Tree<Mesh>):
          One closed filament mesh per curve, organized with the same
          {branch; index} structure as the input curves. Vertex colors are
          baked in per the resolved role colour, so the default Grasshopper
          preview already shows shell/infill/partition/undefined distinctly.
      - p_path_colour (Tree<Colour>):
          The resolved colour for each mesh, same {branch; index} structure.
          Wire into a native Custom Preview's Colour input if you want to
          recolor externally instead of relying on the baked vertex colors.
      - p_path_role (Tree<Text>):
          Detected role name ("Shell", "Infill", "Partition", "Support", "Transition", "Undefined") for
          each curve/mesh, same {branch; index} structure.

*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion

namespace WASPer_3DP_Components._3_0_Slicing
{
    public class WSP_Sl07_Printing_Path_Visualizer : GH_Component
    {
        private readonly string _versionTag;
        private readonly List<global::WASPer_3DP.WasperPrintPathSegmentRenderer> _previewRenderers =
            new List<global::WASPer_3DP.WasperPrintPathSegmentRenderer>();
        private BoundingBox _clippingBox = BoundingBox.Empty;

        // ----------------------------------------------------------------
        // Role coloring: default palettes, in [Shell, Infill, Partition,
        // Support, Transition, Undefined] order. Selected via right-click when role_colors is
        // left unwired; persisted so saved documents reopen with the same
        // choice.
        // ----------------------------------------------------------------
        private static readonly string[] PaletteNames =
        {
            "Classic",
            "Vivid",
            "Raw gray redware",
            "Fired gray redware",
            "Raw red earthenware",
            "Fired red earthenware",
            "Raw buff earthenware",
            "Fired buff earthenware",
            "Raw white stoneware",
            "Fired white stoneware",
            "Raw pink clay",
            "Fired pink clay",
            "Brighter vivid",
            "Color blind",
            "Grayscale"
        };

        private static readonly Color[][] DefaultPalettes =
        {
            // Classic - matches the salmon/pink/blue swatches used manually before this component
            // could color its own output.
            new[]
            {
                Color.FromArgb(225, 83, 74),   // Shell
                Color.FromArgb(247, 187, 189), // Infill
                Color.FromArgb(146, 197, 222), // Partition
                Color.FromArgb(174, 125, 190), // Support
                Color.FromArgb(238, 158, 65),  // Transition
                Color.FromArgb(140, 140, 140), // Undefined
            },
            // Vivid - higher-contrast, saturated set.
            new[]
            {
                Color.FromArgb(198, 40, 40),   // Shell
                Color.FromArgb(255, 152, 0),   // Infill
                Color.FromArgb(0, 150, 136),   // Partition
                Color.FromArgb(124, 77, 255),  // Support
                Color.FromArgb(255, 193, 7),   // Transition
                Color.FromArgb(66, 66, 66),    // Undefined
            },
            // Clay material presets - one color across every role.
            SingleColorPalette(138, 136, 128), // Raw gray redware
            SingleColorPalette(154, 73, 45),   // Fired gray redware
            SingleColorPalette(145, 77, 57),   // Raw red earthenware
            SingleColorPalette(196, 88, 51),   // Fired red earthenware
            SingleColorPalette(174, 148, 109), // Raw buff earthenware
            SingleColorPalette(218, 184, 128), // Fired buff earthenware
            SingleColorPalette(205, 200, 184), // Raw white stoneware
            SingleColorPalette(232, 222, 199), // Fired white stoneware
            SingleColorPalette(211, 170, 158), // Raw pink clay
            SingleColorPalette(229, 157, 147), // Fired pink clay
            // Brighter vivid - saturated role colors for presentations and dense views.
            new[]
            {
                Color.FromArgb(255, 32, 92),   // Shell
                Color.FromArgb(255, 214, 10),  // Infill
                Color.FromArgb(0, 229, 255),   // Partition
                Color.FromArgb(155, 77, 255),  // Support
                Color.FromArgb(57, 255, 20),   // Transition
                Color.FromArgb(35, 35, 35),    // Undefined
            },
            // Color blind - Okabe-Ito-inspired high-legibility set.
            new[]
            {
                Color.FromArgb(213, 94, 0),    // Shell
                Color.FromArgb(0, 114, 178),   // Infill
                Color.FromArgb(0, 158, 115),   // Partition
                Color.FromArgb(204, 121, 167), // Support
                Color.FromArgb(240, 228, 66),  // Transition
                Color.FromArgb(102, 102, 102), // Undefined
            },
            // Grayscale - neutral tones; Undefined stays reddish so unrecognized
            // paths still stand out as something to check.
            new[]
            {
                Color.FromArgb(60, 60, 60),    // Shell
                Color.FromArgb(130, 130, 130), // Infill
                Color.FromArgb(190, 190, 190), // Partition
                Color.FromArgb(95, 95, 95),    // Support
                Color.FromArgb(160, 160, 160), // Transition
                Color.FromArgb(220, 60, 60),   // Undefined
            },
        };

        private static Color[] SingleColorPalette(int r, int g, int b)
        {
            Color color = Color.FromArgb(r, g, b);
            return new[] { color, color, color, color, color, color };
        }

        private int _paletteIndex;
        private const string PaletteIndexKey = "wsp_sl07_palette_index";
        private const int AllRolesMask = 0b111111;
        private const string VisibleRolesMaskKey = "wsp_sl07_visible_roles_mask";
        private int _visibleRolesMask = AllRolesMask;
        private static readonly string[] RoleNames =
            { "Shell", "Infill", "Partition", "Support", "Transition", "Undefined" };

        public WSP_Sl07_Printing_Path_Visualizer()
          : base(
                "wsp_Sl07_Visualize Printing Path",
                "PPath Vis",
                "Visualizes printing paths as GPU ray-marched rounded-square beads, with optional filament mesh output.\n" +
                "This component just allows visualization of HOMOGENEOUS printing paths ----> Constant height and width / thickness. \n" +
                "NON-PLANAR printing paths, can be visualized as long as they have a HOMOGENEOUS layer height. \n" +
                "Closed curves (or curves whose endpoints are within half a bead-width) are lofted as seamless rings with no end-caps.\n" +
                "For NON-HOMOGENEOUS and or NON-PLANAR printing path visualization you can use the component 'wsp_Gc07_Visualize Gcode Path (Points)'.\n" +
                "Each curve's Shell/Infill/Partition/Support/Transition/Undefined role is auto-detected from its WASPer.PathRole tag " +
                "and colored accordingly; wire role_colors to override, or right-click to pick a default palette. " +
                "The right-click Visible roles submenu can exclude roles from every output.\n",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.0_Slicing")
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
            get { return new Guid("D4F2F6D1-7C1B-4C2F-9E3A-6E887C1F9E0B"); }
        }

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        public override BoundingBox ClippingBox => _clippingBox;

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);

            var header = Menu_AppendItem(
                menu,
                "Local fallback palette (used when WASPet palette is disabled)");
            header.DropDown.Closing += KeepPaletteMenuOpenWhenChoosing;
            for (int i = 0; i < PaletteNames.Length; i++)
            {
                int paletteIndex = i;
                Menu_AppendItem(
                    header.DropDown,
                    PaletteNames[paletteIndex],
                    (sender, args) =>
                    {
                        if (_paletteIndex == paletteIndex) return;
                        RecordUndoEvent("Change PPath Visualizer default palette");
                        _paletteIndex = paletteIndex;
                        ExpireSolution(true);
                    },
                    true,
                    _paletteIndex == paletteIndex);
            }

            var rolesHeader = Menu_AppendItem(menu, "Visible roles");
            for (int i = 0; i < RoleNames.Length; i++)
            {
                int roleIndex = i;
                Menu_AppendItem(
                    rolesHeader.DropDown,
                    RoleNames[roleIndex],
                    (sender, args) =>
                    {
                        RecordUndoEvent("Toggle PPath Visualizer role");
                        _visibleRolesMask ^= 1 << roleIndex;
                        ExpireSolution(true);
                    },
                    true,
                    IsRoleIndexVisible(roleIndex));
            }
        }

        private static void KeepPaletteMenuOpenWhenChoosing(
            object sender,
            ToolStripDropDownClosingEventArgs e)
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                e.Cancel = true;
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetInt32(PaletteIndexKey, _paletteIndex);
            writer.SetInt32(VisibleRolesMaskKey, _visibleRolesMask);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            bool result = base.Read(reader);
            _paletteIndex = reader.ItemExists(PaletteIndexKey) ? reader.GetInt32(PaletteIndexKey) : 0;
            if (_paletteIndex < 0 || _paletteIndex >= DefaultPalettes.Length)
                _paletteIndex = 0;
            _visibleRolesMask = reader.ItemExists(VisibleRolesMaskKey)
                ? reader.GetInt32(VisibleRolesMaskKey) & AllRolesMask
                : AllRolesMask;
            return result;
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Sl06_Visualize_Printing_Path.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        // --------------------------------------------------------------------
        // IO
        // --------------------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "p_path_curves",
                "p_path",
                "Printing-path curves. Can be a list or a tree; internally treated as {layer; curve}.",
                GH_ParamAccess.tree);

            pManager.AddNumberParameter(
                "layer_h",
                "layer_h",
                "Filament height [model units]. Must be > 0.",
                GH_ParamAccess.item,
                1.0);

            pManager.AddNumberParameter(
                "layer_w",
                "layer_w",
                "Filament width [model units]. Must be > 0.",
                GH_ParamAccess.item,
                2.0);

            pManager.AddBooleanParameter(
                "high_res",
                "hi_res",
                "If True outputs a high-resolution bead mesh (24 profile segments). If False uses a coarser mesh (10 segments) for faster preview.",
                GH_ParamAccess.item,
                false);

            pManager.AddColourParameter(
                "role_colors",
                "colors",
                "Optional colours in [Shell, Infill, Partition, Support, Transition, Undefined] order. Each curve's role is read " +
                "from its WASPer.PathRole tag (set upstream by Sl02 SlicerPlus, In10, or Gc01 v2; untagged " +
                "curves are Undefined). 1 colour = flat for all roles, 2-5 = interpolated across the 6 roles, " +
                "6 = exact mapping; extra colours beyond 6 are ignored. Left empty, the default palette picked " +
                "via right-click (Classic / Vivid / Grayscale) is used instead.",
                GH_ParamAccess.list);
            pManager[4].Optional = true;

            pManager.AddBooleanParameter(
                "mesh?",
                "mesh?",
                "If True, also generates the conventional closed bead meshes. Disable for a faster, meshless GPU preview. Existing definitions default to True.",
                GH_ParamAccess.item,
                true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "p_path_mesh",
                "p_mesh",
                "Closed filament meshes with the same tree structure as the input curves. Vertex colors are " +
                "baked in per the resolved role colour, so the default Grasshopper preview already shows " +
                "shell/infill/partition/undefined distinctly. Empty when mesh? is False.",
                GH_ParamAccess.tree);

            pManager.AddColourParameter(
                "p_path_colour",
                "p_colour",
                "Resolved colour for each mesh, same tree structure as p_path_mesh. Wire into a native Custom " +
                "Preview's Colour input if you prefer to recolor externally instead of the baked vertex colors.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "p_path_role",
                "p_role",
                "Detected role name (Shell, Infill, Partition, Support, Transition, Undefined) for each curve/mesh, same tree " +
                "structure as p_path_mesh.",
                GH_ParamAccess.tree);
        }

        // --------------------------------------------------------------------
        // Main solve
        // --------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            this.Message = _versionTag;
            ClearPreviewRenderers();
            _clippingBox = BoundingBox.Empty;

            GH_Structure<GH_Curve> ghCurves;
            if (!DA.GetDataTree(0, out ghCurves)) return;

            double layer_h = 0.0;
            double layer_w = 0.0;
            bool high_res = false;
            bool makeMesh = true;
            var userColors = new List<Color>();
            if (!DA.GetData(1, ref layer_h)) return;
            if (!DA.GetData(2, ref layer_w)) return;
            if (!DA.GetData(3, ref high_res)) return;
            DA.GetDataList(4, userColors);
            DA.GetData(5, ref makeMesh);

            Color[] fallbackPalette =
                userColors.Count == 0 &&
                global::WASPer_3DP.WasperPrintPathPreviewSettings.ApplyToVisualizers
                    ? global::WASPer_3DP.WasperPrintPathPreviewSettings.ResolveRolePalette()
                    : DefaultPalettes[_paletteIndex < 0 || _paletteIndex >= DefaultPalettes.Length ? 0 : _paletteIndex];
            List<Color> roleColors = global::WASPer_3DP.WasperChartSettingsTools.ResolveSeriesColors(
                userColors.Select(c => c.ToArgb()).ToList(),
                6,
                fallbackPalette);

            WarnIfRolesHidden();

            double tol = RhinoDoc.ActiveDoc != null
                ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                : RhinoMath.SqrtEpsilon;

            if (layer_h <= tol)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "layer_h must be greater than zero.");
                return;
            }
            if (layer_w <= tol)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "layer_w must be greater than zero.");
                return;
            }

            double segLength = layer_w * 0.5;
            if (segLength <= tol)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Derived segment length (layer_w / 2) is extremely small. Using tolerance instead.");
                segLength = tol * 10.0;
            }

            int branchCount = ghCurves.PathCount;
            if (branchCount == 0)
            {
                DA.SetDataTree(0, new GH_Structure<GH_Mesh>());
                DA.SetDataTree(1, new GH_Structure<GH_Colour>());
                DA.SetDataTree(2, new GH_Structure<GH_String>());
                return;
            }

            var paths = ghCurves.Paths;
            var curveBranches = new List<List<Curve>>(branchCount);

            for (int b = 0; b < branchCount; b++)
            {
                var path = paths[b];
                var branchRaw = ghCurves.get_Branch(path);
                var branchLst = new List<Curve>();

                if (branchRaw != null)
                {
                    foreach (var goo in branchRaw)
                    {
                        var ghc = goo as GH_Curve;
                        if (ghc == null) continue;
                        Curve c = ghc.Value;
                        if (c == null || !c.IsValid) continue;
                        branchLst.Add(c.DuplicateCurve());
                    }
                }
                curveBranches.Add(branchLst);
            }

            Mesh[][] branchMeshes = new Mesh[branchCount][];
            Color[][] branchColors = new Color[branchCount][];
            global::WASPer_3DP.WasperPathRole[][] branchRoles = new global::WASPer_3DP.WasperPathRole[branchCount][];
            for (int b = 0; b < branchCount; b++)
            {
                int count = curveBranches[b].Count;
                branchMeshes[b] = count > 0 ? new Mesh[count] : Array.Empty<Mesh>();
                branchColors[b] = count > 0 ? new Color[count] : Array.Empty<Color>();
                branchRoles[b] = count > 0 ? new global::WASPer_3DP.WasperPathRole[count] : Array.Empty<global::WASPer_3DP.WasperPathRole>();
            }

            Vector3d worldZ = Vector3d.ZAxis;

            // ----------------------------------------------------------------
            // Parallel per-branch / per-curve processing. Each curve's role is
            // read from its WASPer.PathRole user-string tag (thread-safe: plain
            // RhinoCommon reads on independent curve instances), resolved to a
            // color, and baked into the built mesh's vertex colors.
            // ----------------------------------------------------------------
            Parallel.For(0, branchCount, b =>
            {
                var curvesInBranch = curveBranches[b];
                int curveCount = curvesInBranch.Count;
                if (curveCount == 0) return;

                for (int i = 0; i < curveCount; i++)
                {
                    Curve crv = curvesInBranch[i];

                    global::WASPer_3DP.WasperPathRole role = global::WASPer_3DP.WasperPathRoleMetadata.Get(crv);
                    Color color = roleColors[RoleColorIndex(role)];
                    branchColors[b][i] = color;
                    branchRoles[b][i] = role;

                    if (!IsRoleVisible(role))
                        continue;

                    Mesh m = makeMesh
                        ? BuildMeshForCurve(crv, layer_w, layer_h, segLength, tol, worldZ, high_res)
                        : null;
                    if (m != null && m.IsValid && m.Vertices.Count > 0)
                    {
                        global::WASPer_3DP.WasperPrintPathShading.Apply(
                            m,
                            color,
                            global::WASPer_3DP.WasperPrintPathPreviewSettings.LightDirection,
                            global::WASPer_3DP.WasperPrintPathPreviewSettings.Ambient,
                            global::WASPer_3DP.WasperPrintPathPreviewSettings.ShadeStrength);
                    }

                    branchMeshes[b][i] = m;
                }
            });

            // ----------------------------------------------------------------
            // Build output trees
            // ----------------------------------------------------------------
            GH_Structure<GH_Mesh> meshTree = new GH_Structure<GH_Mesh>();
            GH_Structure<GH_Colour> colourTree = new GH_Structure<GH_Colour>();
            GH_Structure<GH_String> roleTree = new GH_Structure<GH_String>();

            for (int b = 0; b < branchCount; b++)
            {
                var path = paths[b];
                var meshesInBranch = branchMeshes[b];
                if (meshesInBranch == null || meshesInBranch.Length == 0) continue;

                for (int i = 0; i < meshesInBranch.Length; i++)
                {
                    if (!IsRoleVisible(branchRoles[b][i]))
                        continue;

                    Mesh m = meshesInBranch[i];
                    if (makeMesh)
                    {
                        if (m == null || !m.IsValid || m.Vertices.Count == 0) continue;
                        m.Normals.ComputeNormals();
                        m.Compact();
                        meshTree.Append(new GH_Mesh(m), path);
                        _clippingBox.Union(m.GetBoundingBox(false));
                    }
                    colourTree.Append(new GH_Colour(branchColors[b][i]), path);
                    roleTree.Append(new GH_String(global::WASPer_3DP.WasperPathRoleMetadata.RoleName(branchRoles[b][i])), path);
                }
            }

            var previewBatches = new List<global::WASPer_3DP.WasperPrintPathPreviewBatch>();
            if (!makeMesh)
            {
                var curvesByRole = new List<Curve>[RoleNames.Length];
                for (int i = 0; i < curvesByRole.Length; i++)
                    curvesByRole[i] = new List<Curve>();

                for (int b = 0; b < branchCount; b++)
                {
                    for (int i = 0; i < curveBranches[b].Count; i++)
                    {
                        int roleIndex = RoleColorIndex(branchRoles[b][i]);
                        if (IsRoleIndexVisible(roleIndex))
                            curvesByRole[roleIndex].Add(curveBranches[b][i]);
                    }
                }

                for (int roleIndex = 0; roleIndex < curvesByRole.Length; roleIndex++)
                {
                    if (curvesByRole[roleIndex].Count == 0)
                        continue;

                    List<global::WASPer_3DP.WasperPrintPathPreviewBatch> roleBatches =
                        global::WASPer_3DP.WasperPrintPathPreviewBuilder.BuildPlanar(
                        curvesByRole[roleIndex],
                        layer_w,
                        layer_h,
                        segLength,
                        tol,
                        roleColors[roleIndex]);

                    foreach (global::WASPer_3DP.WasperPrintPathPreviewBatch batch in roleBatches)
                    {
                        previewBatches.Add(batch);
                        _clippingBox.Union(batch.Bounds);
                    }
                }
            }

            // Light/shading/profile-exponent uniforms are applied per-frame in
            // DrawViewportMeshes instead of here, so slider changes in the
            // WASPer display menu redraw live without re-running SolveInstance.
            EnsurePreviewRendererCount(previewBatches.Count);
            for (int i = 0; i < previewBatches.Count; i++)
            {
                _previewRenderers[i].SetBatch(previewBatches[i]);
            }

            DA.SetDataTree(0, meshTree);
            DA.SetDataTree(1, colourTree);
            DA.SetDataTree(2, roleTree);

            this.Message = makeMesh
                ? $"{_versionTag} | mesh"
                : $"{_versionTag} | GPU";
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            // Ambient/shade strength/light direction/bead profile exponent are
            // pure shader uniforms, not baked into the batch geometry, so they
            // are refreshed here on every redraw (cheap) instead of only in
            // SolveInstance. This lets the WASPer display menu sliders update
            // the preview live, without expiring the solution on every tick.
            bool drewAny = false;
            Vector3d lightDirection = global::WASPer_3DP.WasperPrintPathPreviewSettings.LightDirection;
            double ambient = global::WASPer_3DP.WasperPrintPathPreviewSettings.Ambient;
            double shadeStrength = global::WASPer_3DP.WasperPrintPathPreviewSettings.ShadeStrength;
            int profileExponent = global::WASPer_3DP.WasperPrintPathPreviewSettings.BeadProfileExponent;
            foreach (global::WASPer_3DP.WasperPrintPathSegmentRenderer renderer in _previewRenderers)
            {
                renderer.SetLightDirection(lightDirection);
                renderer.SetShading(ambient, shadeStrength);
                renderer.SetProfileExponent(profileExponent);
                drewAny |= renderer.Draw(args.Display);
            }

            if (!drewAny)
                base.DrawViewportMeshes(args);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            foreach (global::WASPer_3DP.WasperPrintPathSegmentRenderer renderer in _previewRenderers)
                renderer.Dispose();
            _previewRenderers.Clear();
            base.RemovedFromDocument(document);
        }

        private void ClearPreviewRenderers()
        {
            foreach (global::WASPer_3DP.WasperPrintPathSegmentRenderer renderer in _previewRenderers)
                renderer.Clear();
        }

        private void EnsurePreviewRendererCount(int count)
        {
            while (_previewRenderers.Count < count)
                _previewRenderers.Add(new global::WASPer_3DP.WasperPrintPathSegmentRenderer());

            while (_previewRenderers.Count > count)
            {
                int index = _previewRenderers.Count - 1;
                _previewRenderers[index].Dispose();
                _previewRenderers.RemoveAt(index);
            }
        }

        private static int RoleColorIndex(global::WASPer_3DP.WasperPathRole role)
        {
            switch (role)
            {
                case global::WASPer_3DP.WasperPathRole.Shell: return 0;
                case global::WASPer_3DP.WasperPathRole.Infill: return 1;
                case global::WASPer_3DP.WasperPathRole.Partition: return 2;
                case global::WASPer_3DP.WasperPathRole.Support: return 3;
                case global::WASPer_3DP.WasperPathRole.Transition: return 4;
                default: return 5; // Undefined
            }
        }

        private bool IsRoleIndexVisible(int roleIndex)
        {
            return roleIndex >= 0 &&
                   roleIndex < RoleNames.Length &&
                   (_visibleRolesMask & (1 << roleIndex)) != 0;
        }

        private bool IsRoleVisible(global::WASPer_3DP.WasperPathRole role)
        {
            return IsRoleIndexVisible(RoleColorIndex(role));
        }

        private void WarnIfRolesHidden()
        {
            if (_visibleRolesMask == AllRolesMask)
                return;

            string hidden = string.Join(
                ", ",
                Enumerable.Range(0, RoleNames.Length)
                    .Where(index => !IsRoleIndexVisible(index))
                    .Select(index => RoleNames[index]));

            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Warning,
                $"Role visibility filter is active. Hidden roles: {hidden}. " +
                "Meshes, colours, and role outputs exclude these paths.");
        }

        // ==================================================================
        // Geometry helpers (no Grasshopper calls inside ? thread-safe)
        // ==================================================================

        private Mesh BuildMeshForCurve(
            Curve crv,
            double layer_w,
            double layer_h,
            double segLength,
            double tol,
            Vector3d worldZ,
            bool highRes)
        {
            if (crv == null || !crv.IsValid)
                return null;

            double crvLength = crv.GetLength();
            if (crvLength < tol)
                return null;

            // ------------------------------------------------------------------
            // Closed-loop detection:
            // Use the Rhino curve flag first (handles circles, closed NURBs, etc.)
            // then fall back to the endpoint-distance check scaled by layer_w,
            // matching the same heuristic used in the Gc00 point-based component.
            // ------------------------------------------------------------------
            bool isClosed = crv.IsClosed ||
                            crv.PointAtStart.DistanceTo(crv.PointAtEnd) <= layer_w * 0.8;

            // --- Sample parameters along curve ---
            List<double> tList = new List<double>();
            tList.Add(crv.Domain.Min);

            double currentLength = segLength;
            while (currentLength < crvLength - tol)
            {
                double tParam;
                if (crv.LengthParameter(currentLength, out tParam))
                    tList.Add(tParam);
                else
                    break;

                currentLength += segLength;
            }

            // For open curves: always include the end point.
            // For closed curves: do NOT add the end point — it duplicates the start
            // and would create an overlapping section at the seam.
            if (!isClosed)
                tList.Add(crv.Domain.Max);

            if (tList.Count < 2)
                return null;

            int profileSegs = highRes ? 24 : 10;

            // --- Build cross-section polylines ---
            List<Polyline> sections = new List<Polyline>();
            Vector3d lastTangent = Vector3d.XAxis;
            int n = tList.Count;

            for (int i = 0; i < n; i++)
            {
                double t = tList[i];
                Point3d pt = crv.PointAt(t);

                // For closed curves, wrap around so the first/last sections share
                // the same smooth tangent logic across the seam.
                Vector3d tan;
                if (isClosed)
                {
                    // Central-difference using neighbours, wrapping at seam
                    double tPrev = tList[(i - 1 + n) % n];
                    double tNext = tList[(i + 1) % n];
                    tan = crv.PointAt(tNext) - crv.PointAt(tPrev);
                }
                else
                {
                    tan = crv.TangentAt(t);
                }

                if (!tan.Unitize() || tan.IsTiny(tol))
                    tan = lastTangent;
                else
                    lastTangent = tan;

                Vector3d widthDir = Vector3d.CrossProduct(worldZ, tan);
                if (!widthDir.Unitize() || widthDir.IsTiny(tol))
                    widthDir = Vector3d.XAxis;

                Vector3d heightDir = -worldZ;

                Polyline section = GenerateRoundedSquareSection(
                    pt, widthDir, heightDir, layer_w, layer_h, profileSegs, 4.0);

                if (section != null && section.Count >= 4)
                    sections.Add(section);
            }

            if (sections.Count < 2)
                return null;

            // Pass isClosed so the loft either wraps (ring) or caps (open tube)
            Mesh pathMesh = LoftSectionPolylinesToMesh(sections, isClosed, tol);
            if (pathMesh == null || !pathMesh.IsValid || pathMesh.Vertices.Count == 0)
                return null;

            return pathMesh;
        }

        private Polyline GenerateRoundedSquareSection(
            Point3d pt,
            Vector3d widthDir,
            Vector3d heightDir,
            double layer_w,
            double layer_h,
            int segs,
            double power)
        {
            if (segs < 8) segs = 8;
            if (power < 2.0) power = 2.0;

            Polyline pl = new Polyline();

            double a = layer_w * 0.5;
            double b = layer_h * 0.5;
            double centerY = b;

            for (int i = 0; i < segs; i++)
            {
                double t = (2.0 * Math.PI * i) / segs;

                double cosT = Math.Cos(t);
                double sinT = Math.Sin(t);

                double xUnit = Math.Sign(cosT) * Math.Pow(Math.Abs(cosT), 2.0 / power);
                double yUnit = Math.Sign(sinT) * Math.Pow(Math.Abs(sinT), 2.0 / power);

                double xLocal = a * xUnit;
                double yRel = b * yUnit;
                double yLocal = centerY + yRel;

                Point3d p = pt + widthDir * xLocal + heightDir * yLocal;
                pl.Add(p);
            }

            pl.Add(pl[0]); // close profile loop
            return pl;
        }

        /// <summary>
        /// Lofts closed cross-section polylines into a mesh.
        ///
        /// When <paramref name="isClosed"/> is true:
        ///   - An extra ring of quads connects the last section back to the first,
        ///     sealing the tube into a seamless torus-like solid.
        ///   - No flat end-caps are added.
        ///
        /// When <paramref name="isClosed"/> is false:
        ///   - Original behaviour: open barrel + two flat end-caps.
        /// </summary>
        private Mesh LoftSectionPolylinesToMesh(
            List<Polyline> sections,
            bool isClosed,
            double tol)
        {
            if (sections == null || sections.Count < 2)
                return null;

            int profileCount = sections.Count;
            int vertPerProfile = sections[0].Count;

            if (vertPerProfile < 4)
                return null;

            for (int i = 1; i < profileCount; i++)
            {
                if (sections[i].Count != vertPerProfile)
                    return null;
            }

            Mesh mesh = new Mesh();
            int[,] idx = new int[profileCount, vertPerProfile];

            for (int i = 0; i < profileCount; i++)
            {
                Polyline pl = sections[i];
                for (int j = 0; j < vertPerProfile; j++)
                    idx[i, j] = mesh.Vertices.Add(pl[j]);
            }

            // Barrel quads between consecutive sections
            for (int i = 0; i < profileCount - 1; i++)
            {
                for (int j = 0; j < vertPerProfile - 1; j++)
                {
                    int i0 = idx[i, j];
                    int i1 = idx[i, j + 1];
                    int i2 = idx[i + 1, j + 1];
                    int i3 = idx[i + 1, j];

                    if (i0 == i1 || i1 == i2 || i2 == i3 || i3 == i0)
                        continue;

                    mesh.Faces.AddFace(i0, i1, i2, i3);
                }
            }

            if (isClosed)
            {
                // Wrap-around ring: last section ? first section
                int last = profileCount - 1;
                for (int j = 0; j < vertPerProfile - 1; j++)
                {
                    int i0 = idx[last, j];
                    int i1 = idx[last, j + 1];
                    int i2 = idx[0, j + 1];
                    int i3 = idx[0, j];

                    if (i0 == i1 || i1 == i2 || i2 == i3 || i3 == i0)
                        continue;

                    mesh.Faces.AddFace(i0, i1, i2, i3);
                }
                // No caps — the ring above fully seals the tube.
            }
            else
            {
                // Open path: flat end-caps
                Mesh startCap = Mesh.CreateFromClosedPolyline(sections[0]);
                if (startCap != null && startCap.IsValid && startCap.Vertices.Count > 0)
                    mesh.Append(startCap);

                Polyline last = new Polyline(sections[sections.Count - 1]);
                last.Reverse();
                Mesh endCap = Mesh.CreateFromClosedPolyline(last);
                if (endCap != null && endCap.IsValid && endCap.Vertices.Count > 0)
                    mesh.Append(endCap);
            }

            if (!mesh.IsValid || mesh.Vertices.Count == 0)
                return null;

            return mesh;
        }
    }
}
