using System;

using Grasshopper.Kernel.Types;

using Rhino.Geometry;

namespace WASPer_3DP
{
    internal sealed class WasperBinderSettings
    {
        internal WasperFieldBitmapMode Mode { get; }
        internal double Threshold { get; }
        internal Interval FieldRange { get; }
        internal bool Invert { get; }

        internal WasperBinderSettings(
            WasperFieldBitmapMode mode,
            double threshold,
            Interval fieldRange,
            bool invert)
        {
            Mode = mode;
            Threshold = threshold;
            FieldRange = fieldRange;
            Invert = invert;
        }

        internal static WasperBinderSettings Default =>
            new WasperBinderSettings(
                WasperFieldBitmapMode.Binary,
                0.0,
                new Interval(-1.0, 1.0),
                false);

        internal string Summary => Mode == WasperFieldBitmapMode.Binary
            ? $"Binary | threshold={Threshold:0.###} | black={(Invert ? "no binder" : "binder")}"
            : $"Grayscale | range={Math.Min(FieldRange.T0, FieldRange.T1):0.###} to {Math.Max(FieldRange.T0, FieldRange.T1):0.###} | black={(Invert ? "no binder" : "binder")}";
    }

    internal sealed class WasperBinderSettingsGoo : GH_Goo<WasperBinderSettings>
    {
        internal WasperBinderSettingsGoo() : base((WasperBinderSettings)null) { }
        internal WasperBinderSettingsGoo(WasperBinderSettings settings) : base(settings) { }

        public override bool IsValid => Value != null;
        public override string TypeName => "WASPer Binder Settings";
        public override string TypeDescription => "Field-to-binder mapping settings for a WASPer raster stack.";
        public override IGH_Goo Duplicate() => new WasperBinderSettingsGoo(Value);
        public override string ToString() => Value == null ? "Null Binder Settings" : Value.Summary;
        public override bool Write(GH_IO.Serialization.GH_IWriter writer) => true;
        public override bool Read(GH_IO.Serialization.GH_IReader reader) => true;
    }

    internal sealed class WasperRasterStack
    {
        internal WasperField Field { get; }
        internal WasperFieldBitmapLayout Layout { get; }
        internal WasperBinderSettings Settings { get; }
        internal bool HasFixedFrame { get; }

        internal WasperRasterStack(
            WasperField field,
            WasperFieldBitmapLayout layout,
            WasperBinderSettings settings,
            bool hasFixedFrame)
        {
            Field = field;
            Layout = layout;
            Settings = settings ?? WasperBinderSettings.Default;
            HasFixedFrame = hasFixedFrame;
        }

        internal string Summary =>
            $"{Layout.LayerCount} layers | {Layout.Width}x{Layout.Height} px | " +
            $"{Layout.SizeX:0.###}x{Layout.SizeY:0.###} units | " +
            $"pixel={Layout.PixelSizeX:0.###}x{Layout.PixelSizeY:0.###} | {Settings.Summary}";
    }

    internal sealed class WasperRasterStackGoo : GH_Goo<WasperRasterStack>
    {
        internal WasperRasterStackGoo() : base((WasperRasterStack)null) { }
        internal WasperRasterStackGoo(WasperRasterStack stack) : base(stack) { }

        public override bool IsValid => Value?.Field?.Evaluator != null && Value.Layout != null;
        public override string TypeName => "WASPer Raster Stack";
        public override string TypeDescription => "A lazy binder-jet raster job. Pixels are generated only by preview or export components.";
        public override IGH_Goo Duplicate() => new WasperRasterStackGoo(Value);
        public override string ToString() => Value == null ? "Null Raster Stack" : $"WASPer Raster Stack | {Value.Summary}";
        public override bool Write(GH_IO.Serialization.GH_IWriter writer) => true;
        public override bool Read(GH_IO.Serialization.GH_IReader reader) => true;
    }

    internal static class WasperRasterData
    {
        internal static WasperField ExtractField(object source)
        {
            object current = source;
            for (int depth = 0; depth < 5 && current is IGH_Goo goo; depth++)
            {
                if (goo is WasperFieldGoo fieldGoo) return fieldGoo.Value;
                if (goo is GH_ObjectWrapper wrapper)
                {
                    current = wrapper.Value;
                    continue;
                }
                try { current = goo.ScriptVariable(); }
                catch { break; }
            }
            return current as WasperField;
        }

        internal static WasperRasterStack ExtractStack(object source)
        {
            object current = source;
            for (int depth = 0; depth < 5 && current is IGH_Goo goo; depth++)
            {
                if (goo is WasperRasterStackGoo stackGoo) return stackGoo.Value;
                if (goo is GH_ObjectWrapper wrapper)
                {
                    current = wrapper.Value;
                    continue;
                }
                try { current = goo.ScriptVariable(); }
                catch { break; }
            }
            return current as WasperRasterStack;
        }

        internal static WasperBinderSettings ExtractSettings(object source)
        {
            object current = source;
            for (int depth = 0; depth < 5 && current is IGH_Goo goo; depth++)
            {
                if (goo is WasperBinderSettingsGoo settingsGoo) return settingsGoo.Value;
                if (goo is GH_ObjectWrapper wrapper)
                {
                    current = wrapper.Value;
                    continue;
                }
                try { current = goo.ScriptVariable(); }
                catch { break; }
            }
            return current as WasperBinderSettings;
        }
    }
}
