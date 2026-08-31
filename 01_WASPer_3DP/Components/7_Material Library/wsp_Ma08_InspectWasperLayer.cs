using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Display;
using Rhino.Geometry;
using WASPer_3DP;

public sealed class wsp_Ma08_InspectWasperLayer : GH_Component
{
    private static readonly Color[] EarthPalette =
    {
        Color.FromArgb(164, 98, 62), Color.FromArgb(188, 121, 73),
        Color.FromArgb(137, 77, 54), Color.FromArgb(198, 143, 96)
    };

    private static readonly Color[] ConcretePalette =
    {
        Color.FromArgb(145, 145, 140), Color.FromArgb(166, 166, 160),
        Color.FromArgb(116, 116, 112), Color.FromArgb(190, 190, 185)
    };

    private static readonly Color[] WoodPalette =
    {
        Color.FromArgb(145, 91, 55), Color.FromArgb(174, 116, 66),
        Color.FromArgb(121, 70, 43), Color.FromArgb(196, 143, 87)
    };

    private static readonly Color[] PolymerPalette =
    {
        Color.FromArgb(74, 127, 171), Color.FromArgb(91, 145, 188),
        Color.FromArgb(55, 103, 151), Color.FromArgb(117, 163, 198)
    };

    private static readonly Color[] NeutralPalette =
    {
        Color.FromArgb(120, 144, 156), Color.FromArgb(102, 125, 139),
        Color.FromArgb(128, 128, 128), Color.FromArgb(111, 134, 145)
    };

    private static readonly Color GasColor = Color.FromArgb(242, 246, 248);

    private static readonly Color ExteriorTint = Color.FromArgb(28, 66, 165, 245);
    private static readonly Color InteriorTint = Color.FromArgb(28, 255, 167, 38);

    private readonly string _versionTag;
    private Mesh _mesh;
    private DisplayMaterial _material;

    public wsp_Ma08_InspectWasperLayer()
        : base("wsp_Ma08_Inspect WASPer Layer", "Inspect Layer",
            "Inspects a stack of WASPer Layers from Ma07. Draws a to-scale layer-section diagram (exterior on the left, " +
            "interior on the right), passes each layer's underlying WASPer material through for reuse in Ma05, lists the " +
            "layer thicknesses, and reports whether porosity or a λ_eff override changed any layer's resolved properties. " +
            "Material colors are deterministic: repeated instances of the same material share one color, while gases, earths, concretes, wood, and polymers use distinct realistic palettes. If diag_rect is omitted, the diagram is previewed on the World XY plane at the origin. Wire the full layer list from Ma07 into wasper_layer.",
            global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        _versionTag = version == null ? "v1.0.x" : $"v{version.Major}.{version.Minor}.{version.Build}";
        Message = _versionTag;
    }

