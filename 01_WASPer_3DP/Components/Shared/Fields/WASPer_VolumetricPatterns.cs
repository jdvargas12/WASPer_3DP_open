using System;
using System.Runtime.CompilerServices;
using Rhino.Geometry;

namespace WASPer_3DP
{
    /// <summary>
    /// Identifies the geometry system that produced a volumetric sample.
    /// Pattern evaluators should normally work from normalized U/V/W and remain
    /// independent of the originating Grasshopper component.
    /// </summary>
    public enum WasperVolumetricDomainKind
    {
        Box = 0,
        SurfacePair = 1
    }

    /// <summary>
    /// Immutable geometric information supplied to a volumetric-pattern
    /// evaluator for one field sample.
    ///
    /// U/V/W are normalized domain coordinates. WorldPoint and LocalPoint
    /// preserve model-space and frame-space information for patterns that need
    /// physical distances. SizeU/V/W are model-unit domain dimensions.
    /// </summary>
    public readonly struct WasperVolumetricEvaluationContext
    {
        public WasperVolumetricEvaluationContext(
            WasperVolumetricDomainKind domainKind,
            int domainIndex,
            double u,
            double v,
            double w,
            Point3d worldPoint,
            Point3d localPoint,
            Plane frame,
            double sizeU,
            double sizeV,
            double sizeW)
        {
            DomainKind = domainKind;
            DomainIndex = domainIndex;
            U = u;
            V = v;
            W = w;
            WorldPoint = worldPoint;
            LocalPoint = localPoint;
            Frame = frame;
            SizeU = sizeU;
            SizeV = sizeV;
            SizeW = sizeW;
        }

        public WasperVolumetricDomainKind DomainKind { get; }
        public int DomainIndex { get; }

        public double U { get; }
        public double V { get; }
        public double W { get; }

        public Point3d WorldPoint { get; }
        public Point3d LocalPoint { get; }
        public Plane Frame { get; }

        public double SizeU { get; }
        public double SizeV { get; }
        public double SizeW { get; }

        public bool IsValid =>
            DomainIndex >= 0 &&
            Finite(U) &&
            Finite(V) &&
            Finite(W) &&
            WorldPoint.IsValid &&
            LocalPoint.IsValid &&
            Frame.IsValid &&
            FinitePositive(SizeU) &&
            FinitePositive(SizeV) &&
            FinitePositive(SizeW);

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool FinitePositive(double value) =>
            Finite(value) && value > 0.0;
    }

    /// <summary>
    /// Shared internal contract for TPMS, polyhedral, brick, and future
    /// volumetric patterns.
    ///
    /// EvaluateSignedDistance must return a finite signed-distance-like value
    /// in Rhino model units:
    ///   negative = material / inside
    ///   zero     = pattern boundary
    ///   positive = void / outside
    ///
    /// Domain clipping, shells, partitions, trim geometry, and meshing remain
    /// responsibilities of the box/surface generator rather than the pattern.
    /// </summary>
    public interface IWasperVolumetricPattern
    {
        string PatternName { get; }
        string PatternKind { get; }
        IWasperInfillParams SourceParameters { get; }

        double EvaluateSignedDistance(
            in WasperVolumetricEvaluationContext context);
    }

    /// <summary>
    /// Allocation-free shared TPMS mathematics used by optimized box and
    /// curvilinear surface engines. Components keep their own grid generation
    /// and call these static methods directly in hot sampling loops.
    /// </summary>
    public static class WasperTpmsPatternMath
    {
        public const double TwoPi = 2.0 * Math.PI;
        private const double Epsilon = 1e-10;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double EvaluateRawNormalized(
            int type,
            double level,
            double countU,
            double countV,
            double countW,
            double phaseU,
            double phaseV,
            double phaseW,
            double u,
            double v,
            double w)
        {
            double x = TwoPi * (countU * u + phaseU);
            double y = TwoPi * (countV * v + phaseV);
            double z = TwoPi * (countW * w + phaseW);
            return Value(type, x, y, z) - level;
        }

