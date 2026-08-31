#region Component Description
/*
Component: wsp_Ge04_Point Distance Checker
Nickname: Pt Dist Check
Category: WASPer_3DP
SubCategory: 2.0_Geometry

GENERAL DESCRIPTION
Checks each point in a pointsA data tree against a list of pointsB and returns
a matching boolean tree. A point is true when it is within tolerance distance of
at least one point in pointsB.

Inputs:
0) pointsA : DataTree<Point3d>
   Points to test. Tree structure is preserved.

1) pointsB : List<Point3d>
   Reference points.

2) tolerance : double
   Distance tolerance. Default: 0.1.

Outputs:
0) pointsA : DataTree<Point3d>
   Pass-through of input pointsA.

1) pts_bool : DataTree<bool>
   True for each pointsA item that is within tolerance of any pointsB item.
*/
#endregion

#region Usings
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;

using Rhino;
using Rhino.Geometry;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion

namespace WASPer_3DP.Components._3_Geometry
{
    public class wsp_Ge04_Point_Distance_Checker : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge04_Point_Distance_Checker()
          : base(
              "wsp_Ge04_Point Distance Checker",
              "Pt Dist Check",
              "Checks whether each point in pointsA is within tolerance of any point in pointsB.",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "2.0_Geometry")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("55F0A1D0-D9F8-4E91-8F0E-4D4928D06C92");

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ge04_Point Distance Checker.png"))
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
                "pointsA",
                "pointsA",
                "Points to check. Tree structure is preserved.",
                GH_ParamAccess.tree);

            pManager.AddPointParameter(
                "pointsB",
                "pointsB",
                "Reference points.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "tolerance",
                "tolerance",
                "Distance tolerance.",
                GH_ParamAccess.item,
                0.1);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter(
                "pointsA",
                "pointsA",
                "Pass-through of input pointsA.",
                GH_ParamAccess.tree);

            pManager.AddBooleanParameter(
                "pts_bool",
                "pts_bool",
                "True when each pointsA item is within tolerance of any pointsB item.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            GH_Structure<GH_Point> pointsATree = null;
            var pointsB = new List<Point3d>();
            double tolerance = 0.1;

            if (!DA.GetDataTree(0, out pointsATree)) return;
            DA.GetDataList(1, pointsB);
            DA.GetData(2, ref tolerance);

            if (tolerance <= RhinoMath.ZeroTolerance)
                tolerance = 0.1;

            var outPoints = new GH_Structure<GH_Point>();
            var outBools = new GH_Structure<GH_Boolean>();

            if (pointsATree == null || pointsATree.PathCount == 0)
            {
                DA.SetDataTree(0, outPoints);
                DA.SetDataTree(1, outBools);
                return;
            }

            var validB = new List<Point3d>();
            foreach (var p in pointsB)
                if (p.IsValid) validB.Add(p);

            RTree rtree = null;
            if (validB.Count > 0)
            {
                rtree = new RTree();
                for (int i = 0; i < validB.Count; i++)
                    rtree.Insert(validB[i], i);
            }

            double tol2 = tolerance * tolerance;

            for (int b = 0; b < pointsATree.PathCount; b++)
            {
                GH_Path path = pointsATree.Paths[b];
                IList branch = pointsATree.get_Branch(path);
                outPoints.EnsurePath(path);
                outBools.EnsurePath(path);

                if (branch == null) continue;

                foreach (object item in branch)
                {
                    GH_Point ghp = item as GH_Point;
                    if (ghp == null)
                    {
                        outBools.Append(new GH_Boolean(false), path);
                        continue;
                    }

                    Point3d pt = ghp.Value;
                    outPoints.Append(new GH_Point(pt), path);
                    outBools.Append(new GH_Boolean(IsNearAny(pt, validB, rtree, tolerance, tol2)), path);
                }
            }

            DA.SetDataTree(0, outPoints);
            DA.SetDataTree(1, outBools);
        }

        private static bool IsNearAny(Point3d pt, List<Point3d> refs, RTree rtree, double tol, double tol2)
        {
            if (!pt.IsValid || refs == null || refs.Count == 0 || rtree == null)
                return false;

            bool found = false;
            BoundingBox box = new BoundingBox(
                new Point3d(pt.X - tol, pt.Y - tol, pt.Z - tol),
                new Point3d(pt.X + tol, pt.Y + tol, pt.Z + tol));

            rtree.Search(box, (sender, args) =>
            {
                if (found) return;
                int id = args.Id;
                if (id >= 0 && id < refs.Count && pt.DistanceToSquared(refs[id]) <= tol2)
                    found = true;
            });

            return found;
        }
    }
}
