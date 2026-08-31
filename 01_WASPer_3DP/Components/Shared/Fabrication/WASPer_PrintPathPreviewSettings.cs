using System;
using System.Drawing;

using Grasshopper;
using Grasshopper.Kernel;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP
{
    internal enum WasperPrintPathPreviewMode
    {
        WasperBlue = 0,
        RoleClassic = 1,
        RoleVivid = 2,
        NeutralGray = 3,
        ClayRawGrayRedware = 4,
        ClayFiredGrayRedware = 5,
        ClayRawRedEarthenware = 6,
        ClayFiredRedEarthenware = 7,
        ClayRawBuffEarthenware = 8,
        ClayFiredBuffEarthenware = 9,
        ClayRawWhiteStoneware = 10,
        ClayFiredWhiteStoneware = 11,
        ClayRawPinkClay = 12,
        ClayFiredPinkClay = 13,
        RoleBright = 14,
        RoleColorBlind = 15,
        Custom = 16,
        CustomByRole = 17
    }

    /// <summary>
    /// Application-level appearance preferences for the lightweight wsp_path
    /// polyline preview. WASPet exposes these values to the user.
    /// </summary>
    internal static class WasperPrintPathPreviewSettings
    {
        internal static event Action EnabledChanged;

        internal const bool DefaultEnabled = true;
        private const string EnabledKey = "WASPer_3DP.PrintPathPreview.Enabled";
        private const string ModeKey = "WASPer_3DP.PrintPathPreview.Mode";
        private const string CustomColorKey = "WASPer_3DP.PrintPathPreview.CustomColor";
        private const string CustomShellColorKey = "WASPer_3DP.PrintPathPreview.CustomRole.Shell";
        private const string CustomInfillColorKey = "WASPer_3DP.PrintPathPreview.CustomRole.Infill";
        private const string CustomPartitionColorKey = "WASPer_3DP.PrintPathPreview.CustomRole.Partition";
        private const string CustomSupportColorKey = "WASPer_3DP.PrintPathPreview.CustomRole.Support";
        private const string CustomTransitionColorKey = "WASPer_3DP.PrintPathPreview.CustomRole.Transition";
        private const string CustomUndefinedColorKey = "WASPer_3DP.PrintPathPreview.CustomRole.Undefined";
        private const string ThicknessKey = "WASPer_3DP.PrintPathPreview.Thickness";
        private const string ApplyToVisualizersKey = "WASPer_3DP.PrintPathPreview.ApplyToVisualizers";
        private const string AmbientKey = "WASPer_3DP.PrintPathPreview.Ambient";
        private const string ShadeStrengthKey = "WASPer_3DP.PrintPathPreview.ShadeStrength";
        private const string LightAzimuthKey = "WASPer_3DP.PrintPathPreview.LightAzimuth";
        private const string LightAltitudeKey = "WASPer_3DP.PrintPathPreview.LightAltitude";
        private const string BeadProfileExponentKey = "WASPer_3DP.PrintPathPreview.BeadProfileExponent";

        private static bool _loaded;
        private static bool _enabled = DefaultEnabled;
        private static WasperPrintPathPreviewMode _mode =
            WasperPrintPathPreviewMode.WasperBlue;
        private static Color _customColor = Color.FromArgb(61, 157, 221);
        private static Color _customShellColor = Color.FromArgb(225, 83, 74);
        private static Color _customInfillColor = Color.FromArgb(247, 187, 189);
        private static Color _customPartitionColor = Color.FromArgb(146, 197, 222);
        private static Color _customSupportColor = Color.FromArgb(174, 125, 190);
        private static Color _customTransitionColor = Color.FromArgb(238, 158, 65);
        private static Color _customUndefinedColor = Color.FromArgb(140, 140, 140);
        private static int _thickness = 2;
        private static bool _applyToVisualizers = true;
        private static double _ambient = 0.6;
        private static double _shadeStrength = 0.4;
        private static double _lightAzimuth;
        private static double _lightAltitude = 90.0;
        private static int _beadProfileExponent = 4;

        internal static bool Enabled
        {
            get { EnsureLoaded(); return _enabled; }
            set
            {
                EnsureLoaded();
                if (_enabled == value) return;
                _enabled = value;
                Save(EnabledKey, value);
                Redraw();
                EnabledChanged?.Invoke();
            }
        }

        internal static WasperPrintPathPreviewMode Mode
        {
            get { EnsureLoaded(); return _mode; }
            set
            {
                EnsureLoaded();
                if (!Enum.IsDefined(typeof(WasperPrintPathPreviewMode), value))
                    value = WasperPrintPathPreviewMode.WasperBlue;
                if (_mode == value) return;
                _mode = value;
                Save(ModeKey, (int)value);
                PaletteChanged();
            }
        }

        internal static Color CustomColor
        {
            get { EnsureLoaded(); return _customColor; }
            set
            {
                EnsureLoaded();
                if (_customColor.ToArgb() == value.ToArgb()) return;
                _customColor = Color.FromArgb(value.ToArgb());
                Save(CustomColorKey, _customColor.ToArgb());
                PaletteChanged();
            }
        }

        internal static int Thickness
        {
            get { EnsureLoaded(); return _thickness; }
            set
            {
                EnsureLoaded();
                value = Math.Max(1, Math.Min(5, value));
                if (_thickness == value) return;
                _thickness = value;
                Save(ThicknessKey, value);
                Redraw();
            }
        }

        internal static Color CustomRoleColor(WasperPathRole role)
        {
            EnsureLoaded();
            switch (role)
            {
                case WasperPathRole.Shell: return _customShellColor;
                case WasperPathRole.Infill: return _customInfillColor;
                case WasperPathRole.Partition: return _customPartitionColor;
                case WasperPathRole.Support: return _customSupportColor;
                case WasperPathRole.Transition: return _customTransitionColor;
                default: return _customUndefinedColor;
            }
        }

        internal static bool ApplyToVisualizers
        {
            get { EnsureLoaded(); return _applyToVisualizers; }
            set
            {
                EnsureLoaded();
                if (_applyToVisualizers == value) return;
                _applyToVisualizers = value;
                Save(ApplyToVisualizersKey, value);
                PaletteChanged();
            }
        }

        internal static double Ambient
        {
            get { EnsureLoaded(); return _ambient; }
            set
            {
                EnsureLoaded();
                value = Clamp(value, 0.0, 1.0);
                if (Math.Abs(_ambient - value) < 1e-9) return;
                _ambient = value;
                Save(AmbientKey, value);
                AppearanceChanged();
            }
        }

        internal static double ShadeStrength
        {
            get { EnsureLoaded(); return _shadeStrength; }
            set
            {
                EnsureLoaded();
                value = Clamp(value, 0.0, 1.0);
                if (Math.Abs(_shadeStrength - value) < 1e-9) return;
                _shadeStrength = value;
                Save(ShadeStrengthKey, value);
                AppearanceChanged();
            }
        }

        internal static double LightAzimuth
        {
            get { EnsureLoaded(); return _lightAzimuth; }
            set
            {
                EnsureLoaded();
                value = NormalizeDegrees(value);
                if (Math.Abs(_lightAzimuth - value) < 1e-9) return;
                _lightAzimuth = value;
                Save(LightAzimuthKey, value);
                AppearanceChanged();
            }
        }

        internal static double LightAltitude
        {
            get { EnsureLoaded(); return _lightAltitude; }
            set
            {
                EnsureLoaded();
                value = Clamp(value, -90.0, 90.0);
                if (Math.Abs(_lightAltitude - value) < 1e-9) return;
                _lightAltitude = value;
                Save(LightAltitudeKey, value);
                AppearanceChanged();
            }
        }

        // Direction in which light rays travel. Altitude 90 degrees therefore
        // produces (0,0,-1): light shining downward from directly above.
        internal static Vector3d LightDirection
        {
            get
            {
                EnsureLoaded();
                double azimuth = RhinoMath.ToRadians(_lightAzimuth);
                double altitude = RhinoMath.ToRadians(_lightAltitude);
                double horizontal = Math.Cos(altitude);
                return new Vector3d(
                    -horizontal * Math.Cos(azimuth),
                    -horizontal * Math.Sin(azimuth),
                    -Math.Sin(altitude));
            }
        }

        internal static int BeadProfileExponent
        {
            get { EnsureLoaded(); return _beadProfileExponent; }
            set
            {
                EnsureLoaded();
                value = ClosestProfileExponent(value);
                if (_beadProfileExponent == value) return;
                _beadProfileExponent = value;
                Save(BeadProfileExponentKey, value);
                AppearanceChanged();
            }
        }

        internal static void SetCustomRoleColor(
            WasperPathRole role,
            Color color)
        {
            EnsureLoaded();
            color = Color.FromArgb(color.ToArgb());
            switch (role)
            {
                case WasperPathRole.Shell:
                    _customShellColor = color;
                    Save(CustomShellColorKey, color.ToArgb());
                    break;
                case WasperPathRole.Infill:
                    _customInfillColor = color;
                    Save(CustomInfillColorKey, color.ToArgb());
                    break;
                case WasperPathRole.Partition:
                    _customPartitionColor = color;
                    Save(CustomPartitionColorKey, color.ToArgb());
                    break;
                case WasperPathRole.Support:
                    _customSupportColor = color;
                    Save(CustomSupportColorKey, color.ToArgb());
                    break;
                case WasperPathRole.Transition:
                    _customTransitionColor = color;
                    Save(CustomTransitionColorKey, color.ToArgb());
                    break;
                default:
                    _customUndefinedColor = color;
                    Save(CustomUndefinedColorKey, color.ToArgb());
                    break;
            }
            PaletteChanged();
        }

        internal static void Reset()
        {
            EnsureLoaded();
            _enabled = DefaultEnabled;
            _mode = WasperPrintPathPreviewMode.WasperBlue;
            _customColor = Color.FromArgb(61, 157, 221);
            ResetCustomRoleColors();
            _thickness = 2;
            _applyToVisualizers = true;
            _ambient = 0.6;
            _shadeStrength = 0.4;
            _lightAzimuth = 0.0;
            _lightAltitude = 90.0;
            _beadProfileExponent = 4;
            Save(EnabledKey, _enabled);
            Save(ModeKey, (int)_mode);
            Save(CustomColorKey, _customColor.ToArgb());
            SaveCustomRoleColors();
            Save(ThicknessKey, _thickness);
            Save(ApplyToVisualizersKey, _applyToVisualizers);
            Save(AmbientKey, _ambient);
            Save(ShadeStrengthKey, _shadeStrength);
            Save(LightAzimuthKey, _lightAzimuth);
            Save(LightAltitudeKey, _lightAltitude);
            Save(BeadProfileExponentKey, _beadProfileExponent);
            PaletteChanged();
            EnabledChanged?.Invoke();
        }

        internal static Color ResolveColor(WasperPathRole role)
        {
            EnsureLoaded();
            switch (_mode)
            {
                case WasperPrintPathPreviewMode.RoleClassic:
                    return ClassicRoleColor(role);
                case WasperPrintPathPreviewMode.RoleVivid:
                    return VividRoleColor(role);
                case WasperPrintPathPreviewMode.ClayRawGrayRedware:
                case WasperPrintPathPreviewMode.ClayFiredGrayRedware:
                case WasperPrintPathPreviewMode.ClayRawRedEarthenware:
                case WasperPrintPathPreviewMode.ClayFiredRedEarthenware:
                case WasperPrintPathPreviewMode.ClayRawBuffEarthenware:
                case WasperPrintPathPreviewMode.ClayFiredBuffEarthenware:
                case WasperPrintPathPreviewMode.ClayRawWhiteStoneware:
                case WasperPrintPathPreviewMode.ClayFiredWhiteStoneware:
                case WasperPrintPathPreviewMode.ClayRawPinkClay:
                case WasperPrintPathPreviewMode.ClayFiredPinkClay:
                    return ClayMaterialColor(_mode);
                case WasperPrintPathPreviewMode.RoleBright:
                    return BrightRoleColor(role);
                case WasperPrintPathPreviewMode.RoleColorBlind:
                    return ColorBlindRoleColor(role);
                case WasperPrintPathPreviewMode.NeutralGray:
                    return Color.FromArgb(140, 140, 140);
                case WasperPrintPathPreviewMode.Custom:
                    return _customColor;
                case WasperPrintPathPreviewMode.CustomByRole:
                    return CustomRoleColor(role);
                default:
                    return Color.FromArgb(61, 157, 221);
            }
        }

        internal static Color[] ResolveRolePalette()
        {
            return new[]
            {
                ResolveColor(WasperPathRole.Shell),
                ResolveColor(WasperPathRole.Infill),
                ResolveColor(WasperPathRole.Partition),
                ResolveColor(WasperPathRole.Support),
                ResolveColor(WasperPathRole.Transition),
                ResolveColor(WasperPathRole.Undefined)
            };
        }

        private static Color ClassicRoleColor(WasperPathRole role)
        {
            switch (role)
            {
                case WasperPathRole.Shell: return Color.FromArgb(225, 83, 74);
                case WasperPathRole.Infill: return Color.FromArgb(247, 187, 189);
                case WasperPathRole.Partition: return Color.FromArgb(146, 197, 222);
                case WasperPathRole.Support: return Color.FromArgb(174, 125, 190);
                case WasperPathRole.Transition: return Color.FromArgb(238, 158, 65);
                default: return Color.FromArgb(140, 140, 140);
            }
        }

        private static Color VividRoleColor(WasperPathRole role)
        {
            switch (role)
            {
                case WasperPathRole.Shell: return Color.FromArgb(198, 40, 40);
                case WasperPathRole.Infill: return Color.FromArgb(255, 152, 0);
                case WasperPathRole.Partition: return Color.FromArgb(0, 150, 136);
                case WasperPathRole.Support: return Color.FromArgb(124, 77, 255);
                case WasperPathRole.Transition: return Color.FromArgb(255, 193, 7);
                default: return Color.FromArgb(66, 66, 66);
            }
        }

        private static Color BrightRoleColor(WasperPathRole role)
        {
            switch (role)
            {
                case WasperPathRole.Shell: return Color.FromArgb(255, 32, 92);
                case WasperPathRole.Infill: return Color.FromArgb(255, 214, 10);
                case WasperPathRole.Partition: return Color.FromArgb(0, 229, 255);
                case WasperPathRole.Support: return Color.FromArgb(155, 77, 255);
                case WasperPathRole.Transition: return Color.FromArgb(57, 255, 20);
                default: return Color.FromArgb(35, 35, 35);
            }
        }

        private static Color ColorBlindRoleColor(WasperPathRole role)
        {
            switch (role)
            {
                case WasperPathRole.Shell: return Color.FromArgb(213, 94, 0);
                case WasperPathRole.Infill: return Color.FromArgb(0, 114, 178);
                case WasperPathRole.Partition: return Color.FromArgb(0, 158, 115);
                case WasperPathRole.Support: return Color.FromArgb(204, 121, 167);
                case WasperPathRole.Transition: return Color.FromArgb(240, 228, 66);
                default: return Color.FromArgb(102, 102, 102);
            }
        }

        private static Color ClayMaterialColor(WasperPrintPathPreviewMode mode)
        {
            switch (mode)
            {
                case WasperPrintPathPreviewMode.ClayRawGrayRedware:
                    return Color.FromArgb(138, 136, 128);
                case WasperPrintPathPreviewMode.ClayFiredGrayRedware:
                    return Color.FromArgb(154, 73, 45);
                case WasperPrintPathPreviewMode.ClayRawRedEarthenware:
                    return Color.FromArgb(145, 77, 57);
                case WasperPrintPathPreviewMode.ClayFiredRedEarthenware:
                    return Color.FromArgb(196, 88, 51);
                case WasperPrintPathPreviewMode.ClayRawBuffEarthenware:
                    return Color.FromArgb(174, 148, 109);
                case WasperPrintPathPreviewMode.ClayFiredBuffEarthenware:
                    return Color.FromArgb(218, 184, 128);
                case WasperPrintPathPreviewMode.ClayRawWhiteStoneware:
                    return Color.FromArgb(205, 200, 184);
                case WasperPrintPathPreviewMode.ClayFiredWhiteStoneware:
                    return Color.FromArgb(232, 222, 199);
                case WasperPrintPathPreviewMode.ClayRawPinkClay:
                    return Color.FromArgb(211, 170, 158);
                case WasperPrintPathPreviewMode.ClayFiredPinkClay:
                    return Color.FromArgb(229, 157, 147);
                default:
                    return Color.FromArgb(140, 140, 140);
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                _enabled = Instances.Settings.GetValue(EnabledKey, DefaultEnabled);
                int mode = Instances.Settings.GetValue(ModeKey, 0);
                _mode = Enum.IsDefined(typeof(WasperPrintPathPreviewMode), mode)
                    ? (WasperPrintPathPreviewMode)mode
                    : WasperPrintPathPreviewMode.WasperBlue;
                _customColor = Color.FromArgb(
                    Instances.Settings.GetValue(
                        CustomColorKey,
                        Color.FromArgb(61, 157, 221).ToArgb()));
                _customShellColor = LoadColor(CustomShellColorKey, ClassicRoleColor(WasperPathRole.Shell));
                _customInfillColor = LoadColor(CustomInfillColorKey, ClassicRoleColor(WasperPathRole.Infill));
                _customPartitionColor = LoadColor(CustomPartitionColorKey, ClassicRoleColor(WasperPathRole.Partition));
                _customSupportColor = LoadColor(CustomSupportColorKey, ClassicRoleColor(WasperPathRole.Support));
                _customTransitionColor = LoadColor(CustomTransitionColorKey, ClassicRoleColor(WasperPathRole.Transition));
                _customUndefinedColor = LoadColor(CustomUndefinedColorKey, ClassicRoleColor(WasperPathRole.Undefined));
                _applyToVisualizers = Instances.Settings.GetValue(ApplyToVisualizersKey, true);
                _thickness = Math.Max(
                    1,
                    Math.Min(5, Instances.Settings.GetValue(ThicknessKey, 2)));
                _ambient = Clamp(Instances.Settings.GetValue(AmbientKey, 0.6), 0.0, 1.0);
                _shadeStrength = Clamp(Instances.Settings.GetValue(ShadeStrengthKey, 0.4), 0.0, 1.0);
                _lightAzimuth = NormalizeDegrees(Instances.Settings.GetValue(LightAzimuthKey, 0.0));
                _lightAltitude = Clamp(Instances.Settings.GetValue(LightAltitudeKey, 90.0), -90.0, 90.0);
                _beadProfileExponent = ClosestProfileExponent(
                    Instances.Settings.GetValue(BeadProfileExponentKey, 4));
            }
            catch
            {
                _enabled = DefaultEnabled;
                _mode = WasperPrintPathPreviewMode.WasperBlue;
                _customColor = Color.FromArgb(61, 157, 221);
                ResetCustomRoleColors();
                _thickness = 2;
                _applyToVisualizers = true;
                _ambient = 0.6;
                _shadeStrength = 0.4;
                _lightAzimuth = 0.0;
                _lightAltitude = 90.0;
                _beadProfileExponent = 4;
            }
        }

        private static Color LoadColor(string key, Color fallback)
        {
            return Color.FromArgb(
                Instances.Settings.GetValue(key, fallback.ToArgb()));
        }

        private static void ResetCustomRoleColors()
        {
            _customShellColor = ClassicRoleColor(WasperPathRole.Shell);
            _customInfillColor = ClassicRoleColor(WasperPathRole.Infill);
            _customPartitionColor = ClassicRoleColor(WasperPathRole.Partition);
            _customSupportColor = ClassicRoleColor(WasperPathRole.Support);
            _customTransitionColor = ClassicRoleColor(WasperPathRole.Transition);
            _customUndefinedColor = ClassicRoleColor(WasperPathRole.Undefined);
        }

        private static void SaveCustomRoleColors()
        {
            Save(CustomShellColorKey, _customShellColor.ToArgb());
            Save(CustomInfillColorKey, _customInfillColor.ToArgb());
            Save(CustomPartitionColorKey, _customPartitionColor.ToArgb());
            Save(CustomSupportColorKey, _customSupportColor.ToArgb());
            Save(CustomTransitionColorKey, _customTransitionColor.ToArgb());
            Save(CustomUndefinedColorKey, _customUndefinedColor.ToArgb());
        }

        private static void PaletteChanged()
        {
            RefreshVisualizers();
            Redraw();
        }

        // Ambient, ShadeStrength, LightAzimuth/Altitude (via LightDirection), and
        // BeadProfileExponent are pure shader uniforms read live every frame by
        // WasperPrintPathSegmentRenderer.Draw() - they are never baked into the
        // preview batch geometry built during SolveInstance. So unlike
        // PaletteChanged() (which does affect batch-baked colors and must
        // re-solve), these only need a viewport redraw - no ExpireSolution, no
        // rebuilt batch, no flicker while dragging a slider.
        private static void AppearanceChanged()
        {
            Redraw();
        }

        private static void RefreshVisualizers()
        {
            GH_Document document = Instances.ActiveCanvas?.Document;
            if (document == null)
                return;

            var pp04 = new Guid("B6E4A2C1-7D93-4F80-AB16-5C9E2D7F4381");
            var sl07 = new Guid("D4F2F6D1-7C1B-4C2F-9E3A-6E887C1F9E0B");
            var pp18 = new Guid("6D830E77-1C39-49B7-ADCE-519BC62B75D4");
            var ut15 = new Guid("D8E4A1F2-6B73-4C90-9E15-2A7D5B8C4310");
            var sm01 = new Guid("F2D4C8B6-2A4E-4F92-9D54-8B2E6C7A1F30");
            bool expired = false;
            foreach (IGH_DocumentObject obj in document.Objects)
            {
                if (obj.ComponentGuid != pp04 && obj.ComponentGuid != sl07 &&
                    obj.ComponentGuid != pp18 && obj.ComponentGuid != ut15 &&
                    obj.ComponentGuid != sm01)
                    continue;
                if (obj is GH_ActiveObject active)
                {
                    active.ExpireSolution(false);
                    expired = true;
                }
            }

            if (expired)
                document.ScheduleSolution(1, _ => { });
        }

        private static void Save(string key, bool value)
        {
            try { Instances.Settings.SetValue(key, value); }
            catch { }
        }

        private static void Save(string key, int value)
        {
            try { Instances.Settings.SetValue(key, value); }
            catch { }
        }

        private static void Save(string key, double value)
        {
            try { Instances.Settings.SetValue(key, value); }
            catch { }
        }

        private static double Clamp(double value, double min, double max) =>
            Math.Max(min, Math.Min(max, value));

        private static double NormalizeDegrees(double value)
        {
            value %= 360.0;
            if (value > 180.0) value -= 360.0;
            if (value <= -180.0) value += 360.0;
            return value;
        }

        private static int ClosestProfileExponent(int value)
        {
            if (value <= 2) return 2;
            if (value == 3) return 3;
            if (value <= 5) return 4;
            return 6;
        }

        internal static void Redraw()
        {
            try { Instances.RedrawCanvas(); }
            catch { }
            try { RhinoDoc.ActiveDoc?.Views.Redraw(); }
            catch { }
        }
    }
}
