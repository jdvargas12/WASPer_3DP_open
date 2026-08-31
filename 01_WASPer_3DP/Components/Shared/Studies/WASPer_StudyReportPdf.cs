using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;

using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

using RhinoFont = Rhino.DocObjects.Font;

namespace WASPer_3DP
{
    public static class WasperStudyReportPdf
    {
        private static readonly Color Ink = Color.FromArgb(45, 48, 52);
        private static readonly Color Muted = Color.FromArgb(105, 110, 118);
        private static readonly Color Light = Color.FromArgb(228, 231, 235);
        private static readonly Color Accent = Color.FromArgb(221, 92, 32);
        private static readonly Color AccentSoft = Color.FromArgb(247, 230, 220);

        public static string Write(
            WasperStudy study,
            WasperKpiSet currentKpis,
            WasperReportSettings settings,
            string outputPath,
            Bitmap snapshot = null)
        {
            if (study == null)
                throw new ArgumentNullException(nameof(study));
            if (settings == null)
                settings = new WasperReportSettings();
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("A PDF output path is required.", nameof(outputPath));

            string folder = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            PageDimensions(settings, out int pageWidth, out int pageHeight);
            FilePdf pdf = FilePdf.Create();
            var context = new ReportContext(pdf, pageWidth, pageHeight);
            Bitmap ownedSnapshot = null;
            try
            {
                if (snapshot == null && settings.IncludeSnapshot)
                {
                    ownedSnapshot = CaptureActiveViewport();
                    snapshot = ownedSnapshot;
                }

                DrawOverview(context, study, currentKpis, settings, snapshot);
                DrawKpiSummary(context, study, currentKpis);
                if (settings.IncludeIterationTable && (study.Iterations?.Count ?? 0) > 0)
                    DrawIterationPreview(context, study);
                context.AddPageNumbers();
                pdf.Write(outputPath);
                return outputPath;
            }
            finally
            {
                ownedSnapshot?.Dispose();
            }
        }

