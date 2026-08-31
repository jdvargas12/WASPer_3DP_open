using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text;

using Grasshopper.Kernel;

using Newtonsoft.Json;

namespace WASPer_3DP.Painting
{
    internal static class WasperPaintPersistence
    {
        internal static string SerializeEmbedded(WasperPaintState state)
        {
            return WasperPaintUtilities.Compress(
                JsonConvert.SerializeObject(state, Formatting.None));
        }

        internal static WasperPaintState DeserializeEmbedded(string value)
        {
            return JsonConvert.DeserializeObject<WasperPaintState>(
                WasperPaintUtilities.Decompress(value));
        }

        internal static void SaveSession(string destination, WasperPaintState state)
        {
            string temporary = destination + ".tmp";
            string json = JsonConvert.SerializeObject(state, Formatting.None);
            using (var file = new FileStream(
                       temporary,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            {
                writer.Write(json);
            }
            if (File.Exists(destination))
                File.Move(temporary, destination, true);
            else
                File.Move(temporary, destination);
        }

        internal static WasperPaintState LoadSession(string path)
        {
            using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return JsonConvert.DeserializeObject<WasperPaintState>(reader.ReadToEnd());
        }

        internal static void SaveBitmap(string path, Bitmap bitmap)
        {
            bitmap?.Save(path, ImageFormat.Png);
        }

        internal static string DefaultDirectory(GH_Document document)
        {
            string ghPath = document?.FilePath;
            if (!string.IsNullOrWhiteSpace(ghPath))
            {
                string parent = Path.GetDirectoryName(ghPath);
                string name = Path.GetFileNameWithoutExtension(ghPath);
                if (!string.IsNullOrWhiteSpace(parent) && !string.IsNullOrWhiteSpace(name))
                    return Path.Combine(parent, "WASPer_" + name, "MeshPaint");
            }
            string documentId = document == null
                ? "unsaved"
                : document.DocumentID.ToString("N");
            return Path.Combine(Path.GetTempPath(), "WASPer_MeshPaint", documentId);
        }

        internal static string DefaultStem(string componentName, Guid instanceGuid)
        {
            return componentName + "_MeshPaint_" +
                   instanceGuid.ToString("N").Substring(0, 8);
        }
    }
}