    public override Guid ComponentGuid => new("9C1F5A3E-4B2D-4E7A-9F6C-2D8A5B3E7C10");

    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ma08_Inspect WASPer Layer.png");
                return stream == null ? null : new System.Drawing.Bitmap(stream);
            }
            catch { return null; }
        }
    }

    // ── inputs ────────────────────────────────────────────────────────────
    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("wasper_layer", "wasper_layer",
            "WASPer Layer objects produced by Ma07, in exterior → interior order. Wire the whole layer list to inspect the complete stack in one diagram.",
            GH_ParamAccess.list);
        p.AddRectangleParameter("diag_rect", "diag_rect",
            "Optional planar rectangle for an aspect-preserving preview of the layer-section diagram in the Rhino viewport. The PNG is written regardless; this only controls the on-canvas preview (same behaviour as chart_rect in Da01–Da04).",
            GH_ParamAccess.item);
        p[1].Optional = true;
    }

    // ── outputs ───────────────────────────────────────────────────────────
    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGenericParameter("wsp_mat", "wsp_mat",
            "The underlying WASPer material of each layer, in exterior → interior order. Connect to Ma05 to inspect a material, or reuse in other material-aware components.",
            GH_ParamAccess.list);
        p.AddNumberParameter("thickness", "thickness",
            "Thickness of each layer in metres, in exterior → interior order (aligned index-for-index with wsp_mat).",
            GH_ParamAccess.list);
        p.AddTextParameter("diagram", "diagram",
            "Absolute path to the generated layer-section PNG (exterior on the left, interior on the right, bands drawn to scale).",
            GH_ParamAccess.item);
        p.AddTextParameter("summary", "summary",
            "Per-layer report of whether the resolved λ came from a λ_eff override or the base material, and whether porosity changed the resolved density / specific heat / volumetric heat capacity, followed by stack totals.",
            GH_ParamAccess.item);
    }

    // ── solve ─────────────────────────────────────────────────────────────
    protected override void SolveInstance(IGH_DataAccess da)
    {
        ResetPreview();

        var raw = new List<object>();
        if (!da.GetDataList(0, raw) || raw.Count == 0)
        {
            Message = _versionTag;
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Connect one or more wasper_layer objects from Ma07.");
            return;
        }

        var layers = new List<WasperLayer>();
        for (int i = 0; i < raw.Count; i++)
        {
            WasperLayer layer = Extract(raw[i]);
            if (layer == null || layer.Material == null)
            {
                Message = _versionTag;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Input wasper_layer[{i}] is not a valid WASPer Layer. Connect the wasper_layer output from Ma07.");
                return;
            }
            layers.Add(layer);
        }

        var mats = layers.Select(l => new WasperMaterialGoo(l.Material)).ToList();
        var thicknesses = layers.Select(l => l.Thickness_m).ToList();
        string summary = BuildSummary(layers);

        string path;
        int pxW = 1600, pxH = 700;
        try
        {
            using Bitmap bmp = RenderSection(pxW, pxH, layers);
            bmp.SetResolution(150, 150);
            string dir = DiagramDirectory();
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "layer_section.png");
            bmp.Save(path, ImageFormat.Png);
        }
        catch (Exception ex)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Diagram render failed: " + ex.Message);
            da.SetDataList(0, mats);
            da.SetDataList(1, thicknesses);
            da.SetData(3, summary);
            Message = $"{_versionTag} | render error";
            return;
        }

        Rectangle3d rect = Rectangle3d.Unset;
        bool hasRect = da.GetData(1, ref rect) && rect.IsValid;
        if (!hasRect)
        {
            rect = DefaultOriginRectangle(pxW / (double)pxH);
            hasRect = true;
        }

        if (hasRect && !Preview(rect, pxW / (double)pxH, path))
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Rectangle preview failed; the diagram PNG was still saved.");

        da.SetDataList(0, mats);
        da.SetDataList(1, thicknesses);
        da.SetData(2, path);
        da.SetData(3, summary);
        Message = $"{_versionTag} | {layers.Count} layer{(layers.Count == 1 ? "" : "s")}";
    }

    // ── summary ───────────────────────────────────────────────────────────
    private static string BuildSummary(List<WasperLayer> layers)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "WASPer Layer Stack — {0} layer{1} (exterior → interior)", layers.Count, layers.Count == 1 ? "" : "s"));
        sb.AppendLine();

        int lambdaOverrides = 0, storageOverrides = 0;
        double totalThickness = 0;

        foreach (WasperLayer l in layers)
        {
            totalThickness += l.Thickness_m;
            l.TryGetDouble("lambda", out double lambda);

            string lambdaNote = l.HasLambdaEffOverride
                ? string.Format(CultureInfo.InvariantCulture, "λ = {0:0.####} W/(m·K) [λ_eff override]", lambda)
                : string.Format(CultureInfo.InvariantCulture, "λ = {0:0.####} W/(m·K) [material]", lambda);

            string storageNote;
            if (l.Porosity <= 0.0)
                storageNote = "φ = 0 → storage unaffected";
            else if (l.HasStorageOverride)
                storageNote = string.Format(CultureInfo.InvariantCulture,
                    "φ = {0:0.###} → porosity-adjusted ρ/cp/ρc (ρ_eff = {1:0.#} kg/m³, cp_eff = {2:0.#} J/(kg·K), ρc_eff = {3:0.#} J/(m³·K))",
                    l.Porosity, l.RhoEff_kg_m3, l.CpEff_J_kgK, l.RhoCEff_J_m3K);
            else
                storageNote = string.Format(CultureInfo.InvariantCulture,
                    "φ = {0:0.###} set, but material lacked ρ and/or cp → storage fell back to material", l.Porosity);

            if (l.HasLambdaEffOverride) lambdaOverrides++;
            if (l.HasStorageOverride) storageOverrides++;

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "[{0}] {1} — d = {2:0.###} m ({3:0.#} mm)", l.Index, l.MaterialName, l.Thickness_m, l.Thickness_m * 1000.0));
            sb.AppendLine("      " + lambdaNote);
            sb.AppendLine("      " + storageNote);
        }

        sb.AppendLine();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Total thickness: {0:0.###} m ({1:0.#} mm)", totalThickness, totalThickness * 1000.0));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "λ_eff overrides applied on {0} of {1} layer{2}.", lambdaOverrides, layers.Count, layers.Count == 1 ? "" : "s"));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "Porosity changed ρ/cp/ρc on {0} of {1} layer{2}.", storageOverrides, layers.Count, layers.Count == 1 ? "" : "s"));

        return sb.ToString();
    }

    // ── section diagram ───────────────────────────────────────────────────
    private Bitmap RenderSection(int w, int h, List<WasperLayer> layers)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.White);

        using var titleFont = new Font(FontFamily.GenericSansSerif, 26, FontStyle.Bold, GraphicsUnit.Pixel);
        using var gutterFont = new Font(FontFamily.GenericSansSerif, 22, FontStyle.Bold, GraphicsUnit.Pixel);
        using var nameFont = new Font(FontFamily.GenericSansSerif, 21, FontStyle.Bold, GraphicsUnit.Pixel);
        using var dimFont = new Font(FontFamily.GenericSansSerif, 18, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(40, 40, 40));
        using var faint = new SolidBrush(Color.FromArgb(120, 120, 120));
        using var edge = new Pen(Color.FromArgb(55, 55, 55), 1.6f);
        using var frame = new Pen(Color.FromArgb(30, 30, 30), 2.2f);

        float gutter = 118f;
        float top = 64f;
        float bottom = 54f;
        var plot = RectangleF.FromLTRB(gutter, top, w - gutter, h - bottom);

        // Environment tints on each side of the assembly.
        using (var extB = new SolidBrush(ExteriorTint)) g.FillRectangle(extB, 0, plot.Top, gutter, plot.Height);
        using (var intB = new SolidBrush(InteriorTint)) g.FillRectangle(intB, w - gutter, plot.Top, gutter, plot.Height);

        // Title.
        g.DrawString("Layer Section", titleFont, ink,
            new RectangleF(0, 14, w, titleFont.GetHeight(g)),
            new StringFormat { Alignment = StringAlignment.Center });

        // Side labels (exterior left, interior right), rotated to read upward.
        DrawRotatedLabel(g, "EXTERIOR", gutterFont, faint, new PointF(gutter / 2f, plot.Top + plot.Height / 2f), plot.Height, gutter);
        DrawRotatedLabel(g, "INTERIOR", gutterFont, faint, new PointF(w - gutter / 2f, plot.Top + plot.Height / 2f), plot.Height, gutter);

        int n = layers.Count;
        double totalT = layers.Sum(l => l.Thickness_m > 0 && !double.IsNaN(l.Thickness_m) ? l.Thickness_m : 0.0);

        // To-scale widths with a minimum band so thin layers stay legible.
        float minW = Math.Min(48f, plot.Width / n);
        float free = plot.Width - n * minW;
        if (free < 0) { minW = plot.Width / n; free = 0; }

        float x = plot.Left;
        for (int i = 0; i < n; i++)
        {
            WasperLayer l = layers[i];
            double share = totalT > 0 ? l.Thickness_m / totalT : 1.0 / n;
            float bw = minW + free * (float)share;
            if (i == n - 1) bw = plot.Right - x; // absorb rounding into the last band

            var band = new RectangleF(x, plot.Top, bw, plot.Height);
            Color fillColor = ColorForMaterial(l.Material);
            using (var fill = new SolidBrush(fillColor))
                g.FillRectangle(fill, band);
            g.DrawRectangle(edge, band.X, band.Y, band.Width, band.Height);

            string name = l.MaterialName ?? "layer";
            string dim = string.Format(CultureInfo.InvariantCulture, "{0:0.#} mm", l.Thickness_m * 1000.0);
            string label = name + "\n" + dim;
            DrawRotatedLabel(g, label, nameFont, TextBrushFor(fillColor), new PointF(band.X + band.Width / 2f, band.Y + band.Height / 2f), band.Height, band.Width, dimFont);

            x += bw;
        }

        g.DrawRectangle(frame, plot.X, plot.Y, plot.Width, plot.Height);

        // Direction cue under the assembly.
        g.DrawString("exterior  →  interior", dimFont, faint,
            new RectangleF(plot.Left, plot.Bottom + 16, plot.Width, dimFont.GetHeight(g)),
            new StringFormat { Alignment = StringAlignment.Center });

        return bmp;
    }

    private static Color ColorForMaterial(WasperMaterial material)
    {
        if (material == null) return NeutralPalette[0];

        string phase = material.Phase ?? string.Empty;
        if (phase.IndexOf("gas", StringComparison.OrdinalIgnoreCase) >= 0 ||
            phase.IndexOf("air", StringComparison.OrdinalIgnoreCase) >= 0)
            return GasColor;

        string category = GetProperty(material, "Category");
        string name = material.Name ?? string.Empty;
        string text = (category + " " + name).ToLowerInvariant();

        Color[] palette = text.Contains("earth") || text.Contains("clay") || text.Contains("soil") || text.Contains("terracotta")
            ? EarthPalette
            : text.Contains("concrete") || text.Contains("cement") || text.Contains("mortar")
                ? ConcretePalette
                : text.Contains("wood") || text.Contains("timber") || text.Contains("cork")
                    ? WoodPalette
                    : text.Contains("polymer") || text.Contains("plastic") || text.Contains("fdm")
                        ? PolymerPalette
                        : NeutralPalette;

        return palette[StableHash(material, category) % palette.Length];
    }

    private static Brush TextBrushFor(Color color)
    {
        double luminance = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
        return luminance > 175.0 ? Brushes.Black : Brushes.White;
    }

    private static string GetProperty(WasperMaterial material, string key)
    {
        return material.TryGet(key, out string value) ? value ?? string.Empty : string.Empty;
    }

    private static int StableHash(WasperMaterial material, string category)
    {
        string key = $"{material.Phase}|{category}|{material.Name}";
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in key)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)(hash & 0x7fffffff);
        }
    }

    // Draws vertically-stacked, centred, -90° rotated text inside a band.
    private static void DrawRotatedLabel(Graphics g, string text, Font font, Brush brush, PointF center, float alongLen, float acrossLen, Font secondFont = null)
    {
        GraphicsState st = g.Save();
        g.TranslateTransform(center.X, center.Y);
        g.RotateTransform(-90);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        // After rotation the band's height maps to the drawing x-extent and its width to the y-extent.
        var rect = new RectangleF(-alongLen / 2f, -acrossLen / 2f, alongLen, acrossLen);

        if (secondFont != null && text.Contains("\n"))
        {
            string[] parts = text.Split('\n');
            float h1 = font.GetHeight(g), h2 = secondFont.GetHeight(g);
            var top = new RectangleF(-alongLen / 2f, -acrossLen / 2f + (acrossLen - h1 - h2) / 2f, alongLen, h1);
            var bot = new RectangleF(-alongLen / 2f, top.Bottom, alongLen, h2);
            g.DrawString(parts[0], font, brush, top, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter });
            g.DrawString(parts[1], secondFont, brush, bot, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
        else
        {
            g.DrawString(text, font, brush, rect, fmt);
        }

        g.Restore(st);
    }

    // ── viewport preview (mirrors Da01–Da04 chart_rect behaviour) ─────────
    private bool Preview(Rectangle3d r, double aspect, string path)
    {
        double aw = r.Width, ah = r.Height, w = aw, h = w / aspect;
        if (h > ah) { h = ah; w = h * aspect; }

        Plane p = r.Plane;
        p.Origin = r.Center;
        var q = new Rectangle3d(p, new Interval(-w / 2, w / 2), new Interval(-h / 2, h / 2));

        var m = new Mesh();
        for (int i = 0; i < 4; i++) m.Vertices.Add(q.Corner(i));
        m.Faces.AddFace(0, 1, 2, 3);
        m.TextureCoordinates.Add(0, 0);
        m.TextureCoordinates.Add(1, 0);
        m.TextureCoordinates.Add(1, 1);
        m.TextureCoordinates.Add(0, 1);
        m.Normals.ComputeNormals();

        var mat = new DisplayMaterial(Color.White);
        if (!mat.SetBitmapTexture(path, true))
        {
            m.Dispose();
            mat.Dispose();
            return false;
        }
        _mesh = m;
        _material = mat;
        return true;
    }

    private static Rectangle3d DefaultOriginRectangle(double aspect)
    {
        const double width = 10.0;
        double height = width / aspect;
        return new Rectangle3d(
            Plane.WorldXY,
            new Interval(-width / 2.0, width / 2.0),
            new Interval(-height / 2.0, height / 2.0));
    }

    public override BoundingBox ClippingBox => _mesh?.GetBoundingBox(false) ?? BoundingBox.Empty;

    public override void DrawViewportMeshes(IGH_PreviewArgs a)
    {
        base.DrawViewportMeshes(a);
        if (_mesh != null && _material != null) a.Display.DrawMeshShaded(_mesh, _material);
    }

    private void ResetPreview()
    {
        _mesh?.Dispose();
        _mesh = null;
        _material?.Dispose();
        _material = null;
    }

    // ── helpers ───────────────────────────────────────────────────────────
    private string DiagramDirectory()
    {
        string gh = OnPingDocument()?.FilePath;
        if (!string.IsNullOrWhiteSpace(gh))
        {
            string dir = Path.GetDirectoryName(gh);
            string fileName = Path.GetFileNameWithoutExtension(gh);
            if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(fileName))
                return Path.Combine(dir, "WASPer_" + fileName, "layer_diagrams");
        }

        return Path.Combine(Path.GetTempPath(), "WASPer_3DP", "layer_diagrams");
    }

    private static WasperLayer Extract(object input)
    {
        if (input == null) return null;
        if (input is WasperLayer layer) return layer;
        if (input is WasperLayerGoo layerGoo) return layerGoo.Value;
        if (input is GH_ObjectWrapper wrapper) return Extract(wrapper.Value);
        if (input is IGH_Goo g)
        {
            object value = g.ScriptVariable();
            if (value != null && !ReferenceEquals(value, input)) return Extract(value);
        }
        return null;
    }
}
