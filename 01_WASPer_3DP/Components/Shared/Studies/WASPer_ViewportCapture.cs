using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;

using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace WASPer_3DP
{
    internal static class WasperViewportCapture
    {
        internal const string ActiveViewportLabel = "<Active viewport>";

        internal static IReadOnlyList<string> ViewportNames()
        {
            RhinoDoc document = RhinoDoc.ActiveDoc;
            if (document == null)
                return Array.Empty<string>();
            return document.Views
                .Where(view => view?.ActiveViewport != null)
                .Select(view => view.ActiveViewport.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static Bitmap Capture(
            WasperSnapshotSettings settings,
            bool applyWait,
            BoundingBox? requiredBounds = null)
        {
            settings ??= new WasperSnapshotSettings();
            RhinoDoc document = RhinoDoc.ActiveDoc ??
                throw new InvalidOperationException("No active Rhino document is available.");
            RhinoView view = ResolveView(document, settings.ViewportName) ?? document.Views.ActiveView;
            if (view?.ActiveViewport == null)
                throw new InvalidOperationException("The selected Rhino viewport could not be resolved.");
            if (requiredBounds.HasValue &&
                requiredBounds.Value.IsValid &&
                !view.ActiveViewport.IsVisible(requiredBounds.Value))
            {
                throw new InvalidOperationException(
                    "The linked visualization geometry is not visible in the selected viewport.");
            }

            if (applyWait)
            {
                int wait = Math.Max(0, Math.Min(10000, settings.WaitMilliseconds));
                int elapsed = 0;
                while (elapsed < wait)
                {
                    document.Views.Redraw();
                    int step = Math.Min(50, wait - elapsed);
                    Thread.Sleep(step);
                    elapsed += step;
                }
                document.Views.Redraw();
            }
            else
            {
                document.Views.Redraw();
                RhinoApp.Wait();
            }

            int width = Math.Max(64, Math.Min(16384, settings.Width));
            int height = Math.Max(64, Math.Min(16384, settings.Height));
            DisplayModeDescription mode = view.ActiveViewport.DisplayMode ??
                throw new InvalidOperationException("The selected viewport has no display mode.");
            Bitmap bitmap = view.CaptureToBitmap(new Size(width, height), mode);
            if (bitmap == null)
                throw new InvalidOperationException("Rhino returned an empty viewport capture.");
            bitmap.SetResolution(
                Math.Max(1, Math.Min(1200, settings.Dpi)),
                Math.Max(1, Math.Min(1200, settings.Dpi)));
            return bitmap;
        }

        internal static List<string> SaveBesideGcodes(
            IEnumerable<string> gcodeFiles,
            WasperSnapshotSettings settings,
            BoundingBox? requiredBounds = null)
        {
            List<string> targets = (gcodeFiles ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.ChangeExtension(path, ".png"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targets.Count == 0 || settings?.Enabled != true)
                return new List<string>();

            using Bitmap bitmap = Capture(settings, true, requiredBounds);
            foreach (string target in targets)
            {
                string folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(folder))
                    Directory.CreateDirectory(folder);
                bitmap.Save(target, ImageFormat.Png);
                if (!File.Exists(target) || new FileInfo(target).Length == 0)
                    throw new IOException("Viewport image was not written: " + target);
            }
            return targets;
        }

        internal static List<string> SaveToSnapshotFile(
            string targetPath,
            WasperSnapshotSettings settings,
            BoundingBox? requiredBounds = null)
        {
            return SaveToSnapshotFiles(new[] { targetPath }, settings, requiredBounds);
        }

        internal static List<string> SaveToSnapshotFiles(
            IEnumerable<string> targetPaths,
            WasperSnapshotSettings settings,
            BoundingBox? requiredBounds = null)
        {
            List<string> targets = (targetPaths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.ChangeExtension(path, ".png"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targets.Count == 0 || settings?.Enabled != true)
                return new List<string>();
            using Bitmap bitmap = Capture(settings, true, requiredBounds);
            foreach (string target in targets)
            {
                string folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(folder))
                    Directory.CreateDirectory(folder);
                bitmap.Save(target, ImageFormat.Png);
                if (!File.Exists(target) || new FileInfo(target).Length == 0)
                    throw new IOException("Viewport image was not written: " + target);
            }
            return targets;
        }

        private static RhinoView ResolveView(RhinoDoc document, string viewportName)
        {
            if (document == null ||
                string.IsNullOrWhiteSpace(viewportName) ||
                string.Equals(
                    viewportName.Trim(),
                    ActiveViewportLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                return document?.Views?.ActiveView;
            }

            string target = viewportName.Trim();
            foreach (RhinoView view in document.Views)
            {
                if (view?.ActiveViewport?.Name != null &&
                    view.ActiveViewport.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    return view;
                }
            }
            return null;
        }
    }
}
