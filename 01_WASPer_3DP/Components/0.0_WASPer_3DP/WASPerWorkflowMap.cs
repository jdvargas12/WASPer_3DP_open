using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

using Rhino;
using Rhino.Display;
using Rhino.UI;

using WASPer_3DP.Components._0_0_WASPer_3DP;

namespace WASPer_3DP
{
    /// <summary>
    /// Interactive, screen-space WASPer structure map drawn in the active Rhino
    /// viewport. It is UI-only and never creates Rhino document geometry.
    /// </summary>
    internal static class WasperWorkflowMapManager
    {
        // No natural Eto Control owner exists here -- this is a static viewport-overlay manager, not
        // a modeless form -- so dialogs resolve the Rhino main window the same way
        // Sm01EtoManagerForm/WasperEtoPaintForm do for their Owner property. RhinoDoc.ActiveDoc can
        // be null (no open document); Eto's dialog APIs are relied on to tolerate a null parent the
        // same way these calls previously passed no owner at all under WinForms.
        private static Eto.Forms.Control DialogOwner() =>
            RhinoDoc.ActiveDoc != null ? RhinoEtoApp.MainWindowForDocument(RhinoDoc.ActiveDoc) : null;

        private const string XKey = "WASPer_3DP.WorkflowMap.X";
        private const string YKey = "WASPer_3DP.WorkflowMap.Y";
        private const string WorkflowFolderKey = "WASPer_3DP.WorkflowMap.LibraryFolder";

        private static readonly WorkflowCategory[] Categories =
        {
            new WorkflowCategory("Geometry", new[] { "2.0_Geometry", "2.1_Facades" }, 24, 74, Color.FromArgb(61, 126, 183)),
            new WorkflowCategory("Slicing", new[] { "3.0_Slicing" }, 174, 74, Color.FromArgb(55, 150, 120)),
            new WorkflowCategory("Print Paths", new[] { "4.0_Print Paths" }, 324, 74, Color.FromArgb(235, 155, 35)),
            new WorkflowCategory("G-code", new[] { "5.0_Gcode" }, 474, 74, Color.FromArgb(215, 91, 71)),
            new WorkflowCategory("Robot G-code", new[] { "5.1_Robot Gcode" }, 624, 74, Color.FromArgb(139, 96, 174)),
            new WorkflowCategory("Fields", new[] { "2.2_Fields", "2.3_Fields_3D" }, 24, 142, Color.FromArgb(73, 139, 204)),
            new WorkflowCategory("Infills", new[] { "3.1_Infills" }, 24, 210, Color.FromArgb(49, 160, 171)),
            new WorkflowCategory("Printability", new[] { "4.1_Printability" }, 474, 210, Color.FromArgb(201, 111, 174)),
            new WorkflowCategory("Workflows", Array.Empty<string>(), 284, 210, Color.FromArgb(232, 169, 46))
        };

        private static readonly WorkflowDefinition[] SuggestedWorkflows =
        {
            new WorkflowDefinition(
                new Guid("DB5B78F1-98C5-44BF-8E05-AFB09D5EAE01"),
                "Path preview starter",
                "Pp01 Path from Curves → Pp04 Visualize Print Path. Supply sliced/role-tagged curves and layer settings to Pp01.",
                new[]
                {
                    new Guid("6AB6E12C-5FC4-4E0F-AE00-4744CE81B769"),
                    new Guid("B6E4A2C1-7D93-4F80-AB16-5C9E2D7F4381")
                }),
            new WorkflowDefinition(
                new Guid("62D7E942-94EE-49F2-B409-026D15FE2E02"),
                "Optimize and preview",
                "Pp01 → Pp03 Printing Path Optimizer → Pp04. A compact path-preparation chain with preview.",
                new[]
                {
                    new Guid("6AB6E12C-5FC4-4E0F-AE00-4744CE81B769"),
                    new Guid("7C3A1E53-3CD1-4830-A665-85F4B826FB40"),
                    new Guid("B6E4A2C1-7D93-4F80-AB16-5C9E2D7F4381")
                }),
            new WorkflowDefinition(
                new Guid("4F6935F2-0DAA-4C71-8344-3F8BE1E0E303"),
                "Continuous path and preview",
                "Pp01 → Pp10 Continuous Print Path → Pp04. Configure continuity only after the base path is valid.",
                new[]
                {
                    new Guid("6AB6E12C-5FC4-4E0F-AE00-4744CE81B769"),
                    new Guid("C2AD21AD-120A-4E18-9319-4C4887A6D6CC"),
                    new Guid("B6E4A2C1-7D93-4F80-AB16-5C9E2D7F4381")
                })
        };

        private static readonly Tuple<string, string>[] Connections =
        {
            Tuple.Create("Geometry", "Slicing"),
            Tuple.Create("Fields", "Slicing"),
            Tuple.Create("Infills", "Slicing"),
            Tuple.Create("Slicing", "Print Paths"),
            Tuple.Create("Print Paths", "G-code"),
            Tuple.Create("Print Paths", "Printability"),
            Tuple.Create("G-code", "Robot G-code")
        };

