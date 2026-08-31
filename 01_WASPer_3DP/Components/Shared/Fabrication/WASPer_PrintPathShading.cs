using System;
using System.Drawing;

using Rhino.Geometry;

namespace WASPer_3DP
{
    internal static class WasperPrintPathShading
    {
        internal static void Apply(
            Mesh mesh,
            Color baseColor,
            Vector3d rayDirection,
            double ambient,
            double shadeStrength)
        {
            if (mesh == null || mesh.Vertices.Count == 0)
                return;

            if (mesh.Normals.Count != mesh.Vertices.Count)
                mesh.Normals.ComputeNormals();

            if (!rayDirection.IsValid || !rayDirection.Unitize())
                rayDirection = -Vector3d.ZAxis;
            Vector3d towardLight = -rayDirection;
            ambient = Clamp01(ambient);
            shadeStrength = Clamp01(shadeStrength);

            mesh.VertexColors.Clear();
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                Vector3d normal = i < mesh.Normals.Count
                    ? new Vector3d(mesh.Normals[i])
                    : Vector3d.ZAxis;
                if (!normal.Unitize())
                    normal = Vector3d.ZAxis;

                double diffuse = Math.Max(0.0, normal * towardLight);
                double brightness = Clamp01(ambient + shadeStrength * diffuse);
                mesh.VertexColors.Add(ScaleColor(baseColor, brightness));
            }
        }

        private static Color ScaleColor(Color color, double factor) =>
            Color.FromArgb(
                color.A,
                ClampByte(color.R * factor),
                ClampByte(color.G * factor),
                ClampByte(color.B * factor));

        private static int ClampByte(double value) =>
            (int)Math.Round(Math.Max(0.0, Math.Min(255.0, value)));

        private static double Clamp01(double value) =>
            Math.Max(0.0, Math.Min(1.0, value));
    }
}
