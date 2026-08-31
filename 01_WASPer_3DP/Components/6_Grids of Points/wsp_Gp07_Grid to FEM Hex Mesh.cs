// wsp_Gp07_Grid to FEM Hex Mesh.cs
// WASPer_3DP - Subcategory: 6_Grids of points
//
// Converts a regular WASPer point grid into solver-ready hexahedral FEM data.

#region Component documentation
/*
COMPONENT
    wsp_Gp07_Grid to FEM Hex Mesh
    Nickname: Grid Hex FEM
    Category: WASPerformance
    Subcategory: 6_Grids of points

PURPOSE
    Creates a reusable hexahedral finite-element mesh from a regular material
    point grid. The main outputs are solver data, not display geometry.

    This component is intended as the bridge between WASPer grid/material
    components and future external FEM backends such as TensorMesh.

INPUTS
    0  mat_pts   DataTree<Point3d>. Material point grid. Each branch is one
                 material. MaterialID = branch index + 1.
    1  d         int. Rounding decimals for node indexing and grid detection.
    2  out_mesh  bool. If true, also builds a Rhino display mesh of hexahedra.
                 Default false to avoid heavy previews.
    3  bound_hex bool. If true, the display mesh contains only the outermost
                 hexahedra. If false, it contains every hexahedron. Default true.

OUTPUTS
    0  nodes           List<Point3d>. Unique FEM nodes.
    1  hex             DataTree<int>. Hex connectivity, path {element}, eight
                       node indices per branch:
                       n000,n100,n110,n010,n001,n101,n111,n011.
    2  elem_matID      List<int>. Material ID per hex element, assigned by
                       majority vote from the eight corner nodes.
    3  elem_centers    List<Point3d>. Element center points.
    4  boundary_nodes  DataTree<int>. Boundary node groups:
                       {0}=x_min, {1}=x_max, {2}=y_min, {3}=y_max,
                       {4}=z_min, {5}=z_max, {6}=outer.
    5  boundary_faces  DataTree<int>. Boundary face groups. Each branch path is
                       {group;face}. Values are:
                       element_index, n0, n1, n2, n3.
    6  boundary_names  List<string>. Names for boundary group indices.
    7  hex_mesh        Mesh. Optional hexahedron visualization. Empty unless
                       out_mesh is true; filtered by bound_hex.
    8  summary         string. Counts, spacing, warnings, and output contract.

NOTES
    - A hex element is created only when all eight corner nodes exist.
    - With bound_hex=true, the display mesh includes only elements touching an
      exposed boundary face. This is intended as the fast preview mode.
    - The component assumes a regular orthogonal grid. It does not tetrahedralize
      arbitrary Rhino meshes.
    - Solver components should consume nodes/hex/elem_matID/boundary_* outputs.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

namespace WASPer_3DP.Components._6_Grids_of_points
{
    public class wsp_Gp07_GridToFemHexMesh : GH_Component
    {
        private readonly string _versionTag;

        private static readonly string[] BoundaryNames =
        {
            "x_min",
            "x_max",
            "y_min",
            "y_max",
            "z_min",
            "z_max",
            "outer"
        };

        public wsp_Gp07_GridToFemHexMesh()
            : base(
                "wsp_Gp07_Grid to FEM Hex Mesh",
                "Grid Hex FEM",
                "Converts a regular material point grid into solver-ready hexahedral FEM data. " +
                "Outputs nodes, hex connectivity, element material IDs, boundary groups, and optional exposed shell mesh.",
                global::WASPer_3DP.WASPerPalette.Performance,
                "6_Grids of points")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("4E2E58C1-9B0C-42C7-A739-1E4B2DD05C49");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Gp07_Grid to FEM Hex Mesh.png"))
                    {
                        if (s != null) return new System.Drawing.Bitmap(s);
                    }

                    using (var s = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.13_Grid_from_mesh.png"))
                    {
                        if (s != null) return new System.Drawing.Bitmap(s);
                    }
                }
                catch { }

                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddPointParameter(
                "mat_pts",
                "mat_pts",
                "Material point grid as a DataTree. Each branch is one material; MaterialID = branch index + 1.",
                GH_ParamAccess.tree);

            pManager.AddIntegerParameter(
                "decimals",
                "d",
                "Rounding decimals used for node indexing and grid-spacing detection.",
                GH_ParamAccess.item,
                3);

            pManager.AddBooleanParameter(
                "out_mesh",
                "mesh?",
                "Build an optional Rhino display mesh of the hexahedral elements. Default false to avoid heavy previews.",
                GH_ParamAccess.item,
                false);

            pManager.AddBooleanParameter(
                "bound_hex",
                "bound_hex",
                "When mesh? is true, show only the outermost hexahedra for a faster preview. False shows every hexahedron.",
                GH_ParamAccess.item,
                true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter(
                "nodes",
                "nodes",
                "Unique FEM node coordinates.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "hex_elements",
                "hex",
                "Hex element connectivity. Tree path {element}; each branch has eight node indices: n000,n100,n110,n010,n001,n101,n111,n011.",
                GH_ParamAccess.tree);

            pManager.AddIntegerParameter(
                "element_material_ids",
                "elem_matID",
                "Material ID per hex element, assigned by majority vote from the eight corner nodes.",
                GH_ParamAccess.list);

            pManager.AddPointParameter(
                "element_centers",
                "elem_centers",
                "Center point of each hex element.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "boundary_nodes",
                "b_nodes",
                "Boundary node groups: {0}=x_min, {1}=x_max, {2}=y_min, {3}=y_max, {4}=z_min, {5}=z_max, {6}=outer.",
                GH_ParamAccess.tree);

            pManager.AddIntegerParameter(
                "boundary_faces",
                "b_faces",
                "Boundary face groups. Path {group;face}; values are element_index,n0,n1,n2,n3.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "boundary_names",
                "b_names",
                "Boundary group names matching b_nodes and b_faces: x_min,x_max,y_min,y_max,z_min,z_max,outer.",
                GH_ParamAccess.list);

            pManager.AddMeshParameter(
                "hex_mesh",
                "hex_mesh",
                "Optional hexahedron visualization. With bound_hex=true, contains only the outermost hexahedra.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "summary",
                "summary",
                "Text summary of FEM mesh counts, spacing, boundary groups, and warnings.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            GH_Structure<GH_Point> matTree = null;
            int decimals = 3;
            bool outputMesh = false;
            bool boundaryHexOnly = true;

            if (!DA.GetDataTree(0, out matTree) || matTree == null || matTree.PathCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "mat_pts is empty. Provide a regular material point grid.");
                SetEmptyOutputs(DA, "No material points were provided.");
                return;
            }

            DA.GetData(1, ref decimals);
            DA.GetData(2, ref outputMesh);
            DA.GetData(3, ref boundaryHexOnly);

            decimals = WasperGridTools.Clamp(decimals, 0, 10);

            var nodes = new List<Point3d>();
            var nodeMatIds = new List<int>();
            var indexByKey = new Dictionary<WasperGridKey, int>();
            int duplicateCount = 0;
            int invalidCount = 0;

            for (int b = 0; b < matTree.PathCount; b++)
            {
                var branch = matTree.Branches[b];
                if (branch == null) continue;

                int matId = b + 1;
                foreach (var ghp in branch)
                {
                    if (ghp == null || !ghp.Value.IsValid)
                    {
                        invalidCount++;
                        continue;
                    }

                    Point3d p = WasperGridTools.RoundPoint(ghp.Value, decimals);
                    WasperGridKey key = WasperGridTools.Key(p, decimals);
                    if (indexByKey.ContainsKey(key))
                    {
                        duplicateCount++;
                        continue;
                    }

                    indexByKey[key] = nodes.Count;
                    nodes.Add(p);
                    nodeMatIds.Add(matId);
                }
            }

            if (nodes.Count < 8)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "At least eight unique grid points are required to create a hex element.");
                SetEmptyOutputs(DA, "Not enough unique nodes to form a hexahedral element.");
                return;
            }

            WasperGridSpacing spacing = WasperGridTools.EstimateMedianSpacing(nodes, decimals);
            double dx = spacing.Dx;
            double dy = spacing.Dy;
            double dz = spacing.Dz;

            if (dx <= 1e-12 || dy <= 1e-12 || dz <= 1e-12)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not detect positive dx/dy/dz spacing. The input must be a 3D regular grid.");
                SetEmptyOutputs(DA, "Could not detect valid grid spacing.");
                return;
            }

            double xMin = nodes.Min(p => p.X);
            double xMax = nodes.Max(p => p.X);
            double yMin = nodes.Min(p => p.Y);
            double yMax = nodes.Max(p => p.Y);
            double zMin = nodes.Min(p => p.Z);
            double zMax = nodes.Max(p => p.Z);
            double boundaryTol = Math.Max(Math.Max(dx, dy), dz) * 1e-6 + Math.Pow(10.0, -decimals) * 2.0;

            var candidates = new HexCandidate[nodes.Count];
            Parallel.For(0, nodes.Count, i =>
            {
                Point3d p = nodes[i];

                int n000 = i;
                int n100 = FindNode(indexByKey, p.X + dx, p.Y, p.Z, decimals);
                int n110 = FindNode(indexByKey, p.X + dx, p.Y + dy, p.Z, decimals);
                int n010 = FindNode(indexByKey, p.X, p.Y + dy, p.Z, decimals);
                int n001 = FindNode(indexByKey, p.X, p.Y, p.Z + dz, decimals);
                int n101 = FindNode(indexByKey, p.X + dx, p.Y, p.Z + dz, decimals);
                int n111 = FindNode(indexByKey, p.X + dx, p.Y + dy, p.Z + dz, decimals);
                int n011 = FindNode(indexByKey, p.X, p.Y + dy, p.Z + dz, decimals);

                if (n100 < 0 || n110 < 0 || n010 < 0 || n001 < 0 || n101 < 0 || n111 < 0 || n011 < 0)
                    return;

                int[] hex = { n000, n100, n110, n010, n001, n101, n111, n011 };
                candidates[i] = new HexCandidate(
                    true,
                    hex,
                    MajorityMaterial(hex, nodeMatIds),
                    new Point3d(p.X + 0.5 * dx, p.Y + 0.5 * dy, p.Z + 0.5 * dz));
            });

            var hexes = new List<int[]>();
            var elemMatIds = new List<int>();
            var elemCenters = new List<Point3d>();
            var faceCandidates = new List<FaceRecord>();

            for (int i = 0; i < candidates.Length; i++)
            {
                HexCandidate candidate = candidates[i];
                if (!candidate.IsValid) continue;

                int elemIndex = hexes.Count;
                hexes.Add(candidate.Hex);
                elemMatIds.Add(candidate.MaterialID);
                elemCenters.Add(candidate.Center);
                AddFaces(faceCandidates, elemIndex, candidate.Hex, nodes);
            }

            if (hexes.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No complete hex elements were found. Check that mat_pts contains a regular 3D point grid.");
                SetEmptyOutputs(DA, "No complete hexahedral cells were found.");
                return;
            }

            var faceCounts = new Dictionary<FaceKey, int>();
            foreach (FaceRecord f in faceCandidates)
            {
                if (faceCounts.TryGetValue(f.Key, out int count)) faceCounts[f.Key] = count + 1;
                else faceCounts[f.Key] = 1;
            }

            var boundaryFacesByGroup = new List<FaceRecord>[BoundaryNames.Length];
            var boundaryNodesByGroup = new SortedSet<int>[BoundaryNames.Length];
            for (int i = 0; i < BoundaryNames.Length; i++)
            {
                boundaryFacesByGroup[i] = new List<FaceRecord>();
                boundaryNodesByGroup[i] = new SortedSet<int>();
            }

            int exposedFaceCount = 0;
            foreach (FaceRecord f in faceCandidates)
            {
                if (!faceCounts.TryGetValue(f.Key, out int count) || count != 1)
                    continue;

                exposedFaceCount++;
                AddBoundaryFace(6, f, boundaryFacesByGroup, boundaryNodesByGroup);

                int sideGroup = GetGlobalSideGroup(f, xMin, xMax, yMin, yMax, zMin, zMax, boundaryTol);
                if (sideGroup >= 0)
                    AddBoundaryFace(sideGroup, f, boundaryFacesByGroup, boundaryNodesByGroup);
            }

            var hexTree = new DataTree<int>();
            for (int e = 0; e < hexes.Count; e++)
                hexTree.AddRange(hexes[e], new GH_Path(e));

            var boundaryNodeTree = new DataTree<int>();
            for (int g = 0; g < BoundaryNames.Length; g++)
                boundaryNodeTree.AddRange(boundaryNodesByGroup[g], new GH_Path(g));

            var boundaryFaceTree = new DataTree<int>();
            for (int g = 0; g < BoundaryNames.Length; g++)
            {
                for (int f = 0; f < boundaryFacesByGroup[g].Count; f++)
                {
                    FaceRecord face = boundaryFacesByGroup[g][f];
                    var path = new GH_Path(g, f);
                    boundaryFaceTree.Add(face.ElementIndex, path);
                    boundaryFaceTree.Add(face.N0, path);
                    boundaryFaceTree.Add(face.N1, path);
                    boundaryFaceTree.Add(face.N2, path);
                    boundaryFaceTree.Add(face.N3, path);
                }
            }

            var boundaryElementIndices = new HashSet<int>(
                boundaryFacesByGroup[6].Select(face => face.ElementIndex));
            Mesh displayMesh = outputMesh
                ? BuildHexDisplayMesh(hexes, nodes, boundaryHexOnly ? boundaryElementIndices : null)
                : new Mesh();

            var sb = new StringBuilder();
            sb.AppendLine("wsp_Gp07_Grid to FEM Hex Mesh");
            sb.AppendLine("Version: " + _versionTag);
            sb.AppendLine();
            sb.AppendLine("MODEL");
            sb.AppendLine("Input branches: " + matTree.PathCount);
            sb.AppendLine("Unique nodes: " + nodes.Count);
            sb.AppendLine("Hex elements: " + hexes.Count);
            sb.AppendLine("Exposed boundary faces: " + exposedFaceCount);
            sb.AppendLine("Boundary hexahedra: " + boundaryElementIndices.Count);
            sb.AppendLine("Duplicate points ignored: " + duplicateCount);
            sb.AppendLine("Invalid points ignored: " + invalidCount);
            sb.AppendLine("Hex candidate scan: parallel, deterministic node-order flattening");
            sb.AppendLine();
            sb.AppendLine("GRID");
            sb.AppendLine("dx: " + F(dx));
            sb.AppendLine("dy: " + F(dy));
            sb.AppendLine("dz: " + F(dz));
            sb.AppendLine("decimals: " + decimals);
            sb.AppendLine();
            sb.AppendLine("BOUNDARY GROUPS");
            for (int g = 0; g < BoundaryNames.Length; g++)
                sb.AppendLine(g + " " + BoundaryNames[g] + ": nodes=" + boundaryNodesByGroup[g].Count + ", faces=" + boundaryFacesByGroup[g].Count);
            sb.AppendLine();
            sb.AppendLine("OUTPUT CONTRACT");
            sb.AppendLine("hex path {element}: n000,n100,n110,n010,n001,n101,n111,n011.");
            sb.AppendLine("b_faces path {group;face}: element_index,n0,n1,n2,n3.");
            sb.AppendLine("hex_mesh is generated only when out_mesh=true.");
            sb.AppendLine(boundaryHexOnly
                ? "hex_mesh mode: outermost hexahedra only (bound_hex=true)."
                : "hex_mesh mode: all hexahedra (bound_hex=false).");

            DA.SetDataList(0, nodes);
            DA.SetDataTree(1, hexTree);
            DA.SetDataList(2, elemMatIds);
            DA.SetDataList(3, elemCenters);
            DA.SetDataTree(4, boundaryNodeTree);
            DA.SetDataTree(5, boundaryFaceTree);
            DA.SetDataList(6, BoundaryNames);
            DA.SetData(7, displayMesh);
            DA.SetData(8, sb.ToString());
        }

        private static int FindNode(Dictionary<WasperGridKey, int> indexByKey, double x, double y, double z, int decimals)
        {
            var key = WasperGridTools.Key(new Point3d(x, y, z), decimals);
            return indexByKey.TryGetValue(key, out int index) ? index : -1;
        }

        private static int MajorityMaterial(int[] hex, List<int> nodeMatIds)
        {
            var counts = new Dictionary<int, int>();
            foreach (int nodeIndex in hex)
            {
                int matId = nodeMatIds[nodeIndex];
                if (counts.TryGetValue(matId, out int count)) counts[matId] = count + 1;
                else counts[matId] = 1;
            }

            return counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .First()
                .Key;
        }

        private static void AddFaces(List<FaceRecord> faces, int elemIndex, int[] h, List<Point3d> nodes)
        {
            faces.Add(new FaceRecord(elemIndex, h[0], h[3], h[7], h[4], 0, -1, FaceCoordinate(nodes, h[0], h[3], h[7], h[4], 0)));
            faces.Add(new FaceRecord(elemIndex, h[1], h[5], h[6], h[2], 0, 1, FaceCoordinate(nodes, h[1], h[5], h[6], h[2], 0)));
            faces.Add(new FaceRecord(elemIndex, h[0], h[4], h[5], h[1], 1, -1, FaceCoordinate(nodes, h[0], h[4], h[5], h[1], 1)));
            faces.Add(new FaceRecord(elemIndex, h[3], h[2], h[6], h[7], 1, 1, FaceCoordinate(nodes, h[3], h[2], h[6], h[7], 1)));
            faces.Add(new FaceRecord(elemIndex, h[0], h[1], h[2], h[3], 2, -1, FaceCoordinate(nodes, h[0], h[1], h[2], h[3], 2)));
            faces.Add(new FaceRecord(elemIndex, h[4], h[7], h[6], h[5], 2, 1, FaceCoordinate(nodes, h[4], h[7], h[6], h[5], 2)));
        }

        private static double FaceCoordinate(List<Point3d> nodes, int a, int b, int c, int d, int axis)
        {
            if (axis == 0) return 0.25 * (nodes[a].X + nodes[b].X + nodes[c].X + nodes[d].X);
            if (axis == 1) return 0.25 * (nodes[a].Y + nodes[b].Y + nodes[c].Y + nodes[d].Y);
            return 0.25 * (nodes[a].Z + nodes[b].Z + nodes[c].Z + nodes[d].Z);
        }

        private static int GetGlobalSideGroup(
            FaceRecord face,
            double xMin,
            double xMax,
            double yMin,
            double yMax,
            double zMin,
            double zMax,
            double tol)
        {
            if (face.Axis == 0 && face.Sign < 0 && Math.Abs(face.AxisCoordinate - xMin) <= tol) return 0;
            if (face.Axis == 0 && face.Sign > 0 && Math.Abs(face.AxisCoordinate - xMax) <= tol) return 1;
            if (face.Axis == 1 && face.Sign < 0 && Math.Abs(face.AxisCoordinate - yMin) <= tol) return 2;
            if (face.Axis == 1 && face.Sign > 0 && Math.Abs(face.AxisCoordinate - yMax) <= tol) return 3;
            if (face.Axis == 2 && face.Sign < 0 && Math.Abs(face.AxisCoordinate - zMin) <= tol) return 4;
            if (face.Axis == 2 && face.Sign > 0 && Math.Abs(face.AxisCoordinate - zMax) <= tol) return 5;
            return -1;
        }

        private static void AddBoundaryFace(
            int group,
            FaceRecord face,
            List<FaceRecord>[] boundaryFacesByGroup,
            SortedSet<int>[] boundaryNodesByGroup)
        {
            boundaryFacesByGroup[group].Add(face);
            boundaryNodesByGroup[group].Add(face.N0);
            boundaryNodesByGroup[group].Add(face.N1);
            boundaryNodesByGroup[group].Add(face.N2);
            boundaryNodesByGroup[group].Add(face.N3);
        }

        private static Mesh BuildHexDisplayMesh(
            List<int[]> hexes,
            List<Point3d> nodes,
            HashSet<int> includedElements)
        {
            var mesh = new Mesh();

            for (int elementIndex = 0; elementIndex < hexes.Count; elementIndex++)
            {
                if (includedElements != null && !includedElements.Contains(elementIndex))
                    continue;

                int[] h = hexes[elementIndex];
                int v = mesh.Vertices.Count;
                for (int i = 0; i < 8; i++)
                    mesh.Vertices.Add(nodes[h[i]]);

                mesh.Faces.AddFace(v + 0, v + 3, v + 7, v + 4);
                mesh.Faces.AddFace(v + 1, v + 5, v + 6, v + 2);
                mesh.Faces.AddFace(v + 0, v + 4, v + 5, v + 1);
                mesh.Faces.AddFace(v + 3, v + 2, v + 6, v + 7);
                mesh.Faces.AddFace(v + 0, v + 1, v + 2, v + 3);
                mesh.Faces.AddFace(v + 4, v + 7, v + 6, v + 5);
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();
            return mesh;
        }

        private static void SetEmptyOutputs(IGH_DataAccess DA, string summary)
        {
            DA.SetDataList(0, new List<Point3d>());
            DA.SetDataTree(1, new DataTree<int>());
            DA.SetDataList(2, new List<int>());
            DA.SetDataList(3, new List<Point3d>());
            DA.SetDataTree(4, new DataTree<int>());
            DA.SetDataTree(5, new DataTree<int>());
            DA.SetDataList(6, BoundaryNames);
            DA.SetData(7, new Mesh());
            DA.SetData(8, summary);
        }

        private static string F(double value)
        {
            if (!WasperGridTools.IsFinite(value)) return "NaN";
            return value.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
        }

        private readonly struct FaceRecord
        {
            public readonly int ElementIndex;
            public readonly int N0;
            public readonly int N1;
            public readonly int N2;
            public readonly int N3;
            public readonly int Axis;
            public readonly int Sign;
            public readonly double AxisCoordinate;
            public readonly FaceKey Key;

            public FaceRecord(int elementIndex, int n0, int n1, int n2, int n3, int axis, int sign, double axisCoordinate)
            {
                ElementIndex = elementIndex;
                N0 = n0;
                N1 = n1;
                N2 = n2;
                N3 = n3;
                Axis = axis;
                Sign = sign;
                AxisCoordinate = axisCoordinate;
                Key = new FaceKey(n0, n1, n2, n3);
            }
        }

        private readonly struct HexCandidate
        {
            public readonly bool IsValid;
            public readonly int[] Hex;
            public readonly int MaterialID;
            public readonly Point3d Center;

            public HexCandidate(bool isValid, int[] hex, int materialID, Point3d center)
            {
                IsValid = isValid;
                Hex = hex;
                MaterialID = materialID;
                Center = center;
            }
        }

        private readonly struct FaceKey : IEquatable<FaceKey>
        {
            private readonly int A;
            private readonly int B;
            private readonly int C;
            private readonly int D;

            public FaceKey(int n0, int n1, int n2, int n3)
            {
                int[] ids = { n0, n1, n2, n3 };
                Array.Sort(ids);
                A = ids[0];
                B = ids[1];
                C = ids[2];
                D = ids[3];
            }

            public bool Equals(FaceKey other) => A == other.A && B == other.B && C == other.C && D == other.D;
            public override bool Equals(object obj) => obj is FaceKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + A.GetHashCode();
                    hash = hash * 31 + B.GetHashCode();
                    hash = hash * 31 + C.GetHashCode();
                    hash = hash * 31 + D.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
