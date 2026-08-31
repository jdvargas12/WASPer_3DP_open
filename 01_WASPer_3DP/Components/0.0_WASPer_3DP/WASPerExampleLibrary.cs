using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;


namespace WASPer_3DP.Components._0_0_WASPer_3DP
{
    /// <summary>
    /// Shared example discovery, interactive placement, and undo support used by
    /// WASPet. This is deliberately not a GH_Component; examples are accessed
    /// from WASPet instead of occupying a component in the Grasshopper palette.
    /// </summary>
    internal static class WASPerExampleLibrary
    {
        internal static bool BeginExamplePlacement(
            string fileName,
            GH_Document targetDoc,
            out string error)
        {
            error = string.Empty;
            GH_Canvas canvas = Instances.ActiveCanvas;
            if (canvas == null)
            {
                error = "There is no active Grasshopper canvas for example placement.";
                return false;
            }

            // WASPet is also visible on Grasshopper's welcome canvas, before a
            // document exists. Create and activate a blank document so choosing
            // an example there behaves like starting a new definition.
            if (targetDoc == null && canvas.Document == null)
            {
                targetDoc = new GH_Document();
                Instances.DocumentServer.AddDocument(targetDoc);
                canvas.Document = targetDoc;
            }
            else if (targetDoc == null)
            {
                targetDoc = canvas.Document;
            }

            if (targetDoc == null || canvas.Document != targetDoc)
            {
                error = "The target Grasshopper document is not active on the canvas.";
                return false;
            }

            GH_Document exampleDoc = LoadExampleFromDisk(fileName);
            if (exampleDoc == null)
            {
                error = $"Could not load example file '{fileName}'. Check the examples folder and file name.";
                return false;
            }

            RectangleF bounds = exampleDoc.BoundingBox();
            if (bounds.IsEmpty)
            {
                error = $"Example file '{fileName}' does not contain placeable objects.";
                return false;
            }

            try
            {
                var initialEvent = new GH_CanvasMouseEvent(
                    canvas.CursorControlPosition,
                    canvas.CursorCanvasPosition,
                    MouseButtons.None,
                    0,
                    0);
                canvas.ActiveInteraction = new ExamplePlacementInteraction(
                    canvas,
                    initialEvent,
                    targetDoc,
                    exampleDoc,
                    fileName,
                    bounds);
                canvas.Focus();
                canvas.Refresh();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not start placement for '{fileName}'.\n\n{ex.Message}";
                return false;
            }
        }

        internal static bool BeginGeneratedPlacement(
            GH_Document placeableDocument,
            string displayName,
            out string error)
        {
            error = string.Empty;
            GH_Canvas canvas = Instances.ActiveCanvas;
            if (canvas == null)
            {
                error = "There is no active Grasshopper canvas for workflow placement.";
                return false;
            }

            GH_Document targetDoc = canvas.Document;
            if (targetDoc == null)
            {
                targetDoc = new GH_Document();
                Instances.DocumentServer.AddDocument(targetDoc);
                canvas.Document = targetDoc;
            }
            if (placeableDocument == null)
            {
                error = $"The suggested workflow '{displayName}' could not be created.";
                return false;
            }

            RectangleF bounds = placeableDocument.BoundingBox();
            if (bounds.IsEmpty)
            {
                error = $"The suggested workflow '{displayName}' has no placeable objects.";
                return false;
            }

            try
            {
                var initialEvent = new GH_CanvasMouseEvent(
                    canvas.CursorControlPosition,
                    canvas.CursorCanvasPosition,
                    MouseButtons.None,
                    0,
                    0);
                canvas.ActiveInteraction = new ExamplePlacementInteraction(
                    canvas,
                    initialEvent,
                    targetDoc,
                    placeableDocument,
                    displayName,
                    bounds);
                try
                {
                    Instances.DocumentEditor?.Show();
                    Instances.DocumentEditor?.BringToFront();
                }
                catch { }
                canvas.Focus();
                canvas.Refresh();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not start placement for '{displayName}'.\n\n{ex.Message}";
                return false;
            }
        }

