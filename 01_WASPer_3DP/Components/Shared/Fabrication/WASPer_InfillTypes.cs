using System;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP
{
    public interface IWasperInfillParams
    {
        string InfillKind { get; }
        int SchemaVersion { get; }
        string Validate();
    }

    public interface IWasperInfillParamsGoo
    {
        IWasperInfillParams InfillParams { get; }
    }

    public sealed class WasperTpmsInfillParams : IWasperInfillParams
    {
        public string InfillKind => "TPMS";
        public int SchemaVersion { get; set; } = 1;
        public int Type { get; set; } = 2;
        public double Level { get; set; }
        public double CountX { get; set; } = 3.0;
        public double CountY { get; set; } = 1.0;
        public double CountZ { get; set; } = 4.0;
        public double PhaseX { get; set; }
        public double PhaseY { get; set; }
        public double PhaseZ { get; set; }
        public bool CloseTpms { get; set; }
        public bool InvertTpms { get; set; }

        public string Validate()
        {
            if (Type < 0 || Type > 7) return "type must be between 0 and 7.";
            if (!Finite(Level)) return "level must be finite.";
            if (!Finite(CountX) || CountX <= 0.0) return "c_x must be finite and > 0.";
            if (!Finite(CountY) || CountY <= 0.0) return "c_y must be finite and > 0.";
            if (!Finite(CountZ) || CountZ < 0.0) return "c_z must be finite and >= 0.";
            if (!Finite(PhaseX) || !Finite(PhaseY) || !Finite(PhaseZ)) return "phase values must be finite.";
            return null;
        }

        public override string ToString() =>
            $"WASPer TPMS Infill Params | {Tag(Type)} | c={CountX:0.###},{CountY:0.###},{CountZ:0.###} | close={CloseTpms} | invert={InvertTpms}";

        public static string Tag(int type)
        {
            return type switch
            {
                0 => "Prim",
                1 => "Diam",
                2 => "Gyr",
                3 => "IWP",
                4 => "Neo",
                5 => "Lidi",
                6 => "FK-S",
                7 => "FK-Y",
                _ => "?"
            };
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class WasperTpmsInfillParamsGoo : WasperJsonGoo<WasperTpmsInfillParams>, IWasperInfillParamsGoo
    {
        public WasperTpmsInfillParamsGoo() { }
        public WasperTpmsInfillParamsGoo(WasperTpmsInfillParams value) : base(value) { }
        protected override string StorageKey => "wasper_tpms_infill_params_json";
        protected override WasperJsonGoo<WasperTpmsInfillParams> Create(WasperTpmsInfillParams value) =>
            new WasperTpmsInfillParamsGoo(value);
        public override string TypeName => "WASPer TPMS Infill Params";
        public override string TypeDescription => "Typed TPMS pattern settings for WASPer multi-infill components.";
        public IWasperInfillParams InfillParams => Value;
    }

    public sealed class WasperPolyhedralInfillParams : IWasperInfillParams
    {
        public string InfillKind => "Polyhedral";
        public int SchemaVersion { get; set; } = 1;
        public int Type { get; set; } = 1;
        public int CountX { get; set; } = 3;
        public int CountY { get; set; } = 3;
        public int CountZ { get; set; } = 3;
        public bool InvertPolyhedral { get; set; }

        public string Validate()
        {
            if (Type < 0 || Type > 1)
                return "type must be 0 (Truncated Octahedron) or 1 (Octahedron).";
            if (CountX < 1) return "c_x must be >= 1.";
            if (CountY < 1) return "c_y must be >= 1.";
            if (CountZ < 1) return "c_z must be >= 1.";
            return null;
        }

        public override string ToString() =>
            $"WASPer Polyhedral Infill Params | {Tag(Type)} | c={CountX},{CountY},{CountZ} | invert={InvertPolyhedral}";

        public static string Tag(int type)
        {
            return type switch
            {
                0 => "TruncOct",
                1 => "Oct",
                _ => "?"
            };
        }

        public static string Name(int type)
        {
            return type switch
            {
                0 => "Truncated Octahedron",
                1 => "Octahedron",
                _ => "?"
            };
        }
    }

    public sealed class WasperPolyhedralInfillParamsGoo :
        WasperJsonGoo<WasperPolyhedralInfillParams>,
        IWasperInfillParamsGoo
    {
        public WasperPolyhedralInfillParamsGoo() { }
        public WasperPolyhedralInfillParamsGoo(WasperPolyhedralInfillParams value) : base(value) { }
        protected override string StorageKey => "wasper_polyhedral_infill_params_json";
        protected override WasperJsonGoo<WasperPolyhedralInfillParams> Create(WasperPolyhedralInfillParams value) =>
            new WasperPolyhedralInfillParamsGoo(value);
        public override string TypeName => "WASPer Polyhedral Infill Params";
        public override string TypeDescription =>
            "Typed polyhedral-cell settings for WASPer volumetric multi-infill components.";
        public IWasperInfillParams InfillParams => Value;
    }

    public sealed class WasperTurtleInfillParams : IWasperInfillParams
    {
        public string InfillKind => "Turtle";
        public int SchemaVersion { get; set; } = 1;
        public double PathWidth { get; set; } = 4.0;
        public int CountX { get; set; } = 6;
        public int CountY { get; set; } = 1;
        public double CountZ { get; set; } = 1.0;
        public double Bridge0 { get; set; }
        public double Bridge1 { get; set; } = 1.0;
        public double ExtendEnds { get; set; }
        public bool Teeth { get; set; }

        public string Validate()
        {
            if (!Finite(PathWidth) || PathWidth <= 0.0) return "p_width must be finite and > 0.";
            if (CountX < 1) return "c_x must be >= 1.";
            if (CountY < 1) return "c_y must be >= 1.";
            if (!Finite(CountZ) || CountZ < 0.0) return "c_z must be finite and >= 0.";
            if (!Finite(Bridge0) || Bridge0 < 0.0 || Bridge0 > 1.0) return "bridge_p_0 must be between 0 and 1.";
            if (!Finite(Bridge1) || Bridge1 < 0.0 || Bridge1 > 1.0) return "bridge_p_1 must be between 0 and 1.";
            if (!Finite(ExtendEnds) || ExtendEnds < 0.0) return "extend_ends must be finite and >= 0.";
            return null;
        }

        public override string ToString() =>
            $"WASPer Turtle Infill Params | c={CountX},{CountY},{CountZ:0.###} | width={PathWidth:0.###} | teeth={Teeth}";

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class WasperTurtleInfillParamsGoo : WasperJsonGoo<WasperTurtleInfillParams>, IWasperInfillParamsGoo
    {
        public WasperTurtleInfillParamsGoo() { }
        public WasperTurtleInfillParamsGoo(WasperTurtleInfillParams value) : base(value) { }
        protected override string StorageKey => "wasper_turtle_infill_params_json";
        protected override WasperJsonGoo<WasperTurtleInfillParams> Create(WasperTurtleInfillParams value) =>
            new WasperTurtleInfillParamsGoo(value);
        public override string TypeName => "WASPer Turtle Infill Params";
        public override string TypeDescription => "Typed Turtle-cell pattern settings for WASPer multi-infill components.";
        public IWasperInfillParams InfillParams => Value;
    }

    public sealed class WasperInfill2DParams : IWasperInfillParams
    {
        public string InfillKind => "2D";
        public int SchemaVersion { get; set; } = 1;
        public int Type { get; set; } = 4;
        public bool Flip { get; set; }
        public int Count { get; set; } = 4;
        public double PhaseShift { get; set; }

        public string Validate()
        {
            if (Type < 1 || Type > 4)
                return "type must be between 1 and 4 (1=Square S, 2=Sticks, 3=Triangle, 4=Sine).";
            if (Count < 1) return "count must be >= 1.";
            if (!Finite(PhaseShift)) return "phase_shift must be finite.";
            return null;
        }

        public override string ToString() =>
            $"WASPer 2D Infill Params | {Tag(Type)} | count={Count} | phase={PhaseShift:0.###} | flip={Flip}";

        public static string Tag(int type)
        {
            return type switch
            {
                1 => "SquareS",
                2 => "Sticks",
                3 => "Tri",
                4 => "Sine",
                _ => "?"
            };
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class WasperInfill2DParamsGoo : WasperJsonGoo<WasperInfill2DParams>, IWasperInfillParamsGoo
    {
        public WasperInfill2DParamsGoo() { }
        public WasperInfill2DParamsGoo(WasperInfill2DParams value) : base(value) { }
        protected override string StorageKey => "wasper_infill_2d_params_json";
        protected override WasperJsonGoo<WasperInfill2DParams> Create(WasperInfill2DParams value) =>
            new WasperInfill2DParamsGoo(value);
        public override string TypeName => "WASPer 2D Infill Params";
        public override string TypeDescription =>
            "Typed Square-S, Sticks, Triangle, or Sine centreline settings for layered WASPer multi-infill components.";
        public IWasperInfillParams InfillParams => Value;
    }

    public sealed class WasperBrickInfillParams : IWasperInfillParams
    {
        public string InfillKind => "Brick-like";
        public int SchemaVersion { get; set; } = 1;
        public int CountU { get; set; } = 3;
        public int CountV { get; set; } = 2;
        public int CavityDirection { get; set; } = 1;
        public bool Invert { get; set; }

        public string Validate()
        {
            if (CountU < 1) return "count_u must be >= 1.";
            if (CountV < 1) return "count_v must be >= 1.";
            if (CavityDirection < 1 || CavityDirection > 3)
                return "cav_dir must be 1 (local W), 2 (local U), or 3 (local V).";
            return null;
        }

        public override string ToString() =>
            $"WASPer Brick-like Params | cavities={CountU}x{CountV} | dir={DirectionName(CavityDirection)} | invert={Invert}";

        public static string DirectionName(int direction)
        {
            return direction switch
            {
                1 => "W",
                2 => "U",
                3 => "V",
                _ => "?"
            };
        }
    }

    public sealed class WasperBrickInfillParamsGoo :
        WasperJsonGoo<WasperBrickInfillParams>,
        IWasperInfillParamsGoo
    {
        public WasperBrickInfillParamsGoo() { }
        public WasperBrickInfillParamsGoo(WasperBrickInfillParams value) : base(value) { }
        protected override string StorageKey => "wasper_brick_infill_params_json";
        protected override WasperJsonGoo<WasperBrickInfillParams> Create(WasperBrickInfillParams value) =>
            new WasperBrickInfillParamsGoo(value);
        public override string TypeName => "WASPer Brick-like Infill Params";
        public override string TypeDescription =>
            "Typed cavity-count and run-direction settings for volumetric WASPer multi-infill components.";
        public IWasperInfillParams InfillParams => Value;
    }

    public static class WasperInfillParamsTools
    {
        public static IWasperInfillParams Unwrap(object raw)
        {
            if (raw is IWasperInfillParams direct) return direct;
            if (raw is IWasperInfillParamsGoo typedGoo) return typedGoo.InfillParams;
            if (raw is GH_ObjectWrapper wrapper) return Unwrap(wrapper.Value);
            return null;
        }
    }
}
