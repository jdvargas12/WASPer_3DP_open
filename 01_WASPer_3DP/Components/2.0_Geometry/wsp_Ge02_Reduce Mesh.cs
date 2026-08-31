#region Component Description
/*
Component: wsp_Ge02_Reduce Mesh
Nickname: Reduce Mesh
Category: WASPer_3DP
SubCategory: 2.0_Geometry

GENERAL DESCRIPTION
Simplifies a mesh using a reduction factor while preserving geometry accuracy.

Inputs:
0) mesh : Mesh
   Input mesh to simplify.

1) reduce_factor : double
   Target face ratio in the range (0, 1].
   1.0 keeps the original face count target, 0.5 targets 50 percent.
   Default: 0.5.

2) allow_distortion : bool
   Allows more aggressive reduction.
   Default: true.

3) accuracy : int
   Mesh reduction accuracy from 1 to 10.
   Default: 10.

4) norm_size : bool
   Normalize mesh size during reduction.
   Default: true.

5) thread_thresh : int
   Face-count threshold for threaded reduction.
   Default: 1000.

Outputs:
0) S : Mesh
   Simplified mesh.
*/
#endregion

#region Usings
using System;
using System.Drawing;

using Rhino;
using Rhino.Geometry;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._3_Geometry
{
    public class wsp_Ge02_Reduce_Mesh : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge02_Reduce_Mesh()
          : base(
              "wsp_Ge02_Reduce Mesh",
              "Reduce Mesh",
              "Simplifies a mesh using a reduction factor while preserving geometry accuracy.",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "2.0_Geometry")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("27B7CB79-EB8E-4A93-A40C-9F6B4C992E6C");

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.08_MeshrRed.png"))
                    {
                        return stream != null ? new Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "mesh",
                "mesh",
                "Input mesh to simplify.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "reduce_factor",
                "reduce_factor",
                "Target face ratio in the range (0, 1]. 1 = no target reduction, 0.5 = 50 percent target.",
                GH_ParamAccess.item,
                0.5);

            pManager.AddBooleanParameter(
                "allow_distortion",
                "allow_distortion",
                "Allow distortion for more aggressive reduction.",
                GH_ParamAccess.item,
                true);

            pManager.AddIntegerParameter(
                "accuracy",
                "accuracy",
                "Reduction accuracy from 1 to 10.",
                GH_ParamAccess.item,
                10);

            pManager.AddBooleanParameter(
                "norm_size",
                "norm_size",
                "Normalize mesh size during reduction.",
                GH_ParamAccess.item,
                true);

            pManager.AddIntegerParameter(
                "thread_thresh",
                "thread_thresh",
                "Face-count threshold for threaded reduction.",
                GH_ParamAccess.item,
                1000);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "S",
                "S",
                "Simplified mesh.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            Mesh mesh = null;
            double reduceFactor = 0.5;
            bool allowDistortion = true;
            int accuracy = 10;
            bool normalizeSize = true;
            int threadThreshold = 1000;

            if (!DA.GetData(0, ref mesh)) return;
            DA.GetData(1, ref reduceFactor);
            DA.GetData(2, ref allowDistortion);
            DA.GetData(3, ref accuracy);
            DA.GetData(4, ref normalizeSize);
            DA.GetData(5, ref threadThreshold);

            if (mesh == null || !mesh.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input mesh is null or invalid.");
                return;
            }

            if (reduceFactor <= 0.0 || reduceFactor > 1.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "reduce_factor must be greater than 0 and less than or equal to 1.");
                return;
            }

            accuracy = Math.Max(1, Math.Min(10, accuracy));
            threadThreshold = Math.Max(0, threadThreshold);

            Mesh simplifiedMesh = mesh.DuplicateMesh();
            int originalFaceCount = mesh.Faces.Count;

            if (originalFaceCount <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input mesh has no faces.");
                return;
            }

            int targetFaceCount = Math.Max(1, (int)Math.Round(originalFaceCount * reduceFactor));
            bool success = simplifiedMesh.Reduce(
                targetFaceCount,
                allowDistortion,
                accuracy,
                normalizeSize,
                originalFaceCount >= threadThreshold);

            if (!success || !simplifiedMesh.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Mesh simplification failed or resulted in an invalid mesh.");
                return;
            }

            DA.SetData(0, simplifiedMesh);
        }
    }
}