        internal static bool InsertExampleIntoDocument(
            string fileName,
            GH_Document targetDoc,
            out string error)
        {
            error = string.Empty;
            if (targetDoc == null)
            {
                error = "There is no active Grasshopper document.";
                return false;
            }

            GH_Document exampleDoc = LoadExampleFromDisk(fileName);
            if (exampleDoc == null)
            {
                error = $"Could not load example file '{fileName}'. Check the examples folder and file name.";
                return false;
            }

            try
            {
                // MergeDocument(remap:true) does not reliably remap GH_Group GUIDs.
                // Remap collisions first so an example can be inserted repeatedly.
                RemapConflictingGroupGuids(targetDoc, exampleDoc);
                targetDoc.MergeDocument(exampleDoc, true, true);
                targetDoc.NewSolution(true);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not insert example file '{fileName}'.\n\n{ex.Message}";
                return false;
            }
        }

        private sealed class ExamplePlacementInteraction : GH_AbstractInteraction
        {
            private readonly GH_Document _targetDocument;
            private readonly GH_Document _exampleDocument;
            private readonly string _fileName;
            private readonly RectangleF _sourceBounds;
            private PointF _anchor;
            private bool _finished;
            private GH_PanInteraction _panInteraction;

            public ExamplePlacementInteraction(
                GH_Canvas canvas,
                GH_CanvasMouseEvent initialEvent,
                GH_Document targetDocument,
                GH_Document exampleDocument,
                string fileName,
                RectangleF sourceBounds)
                : base(canvas, initialEvent, true)
            {
                _targetDocument = targetDocument;
                _exampleDocument = exampleDocument;
                _fileName = fileName;
                _sourceBounds = sourceBounds;
                _anchor = canvas.CursorCanvasPosition;
                canvas.CanvasPostPaintWidgets += PaintPreview;
                canvas.Cursor = Cursors.Cross;
            }

            public override bool DeactivateOnFocusLoss => false;

            public override GH_ObjectResponse RespondToMouseMove(
                GH_Canvas sender,
                GH_CanvasMouseEvent e)
            {
                if (_panInteraction != null)
                {
                    _panInteraction.RespondToMouseMove(sender, e);
                    _anchor = sender.CursorCanvasPosition;
                    sender.Refresh();
                    return GH_ObjectResponse.Handled;
                }

                _anchor = e.CanvasLocation;
                sender.Refresh();
                return GH_ObjectResponse.Handled;
            }

            public override GH_ObjectResponse RespondToMouseDown(
                GH_Canvas sender,
                GH_CanvasMouseEvent e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    _anchor = e.CanvasLocation;
                    PlaceExample();
                    return GH_ObjectResponse.Release;
                }

                if (e.Button == MouseButtons.Right)
                {
                    _panInteraction?.Destroy();
                    _panInteraction = new GH_PanInteraction(sender, e);
                    return GH_ObjectResponse.Handled;
                }

                return GH_ObjectResponse.Handled;
            }

            public override GH_ObjectResponse RespondToMouseUp(
                GH_Canvas sender,
                GH_CanvasMouseEvent e)
            {
                if (e.Button == MouseButtons.Right && _panInteraction != null)
                {
                    _panInteraction.RespondToMouseUp(sender, e);
                    _panInteraction.Destroy();
                    _panInteraction = null;
                    _anchor = sender.CursorCanvasPosition;
                    sender.Cursor = Cursors.Cross;
                    sender.Refresh();
                    return GH_ObjectResponse.Handled;
                }

                return GH_ObjectResponse.Handled;
            }

            public override GH_ObjectResponse RespondToKeyDown(
                GH_Canvas sender,
                KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                    return GH_ObjectResponse.Release;

                return GH_ObjectResponse.Handled;
            }

