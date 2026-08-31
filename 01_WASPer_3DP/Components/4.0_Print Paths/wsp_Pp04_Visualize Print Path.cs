#region Component Description
/*
    Component Name:
      wsp_Pp04_Visualize Gcode Path (Bead, Parallel, Non-Planar)

    Description:
      Visualizes planar or non-planar 3D-printing G-code paths as solid
      filament meshes.

      The component:
        - Accepts canonical point planes in a tree {layer; curve}; plane origins
          define the printing path.
        - Optionally accepts flow multipliers per point in a matching tree.
        - Optionally accepts per-point filament heights (layer_h tree).
        - Optionally accepts per-point planes (point_planes tree)
          to support non-planar toolpaths.

        - For every point i in a toolpath, the local bead **width** is computed as:
              
              width_i = layer_w * flux[i]

          where:
            * layer_w is the base filament width input.
            * flux[i] is the flow multiplier assigned to point i
              (flux[0] is ignored intentionally).
            * If flow data is missing, mismatched, or empty, a default list is used:
                  flux[0] = 0
                  flux[i>0] = 1

          This scaling reproduces how real G-code flow/pressure variation changes
          the *effective extrusion width*, and allows visualizing widening or
          narrowing tracks along the print.

        - At each point the component builds a rounded-square (superellipse)
          bead cross-section:
             width  = layer_w * flux[i]    (perpendicular to the path)
             height = layer_h[i]           (along the local height axis)

          The bead body is always generated *below* the path, along -heightAxis.

        - All cross-sections are lofted into a closed mesh per path.
        - Closed toolpaths (first point Ëœ last point) are detected automatically
          and lofted as a seamless ring (no end caps, last section wraps to first).
        - All branches are processed in parallel for high performance.

      Geometry assumptions:
        - Input points represent the toolpath centerline.
        - The filament is modelled as a rounded-square bead.
        - No material is placed above the path (the path lies at the bead top).

    Inputs:
      - flows (Tree<double>):
          Flow multipliers per location, matching pt_planes.
          Used to compute bead width as:
              
              width_i = layer_w * flux[i]
          
          Rules:
            * flux[0] is always ignored.
            * Segment [i-1 ? i] uses flow[i].
            * If missing or mismatched:
                  flux[0] = 0
                  flux[i>0] = 1

      - layer_h (Tree<double>):
          Filament height per point.
          Per-branch behavior:
            * match count ? use per-point height
            * single value ? replicated to all points
            * mismatch/missing ? defaults to height = 1.0

      - layer_w (Double):
          Base filament width.
          A scalar value applied to every point, but **modulated point-by-point**
          using the flow multipliers:
              
              width_i = layer_w * flux[i]

          Must be > 0.

      - point_planes (Tree<Plane>):
          Optional local point planes. Plane Z is used as the local height axis.
          If missing or mismatched, the component falls back to global Z.
          A warning is shown only if the user supplied a tree but sizes mismatch.

      - high_res (Boolean):
          TRUE  ? high-resolution bead cross-section  
          FALSE ? coarse profile (faster)

      - sim_path (Generic):
          Either global path simulation progress from 0.0 to 1.0, or the
          Program (P) output from Robots Program Simulation. Program target
          indices and coordinates are matched to wsp_path points.
          When sim_path trims the preview, the output wsp_path is clipped to the
          same simulated print moment and marked IsPartial so downstream
          components can warn or treat it as a time-sliced analysis path.
          If the incoming wsp_path already carries IsPartial=true (for example
          from Gc05), that path is treated as the authoritative current state
          and sim_path is ignored to prevent applying the simulation cut twice.

      - role_colors (Optional Colour list):
          Colours in [Shell, Infill, Partition, Support, Transition, Undefined] order. Each
          branch's role is read from wsp_path.PathRoles (set upstream by Sl02
          SlicerPlus, In10, or Pp01; untagged branches are Undefined).
          1 colour = flat for all roles, 2-3 = interpolated across the 4
          roles, 4 = exact one-to-one mapping. Left empty, the default
          palette selected via right-click (Classic / Vivid / Grayscale) is
          used instead.

      - mesh? (Boolean):
          If True, also builds the conventional closed filament meshes.
          If False, keeps only the meshless GPU impostor preview (analytic
          swept-ellipse beads with flow taper and per-point planes).
          Existing definitions default to True.

    Outputs (fixed, always visible):
      - gcode_mesh (Tree<Mesh>):
          One closed filament mesh per {layer; curve} branch. Vertex colors
          are baked in per the resolved role colour, so the default
          Grasshopper preview already shows shell/infill/partition/undefined
          distinctly.

      - wasper_path (Generic, item):
          WASPer Print Path enriched with nominal LayerW, flow-adjusted
          LayerWf, and per-segment PrintVol fields.

      - dbg_paths (Tree<Curve>):
          PolylineCurve of the toolpath.

      - dbg_profiles (Tree<Curve>):
          Cross-section profiles at each point.

      - point_planes (Tree<Plane>):
          Canonical path planes carried by wsp_path.

    Outputs (hidden by default; right-click "Debug Outputs" to reveal each one
    individually, or "Hide unconnected outputs" to clear whichever of the
    currently-shown ones have no wire attached):
      - la_planes (Tree<Plane>):
          Authoritative reference plane per logical layer, if carried by wsp_path.

      - flows (Tree<Number>):
          Flow multipliers carried by wsp_path, aligned with point_planes.

      - layer_h (Tree<Number>):
          Layer heights carried by wsp_path, aligned with point_planes.

      - layer_w (Tree<Number>):
          Nominal per-location layer width, if carried by wsp_path.

      - layer_wf (Tree<Number>):
          Flow-adjusted deposited width per point.

      - print_speed (Tree<Number>):
          Per-location print speed, if carried by wsp_path.

      - print_vol (Tree<Number>):
          Per-segment deposited volume, if carried by wsp_path.

      - path_role (Tree<Integer>):
          Stored semantic role per path branch, if carried by wsp_path.

      - stroke_id (Tree<Integer>):
          Branch-aligned continuity group, if carried by wsp_path.

      - p_colour (Tree<Colour>):
          Resolved colour per branch, matching gcode_mesh's vertex colors.

      - role_name (Tree<Text>):
          Detected role name (Shell, Infill, Partition, Support, Transition, Undefined) per branch.
*/
#endregion


#region Usings
using System;
using System.Collections;               // for non-generic IList
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
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
#endregion

namespace WASPer_3DP.Components._4_0_Print_Paths
{
    public class wsp_Pp04_Visualize_Print_Path : GH_Component
    {
        private readonly string _versionTag;
        private readonly List<global::WASPer_3DP.WasperPrintPathSegmentRenderer> _previewRenderers =
            new List<global::WASPer_3DP.WasperPrintPathSegmentRenderer>();
        private BoundingBox _clippingBox = BoundingBox.Empty;

