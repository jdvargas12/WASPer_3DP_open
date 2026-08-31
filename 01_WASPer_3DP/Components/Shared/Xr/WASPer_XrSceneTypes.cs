using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using Grasshopper.Kernel.Types;

using Rhino.Geometry;

namespace WASPer_3DP
{
    /// <summary>
    /// Bundled by Sm05 (wsp_Sm05_XR Scene Params, Components/1.2_Studies) and consumed by Sm01's
    /// "xr_pack" input -- extra scene data that lives alongside a wsp_path export but isn't part
    /// of the printed path itself, so Sm01 can build a complete XR export/live-push package on
    /// its own without a Gc07 component wired in just to carry these extras (2026-08-19).
    /// </summary>
    public sealed class WasperXrScenePack
    {
        // Original Grasshopper values are retained for component inspection; ContextMeshes is the
        // viewer-ready representation serialized by Gc07/Sm01.
        public List<GeometryBase> ContextGeometry { get; set; } = new List<GeometryBase>();
        public List<IGH_Goo> Materials { get; set; } = new List<IGH_Goo>();
        internal List<WasperXrContextMeshData> ContextMeshes { get; set; } =
            new List<WasperXrContextMeshData>();

        // Whether *anything* is wired into Sm05's sim_par input -- not just whether the value is
        // non-default. Sm01 uses this (not the value itself) to decide whether to suppress the
        // web viewer's own playback controls: a connected sim_par means an external source
        // (typically Gc05) already owns the simulated print position, so the browser's local
        // Play/Stop/time-slider would just be a second, conflicting clock.
        public bool SimulationParameterConnected { get; set; }
        public double SimulationParameter { get; set; } = 1.0;
    }

    /// <summary>Grasshopper wire wrapper for <see cref="WasperXrScenePack" />.</summary>
    public sealed class WasperXrScenePackGoo : GH_Goo<WasperXrScenePack>
    {
        public WasperXrScenePackGoo()
        {
        }

        public WasperXrScenePackGoo(WasperXrScenePack value)
        {
            Value = value;
        }

        public override bool IsValid => Value != null;

        public override string IsValidWhyNot =>
            IsValid ? string.Empty : "No WASPer XR Scene Params were set.";

        public override string TypeName => "WASPer XR Scene Params";

        public override string TypeDescription =>
            "Context geometry, materials, and an optional externally-driven simulation " +
            "parameter for XR export, bundled by Sm05.";

        public override IGH_Goo Duplicate() => new WasperXrScenePackGoo(Value);

        public override string ToString()
        {
            if (Value == null)
                return "WASPer XR Scene Params (empty)";
            string simPar = Value.SimulationParameterConnected
                ? $"sim_par {Value.SimulationParameter:0.00}"
                : "sim_par not connected";
            return $"WASPer XR Scene Params ({Value.ContextGeometry.Count} geo, " +
                $"{Value.Materials.Count} material(s), {simPar})";
        }
    }

    internal sealed class WasperXrContextMeshData
    {
        public string Id { get; set; } = string.Empty;
        public Mesh Mesh { get; set; }
        public Color Color { get; set; } = Color.FromArgb(255, 170, 174, 182);
    }

    internal static class WasperXrContextMeshBuilder
    {
        private static readonly Color DefaultColor = Color.FromArgb(255, 170, 174, 182);

        internal static string ComputeSignature(
            IReadOnlyList<GeometryBase> geometry,
            IReadOnlyList<IGH_Goo> materials)
        {
            WasperCacheSignature signature = WasperCacheSignature.Create();
            signature.Add(geometry?.Count ?? 0);
            if (geometry == null)
                return signature.Finish();

            for (int geometryIndex = 0; geometryIndex < geometry.Count; geometryIndex++)
            {
                signature.Add(geometry[geometryIndex]);
                Color color = ResolveColor(materials, geometryIndex, geometry[geometryIndex]);
                signature.Add(color.ToArgb());
            }
            return signature.Finish();
        }

