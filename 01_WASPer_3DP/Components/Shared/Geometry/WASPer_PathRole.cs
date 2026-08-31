using System;

using Rhino.Geometry;

namespace WASPer_3DP
{
    /// <summary>
    /// Semantic printing role attached to sliced curves and later carried by a
    /// WasperPrintPath. Integer values are stable for downstream data trees.
    /// </summary>
    public enum WasperPathRole
    {
        Undefined = 0,
        Shell = 1,
        Infill = 2,
        Partition = 3,
        Support = 4,
        Transition = 5
    }

    /// <summary>
    /// Shared Rhino-curve metadata API for WASPer printing roles. Curves remain
    /// ordinary Rhino Curve objects; the role is stored as a user string.
    /// Curve-producing operations should explicitly copy or reassign the role.
    /// </summary>
    public static class WasperPathRoleMetadata
    {
        public const string RoleKey = "WASPer.PathRole";

        public static void Set(Curve curve, WasperPathRole role)
        {
            if (curve == null)
                return;

            curve.SetUserString(RoleKey, RoleName(role));
        }

        public static WasperPathRole Get(Curve curve)
        {
            if (curve == null)
                return WasperPathRole.Undefined;

            string value = curve.GetUserString(RoleKey);
            if (string.IsNullOrWhiteSpace(value))
                return WasperPathRole.Undefined;

            if (Enum.TryParse(value, true, out WasperPathRole parsed) &&
                Enum.IsDefined(typeof(WasperPathRole), parsed))
                return parsed;

            if (int.TryParse(value, out int code) &&
                Enum.IsDefined(typeof(WasperPathRole), code))
                return (WasperPathRole)code;

            return WasperPathRole.Undefined;
        }

        public static void Copy(Curve source, Curve target)
        {
            if (target != null)
                Set(target, Get(source));
        }

        public static string RoleName(WasperPathRole role)
        {
            switch (role)
            {
                case WasperPathRole.Shell: return "Shell";
                case WasperPathRole.Infill: return "Infill";
                case WasperPathRole.Partition: return "Partition";
                case WasperPathRole.Support: return "Support";
                case WasperPathRole.Transition: return "Transition";
                default: return "Undefined";
            }
        }
    }
}
