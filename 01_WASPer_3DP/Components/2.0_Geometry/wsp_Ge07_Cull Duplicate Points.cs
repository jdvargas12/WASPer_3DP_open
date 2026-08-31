#region Component Description
/*
Component: wsp_Ge07_Cull Duplicate Points
Nickname: Cull Dup Pts
Category: WASPer_3DP
SubCategory: 2.0_Geometry

GENERAL DESCRIPTION
Removes duplicate points from a list using a distance tolerance. The first point
in each duplicate cluster is kept, and the indices of culled points are output.

Inputs:
0) points : List<Point3d>
   Points to process.

1) tolerance : double
   Duplicate tolerance. Default: 0.5.

Outputs:
0) unique_points : List<Point3d>
   Points kept after duplicate removal.

1) cull_indices : List<int>
   Original indices removed as duplicates.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;

using Rhino;
using Rhino.Geometry;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._3_Geometry
{
    public class wsp_Ge07_Cull_Duplicate_Points : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge07_Cull_Duplicate_Points()
          : base(
              "wsp_Ge07_Cull Duplicate Points",
              "Cull Dup Pts",
              "Culls duplicate points using a distance tolerance and returns the removed original indices.",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "2.0_Geometry")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("9B970BF8-BD3C-4D4C-9D47-A6E27141D5B1");

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ge07_Cull Duplicate Points.png"))
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
            pManager.AddPointParameter(
                "points",
                "points",
                "Points to process.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "tolerance",
                "tolerance",
                "Duplicate distance tolerance.",
                GH_ParamAccess.item,
                0.5);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter(
                "unique_points",
                "unique_points",
                "Points kept after duplicate removal.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "cull_indices",
                "cull_indices",
                "Original indices removed as duplicates.",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var points = new List<Point3d>();
            double tolerance = 0.5;

            DA.GetDataList(0, points);
            DA.GetData(1, ref tolerance);

            if (tolerance <= RhinoMath.ZeroTolerance)
                tolerance = 0.5;

            var uniquePoints = new List<Point3d>();
            var culledIndices = new List<int>();

            if (points == null || points.Count == 0)
            {
                DA.SetDataList(0, uniquePoints);
                DA.SetDataList(1, culledIndices);
                return;
            }

            var rtree = new RTree();
            double tol2 = tolerance * tolerance;

            for (int i = 0; i < points.Count; i++)
            {
                Point3d pt = points[i];
                if (!pt.IsValid)
                {
                    culledIndices.Add(i);
                    continue;
                }

                bool duplicate = false;
                BoundingBox box = new BoundingBox(
                    new Point3d(pt.X - tolerance, pt.Y - tolerance, pt.Z - tolerance),
                    new Point3d(pt.X + tolerance, pt.Y + tolerance, pt.Z + tolerance));

                rtree.Search(box, (sender, args) =>
                {
                    if (duplicate) return;
                    int id = args.Id;
                    if (id >= 0 && id < uniquePoints.Count && pt.DistanceToSquared(uniquePoints[id]) <= tol2)
                        duplicate = true;
                });

                if (duplicate)
                {
                    culledIndices.Add(i);
                }
                else
                {
                    int uniqueIndex = uniquePoints.Count;
                    uniquePoints.Add(pt);
                    rtree.Insert(pt, uniqueIndex);
                }
            }

            DA.SetDataList(0, uniquePoints);
            DA.SetDataList(1, culledIndices);
        }
    }
}