        private static bool _loaded;
        private static bool _enabled;
        private static Point _origin = new Point(24, 24);
        private static string _expandedCategory = "Print Paths";
        private static Guid _selectedComponent;
        private static MapHit _hover;
        private static WorkflowMapConduit _conduit;
        private static WorkflowMapMouseCallback _mouse;
        private static bool _dragging;
        private static Point _dragStart;
        private static Point _originStart;
        private static List<WorkflowDefinition> _userWorkflows;
        private static string _workflowFolder;

        internal static bool Enabled
        {
            get
            {
                RestoreAfterGrasshopperOpen();
                return _enabled;
            }
            set
            {
                EnsureLoaded();
                if (_enabled == value)
                {
                    RestoreAfterGrasshopperOpen();
                    return;
                }
                _enabled = value;
                EnsureInfrastructure();
                _conduit.Enabled = value;
                _mouse.Enabled = value;
                if (!value)
                {
                    _dragging = false;
                    _hover = null;
                }
                Redraw();
            }
        }

        internal static void Toggle() => Enabled = !Enabled;

        internal static void RestoreAfterGrasshopperOpen()
        {
            EnsureLoaded();
            EnsureInfrastructure();
            _conduit.Enabled = _enabled;
            _mouse.Enabled = _enabled;
            if (_enabled)
                Redraw();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            // Visibility is intentionally session-only: keep it while
            // Grasshopper is reopened in this Rhino process, but start hidden
            // after a full Rhino restart.
            _enabled = false;
            try
            {
                _origin = new Point(
                    Math.Max(0, Instances.Settings.GetValue(XKey, 24)),
                    Math.Max(0, Instances.Settings.GetValue(YKey, 24)));
            }
            catch
            {
                _enabled = false;
                _origin = new Point(24, 24);
            }
            EnsureInfrastructure();
            _conduit.Enabled = _enabled;
            _mouse.Enabled = _enabled;
        }

        private static void EnsureInfrastructure()
        {
            if (_conduit == null)
                _conduit = new WorkflowMapConduit();
            if (_mouse == null)
                _mouse = new WorkflowMapMouseCallback();
        }

        private static void Save(string key, int value)
        {
            try { Instances.Settings.SetValue(key, value); }
            catch { }
        }

        private static void Redraw()
        {
            try { RhinoDoc.ActiveDoc?.Views.Redraw(); }
            catch { }
        }

