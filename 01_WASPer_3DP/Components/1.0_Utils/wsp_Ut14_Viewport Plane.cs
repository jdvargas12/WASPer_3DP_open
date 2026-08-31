#region Component Description
/*
    Component Name:
        wsp_Ut14_Viewport Plane

    Nickname:
        View Plane

    Version:
        Assembly-derived (vMajor.Minor.Build)

    Category / Subcategory:
        WASPer_3DP / 1.0_Utils

    Description:
        Creates a plane aligned to the active or a named Rhino viewport.

    Inputs:
        origin        : optional plane origin; camera target when omitted
        viewport_name : optional Rhino viewport name; active viewport when empty
        update        : inert trigger input for a Button or Grasshopper Timer

    Outputs:
        plane     : camera-aligned plane
        view_name : resolved Rhino viewport name
        info      : selection and orientation details
*/
#endregion

using System;
using System.Drawing;
using System.Linq;
using System.Reflection;

using Grasshopper.Kernel;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace WASPer_3DP.Components._1_0_Utils
{
    public sealed class wsp_Ut14_Viewport_Plane : GH_Component
    {
        private const string NAME = "wsp_Ut14_Viewport Plane";
        private const string NICK = "View Plane";
        private const string CAT = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "1.0_Utils";

        private readonly string _versionTag;

        public wsp_Ut14_Viewport_Plane()
            : base(
                NAME,
                NICK,
                "Creates a plane whose X and Y axes follow the horizontal and vertical axes of the active or a named Rhino viewport.",
                CAT,
                SUBCAT)
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("AEBA9F5A-8182-4F60-9ED5-21E7D58A0A1B");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut14_Viewport Plane.png"))
                        return stream != null ? new Bitmap(stream) : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddPointParameter(
                "origin", "origin",
                "Optional plane origin. When omitted, the selected viewport's camera target is used.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "viewport_name", "view",
                "Rhino viewport name, matched without case sensitivity. Empty uses the active viewport.",
                GH_ParamAccess.item, string.Empty);

            p.AddBooleanParameter(
                "update", "update",
                "Inert recompute trigger. Connect a Button or Grasshopper Timer to refresh the plane after camera changes.",
                GH_ParamAccess.item, false);

            p[0].Optional = true;
            p[1].Optional = true;
            p[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddPlaneParameter(
                "plane", "plane",
                "Plane aligned to the selected viewport: X is screen-right, Y is screen-up, and Z points toward the camera.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "view_name", "view",
                "Resolved Rhino viewport name.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "info", "info",
                "Viewport selection, origin source, and orientation details.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Point3d origin = Point3d.Unset;
            string requestedName = string.Empty;
            bool update = false;

            bool hasOrigin = DA.GetData(0, ref origin) && origin.IsValid;
            DA.GetData(1, ref requestedName);
            DA.GetData(2, ref update); // Intentionally read so Buttons and Timers can expire the component.

            RhinoDoc document = RhinoDoc.ActiveDoc;
            if (document == null)
            {
                SetError(DA, "No active Rhino document.");
                return;
            }

            RhinoView view = ResolveView(document, requestedName);
            if (view == null)
            {
                string available = string.Join(", ", document.Views
                    .Select(v => v?.ActiveViewport?.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase));

                SetError(
                    DA,
                    $"Rhino viewport '{requestedName}' was not found." +
                    (available.Length > 0 ? $" Available viewports: {available}." : string.Empty));
                return;
            }

            RhinoViewport viewport = view.ActiveViewport;
            if (viewport == null || !viewport.IsValidCamera)
            {
                SetError(DA, "The selected viewport does not have a valid camera.");
                return;
            }

            Point3d planeOrigin = hasOrigin ? origin : viewport.CameraTarget;
            Vector3d xAxis = viewport.CameraX;
            Vector3d yAxis = viewport.CameraY;

            if (!xAxis.Unitize() || !yAxis.Unitize())
            {
                SetError(DA, "The selected viewport returned invalid camera axes.");
                return;
            }

            Plane plane = new Plane(planeOrigin, xAxis, yAxis);
            if (!plane.IsValid)
            {
                SetError(DA, "Could not construct a valid plane from the viewport camera axes.");
                return;
            }

            string viewName = viewport.Name ?? string.Empty;
            string source = string.IsNullOrWhiteSpace(requestedName) ? "active viewport" : "named viewport";
            string originSource = hasOrigin ? "input origin" : "camera target";

            Message = $"{_versionTag} | {viewName}";
            DA.SetData(0, plane);
            DA.SetData(1, viewName);
            DA.SetData(2,
                $"OK | {source}: {viewName} | origin: {originSource} | " +
                "X = camera X, Y = camera Y, Z = camera Z (toward camera)");
        }

        private static RhinoView ResolveView(RhinoDoc document, string requestedName)
        {
            if (document == null)
                return null;

            if (string.IsNullOrWhiteSpace(requestedName))
                return document.Views.ActiveView;

            string target = requestedName.Trim();
            foreach (RhinoView view in document.Views)
            {
                if (view?.ActiveViewport?.Name != null &&
                    view.ActiveViewport.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                    return view;

                if (view?.MainViewport?.Name != null &&
                    view.MainViewport.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                    return view;
            }

            return null;
        }

        private void SetError(IGH_DataAccess DA, string message)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, message);
            Message = $"{_versionTag} | error";
            DA.SetData(2, message);
        }
    }
}
