// wsp_Fi3d05_Transform Field.cs
// WASPer_3DP — Subcategory: 2.3_Fields_3D
//
// Applies a Grasshopper Transform (rotation, translation, mirror, uniform scale…)
// to one or more WasperFields.
//
// How it works — inverse pull-back:
//   A WasperField evaluator is a function f : Point3d → double.  To "move" the
//   field with a transform T, we do NOT move the evaluator.  Instead, for every
//   query point q in the new (transformed) space we un-do the transform first:
//
//       g(q)  =  s · f( T⁻¹ · q )
//
//   where s is the uniform-scale correction (see below).  This is the standard
//   pull-back of a scalar field through a coordinate change.
//
// SDF value preservation:
//   • Rigid transforms (rotation + translation):  |det T| = 1.  s = 1.
//     SDF values (= distances) are preserved exactly.
//   • Uniform scale by factor k:  |det T| = k³.  s = k.
//     After scaling by k, distances scale proportionally, so multiplying by s
//     restores the correct distance field.
//   • Non-uniform scale / shear:  distances are distorted.  s = ∛|det T| is used
//     as an isotropic approximation.  The output is no longer a true SDF but is
//     still useful for iso-surface extraction and boolean operations.
//     A runtime warning is issued so the user is aware.
//
// Domain transformation:
//   The field's Domain (BoundingBox) is transformed by T.  The 8 corners of the
//   original box are mapped through T and a new axis-aligned BoundingBox is fitted.
//   This is a conservative bound — correct for rigid & uniform-scale transforms;
//   may be larger than the true domain for shear/non-uniform scale.

using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_3_Fields_3D
{
    public class wsp_Fi3d05_TransformField : GH_Component
    {
        private const string NAME    = "wsp_Fi3d05_Transform Field";
        private const string NICK    = "Fi3d05";
        private const string DESC    =
            "Applies a Grasshopper Transform (rotation, translation, mirror, uniform scale…) " +
            "to one or more WasperFields via inverse pull-back.\n" +
            "SDF values are preserved exactly for rigid transforms; corrected for uniform scale.";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "2.3_Fields_3D";

        public wsp_Fi3d05_TransformField()
            : base(NAME, NICK, DESC, CAT, SUBCAT) { }

        public override Guid ComponentGuid =>
            new Guid("D9F3A215-6B4C-4E8A-B2F7-3C1D5E0A8B46");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d05_Transform Field.png"))
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
            pManager.AddGenericParameter(
                "field", "field",
                "One or more WasperFields to transform (list, flattened).",
                GH_ParamAccess.list);
            pManager[0].DataMapping = GH_DataMapping.Flatten;

            pManager.AddTransformParameter(
                "transform", "T",
                "The Grasshopper Transform to apply (rotation, translation, mirror, uniform scale…).\n" +
                "Use any standard GH Transform component (Move, Rotate, Mirror, Scale, Orient, etc.).",
                GH_ParamAccess.item);
        }

        // ── Outputs ──────────────────────────────────────────────────────────

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "field_trans", "field",
                "Transformed WasperFields — one per input field, in the same order.",
                GH_ParamAccess.list);
        }

        // ── Solve ────────────────────────────────────────────────────────────

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Read inputs ──────────────────────────────────────────────────
            var fieldGoos = new List<IGH_Goo>();
            if (!DA.GetDataList(0, fieldGoos)) return;

            Transform xform = Transform.Identity;
            if (!DA.GetData(1, ref xform)) return;

            // ── Validate transform ───────────────────────────────────────────
            if (!xform.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "The supplied Transform is invalid.");
                return;
            }

            // Compute inverse transform — required for the pull-back.
            Transform inv;
            if (!xform.TryGetInverse(out inv))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not invert the supplied Transform. " +
                    "Make sure the transform is not singular (e.g. a zero-scale).");
                return;
            }

            // ── SDF scale correction ─────────────────────────────────────────
            // For a uniform scale by k: |det| = k³, so s = ∛|det|.
            // For rigid (rotation/translation): det ≈ ±1, so s ≈ 1 (no change).
            // For non-uniform transforms: s is the geometric mean of the three
            // axis scales — used as an approximation only.
            double det = xform.Determinant;
            double absDet = Math.Abs(det);
            double scaleCorrection = (absDet > 1e-12) ? Math.Pow(absDet, 1.0 / 3.0) : 1.0;

            // Warn about non-rigid transforms (|det| significantly different from 1)
            bool isRigid = Math.Abs(absDet - 1.0) < 1e-4;
            if (!isRigid)
            {
                double s = scaleCorrection;
                bool isUniformScale = Math.Abs(s * s * s - absDet) < 1e-6 * absDet;

                if (isUniformScale)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        $"Uniform scale detected (factor ≈ {s:F4}). " +
                        "SDF values have been corrected to preserve distance units.");
                }
                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "The transform contains non-uniform scale or shear. " +
                        "SDF distances will be approximate (isotropic correction applied). " +
                        "Iso-surface extraction and boolean operations will still work; " +
                        "exact distance queries will not.");
                }
            }

            // ── Unwrap fields ────────────────────────────────────────────────
            var fields = new List<WasperField>();
            foreach (var g in fieldGoos)
            {
                var f = ExtractField(g);
                if (f != null) fields.Add(f);
            }

            if (fields.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "No valid WasperField inputs were provided.");
                return;
            }

            // ── Build transformed fields ─────────────────────────────────────
            var results = new List<WasperFieldGoo>(fields.Count);

            foreach (var field in fields)
            {
                var transField = WasperFieldOps.Transform(field, xform, inv, scaleCorrection);
                if (transField != null)
                    results.Add(new WasperFieldGoo(transField));
            }

            DA.SetDataList(0, results);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static WasperField ExtractField(IGH_Goo goo)
        {
            if (goo == null) return null;
            if (goo is WasperFieldGoo fg) return fg.Value;
            object val = null;
            if (goo is GH_ObjectWrapper ow) val = ow.Value;
            if (val is WasperField  wf) return wf;
            if (val is WasperFieldGoo wg) return wg.Value;
            return null;
        }
    }
}
