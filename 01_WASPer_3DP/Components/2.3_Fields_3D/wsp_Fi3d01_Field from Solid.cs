// wsp_Fi3d01_Field from Solid.cs
// WASPer_3DP — Subcategory: 2.3_Fields_3D
//
// Converts a Box or solid-capable Rhino geometry into a WASPer signed
// distance field.  The field evaluates negative inside the solid and positive
// outside, with magnitude equal to the Euclidean distance to the nearest surface.
//
// Downstream uses:
//   • Wire into Fi3d02+ for field booleans and blending between solids.
//   • Use as a trim / clip domain for TPMS infill components (In08–In12) once
//     those accept a field input.
//   • Iso-surface extraction at any offset distance.

using System;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_3_Fields_3D
{
    public class wsp_Fi3d01_FieldFromSolid : GH_Component
    {
        private const string NAME    = "wsp_Fi3d01_Field from Solid";
        private const string NICK    = "Fi3d01";
        private const string DESC    =
            "Converts a Box or solid geometry (Brep, Mesh, or Extrusion) into a WASPer signed distance field.\n" +
            "The field is negative inside the solid and positive outside.\n" +
            "Compatible with all 2.3_Fields_3D components.";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "2.3_Fields_3D";

        public wsp_Fi3d01_FieldFromSolid()
            : base(NAME, NICK, DESC, CAT, SUBCAT) { }

        public override Guid ComponentGuid =>
            new Guid("A1C4E7B2-3F85-4D60-9E21-7C8A5B0D3F94");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d01_Field from Solid.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        // ── Inputs ───────────────────────────────────────────────────────────

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGeometryParameter(
                "solid", "solid",
                "Closed solid geometry to convert into a field.\n" +
                "Accepts: Box, Brep, Mesh, or Extrusion geometry, including items passed through a Geometry container.\n" +
                "The solid must be watertight for correct inside/outside evaluation.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "label", "lbl",
                "Optional label attached to the field for display in downstream components.",
                GH_ParamAccess.item, "");

            pManager[1].Optional = true;
        }

        // ── Outputs ──────────────────────────────────────────────────────────

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "field", "field",
                "WASPer signed distance field.\n" +
                "Negative inside the solid, positive outside.\n" +
                "Wire into field booleans, blending, or iso-surface extraction.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "info", "info",
                "Field diagnostics: geometry type, bounding box, and evaluation note.",
                GH_ParamAccess.item);
        }

        // ── Solve ────────────────────────────────────────────────────────────

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            IGH_GeometricGoo goo   = null;
            string           label = "";

            if (!DA.GetData(0, ref goo) || goo == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No solid geometry provided.");
                return;
            }

            DA.GetData(1, ref label);

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

            WasperField field  = null;
            string      geoTag = "?";
            string evaluatorTag = "ClosestPoint + IsPointInside (on-demand, no pre-sampling)";
            object scriptValue = goo.ScriptVariable();

            Box box = Box.Unset;
            if (goo is GH_Box ghBox)
                box = ghBox.Value;
            else if (scriptValue is Box scriptBox)
                box = scriptBox;

            if (box.IsValid)
            {
                field = WasperField.FromBox(box, label);
                geoTag = "Box";
                evaluatorTag = "Analytic oriented-box SDF (exact, on-demand)";
            }

            // ── Try Brep ─────────────────────────────────────────────────────
            Brep brep = null;
            if (goo is GH_Brep ghBrep)
                brep = ghBrep.Value;
            else if (scriptValue is Brep scriptBrep)
                brep = scriptBrep;
            else if (scriptValue is Extrusion extrusion)
            {
                brep = extrusion.ToBrep();
                geoTag = "Extrusion";
            }

            if (field == null && brep != null)
            {
                if (!brep.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Brep is not valid.");
                    return;
                }
                if (!brep.IsSolid)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Brep is not a closed solid — inside/outside evaluation may be incorrect.");

                field  = WasperField.FromBrep(brep, tol, label);
                if (geoTag == "?") geoTag = "Brep";
            }

            // ── Try Mesh ─────────────────────────────────────────────────────
            if (field == null)
            {
                Mesh mesh = null;
                if (goo is GH_Mesh ghMesh)
                    mesh = ghMesh.Value;
                else if (scriptValue is Mesh scriptMesh)
                    mesh = scriptMesh;

                if (mesh != null)
                {
                    if (!mesh.IsValid)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Mesh is not valid.");
                        return;
                    }
                    if (!mesh.IsClosed)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                            "Mesh is not closed — inside/outside evaluation may be incorrect.");

                    field  = WasperField.FromMesh(mesh, tol, label);
                    geoTag = "Mesh";
                }
            }

            if (field == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Input must be a valid Box, Brep, Mesh, or Extrusion solid.");
                return;
            }

            var bb   = field.Domain;
            string info =
                $"Field from Solid\n" +
                $"geometry type  : {geoTag}\n" +
                $"label          : {(string.IsNullOrEmpty(label) ? "(none)" : label)}\n" +
                $"domain min     : {bb.Min.X:F3}, {bb.Min.Y:F3}, {bb.Min.Z:F3}\n" +
                $"domain max     : {bb.Max.X:F3}, {bb.Max.Y:F3}, {bb.Max.Z:F3}\n" +
                $"domain size    : {bb.Diagonal.X:F3} x {bb.Diagonal.Y:F3} x {bb.Diagonal.Z:F3}\n" +
                $"evaluator      : {evaluatorTag}\n" +
                $"tolerance      : {tol}";

            DA.SetData(0, new WasperFieldGoo(field));
            DA.SetData(1, info);

            Message = $"{geoTag} field";
        }
    }
}