        private static void DrawOverview(
            ReportContext context,
            WasperStudy study,
            WasperKpiSet currentKpis,
            WasperReportSettings settings,
            Bitmap snapshot)
        {
            int page = context.NewPage();
            float margin = 42f;
            float width = context.Width - (margin * 2f);

            context.Text(page, settings.Title, margin, 58, 24, true, Ink);
            string subtitle = string.IsNullOrWhiteSpace(settings.Subtitle)
                ? study.RunName
                : settings.Subtitle;
            context.Text(page, subtitle, margin, 88, 11, false, Muted);
            context.Line(page, margin, 108, context.Width - margin, 108, Accent, 2.5f);

            float cardY = 126;
            float gap = 10;
            float cardWidth = (width - (gap * 2)) / 3f;
            int iterationCount = study.Iterations?.Count ?? 0;
            int parameterCount = study.Parameters?.Count ?? 0;
            context.Card(page, margin, cardY, cardWidth, 62, "Iterations", iterationCount.ToString(CultureInfo.InvariantCulture));
            context.Card(page, margin + cardWidth + gap, cardY, cardWidth, 62, "Parameters", parameterCount.ToString(CultureInfo.InvariantCulture));
            int kpiCount = study.Iterations?.LastOrDefault()?.Kpis?.Count ??
                currentKpis?.EnabledItems.Count() ?? 0;
            context.Card(page, margin + ((cardWidth + gap) * 2), cardY, cardWidth, 62, "Selected KPIs", kpiCount.ToString(CultureInfo.InvariantCulture));

            float y = cardY + 82;
            if (snapshot != null && settings.IncludeSnapshot)
            {
                float imageHeight = Math.Min(340, context.Height - y - 155);
                float imageWidth = width;
                float sourceAspect = snapshot.Width / (float)Math.Max(1, snapshot.Height);
                float targetAspect = imageWidth / imageHeight;
                if (sourceAspect > targetAspect)
                    imageHeight = imageWidth / sourceAspect;
                else
                    imageWidth = imageHeight * sourceAspect;
                float imageX = margin + ((width - imageWidth) * 0.5f);
                context.Pdf.DrawBitmap(page, snapshot, imageX, y, imageWidth, imageHeight, 0);
                context.Box(page, imageX, y, imageWidth, imageHeight, Light, 0.8f);
                y += imageHeight + 22;
            }

            context.Text(page, "Study information", margin, y, 14, true, Ink);
            y += 24;
            context.KeyValue(page, margin, y, "Study ID", study.StudyId.ToString());
            y += 18;
            context.KeyValue(page, margin, y, "Created UTC", study.CreatedUtc.ToString("u"));
            y += 18;
            context.KeyValue(page, margin, y, "Updated UTC", study.UpdatedUtc.ToString("u"));
            y += 18;
            context.KeyValue(page, margin, y, "Study schema", study.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        }

        private static void DrawKpiSummary(
            ReportContext context,
            WasperStudy study,
            WasperKpiSet currentKpis)
        {
            List<WasperKpi> representative = study.Iterations?
                .LastOrDefault()?.Kpis?
                .Where(kpi => kpi != null && kpi.Enabled)
                .ToList() ?? new List<WasperKpi>();
            if (representative.Count == 0)
                representative = currentKpis?.EnabledItems.ToList() ?? new List<WasperKpi>();
            if (representative.Count == 0)
                return;

            Dictionary<string, List<double>> numericSeries = (study.Iterations ?? new List<WasperStudyIteration>())
                .Where(iteration => iteration != null)
                .SelectMany(iteration => iteration.Kpis ?? new List<WasperKpi>())
                .Where(kpi => kpi?.Value.HasValue == true)
                .GroupBy(kpi => kpi.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(kpi => kpi.Value.Value).Where(IsFinite).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            int page = -1;
            float y = 0;
            foreach (IGrouping<string, WasperKpi> group in representative
                .GroupBy(kpi => kpi.DisplayGroup))
            {
                float required = 38 + (group.Count() * 26);
                if (page < 0 || y + required > context.Height - 54)
                {
                    page = context.NewPage();
                    y = context.PageHeader(page, "KPI summary", "Latest values and study ranges");
                }

                context.Text(page, group.Key, 42, y, 14, true, Accent);
                y += 22;
                context.TableHeader(page, 42, y, context.Width - 84, new[] { 0.42f, 0.18f, 0.13f, 0.13f, 0.14f },
                    new[] { "KPI", "Latest", "Minimum", "Mean", "Maximum" });
                y += 22;

                foreach (WasperKpi kpi in group)
                {
                    string latest = KpiValue(kpi);
                    string minimum = string.Empty;
                    string mean = string.Empty;
                    string maximum = string.Empty;
                    if (numericSeries.TryGetValue(kpi.Key, out List<double> values) && values.Count > 0)
                    {
                        minimum = Format(values.Min());
                        mean = Format(values.Average());
                        maximum = Format(values.Max());
                    }
                    context.TableRow(page, 42, y, context.Width - 84,
                        new[] { 0.42f, 0.18f, 0.13f, 0.13f, 0.14f },
                        new[] { kpi.Label, latest, minimum, mean, maximum });
                    y += 26;
                }
                y += 18;
            }
        }

        private static void DrawIterationPreview(ReportContext context, WasperStudy study)
        {
            List<WasperStudyIteration> iterations = (study.Iterations ?? new List<WasperStudyIteration>())
                .Where(iteration => iteration != null)
                .ToList();
            List<string> parameters = iterations
                .SelectMany(iteration => iteration.Parameters?.Keys ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
            List<WasperKpi> kpiDefinitions = iterations
                .SelectMany(iteration => iteration.Kpis ?? new List<WasperKpi>())
                .GroupBy(kpi => kpi.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Where(kpi => kpi.Value.HasValue)
                .Take(Math.Max(1, 5 - parameters.Count))
                .ToList();

            int page = context.NewPage();
            float y = context.PageHeader(
                page,
                "Iteration preview",
                "The complete rectangular dataset is available in CSV, XLSX, and JSON exports.");
            var headers = new List<string> { "ID", "Sample name" };
            headers.AddRange(parameters);
            headers.AddRange(kpiDefinitions.Select(kpi => kpi.Label));
            float[] widths = Enumerable.Repeat(1f / headers.Count, headers.Count).ToArray();
            context.TableHeader(page, 42, y, context.Width - 84, widths, headers);
            y += 24;

            foreach (WasperStudyIteration iteration in iterations.Take(24))
            {
                if (y > context.Height - 70)
                {
                    page = context.NewPage();
                    y = context.PageHeader(page, "Iteration preview - continued", study.RunName);
                    context.TableHeader(page, 42, y, context.Width - 84, widths, headers);
                    y += 24;
                }
                Dictionary<string, WasperKpi> kpis = (iteration.Kpis ?? new List<WasperKpi>())
                    .Where(kpi => kpi != null)
                    .GroupBy(kpi => kpi.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                var cells = new List<string>
                {
                    iteration.Index.ToString(CultureInfo.InvariantCulture),
                    iteration.SampleName ?? string.Empty
                };
                cells.AddRange(parameters.Select(key => iteration.Parameters != null &&
                    iteration.Parameters.TryGetValue(key, out double value)
                    ? Format(value)
                    : string.Empty));
                cells.AddRange(kpiDefinitions.Select(definition => kpis.TryGetValue(definition.Key, out WasperKpi value)
                    ? KpiValue(value)
                    : string.Empty));
                context.TableRow(page, 42, y, context.Width - 84, widths, cells);
                y += 25;
            }
        }

        private static Bitmap CaptureActiveViewport()
        {
            try
            {
                return RhinoDoc.ActiveDoc?.Views?.ActiveView?.CaptureToBitmap(new Size(1200, 800));
            }
            catch
            {
                return null;
            }
        }

        private static void PageDimensions(WasperReportSettings settings, out int width, out int height)
        {
            switch ((settings.PageSize ?? "A4").Trim().ToUpperInvariant())
            {
                case "A3": width = 842; height = 1191; break;
                case "LETTER": width = 612; height = 792; break;
                case "LEGAL": width = 612; height = 1008; break;
                default: width = 595; height = 842; break;
            }
            if (settings.Landscape)
            {
                int temporary = width;
                width = height;
                height = temporary;
            }
        }

        private static string KpiValue(WasperKpi kpi)
        {
            string value = kpi.Value.HasValue ? Format(kpi.Value.Value) : kpi.TextValue ?? string.Empty;
            return string.IsNullOrWhiteSpace(kpi.Unit) ? value : value + " " + kpi.Unit;
        }

        private static string Format(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private sealed class ReportContext
        {
            private readonly RhinoFont _regular = new RhinoFont("Arial");
            private readonly RhinoFont _bold = RhinoFont.FromQuartetProperties("Arial", true, false);
            private readonly List<int> _pages = new List<int>();

            public ReportContext(FilePdf pdf, int width, int height)
            {
                Pdf = pdf;
                Width = width;
                Height = height;
            }

            public FilePdf Pdf { get; }
            public int Width { get; }
            public int Height { get; }

            public int NewPage()
            {
                int page = Pdf.AddPage(Width, Height, 72);
                _pages.Add(page);
                return page;
            }

            public float PageHeader(int page, string title, string subtitle)
            {
                Text(page, title, 42, 50, 20, true, Ink);
                Text(page, subtitle, 42, 76, 9, false, Muted);
                Line(page, 42, 94, Width - 42, 94, Accent, 2);
                return 116;
            }

            public void AddPageNumbers()
            {
                for (int index = 0; index < _pages.Count; index++)
                {
                    int page = _pages[index];
                    Line(page, 42, Height - 34, Width - 42, Height - 34, Light, 0.6f);
                    Text(page, "WASPer Study Manager", 42, Height - 18, 8, false, Muted);
                    Text(page, $"Page {index + 1} of {_pages.Count}", Width - 42, Height - 18, 8, false, Muted,
                        TextHorizontalAlignment.Right);
                }
            }

            public void Card(int page, float x, float y, float width, float height, string label, string value)
            {
                Box(page, x, y, width, height, Light, 0.8f);
                Text(page, label, x + 12, y + 20, 9, false, Muted);
                Text(page, value, x + 12, y + 47, 19, true, Accent);
            }

            public void KeyValue(int page, float x, float y, string key, string value)
            {
                Text(page, key, x, y, 9, true, Muted);
                Text(page, value, x + 115, y, 9, false, Ink);
            }

            public void TableHeader(
                int page,
                float x,
                float y,
                float width,
                IReadOnlyList<float> fractions,
                IReadOnlyList<string> cells)
            {
                float cursor = x;
                for (int index = 0; index < cells.Count; index++)
                {
                    float cellWidth = width * fractions[index];
                    Pdf.DrawPolyline(page, new[]
                    {
                        new PointF(cursor, y), new PointF(cursor + cellWidth, y),
                        new PointF(cursor + cellWidth, y + 22), new PointF(cursor, y + 22),
                        new PointF(cursor, y)
                    }, AccentSoft, AccentSoft, 0.2f);
                    Text(page, Truncate(cells[index], cellWidth, 8), cursor + 5, y + 15, 8, true, Ink);
                    cursor += cellWidth;
                }
            }

            public void TableRow(
                int page,
                float x,
                float y,
                float width,
                IReadOnlyList<float> fractions,
                IReadOnlyList<string> cells)
            {
                float cursor = x;
                Line(page, x, y + 25, x + width, y + 25, Light, 0.45f);
                for (int index = 0; index < cells.Count; index++)
                {
                    float cellWidth = width * fractions[index];
                    Text(page, Truncate(cells[index], cellWidth, 7.5f), cursor + 5, y + 16, 7.5f, false, Ink);
                    cursor += cellWidth;
                }
            }

            public void Box(int page, float x, float y, float width, float height, Color color, float thickness)
            {
                Pdf.DrawPolyline(page, new[]
                {
                    new PointF(x, y), new PointF(x + width, y),
                    new PointF(x + width, y + height), new PointF(x, y + height),
                    new PointF(x, y)
                }, Color.Transparent, color, thickness);
            }

            public void Line(int page, float x1, float y1, float x2, float y2, Color color, float width)
            {
                Pdf.DrawLine(page, new PointF(x1, y1), new PointF(x2, y2), color, width);
            }

            public void Text(
                int page,
                string text,
                double x,
                double y,
                float size,
                bool bold,
                Color color,
                TextHorizontalAlignment alignment = TextHorizontalAlignment.Left)
            {
                Pdf.DrawText(
                    page,
                    text ?? string.Empty,
                    x,
                    y,
                    size,
                    bold ? _bold : _regular,
                    color,
                    Color.Transparent,
                    0,
                    0,
                    alignment,
                    TextVerticalAlignment.Middle);
            }

            private static string Truncate(string text, float width, float fontSize)
            {
                text ??= string.Empty;
                int maximum = Math.Max(4, (int)(width / Math.Max(1f, fontSize * 0.52f)));
                return text.Length <= maximum ? text : text.Substring(0, maximum - 3) + "...";
            }
        }
    }
}
