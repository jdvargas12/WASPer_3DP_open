using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

namespace WASPer_3DP.Painting
{
    internal static class WasperPaintUtilities
    {
        internal static bool TryGetDomain(
            IGH_DataAccess dataAccess,
            int index,
            out Interval domain,
            out string error)
        {
            domain = new Interval(-5.0, 5.0);
            error = null;
            object raw = null;
            if (!dataAccess.GetData(index, ref raw) || raw == null)
                return true;
            object value = raw is IGH_Goo goo ? goo.ScriptVariable() : raw;
            if (value is Interval interval)
                domain = interval;
            else if (raw is GH_Interval ghInterval)
                domain = ghInterval.Value;
            else if (value is string text)
            {
                if (!TryParseDomainText(text, out domain))
                {
                    error =
                        "mag_domain text must be one number or two finite numbers written as " +
                        "'minimum to maximum', for example '-5 to 5'.";
                    return false;
                }
            }
            else if (value is IConvertible convertible)
            {
                try
                {
                    domain = new Interval(0.0, convertible.ToDouble(null));
                }
                catch
                {
                    error =
                        "mag_domain must be one Domain/Interval, one number, or text such as '-5 to 5'.";
                    return false;
                }
            }
            else
            {
                error =
                    "mag_domain must be one Domain/Interval, one number, or text such as '-5 to 5'.";
                return false;
            }
            if (!double.IsFinite(domain.T0) || !double.IsFinite(domain.T1))
            {
                error = "mag_domain endpoints must be finite.";
                return false;
            }
            return true;
        }

        internal static string Compress(string value)
        {
            byte[] input = Encoding.UTF8.GetBytes(value ?? string.Empty);
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
                gzip.Write(input, 0, input.Length);
            return Convert.ToBase64String(output.ToArray());
        }

        internal static string Decompress(string value)
        {
            using var input = new MemoryStream(Convert.FromBase64String(value ?? string.Empty));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        internal static double Lerp(double a, double b, double t) => a + (b - a) * t;

        internal static bool ValuesEqual(IList<double> first, IList<double> second)
        {
            if (ReferenceEquals(first, second))
                return true;
            if (first == null || second == null || first.Count != second.Count)
                return false;
            for (int i = 0; i < first.Count; i++)
            {
                if (Math.Abs(first[i] - second[i]) > 1e-12)
                    return false;
            }
            return true;
        }

        private static bool TryParseDomainText(string text, out Interval domain)
        {
            domain = Interval.Unset;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            string trimmed = text.Trim();
            int separator = trimmed.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            if (separator >= 0)
            {
                string first = trimmed.Substring(0, separator).Trim();
                string second = trimmed.Substring(separator + 4).Trim();
                if (!TryParseFiniteDouble(first, out double start) ||
                    !TryParseFiniteDouble(second, out double end))
                    return false;
                domain = new Interval(start, end);
                return true;
            }
            if (!TryParseFiniteDouble(trimmed, out double single))
                return false;
            domain = new Interval(0.0, single);
            return true;
        }

        private static bool TryParseFiniteDouble(string text, out double value)
        {
            const NumberStyles styles = NumberStyles.Float | NumberStyles.AllowThousands;
            bool parsed =
                double.TryParse(text, styles, CultureInfo.CurrentCulture, out value) ||
                double.TryParse(text, styles, CultureInfo.InvariantCulture, out value);
            return parsed && double.IsFinite(value);
        }
    }
}
