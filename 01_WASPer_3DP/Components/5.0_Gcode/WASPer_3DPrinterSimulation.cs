using System;
using System.Collections.Generic;
using System.Drawing;

using Grasshopper;
using Grasshopper.Kernel.Data;

using Rhino.Geometry;

namespace WASPer_3DP.Components._5_0_Gcode
{
    internal enum WasperPrinterFamily
    {
        Cartesian = 0,
        Delta = 1,
        Robot = 2
    }

    internal sealed class WasperSimulationPose
    {
        public Point3d Position { get; set; }
        public int MotionIndex { get; set; }
        public WasperMotionType MotionType { get; set; }
        public double CurrentTimeSeconds { get; set; }
        public double TimeProgress { get; set; }
        public double PathProgress { get; set; }
    }

    internal sealed class WasperSimulationTimeline
    {
        private readonly WasperMotionPlan _plan;
        private readonly double[] _endTimes;
        private readonly int[] _completedBefore;
        private readonly int[] _completedAfter;
        private readonly int _totalPointCount;

        public WasperSimulationTimeline(WasperPrintPath path)
        {
            _plan = path.MotionPlan;
            _totalPointCount = path.PointCount;
            int count = _plan.Count;
            _endTimes = new double[count];
            _completedBefore = new int[count];
            _completedAfter = new int[count];

            var branchOffsets = BuildBranchOffsets(path.Points);
            double time = 0.0;
            int completed = 0;

            for (int i = 0; i < count; i++)
            {
                WasperMotion motion = _plan.Motions[i];
                int before = completed;

                if (motion.Type == WasperMotionType.Print &&
                    motion.BranchIndex >= 0 &&
                    motion.BranchIndex < branchOffsets.Length)
                {
                    int targetFlatIndex =
                        branchOffsets[motion.BranchIndex] + motion.PointIndex;
                    before = Math.Max(before, targetFlatIndex);
                    completed = Math.Max(completed, targetFlatIndex + 1);
                }

                _completedBefore[i] = Math.Min(before, _totalPointCount);
                _completedAfter[i] = Math.Min(completed, _totalPointCount);
                time += motion.DurationMinutes * 60.0;
                _endTimes[i] = time;
            }

            DurationSeconds = time;
        }

        public WasperMotionPlan Plan => _plan;
        public double DurationSeconds { get; }

        public WasperSimulationPose Evaluate(double timeSeconds)
        {
            double time = Math.Max(0.0, Math.Min(timeSeconds, DurationSeconds));
            int index = FindMotionIndex(time);
            WasperMotion motion = _plan.Motions[index];
            double startTime = index == 0 ? 0.0 : _endTimes[index - 1];
            double duration = Math.Max(0.0, _endTimes[index] - startTime);
            double local = duration > Rhino.RhinoMath.ZeroTolerance
                ? (time - startTime) / duration
                : 1.0;
            local = Math.Max(0.0, Math.Min(local, 1.0));

            int completed = local >= 1.0
                ? _completedAfter[index]
                : _completedBefore[index];

            return new WasperSimulationPose
            {
                Position = motion.From + (motion.To - motion.From) * local,
                MotionIndex = index,
                MotionType = motion.Type,
                CurrentTimeSeconds = time,
                TimeProgress = DurationSeconds > 0.0 ? time / DurationSeconds : 0.0,
                PathProgress = _totalPointCount > 0
                    ? completed / (double)_totalPointCount
                    : 0.0
            };
        }

        private int FindMotionIndex(double time)
        {
            int low = 0;
            int high = _endTimes.Length - 1;

            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (_endTimes[mid] >= time)
                    high = mid;
                else
                    low = mid + 1;
            }

            return low;
        }