        internal static List<WasperXrContextMeshData> Build(
            IReadOnlyList<GeometryBase> geometry,
            IReadOnlyList<IGH_Goo> materials,
            out List<string> warnings)
        {
            warnings = new List<string>();
            var result = new List<WasperXrContextMeshData>();
            for (int geometryIndex = 0; geometryIndex < geometry.Count; geometryIndex++)
            {
                List<Mesh> meshes = CreateMeshes(geometry[geometryIndex]);
                if (meshes.Count == 0)
                {
                    warnings.Add($"Context object {geometryIndex + 1} ({geometry[geometryIndex].ObjectType}) " +
                        "could not be converted to a display mesh and was skipped.");
                    continue;
                }

                Color color = ResolveColor(materials, geometryIndex, geometry[geometryIndex]);
                for (int partIndex = 0; partIndex < meshes.Count; partIndex++)
                {
                    Mesh mesh = meshes[partIndex];
                    mesh.Faces.ConvertQuadsToTriangles();
                    if (mesh.Normals.Count != mesh.Vertices.Count)
                        mesh.Normals.ComputeNormals();
                    mesh.Compact();
                    result.Add(new WasperXrContextMeshData
                    {
                        Id = $"context-{geometryIndex}-{partIndex}",
                        Mesh = mesh,
                        Color = color
                    });
                }
            }
            return result;
        }

        private static List<Mesh> CreateMeshes(GeometryBase geometry)
        {
            if (geometry is Mesh mesh)
                return new List<Mesh> { mesh.DuplicateMesh() };

            Brep brep = geometry switch
            {
                Brep value => value,
                Extrusion value => value.ToBrep(),
                Surface value => value.ToBrep(),
                _ => null
            };
            if (brep == null)
                return new List<Mesh>();

            return (Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>())
                .Where(value => value != null && value.IsValid && value.Vertices.Count > 0)
                .ToList();
        }

        private static Color ResolveColor(
            IReadOnlyList<IGH_Goo> materials,
            int geometryIndex,
            GeometryBase geometry)
        {
            if (materials == null || materials.Count == 0)
                return TryResolveMeshVertexColor(geometry, out Color meshColor)
                    ? meshColor
                    : DefaultColor;
            IGH_Goo goo = materials.Count == 1
                ? materials[0]
                : materials[Math.Min(geometryIndex, materials.Count - 1)];
            if (TryResolveColor(goo, out Color color))
                return color;
            return TryResolveMeshVertexColor(geometry, out Color fallbackColor)
                ? fallbackColor
                : DefaultColor;
        }

        private static bool TryResolveMeshVertexColor(GeometryBase geometry, out Color color)
        {
            color = DefaultColor;
            if (geometry is not Mesh mesh ||
                mesh.VertexColors.Count == 0 ||
                mesh.VertexColors.Count != mesh.Vertices.Count)
                return false;

            long alpha = 0;
            long red = 0;
            long green = 0;
            long blue = 0;
            for (int index = 0; index < mesh.VertexColors.Count; index++)
            {
                Color vertexColor = mesh.VertexColors[index];
                alpha += vertexColor.A;
                red += vertexColor.R;
                green += vertexColor.G;
                blue += vertexColor.B;
            }
            int count = mesh.VertexColors.Count;
            color = Color.FromArgb(
                (int)(alpha / count),
                (int)(red / count),
                (int)(green / count),
                (int)(blue / count));
            return true;
        }

        private static bool TryResolveColor(IGH_Goo goo, out Color color)
        {
            if (goo is GH_Colour ghColor)
            {
                color = ghColor.Value;
                return true;
            }

            object value = goo?.ScriptVariable();
            if (value is Color directColor)
            {
                color = directColor;
                return true;
            }
            if (value is Rhino.DocObjects.Material material)
            {
                int alpha = (int)Math.Round(255.0 * (1.0 - Math.Max(0.0, Math.Min(1.0, material.Transparency))));
                Color diffuse = material.DiffuseColor;
                color = Color.FromArgb(alpha, diffuse.R, diffuse.G, diffuse.B);
                return true;
            }

            color = DefaultColor;
            return false;
        }
    }
}