        public static double Value(int type, double x, double y, double z)
        {
            switch (type)
            {
                case 0:
                    return Math.Cos(x) + Math.Cos(y) + Math.Cos(z);
                case 1:
                    return Math.Sin(x) * Math.Sin(y) * Math.Sin(z)
                         + Math.Sin(x) * Math.Cos(y) * Math.Cos(z)
                         + Math.Cos(x) * Math.Sin(y) * Math.Cos(z)
                         + Math.Cos(x) * Math.Cos(y) * Math.Sin(z);
                case 2:
                    return Math.Sin(x) * Math.Cos(y)
                         + Math.Sin(y) * Math.Cos(z)
                         + Math.Sin(z) * Math.Cos(x);
                case 3:
                    return -2.0 * (Math.Cos(x) * Math.Cos(y)
                                  + Math.Cos(y) * Math.Cos(z)
                                  + Math.Cos(z) * Math.Cos(x))
                         + Math.Cos(2.0 * x)
                         + Math.Cos(2.0 * y)
                         + Math.Cos(2.0 * z);
                case 4:
                    return 3.0 * (Math.Cos(x) + Math.Cos(y) + Math.Cos(z))
                         + 4.0 * Math.Cos(x) * Math.Cos(y) * Math.Cos(z);
                case 5:
                    return 0.5 * (Math.Sin(2.0 * x) * Math.Cos(y) * Math.Sin(z)
                                + Math.Sin(2.0 * y) * Math.Cos(z) * Math.Sin(x)
                                + Math.Sin(2.0 * z) * Math.Cos(x) * Math.Sin(y))
                         - 0.5 * (Math.Cos(2.0 * x) * Math.Cos(2.0 * y)
                                + Math.Cos(2.0 * y) * Math.Cos(2.0 * z)
                                + Math.Cos(2.0 * z) * Math.Cos(2.0 * x));
                case 6:
                    return Math.Sin(x) * Math.Cos(y) * Math.Cos(2.0 * z)
                         + Math.Cos(2.0 * x) * Math.Sin(y) * Math.Cos(z)
                         + Math.Cos(x) * Math.Cos(2.0 * y) * Math.Sin(z);
                case 7:
                    return Math.Sin(x) * Math.Sin(y) * Math.Sin(z)
                         + Math.Cos(x) * Math.Cos(y) * Math.Cos(z)
                         + Math.Sin(2.0 * x) * Math.Sin(y)
                         + Math.Cos(x) * Math.Sin(2.0 * y)
                         + Math.Sin(x) * Math.Sin(2.0 * z)
                         + Math.Sin(2.0 * x) * Math.Cos(z)
                         + Math.Sin(2.0 * y) * Math.Sin(z)
                         + Math.Cos(y) * Math.Sin(2.0 * z);
                default:
                    return 0.0;
            }
        }

        public static double ApproxGradientScale(
            double countU,
            double countV,
            double countW,
            double sizeU,
            double sizeV,
            double sizeW)
        {
            double gx = TwoPi * Math.Max(0.0, countU) / Math.Max(sizeU, Epsilon);
            double gy = TwoPi * Math.Max(0.0, countV) / Math.Max(sizeV, Epsilon);
            double gz = TwoPi * Math.Max(0.0, countW) / Math.Max(sizeW, Epsilon);
            double scale = Math.Sqrt(gx * gx + gy * gy + gz * gz);
            return scale > Epsilon ? scale : 1.0;
        }

        public static double GradientMagnitude(
            int type,
            double x,
            double y,
            double z,
            double kx,
            double ky,
            double kz)
        {
            const double h = 1e-5;
            double dFx = (Value(type, x + h, y, z) - Value(type, x - h, y, z)) / (2.0 * h);
            double dFy = (Value(type, x, y + h, z) - Value(type, x, y - h, z)) / (2.0 * h);
            double dFz = (Value(type, x, y, z + h) - Value(type, x, y, z - h)) / (2.0 * h);

            double gx = dFx * kx;
            double gy = dFy * ky;
            double gz = dFz * kz;
            return Math.Sqrt(gx * gx + gy * gy + gz * gz);
        }

        public static string Name(int type)
        {
            switch (type)
            {
                case 0: return "Schwarz P";
                case 1: return "Schwarz D";
                case 2: return "Gyroid";
                case 3: return "IWP";
                case 4: return "Neovius";
                case 5: return "Lidinoid";
                case 6: return "Fischer-Koch S";
                case 7: return "Fischer-Koch Y";
                default: return "?";
            }
        }
    }