        public wsp_Pp04_Visualize_Print_Path()
          : base(
                "wsp_Pp04_Visualize Print Path",
                "GcPath Vis",
                "Visualizes (non-)planar G-code printing paths as GPU ray-cast beads with flow-scaled profiles below each path, with optional solid filament mesh output (mesh?). \n" +
                "Supports per-point flow multipliers, heights, and point planes. This allows the visualization of NON-HOMOGENEOUS and/or NON-PLANAR printing paths.\n" +
                "Closed toolpaths (first Ëœ last point) are automatically detected and lofted as seamless rings. sim_path previews global print progress. An incoming partial wsp_path is visualized as-is so it is not cut twice.\n" +
                "Right-click Visible roles to exclude Shell, Infill, Partition, Support, Transition, or Undefined branches from every output; simulation progress is evaluated before this display/output filter.\n\n" +
                "PRINTABILITY WARNING: when a Shell is continuous across layers, separate interior paths are not automatically adapted. Pp04 warns about possible intersections, protrusions, lost support, and nozzle collisions; inspect all roles together.\n\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
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
            get { return new Guid("B6E4A2C1-7D93-4F80-AB16-5C9E2D7F4381"); }
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;

        public override BoundingBox ClippingBox => _clippingBox;

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

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Gc11_Visualize_Gcode_Path_and_Fluxes.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        // Legacy all-or-nothing key, still read for backward compatibility with saved files
        // (migrated to _visibleOutputsMask = AllOutputsMask on load, see Read()).
        private const string ShowAllOutputsKey = "wsp_gc11_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc11_visible_outputs_mask";

        // Order defines the mask bit for each field (bit i = OutputCatalog[i]); keep stable once
        // saved files start using it. Matches WasperPathDebugOutputs.CoreNickNames minus
        // "pt_planes" (always one of the 5 fixed outputs), plus Pp04's own two extra fields.
        private static readonly string[] OutputCatalog =
        {
            "la_planes",
            "flows",
            "layer_h",
            "layer_w",
            "layer_wf",
            "print_speed",
            "print_vol",
            "path_role",
            "stroke_id",
            "p_colour",
            "role_name"
        };
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;
        private int _visibleOutputsMask;

        private bool IsOutputVisible(string nickName)
        {
            int bit = Array.IndexOf(OutputCatalog, nickName);
            return bit >= 0 && (_visibleOutputsMask & (1 << bit)) != 0;
        }

        // ----------------------------------------------------------------
        // Role coloring: default palettes, in [Shell, Infill, Partition,
        // Support, Transition, Undefined] order (same as Sl07 Printing Path Visualizer). Selected
        // via right-click when role_colors is left unwired; persisted so
        // saved documents reopen with the same choice.
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
            new[]
            {
                Color.FromArgb(225, 83, 74),   // Shell
                Color.FromArgb(247, 187, 189), // Infill
                Color.FromArgb(146, 197, 222), // Partition
                Color.FromArgb(174, 125, 190), // Support
                Color.FromArgb(238, 158, 65),  // Transition
                Color.FromArgb(140, 140, 140), // Undefined
            },
            new[]
            {
                Color.FromArgb(198, 40, 40),   // Shell
                Color.FromArgb(255, 152, 0),   // Infill
                Color.FromArgb(0, 150, 136),   // Partition
                Color.FromArgb(124, 77, 255),  // Support
                Color.FromArgb(255, 193, 7),   // Transition
                Color.FromArgb(66, 66, 66),    // Undefined
            },
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
            new[]
            {
                Color.FromArgb(255, 32, 92),   // Shell
                Color.FromArgb(255, 214, 10),  // Infill
                Color.FromArgb(0, 229, 255),   // Partition
                Color.FromArgb(155, 77, 255),  // Support
                Color.FromArgb(57, 255, 20),   // Transition
                Color.FromArgb(35, 35, 35),    // Undefined
            },
            new[]
            {
                Color.FromArgb(213, 94, 0),    // Shell
                Color.FromArgb(0, 114, 178),   // Infill
                Color.FromArgb(0, 158, 115),   // Partition
                Color.FromArgb(204, 121, 167), // Support
                Color.FromArgb(240, 228, 66),  // Transition
                Color.FromArgb(102, 102, 102), // Undefined
            },
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
        private const string PaletteIndexKey = "wsp_gc11_palette_index";
        private const int AllRolesMask = 0b111111;
        private const string VisibleRolesMaskKey = "wsp_gc11_visible_roles_mask";
        private int _visibleRolesMask = AllRolesMask;
        private static readonly string[] RoleNames =
            { "Shell", "Infill", "Partition", "Support", "Transition", "Undefined" };

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            global::WASPer_3DP.WasperPathDebugOutputs.AppendOutputVisibilityMenu(
                this,
                menu,
                "Debug Outputs",
                OutputCatalog,
                () => _visibleOutputsMask,
                mask =>
                {
                    RecordUndoEvent("Toggle Pp04 debug outputs");
                    _visibleOutputsMask = mask;
                    RebuildOutputs();
                    ExpireSolution(true);
                },
                fixedOutputCount: 5);

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
                        RecordUndoEvent("Change Pp04 default palette");
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
                        RecordUndoEvent("Toggle Pp04 visible role");
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
            writer.SetInt32(VisibleOutputsMaskKey, _visibleOutputsMask);
            writer.SetInt32(PaletteIndexKey, _paletteIndex);
            writer.SetInt32(VisibleRolesMaskKey, _visibleRolesMask);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            // Restore component state FIRST, then rebuild the output topology, and only
            // then call base.Read(). Grasshopper restores saved parameter wires as part of
            // base.Read(); if the optional outputs don't exist yet when that happens (e.g.
            // rebuilding afterward, as this used to), their saved connections are silently
            // dropped even though the parameters reappear a moment later.
            //
            // _visibleOutputsMask migration: files saved before per-output toggles existed only
            // have the old boolean ShowAllOutputsKey. If the new mask key is missing, derive the
            // mask from that legacy flag so old "Show all outputs" files keep showing everything
            // instead of silently losing their wires.
            if (reader.ItemExists(VisibleOutputsMaskKey))
                _visibleOutputsMask = reader.GetInt32(VisibleOutputsMaskKey);
            else if (reader.ItemExists(ShowAllOutputsKey) && reader.GetBoolean(ShowAllOutputsKey))
                _visibleOutputsMask = AllOutputsMask;
            else
                _visibleOutputsMask = 0;
            _paletteIndex = reader.ItemExists(PaletteIndexKey) ? reader.GetInt32(PaletteIndexKey) : 0;
            if (_paletteIndex < 0 || _paletteIndex >= DefaultPalettes.Length)
                _paletteIndex = 0;
            _visibleRolesMask = reader.ItemExists(VisibleRolesMaskKey)
                ? reader.GetInt32(VisibleRolesMaskKey) & AllRolesMask
                : AllRolesMask;

            RebuildOutputs();

            return base.Read(reader);
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
                "All Pp04 geometry, debug trees, and the output wsp_path exclude these branches.");
        }

        // Fixed outputs: gcode_mesh, wasper_path, dbg_paths, dbg_profiles, point_planes.
        // Auxiliary per-point trees (la_planes, flows, layer_h, layer_w, layer_wf, print_speed,
        // print_vol, path_role, stroke_id, p_colour, role_name) are individually toggleable via
        // the right-click "Debug Outputs" submenu (see OutputCatalog / _visibleOutputsMask).
        private void RebuildOutputs()
        {
            const int fixedOutputCount = 5;

            while (Params.Output.Count > fixedOutputCount)
                Params.UnregisterOutputParameter(Params.Output[Params.Output.Count - 1], true);

            if (Params.Output.Count < fixedOutputCount)
            {
                while (Params.Output.Count > 0)
                    Params.UnregisterOutputParameter(Params.Output[Params.Output.Count - 1], true);

                Params.RegisterOutputParam(new Param_Mesh { Name = "gcode_mesh", NickName = "g_mesh", Description = "Closed filament meshes generated from the packed WASPer Print Path.", Access = GH_ParamAccess.tree });
                Params.RegisterOutputParam(new Param_GenericObject { Name = "wasper_path", NickName = "wsp_path", Description = "WASPer Print Path enriched with nominal LayerW, flow-adjusted LayerWf, and per-segment PrintVol fields. When sim_path trims a complete input, this output is a partial time-sliced path and carries IsPartial=true. An already-partial input is visualized and emitted without applying a second simulation cut.", Access = GH_ParamAccess.item });
                Params.RegisterOutputParam(new Param_Curve { Name = "dbg_paths", NickName = "paths", Description = "Reconstructed toolpath polylines for debugging (PolylineCurve).", Access = GH_ParamAccess.tree });
                Params.RegisterOutputParam(new Param_Curve { Name = "dbg_profiles", NickName = "profiles", Description = "Bead cross-section profiles for debugging (PolylineCurve), one per point.", Access = GH_ParamAccess.tree });
                Params.RegisterOutputParam(new Param_Plane { Name = "point_planes", NickName = "p_planes", Description = "Canonical path planes carried by wsp_path. Their origins are the printing points.", Access = GH_ParamAccess.tree });
            }

            global::WASPer_3DP.WasperPathDebugOutputs.RegisterCore(
                this,
                IsOutputVisible,
                new[] { "pt_planes" });
            if (IsOutputVisible("p_colour"))
                Params.RegisterOutputParam(new Param_Colour { Name = "p_path_colour", NickName = "p_colour", Description = "Resolved colour per branch (one per gcode_mesh branch), matching the vertex colors already baked into gcode_mesh.", Access = GH_ParamAccess.tree });
            if (IsOutputVisible("role_name"))
                Params.RegisterOutputParam(new Param_String { Name = "role_name", NickName = "role_name", Description = "Detected role name (Shell, Infill, Partition, Support, Transition, Undefined) per branch, matching gcode_mesh.", Access = GH_ParamAccess.tree });

            Params.OnParametersChanged();
        }

        // --------------------------------------------------------------------
        // IO
        // --------------------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "WASPer Print Path containing canonical pt_planes plus flows and layer_h. Plane origins define the printing path; this is the only path input required by the v2 visualizer. Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);
            pManager[0].Optional = false;

            pManager.AddNumberParameter(
                "layer_w",
                "layer_w",
                "Optional nominal/base bead width before flow adjustment, in model units. If connected, it overrides wsp_path.LayerW; otherwise the incoming path value is preserved when available. If neither is available, defaults to layer_h * 2.5. The outgoing wsp_path stores this nominal width as LayerW, estimates LayerWf by scaling the bead cross-sectional area with local flow and recovering the equivalent deposited width from layer_h, and updates per-segment PrintVol.",
                GH_ParamAccess.item,
                0.0);

            pManager.AddBooleanParameter(
                "high_res",
                "hi_res",
                "If TRUE, outputs a high-resolution bead mesh. If FALSE, uses a coarser cross-section for faster computation.",
                GH_ParamAccess.item,
                false);

            pManager.AddGenericParameter(
                "sim_path",
                "sim",
                "Either global path progress from 0.0 to 1.0, or the Program (P) output from Robots Program Simulation. Program targets are matched in order to wsp_path points, so extra home, approach, travel, and hop targets do not shift deposition progress. Ignored when wsp_path already has IsPartial=true (for example from Gc05), because that geometry already represents the current simulation state.",
                GH_ParamAccess.item);
            pManager[3].Optional = true;