            public override void Destroy()
            {
                if (!_finished)
                {
                    _panInteraction?.Destroy();
                    _panInteraction = null;
                    Canvas.CanvasPostPaintWidgets -= PaintPreview;
                    Canvas.ResetCursor();
                    Canvas.Refresh();
                    _finished = true;
                }

                base.Destroy();
            }

            private void PaintPreview(GH_Canvas canvas)
            {
                if (_finished || canvas.Graphics == null)
                    return;

                float dx = _anchor.X - _sourceBounds.Left;
                float dy = _anchor.Y - _sourceBounds.Top;
                using var fill = new SolidBrush(Color.FromArgb(38, 255, 184, 35));
                using var outline = new Pen(Color.FromArgb(210, 214, 139, 0), 1.2f)
                {
                    DashStyle = System.Drawing.Drawing2D.DashStyle.Dash
                };

                foreach (IGH_DocumentObject obj in _exampleDocument.Objects)
                {
                    RectangleF bounds = obj.Attributes?.Bounds ?? RectangleF.Empty;
                    if (bounds.IsEmpty)
                        continue;

                    bounds.Offset(dx, dy);
                    canvas.Graphics.FillRectangle(fill, bounds);
                    canvas.Graphics.DrawRectangle(
                        outline,
                        bounds.X,
                        bounds.Y,
                        bounds.Width,
                        bounds.Height);
                }

                var overall = _sourceBounds;
                overall.Offset(dx, dy);
                using var overallPen = new Pen(Color.FromArgb(235, 255, 165, 0), 2.0f);
                canvas.Graphics.DrawRectangle(
                    overallPen,
                    overall.X,
                    overall.Y,
                    overall.Width,
                    overall.Height);

                const float crossSize = 8f;
                canvas.Graphics.DrawLine(
                    overallPen,
                    _anchor.X - crossSize,
                    _anchor.Y,
                    _anchor.X + crossSize,
                    _anchor.Y);
                canvas.Graphics.DrawLine(
                    overallPen,
                    _anchor.X,
                    _anchor.Y - crossSize,
                    _anchor.X,
                    _anchor.Y + crossSize);
            }

            private void PlaceExample()
            {
                if (_finished)
                    return;

                float dx = _anchor.X - _sourceBounds.Left;
                float dy = _anchor.Y - _sourceBounds.Top;
                foreach (IGH_DocumentObject obj in _exampleDocument.Objects)
                {
                    if (obj?.Attributes == null)
                        continue;

                    PointF pivot = obj.Attributes.Pivot;
                    obj.Attributes.Pivot = new PointF(pivot.X + dx, pivot.Y + dy);
                }

                var existingIds = new HashSet<Guid>(
                    _targetDocument.Objects.Select(obj => obj.InstanceGuid));

                RemapConflictingGroupGuids(_targetDocument, _exampleDocument);
                _targetDocument.MergeDocument(_exampleDocument, true, true);

                var insertedObjects = _targetDocument.Objects
                    .Where(obj => !existingIds.Contains(obj.InstanceGuid))
                    .ToArray();
                if (insertedObjects.Length > 0)
                {
                    _targetDocument.UndoUtil.RecordAddObjectEvent(
                        "Insert WASPer example",
                        insertedObjects);
                }

                _targetDocument.NewSolution(true);
            }
        }

        private static GH_Document LoadExampleFromDisk(string fileName)
        {
            string examplesFolder = GetExamplesFolder();
            string fullPath = Path.Combine(examplesFolder, fileName);

            if (!File.Exists(fullPath))
                return null;

            var io = new GH_DocumentIO();
            if (!io.Open(fullPath))
                return null;

            return io.Document;
        }

        internal sealed class ExampleFileGroups
        {
            public readonly List<string> BuiltIn = new List<string>();
            public readonly List<string> User = new List<string>();
        }