    /// <summary>
    /// Allocation-free polyhedral face-network mathematics shared by regular
    /// box grids and curvilinear surface-pair grids.
    ///
    /// Cell coordinates are expressed in repetitions (for example,
    /// cellU = countU * normalizedU). ScaleU/V/W are repetitions per model
    /// unit. The returned distances therefore remain in Rhino model units,
    /// including when the domain is anisotropic.
    ///
    /// This helper deliberately contains pattern evaluation only. Grid
    /// generation, phase nudging, boundary clipping, shells, partitions,
    /// trimming, inversion of the complete material field, and meshing remain
    /// responsibilities of the consuming generator.
    /// </summary>
    public static class WasperPolyhedralPatternMath
    {
        public const int TruncatedOctahedron = 0;
        public const int Octahedron = 1;

        private const double Epsilon = 1e-10;
        private const double InvalidDistance = 1e9;

        /// <summary>
        /// Returns the unsigned model-space distance to the nearest face in the
        /// selected periodic polyhedral network.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double FaceDistanceWorld(
            int type,
            double cellU,
            double cellV,
            double cellW,
            double scaleU,
            double scaleV,
            double scaleW)
        {
            double diagonalScale = Math.Sqrt(
                scaleU * scaleU +
                scaleV * scaleV +
                scaleW * scaleW);

            if (diagonalScale <= Epsilon)
                return InvalidDistance;

            if (type == TruncatedOctahedron)
            {
                double squareDistance = TruncatedOctahedronSquareDistanceWorld(
                    cellU, cellV, cellW,
                    scaleU, scaleV, scaleW);
                double hexDistance = TruncatedOctahedronHexDistanceWorld(
                    cellU, cellV, cellW,
                    diagonalScale);

                return Math.Min(squareDistance, hexDistance);
            }

            // Existing In11/In12 inputs clamp the family to 0..1 before
            // evaluation, so every non-zero value reaches the octahedron path.
            return OctahedronDistanceWorld(
                cellU, cellV, cellW,
                diagonalScale);
        }