            pManager.AddColourParameter(
                "role_colors",
                "colors",
                "Optional colours in [Shell, Infill, Partition, Support, Transition, Undefined] order. Each branch's role is read " +
                "from wsp_path.PathRoles (set upstream by Sl02 SlicerPlus, In10, or Pp01; untagged branches " +
                "are Undefined). 1 colour = flat for all roles, 2-5 = interpolated across the 6 roles, 6 = exact " +
                "mapping; extra colours beyond 6 are ignored. Left empty, the default palette picked via " +
                "right-click (Classic / Vivid / Grayscale) is used instead.",
                GH_ParamAccess.list);
            pManager[4].Optional = true;

            pManager.AddBooleanParameter(
                "mesh?",
                "mesh?",
                "If True, also generates the conventional closed bead meshes. Disable for a faster, meshless " +
                "GPU preview of the flow-tapered beads. Existing definitions default to True.",
                GH_ParamAccess.item,
                true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "gcode_mesh",
                "g_mesh",
                "Closed filament meshes generated from the packed WASPer Print Path.",
                GH_ParamAccess.tree);

            pManager.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "WASPer Print Path enriched with nominal LayerW, flow-adjusted LayerWf, and per-segment PrintVol fields. When sim_path trims a complete input, this output is a partial time-sliced path and carries IsPartial=true. An already-partial input is visualized and emitted without applying a second simulation cut.",
                GH_ParamAccess.item);
            pManager.AddCurveParameter(
                "dbg_paths",
                "paths",
                "Reconstructed toolpath polylines for debugging (PolylineCurve).",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "dbg_profiles",
                "profiles",
                "Bead cross-section profiles for debugging (PolylineCurve), one per point.",
                GH_ParamAccess.tree);

            pManager.AddPlaneParameter(
                "point_planes",
                "p_planes",
                "Canonical path planes carried by wsp_path. Their origins are the printing points.",
                GH_ParamAccess.tree);

            // Auxiliary debug outputs (la_planes, flows, layer_h, layer_w, layer_wf, print_speed,
            // print_vol, path_role, stroke_id, p_colour, role_name) are NOT registered here.
            // RegisterOutputParams only runs once at construction, before Read() has restored
            // any persisted state, so a _visibleOutputsMask-gated branch here would never
            // execute; the real, runtime-toggleable output set is entirely owned by
            // RebuildOutputs(), driven by the right-click "Debug Outputs" submenu.
        }
        // --------------------------------------------------------------------
        // Main solve
        // --------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            this.Message = _versionTag;
            ClearPreviewRenderers();
            _clippingBox = BoundingBox.Empty;

            global::WASPer_3DP.WasperPrintPath packedPath = null;
            if (!global::WASPer_3DP.WasperGcodeTreeUtil.TryGetPrintPath(DA, 0, out packedPath) ||
                packedPath == null || !packedPath.HasPoints)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "wsp_path must contain a non-empty pt_planes tree; plane origins define the path. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }

            if (global::WASPer_3DP.WasperGcodeTreeUtil.TryGetContinuousShellInteriorWarning(
                    packedPath,
                    out string continuousShellWarning))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    continuousShellWarning);
            }

            GH_Structure<GH_Point> ghPoints =
                global::WASPer_3DP.WasperGcodeTreeUtil.ToPointStructure(packedPath.Points);

            GH_Structure<GH_Plane> ghPointPlanes = packedPath.HasPlanes
                ? global::WASPer_3DP.WasperGcodeTreeUtil.ToPlaneStructure(packedPath.PtPlanes)
                : null;

            GH_Structure<GH_Number> ghFlows = packedPath.HasFlows
                ? global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.Flows)
                : null;

            GH_Structure<GH_Number> ghHeights = packedPath.HasLayerH
                ? global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerH)
                : null;

            bool hasPointPlaneTree = ghPointPlanes != null && ghPointPlanes.PathCount > 0;
            bool hasFluxTree = ghFlows != null && ghFlows.PathCount > 0;
            bool hasHeightTree = ghHeights != null && ghHeights.PathCount > 0;

            double layer_w = 0.0;
            bool highRes = false;
            bool explicitLayerW = Params.Input[1].SourceCount > 0 && DA.GetData(1, ref layer_w);
            DA.GetData(2, ref highRes);
            bool makeMesh = true;
            if (Params.Input.Count > 5)
                DA.GetData(5, ref makeMesh);

            var userColors = new List<Color>();
            DA.GetDataList(4, userColors);
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

            double simPath = 1.0;
            global::WASPer_3DP.WasperRobotProgramAdapter robotProgram = null;
            bool ignoredSimulationInput = packedPath.IsPartial && Params.Input[3].SourceCount > 0;
            if (!packedPath.IsPartial)
            {
                string simError;
                if (!global::WASPer_3DP.WasperGcodeTreeUtil.TryGetSimulationInput(
                    DA, 3, out simPath, out robotProgram, out simError))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, simError);
                    return;
                }
            }
            else if (ignoredSimulationInput)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Incoming wsp_path is already marked partial. Pp04 visualizes the supplied " +
                    "current-state geometry and ignores sim_path to avoid applying the simulation cut twice.");
            }
            bool simFromRobotProgram = robotProgram != null;

            int sectionSegs = highRes ? 24 : 10;
            const double defaultHeight = 1.0;

            double tol = RhinoDoc.ActiveDoc != null
                ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                : RhinoMath.SqrtEpsilon;

            if (!explicitLayerW && packedPath.HasLayerW)
                layer_w = RepresentativeLayerWidth(global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerW), 0.0, tol);
            if (explicitLayerW && packedPath.HasLayerW)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Override applied: explicit layer_w replaced wsp_path.LayerW; layer_wf and print_vol were recomputed.");

            double fallbackHeight = RepresentativeLayerHeight(ghHeights, defaultHeight, tol);

            if (layer_w <= tol)
                layer_w = Math.Max(tol * 10.0, fallbackHeight * 2.5);

            int branchCount = ghPoints.PathCount;
            if (branchCount == 0)
            {
                DA.SetDataTree(0, new GH_Structure<GH_Mesh>());
                DA.SetDataTree(1, new GH_Structure<GH_Curve>());
                DA.SetDataTree(2, new GH_Structure<GH_Curve>());
                return;
            }

            // Copy data into plain structures (thread-safe later)
            IList<GH_Path> paths = ghPoints.Paths;

            var pointBranches = new List<List<Point3d>>(branchCount);
            var fluxBranchesRaw = new List<List<double>>(branchCount);
            var heightBranchesRaw = new List<List<double>>(branchCount);
            var pointPlaneBranchesRaw = new List<List<Plane>>(branchCount);

            bool[] branchUsesDefaultPlane = new bool[branchCount];
            bool anyDefaultPlane = false;
            int fluxIndexFallbacks = 0;
            int heightIndexFallbacks = 0;
            int planeIndexFallbacks = 0;
            int invalidPlaneNormalCount = 0;
            int downwardPlaneNormalCount = 0;
            int inconsistentPlaneNormalBranches = 0;

            for (int b = 0; b < branchCount; b++)
            {
                GH_Path path = paths[b];

                // Points
                IList ptBranchRaw = ghPoints.get_Branch(path);
                var ptList = new List<Point3d>();
                if (ptBranchRaw != null)
                {
                    foreach (object goo in ptBranchRaw)
                    {
                        GH_Point ghp = goo as GH_Point;
                        if (ghp == null) continue;
                        Point3d p = ghp.Value;
                        if (!p.IsValid) continue;
                        ptList.Add(p);
                    }
                }
                pointBranches.Add(ptList);
                int nPts = ptList.Count;

                // Flows
                List<double> fluxList = null;
                if (hasFluxTree)
                {
                    bool usedIndexFallback;
                    IList flBranchRaw = GetMatchingBranch(ghFlows, path, b, out usedIndexFallback);
                    if (usedIndexFallback) fluxIndexFallbacks++;
                    if (flBranchRaw != null)
                    {
                        fluxList = new List<double>();
                        foreach (object goo in flBranchRaw)
                        {
                            GH_Number ghn = goo as GH_Number;
                            if (ghn == null) continue;
                            fluxList.Add(ghn.Value);
                        }
                    }
                }
                fluxBranchesRaw.Add(fluxList);

                // Heights
                List<double> hList = null;

                // If the user wired a single number (flat tree, one path, one item)
                // promote it to a global scalar so every branch receives it.
                double? globalHeightScalar = null;
                if (hasHeightTree && ghHeights.PathCount == 1)
                {
                    IList onlyBranch = ghHeights.get_Branch(ghHeights.Paths[0]);
                    if (onlyBranch != null && onlyBranch.Count == 1)
                    {
                        GH_Number ghn = onlyBranch[0] as GH_Number;
                        if (ghn != null) globalHeightScalar = ghn.Value;
                    }
                }

                if (globalHeightScalar.HasValue)
                {
                    // Single scalar ? replicate for every point in this branch
                    hList = new List<double>(nPts);
                    for (int i = 0; i < nPts; i++)
                        hList.Add(globalHeightScalar.Value);
                }
                else if (hasHeightTree && b < ghHeights.PathCount)
                {
                    bool usedIndexFallback;
                    IList hBranchRaw = GetMatchingBranch(ghHeights, path, b, out usedIndexFallback);
                    if (usedIndexFallback) heightIndexFallbacks++;
                    if (hBranchRaw != null)
                    {
                        hList = new List<double>();
                        foreach (object goo in hBranchRaw)
                        {
                            GH_Number ghn = goo as GH_Number;
                            if (ghn == null) continue;
                            hList.Add(ghn.Value);
                        }
                    }
                }
                heightBranchesRaw.Add(hList);

                // Point planes
                List<Plane> planeList = null;
                if (hasPointPlaneTree)
                {
                    bool usedIndexFallback;
                    IList planeBranchRaw = GetMatchingBranch(ghPointPlanes, path, b, out usedIndexFallback);
                    if (usedIndexFallback) planeIndexFallbacks++;
                    if (planeBranchRaw != null)
                    {
                        planeList = new List<Plane>();
                        foreach (object goo in planeBranchRaw)
                        {
                            GH_Plane ghp = goo as GH_Plane;
                            if (ghp == null) continue;
                            planeList.Add(ghp.Value);
                        }
                    }
                }

                // Check if point planes are usable; if not, mark fallback
                if (hasPointPlaneTree && planeList != null && planeList.Count == nPts)
                {
                    branchUsesDefaultPlane[b] = false;
                    InspectPlaneNormals(
                        planeList,
                        tol,
                        ref invalidPlaneNormalCount,
                        ref downwardPlaneNormalCount,
                        ref inconsistentPlaneNormalBranches);
                }
                else
                {
                    if (hasPointPlaneTree)
                    {
                        branchUsesDefaultPlane[b] = true;
                        anyDefaultPlane = true;
                    }
                    planeList = null;
                }
                pointPlaneBranchesRaw.Add(planeList);
            }

            if (anyDefaultPlane)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "point_planes not supplied or size-mismatched for one or more branches. Falling back to global Z-axis for those points.");
            }

            if (invalidPlaneNormalCount > 0 || downwardPlaneNormalCount > 0 || inconsistentPlaneNormalBranches > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Suspicious point_planes normals detected: invalid={invalidPlaneNormalCount}, " +
                    $"downward={downwardPlaneNormalCount}, inconsistent_branches={inconsistentPlaneNormalBranches}. " +
                    "Pp04 uses plane Z as the local layer/height direction; inverted normals can flip bead visualization or create apparent gaps.");
            }

            if (fluxIndexFallbacks > 0 || heightIndexFallbacks > 0 || planeIndexFallbacks > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"Some auxiliary branches did not have matching paths and were matched by branch index instead. " +
                    $"flows={fluxIndexFallbacks}, layer_h={heightIndexFallbacks}, point_planes={planeIndexFallbacks}. " +
                    "If layers look wrong, graft/simplify the auxiliary trees so their paths match pt_planes.");
            }

            int totalPointCount = 0;
            for (int b = 0; b < branchCount; b++)
            {
                if (pointBranches[b] != null)
                    totalPointCount += pointBranches[b].Count;
            }

            global::WASPer_3DP.WasperRobotSimulationCut robotCut = null;
            int selectedPointCount;
            if (robotProgram != null)
            {
                string mappingError;
                if (global::WASPer_3DP.WasperGcodeTreeUtil.TryGetRobotSimulationCut(
                    robotProgram, pointBranches, tol, out robotCut, out mappingError))
                {
                    selectedPointCount = robotCut.CompletedPointCount;
                    simPath = robotCut.Progress;
                    if (robotCut.MatchedPointCount < robotCut.TotalPointCount)
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Warning,
                            $"Robots simulation matched {robotCut.MatchedPointCount}/{robotCut.TotalPointCount} " +
                            "ordered wsp_path points. Progress is reliable through the matched prefix; " +
                            "verify that the same path and point order generated the program.");
                    }
                }
                else
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        mappingError + " Falling back to normalized program time.");
                    selectedPointCount = simPath >= 1.0
                        ? totalPointCount
                        : (int)Math.Floor(simPath * totalPointCount);
                }
            }
            else
            {
                selectedPointCount = simPath >= 1.0
                    ? totalPointCount
                    : (int)Math.Floor(simPath * totalPointCount);
            }
            if (selectedPointCount < 0) selectedPointCount = 0;
            if (selectedPointCount > totalPointCount) selectedPointCount = totalPointCount;

            if (robotCut != null)
            {
                ApplyRobotSimulationCut(
                    pointBranches,
                    fluxBranchesRaw,
                    heightBranchesRaw,
                    pointPlaneBranchesRaw,
                    robotCut,
                    tol);
            }
            else if (selectedPointCount < totalPointCount)
            {
                ApplyGlobalSimulationTrim(
                    pointBranches,
                    fluxBranchesRaw,
                    heightBranchesRaw,
                    pointPlaneBranchesRaw,
                    selectedPointCount);
            }

            FilterHiddenRoleBranches(
                ref paths,
                ref pointBranches,
                ref fluxBranchesRaw,
                ref heightBranchesRaw,
                ref pointPlaneBranchesRaw,
                packedPath.PathRoles);
            branchCount = paths.Count;

            this.Message = packedPath.IsPartial
                ? $"{_versionTag} | partial"
                : simPath >= 1.0
                    ? _versionTag
                    : $"{_versionTag} | {(simFromRobotProgram ? "robot " : "sim ")}{simPath:0.##}";

            // ----------------------------------------------------------------
            // Parallel per-branch processing
            // ----------------------------------------------------------------
            Mesh[] branchMeshes = new Mesh[branchCount];
            Polyline[] branchPaths = new Polyline[branchCount];
            List<Polyline>[] branchProfiles = new List<Polyline>[branchCount];
            bool[] skippedShortBranch = new bool[branchCount];
            bool[] failedMeshBranch = new bool[branchCount];
            int[] invalidFluxSections = new int[branchCount];
            int[] invalidHeightSections = new int[branchCount];
            int[] missingProfileSections = new int[branchCount];

            // Role/color per branch: read from wsp_path.PathRoles (set upstream by Sl02
            // SlicerPlus, In10, or Pp01), resolved to a color, and baked into the
            // branch's mesh vertex colors below.
            Color[] branchColors = new Color[branchCount];
            global::WASPer_3DP.WasperPathRole[] branchRoles = new global::WASPer_3DP.WasperPathRole[branchCount];

            // Per-branch data for the meshless GPU preview: the same points,
            // flow-adjusted widths, heights, and bead directions used for the
            // lofted sections, so the shader and the mesh agree exactly.
            var previewPoints = new List<Point3d>[branchCount];
            var previewWidths = new double[branchCount][];
            var previewHeights = new double[branchCount][];
            var previewHeightDirs = new Vector3d[branchCount][];
            bool[] previewClosed = new bool[branchCount];

            Vector3d worldZ = Vector3d.ZAxis;

            Parallel.For(0, branchCount, b =>
            {
                global::WASPer_3DP.WasperPathRole role =
                    global::WASPer_3DP.WasperGcodeTreeUtil.PathRoleAt(packedPath.PathRoles, paths[b]);
                branchRoles[b] = role;
                branchColors[b] = roleColors[RoleColorIndex(role)];

                List<Point3d> pts = pointBranches[b];
                if (pts == null || pts.Count < 2)
                {
                    skippedShortBranch[b] = true;
                    return;
                }

                int nPts = pts.Count;

                // Closed source curves often arrive from Pp01 without a duplicated
                // final point, so duplicate-endpoint tolerance is too strict here.
                // Use bead-width proximity to restore seamless visualization.
                double closeTol = Math.Max(tol * 10.0, layer_w * 0.8);
                bool isClosed = nPts > 3 && pts[0].DistanceTo(pts[nPts - 1]) <= closeTol;

                List<Point3d> workPts;   // points actually used for section generation
                if (isClosed && nPts > 2)
                {
                    // Remove the duplicate closing point; the loft wraps back itself.
                    workPts = new List<Point3d>(pts);
                    workPts.RemoveAt(workPts.Count - 1);
                }
                else
                {
                    workPts = pts;
                    isClosed = false; // can't close with only 2 unique points
                }

                int nWork = workPts.Count;

                previewPoints[b] = workPts;
                previewClosed[b] = isClosed;
                double[] pvWidths = new double[nWork];
                double[] pvHeights = new double[nWork];
                Vector3d[] pvHeightDirs = new Vector3d[nWork];
                previewWidths[b] = pvWidths;
                previewHeights[b] = pvHeights;
                previewHeightDirs[b] = pvHeightDirs;

                // Path polyline for debug (keep original pts so curve shows closed)
                Polyline pathPoly = new Polyline(pts);
                branchPaths[b] = pathPoly;

                // Flux branch (or default) â€” sized to nPts (original)
                List<double> fluxBranch = fluxBranchesRaw[b];
                if (fluxBranch == null || fluxBranch.Count != nPts)
                {
                    fluxBranch = new List<double>(nPts);
                    fluxBranch.Add(0.0);
                    for (int i = 1; i < nPts; i++)
                        fluxBranch.Add(1.0);
                }

                // Height per point (or default) â€” sized to nPts
                List<double> hRaw = heightBranchesRaw[b];
                double[] heights = new double[nPts];
                if (hRaw != null)
                {
                    if (hRaw.Count == nPts)
                    {
                        for (int i = 0; i < nPts; i++)
                            heights[i] = hRaw[i];
                    }
                    else if (hRaw.Count == 1)
                    {
                        double hh = hRaw[0];
                        for (int i = 0; i < nPts; i++)
                            heights[i] = hh;
                    }
                    else
                    {
                        for (int i = 0; i < nPts; i++)
                            heights[i] = defaultHeight;
                    }
                }
                else
                {
                    for (int i = 0; i < nPts; i++)
                        heights[i] = defaultHeight;
                }

                // Point planes per point (may be null ? fallback)
                List<Plane> planeBranch = pointPlaneBranchesRaw[b];
                bool hasPlanesForBranch = planeBranch != null && planeBranch.Count == nPts;

                // Build sections â€” iterate over workPts
                List<Polyline> sections = new List<Polyline>();
                branchProfiles[b] = sections;
                Vector3d lastTangent = Vector3d.XAxis;

                for (int i = 0; i < nWork; i++)
                {
                    Point3d pt = workPts[i];

                    // ----------------------------------------------------------
                    // Tangent â€” wrap-around aware
                    // For open paths: clamp at endpoints.
                    // For closed paths: indices wrap modulo nWork so the
                    // first and last sections share a smooth tangent with their
                    // neighbours across the seam.
                    // ----------------------------------------------------------
                    Vector3d tan;
                    if (isClosed)
                    {
                        // Both neighbours always exist via modular arithmetic
                        int prev = (i - 1 + nWork) % nWork;
                        int next = (i + 1) % nWork;
                        tan = workPts[next] - workPts[prev];
                    }
                    else
                    {
                        if (i > 0 && i < nWork - 1)
                            tan = workPts[i + 1] - workPts[i - 1];
                        else if (i == 0)
                            tan = workPts[1] - workPts[0];
                        else
                            tan = workPts[i] - workPts[i - 1];
                    }

                    if (!tan.Unitize() || tan.IsTiny(tol))
                        tan = lastTangent;
                    else
                        lastTangent = tan;

                    // Height direction (local +Z of frame, bead below ? -heightDir)
                    // For closed paths, index i maps directly to the original pts list
                    // (we only stripped the last duplicate, so indices 0..nWork-1 are safe).
                    Vector3d heightDir;
                    if (hasPlanesForBranch)
                    {
                        heightDir = planeBranch[i].ZAxis;
                        if (!heightDir.Unitize() || heightDir.IsTiny(tol))
                            heightDir = -worldZ;
                        else
                            heightDir = -heightDir;
                    }
                    else
                    {
                        heightDir = -worldZ;
                    }

                    // Width direction: perpendicular to both heightDir and tangent
                    Vector3d widthDir = Vector3d.CrossProduct(heightDir, tan);
                    if (!widthDir.Unitize() || widthDir.IsTiny(tol))
                    {
                        widthDir = Vector3d.CrossProduct(tan, Vector3d.XAxis);
                        if (!widthDir.Unitize() || widthDir.IsTiny(tol))
                            widthDir = Vector3d.YAxis;
                    }

                    // ----------------------------------------------------------
                    // Visual-only endpoint flux fix (open paths only).
                    // For closed loops flux[0] == 0 is still borrowed from flux[1]
                    // at i==0 to avoid a zero-width seam section.
                    // ----------------------------------------------------------
                    double fluxVis = fluxBranch[i];

                    if (i == 0 && nWork > 1 && fluxVis <= tol)
                        fluxVis = fluxBranch[1];

                    if (!isClosed && i == nWork - 1 && nWork > 1 && fluxVis <= tol)
                        fluxVis = fluxBranch[nWork - 2];

                    if (double.IsNaN(fluxVis) || double.IsInfinity(fluxVis) || fluxVis <= tol)
                    {
                        fluxVis = ResolvePositiveFlux(fluxBranch, i, tol);
                        invalidFluxSections[b]++;
                    }

                    double h = heights[i];
                    if (double.IsNaN(h) || double.IsInfinity(h) || h <= tol)
                    {
                        h = fallbackHeight;
                        invalidHeightSections[b]++;
                    }

                    double w = EstimateFlowAdjustedWidth(layer_w, h, fluxVis, tol);

                    pvWidths[i] = w;
                    pvHeights[i] = h;
                    pvHeightDirs[i] = heightDir;

                    Polyline section = GenerateRoundedSquareSection(
                        pt, widthDir, heightDir, w, h, sectionSegs, 4.0);

                    if (section != null && section.Count >= 4)
                        sections.Add(section);
                }

                if (sections.Count != nWork)
                    missingProfileSections[b] = Math.Max(0, nWork - sections.Count);

                if (sections.Count < 2)
                {
                    failedMeshBranch[b] = true;
                    return;
                }

                if (!makeMesh)
                    return;

                Mesh m = LoftSectionPolylinesToMesh(sections, isClosed);
                if (m != null && m.Vertices.Count > 0 && m.Faces.Count > 0)
                {
                    global::WASPer_3DP.WasperPrintPathShading.Apply(
                        m,
                        branchColors[b],
                        global::WASPer_3DP.WasperPrintPathPreviewSettings.LightDirection,
                        global::WASPer_3DP.WasperPrintPathPreviewSettings.Ambient,
                        global::WASPer_3DP.WasperPrintPathPreviewSettings.ShadeStrength);
                    branchMeshes[b] = m;
                }
                else
                {
                    failedMeshBranch[b] = true;
                }
            });

            // ----------------------------------------------------------------
            // Collect results into GH trees
            // ----------------------------------------------------------------
            GH_Structure<GH_Mesh> meshTree = new GH_Structure<GH_Mesh>();
            GH_Structure<GH_Curve> pathTree = new GH_Structure<GH_Curve>();
            GH_Structure<GH_Curve> profileTree = new GH_Structure<GH_Curve>();
            GH_Structure<GH_Colour> colourTree = new GH_Structure<GH_Colour>();
            GH_Structure<GH_String> roleTree = new GH_Structure<GH_String>();

            for (int b = 0; b < branchCount; b++)
            {
                GH_Path path = paths[b];

                if (branchPaths[b] != null && branchPaths[b].Count > 1)
                {
                    var c = new PolylineCurve(branchPaths[b]);
                    pathTree.Append(new GH_Curve(c), path);
                }

                var profList = branchProfiles[b];
                if (profList != null)
                {
                    foreach (Polyline pl in profList)
                    {
                        if (pl.Count > 1)
                        {
                            var c = new PolylineCurve(pl);
                            profileTree.Append(new GH_Curve(c), path);
                        }
                    }
                }

                bool meshlessBranch = !makeMesh && !skippedShortBranch[b] && !failedMeshBranch[b];
                if (branchMeshes[b] != null || meshlessBranch)
                {
                    if (branchMeshes[b] != null)
                    {
                        meshTree.Append(new GH_Mesh(branchMeshes[b]), path);
                        _clippingBox.Union(branchMeshes[b].GetBoundingBox(false));
                    }
                    colourTree.Append(new GH_Colour(branchColors[b]), path);
                    roleTree.Append(new GH_String(global::WASPer_3DP.WasperPathRoleMetadata.RoleName(branchRoles[b])), path);
                }
            }

            // ----------------------------------------------------------------
            // Meshless GPU preview: group the collected per-point bead data by
            // role and hand analytic segment batches to the impostor renderers.
            // Runs after simulation trimming and role filtering, so partial
            // prints and hidden roles are reflected automatically.
            // ----------------------------------------------------------------
            var strokesByRole = new List<global::WASPer_3DP.WasperPrintPathPreviewStroke>[RoleNames.Length];
            for (int i = 0; i < strokesByRole.Length; i++)
                strokesByRole[i] = new List<global::WASPer_3DP.WasperPrintPathPreviewStroke>();

            if (!makeMesh)
            {
                for (int b = 0; b < branchCount; b++)
                {
                    if (previewPoints[b] == null || previewPoints[b].Count < 2 ||
                        previewWidths[b] == null || previewWidths[b].Length != previewPoints[b].Count)
                        continue;

                    strokesByRole[RoleColorIndex(branchRoles[b])].Add(
                        new global::WASPer_3DP.WasperPrintPathPreviewStroke(
                            previewPoints[b],
                            previewWidths[b],
                            previewHeights[b],
                            previewHeightDirs[b],
                            previewClosed[b]));
                }
            }

            var previewBatches = new List<global::WASPer_3DP.WasperPrintPathPreviewBatch>();
            for (int roleIndex = 0; roleIndex < strokesByRole.Length; roleIndex++)
            {
                if (strokesByRole[roleIndex].Count == 0)
                    continue;

                List<global::WASPer_3DP.WasperPrintPathPreviewBatch> roleBatches =
                    global::WASPer_3DP.WasperPrintPathPreviewBuilder.Build(
                        strokesByRole[roleIndex],
                        tol,
                        roleColors[roleIndex]);

                foreach (global::WASPer_3DP.WasperPrintPathPreviewBatch batch in roleBatches)
                {
                    previewBatches.Add(batch);
                    _clippingBox.Union(batch.Bounds);
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

            this.Message = makeMesh
                ? this.Message + " | mesh"
                : this.Message + " | GPU";

            if (!makeMesh)
            {
                if (previewBatches.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "mesh? is False and no meshless preview segments could be generated.");
                }
            }
            else if (meshTree.IsEmpty)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Resulting gcode mesh tree is empty.");
            }
            else
            {
                int shortBranches = CountTrue(skippedShortBranch);
                int failedBranches = CountTrue(failedMeshBranch);
                int fluxFallbacks = Sum(invalidFluxSections);
                int heightFallbacks = Sum(invalidHeightSections);
                int missingProfiles = Sum(missingProfileSections);
                int meshBranches = meshTree.PathCount;
                if (meshBranches < branchCount || shortBranches > 0 || failedBranches > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"Visualized {meshBranches}/{branchCount} point branches. " +
                        $"short_after_sim={shortBranches}, mesh_failed={failedBranches}. " +
                        "Branches with fewer than 2 points, zero/invalid height, or zero/invalid flow cannot create bead meshes.");
                }
                if (missingProfiles > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"Generated fewer profiles than points in some branches: missing_profiles={missingProfiles}. " +
                        "This means some bead sections still failed after visual fallbacks.");
                }
                if (fluxFallbacks > 0 || heightFallbacks > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"Visual fallback dimensions were used for invalid bead sections: " +
                        $"flux={fluxFallbacks}, layer_h={heightFallbacks}. " +
                        "This usually means some incoming flow or layer_h values are zero, NaN, or rounded too close to zero.");
                }
            }

            GH_Structure<GH_Point> simPoints = BuildPointStructure(paths, pointBranches);
            GH_Structure<GH_Plane> simPointPlanes = BuildPlaneStructure(paths, pointPlaneBranchesRaw);
            GH_Structure<GH_Number> simFlows = BuildNumberStructure(paths, fluxBranchesRaw);
            GH_Structure<GH_Number> simHeights = BuildNumberStructure(paths, heightBranchesRaw);
            GH_Structure<GH_Number> simPrintSpeed = TrimNumberTreeToPointBranches(
                packedPath.HasPrintSpeed
                    ? global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.PrintSpeed)
                    : null,
                paths,
                pointBranches);

            bool outputIsPartial = packedPath.IsPartial || selectedPointCount < totalPointCount || simPath < 1.0 - 1e-9;
            if (outputIsPartial && !packedPath.IsPartial)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Pp04 output wsp_path is marked partial because sim_path/robot simulation trimmed it to the current print state.");
            }

            GH_Structure<GH_Number> layerWTree = explicitLayerW || !packedPath.HasLayerW
                ? BuildConstantTree(simPoints, layer_w)
                : TrimNumberTreeToPointBranches(
                    global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerW),
                    paths,
                    pointBranches);
            GH_Structure<GH_Number> layerWfTree =
                explicitLayerW || !packedPath.HasLayerWf
                    ? BuildLayerWidthTree(simPoints, simFlows, simHeights, layer_w, fallbackHeight, tol)
                    : TrimNumberTreeToPointBranches(
                        global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerWf),
                        paths,
                        pointBranches);
            DataTree<double> printVolTree = BuildPrintVolumeTree(simPoints, simHeights, layerWfTree, tol);

            DataTree<double> layerWData = global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(layerWTree);
            DataTree<double> layerWfData = global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(layerWfTree);
            var enrichedPath = new global::WASPer_3DP.WasperPrintPath(
                global::WASPer_3DP.WasperGcodeTreeUtil.ToPointTree(simPoints),
                global::WASPer_3DP.WasperGcodeTreeUtil.ToPlaneTree(simPointPlanes),
                global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(simFlows),
                global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(simHeights),
                simPrintSpeed != null && simPrintSpeed.DataCount > 0
                    ? global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(simPrintSpeed)
                    : null,
                null, null, null, null,
                null, null, null, null,
                null, null, packedPath.NozzleDiam, null, null,
                null, null, null, null,
                    null, null,
                    layerW: layerWData, layerWf: layerWfData, printVol: printVolTree,
                    isPartial: outputIsPartial,
                    pathRoles: global::WASPer_3DP.WasperGcodeTreeUtil.FilterPathRoles(
                        packedPath.PathRoles,
                        simPoints?.Paths),
                    layerPlanes: global::WASPer_3DP.WasperGcodeTreeUtil.FilterLayerPlanes(
                        packedPath.LayerPlanes,
                        simPoints?.Paths,
                        global::WASPer_3DP.WasperGcodeTreeUtil.CommonPathPrefixLength(
                            packedPath.PtPlanes.Paths.ToList())),
                    strokeIds: global::WASPer_3DP.WasperGcodeTreeUtil.FilterPathRoles(
                        packedPath.StrokeIds,
                        simPoints?.Paths),
                    hasCrossLayerShellContinuity: packedPath.HasCrossLayerShellContinuity);

            DA.SetDataTree(0, meshTree);
            DA.SetData(1, new global::WASPer_3DP.WasperPrintPathGoo(enrichedPath));
            DA.SetDataTree(2, pathTree);
            DA.SetDataTree(3, profileTree);
            DA.SetDataTree(4, simPointPlanes ?? new GH_Structure<GH_Plane>());
            global::WASPer_3DP.WasperPathDebugOutputs.SetCore(
                DA,
                this,
                enrichedPath);
            int colourIndex =
                global::WASPer_3DP.WasperPathDebugOutputs.OutputIndex(this, "p_colour");
            if (colourIndex >= 0)
                DA.SetDataTree(colourIndex, colourTree);
            int roleNameIndex =
                global::WASPer_3DP.WasperPathDebugOutputs.OutputIndex(this, "role_name");
            if (roleNameIndex >= 0)
                DA.SetDataTree(roleNameIndex, roleTree);
        }

        private void FilterHiddenRoleBranches(
            ref IList<GH_Path> paths,
            ref List<List<Point3d>> pointBranches,
            ref List<List<double>> flowBranches,
            ref List<List<double>> heightBranches,
            ref List<List<Plane>> planeBranches,
            DataTree<int> pathRoles)
        {
            if (_visibleRolesMask == AllRolesMask || paths == null)
                return;

            var keptIndices = new List<int>();
            for (int i = 0; i < paths.Count; i++)
            {
                global::WASPer_3DP.WasperPathRole role =
                    global::WASPer_3DP.WasperGcodeTreeUtil.PathRoleAt(
                        pathRoles,
                        paths[i]);
                if (IsRoleVisible(role))
                    keptIndices.Add(i);
            }

            var keptPaths = new List<GH_Path>(keptIndices.Count);
            var keptPoints = new List<List<Point3d>>(keptIndices.Count);
            var keptFlows = new List<List<double>>(keptIndices.Count);
            var keptHeights = new List<List<double>>(keptIndices.Count);
            var keptPlanes = new List<List<Plane>>(keptIndices.Count);
            foreach (int index in keptIndices)
            {
                keptPaths.Add(paths[index]);
                keptPoints.Add(pointBranches[index]);
                keptFlows.Add(flowBranches[index]);
                keptHeights.Add(heightBranches[index]);
                keptPlanes.Add(planeBranches[index]);
            }

            paths = keptPaths;
            pointBranches = keptPoints;
            flowBranches = keptFlows;
            heightBranches = keptHeights;
            planeBranches = keptPlanes;
        }

        private static GH_Structure<GH_Point> BuildPointStructure(
            IList<GH_Path> paths,
            List<List<Point3d>> branches)
        {
            var result = new GH_Structure<GH_Point>();
            if (paths == null || branches == null) return result;

            int count = Math.Min(paths.Count, branches.Count);
            for (int b = 0; b < count; b++)
            {
                GH_Path path = paths[b];
                result.EnsurePath(path);
                List<Point3d> branch = branches[b];
                if (branch == null) continue;
                for (int i = 0; i < branch.Count; i++)
                    if (branch[i].IsValid)
                        result.Append(new GH_Point(branch[i]), path);
            }

            return result;
        }

        private static GH_Structure<GH_Plane> BuildPlaneStructure(
            IList<GH_Path> paths,
            List<List<Plane>> branches)
        {
            var result = new GH_Structure<GH_Plane>();
            if (paths == null || branches == null) return result;

            int count = Math.Min(paths.Count, branches.Count);
            for (int b = 0; b < count; b++)
            {
                GH_Path path = paths[b];
                result.EnsurePath(path);
                List<Plane> branch = branches[b];
                if (branch == null) continue;
                for (int i = 0; i < branch.Count; i++)
                    if (branch[i].IsValid)
                        result.Append(new GH_Plane(branch[i]), path);
            }

            return result;
        }

        private static GH_Structure<GH_Number> BuildNumberStructure(
            IList<GH_Path> paths,
            List<List<double>> branches)
        {
            var result = new GH_Structure<GH_Number>();
            if (paths == null || branches == null) return result;

            int count = Math.Min(paths.Count, branches.Count);
            for (int b = 0; b < count; b++)
            {
                GH_Path path = paths[b];
                result.EnsurePath(path);
                List<double> branch = branches[b];
                if (branch == null) continue;
                for (int i = 0; i < branch.Count; i++)
                    if (double.IsFinite(branch[i]))
                        result.Append(new GH_Number(branch[i]), path);
            }

            return result;
        }

        private static GH_Structure<GH_Number> TrimNumberTreeToPointBranches(
            GH_Structure<GH_Number> source,
            IList<GH_Path> paths,
            List<List<Point3d>> pointBranches)
        {
            var result = new GH_Structure<GH_Number>();
            if (paths == null || pointBranches == null) return result;

            bool hasGlobalScalar = false;
            double globalScalar = 0.0;
            if (source != null && source.PathCount == 1)
            {
                IList onlyBranch = source.get_Branch(source.Paths[0]);
                if (onlyBranch != null && onlyBranch.Count == 1 && onlyBranch[0] is GH_Number scalar)
                {
                    hasGlobalScalar = true;
                    globalScalar = scalar.Value;
                }
            }

            int count = Math.Min(paths.Count, pointBranches.Count);
            for (int b = 0; b < count; b++)
            {
                GH_Path path = paths[b];
                result.EnsurePath(path);
                int pointCount = pointBranches[b]?.Count ?? 0;
                if (pointCount == 0) continue;

                if (hasGlobalScalar)
                {
                    for (int i = 0; i < pointCount; i++)
                        result.Append(new GH_Number(globalScalar), path);
                    continue;
                }

                IList sourceBranch = null;
                if (source != null && source.PathCount > 0)
                {
                    if (source.PathExists(path)) sourceBranch = source.get_Branch(path);
                    else if (b < source.PathCount) sourceBranch = source.get_Branch(source.Paths[b]);
                }

                for (int i = 0; i < pointCount; i++)
                {
                    double value = NumberAt(sourceBranch, i);
                    if (double.IsFinite(value))
                        result.Append(new GH_Number(value), path);
                }
            }

            return result;
        }

        private static GH_Structure<GH_Number> BuildLayerWidthTree(
            GH_Structure<GH_Point> points,
            GH_Structure<GH_Number> flows,
            GH_Structure<GH_Number> heights,
            double layerWidth,
            double fallbackHeight,
            double tol)
        {
            var result = new GH_Structure<GH_Number>();
            if (points == null) return result;

            for (int b = 0; b < points.PathCount; b++)
            {
                GH_Path path = points.Paths[b];
                IList pointBranch = points.get_Branch(path);
                int count = pointBranch != null ? pointBranch.Count : 0;
                IList flowBranch = null;
                IList heightBranch = null;

                if (flows != null && flows.PathCount > 0)
                {
                    if (flows.PathCount == 1)
                        flowBranch = flows.get_Branch(flows.Paths[0]);
                    else if (flows.PathExists(path))
                        flowBranch = flows.get_Branch(path);
                    else if (b < flows.PathCount)
                        flowBranch = flows.get_Branch(flows.Paths[b]);
                }

                if (heights != null && heights.PathCount > 0)
                {
                    if (heights.PathExists(path)) heightBranch = heights.get_Branch(path);
                    else if (b < heights.PathCount) heightBranch = heights.get_Branch(heights.Paths[b]);
                }

                double globalFlow = 1.0;
                GH_Number globalNumber = flowBranch != null && flowBranch.Count == 1
                    ? flowBranch[0] as GH_Number
                    : null;
                bool hasGlobalFlow = globalNumber != null && IsUsableFlow(globalNumber.Value, tol);
                if (hasGlobalFlow)
                    globalFlow = globalNumber.Value;

                for (int i = 0; i < count; i++)
                {
                    double flow = globalFlow;
                    if (!hasGlobalFlow && flowBranch != null && i < flowBranch.Count &&
                        flowBranch[i] is GH_Number number &&
                        IsUsableFlow(number.Value, tol))
                        flow = number.Value;

                    double height = NumberAt(heightBranch, i);
                    if (height <= tol) height = fallbackHeight;
                    result.Append(new GH_Number(EstimateFlowAdjustedWidth(layerWidth, height, flow, tol)), path);
                }
            }

            return result;
        }

        private static double EstimateFlowAdjustedWidth(double nominalWidth, double height,
            double flow, double tol)
        {
            if (nominalWidth <= tol || height <= tol || flow <= tol ||
                !double.IsFinite(nominalWidth) || !double.IsFinite(height) || !double.IsFinite(flow))
                return nominalWidth * Math.Max(flow, 1.0);

            double referenceWidth = Math.Max(nominalWidth, height);
            double referenceArea = height * (referenceWidth - height)
                + Math.PI * height * height / 4.0;
            return (flow * referenceArea) / height
                + height * (1.0 - Math.PI / 4.0);
        }

        private static GH_Structure<GH_Number> BuildConstantTree(
            GH_Structure<GH_Point> points,
            double value)
        {
            var result = new GH_Structure<GH_Number>();
            if (points == null) return result;

            for (int b = 0; b < points.PathCount; b++)
            {
                GH_Path path = points.Paths[b];
                IList branch = points.get_Branch(path);
                int count = branch?.Count ?? 0;
                for (int i = 0; i < count; i++) result.Append(new GH_Number(value), path);
            }

            return result;
        }

        private static DataTree<double> BuildPrintVolumeTree(
            GH_Structure<GH_Point> points,
            GH_Structure<GH_Number> heights,
            GH_Structure<GH_Number> widths,
            double tol)
        {
            var result = new DataTree<double>();
            if (points == null) return result;

            for (int b = 0; b < points.PathCount; b++)
            {
                GH_Path path = points.Paths[b];
                IList pointBranch = points.get_Branch(path);
                IList widthBranch = widths != null && widths.PathExists(path) ? widths.get_Branch(path) : null;
                IList heightBranch = heights != null && heights.PathExists(path) ? heights.get_Branch(path) : null;
                int count = pointBranch?.Count ?? 0;

                for (int i = 0; i < count; i++)
                {
                    double volume = 0.0;
                    if (i > 0 && pointBranch[i - 1] is GH_Point previous && pointBranch[i] is GH_Point current)
                    {
                        double width = NumberAt(widthBranch, i);
                        double height = NumberAt(heightBranch, i);
                        double length = previous.Value.DistanceTo(current.Value);
                        if (width > tol && height > tol && double.IsFinite(length))
                        {
                            double area = height * (width - height)
                                + Math.PI * height * height / 4.0;
                            if (area > 0.0 && double.IsFinite(area))
                                volume = length * area;
                        }
                    }
                    result.Add(volume, path);
                }
            }

            return result;
        }

        private static double NumberAt(IList branch, int index)
        {
            if (branch == null || branch.Count == 0) return 0.0;
            int resolved = branch.Count == 1 ? 0 : Math.Min(index, branch.Count - 1);
            return branch[resolved] is GH_Number number && double.IsFinite(number.Value) ? number.Value : 0.0;
        }

        private static double RepresentativeLayerWidth(
            GH_Structure<GH_Number> widths,
            double fallback,
            double tol)
        {
            if (widths == null || widths.PathCount == 0) return fallback;
            for (int p = 0; p < widths.PathCount; p++)
            {
                IList branch = widths.get_Branch(widths.Paths[p]);
                if (branch == null) continue;
                foreach (object goo in branch)
                    if (goo is GH_Number number && number.Value > tol && double.IsFinite(number.Value))
                        return number.Value;
            }
            return fallback;
        }

        private static bool IsUsableFlow(double value, double tol)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > tol;
        }
        private static IList GetMatchingBranch<T>(
            GH_Structure<T> tree,
            GH_Path preferredPath,
            int fallbackIndex,
            out bool usedIndexFallback)
            where T : IGH_Goo
        {
            usedIndexFallback = false;
            if (tree == null || tree.PathCount == 0)
                return null;

            if (preferredPath != null && tree.PathExists(preferredPath))
                return tree.get_Branch(preferredPath);

            if (fallbackIndex >= 0 && fallbackIndex < tree.PathCount)
            {
                usedIndexFallback = true;
                return tree.get_Branch(tree.Paths[fallbackIndex]);
            }

            return null;
        }

        private static int CountTrue(bool[] values)
        {
            if (values == null) return 0;
            int count = 0;
            for (int i = 0; i < values.Length; i++)
                if (values[i]) count++;
            return count;
        }

        private static int Sum(int[] values)
        {
            if (values == null) return 0;
            int sum = 0;
            for (int i = 0; i < values.Length; i++)
                sum += values[i];
            return sum;
        }

        private static void InspectPlaneNormals(
            IList<Plane> planes,
            double tol,
            ref int invalidCount,
            ref int downwardCount,
            ref int inconsistentBranchCount)
        {
            if (planes == null || planes.Count == 0)
                return;

            Vector3d reference = Vector3d.Unset;
            bool branchInconsistent = false;

            for (int i = 0; i < planes.Count; i++)
            {
                Vector3d z = planes[i].ZAxis;
                if (!z.IsValid || z.Length <= tol || !z.Unitize())
                {
                    invalidCount++;
                    continue;
                }

                if (Vector3d.Multiply(z, Vector3d.ZAxis) < -0.1)
                    downwardCount++;

                if (!reference.IsValid)
                {
                    reference = z;
                    continue;
                }

                if (Vector3d.Multiply(reference, z) < 0.0)
                    branchInconsistent = true;
            }

            if (branchInconsistent)
                inconsistentBranchCount++;
        }

        // ==================================================================
        // Geometry helpers
        // ==================================================================
        private static void ApplyGlobalSimulationTrim(
            List<List<Point3d>> pointBranches,
            List<List<double>> fluxBranches,
            List<List<double>> heightBranches,
            List<List<Plane>> pointPlaneBranches,
            int selectedPointCount)
        {
            int remaining = Math.Max(0, selectedPointCount);
            int branchCount = pointBranches != null ? pointBranches.Count : 0;

            for (int b = 0; b < branchCount; b++)
            {
                List<Point3d> pts = pointBranches[b];
                int count = pts != null ? pts.Count : 0;
                int take = Math.Min(count, remaining);

                if (pts != null && take < count)
                    pointBranches[b] = pts.GetRange(0, take);

                TrimMatchingBranch(fluxBranches, b, take);
                TrimMatchingBranch(heightBranches, b, take);
                TrimMatchingBranch(pointPlaneBranches, b, take);

                remaining -= take;
                if (remaining < 0) remaining = 0;
            }
        }

        private static void ApplyRobotSimulationCut(
            List<List<Point3d>> pointBranches,
            List<List<double>> fluxBranches,
            List<List<double>> heightBranches,
            List<List<Plane>> pointPlaneBranches,
            global::WASPer_3DP.WasperRobotSimulationCut cut,
            double tolerance)
        {
            int branch = cut.PartialBranchIndex;
            int point = cut.PartialPointIndex;
            double? flux = ValueAt(fluxBranches, branch, point);
            double? height = ValueAt(heightBranches, branch, point);
            Plane? plane = ValueAt(pointPlaneBranches, branch, point);

            ApplyGlobalSimulationTrim(
                pointBranches,
                fluxBranches,
                heightBranches,
                pointPlaneBranches,
                cut.CompletedPointCount);

            if (!cut.HasPartialPoint ||
                branch < 0 ||
                branch >= pointBranches.Count ||
                pointBranches[branch] == null ||
                pointBranches[branch].Count == 0 ||
                pointBranches[branch][pointBranches[branch].Count - 1]
                    .DistanceTo(cut.PartialPoint) <= tolerance)
            {
                return;
            }

            pointBranches[branch].Add(cut.PartialPoint);
            if (flux.HasValue && fluxBranches[branch] != null)
                fluxBranches[branch].Add(flux.Value);
            if (height.HasValue && heightBranches[branch] != null)
                heightBranches[branch].Add(height.Value);
            if (plane.HasValue && pointPlaneBranches[branch] != null)
            {
                Plane partialPlane = plane.Value;
                partialPlane.Origin = cut.PartialPoint;
                pointPlaneBranches[branch].Add(partialPlane);
            }
        }

        private static T? ValueAt<T>(
            List<List<T>> branches,
            int branch,
            int index)
            where T : struct
        {
            if (branches == null ||
                branch < 0 ||
                branch >= branches.Count ||
                branches[branch] == null ||
                index < 0 ||
                index >= branches[branch].Count)
            {
                return null;
            }

            return branches[branch][index];
        }

        private static void TrimMatchingBranch<T>(List<List<T>> branches, int index, int count)
        {
            if (branches == null || index < 0 || index >= branches.Count) return;

            List<T> branch = branches[index];
            if (branch == null) return;

            if (count <= 0)
            {
                branches[index] = new List<T>();
                return;
            }

            if (branch.Count > count)
                branches[index] = branch.GetRange(0, count);
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        private static double RepresentativeLayerHeight(
            GH_Structure<GH_Number> heights,
            double fallback,
            double tol)
        {
            if (heights == null || heights.PathCount == 0)
                return fallback;

            for (int p = 0; p < heights.PathCount; p++)
            {
                IList branch = heights.get_Branch(heights.Paths[p]);
                if (branch == null) continue;

                foreach (object goo in branch)
                {
                    GH_Number number = goo as GH_Number;
                    if (number != null && number.Value > tol)
                        return number.Value;
                }
            }

            return fallback;
        }

        private static double ResolvePositiveFlux(IList<double> flows, int index, double tol)
        {
            if (flows != null)
            {
                int n = flows.Count;
                for (int offset = 1; offset < n; offset++)
                {
                    int lo = index - offset;
                    if (lo >= 0)
                    {
                        double f = flows[lo];
                        if (!double.IsNaN(f) && !double.IsInfinity(f) && f > tol)
                            return f;
                    }

                    int hi = index + offset;
                    if (hi < n)
                    {
                        double f = flows[hi];
                        if (!double.IsNaN(f) && !double.IsInfinity(f) && f > tol)
                            return f;
                    }
                }
            }

            return 1.0;
        }

        private Polyline GenerateRoundedSquareSection(
            Point3d pt,
            Vector3d widthDir,
            Vector3d heightDir,
            double width,
            double height,
            int segs,
            double power)
        {
            if (width <= 0.0 || height <= 0.0)
                return null;

            if (segs < 8) segs = 8;
            if (power < 2.0) power = 2.0;

            Polyline pl = new Polyline();

            double a = width * 0.5;
            double b = height * 0.5;
            double centerY = b; // y ? [0, height]

            for (int i = 0; i < segs; i++)
            {
                double t = (2.0 * Math.PI * i) / segs;

                double cosT = Math.Cos(t);
                double sinT = Math.Sin(t);

                double xUnit = Math.Sign(cosT) * Math.Pow(Math.Abs(cosT), 2.0 / power);
                double yUnit = Math.Sign(sinT) * Math.Pow(Math.Abs(sinT), 2.0 / power);

                double xLocal = a * xUnit;
                double yRel = b * yUnit;
                double yLocal = centerY + yRel; // [0, height]

                Point3d p = pt + widthDir * xLocal + heightDir * yLocal;
                pl.Add(p);
            }

            pl.Add(pl[0]); // close the profile loop
            return pl;
        }

        /// <summary>
        /// Lofts a list of closed cross-section polylines into a mesh.
        /// 
        /// When <paramref name="isClosed"/> is true the path itself is a loop:
        ///   - An extra ring of quad faces connects the LAST section back to the
        ///     FIRST section, sealing the tube into a torus-like solid.
        ///   - No flat end-caps are added (the tube has no open ends).
        /// 
        /// When <paramref name="isClosed"/> is false the original behaviour is
        /// preserved: open barrel + two flat end-caps.
        /// </summary>
        private Mesh LoftSectionPolylinesToMesh(List<Polyline> sections, bool isClosed)
        {
            if (sections == null || sections.Count < 2)
                return null;

            int profileCount = sections.Count;
            int vertPerProfile = SectionVertexCount(sections[0]);
            if (vertPerProfile < 3)
                return null;

            for (int i = 1; i < profileCount; i++)
            {
                if (SectionVertexCount(sections[i]) != vertPerProfile)
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

            for (int i = 0; i < profileCount - 1; i++)
            {
                for (int j = 0; j < vertPerProfile; j++)
                {
                    int jNext = (j + 1) % vertPerProfile;
                    int i0 = idx[i, j];
                    int i1 = idx[i, jNext];
                    int i2 = idx[i + 1, jNext];
                    int i3 = idx[i + 1, j];

                    if (i0 == i1 || i1 == i2 || i2 == i3 || i3 == i0)
                        continue;

                    mesh.Faces.AddFace(i0, i1, i2, i3);
                }
            }

            if (isClosed)
            {
                // ----------------------------------------------------------------
                // Wrap-around ring: connect last section ? first section.
                // This closes the tube into a seamless ring (torus topology).
                // ----------------------------------------------------------------
                int last = profileCount - 1;
                for (int j = 0; j < vertPerProfile; j++)
                {
                    int jNext = (j + 1) % vertPerProfile;
                    int i0 = idx[last, j];
                    int i1 = idx[last, jNext];
                    int i2 = idx[0, jNext];
                    int i3 = idx[0, j];

                    if (i0 == i1 || i1 == i2 || i2 == i3 || i3 == i0)
                        continue;

                    mesh.Faces.AddFace(i0, i1, i2, i3);
                }
                // No caps â€” the tube is fully closed by the ring above.
            }
            else
            {
                // ----------------------------------------------------------------
                // Open path: add flat end-caps as before.
                // ----------------------------------------------------------------
                int startCenter = mesh.Vertices.Add(GetSectionCenter(sections[0], vertPerProfile));
                for (int j = 0; j < vertPerProfile; j++)
                {
                    int jNext = (j + 1) % vertPerProfile;
                    mesh.Faces.AddFace(startCenter, idx[0, jNext], idx[0, j]);
                }

                int last = profileCount - 1;
                int endCenter = mesh.Vertices.Add(GetSectionCenter(sections[last], vertPerProfile));
                for (int j = 0; j < vertPerProfile; j++)
                {
                    int jNext = (j + 1) % vertPerProfile;
                    mesh.Faces.AddFace(endCenter, idx[last, j], idx[last, jNext]);
                }
            }

            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CombineIdentical(true, true);
            mesh.Vertices.CullUnused();
            mesh.Weld(Math.PI);
            mesh.Normals.ComputeNormals();
            mesh.Compact();

            if (mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
                return null;

            return mesh;
        }

        private static int SectionVertexCount(Polyline section)
        {
            if (section == null || section.Count == 0)
                return 0;

            int count = section.Count;
            if (count > 1 && section[0].DistanceToSquared(section[count - 1]) <= RhinoMath.SqrtEpsilon)
                count--;

            return count;
        }

        private static Point3d GetSectionCenter(Polyline section, int count)
        {
            if (section == null || count <= 0)
                return Point3d.Origin;

            double x = 0.0;
            double y = 0.0;
            double z = 0.0;

            for (int i = 0; i < count; i++)
            {
                x += section[i].X;
                y += section[i].Y;
                z += section[i].Z;
            }

            double inv = 1.0 / count;
            return new Point3d(x * inv, y * inv, z * inv);
        }
    }
}