        private static WorkflowMapLayout BuildLayout()
        {
            List<WorkflowDefinition> workflows = _expandedCategory == "Workflows"
                ? GetAllWorkflows()
                : null;
            int workflowRows = workflows == null ? 0 : (int)Math.Ceiling(workflows.Count / 2.0);
            int panelHeight = workflows == null
                ? 620
                : Math.Max(620, 348 + workflowRows * 52 + 85);
            var layout = new WorkflowMapLayout
            {
                Panel = new Rectangle(_origin.X, _origin.Y, 780, panelHeight),
                Header = new Rectangle(_origin.X, _origin.Y, 780, 42),
                Close = new Rectangle(_origin.X + 742, _origin.Y + 8, 28, 26)
            };

            foreach (WorkflowCategory category in Categories)
            {
                var rect = new Rectangle(
                    _origin.X + category.X,
                    _origin.Y + category.Y,
                    category.Name == "Workflows" ? 166 : 126,
                    44);
                layout.Categories.Add(new MapHit(MapHitKind.Category, rect, category.Name, Guid.Empty));
            }

            WorkflowCategory expanded = Categories.FirstOrDefault(
                category => category.Name == _expandedCategory) ?? Categories[0];
            if (expanded.Name == "Workflows")
            {
                layout.Components.Add(new MapHit(
                    MapHitKind.SaveWorkflow,
                    new Rectangle(_origin.X + 24, _origin.Y + 304, 400, 32),
                    "+ Save selected Grasshopper objects as a workflow",
                    Guid.Empty,
                    "Select objects in Grasshopper, then click here to save their values and internal connections as a reusable workflow."));
                layout.Components.Add(new MapHit(
                    MapHitKind.SetWorkflowFolder,
                    new Rectangle(_origin.X + 434, _origin.Y + 304, 248, 32),
                    "Choose workflow folder…",
                    Guid.Empty,
                    "Choose the folder where WASPer saves workflows and scans for existing .gh and .ghx workflow files."));
                layout.Components.Add(new MapHit(
                    MapHitKind.RefreshWorkflows,
                    new Rectangle(_origin.X + 692, _origin.Y + 304, 58, 32),
                    "↻",
                    Guid.Empty,
                    "Rescan the configured workflow folder for newly added or removed workflow files."));
                for (int i = 0; i < workflows.Count; i++)
                {
                    WorkflowDefinition workflow = workflows[i];
                    int column = i % 2;
                    int row = i / 2;
                    var rect = new Rectangle(
                        _origin.X + 24 + column * 363,
                        _origin.Y + 348 + row * 52,
                        353,
                        44);
                    layout.Components.Add(new MapHit(
                        MapHitKind.Workflow,
                        rect,
                        workflow.Name,
                        workflow.Id,
                        workflow.Description));
                }
                return layout;
            }

            List<IGH_ObjectProxy> proxies = Instances.ComponentServer.ObjectProxies
                .Where(proxy =>
                    proxy != null &&
                    !proxy.Obsolete &&
                    proxy.Exposure != GH_Exposure.hidden &&
                    proxy.Desc != null &&
                    string.Equals(
                        proxy.Desc.Category,
                        WASPerPalette.DesignFabrication,
                        StringComparison.OrdinalIgnoreCase) &&
                    expanded.Subcategories.Contains(
                        proxy.Desc.SubCategory,
                        StringComparer.OrdinalIgnoreCase))
                .OrderBy(proxy => proxy.Desc.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            const int top = 304;
            const int rowHeight = 22;
            const int columnWidth = 368;
            int rows = Math.Max(1, (int)Math.Ceiling(proxies.Count / 2.0));
            for (int i = 0; i < proxies.Count; i++)
            {
                int column = i / rows;
                int row = i % rows;
                var rect = new Rectangle(
                    _origin.X + 24 + column * columnWidth,
                    _origin.Y + top + row * rowHeight,
                    columnWidth - 10,
                    20);
                layout.Components.Add(new MapHit(
                    MapHitKind.Component,
                    rect,
                    proxies[i].Desc.Name,
                    proxies[i].Guid,
                    proxies[i].Desc.Description));
            }
            return layout;
        }

        private static MapHit Hit(Point point)
        {
            WorkflowMapLayout layout = BuildLayout();
            if (layout.Close.Contains(point))
                return new MapHit(MapHitKind.Close, layout.Close, "Close", Guid.Empty);
            foreach (MapHit hit in layout.Components)
                if (hit.Bounds.Contains(point)) return hit;
            foreach (MapHit hit in layout.Categories)
                if (hit.Bounds.Contains(point)) return hit;
            if (layout.Header.Contains(point))
                return new MapHit(MapHitKind.Header, layout.Header, "Move", Guid.Empty);
            if (layout.Panel.Contains(point))
                return new MapHit(MapHitKind.Panel, layout.Panel, string.Empty, Guid.Empty);
            return null;
        }

        private static void MouseMove(MouseCallbackEventArgs e)
        {
            if (!_enabled || !IsActiveView(e?.View)) return;
            Point point = e.ViewportPoint;
            if (_dragging)
            {
                _origin = new Point(
                    Math.Max(0, _originStart.X + point.X - _dragStart.X),
                    Math.Max(0, _originStart.Y + point.Y - _dragStart.Y));
                e.Cancel = true;
                Redraw();
                return;
            }

            MapHit hit = Hit(point);
            if (!MapHit.Same(_hover, hit))
            {
                _hover = hit;
                Redraw();
            }
        }

        private static void MouseDown(MouseCallbackEventArgs e)
        {
            if (!_enabled || !IsActiveView(e?.View) || e.Button != MouseButtons.Left) return;
            MapHit hit = Hit(e.ViewportPoint);
            if (hit == null) return;
            e.Cancel = true;
            switch (hit.Kind)
            {
                case MapHitKind.Close:
                    Enabled = false;
                    break;
                case MapHitKind.Header:
                    _dragging = true;
                    _dragStart = e.ViewportPoint;
                    _originStart = _origin;
                    break;
                case MapHitKind.Category:
                    _expandedCategory = hit.Label;
                    if (hit.Label == "Workflows")
                        _userWorkflows = null;
                    _selectedComponent = Guid.Empty;
                    Redraw();
                    break;
                case MapHitKind.Component:
                case MapHitKind.Workflow:
                    _selectedComponent = hit.Guid;
                    Redraw();
                    break;
                case MapHitKind.SaveWorkflow:
                    SaveSelectedWorkflow();
                    break;
                case MapHitKind.SetWorkflowFolder:
                    ChooseUserWorkflowFolder();
                    break;
                case MapHitKind.RefreshWorkflows:
                    RefreshUserWorkflows();
                    break;
            }
        }

        private static void MouseUp(MouseCallbackEventArgs e)
        {
            if (!_dragging || !IsActiveView(e?.View) || e.Button != MouseButtons.Left) return;
            e.Cancel = true;
            _dragging = false;
            Save(XKey, _origin.X);
            Save(YKey, _origin.Y);
            Redraw();
        }

        private static void MouseDoubleClick(MouseCallbackEventArgs e)
        {
            if (!_enabled || !IsActiveView(e?.View) || e.Button != MouseButtons.Left) return;
            MapHit hit = Hit(e.ViewportPoint);
            if (hit == null ||
                (hit.Kind != MapHitKind.Component && hit.Kind != MapHitKind.Workflow))
                return;
            e.Cancel = true;
            bool started;
            string error = string.Empty;
            if (hit.Kind == MapHitKind.Workflow)
            {
                WorkflowDefinition workflow = GetAllWorkflows().FirstOrDefault(
                    item => item.Id == hit.Guid);
                started = workflow != null && (workflow.IsUser
                    ? BeginUserWorkflow(workflow, out error)
                    : WASPerWorkflowAssembly.Begin(workflow, out error));
                if (workflow == null)
                    error = "The selected suggested workflow could not be found.";
            }
            else
            {
                started = WASPerComponentPlacement.Begin(hit.Guid, out error);
            }
            if (!started)
            {
                Eto.Forms.MessageBox.Show(
                    DialogOwner(),
                    error,
                    "WASPer structure map",
                    Eto.Forms.MessageBoxButtons.OK,
                    Eto.Forms.MessageBoxType.Error);
            }
        }

        private static List<WorkflowDefinition> GetAllWorkflows()
        {
            var result = new List<WorkflowDefinition>(SuggestedWorkflows);
            result.AddRange(GetUserWorkflows());
            return result;
        }

        private static IEnumerable<WorkflowDefinition> GetUserWorkflows()
        {
            if (_userWorkflows != null)
                return _userWorkflows;

            _userWorkflows = new List<WorkflowDefinition>();
            try
            {
                string folder = GetUserWorkflowFolder();
                if (!Directory.Exists(folder))
                    return _userWorkflows;

                foreach (string path in Directory
                    .EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(item =>
                        string.Equals(Path.GetExtension(item), ".gh", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetExtension(item), ".ghx", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
                {
                    _userWorkflows.Add(new WorkflowDefinition(
                        Guid.NewGuid(),
                        Path.GetFileNameWithoutExtension(path),
                        "My workflow — saved from a Grasshopper selection. Double-click to place the complete assembly.",
                        path));
                }
            }
            catch
            {
                // A read-only or unavailable profile folder should not prevent
                // the built-in workflow library from being shown.
            }
            return _userWorkflows;
        }

        private static string GetUserWorkflowFolder()
        {
            if (!string.IsNullOrWhiteSpace(_workflowFolder))
                return _workflowFolder;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string fallback = Path.Combine(appData, "WASPer_3DP", "Workflows");
            try
            {
                string saved = Instances.Settings.GetValue(WorkflowFolderKey, fallback);
                _workflowFolder = Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(
                        string.IsNullOrWhiteSpace(saved) ? fallback : saved));
            }
            catch
            {
                _workflowFolder = fallback;
            }
            return _workflowFolder;
        }

        internal static void ChooseUserWorkflowFolder()
        {
            try
            {
                string current = GetUserWorkflowFolder();
                Directory.CreateDirectory(current);
                // Eto.Forms.SelectFolderDialog is the folder-picker equivalent of WinForms'
                // FolderBrowserDialog -- no in-repo precedent existed yet for this type, unlike
                // SaveFileDialog/OpenFileDialog, so its member names (Title/Directory rather than
                // Description/SelectedPath, no ShowNewFolderButton equivalent) are taken from general
                // Eto API knowledge and not yet build-confirmed.
                using var dialog = new Eto.Forms.SelectFolderDialog
                {
                    Title = "Choose the WASPer workflow library folder. Existing .gh and .ghx files in this folder will appear under Workflows.",
                    Directory = current
                };
                if (dialog.ShowDialog(DialogOwner()) != Eto.Forms.DialogResult.Ok ||
                    string.IsNullOrWhiteSpace(dialog.Directory))
                    return;

                _workflowFolder = Path.GetFullPath(dialog.Directory);
                Instances.Settings.SetValue(WorkflowFolderKey, _workflowFolder);
                RefreshUserWorkflows();
            }
            catch (Exception ex)
            {
                Eto.Forms.MessageBox.Show(
                    DialogOwner(),
                    "The workflow folder could not be configured.\n\n" + ex.Message,
                    "WASPer workflow library",
                    Eto.Forms.MessageBoxButtons.OK,
                    Eto.Forms.MessageBoxType.Error);
            }
        }

        internal static void RefreshUserWorkflows()
        {
            _userWorkflows = null;
            _selectedComponent = Guid.Empty;
            Redraw();
        }

        private static void SaveSelectedWorkflow()
        {
            GH_Document document = Instances.ActiveCanvas?.Document;
            List<IGH_DocumentObject> selected = document?.SelectedObjects();
            if (document == null || selected == null || selected.Count == 0)
            {
                Eto.Forms.MessageBox.Show(
                    DialogOwner(),
                    "Select the components, parameters, panels, and groups that belong to the workflow in Grasshopper, then try again.",
                    "Save WASPer workflow",
                    Eto.Forms.MessageBoxButtons.OK,
                    Eto.Forms.MessageBoxType.Information);
                return;
            }

            string name = "My workflow";
            if (!Dialogs.ShowEditBox(
                    "Save WASPer workflow",
                    "Workflow name:",
                    name,
                    false,
                    out name) || string.IsNullOrWhiteSpace(name))
                return;

            name = name.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            if (string.IsNullOrWhiteSpace(name))
                name = "My workflow";

            try
            {
                string folder = GetUserWorkflowFolder();
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, name + ".gh");
                if (File.Exists(path) && Eto.Forms.MessageBox.Show(
                        DialogOwner(),
                        $"A workflow named '{name}' already exists. Replace it?",
                        "Save WASPer workflow",
                        Eto.Forms.MessageBoxButtons.YesNo,
                        Eto.Forms.MessageBoxType.Question) != Eto.Forms.DialogResult.Yes)
                    return;

                var sourceIo = new GH_DocumentIO(document);
                Guid[] ids = selected.Select(item => item.InstanceGuid).ToArray();
                if (!sourceIo.Copy(GH_ClipboardType.Local, ids) ||
                    string.IsNullOrWhiteSpace(sourceIo.LocalClipboardContent))
                    throw new InvalidOperationException("Grasshopper could not serialize the selected objects.");

                var workflowIo = new GH_DocumentIO
                {
                    LocalClipboardContent = sourceIo.LocalClipboardContent
                };
                if (!workflowIo.Paste(GH_ClipboardType.Local) || workflowIo.Document == null)
                    throw new InvalidOperationException("Grasshopper could not build the reusable workflow document.");
                // The library file is a template, not an identity-preserving
                // reference to the objects in the source definition.
                workflowIo.Document.MutateAllIds();
                if (!workflowIo.SaveQuiet(path))
                    throw new IOException("Grasshopper could not write the workflow file.");

                _userWorkflows = null;
                _selectedComponent = Guid.Empty;
                Redraw();
            }
            catch (Exception ex)
            {
                Eto.Forms.MessageBox.Show(
                    DialogOwner(),
                    "The workflow could not be saved.\n\n" + ex.Message,
                    "Save WASPer workflow",
                    Eto.Forms.MessageBoxButtons.OK,
                    Eto.Forms.MessageBoxType.Error);
            }
        }

        private static bool BeginUserWorkflow(WorkflowDefinition workflow, out string error)
        {
            error = string.Empty;
            try
            {
                if (workflow == null || string.IsNullOrWhiteSpace(workflow.SourcePath) ||
                    !File.Exists(workflow.SourcePath))
                {
                    error = "The saved workflow file could not be found.";
                    return false;
                }
                var io = new GH_DocumentIO();
                if (!io.Open(workflow.SourcePath) || io.Document == null)
                {
                    error = $"Could not open the saved workflow '{workflow.Name}'.";
                    return false;
                }
                GH_Document freshTemplate = GH_Document.DuplicateDocument(io.Document);
                if (freshTemplate == null)
                {
                    error = $"Could not duplicate the saved workflow '{workflow.Name}'.";
                    return false;
                }
                freshTemplate.MutateAllIds();
                return WASPerExampleLibrary.BeginGeneratedPlacement(
                    freshTemplate,
                    workflow.Name,
                    out error);
            }
            catch (Exception ex)
            {
                error = "Could not place the saved workflow.\n\n" + ex.Message;
                return false;
            }
        }

        private static bool IsActiveView(RhinoView view)
        {
            RhinoView active = RhinoDoc.ActiveDoc?.Views.ActiveView;
            return view != null && active != null &&
                   view.RuntimeSerialNumber == active.RuntimeSerialNumber;
        }

        private sealed class WorkflowMapConduit : DisplayConduit
        {
            protected override void DrawForeground(DrawEventArgs e)
            {
                if (!_enabled || e?.Viewport?.ParentView == null ||
                    RhinoDoc.ActiveDoc?.Views.ActiveView == null ||
                    e.Viewport.ParentView.RuntimeSerialNumber !=
                    RhinoDoc.ActiveDoc.Views.ActiveView.RuntimeSerialNumber)
                    return;

                WorkflowMapLayout layout = BuildLayout();
                DisplayPipeline display = e.Display;
                display.Draw2dRectangle(layout.Panel, Color.FromArgb(220, 30, 36, 48), 2, Color.FromArgb(232, 247, 249, 253));
                display.Draw2dRectangle(layout.Header, Color.FromArgb(255, 27, 73, 116), 1, Color.FromArgb(245, 32, 91, 145));
                Text(display, "WASPer structure map", layout.Header.X + 16, layout.Header.Y + 11, Color.White, 16);
                Text(display, "×", layout.Close.X + 7, layout.Close.Y + 1, Color.White, 20);
                Text(
                    display,
                    "Click a category  •  Hover for details  •  Double-click a component to place it in Grasshopper",
                    layout.Panel.X + 24,
                    layout.Panel.Y + 49,
                    Color.FromArgb(55, 72, 88),
                    11);

                DrawConnections(display, layout);
                foreach (MapHit hit in layout.Categories)
                {
                    WorkflowCategory category = Categories.First(item => item.Name == hit.Label);
                    bool active = hit.Label == _expandedCategory;
                    bool hover = MapHit.Same(_hover, hit);
                    bool workflows = hit.Label == "Workflows";
                    Color fill = active
                        ? category.Color
                        : Color.FromArgb(hover ? 225 : 185, category.Color);
                    Color text = active ? Color.White : Color.FromArgb(26, 35, 45);
                    display.Draw2dRectangle(
                        hit.Bounds,
                        workflows ? Color.FromArgb(255, 167, 103, 0) : Color.FromArgb(220, category.Color),
                        workflows ? (active ? 4 : 3) : (active ? 3 : 1),
                        fill);
                    Text(
                        display,
                        workflows ? "◆  WORKFLOWS" : hit.Label,
                        hit.Bounds.X + (workflows ? 15 : 9),
                        hit.Bounds.Y + 13,
                        text,
                        workflows ? 14 : 13);
                }

                Text(
                    display,
                    _expandedCategory == "Workflows" ? "Reusable workflow assemblies" : _expandedCategory + " components",
                    layout.Panel.X + 24,
                    layout.Panel.Y + 273,
                    Color.FromArgb(32, 54, 74),
                    15);
                foreach (MapHit hit in layout.Components)
                {
                    if (hit.Kind == MapHitKind.SaveWorkflow ||
                        hit.Kind == MapHitKind.SetWorkflowFolder ||
                        hit.Kind == MapHitKind.RefreshWorkflows)
                    {
                        bool saveHover = MapHit.Same(_hover, hit);
                        bool save = hit.Kind == MapHitKind.SaveWorkflow;
                        display.Draw2dRectangle(
                            hit.Bounds,
                            save ? Color.FromArgb(255, 174, 105, 0) : Color.FromArgb(255, 47, 112, 151),
                            saveHover ? 3 : 2,
                            save
                                ? saveHover ? Color.FromArgb(255, 255, 211, 116) : Color.FromArgb(255, 255, 229, 169)
                                : saveHover ? Color.FromArgb(255, 198, 229, 247) : Color.FromArgb(255, 222, 240, 251));
                        Text(
                            display,
                            hit.Label,
                            hit.Bounds.X + (hit.Kind == MapHitKind.RefreshWorkflows ? 20 : 12),
                            hit.Bounds.Y + 7,
                            save ? Color.FromArgb(78, 48, 0) : Color.FromArgb(31, 74, 100),
                            12);
                        continue;
                    }
                    bool selected = hit.Guid == _selectedComponent;
                    bool hover = MapHit.Same(_hover, hit);
                    bool workflow = hit.Kind == MapHitKind.Workflow;
                    WorkflowDefinition definition = workflow
                        ? GetAllWorkflows().FirstOrDefault(item => item.Id == hit.Guid)
                        : null;
                    Color fill = workflow
                        ? definition != null && definition.IsUser
                            ? Color.FromArgb(255, 218, 244, 238)
                            : Color.FromArgb(255, 255, 235, 185)
                        : selected
                            ? Color.FromArgb(255, 255, 210, 122)
                            : hover
                                ? Color.FromArgb(255, 222, 237, 249)
                                : Color.FromArgb(242, 255, 255, 255);
                    Color outline = workflow ? Color.FromArgb(205, 151, 91, 0) : Color.FromArgb(115, 135, 151);
                    display.Draw2dRectangle(hit.Bounds, outline, selected || (workflow && hover) ? 2 : 1, fill);
                    if (workflow)
                    {
                        Text(
                            display,
                            definition != null && definition.IsUser ? "MY WORKFLOW" : "WASPER WORKFLOW",
                            hit.Bounds.X + 9,
                            hit.Bounds.Y + 5,
                            definition != null && definition.IsUser ? Color.FromArgb(24, 105, 88) : Color.FromArgb(145, 83, 0),
                            8);
                        Text(display, Trim(hit.Label, 31), hit.Bounds.X + 9, hit.Bounds.Y + 21, Color.FromArgb(30, 39, 49), 12);
                        DrawWorkflowGlyph(display, hit.Bounds, definition != null && definition.IsUser);
                    }
                    else
                    {
                        Text(display, Trim(hit.Label, 43), hit.Bounds.X + 7, hit.Bounds.Y + 4, Color.FromArgb(30, 39, 49), 11);
                    }
                }

                MapHit described = _hover != null &&
                                   (_hover.Kind == MapHitKind.Component ||
                                    _hover.Kind == MapHitKind.Workflow ||
                                    _hover.Kind == MapHitKind.SaveWorkflow ||
                                    _hover.Kind == MapHitKind.SetWorkflowFolder ||
                                    _hover.Kind == MapHitKind.RefreshWorkflows)
                    ? _hover
                    : layout.Components.FirstOrDefault(hit => hit.Guid == _selectedComponent);
                var footer = new Rectangle(layout.Panel.X + 20, layout.Panel.Bottom - 61, layout.Panel.Width - 40, 43);
                display.Draw2dRectangle(footer, Color.FromArgb(170, 139, 153), 1, Color.FromArgb(248, 248, 251, 255));
                string message = described == null
                    ? "Click a category to expand it. Click a component for details; double-click to place it in Grasshopper. Drag the blue header to move this map."
                    : described.Label + " — " + (described.Description ?? "Double-click to place this component in Grasshopper.");
                string[] lines = Wrap(message, 105, 2);
                for (int i = 0; i < lines.Length; i++)
                    Text(display, lines[i], footer.X + 9, footer.Y + 6 + i * 16, Color.FromArgb(42, 51, 61), 11);
            }

            private static void DrawWorkflowGlyph(DisplayPipeline display, Rectangle bounds, bool user)
            {
                Color color = user ? Color.FromArgb(125, 34, 139, 117) : Color.FromArgb(150, 170, 101, 0);
                int y = bounds.Y + bounds.Height / 2;
                int start = bounds.Right - 72;
                display.Draw2dLine(new Point(start + 8, y), new Point(start + 56, y), color, 2f);
                for (int i = 0; i < 3; i++)
                {
                    var node = new Rectangle(start + i * 24, y - 5, 11, 11);
                    display.Draw2dRectangle(node, color, 1, Color.FromArgb(245, Color.White));
                }
            }

            private static void DrawConnections(DisplayPipeline display, WorkflowMapLayout layout)
            {
                foreach (Tuple<string, string> connection in Connections)
                {
                    MapHit from = layout.Categories.First(hit => hit.Label == connection.Item1);
                    MapHit to = layout.Categories.First(hit => hit.Label == connection.Item2);
                    Point a = Center(from.Bounds);
                    Point b = Center(to.Bounds);
                    if (Math.Abs(b.X - a.X) > Math.Abs(b.Y - a.Y))
                    {
                        a.X = b.X > a.X ? from.Bounds.Right : from.Bounds.Left;
                        b.X = b.X > a.X ? to.Bounds.Left : to.Bounds.Right;
                    }
                    else
                    {
                        a.Y = b.Y > a.Y ? from.Bounds.Bottom : from.Bounds.Top;
                        b.Y = b.Y > a.Y ? to.Bounds.Top : to.Bounds.Bottom;
                    }
                    display.Draw2dLine(a, b, Color.FromArgb(110, 87, 104, 120), 2f);
                }
            }

            private static Point Center(Rectangle rectangle) =>
                new Point(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2);

            private static void Text(DisplayPipeline display, string text, int x, int y, Color color, int height)
            {
                display.Draw2dText(text ?? string.Empty, color, new Rhino.Geometry.Point2d(x, y), false, height, "Segoe UI");
            }
        }

        private sealed class WorkflowMapMouseCallback : MouseCallback
        {
            protected override void OnMouseMove(MouseCallbackEventArgs e) => MouseMove(e);
            protected override void OnMouseDown(MouseCallbackEventArgs e) => MouseDown(e);
            protected override void OnMouseUp(MouseCallbackEventArgs e) => MouseUp(e);
            protected override void OnMouseDoubleClick(MouseCallbackEventArgs e) => MouseDoubleClick(e);
        }

        private sealed class WorkflowCategory
        {
            internal WorkflowCategory(string name, string[] subcategories, int x, int y, Color color)
            {
                Name = name;
                Subcategories = subcategories;
                X = x;
                Y = y;
                Color = color;
            }
            internal string Name { get; }
            internal string[] Subcategories { get; }
            internal int X { get; }
            internal int Y { get; }
            internal Color Color { get; }
        }

        private enum MapHitKind
        {
            Panel,
            Header,
            Close,
            Category,
            Component,
            Workflow,
            SaveWorkflow,
            SetWorkflowFolder,
            RefreshWorkflows
        }

        private sealed class MapHit
        {
            internal MapHit(MapHitKind kind, Rectangle bounds, string label, Guid guid, string description = null)
            {
                Kind = kind;
                Bounds = bounds;
                Label = label;
                Guid = guid;
                Description = description;
            }
            internal MapHitKind Kind { get; }
            internal Rectangle Bounds { get; }
            internal string Label { get; }
            internal Guid Guid { get; }
            internal string Description { get; }
            internal static bool Same(MapHit a, MapHit b) =>
                ReferenceEquals(a, b) ||
                (a != null && b != null && a.Kind == b.Kind && a.Guid == b.Guid && a.Label == b.Label);
        }

        private sealed class WorkflowMapLayout
        {
            internal Rectangle Panel;
            internal Rectangle Header;
            internal Rectangle Close;
            internal readonly List<MapHit> Categories = new List<MapHit>();
            internal readonly List<MapHit> Components = new List<MapHit>();
        }

        internal sealed class WorkflowDefinition
        {
            internal WorkflowDefinition(
                Guid id,
                string name,
                string description,
                Guid[] componentGuids)
            {
                Id = id;
                Name = name;
                Description = description;
                ComponentGuids = componentGuids;
            }

            internal WorkflowDefinition(
                Guid id,
                string name,
                string description,
                string sourcePath)
            {
                Id = id;
                Name = name;
                Description = description;
                SourcePath = sourcePath;
            }
            internal Guid Id { get; }
            internal string Name { get; }
            internal string Description { get; }
            internal Guid[] ComponentGuids { get; }
            internal string SourcePath { get; }
            internal bool IsUser => !string.IsNullOrWhiteSpace(SourcePath);
        }

        private static string Trim(string text, int maximum)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maximum) return text ?? string.Empty;
            return text.Substring(0, Math.Max(1, maximum - 1)) + "…";
        }

