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

using WASPer_3DP.Components._5_0_Gcode;


namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        private List<string> SaveIterationGcode(
            string sampleName,
            int iterationIndex,
            ICollection<string> warnings)
        {
            if (_study?.GcodeEnabled != true)
                return new List<string>();

            var written = new List<string>();
            List<List<string>> branches = (_currentGcodeBranches ?? new List<List<string>>())
                .Where(branch => branch != null && branch.Any(line => !string.IsNullOrWhiteSpace(line)))
                .ToList();
            if (branches.Count == 0)
                return written;

            try
            {
                string folder = Path.Combine(
                    ResolveStudyFolder(_study.RunName, _currentFilePath),
                    "Gcodes");
                Directory.CreateDirectory(folder);
                string safeSampleName = CleanSampleName(sampleName);
                if (string.IsNullOrWhiteSpace(safeSampleName))
                    safeSampleName = $"sample_{iterationIndex + 1:0000}";
                for (int branchIndex = 0; branchIndex < branches.Count; branchIndex++)
                {
                    string suffix = branches.Count > 1
                        ? $"_part_{branchIndex + 1:00}"
                        : string.Empty;
                    string path = Path.Combine(
                        folder,
                        $"{safeSampleName}{suffix}.gcode");
                    if (File.Exists(path))
                    {
                        path = Path.Combine(
                            folder,
                            $"{safeSampleName}_{iterationIndex + 1:0000}{suffix}.gcode");
                    }
                    File.WriteAllLines(path, branches[branchIndex], new UTF8Encoding(false));
                    written.Add(path);
                }
            }
            catch (Exception exception)
            {
                string warning = "G-code capture failed: " + exception.Message;
                warnings?.Add(warning);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
            }
            return written;
        }

        private List<string> SaveIterationSnapshots(
            IEnumerable<string> gcodeFiles,
            string sampleName,
            int iterationIndex,
            ICollection<string> warnings)
        {
            if (_study?.Snapshot?.Enabled != true)
                return new List<string>();
            try
            {
                if (_study.Snapshot.VisualizationComponentId != Guid.Empty)
                {
                    if (!TryGetLinkedVisualizationBounds(out BoundingBox bounds, out string warning))
                    {
                        warnings?.Add(warning);
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
                        return new List<string>();
                    }
                }
                string studyFolder = string.IsNullOrWhiteSpace(_studyFolder)
                    ? ResolveStudyFolder(_study.RunName, _currentFilePath)
                    : _studyFolder;
                string folder = Path.Combine(studyFolder, "Snapshots");
                Directory.CreateDirectory(folder);
                string safeSampleName = CleanSampleName(sampleName);
                if (string.IsNullOrWhiteSpace(safeSampleName))
                    safeSampleName = $"sample_{iterationIndex + 1:0000}";
                List<string> baseNames = (gcodeFiles ?? Enumerable.Empty<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (baseNames.Count == 0)
                    baseNames.Add(safeSampleName);
                var targets = new List<string>();
                foreach (string baseName in baseNames)
                {
                    string targetPath = Path.Combine(folder, baseName + ".png");
                    if (File.Exists(targetPath))
                    {
                        targetPath = Path.Combine(
                            folder,
                            $"{baseName}_{iterationIndex + 1:0000}.png");
                    }
                    targets.Add(targetPath);
                }
                List<string> saved = WasperViewportCapture.SaveToSnapshotFiles(
                    targets,
                    _study.Snapshot,
                    null);
                if (saved.Count == 0)
                    throw new IOException("Rhino did not produce a viewport image.");
                return saved;
            }
            catch (Exception exception)
            {
                string warning = "Viewport snapshot failed: " + exception.Message;
                warnings?.Add(warning);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
                return new List<string>();
            }
        }

        // Captures the just-solved iteration's full wsp_path as a .wasperxr
        // package, immediately, the same way GcodeFiles/SnapshotFiles are
        // captured -- never holds more than one WasperPrintPath at a time in
        // memory. Only called when the Run Study dialog's "wsp_paths" option
        // was checked (WasperStudy.XrPathsEnabled); heavy relative to the
        // other two (a full binary print-path package per iteration), which
        // is why it defaults off and shows a size warning in that dialog.
        // Reuses the exact same export path a live "Export/Update" click
        // already goes through (wsp_Gc07_Export_XR_Package.TryExportPackage,
        // _currentSet for the same merged/enabled KPI set), so per-iteration
        // dumps and the single-job Process Viewer export land in the same
        // study\XR folder and are indistinguishable once written.
        private List<string> SaveIterationXrPackage(
            WasperPrintPath path,
            string sampleName,
            int iterationIndex,
            ICollection<string> warnings)
        {
            var written = new List<string>();
            if (path == null || !path.HasPoints || !path.HasMotionPlan)
                return written;

            try
            {
                string folder = Path.Combine(
                    ResolveStudyFolder(_study.RunName, _currentFilePath),
                    "XR");
                Directory.CreateDirectory(folder);
                string safeSampleName = CleanSampleName(sampleName);
                if (string.IsNullOrWhiteSpace(safeSampleName))
                    safeSampleName = $"sample_{iterationIndex + 1:0000}";
                string jobId = safeSampleName;
                if (File.Exists(Path.Combine(folder, jobId + WasperXrBinaryPackage.Extension)))
                    jobId = $"{safeSampleName}_{iterationIndex + 1:0000}";

                if (!wsp_Gc07_Export_XR_Package.TryExportPackage(
                    path,
                    1.0,
                    folder,
                    jobId,
                    1,
                    _version,
                    out string finalPath,
                    out _,
                    out _,
                    out string error,
                    _currentSet,
                    false,
                    1.0,
                    _currentXrScenePack))
                {
                    string warning = "XR package capture failed: " + error;
                    warnings?.Add(warning);
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
                    return written;
                }
                written.Add(finalPath);
            }
            catch (Exception exception)
            {
                string warning = "XR package capture failed: " + exception.Message;
                warnings?.Add(warning);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
            }
            return written;
        }

        private bool TryGetLinkedVisualizationBounds(
            out BoundingBox bounds,
            out string warning)
        {
            bounds = BoundingBox.Empty;
            warning = string.Empty;
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            GH_Component component = document?.FindObject(
                _study.Snapshot.VisualizationComponentId,
                true) as GH_Component;
            if (component == null)
            {
                warning = "Viewport snapshot skipped: the linked visualization component " +
                    "is not available in this Grasshopper document.";
                return false;
            }

            bool hasData = false;
            foreach (IGH_Param parameter in component.Params.Input.Concat(component.Params.Output))
            {
                foreach (IGH_Goo goo in parameter.VolatileData.AllData(true))
                {
                    hasData = true;
                    try
                    {
                        IncludeGeometryBounds(goo?.ScriptVariable(), ref bounds);
                    }
                    catch
                    {
                        // A visualization may expose custom preview data that cannot be cast to
                        // Rhino geometry. Its presence still satisfies the readiness check.
                    }
                }
            }
            if (!hasData)
            {
                warning = $"Viewport snapshot skipped: linked component '{component.NickName}' " +
                    "does not currently contain output or input data.";
                return false;
            }
            return true;
        }

        private static void IncludeGeometryBounds(object value, ref BoundingBox bounds)
        {
            BoundingBox candidate;
            switch (value)
            {
                case GeometryBase geometry when geometry.IsValid:
                    candidate = geometry.GetBoundingBox(true);
                    break;
                case Point3d point when point.IsValid:
                    candidate = new BoundingBox(point, point);
                    break;
                case BoundingBox box when box.IsValid:
                    candidate = box;
                    break;
                case Box box when box.IsValid:
                    candidate = box.BoundingBox;
                    break;
                case Rectangle3d rectangle when rectangle.IsValid:
                    candidate = rectangle.BoundingBox;
                    break;
                default:
                    return;
            }
            if (!candidate.IsValid)
                return;
            if (bounds.IsValid)
                bounds.Union(candidate);
            else
                bounds = candidate;
        }

    }
}
