using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Grasshopper.Kernel;

using Newtonsoft.Json;

namespace WASPer_3DP.Components._3_0_Slicing_BJ
{
    public sealed class wsp_Sl11_Binder_Jet_Export : GH_Component
    {
        private readonly string _versionTag;
        private static Bitmap _icon;

        public wsp_Sl11_Binder_Jet_Export()
            : base(
                "wsp_Sl11_Binder Jet Export",
                "Binder Jet Export",
                "Streams a raster stack to disk with bounded memory. Format 0 writes one packed 1-bit WASPer job; " +
                "1 writes 1-bit PNG layers; 2 writes 8-bit grayscale PNG layers.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.0_Slicing BJ")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null ? "v1.0.x" : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("62FFBE65-EED0-4D5C-970A-9E8848C46818");
        public override GH_Exposure Exposure => GH_Exposure.primary;
        protected override Bitmap Icon => _icon ??= CreateIcon();

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("raster_stack", "stack", "Raster job from Sl09.", GH_ParamAccess.item);
            p.AddIntegerParameter("format", "format", "0 = packed 1-bit .wspbj; 1 = 1-bit PNG stack; 2 = 8-bit grayscale PNG stack.", GH_ParamAccess.item, 0);
            p.AddTextParameter("folder", "folder", "Optional job folder. Empty uses WASPer_<definition>\\BinderJet\\<prefix> beside the GH file.", GH_ParamAccess.item);
            p[2].Optional = true;
            p.AddTextParameter("prefix", "prefix", "Job and layer filename prefix.", GH_ParamAccess.item, "layer");
            p.AddBooleanParameter("write", "write", "Generate the complete job. Use a Grasshopper Button.", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("paths", "paths", "Written job/layer paths.", GH_ParamAccess.list);
            p.AddTextParameter("manifest", "manifest", "Written JSON manifest path.", GH_ParamAccess.item);
            p.AddTextParameter("info", "info", "Export status, dimensions, and invalid samples.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object rawStack = null;
            if (!da.GetData(0, ref rawStack)) return;
            WasperRasterStack stack = WasperRasterData.ExtractStack(rawStack);
            if (stack == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "stack is not a valid WASPer Raster Stack.");
                return;
            }

            int format = 0;
            string folder = string.Empty;
            string prefix = "layer";
            bool write = false;
            da.GetData(1, ref format);
            da.GetData(2, ref folder);
            da.GetData(3, ref prefix);
            da.GetData(4, ref write);
            format = Math.Max(0, Math.Min(format, 2));
            prefix = SanitizePrefix(prefix);
            string filePrefix = da.Iteration <= 0 ? prefix : $"{prefix}_field{da.Iteration:D3}";

            if (!write)
            {
                string idle = $"ready | {FormatName(format)} | {stack.Summary} | write=false";
                da.SetData(2, idle);
                Message = $"{_versionTag} | ready";
                return;
            }

            folder = ResolveOutputFolder(folder, prefix);
            try { Directory.CreateDirectory(folder); }
            catch (Exception exception)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Cannot create output folder: {exception.Message}");
                return;
            }

            var paths = new List<string>();
            long invalidTotal = 0;
            try
            {
                if (format == 0)
                {
                    if (stack.Settings.Mode == WasperFieldBitmapMode.Linear)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Packed 1-bit export thresholds the grayscale result at 50%. Use format 2 to preserve binder levels.");
                    string path = Path.Combine(folder, filePrefix + ".wspbj");
                    invalidTotal = WritePackedJob(path, stack);
                    paths.Add(path);
                }
                else
                {
                    int count = stack.Layout.LayerCount;
                    var layerPaths = new string[count];
                    var invalid = new int[count];
                    var errors = new string[count];
                    int digits = Math.Max(6, (count - 1).ToString().Length);
                    Action<int> writeLayer = index =>
                    {
                        string path = Path.Combine(folder, $"{filePrefix}_{index.ToString("D" + digits)}.png");
                        try
                        {
                            using Bitmap bitmap = format == 1
                                ? WasperFieldBitmapRasterizer.RasterizeBinaryLayer(
                                    stack.Field, stack.Layout, index, stack.Settings, out invalid[index], false)
                                : WasperFieldBitmapRasterizer.RasterizeLayer(
                                    stack.Field, stack.Layout, index, stack.Settings.Mode, stack.Settings.Threshold,
                                    stack.Settings.FieldRange, stack.Settings.Invert, out invalid[index], false);
                            bitmap.Save(path, ImageFormat.Png);
                            layerPaths[index] = path;
                        }
                        catch (Exception exception) { errors[index] = exception.Message; }
                    };

                    long bytesPerWorker = Math.Max(1L, (long)stack.Layout.Width * stack.Layout.Height * 2L);
                    int memoryBoundWorkers = (int)Math.Max(1L, (256L * 1024 * 1024) / bytesPerWorker);
                    int workers = Math.Min(memoryBoundWorkers, Math.Min(4, Math.Max(1, Environment.ProcessorCount)));
                    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers }, writeLayer);
                    for (int i = 0; i < errors.Length; i++)
                        if (!string.IsNullOrWhiteSpace(errors[i]))
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Layer {i}: {errors[i]}");
                    paths.AddRange(layerPaths.Where(path => !string.IsNullOrWhiteSpace(path)));
                    invalidTotal = invalid.Sum(value => (long)value);
                }