        internal static ExampleFileGroups GetExampleFileGroups()
        {
            var groups = new ExampleFileGroups();
            string examplesFolder = GetExamplesFolder();
            if (!Directory.Exists(examplesFolder))
                return groups;

            var allNames = Directory
                .EnumerateFiles(examplesFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    string ext = Path.GetExtension(path);
                    return string.Equals(ext, ".gh", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(ext, ".ghx", StringComparison.OrdinalIgnoreCase);
                })
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var builtInNames = ReadBuiltInExampleManifest(examplesFolder)
                .Where(name => allNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Development fallback: if no manifest exists, all examples in Resources/Examples
            // are treated as built-in because that folder is the source of truth.
            if (builtInNames.Count == 0 && IsDevExamplesFolder(examplesFolder))
                builtInNames.AddRange(allNames);

            var builtInSet = new HashSet<string>(builtInNames, StringComparer.OrdinalIgnoreCase);

            groups.BuiltIn.AddRange(builtInNames);
            groups.User.AddRange(allNames.Where(name => !builtInSet.Contains(name)));

            return groups;
        }

        private static IEnumerable<string> ReadBuiltInExampleManifest(string examplesFolder)
        {
            string manifestPath = Path.Combine(examplesFolder, "_built_in_examples.txt");
            if (!File.Exists(manifestPath))
                return Enumerable.Empty<string>();

            return File
                .ReadAllLines(manifestPath)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line =>
                {
                    string ext = Path.GetExtension(line);
                    return string.Equals(ext, ".gh", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(ext, ".ghx", StringComparison.OrdinalIgnoreCase);
                });
        }

        private static bool IsDevExamplesFolder(string examplesFolder)
        {
            if (string.IsNullOrWhiteSpace(examplesFolder))
                return false;

            string normalized = examplesFolder
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return normalized.EndsWith(
                Path.Combine("Resources", "Examples"),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void RemapConflictingGroupGuids(GH_Document targetDoc, GH_Document sourceDoc)
        {
            var takenGuids = new HashSet<Guid>(targetDoc.Objects.Select(o => o.InstanceGuid));
            foreach (var group in sourceDoc.Objects.OfType<GH_Group>())
            {
                if (!takenGuids.Contains(group.InstanceGuid))
                    continue;

                Guid newGuid;
                do
                {
                    newGuid = Guid.NewGuid();
                }
                while (takenGuids.Contains(newGuid));

                group.NewInstanceGuid(newGuid);
                takenGuids.Add(newGuid);
            }
        }

        private static string GetExamplesFolder()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string ghaFolder = Path.GetDirectoryName(asm.Location);

            // 1) Yak / package layout: .../examples
            string examplesYak = Path.Combine(ghaFolder, "examples");
            if (Directory.Exists(examplesYak))
                return examplesYak;

            // 2) Your dev layout: .../Resources/Examples
            string examplesDev = Path.Combine(ghaFolder, "Resources", "Examples");
            if (Directory.Exists(examplesDev))
                return examplesDev;

            return ghaFolder;
        }


        private static void OffsetDocumentToUpperLeft(GH_Document doc)
        {
            RectangleF bbox = doc.BoundingBox();
            if (bbox.IsEmpty)
                return;

            // Default target if we can't get the canvas
            PointF target = new PointF(50, 50);

            var canvas = Instances.ActiveCanvas;
            if (canvas != null)
            {
                // VisibleRegion is in canvas coordinates
                RectangleF region = canvas.Viewport.VisibleRegion;
                // small margin from the very corner
                target = new PointF(region.Left + 50, region.Top + 50);
            }

            // Move so that the EXAMPLE'S top-left goes to "target"
            float dx = target.X - bbox.Left;
            float dy = target.Y - bbox.Top;

            foreach (var obj in doc.Objects)
            {
                if (obj == null) continue;

                var attr = obj.Attributes;
                attr.Pivot = new PointF(attr.Pivot.X + dx, attr.Pivot.Y + dy);
            }
        }
    }
}
