// wsp_Fa06_Panel Joints Local SDF.cs
// WASPer_3DP — Subcategory: 2.1_Facades
//
// Generates reciprocal/hermaphroditic interlocking panel meshes using SDF field logic
// and table-based Marching Cubes extraction.
//
// IMPORTANT:
//   This version generates NEW PANEL MESHES.
//   It does NOT modify the original Breps using Rhino booleans.
//   It does NOT sample the original Breps as expensive full SDF fields.
//
// Current base geometry strategy:
//   Each input panel is represented by an aligned reference box.
//   Reference boxes are computed in a common facade-aligned frame.
//   All reference boxes are normalized to the thickest panel depth.
//   The final panel mesh is generated from:
//
//      panel_field = base_box
//      panel_field = panel_field + transfer_volumes
//      panel_field = panel_field - receiving_cutters
//
// In SDF terms, negative = inside:
//
//      Difference(A, B) = max(A, -B)
//      Union(A, B)      = min(A, B)
//
// Final panel field:
//
//      f = base_box
//      for each addition: f = min(f, addition)
//      for each cutter:   f = max(f, -cutter)
//
// Good:
//   - no Rhino BooleanDifference / BooleanUnion
//   - no Brep.ClosestPoint / IsPointInside per voxel
//   - aligned sampling grid in facade reference frame
//   - table-based Marching Cubes
//   - closed triangulated meshes if field/resolution is valid
//
// Limitation:
//   - base panel shape is box-based
//   - complex original trimmed Brep outlines are not preserved yet
//
// Future upgrade:
//   Replace base_box_field with an extruded 2D panel-footprint field from Fa03/Fa04.
//
// Inputs:
//   panels             — DataTree<Brep>
//   side_overlap       — transfer depth for side neighbours [model units]
//   side_joint_dir     — 0 = vertical side strips, 1 = horizontal side strips
//   top_bottom_overlap — transfer depth for top/bottom neighbours [model units]
//   wave_count         — reciprocal finger pairs per interface
//   clearance          — assembly gap around receiving pockets [model units]
//   res                — SDF/Marching Cubes resolution in model units. Default: 3.0
//
// Outputs:
//   panels_out         — final generated SDF panel meshes
//   base_panels        — base aligned normalized panel box meshes
//   transfer_to_a      — debug transfer meshes added to lower-index panel A
//   transfer_to_b      — debug transfer meshes added to higher-index panel B
//   cutters_from_a     — debug cutter meshes removed from lower-index panel A
//   cutters_from_b     — debug cutter meshes removed from higher-index panel B
//   finger_profiles    — interface strip profiles
//   adjacency          — detected neighbour report
//   info               — diagnostic report
//
// Author: Juan Diego Vargas
// Rewritten: 2026-05-10

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_1_Facades
{
    public sealed class wsp_Fa05_PanelJointsLocalSDF : GH_Component
    {
        private const string NAME = "wsp_Fa05_Panel Joints Local SDF";
        private const string NICK = "Fa06_Joints_SDF";
        private const string CAT = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "2.1_Facades";

        private const double EPS = 1e-9;
        // Per-panel and per-debug-job MC sample ceilings.  Raised from 3.5M/1M
        // because real facade panels at res ≈ 2–3 routinely exceeded the old
        // limits and got silently dropped from panels_out.  At these new caps,
        // a 250×3000×285 unit panel resolves down to ≈ res=2 before hitting the
        // ceiling, and the function still bails out cleanly above that.
        private const int MAX_PANEL_SAMPLES = 12000000;
        private const int MAX_DEBUG_SAMPLES = 3000000;

        private readonly string _versionTag;

        public wsp_Fa05_PanelJointsLocalSDF()
            : base(
                NAME,
                NICK,
                "Generates reciprocal/hermaphroditic interlocking panel meshes using SDF field logic and Marching Cubes.\n\n" +
                "Panels are reconstructed from aligned normalized base boxes plus/minus local joint fields.\n" +
                "No Rhino Brep booleans are used.",
                CAT,
                SUBCAT)
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.0";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("D5F3D29A-6A06-4C1D-BC52-3A2EBCF60614");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();

                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fa06_Wave Joints (SDF).png"))
                    {
                        return s != null ? new Bitmap(s) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter(
                "Panels",
                "panels",
                "Volumetric panel Breps. Accepts DataTrees; tree structure is preserved.",
                GH_ParamAccess.tree);

            pManager.AddNumberParameter(
                "Side Overlap",
                "side_overlap",
                "Transfer depth for side neighbours [model units]. Set to 0 to disable side joints.",
                GH_ParamAccess.item,
                20.0);

            pManager.AddIntegerParameter(
                "Side Joint Direction",
                "side_joint_dir",
                "Side-neighbour strip direction. 0 = vertical strips (default), 1 = horizontal strips.",
                GH_ParamAccess.item,
                0);

            pManager.AddNumberParameter(
                "Top/Bottom Overlap",
                "top_bottom_overlap",
                "Transfer depth for top/bottom neighbours [model units]. Set to 0 to disable vertical stacking joints.",
                GH_ParamAccess.item,
                20.0);

            pManager.AddIntegerParameter(
                "Wave Count",
                "wave_count",
                "Number of reciprocal finger pairs per interface. Total strips = 2 × wave_count.",
                GH_ParamAccess.item,
                2);

            pManager.AddNumberParameter(
                "Clearance",
                "clearance",
                "Assembly gap around receiving pockets [model units].",
                GH_ParamAccess.item,
                0.3);

            pManager.AddNumberParameter(
                "Resolution",
                "res",
                "SDF/Marching Cubes resolution in Rhino/model units. Default: 3.0.",
                GH_ParamAccess.item,
                3.0);

            for (int i = 1; i <= 6; i++)
            {
                pManager[i].Optional = true;
            }
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "Panels Out",
                "panels_out",
                "Final generated panel meshes with reciprocal interlocking joints.",
                GH_ParamAccess.tree);

            pManager.AddGenericParameter(
                "field", "F",
                "WASPer signed distance field tree (one field per panel).\n" +
                "Negative inside each panel solid, positive outside.\n" +
                "Wire into 2.3_Fields_3D tools for booleans, blending, and iso-surface extraction.",
                GH_ParamAccess.tree);

            pManager.AddMeshParameter(
                "Base Panels",
                "base_panels",
                "Base aligned normalized panel-box meshes before joint operations.",
                GH_ParamAccess.tree);

            pManager.AddMeshParameter(
                "Transfer To A",
                "transfer_to_a",
                "Debug transfer meshes added to lower-index panel A.",
                GH_ParamAccess.tree);

            pManager.AddMeshParameter(
                "Transfer To B",
                "transfer_to_b",
                "Debug transfer meshes added to higher-index panel B.",
                GH_ParamAccess.tree);

            pManager.AddMeshParameter(
                "Cutters From A",
                "cutters_from_a",
                "Debug cutter meshes removed from lower-index panel A.",
                GH_ParamAccess.tree);

            pManager.AddMeshParameter(
                "Cutters From B",
                "cutters_from_b",
                "Debug cutter meshes removed from higher-index panel B.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "Finger Profiles",
                "finger_profiles",
                "Rectangular strip profiles on each detected interface.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "Adjacency",
                "adjacency",
                "Detected neighbour pairs and interface classification.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "Info",
                "info",
                "Diagnostic report.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var swTotal = Stopwatch.StartNew();

            GH_Structure<GH_Brep> panelTree;

            if (!DA.GetDataTree(0, out panelTree) || panelTree == null || panelTree.IsEmpty)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Panels tree is empty.");
                return;
            }

            double sideOverlap = 20.0;
            int sideJointDir = 0;
            double topBottomOverlap = 20.0;
            int waveCount = 2;
            double clearance = 0.3;
            double res = 3.0;

            DA.GetData(1, ref sideOverlap);
            DA.GetData(2, ref sideJointDir);
            DA.GetData(3, ref topBottomOverlap);
            DA.GetData(4, ref waveCount);
            DA.GetData(5, ref clearance);
            DA.GetData(6, ref res);

            sideOverlap = Math.Max(0.0, sideOverlap);
            topBottomOverlap = Math.Max(0.0, topBottomOverlap);
            waveCount = Math.Max(1, waveCount);
            clearance = Math.Max(0.0, clearance);
            res = Math.Max(0.001, res);

            // Integer-resolution nudge.  When res is an integer, MC sample points
            // tend to land exactly on SDF zero crossings (panel walls, strip
            // boundaries, interface planes), producing degenerate cells and the
            // occasional missing/duplicate triangle.  Same mitigation that In11/
            // In12 use: bias integer res values by -0.01 so samples are slightly
            // offset from the field's zero set.  Effect on mesh density is
            // negligible (~0.5% at res=2, less at higher res).
            if (Math.Abs(res - Math.Round(res)) < 1e-6)
            {
                res -= 0.01;
            }

            sideJointDir = sideJointDir == 1 ? 1 : 0;

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.01;
            int threads = Math.Max(1, Environment.ProcessorCount - 1);

            var panels = FlattenPanelTree(panelTree);
            int panelCount = panels.Count;

            if (panelCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Panels tree contains no valid panels.");
                return;
            }

            var breps = panels.Select(p => p.Brep).Where(b => b != null).ToList();

            ReferenceFrame refFrame = BuildFacadeReferenceFrame(breps);
            AssignAlignedBoxes(panels, refFrame);

            bool keepBackMin;
            double backMinSpread;
            double backMaxSpread;
            double referenceDepth = NormalizePanelReferenceBoxes(
                panels,
                refFrame,
                tol,
                out keepBackMin,
                out backMinSpread,
                out backMaxSpread);

            var panelLateralAdditions = new List<SdfBox>[panelCount];
            var panelLateralPocketCutters = new List<SdfBox>[panelCount];   // pockets cut by lateral (side) joints
            var panelLateralZoneCutters = new List<SdfBox>[panelCount];
            var panelLateralProtectZones = new List<SdfBox>[panelCount];
            var panelTopBottomAdditions = new List<SdfBox>[panelCount];
            var panelTopBottomPocketCutters = new List<SdfBox>[panelCount];   // pockets cut by topBottom (h) joints
            var panelTopBottomZoneCutters = new List<SdfBox>[panelCount];
            var panelTopBottomProtectZones = new List<SdfBox>[panelCount];

            for (int i = 0; i < panelCount; i++)
            {
                panelLateralAdditions[i] = new List<SdfBox>();
                panelLateralPocketCutters[i] = new List<SdfBox>();
                panelLateralZoneCutters[i] = new List<SdfBox>();
                panelLateralProtectZones[i] = new List<SdfBox>();
                panelTopBottomAdditions[i] = new List<SdfBox>();
                panelTopBottomPocketCutters[i] = new List<SdfBox>();
                panelTopBottomZoneCutters[i] = new List<SdfBox>();
                panelTopBottomProtectZones[i] = new List<SdfBox>();
            }

            var swAdj = Stopwatch.StartNew();
            var interfaces = DetectCentroidNeighbours(panels, tol);
            swAdj.Stop();

            // ── Per-panel corner clamp data ────────────────────────────────────────
            // For each panel, record the world-space face coordinate of its topBottom
            // interfaces (Y axis) and its lateral interfaces (X axis).  These are used
            // to clamp uMin/uMax of strip extents so no addition ever enters the
            // neighbour joint zone — fixing the corner protrusion without SDF cross-trim.
            var panelTopFaceY = new double[panelCount];   // top topBottom interface Y
            var panelBotFaceY = new double[panelCount];   // bottom topBottom interface Y
            var panelRightFaceX = new double[panelCount];   // right lateral interface X
            var panelLeftFaceX = new double[panelCount];   // left lateral interface X

            for (int ci = 0; ci < panelCount; ci++)
            {
                panelTopFaceY[ci] = double.PositiveInfinity;
                panelBotFaceY[ci] = double.NegativeInfinity;
                panelRightFaceX[ci] = double.PositiveInfinity;
                panelLeftFaceX[ci] = double.NegativeInfinity;
            }

            foreach (InterfaceInfo fi in interfaces)
            {
                if (fi.IsVerticalStack)
                {
                    double faceY = AxisValue(fi.Center, 1);
                    // Identify which panel is lower (top face = faceY) and which is higher (bottom face = faceY)
                    int pLow = panels[fi.PanelA].Center.Y <= panels[fi.PanelB].Center.Y ? fi.PanelA : fi.PanelB;
                    int pHigh = pLow == fi.PanelA ? fi.PanelB : fi.PanelA;
                    if (faceY < panelTopFaceY[pLow]) panelTopFaceY[pLow] = faceY;
                    if (faceY > panelBotFaceY[pHigh]) panelBotFaceY[pHigh] = faceY;
                }
                else
                {
                    double faceX = AxisValue(fi.Center, 0);
                    int pLeft = panels[fi.PanelA].Center.X <= panels[fi.PanelB].Center.X ? fi.PanelA : fi.PanelB;
                    int pRight = pLeft == fi.PanelA ? fi.PanelB : fi.PanelA;
                    if (faceX < panelRightFaceX[pLeft]) panelRightFaceX[pLeft] = faceX;
                    if (faceX > panelLeftFaceX[pRight]) panelLeftFaceX[pRight] = faceX;
                }
            }
            // ──────────────────────────────────────────────────────────────────────

            var debugJobs = new List<JointJob>();
            var profileTree = new GH_Structure<GH_Curve>();
            var adjacency = new List<string>();

            int skippedZeroOverlap = 0;
            int skippedBadBounds = 0;
            int invalidFields = 0;

            var swJobs = Stopwatch.StartNew();

            for (int i = 0; i < interfaces.Count; i++)
            {
                InterfaceInfo iface = interfaces[i];

                double overlap = iface.IsVerticalStack ? topBottomOverlap : sideOverlap;

                if (overlap <= 0.0)
                {
                    skippedZeroOverlap++;
                    continue;
                }

                Plane planeWorld = BuildInterfacePlaneWorld(iface, refFrame);

                GetInterfaceBounds(
                    iface,
                    out double uMin,
                    out double uMax,
                    out double vMin,
                    out double vMax);

                double rawUMin = uMin;
                double rawUMax = uMax;
                double rawVMin = vMin;
                double rawVMax = vMax;

                // ── Corner extension ───────────────────────────────────────────────
                // EXTEND uMin/uMax so additions reach into the perpendicular joint
                // corner zone.  We extend an edge only when a perpendicular seam is
                // actually positioned at that edge of the overlap region — checked by
                // comparing the seam world coordinate against the world position of
                // uMax / uMin.  This correctly handles border panels (no seam → no
                // extension) and irregular layouts (seam not at this edge → no ext).
                //
                // Proximity tolerance: generous enough to absorb floating-point drift
                // from centroid detection, tight enough to reject unrelated seams.
                double cornerTol = Math.Max(tol * 50.0, 1.0);

                if (!iface.IsVerticalStack && topBottomOverlap > 0.0)
                {
                    double ext = topBottomOverlap * 0.5;
                    double ifaceCY = AxisValue(iface.Center, 1);
                    double worldUMax = ifaceCY + uMax;   // world Y of overlap top edge
                    double worldUMin = ifaceCY + uMin;   // world Y of overlap bottom edge
                    bool extendUp = false;
                    bool extendDn = false;

                    foreach (int pid in new[] { iface.PanelA, iface.PanelB })
                    {
                        // panelTopFaceY[pid] = Y of the topBottom seam above this panel.
                        // Extend UP only when that seam sits at the top edge of the overlap.
                        if (!double.IsPositiveInfinity(panelTopFaceY[pid]) &&
                            Math.Abs(panelTopFaceY[pid] - worldUMax) <= cornerTol)
                            extendUp = true;

                        // panelBotFaceY[pid] = Y of the topBottom seam below this panel.
                        // Extend DOWN only when that seam sits at the bottom edge.
                        if (!double.IsNegativeInfinity(panelBotFaceY[pid]) &&
                            Math.Abs(panelBotFaceY[pid] - worldUMin) <= cornerTol)
                            extendDn = true;
                    }
                    if (extendUp) uMax += ext;
                    if (extendDn) uMin -= ext;
                }
                else if (iface.IsVerticalStack && sideOverlap > 0.0)
                {
                    double ext = sideOverlap * 0.5;
                    double ifaceCX = AxisValue(iface.Center, 0);
                    double worldUMax = ifaceCX + uMax;   // world X of overlap right edge
                    double worldUMin = ifaceCX + uMin;   // world X of overlap left edge
                    bool extendRt = false;
                    bool extendLt = false;

                    foreach (int pid in new[] { iface.PanelA, iface.PanelB })
                    {
                        // panelRightFaceX[pid] = X of the lateral seam to the right.
                        // Extend RIGHT only when that seam sits at the right edge.
                        if (!double.IsPositiveInfinity(panelRightFaceX[pid]) &&
                            Math.Abs(panelRightFaceX[pid] - worldUMax) <= cornerTol)
                            extendRt = true;

                        // panelLeftFaceX[pid] = X of the lateral seam to the left.
                        // Extend LEFT only when that seam sits at the left edge.
                        if (!double.IsNegativeInfinity(panelLeftFaceX[pid]) &&
                            Math.Abs(panelLeftFaceX[pid] - worldUMin) <= cornerTol)
                            extendLt = true;
                    }
                    if (extendRt) uMax += ext;
                    if (extendLt) uMin -= ext;
                }
                // ──────────────────────────────────────────────────────────────────

                // Additions and cutters are generated from the true shared face.
                // The old corner-growth pass above is intentionally neutralized
                // because it can create visible ledges on border panels.
                uMin = rawUMin;
                uMax = rawUMax;
                vMin = rawVMin;
                vMax = rawVMax;

                if (!AreValidBounds(uMin, uMax, vMin, vMax, tol))
                {
                    skippedBadBounds++;
                    continue;
                }

                // Symmetric interface protect zones removed: they sat on BOTH panels
                // and spanned both sides of the interface, so they blocked valid
                // cuts and let invalid ones through.  Ownership is now enforced
                // inside EvaluatePanelField via per-panel territory masks built
                // from each panel's own base + same-family additions, while the
                // OTHER family's addition interiors veto the cut.  No symmetric
                // shared protect SDFs are emitted here anymore.

                // ── Strip inset from interface boundary ────────────────────────
                // The interface U/V range comes from the OverlapBox of the two
                // panels.  For panels aligned with their neighbours, that range
                // = the panel's full face in U and V — which means strips
                // (and therefore pockets and fingers) would reach the panel's
                // outer side edges.  Consequences:
                //   • In the pocket Z range, the cut sets material positive on
                //     both sides of the panel's side face → MC can't draw the
                //     side face there → it looks like the pocket "carves" the
                //     adjacent panel through the seam.
                //   • Lateral fingers and top/bottom fingers reach the panel's
                //     corners and overlap when both joint families are active.
                // The cure is to inset the strip layout by a margin so pockets
                // have walls and fingers don't reach corners — matching the
                // hand-made interlocking geometry.  Both the cutter and the
                // addition derive their U/V from the strip, so they inset
                // together and the mating between panels still aligns.
                //
                // Margin is res-aware so it never collapses into one MC cell.
                // It is clamped to at most ~45% of the span on each side, so
                // thin/short interfaces don't lose their strips entirely.
                double stripInset = Math.Max(res * 2.0, clearance + tol);
                double uSpan = uMax - uMin;
                double vSpan = vMax - vMin;
                double uInset = Math.Min(stripInset, uSpan * 0.45);
                double vInset = Math.Min(stripInset, vSpan * 0.45);

                double stripUMin = uMin + uInset;
                double stripUMax = uMax - uInset;
                double stripVMin = vMin + vInset;
                double stripVMax = vMax - vInset;

                if (!AreValidBounds(stripUMin, stripUMax, stripVMin, stripVMax, tol))
                {
                    skippedBadBounds++;
                    continue;
                }

                int stripCount = 2 * waveCount;
                List<StripBounds> strips = BuildStrips(iface.IsVerticalStack, sideJointDir, stripCount, stripUMin, stripUMax, stripVMin, stripVMax, tol);

                adjacency.Add(
                    $"joint_{i} | A=panel_{iface.PanelA} | B=panel_{iface.PanelB} | axis={iface.AxisName} | vertical_stack={iface.IsVerticalStack} | strips={strips.Count} | overlap={overlap:0.###}");

                for (int k = 0; k < strips.Count; k++)
                {
                    StripBounds strip = strips[k];
                    bool stripToA = (k % 2 == 0);

                    // penetration = per-side depth. The user-supplied overlap is the
                    // total visible joint zone (both panels combined), so each panel
                    // contributes exactly half. Without this halving the visible zone
                    // reads as 2× the input value.
                    double penetration = overlap * 0.5;
                    double eps = Math.Max(tol * 10.0, res * 0.25);
                    double anchor = Math.Max(res * 1.5, Math.Min(penetration * 0.35, res * 3.0));
                    Interval normalU = new Interval(strip.U0 - clearance, strip.U1 + clearance);

                    // Cutters are extended slightly along U so the cut fully covers
                    // its own strip's added material at MC cell boundaries (avoids
                    // leftover tabs at strip ends).  The extension is intentionally
                    // res-aware and SMALL — large extensions cross into:
                    //   (a) the adjacent strip's finger material on the SAME panel
                    //       (which is a same-family addition, not vetoed by the
                    //        owner-mask), eroding the finger anchor;
                    //   (b) the panel's own side edge, which combined with ownerPad
                    //       can erase a slice of the panel's side face inside the
                    //       cut Z range.
                    // A few cells of res is plenty for cell-boundary cleanup.
                    bool extendCutterAlongU = (!iface.IsVerticalStack && sideJointDir == 0) || iface.IsVerticalStack;
                    double cutterExtension = Math.Max(
                        res * 4.0,
                        Math.Max(clearance * 4.0, tol * 20.0));
                    Interval cutterU = extendCutterAlongU
                        ? ExpandIntervalFromCenter(normalU, cutterExtension)
                        : normalU;

                    List<SdfBox>[] targetAdditions = iface.IsVerticalStack ? panelTopBottomAdditions : panelLateralAdditions;
                    List<SdfBox>[] targetPocketCutters = iface.IsVerticalStack ? panelTopBottomPocketCutters : panelLateralPocketCutters;
                    List<SdfBox>[] targetCleanupCutters = iface.IsVerticalStack ? panelTopBottomZoneCutters : panelLateralZoneCutters;

                    if (stripToA)
                    {
                        SdfBox transferToA = new SdfBox(
                            planeWorld,
                            new Interval(strip.U0, strip.U1),
                            new Interval(strip.V0, strip.V1),
                            new Interval(-anchor, penetration));

                        SdfBox cutterFromB = new SdfBox(
                            planeWorld,
                            cutterU,
                            new Interval(strip.V0 - clearance, strip.V1 + clearance),
                            new Interval(-eps, penetration + clearance));

                        SdfBox cleanupCutterFromB = new SdfBox(
                            planeWorld,
                            cutterU,
                            new Interval(strip.V0 - clearance, strip.V1 + clearance),
                            new Interval(-eps, penetration + clearance));

                        // Cutters intentionally NOT clamped to the receiving panel
                        // base box anymore — the old clamp prevented cutters from
                        // reaching into added joint material that lives outside the
                        // base.  Ownership is now enforced by territory masks per
                        // panel inside EvaluatePanelField, so an oversized cutter
                        // is safe.

                        if (transferToA.IsValid)
                        {
                            targetAdditions[iface.PanelA].Add(transferToA);
                            debugJobs.Add(new JointJob
                            {
                                Type = JointJobType.TransferToA,
                                InterfaceIndex = i,
                                StripIndex = k,
                                Field = transferToA
                            });
                        }
                        else
                        {
                            invalidFields++;
                        }

                        if (cutterFromB.IsValid)
                        {
                            targetPocketCutters[iface.PanelB].Add(cutterFromB);
                            if (cleanupCutterFromB.IsValid)
                            {
                                targetCleanupCutters[iface.PanelB].Add(cleanupCutterFromB);
                            }
                            debugJobs.Add(new JointJob
                            {
                                Type = JointJobType.CutterFromB,
                                InterfaceIndex = i,
                                StripIndex = k,
                                Field = cleanupCutterFromB.IsValid ? cleanupCutterFromB : cutterFromB
                            });
                        }
                        else
                        {
                            invalidFields++;
                        }
                    }
                    else
                    {
                        SdfBox transferToB = new SdfBox(
                            planeWorld,
                            new Interval(strip.U0, strip.U1),
                            new Interval(strip.V0, strip.V1),
                            new Interval(-penetration, anchor));

                        SdfBox cutterFromA = new SdfBox(
                            planeWorld,
                            cutterU,
                            new Interval(strip.V0 - clearance, strip.V1 + clearance),
                            new Interval(-penetration - clearance, eps));

                        SdfBox cleanupCutterFromA = new SdfBox(
                            planeWorld,
                            cutterU,
                            new Interval(strip.V0 - clearance, strip.V1 + clearance),
                            new Interval(-penetration - clearance, eps));

                        // Cutters intentionally NOT clamped to the receiving panel
                        // base box anymore — see the symmetric note in the stripToA
                        // branch.  Ownership is enforced by territory masks per
                        // panel inside EvaluatePanelField.

                        if (transferToB.IsValid)
                        {
                            targetAdditions[iface.PanelB].Add(transferToB);
                            debugJobs.Add(new JointJob
                            {
                                Type = JointJobType.TransferToB,
                                InterfaceIndex = i,
                                StripIndex = k,
                                Field = transferToB
                            });
                        }
                        else
                        {
                            invalidFields++;
                        }

                        if (cutterFromA.IsValid)
                        {
                            targetPocketCutters[iface.PanelA].Add(cutterFromA);
                            if (cleanupCutterFromA.IsValid)
                            {
                                targetCleanupCutters[iface.PanelA].Add(cleanupCutterFromA);
                            }
                            debugJobs.Add(new JointJob
                            {
                                Type = JointJobType.CutterFromA,
                                InterfaceIndex = i,
                                StripIndex = k,
                                Field = cleanupCutterFromA.IsValid ? cleanupCutterFromA : cutterFromA
                            });
                        }
                        else
                        {
                            invalidFields++;
                        }
                    }

                    Curve profile = CreateProfileCurve(planeWorld, strip);

                    if (profile != null)
                    {
                        profileTree.Append(new GH_Curve(profile), new GH_Path(i, k));
                    }
                }
            }

            swJobs.Stop();

            var finalPanelMeshes = new Mesh[panelCount];
            var basePanelMeshes = new Mesh[panelCount];
            var panelWarnings = new string[panelCount];

            int panelMeshOk = 0;
            int panelMeshFail = 0;
            int panelTooHeavy = 0;

            var swPanelSdf = Stopwatch.StartNew();

            Parallel.For(
                0,
                panelCount,
                new ParallelOptions { MaxDegreeOfParallelism = threads },
                i =>
                {
                    if (panels[i].BaseField == null || !panels[i].BaseField.IsValid)
                    {
                        panelWarnings[i] = "Invalid base SDF box.";
                        Interlocked.Increment(ref panelMeshFail);
                        return;
                    }

                    Mesh baseMesh = ExtractSingleFieldMeshMC(
                        panels[i].BaseField,
                        refFrame,
                        res,
                        MAX_DEBUG_SAMPLES,
                        out string baseWarning);

                    basePanelMeshes[i] = baseMesh;

                    Mesh finalMesh = ExtractPanelFieldMeshMC(
                        panels[i].BaseField,
                        panelLateralAdditions[i],
                        panelLateralPocketCutters[i],
                        panelLateralZoneCutters[i],
                        panelLateralProtectZones[i],
                        panelTopBottomAdditions[i],
                        panelTopBottomPocketCutters[i],
                        panelTopBottomZoneCutters[i],
                        panelTopBottomProtectZones[i],
                        refFrame,
                        res,
                        MAX_PANEL_SAMPLES,
                        out string warning);

                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        panelWarnings[i] = warning;

                        if (warning.Contains("too many"))
                        {
                            Interlocked.Increment(ref panelTooHeavy);
                        }
                    }

                    if (finalMesh != null && finalMesh.Vertices.Count > 0 && finalMesh.Faces.Count > 0)
                    {
                        finalPanelMeshes[i] = finalMesh;
                        Interlocked.Increment(ref panelMeshOk);
                    }
                    else
                    {
                        Interlocked.Increment(ref panelMeshFail);
                    }
                });

            swPanelSdf.Stop();

            var debugMeshes = new Mesh[debugJobs.Count];
            var debugWarnings = new string[debugJobs.Count];

            int debugOk = 0;
            int debugFail = 0;

            var swDebugSdf = Stopwatch.StartNew();

            Parallel.For(
                0,
                debugJobs.Count,
                new ParallelOptions { MaxDegreeOfParallelism = threads },
                j =>
                {
                    JointJob job = debugJobs[j];

                    if (job.Field == null || !job.Field.IsValid)
                    {
                        debugWarnings[j] = "Invalid debug SDF box.";
                        Interlocked.Increment(ref debugFail);
                        return;
                    }

                    Mesh m = ExtractSingleFieldMeshMC(
                        job.Field,
                        refFrame,
                        res,
                        MAX_DEBUG_SAMPLES,
                        out string warning);

                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        debugWarnings[j] = warning;
                    }

                    if (m != null && m.Vertices.Count > 0 && m.Faces.Count > 0)
                    {
                        debugMeshes[j] = m;
                        Interlocked.Increment(ref debugOk);
                    }
                    else
                    {
                        Interlocked.Increment(ref debugFail);
                    }
                });

            swDebugSdf.Stop();

            var panelsOutTree = RebuildMeshTree(panels, finalPanelMeshes);
            var basePanelsTree = RebuildMeshTree(panels, basePanelMeshes);

            var transferToATree = new GH_Structure<GH_Mesh>();
            var transferToBTree = new GH_Structure<GH_Mesh>();
            var cuttersFromATree = new GH_Structure<GH_Mesh>();
            var cuttersFromBTree = new GH_Structure<GH_Mesh>();

            for (int j = 0; j < debugJobs.Count; j++)
            {
                Mesh m = debugMeshes[j];

                if (m == null)
                {
                    if (!string.IsNullOrWhiteSpace(debugWarnings[j]))
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Debug job {j}: {debugWarnings[j]}");
                    }

                    continue;
                }

                GH_Path path = new GH_Path(debugJobs[j].InterfaceIndex, debugJobs[j].StripIndex);

                switch (debugJobs[j].Type)
                {
                    case JointJobType.TransferToA:
                        transferToATree.Append(new GH_Mesh(m), path);
                        break;

                    case JointJobType.TransferToB:
                        transferToBTree.Append(new GH_Mesh(m), path);
                        break;

                    case JointJobType.CutterFromA:
                        cuttersFromATree.Append(new GH_Mesh(m), path);
                        break;

                    case JointJobType.CutterFromB:
                        cuttersFromBTree.Append(new GH_Mesh(m), path);
                        break;
                }
            }

            for (int i = 0; i < panelWarnings.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(panelWarnings[i]))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Panel {i}: {panelWarnings[i]}");
                }
            }

            swTotal.Stop();

            string info = BuildInfo(
                panelCount,
                interfaces.Count,
                debugJobs.Count,
                sideOverlap,
                topBottomOverlap,
                waveCount,
                clearance,
                res,
                sideJointDir,
                referenceDepth,
                keepBackMin,
                backMinSpread,
                backMaxSpread,
                refFrame,
                threads,
                panelMeshOk,
                panelMeshFail,
                panelTooHeavy,
                debugOk,
                debugFail,
                skippedZeroOverlap,
                skippedBadBounds,
                invalidFields,
                swAdj.ElapsedMilliseconds,
                swJobs.ElapsedMilliseconds,
                swPanelSdf.ElapsedMilliseconds,
                swDebugSdf.ElapsedMilliseconds,
                swTotal.ElapsedMilliseconds);

            DA.SetDataTree(0, panelsOutTree);
            // Build field tree (one WasperField per panel, matching panelsOutTree paths)
            var fieldsOutTree = new GH_Structure<WasperFieldGoo>();
            for (int i = 0; i < panels.Count; i++)
            {
                Mesh pm = finalPanelMeshes[i];
                WasperFieldGoo fgoo = pm != null
                    ? new WasperFieldGoo(WasperField.FromMesh(pm))
                    : null;
                fieldsOutTree.Append(fgoo, panels[i].Path);
            }
            DA.SetDataTree(1, fieldsOutTree);

            DA.SetDataTree(2, basePanelsTree);
            DA.SetDataTree(3, transferToATree);
            DA.SetDataTree(4, transferToBTree);
            DA.SetDataTree(5, cuttersFromATree);
            DA.SetDataTree(6, cuttersFromBTree);
            DA.SetDataTree(7, profileTree);
            DA.SetDataList(8, adjacency);
            DA.SetData(9, info);

            Message = $"{_versionTag} | panels:{panelMeshOk}/{panelCount}";
        }

        private enum JointJobType
        {
            TransferToA,
            TransferToB,
            CutterFromA,
            CutterFromB
        }


        private sealed class PanelItem
        {
            public Brep Brep;
            public GH_Path Path;
            public int BranchIndex;
            public int FlatIndex;

            public BoundingBox WorldBox;
            public BoundingBox Box;
            public BoundingBox RefBox;

            public Point3d Center;
            public Point3d WorldCenter;

            public Vector3d Size;
            public Vector3d RefSize;

            public int DepthAxis;
            public double Depth;

            public SdfBox BaseField;
        }

        private sealed class InterfaceInfo
        {
            public int PanelA;
            public int PanelB;
            public Point3d Center;
            public Vector3d NormalAToB;
            public BoundingBox OverlapBox;
            public int Axis;
            public bool IsVerticalStack;

            public string AxisName
            {
                get
                {
                    if (Axis == 0) return "X";
                    if (Axis == 1) return "Y";
                    return "Z";
                }
            }
        }

        private sealed class JointJob
        {
            public JointJobType Type;
            public int InterfaceIndex;
            public int StripIndex;
            public SdfBox Field;
        }

        private struct StripBounds
        {
            public double U0;
            public double U1;
            public double V0;
            public double V1;

            public StripBounds(double u0, double u1, double v0, double v1)
            {
                U0 = u0;
                U1 = u1;
                V0 = v0;
                V1 = v1;
            }
        }


        private static List<PanelItem> FlattenPanelTree(GH_Structure<GH_Brep> tree)
        {
            var panels = new List<PanelItem>();
            int flat = 0;

            foreach (GH_Path path in tree.Paths)
            {
                IList branch = tree.get_Branch(path);

                for (int i = 0; i < branch.Count; i++)
                {
                    GH_Brep ghBrep = branch[i] as GH_Brep;
                    Brep b = ghBrep?.Value;

                    if (b == null)
                    {
                        continue;
                    }

                    BoundingBox worldBox = b.GetBoundingBox(true);

                    if (!worldBox.IsValid)
                    {
                        continue;
                    }

                    panels.Add(new PanelItem
                    {
                        Brep = b,
                        Path = path,
                        BranchIndex = i,
                        FlatIndex = flat,
                        WorldBox = worldBox,
                        Box = BoundingBox.Empty,
                        RefBox = BoundingBox.Empty,
                        WorldCenter = worldBox.Center,
                        Center = Point3d.Unset,
                        Size = Vector3d.Zero,
                        RefSize = Vector3d.Zero,
                        DepthAxis = 2,
                        Depth = 0.0,
                        BaseField = null
                    });

                    flat++;
                }
            }

            return panels;
        }

        private static GH_Structure<GH_Mesh> RebuildMeshTree(List<PanelItem> panels, Mesh[] meshes)
        {
            var outTree = new GH_Structure<GH_Mesh>();

            for (int i = 0; i < panels.Count; i++)
            {
                if (meshes[i] != null)
                {
                    outTree.Append(new GH_Mesh(meshes[i]), panels[i].Path);
                }
                else
                {
                    outTree.Append(null, panels[i].Path);
                }
            }

            return outTree;
        }

        private static ReferenceFrame BuildFacadeReferenceFrame(List<Brep> breps)
        {
            Vector3d avgNormal = Vector3d.Zero;
            Point3d avgCenter = Point3d.Origin;
            int centerCount = 0;

            for (int i = 0; i < breps.Count; i++)
            {
                Brep brep = breps[i];

                if (brep == null)
                {
                    continue;
                }

                BoundingBox bb = brep.GetBoundingBox(true);

                if (bb.IsValid)
                {
                    avgCenter += (Vector3d)bb.Center;
                    centerCount++;
                }

                Vector3d n;

                if (TryGetLargestFaceNormal(brep, out n))
                {
                    if (avgNormal.Length > EPS && avgNormal * n < 0.0)
                    {
                        n = -n;
                    }

                    avgNormal += n;
                }
            }

            if (centerCount > 0)
            {
                avgCenter = new Point3d(
                    avgCenter.X / centerCount,
                    avgCenter.Y / centerCount,
                    avgCenter.Z / centerCount);
            }
            else
            {
                avgCenter = Point3d.Origin;
            }

            if (!avgNormal.Unitize())
            {
                avgNormal = Vector3d.ZAxis;
            }

            Vector3d depthAxis = avgNormal;

            Vector3d verticalAxis = Vector3d.ZAxis - (Vector3d.ZAxis * depthAxis) * depthAxis;

            if (!verticalAxis.Unitize())
            {
                verticalAxis = Vector3d.YAxis - (Vector3d.YAxis * depthAxis) * depthAxis;

                if (!verticalAxis.Unitize())
                {
                    verticalAxis = Vector3d.YAxis;
                }
            }

            Vector3d horizontalAxis = Vector3d.CrossProduct(verticalAxis, depthAxis);

            if (!horizontalAxis.Unitize())
            {
                horizontalAxis = Vector3d.XAxis;
            }

            verticalAxis = Vector3d.CrossProduct(depthAxis, horizontalAxis);
            verticalAxis.Unitize();

            Plane refPlane = new Plane(avgCenter, horizontalAxis, verticalAxis);

            ReferenceFrame frame = new ReferenceFrame();
            frame.Plane = refPlane;
            frame.WorldToRef = Transform.PlaneToPlane(refPlane, Plane.WorldXY);
            frame.RefToWorld = Transform.PlaneToPlane(Plane.WorldXY, refPlane);

            return frame;
        }

        private static bool TryGetLargestFaceNormal(Brep brep, out Vector3d normal)
        {
            normal = Vector3d.Unset;

            if (brep == null || brep.Faces.Count == 0)
            {
                return false;
            }

            double bestArea = double.MinValue;
            Vector3d bestNormal = Vector3d.Unset;

            for (int i = 0; i < brep.Faces.Count; i++)
            {
                BrepFace face = brep.Faces[i];

                double area = 0.0;

                try
                {
                    Brep single = face.DuplicateFace(false);

                    if (single != null)
                    {
                        AreaMassProperties amp = AreaMassProperties.Compute(single);

                        if (amp != null)
                        {
                            area = amp.Area;
                        }
                    }
                }
                catch
                {
                    area = 0.0;
                }

                if (area <= bestArea)
                {
                    continue;
                }

                Vector3d n = face.NormalAt(face.Domain(0).Mid, face.Domain(1).Mid);

                if (!n.Unitize())
                {
                    continue;
                }

                bestArea = area;
                bestNormal = n;
            }

            if (!bestNormal.IsValid || bestNormal.Length <= EPS)
            {
                return false;
            }

            normal = bestNormal;
            return true;
        }

        private static void AssignAlignedBoxes(List<PanelItem> panels, ReferenceFrame frame)
        {
            for (int i = 0; i < panels.Count; i++)
            {
                PanelItem p = panels[i];

                BoundingBox refBox = GetAlignedBoundingBox(p.Brep, frame.WorldToRef);

                if (!refBox.IsValid)
                {
                    continue;
                }

                Vector3d size = refBox.Max - refBox.Min;

                p.Box = refBox;
                p.RefBox = refBox;
                p.Center = refBox.Center;
                p.Size = size;
                p.RefSize = size;
                p.DepthAxis = 2;
                p.Depth = AxisSize(size, 2);
            }
        }

        private static BoundingBox GetAlignedBoundingBox(Brep brep, Transform worldToRef)
        {
            if (brep == null)
            {
                return BoundingBox.Empty;
            }

            Brep copy = brep.DuplicateBrep();

            if (copy == null)
            {
                return BoundingBox.Empty;
            }

            copy.Transform(worldToRef);

            return copy.GetBoundingBox(true);
        }

        private static double NormalizePanelReferenceBoxes(
            List<PanelItem> panels,
            ReferenceFrame frame,
            double tol,
            out bool keepBackMin,
            out double minSideSpread,
            out double maxSideSpread)
        {
            keepBackMin = true;
            minSideSpread = 0.0;
            maxSideSpread = 0.0;

            if (panels == null || panels.Count == 0)
            {
                return 0.0;
            }

            double maxDepth = 0.0;

            for (int i = 0; i < panels.Count; i++)
            {
                PanelItem p = panels[i];

                p.DepthAxis = 2;
                p.Depth = AxisSize(p.Size, p.DepthAxis);

                if (p.Depth > maxDepth)
                {
                    maxDepth = p.Depth;
                }
            }

            keepBackMin = InferBackAnchorSide(panels, frame, tol, out minSideSpread, out maxSideSpread);

            if (maxDepth <= EPS)
            {
                for (int i = 0; i < panels.Count; i++)
                {
                    panels[i].RefBox = panels[i].Box;
                    panels[i].RefSize = panels[i].Size;
                    panels[i].BaseField = BuildBaseFieldFromRefBox(panels[i].RefBox, frame);
                }

                return 0.0;
            }

            for (int i = 0; i < panels.Count; i++)
            {
                PanelItem p = panels[i];

                p.RefBox = ExpandBoxAxisToDepthAnchored(p.Box, 2, maxDepth, keepBackMin);
                p.RefSize = p.RefBox.Max - p.RefBox.Min;
                p.BaseField = BuildBaseFieldFromRefBox(p.RefBox, frame);
            }

            return maxDepth;
        }

        private static SdfBox BuildBaseFieldFromRefBox(BoundingBox refBox, ReferenceFrame frame)
        {
            Point3d refCenter = refBox.Center;
            Point3d worldCenter = refCenter;
            worldCenter.Transform(frame.RefToWorld);

            Plane plane = frame.Plane;
            plane.Origin = worldCenter;

            double sx = refBox.Max.X - refBox.Min.X;
            double sy = refBox.Max.Y - refBox.Min.Y;
            double sz = refBox.Max.Z - refBox.Min.Z;

            return new SdfBox(
                plane,
                new Interval(-0.5 * sx, 0.5 * sx),
                new Interval(-0.5 * sy, 0.5 * sy),
                new Interval(-0.5 * sz, 0.5 * sz));
        }

        private static bool InferBackAnchorSide(
            List<PanelItem> panels,
            ReferenceFrame frame,
            double tol,
            out double minSideSpread,
            out double maxSideSpread)
        {
            var minSide = new List<double>();
            var maxSide = new List<double>();

            if (panels != null)
            {
                for (int i = 0; i < panels.Count; i++)
                {
                    PanelItem p = panels[i];

                    if (p == null || p.Brep == null || !p.Box.IsValid)
                    {
                        continue;
                    }

                    double zMin;
                    double zMax;

                    if (TryGetDepthFaceSamples(p.Brep, frame, tol, out zMin, out zMax))
                    {
                        minSide.Add(zMin);
                        maxSide.Add(zMax);
                    }
                    else
                    {
                        minSide.Add(p.Box.Min.Z);
                        maxSide.Add(p.Box.Max.Z);
                    }
                }
            }

            minSideSpread = RobustSpread(minSide);
            maxSideSpread = RobustSpread(maxSide);

            if (minSide.Count < 2 || maxSide.Count < 2)
            {
                return true;
            }

            return minSideSpread <= maxSideSpread;
        }

        private static bool TryGetDepthFaceSamples(
            Brep brep,
            ReferenceFrame frame,
            double tol,
            out double zMin,
            out double zMax)
        {
            zMin = double.PositiveInfinity;
            zMax = double.NegativeInfinity;

            if (brep == null || brep.Faces.Count == 0)
            {
                return false;
            }

            Vector3d depth = frame.Plane.ZAxis;
            if (!depth.Unitize())
            {
                return false;
            }

            double minArea = Math.Max(tol * tol, 1e-9);

            for (int i = 0; i < brep.Faces.Count; i++)
            {
                BrepFace face = brep.Faces[i];
                if (face == null)
                {
                    continue;
                }

                double u = face.Domain(0).Mid;
                double v = face.Domain(1).Mid;
                Vector3d n = face.NormalAt(u, v);

                if (!n.Unitize())
                {
                    continue;
                }

                if (Math.Abs(n * depth) < 0.45)
                {
                    continue;
                }

                Point3d sample = Point3d.Unset;
                double area = 0.0;

                try
                {
                    Brep faceBrep = face.DuplicateFace(false);
                    AreaMassProperties amp = faceBrep != null ? AreaMassProperties.Compute(faceBrep) : null;

                    if (amp != null)
                    {
                        sample = amp.Centroid;
                        area = amp.Area;
                    }
                }
                catch
                {
                    sample = Point3d.Unset;
                    area = 0.0;
                }

                if (!sample.IsValid || area < minArea)
                {
                    sample = face.PointAt(u, v);
                }

                if (!sample.IsValid)
                {
                    continue;
                }

                Point3d refPoint = sample;
                refPoint.Transform(frame.WorldToRef);

                zMin = Math.Min(zMin, refPoint.Z);
                zMax = Math.Max(zMax, refPoint.Z);
            }

            return !double.IsInfinity(zMin) && !double.IsInfinity(zMax);
        }

        private static double RobustSpread(List<double> values)
        {
            if (values == null || values.Count < 2)
            {
                return 0.0;
            }

            values.Sort();

            int lo = (int)Math.Floor((values.Count - 1) * 0.1);
            int hi = (int)Math.Ceiling((values.Count - 1) * 0.9);

            lo = Math.Max(0, Math.Min(values.Count - 1, lo));
            hi = Math.Max(0, Math.Min(values.Count - 1, hi));

            return Math.Max(0.0, values[hi] - values[lo]);
        }

        private static BoundingBox ExpandBoxAxisToDepthAnchored(BoundingBox box, int axis, double targetDepth, bool keepMin)
        {
            if (!box.IsValid)
            {
                return box;
            }

            double currentMin = AxisMin(box, axis);
            double currentMax = AxisMax(box, axis);
            double currentDepth = currentMax - currentMin;

            if (currentDepth >= targetDepth)
            {
                return box;
            }

            Point3d min = box.Min;
            Point3d max = box.Max;

            if (keepMin)
            {
                SetAxisValue(ref min, axis, currentMin);
                SetAxisValue(ref max, axis, currentMin + targetDepth);
            }
            else
            {
                SetAxisValue(ref min, axis, currentMax - targetDepth);
                SetAxisValue(ref max, axis, currentMax);
            }

            return new BoundingBox(min, max);
        }

        private static List<InterfaceInfo> DetectCentroidNeighbours(List<PanelItem> panels, double tol)
        {
            var result = new List<InterfaceInfo>();

            double closeTol = Math.Max(tol * 20.0, 1.0);

            for (int i = 0; i < panels.Count; i++)
            {
                PanelItem a = panels[i];

                if (!a.RefBox.IsValid)
                {
                    continue;
                }

                for (int j = i + 1; j < panels.Count; j++)
                {
                    PanelItem b = panels[j];

                    if (!b.RefBox.IsValid)
                    {
                        continue;
                    }

                    Vector3d d = b.Center - a.Center;

                    double ax = Math.Abs(d.X);
                    double ay = Math.Abs(d.Y);
                    double az = Math.Abs(d.Z);

                    int axis = DominantAxis(ax, ay, az);

                    // Ref Z is facade depth. Ignore front/back neighbours.
                    if (axis == 2)
                    {
                        continue;
                    }

                    double gap = AxisGap(a.RefBox, b.RefBox, axis);

                    if (Math.Abs(gap) > closeTol)
                    {
                        continue;
                    }

                    int uAxis;
                    int vAxis;
                    GetOtherAxes(axis, out uAxis, out vAxis);

                    if (!AxisOverlap(a.RefBox, b.RefBox, uAxis, closeTol))
                    {
                        continue;
                    }

                    if (!AxisOverlap(a.RefBox, b.RefBox, vAxis, closeTol))
                    {
                        continue;
                    }

                    double faceCoord = SharedFaceCoordinate(a.RefBox, b.RefBox, axis);
                    BoundingBox overlapBox = BuildOverlapBox(a.RefBox, b.RefBox, axis, faceCoord);

                    if (!overlapBox.IsValid)
                    {
                        continue;
                    }

                    Vector3d normal = AxisVector(axis);

                    if (AxisValue(b.Center, axis) < AxisValue(a.Center, axis))
                    {
                        normal = -normal;
                    }

                    int panelA = Math.Min(i, j);
                    int panelB = Math.Max(i, j);

                    if (panelA != i)
                    {
                        normal = -normal;
                    }

                    result.Add(new InterfaceInfo
                    {
                        PanelA = panelA,
                        PanelB = panelB,
                        Center = overlapBox.Center,
                        NormalAToB = normal,
                        OverlapBox = overlapBox,
                        Axis = axis,
                        IsVerticalStack = axis == 1
                    });
                }
            }

            return result;
        }

        private static Plane BuildInterfacePlaneWorld(InterfaceInfo iface, ReferenceFrame frame)
        {
            Vector3d nRef = iface.NormalAToB;

            if (!nRef.Unitize())
            {
                nRef = Vector3d.XAxis;
            }

            Vector3d xRef;

            if (Math.Abs(nRef * Vector3d.ZAxis) > 0.9)
            {
                xRef = Vector3d.XAxis;
            }
            else
            {
                xRef = Vector3d.CrossProduct(Vector3d.ZAxis, nRef);

                if (!xRef.Unitize())
                {
                    xRef = Vector3d.XAxis;
                }
            }

            Vector3d yRef = Vector3d.CrossProduct(nRef, xRef);

            if (!yRef.Unitize())
            {
                yRef = Vector3d.YAxis;
            }

            Plane planeRef = new Plane(iface.Center, xRef, yRef);
            Plane planeWorld = planeRef;
            planeWorld.Transform(frame.RefToWorld);

            return planeWorld;
        }

        private static void GetInterfaceBounds(
            InterfaceInfo iface,
            out double uMin,
            out double uMax,
            out double vMin,
            out double vMax)
        {
            int uAxis;
            int vAxis;
            GetOtherAxes(iface.Axis, out uAxis, out vAxis);

            uMin = AxisMin(iface.OverlapBox, uAxis) - AxisValue(iface.Center, uAxis);
            uMax = AxisMax(iface.OverlapBox, uAxis) - AxisValue(iface.Center, uAxis);
            vMin = AxisMin(iface.OverlapBox, vAxis) - AxisValue(iface.Center, vAxis);
            vMax = AxisMax(iface.OverlapBox, vAxis) - AxisValue(iface.Center, vAxis);
        }

        private static Interval ExpandIntervalFromCenter(Interval interval, double extension)
        {
            double a = Math.Min(interval.T0, interval.T1);
            double b = Math.Max(interval.T0, interval.T1);
            double c = 0.5 * (a + b);
            double half = 0.5 * (b - a) + Math.Max(0.0, extension);
            return new Interval(c - half, c + half);
        }

        private static List<StripBounds> BuildStrips(
            bool isVerticalStack,
            int sideJointDir,
            int count,
            double uMin,
            double uMax,
            double vMin,
            double vMax,
            double tol)
        {
            var strips = new List<StripBounds>();

            if (count < 1)
            {
                return strips;
            }

            bool splitSideAsVertical = !isVerticalStack && sideJointDir != 1;

            if (!isVerticalStack && !splitSideAsVertical)
            {
                double width = uMax - uMin;

                if (width <= tol)
                {
                    return strips;
                }

                double step = width / count;

                for (int k = 0; k < count; k++)
                {
                    strips.Add(new StripBounds(
                        uMin + k * step,
                        uMin + (k + 1) * step,
                        vMin,
                        vMax));
                }
            }
            else
            {
                double height = vMax - vMin;

                if (height <= tol)
                {
                    return strips;
                }

                double step = height / count;

                for (int k = 0; k < count; k++)
                {
                    strips.Add(new StripBounds(
                        uMin,
                        uMax,
                        vMin + k * step,
                        vMin + (k + 1) * step));
                }
            }

            return strips;
        }

        private static double EvaluatePanelField(
            SdfBox baseField,
            List<SdfBox> lateralAdditions,
            List<SdfBox> lateralPocketCutters,
            List<SdfBox> lateralZoneCutters,
            List<SdfBox> lateralProtectZones,         // kept for API compat, unused
            List<SdfBox> topBottomAdditions,
            List<SdfBox> topBottomPocketCutters,
            List<SdfBox> topBottomZoneCutters,
            List<SdfBox> topBottomProtectZones,        // kept for API compat, unused
            double ownerPad,
            double sameFamilyShrink,
            double otherFamilyShrink,
            Point3d p)
        {
            // ── Strict-ownership SDF evaluation ────────────────────────────────────
            // Sequence:
            //   1.  Place all material (base + every addition, both families).
            //   2.  Apply each family's cutters, masked by:
            //         a) "territory"        = base ∪ same-family additions
            //            → cutter only acts where this panel owns material in its
            //              own family.  Prevents leaking onto neighbour panels.
            //         b) "other-family interior" veto
            //            → cutter does NOT act where the OTHER family's added
            //              fingers live.  Prevents lateral cutters from chewing
            //              top/bottom finger material at corners, and vice versa.
            //
            // ownerPad        : tolerance band outside the territory boundary
            //                    where cuts are still allowed, so the cut can
            //                    shape the boundary cleanly.  Sized in cells of
            //                    res so a single MC cell never collapses it.
            // otherFamilyShrink : inward distance from an other-family addition
            //                    surface where the cut is vetoed.  Slightly
            //                    smaller than ownerPad so the cut can still
            //                    meet the addition's surface at the corner.

            // Phase 1 – base panel body.
            double material = baseField.SignedDistance(p);

            // Phase 2 – ALL additions first (both families).  This is critical:
            // if any cut happened before all additions were placed, later
            // additions would recreate defects.
            UnionAdditions(ref material, lateralAdditions, p);
            UnionAdditions(ref material, topBottomAdditions, p);

            // Phase 3 – cuts, each masked by its own family's territory and
            // vetoed inside both same-family and other-family addition interiors.
            ApplyCuttersOwned(
                ref material, lateralPocketCutters, p,
                baseField, lateralAdditions, topBottomAdditions,
                ownerPad, sameFamilyShrink, otherFamilyShrink);

            ApplyCuttersOwned(
                ref material, lateralZoneCutters, p,
                baseField, lateralAdditions, topBottomAdditions,
                ownerPad, sameFamilyShrink, otherFamilyShrink);

            ApplyCuttersOwned(
                ref material, topBottomPocketCutters, p,
                baseField, topBottomAdditions, lateralAdditions,
                ownerPad, sameFamilyShrink, otherFamilyShrink);

            ApplyCuttersOwned(
                ref material, topBottomZoneCutters, p,
                baseField, topBottomAdditions, lateralAdditions,
                ownerPad, sameFamilyShrink, otherFamilyShrink);

            return material;
        }

        /// <summary>
        /// Applies a family's cutters only at points the panel owns in that family.
        /// </summary>
        /// <param name="cutters">Cutter SDFs to apply.</param>
        /// <param name="baseField">This panel's base SDF.</param>
        /// <param name="sameFamilyAdditions">Same-family addition SDFs (extends territory AND vetoes the cut inside their interior with sameFamilyShrink).</param>
        /// <param name="otherFamilyAdditions">Other-family addition SDFs (vetoes the cut inside their interior with otherFamilyShrink).</param>
        /// <param name="ownerPad">Distance outside the territory boundary where cuts are still applied.</param>
        /// <param name="sameFamilyShrink">Inward distance from a same-family addition surface where the cut is vetoed. Protects neighbouring strips' finger anchors on the same panel.</param>
        /// <param name="otherFamilyShrink">Inward distance from the other-family addition surface where the cut is vetoed.</param>
        private static void ApplyCuttersOwned(
            ref double f,
            List<SdfBox> cutters,
            Point3d p,
            SdfBox baseField,
            List<SdfBox> sameFamilyAdditions,
            List<SdfBox> otherFamilyAdditions,
            double ownerPad,
            double sameFamilyShrink,
            double otherFamilyShrink)
        {
            if (cutters == null || cutters.Count == 0)
            {
                return;
            }

            // Territory SDF (negative = inside) = min(base, all same-family additions).
            double territoryDist = (baseField != null && baseField.IsValid)
                ? baseField.SignedDistance(p)
                : double.PositiveInfinity;

            if (sameFamilyAdditions != null)
            {
                for (int i = 0; i < sameFamilyAdditions.Count; i++)
                {
                    SdfBox a = sameFamilyAdditions[i];

                    if (a == null || !a.IsValid)
                    {
                        continue;
                    }

                    double d = a.SignedDistance(p);

                    if (d < territoryDist)
                    {
                        territoryDist = d;
                    }
                }
            }

            // Outside the owned territory (with pad) → skip the cut entirely.
            if (territoryDist > ownerPad)
            {
                return;
            }

            // Inside a SAME-family addition's interior → veto the cut.  Defense in
            // depth so a cutter for one strip cannot eat its neighbour strip's
            // finger anchor on the same panel, even if cutterExtension grows.
            // Cuts at the addition's surface (|SD| ≤ sameFamilyShrink) are still
            // allowed so the cutter can shape the addition's boundary cleanly.
            if (sameFamilyAdditions != null)
            {
                for (int i = 0; i < sameFamilyAdditions.Count; i++)
                {
                    SdfBox a = sameFamilyAdditions[i];

                    if (a == null || !a.IsValid)
                    {
                        continue;
                    }

                    if (a.SignedDistance(p) < -sameFamilyShrink)
                    {
                        return;
                    }
                }
            }

            // Inside the other-family addition's interior → veto the cut so the
            // other family's finger material survives the corner intersection.
            if (otherFamilyAdditions != null)
            {
                for (int i = 0; i < otherFamilyAdditions.Count; i++)
                {
                    SdfBox a = otherFamilyAdditions[i];

                    if (a == null || !a.IsValid)
                    {
                        continue;
                    }

                    if (a.SignedDistance(p) < -otherFamilyShrink)
                    {
                        return;
                    }
                }
            }

            // Apply cutters.
            for (int i = 0; i < cutters.Count; i++)
            {
                SdfBox c = cutters[i];

                if (c == null || !c.IsValid)
                {
                    continue;
                }

                f = Math.Max(f, -c.SignedDistance(p));
            }
        }

        private static void UnionAdditions(
            ref double f,
            List<SdfBox> additions,
            Point3d p)
        {
            if (additions == null || additions.Count == 0)
            {
                return;
            }

            for (int i = 0; i < additions.Count; i++)
            {
                SdfBox a = additions[i];

                if (a == null || !a.IsValid)
                {
                    continue;
                }

                f = Math.Min(f, a.SignedDistance(p));
            }
        }

        private static void ApplyCutters(ref double f, List<SdfBox> cutters, Point3d p)
        {
            if (cutters == null)
            {
                return;
            }

            for (int i = 0; i < cutters.Count; i++)
            {
                SdfBox c = cutters[i];

                if (c == null || !c.IsValid)
                {
                    continue;
                }

                f = Math.Max(f, -c.SignedDistance(p));
            }
        }

        // ApplyCuttersGuarded / InsideAnyField removed: superseded by
        // ApplyCuttersOwned, which uses per-panel territory masks instead of
        // symmetric interface protect zones.

        private static SdfBox ClampSdfBoxToPanelBase(
            SdfBox field,
            PanelItem panel,
            ReferenceFrame frame,
            double pad)
        {
            if (field == null || !field.IsValid || panel == null || !panel.RefBox.IsValid)
            {
                return field;
            }

            BoundingBox localBounds = GetRefBoxBoundsInPlane(panel.RefBox, frame.RefToWorld, field.Plane);

            if (!localBounds.IsValid)
            {
                return InvalidSdfBox(field.Plane);
            }

            Interval xLimit = new Interval(localBounds.Min.X - pad, localBounds.Max.X + pad);
            Interval yLimit = new Interval(localBounds.Min.Y - pad, localBounds.Max.Y + pad);
            Interval zLimit = new Interval(localBounds.Min.Z - pad, localBounds.Max.Z + pad);

            Interval x;
            Interval y;
            Interval z;

            if (!TryIntersectIntervals(field.X, xLimit, out x) ||
                !TryIntersectIntervals(field.Y, yLimit, out y) ||
                !TryIntersectIntervals(field.Z, zLimit, out z))
            {
                return InvalidSdfBox(field.Plane);
            }

            return new SdfBox(field.Plane, x, y, z);
        }

        private static BoundingBox GetRefBoxBoundsInPlane(
            BoundingBox refBox,
            Transform refToWorld,
            Plane plane)
        {
            if (!refBox.IsValid || !plane.IsValid)
            {
                return BoundingBox.Empty;
            }

            var result = BoundingBox.Empty;
            Point3d[] corners = refBox.GetCorners();

            for (int i = 0; i < corners.Length; i++)
            {
                Point3d p = corners[i];
                p.Transform(refToWorld);

                Vector3d d = p - plane.Origin;
                Point3d local = new Point3d(
                    d * plane.XAxis,
                    d * plane.YAxis,
                    d * plane.ZAxis);

                if (!result.IsValid)
                {
                    result = new BoundingBox(local, local);
                }
                else
                {
                    result.Union(local);
                }
            }

            return result;
        }

        private static bool TryIntersectIntervals(Interval a, Interval b, out Interval result)
        {
            double t0 = Math.Max(Math.Min(a.T0, a.T1), Math.Min(b.T0, b.T1));
            double t1 = Math.Min(Math.Max(a.T0, a.T1), Math.Max(b.T0, b.T1));

            if (t1 <= t0 + EPS)
            {
                result = Interval.Unset;
                return false;
            }

            result = new Interval(t0, t1);
            return true;
        }

        private static SdfBox InvalidSdfBox(Plane plane)
        {
            return new SdfBox(
                plane,
                new Interval(0.0, 0.0),
                new Interval(0.0, 0.0),
                new Interval(0.0, 0.0));
        }

        private static Mesh ExtractPanelFieldMeshMC(
            SdfBox baseField,
            List<SdfBox> lateralAdditions,
            List<SdfBox> lateralPocketCutters,
            List<SdfBox> lateralZoneCutters,
            List<SdfBox> lateralProtectZones,
            List<SdfBox> topBottomAdditions,
            List<SdfBox> topBottomPocketCutters,
            List<SdfBox> topBottomZoneCutters,
            List<SdfBox> topBottomProtectZones,
            ReferenceFrame frame,
            double res,
            int maxSamples,
            out string warning)
        {
            warning = "";

            if (baseField == null || !baseField.IsValid)
            {
                warning = "Invalid base field.";
                return null;
            }

            BoundingBox refDomain = GetSdfBoxBoundsInRef(baseField, frame.WorldToRef);

            UnionFieldBounds(ref refDomain, lateralAdditions, frame.WorldToRef);
            UnionFieldBounds(ref refDomain, topBottomAdditions, frame.WorldToRef);

            refDomain.Inflate(res * 2.0);

            // Resolution-aware ownership tolerances.  All in cells of res so a
            // single Marching Cubes cell can't collapse them at coarse res.
            //
            // ownerPad           — distance OUTSIDE the territory boundary where
            //                      cuts still apply.  Keep modest so the cut
            //                      doesn't bleed into the next panel's mesh.
            // sameFamilyShrink   — inward distance from a same-family addition
            //                      where the cut is vetoed.  Protects neighbour
            //                      strips' fingers on the same panel.
            // otherFamilyShrink  — inward distance from the OTHER family's
            //                      addition where the cut is vetoed.  Larger so
            //                      cross-family fingers survive even small
            //                      cutter misalignments at corners.
            double ownerPad = res * 2.0;
            double sameFamilyShrink = res * 0.5;
            double otherFamilyShrink = res * 1.0;

            return ExtractFieldMeshMC(
                refDomain,
                frame,
                res,
                maxSamples,
                p => EvaluatePanelField(
                    baseField,
                    lateralAdditions, lateralPocketCutters, lateralZoneCutters, lateralProtectZones,
                    topBottomAdditions, topBottomPocketCutters, topBottomZoneCutters, topBottomProtectZones,
                    ownerPad, sameFamilyShrink, otherFamilyShrink,
                    p),
                out warning);
        }

        private static void UnionFieldBounds(ref BoundingBox refDomain, List<SdfBox> fields, Transform worldToRef)
        {
            if (fields == null)
            {
                return;
            }

            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i] != null && fields[i].IsValid)
                {
                    refDomain.Union(GetSdfBoxBoundsInRef(fields[i], worldToRef));
                }
            }
        }

        private static Mesh ExtractSingleFieldMeshMC(
            SdfBox field,
            ReferenceFrame frame,
            double res,
            int maxSamples,
            out string warning)
        {
            warning = "";

            if (field == null || !field.IsValid)
            {
                warning = "Invalid field.";
                return null;
            }

            BoundingBox refDomain = GetSdfBoxBoundsInRef(field, frame.WorldToRef);
            refDomain.Inflate(res * 2.0);

            return ExtractFieldMeshMC(
                refDomain,
                frame,
                res,
                maxSamples,
                p => field.SignedDistance(p),
                out warning);
        }

        private static Mesh ExtractFieldMeshMC(
            BoundingBox refDomain,
            ReferenceFrame frame,
            double res,
            int maxSamples,
            Func<Point3d, double> evaluatorWorld,
            out string warning)
        {
            warning = "";

            if (!refDomain.IsValid)
            {
                warning = "Invalid reference-domain bounding box.";
                return null;
            }

            double sizeX = refDomain.Max.X - refDomain.Min.X;
            double sizeY = refDomain.Max.Y - refDomain.Min.Y;
            double sizeZ = refDomain.Max.Z - refDomain.Min.Z;

            int nx = Math.Max(2, (int)Math.Ceiling(sizeX / res) + 1);
            int ny = Math.Max(2, (int)Math.Ceiling(sizeY / res) + 1);
            int nz = Math.Max(2, (int)Math.Ceiling(sizeZ / res) + 1);

            long sampleCount = (long)nx * ny * nz;

            if (sampleCount > maxSamples)
            {
                warning = $"too many MC samples ({sampleCount:N0}). Increase res.";
                return null;
            }

            double[] scalars = new double[sampleCount];

            int threads = Math.Max(1, Environment.ProcessorCount - 1);

            Parallel.For(
                0,
                nz,
                new ParallelOptions { MaxDegreeOfParallelism = threads },
                iz =>
                {
                    double rz = Lerp(refDomain.Min.Z, refDomain.Max.Z, nz <= 1 ? 0.0 : (double)iz / (nz - 1));

                    for (int iy = 0; iy < ny; iy++)
                    {
                        double ry = Lerp(refDomain.Min.Y, refDomain.Max.Y, ny <= 1 ? 0.0 : (double)iy / (ny - 1));

                        for (int ix = 0; ix < nx; ix++)
                        {
                            double rx = Lerp(refDomain.Min.X, refDomain.Max.X, nx <= 1 ? 0.0 : (double)ix / (nx - 1));

                            Point3d pWorld = new Point3d(rx, ry, rz);
                            pWorld.Transform(frame.RefToWorld);

                            scalars[Idx(ix, iy, iz, nx, ny)] = evaluatorWorld(pWorld);
                        }
                    }
                });

            Mesh mesh = MarchingCubesTableParallel(
                scalars,
                refDomain,
                frame,
                nx,
                ny,
                nz,
                false);

            if (mesh == null || mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
            {
                warning = "Marching Cubes produced an empty mesh.";
                return null;
            }

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.Weld(RhinoMath.ToRadians(180.0));
            mesh.UnifyNormals();
            mesh.Normals.ComputeNormals();
            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Compact();

            return mesh;
        }

        private static BoundingBox GetSdfBoxBoundsInRef(SdfBox field, Transform worldToRef)
        {
            if (field == null || !field.IsValid)
            {
                return BoundingBox.Empty;
            }

            Box box = new Box(field.Plane, field.X, field.Y, field.Z);
            Point3d[] corners = box.GetCorners();

            var bb = BoundingBox.Empty;

            for (int i = 0; i < corners.Length; i++)
            {
                Point3d p = corners[i];
                p.Transform(worldToRef);

                if (!bb.IsValid)
                {
                    bb = new BoundingBox(p, p);
                }
                else
                {
                    bb.Union(p);
                }
            }

            return bb;
        }

        private static Mesh MarchingCubesTableParallel(
            double[] scalars,
            BoundingBox refDomain,
            ReferenceFrame frame,
            int nx,
            int ny,
            int nz,
            bool extractComplement)
        {
            if (scalars == null || scalars.Length == 0)
            {
                return null;
            }

            int sliceCount = Math.Max(0, nz - 1);

            if (sliceCount <= 0)
            {
                return null;
            }

            var sliceMeshes = new Mesh[sliceCount];
            int threads = Math.Max(1, Environment.ProcessorCount - 1);

            Parallel.For(
                0,
                sliceCount,
                new ParallelOptions { MaxDegreeOfParallelism = threads },
                iz =>
                {
                    var localMesh = new Mesh();
                    var vertexMap = new Dictionary<VertexKey, int>();

                    ProcessMarchingCubesSlice(
                        scalars,
                        refDomain,
                        frame,
                        nx,
                        ny,
                        nz,
                        iz,
                        extractComplement,
                        localMesh,
                        vertexMap);

                    if (localMesh.Faces.Count > 0)
                    {
                        localMesh.Vertices.CombineIdentical(true, true);
                        localMesh.Faces.CullDegenerateFaces();
                        localMesh.Vertices.CullUnused();
                        localMesh.Compact();

                        sliceMeshes[iz] = localMesh;
                    }
                });

            var mesh = new Mesh();

            for (int i = 0; i < sliceMeshes.Length; i++)
            {
                if (sliceMeshes[i] != null && sliceMeshes[i].Faces.Count > 0)
                {
                    mesh.Append(sliceMeshes[i]);
                }
            }

            if (mesh.Faces.Count == 0)
            {
                return null;
            }

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.Weld(RhinoMath.ToRadians(180.0));
            mesh.UnifyNormals();
            mesh.Normals.ComputeNormals();
            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Compact();

            return mesh;
        }

        private static void ProcessMarchingCubesSlice(
            double[] scalars,
            BoundingBox refDomain,
            ReferenceFrame frame,
            int nx,
            int ny,
            int nz,
            int iz,
            bool extractComplement,
            Mesh mesh,
            Dictionary<VertexKey, int> vertexMap)
        {
            int[,] cubeCorners =
            {
                {0, 0, 0},
                {1, 0, 0},
                {1, 1, 0},
                {0, 1, 0},
                {0, 0, 1},
                {1, 0, 1},
                {1, 1, 1},
                {0, 1, 1}
            };

            int[,] edgeCorners =
            {
                {0, 1},
                {1, 2},
                {2, 3},
                {3, 0},
                {4, 5},
                {5, 6},
                {6, 7},
                {7, 4},
                {0, 4},
                {1, 5},
                {2, 6},
                {3, 7}
            };

            double keyTol = Math.Max(GetAverageGridStep(refDomain, nx, ny, nz) * 1e-6, 1e-9);

            double[] sv = new double[8];
            Point3d[] cp = new Point3d[8];
            Point3d[] edgeVerts = new Point3d[12];

            for (int iy = 0; iy < ny - 1; iy++)
            {
                for (int ix = 0; ix < nx - 1; ix++)
                {
                    int cubeIndex = 0;

                    for (int c = 0; c < 8; c++)
                    {
                        int gx = ix + cubeCorners[c, 0];
                        int gy = iy + cubeCorners[c, 1];
                        int gz = iz + cubeCorners[c, 2];

                        sv[c] = scalars[Idx(gx, gy, gz, nx, ny)];
                        cp[c] = GridPointWorld(refDomain, frame, gx, gy, gz, nx, ny, nz);

                        bool inside = extractComplement ? sv[c] >= 0.0 : sv[c] < 0.0;

                        if (inside)
                        {
                            cubeIndex |= 1 << c;
                        }
                    }

                    if (cubeIndex == 0 || cubeIndex == 255)
                    {
                        continue;
                    }

                    for (int e = 0; e < 12; e++)
                    {
                        edgeVerts[e] = Point3d.Unset;
                    }

                    for (int t = 0; t < 16; t += 3)
                    {
                        int e0 = MarchingCubesClassicTable.TriTable[cubeIndex, t];

                        if (e0 < 0)
                        {
                            break;
                        }

                        int e1 = MarchingCubesClassicTable.TriTable[cubeIndex, t + 1];
                        int e2 = MarchingCubesClassicTable.TriTable[cubeIndex, t + 2];

                        if (!edgeVerts[e0].IsValid)
                        {
                            edgeVerts[e0] = GetInterpolatedEdgeVertex(e0, edgeCorners, cp, sv);
                        }

                        if (!edgeVerts[e1].IsValid)
                        {
                            edgeVerts[e1] = GetInterpolatedEdgeVertex(e1, edgeCorners, cp, sv);
                        }

                        if (!edgeVerts[e2].IsValid)
                        {
                            edgeVerts[e2] = GetInterpolatedEdgeVertex(e2, edgeCorners, cp, sv);
                        }

                        int a = AddVertex(mesh, vertexMap, edgeVerts[e0], keyTol);
                        int b = AddVertex(mesh, vertexMap, edgeVerts[e1], keyTol);
                        int c = AddVertex(mesh, vertexMap, edgeVerts[e2], keyTol);

                        if (a == b || b == c || c == a)
                        {
                            continue;
                        }

                        mesh.Faces.AddFace(a, c, b);
                    }
                }
            }
        }

        private static Point3d GridPointWorld(
            BoundingBox refDomain,
            ReferenceFrame frame,
            int ix,
            int iy,
            int iz,
            int nx,
            int ny,
            int nz)
        {
            double u = nx <= 1 ? 0.0 : (double)ix / (nx - 1);
            double v = ny <= 1 ? 0.0 : (double)iy / (ny - 1);
            double w = nz <= 1 ? 0.0 : (double)iz / (nz - 1);

            Point3d p = new Point3d(
                Lerp(refDomain.Min.X, refDomain.Max.X, u),
                Lerp(refDomain.Min.Y, refDomain.Max.Y, v),
                Lerp(refDomain.Min.Z, refDomain.Max.Z, w));

            p.Transform(frame.RefToWorld);

            return p;
        }

        private static double GetAverageGridStep(BoundingBox refDomain, int nx, int ny, int nz)
        {
            double sx = nx <= 1 ? 0.0 : (refDomain.Max.X - refDomain.Min.X) / (nx - 1);
            double sy = ny <= 1 ? 0.0 : (refDomain.Max.Y - refDomain.Min.Y) / (ny - 1);
            double sz = nz <= 1 ? 0.0 : (refDomain.Max.Z - refDomain.Min.Z) / (nz - 1);

            return (sx + sy + sz) / 3.0;
        }

        private static Point3d GetInterpolatedEdgeVertex(
            int edgeIndex,
            int[,] edgeCorners,
            Point3d[] cp,
            double[] sv)
        {
            int a = edgeCorners[edgeIndex, 0];
            int b = edgeCorners[edgeIndex, 1];

            return InterpolateIsoVertex(cp[a], cp[b], sv[a], sv[b]);
        }

        private static Point3d InterpolateIsoVertex(Point3d p0, Point3d p1, double v0, double v1)
        {
            double d = v0 - v1;

            if (Math.Abs(d) < 1e-14)
            {
                return new Point3d(
                    0.5 * (p0.X + p1.X),
                    0.5 * (p0.Y + p1.Y),
                    0.5 * (p0.Z + p1.Z));
            }

            double t = v0 / d;
            t = Clamp01(t);

            return p0 + t * (p1 - p0);
        }

        private static int AddVertex(Mesh mesh, Dictionary<VertexKey, int> map, Point3d p, double tol)
        {
            var key = new VertexKey(p, tol);

            if (map.TryGetValue(key, out int idx))
            {
                return idx;
            }

            idx = mesh.Vertices.Add(p);
            map[key] = idx;

            return idx;
        }


        private static Curve CreateProfileCurve(Plane plane, StripBounds strip)
        {
            try
            {
                var rect = new Rectangle3d(
                    plane,
                    new Interval(strip.U0, strip.U1),
                    new Interval(strip.V0, strip.V1));

                return rect.ToNurbsCurve();
            }
            catch
            {
                return null;
            }
        }

        private static bool AreValidBounds(double uMin, double uMax, double vMin, double vMax, double tol)
        {
            if (double.IsNaN(uMin) || double.IsNaN(uMax) || double.IsNaN(vMin) || double.IsNaN(vMax))
            {
                return false;
            }

            if (double.IsInfinity(uMin) || double.IsInfinity(uMax) || double.IsInfinity(vMin) || double.IsInfinity(vMax))
            {
                return false;
            }

            return (uMax - uMin) > tol && (vMax - vMin) > tol;
        }

        private static int DominantAxis(double x, double y, double z)
        {
            if (x >= y && x >= z) return 0;
            if (y >= x && y >= z) return 1;
            return 2;
        }

        private static double AxisSize(Vector3d size, int axis)
        {
            if (axis == 0)
            {
                return Math.Abs(size.X);
            }

            if (axis == 1)
            {
                return Math.Abs(size.Y);
            }

            return Math.Abs(size.Z);
        }

        private static double AxisGap(BoundingBox a, BoundingBox b, int axis)
        {
            double aMin = AxisMin(a, axis);
            double aMax = AxisMax(a, axis);
            double bMin = AxisMin(b, axis);
            double bMax = AxisMax(b, axis);

            if (aMax <= bMin) return bMin - aMax;
            if (bMax <= aMin) return aMin - bMax;
            return 0.0;
        }

        private static bool AxisOverlap(BoundingBox a, BoundingBox b, int axis, double tol)
        {
            double aMin = AxisMin(a, axis);
            double aMax = AxisMax(a, axis);
            double bMin = AxisMin(b, axis);
            double bMax = AxisMax(b, axis);

            return Math.Min(aMax, bMax) - Math.Max(aMin, bMin) > -tol;
        }

        private static double SharedFaceCoordinate(BoundingBox a, BoundingBox b, int axis)
        {
            double aMin = AxisMin(a, axis);
            double aMax = AxisMax(a, axis);
            double bMin = AxisMin(b, axis);
            double bMax = AxisMax(b, axis);

            if (Math.Abs(aMax - bMin) <= Math.Abs(bMax - aMin))
            {
                return 0.5 * (aMax + bMin);
            }

            return 0.5 * (bMax + aMin);
        }

        private static BoundingBox BuildOverlapBox(BoundingBox a, BoundingBox b, int axis, double faceCoord)
        {
            double[] min = new double[3];
            double[] max = new double[3];

            for (int k = 0; k < 3; k++)
            {
                if (k == axis)
                {
                    min[k] = faceCoord;
                    max[k] = faceCoord;
                }
                else
                {
                    min[k] = Math.Max(AxisMin(a, k), AxisMin(b, k));
                    max[k] = Math.Min(AxisMax(a, k), AxisMax(b, k));
                }
            }

            Point3d pMin = new Point3d(min[0], min[1], min[2]);
            Point3d pMax = new Point3d(max[0], max[1], max[2]);

            return new BoundingBox(pMin, pMax);
        }

        private static void GetOtherAxes(int axis, out int u, out int v)
        {
            if (axis == 0)
            {
                u = 1;
                v = 2;
                return;
            }

            if (axis == 1)
            {
                u = 0;
                v = 2;
                return;
            }

            u = 0;
            v = 1;
        }

        private static Vector3d AxisVector(int axis)
        {
            if (axis == 0) return Vector3d.XAxis;
            if (axis == 1) return Vector3d.YAxis;
            return Vector3d.ZAxis;
        }

        private static double AxisValue(Point3d p, int axis)
        {
            if (axis == 0) return p.X;
            if (axis == 1) return p.Y;
            return p.Z;
        }

        private static double AxisMin(BoundingBox b, int axis)
        {
            if (axis == 0) return b.Min.X;
            if (axis == 1) return b.Min.Y;
            return b.Min.Z;
        }

        private static double AxisMax(BoundingBox b, int axis)
        {
            if (axis == 0) return b.Max.X;
            if (axis == 1) return b.Max.Y;
            return b.Max.Z;
        }

        private static void SetAxisValue(ref Point3d p, int axis, double value)
        {
            if (axis == 0)
            {
                p.X = value;
                return;
            }

            if (axis == 1)
            {
                p.Y = value;
                return;
            }

            p.Z = value;
        }

        private static int Idx(int ix, int iy, int iz, int nx, int ny)
        {
            return ix + nx * (iy + ny * iz);
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + Clamp01(t) * (b - a);
        }

        private static double Clamp01(double v)
        {
            if (v < 0.0)
            {
                return 0.0;
            }

            if (v > 1.0)
            {
                return 1.0;
            }

            return v;
        }

        private static string BuildInfo(
            int panelCount,
            int interfaceCount,
            int debugJobCount,
            double sideOverlap,
            double topBottomOverlap,
            int waveCount,
            double clearance,
            double res,
            int sideJointDir,
            double referenceDepth,
            bool keepBackMin,
            double backMinSpread,
            double backMaxSpread,
            ReferenceFrame frame,
            int threads,
            int panelMeshOk,
            int panelMeshFail,
            int panelTooHeavy,
            int debugOk,
            int debugFail,
            int skippedZeroOverlap,
            int skippedBadBounds,
            int invalidFields,
            long adjacencyMs,
            long jobMs,
            long panelSdfMs,
            long debugSdfMs,
            long totalMs)
        {
            var sb = new StringBuilder();

            sb.AppendLine("---- Fa06 Panel Joints Local SDF ----");
            sb.AppendLine($"panels             : {panelCount}");
            sb.AppendLine($"interfaces found   : {interfaceCount}");
            sb.AppendLine($"debug sdf jobs     : {debugJobCount}");
            sb.AppendLine();
            sb.AppendLine("Output strategy:");
            sb.AppendLine("  panels_out        : generated SDF panel meshes");
            sb.AppendLine("  extraction        : table-based Marching Cubes");
            sb.AppendLine("  sampling grid     : facade reference frame");
            sb.AppendLine("  base geometry     : aligned normalized box field");
            sb.AppendLine("  original Brep trim: not preserved yet");
            sb.AppendLine();
            sb.AppendLine("Facade reference frame:");
            sb.AppendLine("  enabled           : true");
            sb.AppendLine("  method            : average largest-face normals");
            sb.AppendLine($"  origin            : {frame.Plane.Origin.X:0.###}, {frame.Plane.Origin.Y:0.###}, {frame.Plane.Origin.Z:0.###}");
            sb.AppendLine($"  X horizontal      : {frame.Plane.XAxis.X:0.###}, {frame.Plane.XAxis.Y:0.###}, {frame.Plane.XAxis.Z:0.###}");
            sb.AppendLine($"  Y vertical        : {frame.Plane.YAxis.X:0.###}, {frame.Plane.YAxis.Y:0.###}, {frame.Plane.YAxis.Z:0.###}");
            sb.AppendLine($"  Z depth           : {frame.Plane.ZAxis.X:0.###}, {frame.Plane.ZAxis.Y:0.###}, {frame.Plane.ZAxis.Z:0.###}");
            sb.AppendLine();
            sb.AppendLine("Reference box normalization:");
            sb.AppendLine("  enabled           : true");
            sb.AppendLine("  rule              : expand each aligned bbox along reference Z from inferred back datum");
            sb.AppendLine("  reference depth   : thickest panel bbox depth in aligned frame");
            sb.AppendLine($"  reference value   : {referenceDepth:0.###} model units");
            sb.AppendLine($"  anchored side     : {(keepBackMin ? "min reference Z" : "max reference Z")}");
            sb.AppendLine($"  min side spread   : {backMinSpread:0.###}");
            sb.AppendLine($"  max side spread   : {backMaxSpread:0.###}");
            sb.AppendLine();
            sb.AppendLine("Neighbour detection:");
            sb.AppendLine("  method            : centroid + aligned normalized reference-box contact");
            sb.AppendLine("  coordinates       : facade reference frame");
            sb.AppendLine("  ignored axis      : reference Z front/back neighbours");
            sb.AppendLine();
            sb.AppendLine("SDF strategy:");
            sb.AppendLine("  base field        : analytic aligned box SDF");
            sb.AppendLine("  joint fields      : analytic local box SDFs");
            sb.AppendLine("  boolean order     : base cut by normal pockets; side transfers cut only by side cleanup pockets; top/bottom transfers cut only by top/bottom cleanup pockets");
            sb.AppendLine("  Brep booleans     : false");
            sb.AppendLine("  Brep point sampling: false");
            sb.AppendLine();
            sb.AppendLine("Parameters:");
            sb.AppendLine($"  side_overlap      : {sideOverlap:0.###}");
            sb.AppendLine($"  top_bottom_overlap: {topBottomOverlap:0.###}");
            sb.AppendLine($"  wave_count        : {waveCount}");
            sb.AppendLine($"  side_joint_dir    : {(sideJointDir == 1 ? "horizontal" : "vertical")}");
            sb.AppendLine($"  clearance         : {clearance:0.###}");
            sb.AppendLine($"  res               : {res:0.###} model units");
            sb.AppendLine($"  threads           : {threads} automatic");
            sb.AppendLine();
            sb.AppendLine("Panel mesh results:");
            sb.AppendLine($"  panels OK         : {panelMeshOk}");
            sb.AppendLine($"  panels failed     : {panelMeshFail}");
            sb.AppendLine($"  too heavy skipped : {panelTooHeavy}");
            sb.AppendLine();
            sb.AppendLine("Debug mesh results:");
            sb.AppendLine($"  debug OK          : {debugOk}");
            sb.AppendLine($"  debug failed      : {debugFail}");
            sb.AppendLine($"  skipped overlap=0 : {skippedZeroOverlap}");
            sb.AppendLine($"  skipped bad bounds: {skippedBadBounds}");
            sb.AppendLine($"  invalid fields    : {invalidFields}");
            sb.AppendLine();
            sb.AppendLine("Timing:");
            sb.AppendLine($"  adjacency         : {adjacencyMs} ms");
            sb.AppendLine($"  jobs              : {jobMs} ms");
            sb.AppendLine($"  panel sdf         : {panelSdfMs} ms");
            sb.AppendLine($"  debug sdf         : {debugSdfMs} ms");
            sb.AppendLine($"  total             : {totalMs} ms");

            return sb.ToString().TrimEnd();
        }

    }
}