        private static string[] Wrap(string text, int width, int maximumLines)
        {
            var result = new List<string>();
            var words = (text ?? string.Empty).Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string line = string.Empty;
            foreach (string word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (candidate.Length <= width)
                {
                    line = candidate;
                    continue;
                }
                if (line.Length > 0) result.Add(line);
                line = word;
                if (result.Count >= maximumLines - 1) break;
            }
            if (line.Length > 0 && result.Count < maximumLines) result.Add(line);
            if (result.Count == maximumLines && string.Join(" ", result).Length < text.Length)
                result[maximumLines - 1] = Trim(result[maximumLines - 1], Math.Max(4, width - 1));
            return result.ToArray();
        }
    }

    internal static class WASPerWorkflowAssembly
    {
        internal static bool Begin(
            WasperWorkflowMapManager.WorkflowDefinition workflow,
            out string error)
        {
            error = string.Empty;
            if (workflow == null || workflow.ComponentGuids == null ||
                workflow.ComponentGuids.Length == 0)
            {
                error = "The suggested workflow definition is empty.";
                return false;
            }

            var template = new GH_Document();
            var components = new List<GH_Component>();
            for (int i = 0; i < workflow.ComponentGuids.Length; i++)
            {
                if (!(Instances.ComponentServer.EmitObject(
                        workflow.ComponentGuids[i]) is GH_Component component))
                {
                    error = $"A component required by '{workflow.Name}' is unavailable.";
                    return false;
                }
                component.CreateAttributes();
                component.Attributes.Pivot = new PointF(220 + i * 230, 135);
                template.AddObject(component, false);
                components.Add(component);
            }

            for (int i = 1; i < components.Count; i++)
            {
                GH_Component previous = components[i - 1];
                GH_Component current = components[i];
                if (previous.Params.Output.Count == 0 ||
                    current.Params.Input.Count == 0)
                {
                    error = $"'{workflow.Name}' contains a component without the expected wsp_path socket.";
                    return false;
                }
                current.Params.Input[0].AddSource(previous.Params.Output[0]);
            }

            var panel = new GH_Panel
            {
                NickName = workflow.Name,
                UserText = workflow.Name + "\r\n\r\n" + workflow.Description +
                           "\r\n\r\nSuggested by WASPet. Review every input before fabrication."
            };
            panel.CreateAttributes();
            panel.Attributes.Pivot = new PointF(0, 40);
            template.AddObject(panel, false);

            var group = new GH_Group
            {
                Name = "WASPer suggested workflow — " + workflow.Name,
                NickName = "WASPer suggested workflow — " + workflow.Name,
                Colour = Color.FromArgb(255, 255, 213, 109)
            };
            template.AddObject(group, false);
            group.AddObject(panel.InstanceGuid);
            foreach (GH_Component component in components)
                group.AddObject(component.InstanceGuid);
            group.Attributes?.ExpireLayout();

            return WASPerExampleLibrary.BeginGeneratedPlacement(
                template,
                workflow.Name,
                out error);
        }
    }

