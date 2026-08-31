using System;

using Rhino.Geometry;

namespace WASPer_3DP.Painting
{
    internal static class WasperPaintBrushKernel
    {
        internal static double Influence(double distance, double radius, double falloff)
        {
            if (radius <= 0.0 || distance > radius)
                return 0.0;
            return Math.Pow(
                Math.Max(0.0, 1.0 - distance / radius),
                Math.Max(falloff, Rhino.RhinoMath.ZeroTolerance));
        }

        internal static double DirectionalValue(
            double oldValue,
            WasperPaintTool tool,
            Interval domain,
            double amount)
        {
            double min = Math.Min(domain.T0, domain.T1);
            double max = Math.Max(domain.T0, domain.T1);
            double target = tool == WasperPaintTool.Pull
                ? Math.Max(0.0, max)
                : tool == WasperPaintTool.Push
                    ? Math.Min(0.0, min)
                    : 0.0;
            double start = oldValue;
            if (tool == WasperPaintTool.Pull && start < 0.0)
                start = 0.0;
            else if (tool == WasperPaintTool.Push && start > 0.0)
                start = 0.0;
            return ClampToDomain(
                WasperPaintUtilities.Lerp(start, target, amount),
                domain);
        }

        internal static double SmoothValue(
            double oldValue,
            double weightedAverage,
            double amount,
            Interval domain)
        {
            return ClampToDomain(
                WasperPaintUtilities.Lerp(oldValue, weightedAverage, amount),
                domain);
        }

        internal static double ClampToDomain(double value, Interval domain)
        {
            double min = Math.Min(domain.T0, domain.T1);
            double max = Math.Max(domain.T0, domain.T1);
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
