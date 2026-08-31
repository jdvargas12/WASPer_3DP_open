#region Component Description
/*
Component: wsp_Ge01_Reduce Mesh (Fast)
Nickname: Fast Mesh Reduce
Category: WASPer_3DP
SubCategory: 2.0_Geometry

GENERAL DESCRIPTION
Simplifies a mesh with fixed fast/high-accuracy reduction settings.

Inputs:
0) mesh : Mesh
   Input mesh to simplify.

Outputs:
0) simple_mesh : Mesh
   Reduced mesh.
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
    public class wsp_Ge01_Reduce_Mesh_Fast : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge01_Reduce_Mesh_Fast()
          : base(
              "wsp_Ge01_Reduce Mesh (Fast)",
              "Fast Mesh Reduce",
              "Simplifies a mesh with fixed fast/high-accuracy reduction settings.",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "2.0_Geometry")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("4D2D7250-4B8C-4C3E-8DFE-3C01E1FEF9E4");

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
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "simple_mesh",
                "simple_mesh",
                "Reduced mesh.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            Mesh mesh = null;
            if (!DA.GetData(0, ref mesh)) return;

            if (mesh == null || !mesh.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input mesh is null or invalid.");
                return;
            }

            Mesh reducedMesh = mesh.DuplicateMesh();

            int originalFaceCount = mesh.Faces.Count;
            if (originalFaceCount <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input mesh has no faces.");
                return;
            }

            int targetFaceCount = Math.Max(1, (int)Math.Round(originalFaceCount * 0.65));
            bool success = reducedMesh.Reduce(
                targetFaceCount,
                true,
                10,
                true,
                originalFaceCount > 1000);

            if (!success || !reducedMesh.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Mesh simplification failed or resulted in an invalid mesh.");
                return;
            }

            DA.SetData(0, reducedMesh);
        }
    }
}