        private static int[] BuildBranchOffsets(DataTree<Point3d> points)
        {
            var offsets = new int[points.BranchCount];
            int offset = 0;
            for (int i = 0; i < points.BranchCount; i++)
            {
                offsets[i] = offset;
                offset += points.Branches[i]?.Count ?? 0;
            }
            return offsets;
        }
    }

    internal static class WasperPrinterEnvelope
    {
        public static int CountOutside(
            WasperMotionPlan plan,
            Plane origin,
            WasperPrinterFamily family,
            IReadOnlyList<double> dimensions,
            double tolerance)
        {
            Transform toLocal = Transform.PlaneToPlane(origin, Plane.WorldXY);
            int outside = 0;

            for (int i = 0; i < plan.Count; i++)
            {
                Point3d point = plan.Motions[i].To;
                point.Transform(toLocal);

                bool inside = family == WasperPrinterFamily.Delta
                    ? IsInsideDelta(point, dimensions, tolerance)
                    : IsInsideCartesian(point, dimensions, tolerance);
                if (!inside)
                    outside++;
            }

            return outside;
        }

        private static bool IsInsideCartesian(
            Point3d point,
            IReadOnlyList<double> dimensions,
            double tolerance)
        {
            return point.X >= -tolerance &&
                   point.Y >= -tolerance &&
                   point.Z >= -tolerance &&
                   point.X <= dimensions[0] + tolerance &&
                   point.Y <= dimensions[1] + tolerance &&
                   point.Z <= dimensions[2] + tolerance;
        }

        private static bool IsInsideDelta(
            Point3d point,
            IReadOnlyList<double> dimensions,
            double tolerance)
        {
            double radius = dimensions[0] * 0.5 + tolerance;
            return point.X * point.X + point.Y * point.Y <= radius * radius &&
                   point.Z >= -tolerance &&
                   point.Z <= dimensions[1] + tolerance;
        }
    }

    internal static class WasperPrinterMeshFactory
    {
        private static readonly Color FrameColor = Color.FromArgb(62, 68, 74);
        private static readonly Color BedColor = Color.FromArgb(82, 105, 122);
        private static readonly Color MovingColor = Color.FromArgb(226, 135, 45);
        private static readonly Color NozzleColor = Color.FromArgb(205, 151, 54);

        public static List<Mesh> Create(
            WasperPrinterFamily family,
            Plane origin,
            IReadOnlyList<double> dimensions,
            Point3d worldPosition,
            double nozzleDiameter)
        {
            Transform toLocal = Transform.PlaneToPlane(origin, Plane.WorldXY);
            Point3d localPosition = worldPosition;
            localPosition.Transform(toLocal);

            List<Mesh> meshes = family == WasperPrinterFamily.Delta
                ? CreateDelta(dimensions[0], dimensions[1], localPosition, nozzleDiameter)
                : CreateCartesian(
                    dimensions[0], dimensions[1], dimensions[2],
                    localPosition, nozzleDiameter);

            Transform toWorld = Transform.PlaneToPlane(Plane.WorldXY, origin);
            for (int i = 0; i < meshes.Count; i++)
                meshes[i].Transform(toWorld);

            return meshes;
        }

        private static List<Mesh> CreateCartesian(
            double x,
            double y,
            double z,
            Point3d position,
            double nozzleDiameter)
        {
            var meshes = new List<Mesh>();
            double minDim = Math.Min(x, Math.Min(y, z));
            double t = Math.Max(minDim * 0.012, nozzleDiameter * 1.5);
            double nozzleHeight = Math.Max(nozzleDiameter * 4.5, t * 1.5);
            double gantryZ = position.Z + nozzleHeight + t;
            double frameHeight = z + nozzleHeight + t * 2.0;

            AddBox(meshes, 0, x, 0, y, -t, 0, BedColor);

            AddBox(meshes, -t, 0, -t, 0, 0, frameHeight, FrameColor);
            AddBox(meshes, x, x + t, -t, 0, 0, frameHeight, FrameColor);
            AddBox(meshes, -t, 0, y, y + t, 0, frameHeight, FrameColor);
            AddBox(meshes, x, x + t, y, y + t, 0, frameHeight, FrameColor);

            AddBox(
                meshes,
                -t, x + t, -t, 0,
                frameHeight - t, frameHeight,
                FrameColor);
            AddBox(
                meshes,
                -t, x + t, y, y + t,
                frameHeight - t, frameHeight,
                FrameColor);

            AddBox(
                meshes,
                -t * 0.5, x + t * 0.5,
                position.Y - t * 0.5, position.Y + t * 0.5,
                gantryZ - t * 0.5, gantryZ + t * 0.5,
                MovingColor);

            AddBox(
                meshes,
                position.X - t, position.X + t,
                position.Y - t, position.Y + t,
                gantryZ - t, gantryZ + t,
                MovingColor);

            AddBeam(
                meshes,
                new Point3d(position.X, position.Y, gantryZ - t),
                new Point3d(position.X, position.Y, position.Z + nozzleHeight),
                t * 0.45,
                MovingColor);

            AddNozzle(meshes, position, nozzleDiameter);
            return meshes;
        }

        private static List<Mesh> CreateDelta(
            double diameter,
            double height,
            Point3d position,
            double nozzleDiameter)
        {
            var meshes = new List<Mesh>();
            double t = Math.Max(Math.Min(diameter, height) * 0.012, nozzleDiameter * 1.5);
            double bedRadius = diameter * 0.5;
            double baseBeamWidth = t * 0.7;
            double bedClearance = Math.Max(diameter * 0.025, t * 1.5);
            double effectorRadius = Math.Max(diameter * 0.035, nozzleDiameter * 3.0);

            // For an equilateral triangle, the inradius is half its
            // circumradius. Place the tower centerlines far enough out that
            // even the inner face of each base beam clears the circular bed.
            double towerRadius =
                2.0 * (bedRadius + bedClearance + baseBeamWidth * 0.5);

            // Keep the schematic arms long enough to reach the opposite edge
            // of the declared circular print area after widening the frame.
            double maximumHorizontalReach =
                towerRadius + bedRadius + effectorRadius;
            double armLength = maximumHorizontalReach + bedClearance;
            double centerArmRise = Math.Sqrt(Math.Max(
                armLength * armLength - towerRadius * towerRadius,
                0.0));
            double towerHeight = height + centerArmRise + t * 2.0;
            var effectorCenter = new Point3d(
                position.X,
                position.Y,
                position.Z + nozzleDiameter * 3.5);
            var towerBases = new Point3d[3];

            meshes.Add(ColorMesh(
                CreateFrustum(
                    new Point3d(0, 0, -t),
                    diameter * 0.5,
                    diameter * 0.5,
                    t,
                    24),
                BedColor));

            for (int i = 0; i < 3; i++)
            {
                double angle = i * Math.PI * 2.0 / 3.0;
                var radial = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0);
                var tangent = new Vector3d(-radial.Y, radial.X, 0);
                Point3d towerBase = Point3d.Origin + radial * towerRadius;
                towerBases[i] = towerBase;

                AddBeam(
                    meshes,
                    towerBase,
                    towerBase + Vector3d.ZAxis * towerHeight,
                    t,
                    FrameColor);

                double dx = effectorCenter.X - towerBase.X;
                double dy = effectorCenter.Y - towerBase.Y;
                double horizontalSquared = dx * dx + dy * dy;
                double vertical = Math.Sqrt(Math.Max(
                    armLength * armLength - horizontalSquared,
                    0.0));
                double carriageZ = Math.Max(
                    0.0,
                    Math.Min(towerHeight, effectorCenter.Z + vertical));
                Point3d carriage = new Point3d(
                    towerBase.X,
                    towerBase.Y,
                    carriageZ);

                AddBox(
                    meshes,
                    carriage.X - t, carriage.X + t,
                    carriage.Y - t, carriage.Y + t,
                    carriage.Z - t * 1.5, carriage.Z + t * 1.5,
                    MovingColor);

                for (int arm = -1; arm <= 1; arm += 2)
                {
                    Vector3d offset = tangent * (t * 0.7 * arm);
                    Point3d armStart = carriage + offset;
                    Point3d armEnd =
                        effectorCenter + radial * effectorRadius + offset * 0.55;
                    AddBeam(meshes, armStart, armEnd, t * 0.22, MovingColor);
                }
            }

            for (int i = 0; i < 3; i++)
            {
                int next = (i + 1) % 3;
                AddBeam(
                    meshes,
                    towerBases[i],
                    towerBases[next],
                    baseBeamWidth,
                    FrameColor);
                AddBeam(
                    meshes,
                    towerBases[i] + Vector3d.ZAxis * towerHeight,
                    towerBases[next] + Vector3d.ZAxis * towerHeight,
                    baseBeamWidth,
                    FrameColor);
            }

            meshes.Add(ColorMesh(
                CreateFrustum(
                    effectorCenter,
                    effectorRadius,
                    effectorRadius,
                    Math.Max(t, nozzleDiameter),
                    12),
                MovingColor));

            AddNozzle(meshes, position, nozzleDiameter);
            return meshes;
        }

        private static void AddNozzle(
            List<Mesh> meshes,
            Point3d tip,
            double diameter)
        {
            double d = Math.Max(diameter, Rhino.RhinoMath.ZeroTolerance * 10.0);
            meshes.Add(ColorMesh(
                CreateFrustum(tip, d * 0.5, d * 0.5, d * 1.5, 10),
                NozzleColor));
            meshes.Add(ColorMesh(
                CreateFrustum(
                    tip + Vector3d.ZAxis * (d * 1.5),
                    d * 0.5,
                    d * 2.0,
                    d * 3.0,
                    10),
                NozzleColor));

            AddBox(
                meshes,
                tip.X - d * 4.0, tip.X + d * 4.0,
                tip.Y - d * 3.0, tip.Y + d * 3.0,
                tip.Z + d * 4.5, tip.Z + d * 8.5,
                MovingColor);
        }

        private static void AddBox(
            List<Mesh> meshes,
            double x0,
            double x1,
            double y0,
            double y1,
            double z0,
            double z1,
            Color color)
        {
            var box = new Box(
                Plane.WorldXY,
                new Interval(Math.Min(x0, x1), Math.Max(x0, x1)),
                new Interval(Math.Min(y0, y1), Math.Max(y0, y1)),
                new Interval(Math.Min(z0, z1), Math.Max(z0, z1)));
            Mesh mesh = Mesh.CreateFromBox(box, 1, 1, 1);
            meshes.Add(ColorMesh(mesh, color));
        }

        private static void AddBeam(
            List<Mesh> meshes,
            Point3d from,
            Point3d to,
            double width,
            Color color)
        {
            Vector3d axis = to - from;
            if (!axis.Unitize())
                return;

            Vector3d u = Vector3d.CrossProduct(
                axis,
                Math.Abs(axis.Z) < 0.9 ? Vector3d.ZAxis : Vector3d.XAxis);
            if (!u.Unitize())
                return;
            Vector3d v = Vector3d.CrossProduct(axis, u);
            v.Unitize();

            u *= width * 0.5;
            v *= width * 0.5;

            var mesh = new Mesh();
            mesh.Vertices.Add(from + u + v);
            mesh.Vertices.Add(from - u + v);
            mesh.Vertices.Add(from - u - v);
            mesh.Vertices.Add(from + u - v);
            mesh.Vertices.Add(to + u + v);
            mesh.Vertices.Add(to - u + v);
            mesh.Vertices.Add(to - u - v);
            mesh.Vertices.Add(to + u - v);

            mesh.Faces.AddFace(0, 1, 2, 3);
            mesh.Faces.AddFace(4, 7, 6, 5);
            mesh.Faces.AddFace(0, 4, 5, 1);
            mesh.Faces.AddFace(1, 5, 6, 2);
            mesh.Faces.AddFace(2, 6, 7, 3);
            mesh.Faces.AddFace(3, 7, 4, 0);
            mesh.Normals.ComputeNormals();
            mesh.Compact();
            meshes.Add(ColorMesh(mesh, color));
        }

        private static Mesh CreateFrustum(
            Point3d baseCenter,
            double bottomRadius,
            double topRadius,
            double height,
            int sides)
        {
            var mesh = new Mesh();

            for (int i = 0; i < sides; i++)
            {
                double angle = i * Math.PI * 2.0 / sides;
                double c = Math.Cos(angle);
                double s = Math.Sin(angle);
                mesh.Vertices.Add(
                    baseCenter.X + c * bottomRadius,
                    baseCenter.Y + s * bottomRadius,
                    baseCenter.Z);
                mesh.Vertices.Add(
                    baseCenter.X + c * topRadius,
                    baseCenter.Y + s * topRadius,
                    baseCenter.Z + height);
            }

            int bottomCenter = mesh.Vertices.Count;
            mesh.Vertices.Add(baseCenter);
            int topCenter = mesh.Vertices.Count;
            mesh.Vertices.Add(baseCenter + Vector3d.ZAxis * height);

            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;
                int b0 = i * 2;
                int t0 = b0 + 1;
                int b1 = next * 2;
                int t1 = b1 + 1;
                mesh.Faces.AddFace(b0, b1, t1, t0);
                mesh.Faces.AddFace(bottomCenter, b1, b0);
                mesh.Faces.AddFace(topCenter, t0, t1);
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();
            return mesh;
        }

        private static Mesh ColorMesh(Mesh mesh, Color color)
        {
            mesh.VertexColors.CreateMonotoneMesh(color);
            return mesh;
        }
    }
}