                string manifestPath = Path.Combine(folder, filePrefix + "_manifest.json");
                File.WriteAllText(manifestPath, BuildManifest(stack, format, filePrefix, paths));
                if (invalidTotal > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{invalidTotal:N0} invalid field samples were written as no binder.");

                da.SetDataList(0, paths);
                da.SetData(1, manifestPath);
                da.SetData(2,
                    $"written={paths.Count} | {FormatName(format)} | layers={stack.Layout.LayerCount} | " +
                    $"{stack.Layout.Width}x{stack.Layout.Height} px | invalid_samples={invalidTotal:N0} | folder={folder}");
                Message = $"{_versionTag} | {stack.Layout.LayerCount} layers written";
            }
            catch (Exception exception)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Binder-jet export failed: {exception.Message}");
                da.SetData(2, "error: " + exception.Message);
            }
        }

        private static long WritePackedJob(string path, WasperRasterStack stack)
        {
            long invalidTotal = 0;
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, false);
            writer.Write(Encoding.ASCII.GetBytes("WSPBJ001"));
            writer.Write(stack.Layout.Width);
            writer.Write(stack.Layout.Height);
            writer.Write(stack.Layout.LayerCount);
            writer.Write(stack.Layout.SizeX);
            writer.Write(stack.Layout.SizeY);
            writer.Write(stack.Layout.LayerHeight);
            writer.Write(stack.Layout.PixelSizeX);
            writer.Write(stack.Layout.PixelSizeY);
            writer.Write(stack.Layout.XMin);
            writer.Write(stack.Layout.YMin);
            writer.Write(stack.Layout.ZMin);
            writer.Write(stack.Layout.ZMax);
            writer.Write(stack.Layout.BasePlane.OriginX);
            writer.Write(stack.Layout.BasePlane.OriginY);
            writer.Write(stack.Layout.BasePlane.OriginZ);
            writer.Write(stack.Layout.BasePlane.XAxis.X);
            writer.Write(stack.Layout.BasePlane.XAxis.Y);
            writer.Write(stack.Layout.BasePlane.XAxis.Z);
            writer.Write(stack.Layout.BasePlane.YAxis.X);
            writer.Write(stack.Layout.BasePlane.YAxis.Y);
            writer.Write(stack.Layout.BasePlane.YAxis.Z);
            writer.Write(stack.Layout.BasePlane.ZAxis.X);
            writer.Write(stack.Layout.BasePlane.ZAxis.Y);
            writer.Write(stack.Layout.BasePlane.ZAxis.Z);
            writer.Write((byte)(stack.Settings.Invert ? 1 : 0));
            writer.Write((stack.Layout.Width + 7) / 8);

            for (int layer = 0; layer < stack.Layout.LayerCount; layer++)
            {
                byte[] bytes = WasperFieldBitmapRasterizer.RasterizePackedLayer(
                    stack.Field, stack.Layout, layer, stack.Settings, out int invalid, true);
                invalidTotal += invalid;
                byte[] compressed = Compress(bytes);
                writer.Write(bytes.Length);
                writer.Write(compressed.Length);
                writer.Write(compressed);
            }
            return invalidTotal;
        }

        private static byte[] Compress(byte[] source)
        {
            using var memory = new MemoryStream();
            using (var deflate = new DeflateStream(memory, CompressionLevel.Fastest, true))
                deflate.Write(source, 0, source.Length);
            return memory.ToArray();
        }

        private static string BuildManifest(WasperRasterStack stack, int format, string prefix, IList<string> paths)
        {
            var l = stack.Layout;
            var manifest = new
            {
                format = "WASPer binder jet raster job v1",
                export_format = FormatName(format),
                prefix,
                field = stack.Field.Label,
                field_quality = stack.Field.SdfQuality.ToString(),
                width_px = l.Width,
                height_px = l.Height,
                layer_count = l.LayerCount,
                size_x = l.SizeX,
                size_y = l.SizeY,
                pixel_size_requested = l.RequestedPixelSize,
                pixel_size_x = l.PixelSizeX,
                pixel_size_y = l.PixelSizeY,
                layer_height = l.LayerHeight,
                frame_origin = new[] { l.BasePlane.OriginX, l.BasePlane.OriginY, l.BasePlane.OriginZ },
                frame_x_axis = new[] { l.BasePlane.XAxis.X, l.BasePlane.XAxis.Y, l.BasePlane.XAxis.Z },
                frame_y_axis = new[] { l.BasePlane.YAxis.X, l.BasePlane.YAxis.Y, l.BasePlane.YAxis.Z },
                frame_z_axis = new[] { l.BasePlane.ZAxis.X, l.BasePlane.ZAxis.Y, l.BasePlane.ZAxis.Z },
                frame_x = new[] { l.XMin, l.XMin + l.SizeX },
                frame_y = new[] { l.YMin, l.YMin + l.SizeY },
                frame_z = new[] { l.ZMin, l.ZMax },
                mapping = stack.Settings.Mode.ToString(),
                threshold = stack.Settings.Threshold,
                field_range = new[] { Math.Min(stack.Settings.FieldRange.T0, stack.Settings.FieldRange.T1), Math.Max(stack.Settings.FieldRange.T0, stack.Settings.FieldRange.T1) },
                black_means = stack.Settings.Invert ? "no binder" : "maximum binder",
                packed_compression = format == 0 ? "per-layer DEFLATE" : null,
                image_orientation = "columns follow frame X; image bottom-to-top follows frame Y",
                files = paths.Select(Path.GetFileName).ToArray()
            };
            return JsonConvert.SerializeObject(manifest, Formatting.Indented);
        }

        private string ResolveOutputFolder(string requested, string prefix)
        {
            if (!string.IsNullOrWhiteSpace(requested)) return Path.GetFullPath(requested.Trim());
            string documentPath = OnPingDocument()?.FilePath;
            if (!string.IsNullOrWhiteSpace(documentPath))
            {
                string parent = Path.GetDirectoryName(documentPath);
                string definition = Path.GetFileNameWithoutExtension(documentPath);
                if (!string.IsNullOrWhiteSpace(parent) && !string.IsNullOrWhiteSpace(definition))
                    return Path.Combine(parent, "WASPer_" + definition, "BinderJet", prefix);
            }
            string documentId = OnPingDocument() == null ? "unsaved" : OnPingDocument().DocumentID.ToString("N");
            return Path.Combine(Path.GetTempPath(), "WASPer_3DP", documentId, "BinderJet", prefix);
        }

        private static string SanitizePrefix(string prefix)
        {
            prefix = string.IsNullOrWhiteSpace(prefix) ? "layer" : prefix.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) prefix = prefix.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(prefix) ? "layer" : prefix;
        }

        private static string FormatName(int format) => format switch
        {
            0 => "WSPBJ packed 1-bit",
            1 => "PNG 1-bit",
            _ => "PNG 8-bit grayscale"
        };

        private static Bitmap CreateIcon()
        {
            var bitmap = new Bitmap(24, 24);
            using Graphics g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var dark = new SolidBrush(Color.FromArgb(45, 45, 45));
            using var mid = new SolidBrush(Color.FromArgb(130, 130, 130));
            using var pen = new Pen(Color.FromArgb(55, 60, 70), 1.5f);
            g.FillRectangle(mid, 4, 3, 16, 12);
            g.DrawRectangle(pen, 3.5f, 2.5f, 16.5f, 12.5f);
            g.FillRectangle(dark, 7, 17, 10, 4);
            g.DrawLine(pen, 12, 11, 12, 19);
            g.DrawLine(pen, 9, 16, 12, 19);
            g.DrawLine(pen, 15, 16, 12, 19);
            return bitmap;
        }
    }
}
