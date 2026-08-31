// WASPer_SdfMarchingCubes.cs
// WASPer_3DP — Shared infrastructure
//
// Shared SDF and Marching Cubes types used by:
//   - wsp_In11_Polyhedral Box Array SDF
//   - wsp_In12_Polyhedral Array from Surfaces SDF
//   - wsp_In13_Brick-like Box Array SDF
//   - wsp_Fa05_Finger Joints (SDF)
//
// All types are internal to the WASPer_3DP assembly.
// No Grasshopper component is registered here.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rhino.Geometry;

namespace WASPer_3DP
{
    // ─────────────────────────────────────────────────────────────────────────
    // VertexKey — snapped integer key for mesh vertex deduplication
    // ─────────────────────────────────────────────────────────────────────────

    internal readonly struct VertexKey : IEquatable<VertexKey>
    {
        private readonly long _x;
        private readonly long _y;
        private readonly long _z;

        public VertexKey(Point3d p, double tol)
        {
            double inv = 1.0 / Math.Max(tol, 1e-12);
            _x = (long)Math.Round(p.X * inv);
            _y = (long)Math.Round(p.Y * inv);
            _z = (long)Math.Round(p.Z * inv);
        }

        public bool Equals(VertexKey other)
        {
            return _x == other._x && _y == other._y && _z == other._z;
        }

        public override bool Equals(object obj)
        {
            return obj is VertexKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + _x.GetHashCode();
                h = h * 31 + _y.GetHashCode();
                h = h * 31 + _z.GetHashCode();
                return h;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FaceKey — string-based key for face deduplication in explicit meshing
    // ─────────────────────────────────────────────────────────────────────────

    internal readonly struct FaceKey : IEquatable<FaceKey>
    {
        private readonly string _key;

        public FaceKey(string key)
        {
            _key = key ?? "";
        }

        public bool Equals(FaceKey other)
        {
            return _key == other._key;
        }

        public override bool Equals(object obj)
        {
            return obj is FaceKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _key.GetHashCode();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WasperMcHelpers — shared Marching Cubes mesh helpers
    // ─────────────────────────────────────────────────────────────────────────

    internal static class WasperMcHelpers
    {
        /// <summary>
        /// Adds a vertex to a mesh using a snapped-key dictionary for deduplication.
        /// Returns the index of the vertex (existing or newly added).
        /// </summary>
        public static int AddVertex(Mesh mesh, Dictionary<VertexKey, int> map, Point3d p, double tol)
        {
            var key = new VertexKey(p, tol);
            if (map.TryGetValue(key, out int idx)) return idx;
            idx = mesh.Vertices.Add(p);
            map[key] = idx;
            return idx;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MarchingCubesClassicTable — classic 256-case triangle lookup table
    // Values are edge indices (0–11). -1 = end-of-list sentinel.
    // Encoded as base64 with 255 representing -1.
    // ─────────────────────────────────────────────────────────────────────────

    internal static class MarchingCubesClassicTable
    {
        private const string TriTableBase64 = @"
    /////////////////////wAIA/////////////////8AAQn/////////////////AQgDCQgB////
    /////////wECCv////////////////8ACAMBAgr/////////////CQIKAAIJ/////////////wII
    AwIKCAoJCP////////8DCwL/////////////////AAsCCAsA/////////////wEJAAIDC///////
    //////8BCwIBCQsJCAv/////////AwoBCwoD/////////////wAKAQAICggLCv////////8DCQAD
    CwkLCgn/////////CQgKCggL/////////////wQHCP////////////////8EAwAHAwT/////////
    ////AAEJCAQH/////////////wQBCQQHAQcDAf////////8BAgoIBAf/////////////AwQHAwAE
    AQIK/////////wkCCgkAAggEB/////////8CCgkCCQcCBwMHCQT/////CAQHAwsC////////////
    /wsEBwsCBAIABP////////8JAAEIBAcCAwv/////////BAcLCQQLCQsCCQIB/////wMKAQMLCgcI
    BP////////8BCwoBBAsBAAQHCwT/////BAcICQALCQsKCwAD/////wQHCwQLCQkLCv////////8J
    BQT/////////////////CQUEAAgD/////////////wAFBAEFAP////////////8IBQQIAwUDAQX/
    ////////AQIKCQUE/////////////wMACAECCgQJBf////////8FAgoFBAIEAAL/////////AgoF
    AwIFAwUEAwQI/////wkFBAIDC/////////////8ACwIACAsECQX/////////AAUEAAEFAgML////
    /////wIBBQIFCAIICwQIBf////8KAwsKAQMJBQT/////////BAkFAAgBCAoBCAsK/////wUEAAUA
    CwULCgsAA/////8FBAgFCAoKCAv/////////CQcIBQcJ/////////////wkDAAkFAwUHA///////
    //8ABwgAAQcBBQf/////////AQUDAwUH/////////////wkHCAkFBwoBAv////////8KAQIJBQAF
    AwAFBwP/////CAACCAIFCAUHCgUC/////wIKBQIFAwMFB/////////8HCQUHCAkDCwL/////////
    CQUHCQcCCQIAAgcL/////wIDCwABCAEHCAEFB/////8LAgELAQcHAQX/////////CQUICAUHCgED
    CgML/////wUHAAUACQcLAAEACgsKAP8LCgALAAMKBQAIAAcFBwD/CwoFBwsF/////////////woG
    Bf////////////////8ACAMFCgb/////////////CQABBQoG/////////////wEIAwEJCAUKBv//
    //////8BBgUCBgH/////////////AQYFAQIGAwAI/////////wkGBQkABgACBv////////8FCQgF
    CAIFAgYDAgj/////AgMLCgYF/////////////wsACAsCAAoGBf////////8AAQkCAwsFCgb/////
    ////BQoGAQkCCQsCCQgL/////wYDCwYFAwUBA/////////8ACAsACwUABQEFCwb/////AwsGAAMG
    AAYFAAUJ/////wYFCQYJCwsJCP////////8FCgYEBwj/////////////BAMABAcDBgUK////////
    /wEJAAUKBggEB/////////8KBgUBCQcBBwMHCQT/////BgECBgUBBAcI/////////wECBQUCBgMA
    BAMEB/////8IBAcJAAUABgUAAgb/////BwMJBwkEAwIJBQkGAgYJ/wMLAgcIBAoGBf////////8F
    CgYEBwIEAgACBwv/////AAEJBAcIAgMLBQoG/////wkCAQkLAgkECwcLBAUKBv8IBAcDCwUDBQEF
    Cwb/////BQELBQsGAQALBwsEAAQL/wAFCQAGBQADBgsGAwgEB/8GBQkGCQsEBwkHCwn/////CgQJ
    BgQK/////////////wQKBgQJCgAIA/////////8KAAEKBgAGBAD/////////CAMBCAEGCAYEBgEK
    /////wEECQECBAIGBP////////8DAAgBAgkCBAkCBgT/////AAIEBAIG/////////////wgDAggC
    BAQCBv////////8KBAkKBgQLAgP/////////AAgCAggLBAkKBAoG/////wMLAgABBgAGBAYBCv//
    //8GBAEGAQoECAECAQsICwH/CQYECQMGCQEDCwYD/////wgLAQgBAAsGAQkBBAYEAf8DCwYDBgAA
    BgT/////////BgQICwYI/////////////wcKBgcICggJCv////////8ABwMACgcACQoGBwr/////
    CgYHAQoHAQcIAQgA/////woGBwoHAQEHA/////////8BAgYBBgiBCAkIBgf/////AgYJAgkBBgcJ
    AAkDBwMJ/wcIAAcABgYAAv////////8HAwIGBwL/////////////AgMLCgYICggJCAYH/////wIA
    BwIHCwAJBwYHCgkKB/8BCAABBwgBCgcGBwoCAwv/CwIBCwEHCgYBBgcB/////wgJBggGBwkBBgsG
    AwEDBv8ACQELBgf/////////////BwgABwAGAwsACwYA/////wcLBv////////////////8HBgv/
    ////////////////AwAICwcG/////////////wABCQsHBv////////////8IAQkIAwELBwb/////
    ////CgECBgsH/////////////wECCgMACAYLB/////////8CCQACCgkGCwf/////////BgsHAgoD
    CggDCgkI/////wcCAwYCB/////////////8HAAgHBgAGAgD/////////AgcGAgMHAAEJ////////
    /wEGAgEIBgEJCAgHBv////8KBwYKAQcBAwf/////////CgcGAQcKAQgHAQAI/////wADBwAHCgAK
    CQYKB/////8HBgoHCggICgn/////////BggECwgG/////////////wMGCwMABgAEBv////////8I
    BgsIBAYJAAH/////////CQQGCQYDCQMBCwMG/////wYIBAYLCAIKAf////////8BAgoDAAsABgsA
    BAb/////BAsIBAYLAAIJAgoJ/////woJAwoDAgkEAwsDBgQGA/8IAgMIBAIEBgL/////////AAQC
    BAYC/////////////wEJAAIDBAIEBgQDCP////8BCQQBBAICBAb/////////CAEDCAYBCAQGBgoB
    /////woBAAoABgYABP////////8EBgMEAwgGCgMAAwkKCQP/CgkEBgoE/////////////wQJBQcG
    C/////////////8ACAMECQULBwb/////////BQABBQQABwYL/////////wsHBggDBAMFBAMBBf//
    //8JBQQKAQIHBgv/////////BgsHAQIKAAgDBAkF/////wcGCwUECgQCCgQAAv////8DBAgDBQQD
    AgUKBQILBwb/BwIDBwYCBQQJ/////////wkFBAAIBgAGAgYIB/////8DBgIDBwYBBQAFBAD/////
    BgIIBggHAgEIBAgFAQUI/wkFBAoBBgEHBgEDB/////8BBgoBBwYBAAcIBwAJBQT/BAAKBAoFAAMK
    BgoHAwcK/wcGCgcKCAUECgQICv////8GCQUGCwkLCAn/////////AwYLAAYDAAUGAAkF/////wAL
    CAAFCwABBQUGC/////8GCwMGAwUFAwH/////////AQIKCQULCQsICwUG/////wALAwAGCwAJBgUG
    CQECCv8LCAULBQYIAAUKBQIAAgX/BgsDBgMFAgoDCgUD/////wUICQUCCAUGAgMIAv////8JBQYJ
    BgAABgL/////////AQUIAQgABQYIAwgCBgII/wEFBgIBBv////////////8BAwYBBgoDCAYFBgkI
    CQb/CgEACgAGCQUABQYA/////wADCAUGCv////////////8KBQb/////////////////CwUKBwUL
    /////////////wsFCgsHBQgDAP////////8FCwcFCgsBCQD/////////CgcFCgsHCQgBCAMB////
    /wsBAgsHAQcFAf////////8ACAMBAgcBBwUHAgv/////CQcFCQIHCQACAgsH/////wcFAgcCCwUJ
    AgMCCAkIAv8CBQoCAwUDBwX/////////CAIACAUCCAcFCgIF/////wkAAQUKAwUDBwMKAv////8J
    CAIJAgEIBwIKAgUHBQL/AQMFAwcF/////////////wAIBwAHAQEHBf////////8JAAMJAwUFAwf/
    ////////CQgHBQkH/////////////wUIBAUKCAoLCP////////8FAAQFCwAFCgsLAwD/////AAEJ
    CAQKCAoLCgQF/////woLBAoEBQsDBAkEAQMBBP8CBQECCAUCCwgEBQj/////AAQLAAsDBAULAgsB
    BQEL/wACBQAFCQILBQQFCAsIBf8JBAUCCwP/////////////AgUKAwUCAwQFAwgE/////wUKAgUC
    BAQCAP////////8DCgIDBQoDCAUEBQgAAQn/BQoCBQIEAQkCCQQC/////wgEBQgFAwMFAf//////
    //8ABAUBAAX/////////////CAQFCAUDCQAFAAMF/////wkEBf////////////////8ECwcECQsJ
    Cgv/////////AAgDBAkHCQsHCQoL/////wEKCwELBAEEAAcEC/////8DAQQDBAgBCgQHBAsKCwT/
    BAsHCQsECQILCQEC/////wkHBAkLBwkBCwILAQAIA/8LBwQLBAICBAD/////////CwcECwQCCAME
    AwIE/////wIJCgIHCQIDBwcECf////8JCgcJBwQKAgcIBwACAAf/AwcKAwoCBwQKAQoABAAK/wEK
    AggHBP////////////8ECQEEAQcHAQP/////////BAkBBAEHAAgBCAcB/////wQAAwcEA///////
    //////8ECAf/////////////////CQoICgsI/////////////wMACQMJCwsJCv////////8AAQoA
    CggICgv/////////AwEKCwMK/////////////wECCwELCQkLCP////////8DAAkDCQsBAgkCCwn/
    ////AAILCAAL/////////////wMCC/////////////////8CAwgCCAoKCAn/////////CQoCAAkC
    /////////////wIDCAIICgABCAEKCP////8BCgL/////////////////AQMICQEI////////////
    /wAJAf////////////////8AAwj//////////////////////////////////////w==
    ";

        public static readonly int[,] TriTable = DecodeTriTable();

        private static int[,] DecodeTriTable()
        {
            string clean = TriTableBase64
                .Replace(" ", "")
                .Replace("\r", "")
                .Replace("\n", "")
                .Replace("\t", "");

            byte[] bytes = Convert.FromBase64String(clean);

            if (bytes.Length != 256 * 16)
                throw new InvalidOperationException("Invalid Marching Cubes triangle table length.");

            var table = new int[256, 16];

            for (int i = 0; i < 256; i++)
            {
                for (int j = 0; j < 16; j++)
                {
                    byte v = bytes[i * 16 + j];
                    table[i, j] = v == 255 ? -1 : v;
                }
            }

            return table;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SdfBox — axis-aligned box signed distance field in an arbitrary plane frame
    // ─────────────────────────────────────────────────────────────────────────

    internal sealed class SdfBox
    {
        private const double EPS = 1e-9;

        public Plane Plane;
        public Interval X;
        public Interval Y;
        public Interval Z;
        public BoundingBox WorldBox;
        public bool IsValid;

        public SdfBox(Plane plane, Interval x, Interval y, Interval z)
        {
            Plane = plane;
            X = Normalize(x);
            Y = Normalize(y);
            Z = Normalize(z);

            IsValid =
                Plane.IsValid &&
                X.Length > EPS &&
                Y.Length > EPS &&
                Z.Length > EPS;

            if (IsValid)
            {
                WorldBox = new Box(Plane, X, Y, Z).BoundingBox;
            }
            else
            {
                WorldBox = BoundingBox.Empty;
            }
        }

        public double SignedDistance(Point3d p)
        {
            Vector3d d = p - Plane.Origin;

            double lx = d * Plane.XAxis;
            double ly = d * Plane.YAxis;
            double lz = d * Plane.ZAxis;

            double cx = 0.5 * (X.T0 + X.T1);
            double cy = 0.5 * (Y.T0 + Y.T1);
            double cz = 0.5 * (Z.T0 + Z.T1);

            double hx = 0.5 * X.Length;
            double hy = 0.5 * Y.Length;
            double hz = 0.5 * Z.Length;

            double qx = Math.Abs(lx - cx) - hx;
            double qy = Math.Abs(ly - cy) - hy;
            double qz = Math.Abs(lz - cz) - hz;

            double ox = Math.Max(qx, 0.0);
            double oy = Math.Max(qy, 0.0);
            double oz = Math.Max(qz, 0.0);

            double outside = Math.Sqrt(ox * ox + oy * oy + oz * oz);
            double inside  = Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0.0);

            return outside + inside;
        }

        private static Interval Normalize(Interval i)
        {
            return new Interval(Math.Min(i.T0, i.T1), Math.Max(i.T0, i.T1));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ReferenceFrame — panel-local coordinate frame with cached transforms
    // ─────────────────────────────────────────────────────────────────────────

    internal struct ReferenceFrame
    {
        public Plane Plane;
        public Transform WorldToRef;
        public Transform RefToWorld;
    }
}
