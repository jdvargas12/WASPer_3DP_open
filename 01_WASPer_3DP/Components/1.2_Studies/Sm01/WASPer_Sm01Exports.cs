using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using ClosedXML.Excel;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;
using Rhino.Geometry;


namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        private static void WriteStudyCsv(string outputPath, WasperStudy study)
        {
            List<string> parameterKeys = StudyParameterKeys(study);
            List<WasperKpi> kpiDefinitions = StudyKpiDefinitions(study);
            var lines = new List<string>
            {
                string.Join(",", Enumerable.Repeat("Study", 6)
                    .Concat(parameterKeys.Select(key => "Parameter"))
                    .Concat(kpiDefinitions.Select(kpi => kpi.DisplayGroup))
                    .Select(EscapeCsv)),
                string.Join(",", new[] { "Iteration", "Sample name", "Captured UTC", "Status", "G-code files", "Snapshot files" }
                    .Concat(parameterKeys)
                    .Concat(kpiDefinitions.Select(kpi => string.IsNullOrWhiteSpace(kpi.Label)
                        ? kpi.Key
                        : kpi.Label))
                    .Select(EscapeCsv)),
                string.Join(",", Enumerable.Repeat(string.Empty, 6 + parameterKeys.Count)
                    .Concat(kpiDefinitions.Select(kpi => kpi.Unit ?? string.Empty))
                    .Select(EscapeCsv))
            };

            foreach (WasperStudyIteration iteration in study.Iterations)
            {
                Dictionary<string, WasperKpi> kpis = iteration.Kpis
                    .GroupBy(kpi => kpi.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                IEnumerable<string> row = new[]
                {
                    iteration.Index.ToString(CultureInfo.InvariantCulture),
                    iteration.SampleName ?? string.Empty,
                    iteration.CapturedUtc.ToString("o", CultureInfo.InvariantCulture),
                    iteration.Status ?? string.Empty,
                    string.Join(";", iteration.GcodeFiles ?? new List<string>()),
                    string.Join(";", iteration.SnapshotFiles ?? new List<string>())
                }
                .Concat(parameterKeys.Select(key => iteration.Parameters.TryGetValue(key, out double value)
                    ? value.ToString("G17", CultureInfo.InvariantCulture)
                    : string.Empty))
                .Concat(kpiDefinitions.Select(definition => kpis.TryGetValue(definition.Key, out WasperKpi kpi)
                    ? KpiExportValue(kpi)
                    : string.Empty));
                lines.Add(string.Join(",", row.Select(EscapeCsv)));
            }
            File.WriteAllLines(outputPath, lines, new UTF8Encoding(true));
        }

        private static void WriteStudyExcel(string outputPath, WasperStudy study)
        {
            List<string> parameterKeys = StudyParameterKeys(study);
            List<WasperKpi> kpiDefinitions = StudyKpiDefinitions(study);
            List<string> types = Enumerable.Repeat("Study", 6)
                .Concat(parameterKeys.Select(key => "Parameter"))
                .Concat(kpiDefinitions.Select(kpi => kpi.DisplayGroup))
                .ToList();
            List<string> names = new[] { "Iteration", "Sample name", "Captured UTC", "Status", "G-code files", "Snapshot files" }
                .Concat(parameterKeys)
                .Concat(kpiDefinitions.Select(kpi => string.IsNullOrWhiteSpace(kpi.Label)
                    ? kpi.Key
                    : kpi.Label))
                .ToList();
            List<string> units = Enumerable.Repeat(string.Empty, 6 + parameterKeys.Count)
                .Concat(kpiDefinitions.Select(kpi => kpi.Unit ?? string.Empty))
                .ToList();

            using var workbook = new XLWorkbook();
            IXLWorksheet sheet = workbook.Worksheets.Add("Iterations");
            for (int column = 0; column < names.Count; column++)
            {
                sheet.Cell(1, column + 1).Value = types[column];
                sheet.Cell(2, column + 1).Value = names[column];
                sheet.Cell(3, column + 1).Value = units[column];
            }

            for (int row = 0; row < study.Iterations.Count; row++)
            {
                WasperStudyIteration iteration = study.Iterations[row];
                int excelRow = row + 4;
                sheet.Cell(excelRow, 1).Value = iteration.Index;
                sheet.Cell(excelRow, 2).Value = iteration.SampleName ?? string.Empty;
                sheet.Cell(excelRow, 3).Value = iteration.CapturedUtc;
                sheet.Cell(excelRow, 4).Value = iteration.Status ?? string.Empty;
                sheet.Cell(excelRow, 5).Value = string.Join(";", iteration.GcodeFiles ?? new List<string>());
                sheet.Cell(excelRow, 6).Value = string.Join(";", iteration.SnapshotFiles ?? new List<string>());
                for (int index = 0; index < parameterKeys.Count; index++)
                {
                    if (iteration.Parameters.TryGetValue(parameterKeys[index], out double value))
                        sheet.Cell(excelRow, 7 + index).Value = value;
                }
                Dictionary<string, WasperKpi> kpis = iteration.Kpis
                    .GroupBy(kpi => kpi.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < kpiDefinitions.Count; index++)
                {
                    if (!kpis.TryGetValue(kpiDefinitions[index].Key, out WasperKpi kpi))
                        continue;
                    int column = 7 + parameterKeys.Count + index;
                    if (kpi.Value.HasValue)
                        sheet.Cell(excelRow, column).Value = kpi.Value.Value;
                    else
                        sheet.Cell(excelRow, column).Value = kpi.TextValue ?? string.Empty;
                }
            }

            IXLRange typeHeader = sheet.Range(1, 1, 1, Math.Max(6, names.Count));
            typeHeader.Style.Font.Bold = true;
            typeHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#D97B29");
            typeHeader.Style.Font.FontColor = XLColor.White;
            IXLRange nameHeader = sheet.Range(2, 1, 2, Math.Max(6, names.Count));
            nameHeader.Style.Font.Bold = true;
            nameHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#F4B183");
            IXLRange unitHeader = sheet.Range(3, 1, 3, Math.Max(6, names.Count));
            unitHeader.Style.Font.Italic = true;
            unitHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#FCE4D6");
            sheet.Range(1, 1, 3, Math.Max(6, names.Count)).Style.Alignment.WrapText = true;
            sheet.SheetView.FreezeRows(3);
            sheet.Columns().AdjustToContents(1, 40);
            workbook.SaveAs(outputPath);
        }

        private static void WriteStudyCsvLong(string outputPath, WasperStudy study)
        {
            var lines = new List<string>
            {
                "Iteration,Sample name,Captured UTC,Status,Type,Name,Unit,Value"
            };
            foreach (WasperStudyIteration iteration in study.Iterations)
            {
                string[] prefix =
                {
                    iteration.Index.ToString(CultureInfo.InvariantCulture),
                    iteration.SampleName ?? string.Empty,
                    iteration.CapturedUtc.ToString("o", CultureInfo.InvariantCulture),
                    iteration.Status ?? string.Empty
                };
                foreach (KeyValuePair<string, double> parameter in iteration.Parameters)
                {
                    lines.Add(string.Join(",", prefix.Concat(new[]
                    {
                        "Parameter",
                        parameter.Key,
                        string.Empty,
                        parameter.Value.ToString("G17", CultureInfo.InvariantCulture)
                    }).Select(EscapeCsv)));
                }
                foreach (WasperKpi kpi in iteration.Kpis ?? new List<WasperKpi>())
                {
                    lines.Add(string.Join(",", prefix.Concat(new[]
                    {
                        kpi.DisplayGroup,
                        string.IsNullOrWhiteSpace(kpi.Label) ? kpi.Key : kpi.Label,
                        kpi.Unit ?? string.Empty,
                        KpiExportValue(kpi)
                    }).Select(EscapeCsv)));
                }
            }
            File.WriteAllLines(outputPath, lines, new UTF8Encoding(true));
        }

        private static void WriteStudyExcelLong(string outputPath, WasperStudy study)
        {
            using var workbook = new XLWorkbook();
            IXLWorksheet sheet = workbook.Worksheets.Add("KPIs by iteration");
            string[] headers =
            {
                "Iteration", "Sample name", "Captured UTC", "Status",
                "Type", "Name", "Unit", "Value"
            };
            for (int column = 0; column < headers.Length; column++)
                sheet.Cell(1, column + 1).Value = headers[column];

            int row = 2;
            foreach (WasperStudyIteration iteration in study.Iterations)
            {
                foreach (KeyValuePair<string, double> parameter in iteration.Parameters)
                {
                    WriteLongExcelRow(
                        sheet,
                        row++,
                        iteration,
                        "Parameter",
                        parameter.Key,
                        string.Empty,
                        parameter.Value);
                }
                foreach (WasperKpi kpi in iteration.Kpis ?? new List<WasperKpi>())
                {
                    object value = kpi.Value.HasValue
                        ? kpi.Value.Value
                        : kpi.TextValue ?? string.Empty;
                    WriteLongExcelRow(
                        sheet,
                        row++,
                        iteration,
                        kpi.DisplayGroup,
                        string.IsNullOrWhiteSpace(kpi.Label) ? kpi.Key : kpi.Label,
                        kpi.Unit,
                        value);
                }
            }

            IXLRange range = sheet.Range(1, 1, Math.Max(2, row - 1), headers.Length);
            range.CreateTable("WASPerStudyKPIs");
            sheet.SheetView.FreezeRows(1);
            sheet.Columns().AdjustToContents(1, 45);
            workbook.SaveAs(outputPath);
        }

        private static void WriteLongExcelRow(
            IXLWorksheet sheet,
            int row,
            WasperStudyIteration iteration,
            string type,
            string name,
            string unit,
            object value)
        {
            sheet.Cell(row, 1).Value = iteration.Index;
            sheet.Cell(row, 2).Value = iteration.SampleName ?? string.Empty;
            sheet.Cell(row, 3).Value = iteration.CapturedUtc;
            sheet.Cell(row, 4).Value = iteration.Status ?? string.Empty;
            sheet.Cell(row, 5).Value = type ?? string.Empty;
            sheet.Cell(row, 6).Value = name ?? string.Empty;
            sheet.Cell(row, 7).Value = unit ?? string.Empty;
            if (value is double number)
                sheet.Cell(row, 8).Value = number;
            else
                sheet.Cell(row, 8).Value = Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static void WriteKpiSnapshotCsvWide(
            string outputPath,
            IList<WasperKpi> items)
        {
            var lines = new List<string>
            {
                string.Join(",", items.Select(item => EscapeCsv(item.DisplayGroup))),
                string.Join(",", items.Select(item => EscapeCsv(
                    string.IsNullOrWhiteSpace(item.Label) ? item.Key : item.Label))),
                string.Join(",", items.Select(item => EscapeCsv(item.Unit ?? string.Empty))),
                string.Join(",", items.Select(item => EscapeCsv(KpiExportValue(item))))
            };
            File.WriteAllLines(outputPath, lines, new UTF8Encoding(true));
        }

        private static void WriteKpiSnapshotExcelWide(
            string outputPath,
            IList<WasperKpi> items)
        {
            using var workbook = new XLWorkbook();
            IXLWorksheet sheet = workbook.Worksheets.Add("KPIs");
            for (int column = 0; column < items.Count; column++)
            {
                WasperKpi item = items[column];
                int excelColumn = column + 1;
                sheet.Cell(1, excelColumn).Value = item.DisplayGroup;
                sheet.Cell(2, excelColumn).Value = string.IsNullOrWhiteSpace(item.Label)
                    ? item.Key
                    : item.Label;
                sheet.Cell(3, excelColumn).Value = item.Unit ?? string.Empty;
                if (item.Value.HasValue)
                    sheet.Cell(4, excelColumn).Value = item.Value.Value;
                else
                    sheet.Cell(4, excelColumn).Value = item.TextValue ?? string.Empty;
            }
            int lastColumn = Math.Max(1, items.Count);
            sheet.Range(1, 1, 1, lastColumn).Style.Fill.BackgroundColor =
                XLColor.FromHtml("#D97B29");
            sheet.Range(1, 1, 1, lastColumn).Style.Font.FontColor = XLColor.White;
            sheet.Range(1, 1, 2, lastColumn).Style.Font.Bold = true;
            sheet.Range(3, 1, 3, lastColumn).Style.Font.Italic = true;
            sheet.Range(1, 1, 3, lastColumn).Style.Alignment.WrapText = true;
            sheet.SheetView.FreezeRows(3);
            sheet.Columns().AdjustToContents(1, 40);
            workbook.SaveAs(outputPath);
        }

        private static void WriteStudyJson(string outputPath, WasperStudy study)
        {
            File.WriteAllText(
                outputPath,
                JsonConvert.SerializeObject(study, Formatting.Indented),
                new UTF8Encoding(true));
        }

        private static List<string> StudyParameterKeys(WasperStudy study)
        {
            return study.Iterations
                .SelectMany(iteration => iteration.Parameters.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key)
                .ToList();
        }

        private static List<WasperKpi> StudyKpiDefinitions(WasperStudy study)
        {
            return study.Iterations
                .SelectMany(iteration => iteration.Kpis ?? new List<WasperKpi>())
                .Where(kpi => kpi != null && !string.IsNullOrWhiteSpace(kpi.Key))
                .GroupBy(kpi => kpi.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static string KpiExportValue(WasperKpi kpi)
        {
            return kpi.Value.HasValue
                ? kpi.Value.Value.ToString("G17", CultureInfo.InvariantCulture)
                : kpi.TextValue ?? string.Empty;
        }

    }
}