    internal static class WASPerComponentPlacement
    {
        internal static bool Begin(Guid componentGuid, out string error)
        {
            error = string.Empty;
            GH_Canvas canvas = Instances.ActiveCanvas;
            if (canvas == null)
            {
                error = "There is no active Grasshopper canvas.";
                return false;
            }
            GH_Document document = canvas.Document;
            if (document == null)
            {
                document = new GH_Document();
                Instances.DocumentServer.AddDocument(document);
                canvas.Document = document;
            }
            IGH_DocumentObject obj = Instances.ComponentServer.EmitObject(componentGuid);
            if (obj == null)
            {
                error = "The selected component is unavailable in the current Grasshopper session.";
                return false;
            }

            obj.CreateAttributes();
            var initialEvent = new GH_CanvasMouseEvent(
                canvas.CursorControlPosition,
                canvas.CursorCanvasPosition,
                MouseButtons.None,
                0,
                0);
            canvas.ActiveInteraction = new ComponentPlacementInteraction(
                canvas,
                initialEvent,
                document,
                obj);
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

        private sealed class ComponentPlacementInteraction : GH_AbstractInteraction
        {
            private readonly GH_Document _document;
            private readonly IGH_DocumentObject _object;
            private PointF _anchor;
            private bool _finished;

            internal ComponentPlacementInteraction(
                GH_Canvas canvas,
                GH_CanvasMouseEvent initialEvent,
                GH_Document document,
                IGH_DocumentObject obj)
                : base(canvas, initialEvent, true)
            {
                _document = document;
                _object = obj;
                _anchor = canvas.CursorCanvasPosition;
                canvas.CanvasPostPaintWidgets += PaintPreview;
                canvas.Cursor = Cursors.Cross;
            }

            public override bool DeactivateOnFocusLoss => false;

            public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
            {
                _anchor = e.CanvasLocation;
                sender.Refresh();
                return GH_ObjectResponse.Handled;
            }

            public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
            {
                if (e.Button != MouseButtons.Left) return GH_ObjectResponse.Handled;
                _anchor = e.CanvasLocation;
                _object.Attributes.Pivot = _anchor;
                _document.AddObject(_object, false);
                _document.UndoUtil.RecordAddObjectEvent("Place WASPer workflow component", _object);
                _document.NewSolution(false);
                return GH_ObjectResponse.Release;
            }

            public override GH_ObjectResponse RespondToKeyDown(GH_Canvas sender, KeyEventArgs e) =>
                e.KeyCode == Keys.Escape ? GH_ObjectResponse.Release : GH_ObjectResponse.Handled;

            public override void Destroy()
            {
                if (!_finished)
                {
                    Canvas.CanvasPostPaintWidgets -= PaintPreview;
                    Canvas.ResetCursor();
                    Canvas.Refresh();
                    _finished = true;
                }
                base.Destroy();
            }

            private void PaintPreview(GH_Canvas canvas)
            {
                if (_finished || canvas.Graphics == null) return;
                var bounds = new RectangleF(_anchor.X - 55, _anchor.Y - 24, 110, 48);
                using var fill = new SolidBrush(Color.FromArgb(55, 255, 184, 35));
                using var outline = new Pen(Color.FromArgb(230, 214, 139, 0), 2f);
                canvas.Graphics.FillRectangle(fill, bounds);
                canvas.Graphics.DrawRectangle(outline, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
        }
    }
}
