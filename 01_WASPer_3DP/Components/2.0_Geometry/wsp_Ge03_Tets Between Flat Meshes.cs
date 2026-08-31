#region Component Description
/*
    Component Name:
      wsp_Ge03_Tets Between Flat Meshes

    Description:
      Builds volumetric “in-between” tetrahedra by interpolating between two meshes
      (mesh_A ? mesh_B) over a number of discrete layers.

      Requirements / assumptions:
        - Best results when mesh_A and mesh_B share topology (same vertex & face indices).
        - If topology differs, the component will still run but results may be meaningless.

      Algorithm summary:
        1) Precompute interpolated vertex positions for layer 0..L (0=A, L=B).
        2) For each face and for each layer segment k?k+1:
             - triangle face: build triangular prism and split into 3 tets
             - quad face: build hexa cell and split into 5 tets
        3) Output one Mesh per tet (each tet is a closed 4-face mesh)

      Performance:
        - Uses AUTO parallel processing when workload is large enough.
        - Auto decision based on estimated number of generated tets.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._3_Geometry
{
    public class wsp_Ge03_Tets_Between_Flat_Meshes : GH_Component
    {
        private readonly string _versionTag;

        // ------------------------------------------------------------------------
        // ctor
        // ------------------------------------------------------------------------
        public wsp_Ge03_Tets_Between_Flat_Meshes()
          : base(
              "wsp_Ge03_Tets Between Meshes",
              "BetweenTets",
              "Interpolates between two meshes to build layered cells and splits them into tetrahedra.\n" +
              "The meshes need to have the same topology for the tets to be generated correctly",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "2.0_Geometry")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            this.Message = _versionTag;
        }

        public override Guid ComponentGuid
        {
            // TODO: generate your own GUID (Tools > Create GUID in VS)
            get { return new Guid("A7B7F6B2-9B7C-4F06-9D77-0A9D21A4E0F1"); }
        }

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                // If you add an icon resource, update the manifest resource string below.
                // Otherwise return null and GH will show the default puzzle icon.
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                      "WASPer_3DP.Resources.Icons.wsp_Ge03_Tets Between Meshes.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { return null; }
            }
        }

        // ------------------------------------------------------------------------
        // IO
        // ------------------------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddMeshParameter(
              "mesh_A",
              "mesh_A",
              "Start mesh (layer 0). Ideally shares topology with mesh_B.",
              GH_ParamAccess.item);

            pManager.AddMeshParameter(
              "mesh_B",
              "mesh_B",
              "End mesh (layer L). Ideally shares topology with mesh_A.",
              GH_ParamAccess.item);

            pManager.AddIntegerParameter(
              "layers",
              "layers",
              "Number of interpolation layers between A and B.\n" +
              "layers=1 builds cells directly between A and B.\n" +
              "Higher values add intermediate layers.",
              GH_ParamAccess.item,
              5);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
              "tets",
              "tets",
              "List of tetrahedron meshes (one Mesh per tet).",
              GH_ParamAccess.list);

            pManager.AddIntegerParameter(
              "count",
              "n",
              "Number of tetrahedra generated.",
              GH_ParamAccess.item);
        }

        // ------------------------------------------------------------------------
        // Solve
        // ------------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Mesh meshA = null;
            Mesh meshB = null;
            int layers = 0;

            if (!DA.GetData(0, ref meshA)) return;
            if (!DA.GetData(1, ref meshB)) return;
            if (!DA.GetData(2, ref layers)) layers = 5;

            // Safe defaults
            if (meshA == null || meshB == null)
            {
                DA.SetDataList(0, new List<Mesh>());
                DA.SetData(1, 0);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or both meshes are null.");
                return;
            }

            if (!meshA.IsValid || !meshB.IsValid)
            {
                DA.SetDataList(0, new List<Mesh>());
                DA.SetData(1, 0);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid meshes.");
                return;
            }

            layers = Math.Max(1, layers);

            // Topology check
            if (meshA.Faces.Count != meshB.Faces.Count || meshA.Vertices.Count != meshB.Vertices.Count)
            {
                AddRuntimeMessage(
                  GH_RuntimeMessageLevel.Warning,
                  "Meshes do not share topology (faces/vertices counts differ). " +
                  "Proceeding may yield nonsense. Consider remeshing B onto A / ensuring correspondence.");
            }

            // Prepare
            int vCount = Math.Min(meshA.Vertices.Count, meshB.Vertices.Count);
            int fCount = Math.Min(meshA.Faces.Count, meshB.Faces.Count);

            if (vCount < 4 || fCount < 1)
            {
                DA.SetDataList(0, new List<Mesh>());
                DA.SetData(1, 0);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Meshes have insufficient vertices/faces.");
                return;
            }

            // Count tri/quads (for better work estimation)
            int triFaces = 0, quadFaces = 0;
            for (int i = 0; i < fCount; i++)
            {
                if (meshA.Faces[i].IsQuad) quadFaces++;
                else triFaces++;
            }

            // Estimate number of tets to decide auto-parallel
            long tet_est = (long)layers * ((long)triFaces * 3L + (long)quadFaces * 5L);

            bool useParallel =
              Environment.ProcessorCount >= 4 &&
              tet_est >= 20000; // tune threshold as needed

            // Precompute layered vertex positions (layer 0 = A, layer L = B)
            var layered = new List<Point3d[]>(layers + 1);
            for (int k = 0; k <= layers; k++)
            {
                double t = (double)k / (double)layers;
                var arr = new Point3d[vCount];
                for (int i = 0; i < vCount; i++)
                {
                    Point3d pa = (Point3d)meshA.Vertices[i];
                    Point3d pb = (Point3d)meshB.Vertices[i];
                    arr[i] = Interp(pa, pb, t);
                }
                layered.Add(arr);
            }

            // Output accumulator
            int cap = (tet_est > int.MaxValue) ? int.MaxValue : (int)tet_est;
            var outTets = new List<Mesh>(Math.Max(16, cap));

            if (useParallel)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Auto-parallel ON (tet_est ˜ {tet_est:n0})");

                object mergeLock = new object();

                Parallel.For(
                  0,
                  fCount,
                  // thread-local init
                  () => new List<Mesh>(Math.Max(32, (int)Math.Min(4096, Math.Max(1, tet_est / Math.Max(1, fCount))))),
                  // loop body
                  (fi, state, local) =>
                  {
                      BuildFaceCells(fi, meshA, layered, layers, local);
                      return local;
                  },
                  // merge
                  local =>
                  {
                      if (local == null || local.Count == 0) return;
                      lock (mergeLock) outTets.AddRange(local);
                  });
            }
            else
            {
                for (int fi = 0; fi < fCount; fi++)
                    BuildFaceCells(fi, meshA, layered, layers, outTets);
            }

            DA.SetDataList(0, outTets);
            DA.SetData(1, outTets.Count);
        }

        // ------------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------------

        private static void BuildFaceCells(
          int fi,
          Mesh meshA,
          List<Point3d[]> layered,
          int layers,
          List<Mesh> acc)
        {
            var fa = meshA.Faces[fi];
            bool isQuad = fa.IsQuad;

            // Face vertex indices
            int a = fa.A;
            int b = fa.B;
            int c = fa.C;
            int d = isQuad ? fa.D : -1;

            for (int k = 0; k < layers; k++)
            {
                var L0 = layered[k];
                var L1 = layered[k + 1];

                if (!isQuad)
                {
                    // Triangular prism between L0 and L1 -> 3 tets (simple pattern)
                    Point3d a0 = L0[a], b0 = L0[b], c0 = L0[c];
                    Point3d a1 = L1[a], b1 = L1[b], c1 = L1[c];

                    AddTet(acc, a0, b0, c0, a1);
                    AddTet(acc, b0, c0, a1, b1);
                    AddTet(acc, c0, a1, b1, c1);
                }
                else
                {
                    // Hex cell between L0 and L1 -> 5 tets (common split)
                    Point3d a0 = L0[a], b0 = L0[b], c0 = L0[c], d0 = L0[d];
                    Point3d a1 = L1[a], b1 = L1[b], c1 = L1[c], d1 = L1[d];

                    AddTet(acc, a0, b0, d0, a1);
                    AddTet(acc, b0, c0, d0, c1);
                    AddTet(acc, a1, b0, b1, c1);
                    AddTet(acc, a1, d0, c1, d1);
                    AddTet(acc, a1, b0, c1, d0);
                }
            }
        }

        private static Point3d Interp(Point3d a, Point3d b, double t)
        {
            return new Point3d(
              a.X + (b.X - a.X) * t,
              a.Y + (b.Y - a.Y) * t,
              a.Z + (b.Z - a.Z) * t);
        }

        private static void AddTet(List<Mesh> acc, Point3d p0, Point3d p1, Point3d p2, Point3d p3)
        {
            var m = new Mesh();

            int i0 = m.Vertices.Add(p0);
            int i1 = m.Vertices.Add(p1);
            int i2 = m.Vertices.Add(p2);
            int i3 = m.Vertices.Add(p3);

            // 4 triangular faces
            m.Faces.AddFace(i0, i1, i2);
            m.Faces.AddFace(i0, i1, i3);
            m.Faces.AddFace(i1, i2, i3);
            m.Faces.AddFace(i2, i0, i3);

            m.Normals.ComputeNormals();
            m.Compact();

            acc.Add(m);
        }
    }
}