        /// <summary>
        /// Converts the nearest-face distance into a finite-thickness sheet
        /// field. Negative values identify material and positive values void.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double BandDistanceWorld(
            int type,
            double cellU,
            double cellV,
            double cellW,
            double scaleU,
            double scaleV,
            double scaleW,
            double thickness)
        {
            double halfThickness = Math.Max(0.0, thickness * 0.5);
            return FaceDistanceWorld(
                type,
                cellU, cellV, cellW,
                scaleU, scaleV, scaleW) - halfThickness;
        }

        public static string Name(int type)
        {
            switch (type)
            {
                case TruncatedOctahedron: return "Trunc. Octahedron";
                case Octahedron: return "Octahedron";
                default: return "?";
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double HalfIntegerFraction(double value)
        {
            double fraction = value - Math.Floor(value);
            return Math.Abs(fraction - 0.5);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double HexPeriodDistance(double value)
        {
            double fraction = ((value - 0.75) % 1.5 + 1.5) % 1.5;
            return Math.Min(fraction, 1.5 - fraction);
        }

        private static double TruncatedOctahedronSquareDistanceWorld(
            double cellU,
            double cellV,
            double cellW,
            double scaleU,
            double scaleV,
            double scaleW)
        {
            double du = HalfIntegerFraction(cellU) / Math.Max(scaleU, Epsilon);
            double dv = HalfIntegerFraction(cellV) / Math.Max(scaleV, Epsilon);
            double dw = HalfIntegerFraction(cellW) / Math.Max(scaleW, Epsilon);
            return Math.Min(du, Math.Min(dv, dw));
        }

        private static double TruncatedOctahedronHexDistanceWorld(
            double cellU,
            double cellV,
            double cellW,
            double diagonalScale)
        {
            double distance = Math.Min(
                Math.Min(
                    HexPeriodDistance(cellU + cellV + cellW),
                    HexPeriodDistance(cellU + cellV - cellW)),
                Math.Min(
                    HexPeriodDistance(cellU - cellV + cellW),
                    HexPeriodDistance(-cellU + cellV + cellW)));

            return distance / Math.Max(diagonalScale, Epsilon);
        }

        private static double OctahedronDistanceWorld(
            double cellU,
            double cellV,
            double cellW,
            double diagonalScale)
        {
            double distance = Math.Min(
                Math.Min(
                    HalfIntegerFraction(cellU + cellV + cellW),
                    HalfIntegerFraction(cellU + cellV - cellW)),
                Math.Min(
                    HalfIntegerFraction(cellU - cellV + cellW),
                    HalfIntegerFraction(-cellU + cellV + cellW)));

            return distance / Math.Max(diagonalScale, Epsilon);
        }
    }

    /// <summary>
    /// Shared model-unit distance evaluation for U/V planar patterns. The
    /// resulting line network is extruded through local W by volumetric
    /// generators; the layered generator uses the same U/V definitions to
    /// construct printing curves.
    /// </summary>
    public static class WasperPlanarPatternMath
    {
        private const double Epsilon = 1e-10;
        private const double InvalidDistance = 1e9;
        private const int SineSegmentsPerCell = 16;

        public static double BandDistanceWorld(
            int type,
            double u,
            double v,
            double sizeU,
            double sizeV,
            int countU,
            int countV,
            double phaseU,
            double phaseV,
            bool flipU,
            bool flipV,
            double thickness)
        {
            countU = Math.Max(1, countU);
            countV = Math.Max(1, countV);
            sizeU = Math.Max(sizeU, Epsilon);
            sizeV = Math.Max(sizeV, Epsilon);

            double distance;
            if (type == 5)
            {
                distance = RectangularGridDistanceWorld(
                    u, v,
                    sizeU, sizeV,
                    countU, countV,
                    phaseU, phaseV,
                    flipU, flipV);
            }
            else
            {
                double mappedU = flipU ? 1.0 - u : u;
                double mappedV = flipV ? 1.0 - v : v;
                double qU = countU * mappedU + phaseU;
                double qV = countV * mappedV + phaseV;
                double scaleU = sizeU / countU;
                double scaleV = sizeV / countV;

                switch (type)
                {
                    case 1:
                        distance = RepeatedSquareSDistanceWorld(
                            qU, qV, scaleU, scaleV);
                        break;
                    case 2:
                        distance = HalfIntegerDistance(qU) * scaleU;
                        break;
                    case 3:
                        distance = RepeatedTriangleDistanceWorld(
                            qU, qV, scaleU, scaleV);
                        break;
                    case 4:
                    default:
                        distance = RepeatedSineDistanceWorld(
                            qU, qV, scaleU, scaleV);
                        break;
                }
            }

            return distance - Math.Max(0.0, thickness) * 0.5;
        }

        public static double ShapeValue(int type, double cellT)
        {
            cellT -= Math.Floor(cellT);
            switch (type)
            {
                case 1:
                    return cellT < 0.5 ? 0.0 : 1.0;
                case 3:
                    return cellT < 0.5
                        ? 2.0 * cellT
                        : 2.0 * (1.0 - cellT);
                case 4:
                default:
                    return 0.5 * (1.0 - Math.Cos(TwoPi * cellT));
            }
        }

        public static string Name(int type)
        {
            switch (type)
            {
                case 1: return "Square S";
                case 2: return "Sticks";
                case 3: return "Triangle";
                case 4: return "Sine";
                case 5: return "Rectangular Grid / Brick-like";
                default: return "?";
            }
        }

        private const double TwoPi = 2.0 * Math.PI;

        private static double RepeatedSquareSDistanceWorld(
            double qU,
            double qV,
            double scaleU,
            double scaleV)
        {
            double best = InvalidDistance * InvalidDistance;
            int baseCell = (int)Math.Floor(qU);
            int baseRow = (int)Math.Floor(qV);
            for (int cell = baseCell - 1; cell <= baseCell + 1; cell++)
            {
                for (int row = baseRow - 2; row <= baseRow + 1; row++)
                {
                    best = Math.Min(best, SegmentDistanceSquared(
                        qU, qV,
                        cell, row,
                        cell + 0.5, row,
                        scaleU, scaleV));
                    best = Math.Min(best, SegmentDistanceSquared(
                        qU, qV,
                        cell + 0.5, row,
                        cell + 0.5, row + 1.0,
                        scaleU, scaleV));
                    best = Math.Min(best, SegmentDistanceSquared(
                        qU, qV,
                        cell + 0.5, row + 1.0,
                        cell + 1.0, row + 1.0,
                        scaleU, scaleV));
                }
            }
            return Math.Sqrt(best);
        }

        private static double RepeatedTriangleDistanceWorld(
            double qU,
            double qV,
            double scaleU,
            double scaleV)
        {
            double best = InvalidDistance * InvalidDistance;
            int baseCell = (int)Math.Floor(qU);
            int baseRow = (int)Math.Floor(qV);
            for (int cell = baseCell - 1; cell <= baseCell + 1; cell++)
            {
                for (int row = baseRow - 2; row <= baseRow + 1; row++)
                {
                    best = Math.Min(best, SegmentDistanceSquared(
                        qU, qV,
                        cell, row,
                        cell + 0.5, row + 1.0,
                        scaleU, scaleV));
                    best = Math.Min(best, SegmentDistanceSquared(
                        qU, qV,
                        cell + 0.5, row + 1.0,
                        cell + 1.0, row,
                        scaleU, scaleV));
                }
            }
            return Math.Sqrt(best);
        }

        private static double RepeatedSineDistanceWorld(
            double qU,
            double qV,
            double scaleU,
            double scaleV)
        {
            double best = InvalidDistance * InvalidDistance;
            int baseCell = (int)Math.Floor(qU);
            int baseRow = (int)Math.Floor(qV);
            for (int cell = baseCell - 1; cell <= baseCell + 1; cell++)
            {
                for (int row = baseRow - 2; row <= baseRow + 1; row++)
                {
                    double previousX = cell;
                    double previousY = row;
                    for (int segment = 1; segment <= SineSegmentsPerCell; segment++)
                    {
                        double t = segment / (double)SineSegmentsPerCell;
                        double currentX = cell + t;
                        double currentY = row + ShapeValue(4, t);
                        best = Math.Min(best, SegmentDistanceSquared(
                            qU, qV,
                            previousX, previousY,
                            currentX, currentY,
                            scaleU, scaleV));
                        previousX = currentX;
                        previousY = currentY;
                    }
                }
            }
            return Math.Sqrt(best);
        }

        private static double RectangularGridDistanceWorld(
            double u,
            double v,
            double sizeU,
            double sizeV,
            int countU,
            int countV,
            double phaseU,
            double phaseV,
            bool flipU,
            bool flipV)
        {
            phaseU = Wrap01(phaseU);
            phaseV = Wrap01(phaseV);
            double best = InvalidDistance;

            for (int i = 1; i < countU; i++)
            {
                double position = (i - phaseU) / countU;
                if (position <= Epsilon || position >= 1.0 - Epsilon)
                    continue;
                if (flipU) position = 1.0 - position;
                best = Math.Min(best, Math.Abs(u - position) * sizeU);
            }

            for (int j = 1; j < countV; j++)
            {
                double position = (j - phaseV) / countV;
                if (position <= Epsilon || position >= 1.0 - Epsilon)
                    continue;
                if (flipV) position = 1.0 - position;
                best = Math.Min(best, Math.Abs(v - position) * sizeV);
            }

            return best;
        }

        private static double HalfIntegerDistance(double value)
        {
            double fraction = value - Math.Floor(value);
            return Math.Abs(fraction - 0.5);
        }

        private static double SegmentDistanceSquared(
            double px,
            double py,
            double ax,
            double ay,
            double bx,
            double by,
            double scaleX,
            double scaleY)
        {
            double pX = px * scaleX;
            double pY = py * scaleY;
            double aX = ax * scaleX;
            double aY = ay * scaleY;
            double bX = bx * scaleX;
            double bY = by * scaleY;
            double dx = bX - aX;
            double dy = bY - aY;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= Epsilon)
            {
                double ex = pX - aX;
                double ey = pY - aY;
                return ex * ex + ey * ey;
            }

            double t = ((pX - aX) * dx + (pY - aY) * dy) / lengthSquared;
            t = Math.Max(0.0, Math.Min(1.0, t));
            double qx = aX + t * dx;
            double qy = aY + t * dy;
            double rx = pX - qx;
            double ry = pY - qy;
            return rx * rx + ry * ry;
        }

        private static double Wrap01(double value)
        {
            value -= Math.Floor(value);
            return value < 0.0 ? value + 1.0 : value;
        }
    }

    /// <summary>
    /// Model-unit distance to the internal rib planes of a brick-like cavity
    /// array. The volumetric generator owns the external shell and clipping.
    /// </summary>
    public static class WasperBrickPatternMath
    {
        private const double Epsilon = 1e-10;
        private const double InvalidDistance = 1e9;

        public static double BandDistanceWorld(
            double u,
            double v,
            double w,
            double sizeU,
            double sizeV,
            double sizeW,
            int countU,
            int countV,
            int cavityDirection,
            double thickness)
        {
            double a;
            double b;
            double sizeA;
            double sizeB;
            switch (cavityDirection)
            {
                case 2:
                    a = v; b = w;
                    sizeA = sizeV; sizeB = sizeW;
                    break;
                case 3:
                    a = u; b = w;
                    sizeA = sizeU; sizeB = sizeW;
                    break;
                case 1:
                default:
                    a = u; b = v;
                    sizeA = sizeU; sizeB = sizeV;
                    break;
            }

            double distance = Math.Min(
                InternalPlaneDistance(a, Math.Max(sizeA, Epsilon), Math.Max(1, countU)),
                InternalPlaneDistance(b, Math.Max(sizeB, Epsilon), Math.Max(1, countV)));
            return distance - Math.Max(0.0, thickness) * 0.5;
        }

        private static double InternalPlaneDistance(double coordinate, double size, int cavityCount)
        {
            if (cavityCount <= 1)
                return InvalidDistance;

            double scaled = coordinate * cavityCount;
            double nearest = Math.Round(scaled);
            nearest = Math.Max(1.0, Math.Min(cavityCount - 1.0, nearest));
            return Math.Abs(coordinate - nearest / cavityCount) * size;
        }
    }

    public enum WasperVolumetricPatternKind
    {
        Tpms = 0,
        Polyhedral = 1,
        Brick = 2
    }

    /// <summary>
    /// Validated, allocation-free dispatch descriptor used by volumetric
    /// generators. It translates supported IWasperInfillParams objects into a
    /// common model-unit field evaluation without hiding the domain engine.
    /// </summary>
    public readonly struct WasperVolumetricPatternDescriptor
    {
        private const double Epsilon = 1e-10;

        private WasperVolumetricPatternDescriptor(
            WasperTpmsInfillParams tpms,
            WasperPolyhedralInfillParams polyhedral,
            WasperBrickInfillParams brick)
        {
            Tpms = tpms;
            Polyhedral = polyhedral;
            Brick = brick;
            Kind = tpms != null
                ? WasperVolumetricPatternKind.Tpms
                : polyhedral != null
                    ? WasperVolumetricPatternKind.Polyhedral
                    : WasperVolumetricPatternKind.Brick;
        }

        public WasperVolumetricPatternKind Kind { get; }
        public WasperTpmsInfillParams Tpms { get; }
        public WasperPolyhedralInfillParams Polyhedral { get; }
        public WasperBrickInfillParams Brick { get; }

        public IWasperInfillParams SourceParameters =>
            (IWasperInfillParams)Tpms ?? Polyhedral ?? (IWasperInfillParams)Brick;

        public double CountU => Kind == WasperVolumetricPatternKind.Tpms
            ? Tpms.CountX
            : Kind == WasperVolumetricPatternKind.Polyhedral
                ? Polyhedral.CountX
                : Brick.CavityDirection == 2 ? 1 : Brick.CountU;

        public double CountV => Kind == WasperVolumetricPatternKind.Tpms
            ? Tpms.CountY
            : Kind == WasperVolumetricPatternKind.Polyhedral
                ? Polyhedral.CountY
                : Brick.CavityDirection == 1
                    ? Brick.CountV
                    : Brick.CavityDirection == 2
                        ? Brick.CountU
                        : 1;

        public double CountW => Kind == WasperVolumetricPatternKind.Tpms
            ? Tpms.CountZ
            : Kind == WasperVolumetricPatternKind.Polyhedral
                ? Polyhedral.CountZ
                : Brick.CavityDirection == 1 ? 1.0 : Brick.CountV;

        public bool Invert => Kind == WasperVolumetricPatternKind.Tpms
            ? Tpms.InvertTpms
            : Kind == WasperVolumetricPatternKind.Polyhedral
                ? Polyhedral.InvertPolyhedral
                : Brick.Invert;

        public bool ExplicitlyCloseDomain =>
            Kind == WasperVolumetricPatternKind.Tpms && Tpms.CloseTpms;

        public string PatternName => Kind == WasperVolumetricPatternKind.Tpms
            ? WasperTpmsPatternMath.Name(Tpms.Type)
            : Kind == WasperVolumetricPatternKind.Polyhedral
                ? WasperPolyhedralPatternMath.Name(Polyhedral.Type)
                : "Brick-like";

        public string PatternKind => Kind == WasperVolumetricPatternKind.Tpms
            ? "TPMS"
            : Kind == WasperVolumetricPatternKind.Polyhedral
                ? "Polyhedral"
                : "Brick-like";

        public string CountsText =>
            $"{CountU:0.###}.{CountV:0.###}.{CountW:0.###}";

        public static bool TryCreate(
            IWasperInfillParams source,
            out WasperVolumetricPatternDescriptor descriptor,
            out string error)
        {
            descriptor = default;
            error = null;

            if (source == null)
            {
                error = "Infill parameters are null.";
                return false;
            }

            string validation = source.Validate();
            if (!string.IsNullOrEmpty(validation))
            {
                error = validation;
                return false;
            }

            if (source is WasperTpmsInfillParams tpms)
            {
                descriptor = new WasperVolumetricPatternDescriptor(tpms, null, null);
                return true;
            }

            if (source is WasperPolyhedralInfillParams polyhedral)
            {
                descriptor = new WasperVolumetricPatternDescriptor(null, polyhedral, null);
                return true;
            }

            if (source is WasperBrickInfillParams brick)
            {
                descriptor = new WasperVolumetricPatternDescriptor(null, null, brick);
                return true;
            }

            error =
                $"Unsupported volumetric infill parameter type '{source.GetType().Name}'. " +
                "Use TPMS, Polyhedral, or Brick-like Infill Params.";
            return false;
        }

        /// <summary>
        /// Evaluates a signed-distance-like material field in model units.
        /// Negative values represent the generated pattern material.
        /// Inversion can be deferred until after a generator adds its shell.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double EvaluateNormalized(
            double u,
            double v,
            double w,
            double sizeU,
            double sizeV,
            double sizeW,
            double thickness,
            bool applyInvert)
        {
            double tpmsGradientScale = Kind == WasperVolumetricPatternKind.Tpms
                ? WasperTpmsPatternMath.ApproxGradientScale(
                    Tpms.CountX,
                    Tpms.CountY,
                    Tpms.CountZ,
                    sizeU,
                    sizeV,
                    sizeW)
                : 1.0;
            return EvaluateNormalized(
                u, v, w,
                sizeU, sizeV, sizeW,
                thickness,
                applyInvert,
                tpmsGradientScale);
        }

        /// <summary>
        /// Hot-loop overload. Generators can calculate tpmsGradientScale once
        /// per domain and reuse it for every voxel. Polyhedral evaluators ignore
        /// the supplied TPMS scale.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double EvaluateNormalized(
            double u,
            double v,
            double w,
            double sizeU,
            double sizeV,
            double sizeW,
            double thickness,
            bool applyInvert,
            double tpmsGradientScale)
        {
            double value;
            if (Kind == WasperVolumetricPatternKind.Tpms)
            {
                value = WasperTpmsPatternMath.EvaluateRawNormalized(
                    Tpms.Type,
                    Tpms.Level,
                    Tpms.CountX,
                    Tpms.CountY,
                    Tpms.CountZ,
                    Tpms.PhaseX,
                    Tpms.PhaseY,
                    Tpms.PhaseZ,
                    u,
                    v,
                    w);

                if (thickness > Epsilon)
                {
                    value = Math.Abs(value / Math.Max(tpmsGradientScale, Epsilon))
                        - thickness * 0.5;
                }
            }
            else if (Kind == WasperVolumetricPatternKind.Polyhedral)
            {
                value = WasperPolyhedralPatternMath.BandDistanceWorld(
                    Polyhedral.Type,
                    Polyhedral.CountX * u,
                    Polyhedral.CountY * v,
                    Polyhedral.CountZ * w,
                    Polyhedral.CountX / Math.Max(sizeU, Epsilon),
                    Polyhedral.CountY / Math.Max(sizeV, Epsilon),
                    Polyhedral.CountZ / Math.Max(sizeW, Epsilon),
                    thickness);
            }
            else
            {
                value = WasperBrickPatternMath.BandDistanceWorld(
                    u,
                    v,
                    w,
                    sizeU,
                    sizeV,
                    sizeW,
                    Brick.CountU,
                    Brick.CountV,
                    Brick.CavityDirection,
                    thickness);
            }

            return applyInvert && Invert ? -value : value;
        }

        public double ApproximateTpmsGradientScale(
            double sizeU,
            double sizeV,
            double sizeW) =>
            Kind == WasperVolumetricPatternKind.Tpms
                ? WasperTpmsPatternMath.ApproxGradientScale(
                    Tpms.CountX,
                    Tpms.CountY,
                    Tpms.CountZ,
                    sizeU,
                    sizeV,
                    sizeW)
                : 1.0;

        /// <summary>
        /// Box-domain evaluation with the exact local TPMS gradient used by
        /// the established Cartesian engine. Polyhedral evaluation is already
        /// a model-unit distance and therefore uses the common path directly.
        /// </summary>
        public double EvaluateBox(
            double u,
            double v,
            double w,
            double sizeU,
            double sizeV,
            double sizeW,
            double thickness,
            bool applyInvert)
        {
            if (Kind != WasperVolumetricPatternKind.Tpms || thickness <= Epsilon)
                return EvaluateNormalized(
                    u, v, w,
                    sizeU, sizeV, sizeW,
                    thickness,
                    applyInvert);

            double x = WasperTpmsPatternMath.TwoPi * (Tpms.CountX * u + Tpms.PhaseX);
            double y = WasperTpmsPatternMath.TwoPi * (Tpms.CountY * v + Tpms.PhaseY);
            double z = WasperTpmsPatternMath.TwoPi * (Tpms.CountZ * w + Tpms.PhaseZ);
            double raw = WasperTpmsPatternMath.Value(Tpms.Type, x, y, z) - Tpms.Level;
            double gradient = WasperTpmsPatternMath.GradientMagnitude(
                Tpms.Type,
                x, y, z,
                WasperTpmsPatternMath.TwoPi * Tpms.CountX / Math.Max(sizeU, Epsilon),
                WasperTpmsPatternMath.TwoPi * Tpms.CountY / Math.Max(sizeV, Epsilon),
                WasperTpmsPatternMath.TwoPi * Tpms.CountZ / Math.Max(sizeW, Epsilon));
            double value = Math.Abs(raw / Math.Max(gradient, Epsilon)) - thickness * 0.5;
            return applyInvert && Invert ? -value : value;
        }

        public bool RequiresBoundaryClip(double thickness) =>
            ExplicitlyCloseDomain ||
            Kind == WasperVolumetricPatternKind.Polyhedral ||
            Kind == WasperVolumetricPatternKind.Brick ||
            thickness > Epsilon;

        public string Trace()
        {
            if (Kind == WasperVolumetricPatternKind.Tpms)
            {
                return
                    $"{PatternName},L={Tpms.Level:G6}," +
                    $"C={Tpms.CountX:G6}/{Tpms.CountY:G6}/{Tpms.CountZ:G6}," +
                    $"P={Tpms.PhaseX:G6}/{Tpms.PhaseY:G6}/{Tpms.PhaseZ:G6}," +
                    $"close={Tpms.CloseTpms},invert={Tpms.InvertTpms}";
            }

            if (Kind == WasperVolumetricPatternKind.Polyhedral)
            {
                return
                    $"{PatternName}," +
                    $"C={Polyhedral.CountX}/{Polyhedral.CountY}/{Polyhedral.CountZ}," +
                    $"invert={Polyhedral.InvertPolyhedral}";
            }

            return
                $"{PatternName}," +
                $"C={Brick.CountU}/{Brick.CountV}," +
                $"dir={WasperBrickInfillParams.DirectionName(Brick.CavityDirection)}," +
                $"invert={Brick.Invert}";
        }
    }
}
