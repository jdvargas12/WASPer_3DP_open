using System;
using System.Drawing;

namespace WASPer_3DP
{
    /// <summary>
    /// Creates the field-driven variant of the point-grid icons. The geometry
    /// remains identical to the mesh variants, while green grid pixels are
    /// shifted into the blue palette used by the field components.
    /// </summary>
    internal static class FieldGridIcon
    {
        internal static Bitmap Create(Bitmap source)
        {
            var result = new Bitmap(source);

            for (int y = 0; y < result.Height; y++)
            {
                for (int x = 0; x < result.Width; x++)
                {
                    Color pixel = result.GetPixel(x, y);
                    if (pixel.A == 0 || pixel.G <= pixel.R + 12 || pixel.G <= pixel.B + 12)
                        continue;

                    int value = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                    result.SetPixel(x, y, Color.FromArgb(
                        pixel.A,
                        (int)(value * 0.28),
                        (int)(value * 0.72),
                        value));
                }
            }

            return result;
        }
    }
}
