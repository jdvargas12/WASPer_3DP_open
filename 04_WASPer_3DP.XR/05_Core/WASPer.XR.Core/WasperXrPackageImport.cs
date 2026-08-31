using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace WASPer.XR.Core;

/// <summary>
/// Converts an existing .wasperxr package -- either the JSON form (schema
/// 0.1.0, documented in 00_Plans/WASPER_XR_SCHEMA_0.1.0.json) or the binary
/// form actually written by Gc07/Sm01 today (schema 0.2.0, magic
/// <c>WSPXRBN1</c>, documented in 00_Plans/WASPER_XR_BINARY_0.2.md and
/// grounded byte-for-byte against
/// 01_WASPer_3DP/Components/5.0_Gcode/WASPer_XrBinaryPackage.cs) -- into a
/// WASPerPrintJob. This is the bridge between the manufacturing-side export
/// that already exists and the platform-independent model new viewers
/// consume -- Core owns it because it is pure data translation with no
/// dependency beyond the net8.0 BCL (System.Text.Json plus
/// System.IO/System.IO.Compression's BinaryReader/GZipStream, both built in,
/// no NuGet package needed for either).
///
/// Display meshes ("payload.meshes") are normalized to ContextMesh records. The same model is
/// also populated by the optional context-mesh section appended to binary packages by Sm05.
///
/// Both formats are normalized to the same WASPerPrintJob contract:
/// positions are always absolute and CoordinateFrame.Origin is always
/// Vec3.Zero, even though the binary container itself stores positions as
/// float32 offsets from a float64 origin internally (a precision trick for
/// coordinates far from the world origin, not something a consumer of
/// WASPerPrintJob should have to know about or undo itself).
/// </summary>
public static class WasperXrPackageImport
{
    private const string BinaryMagic = "WSPXRBN1";
    private const int BinaryContainerVersion = 1;
    private const byte BinaryGzipCompression = 1;

    private static readonly JsonSerializerOptions DtoOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads a .wasperxr file of either format, detected from its first 8
    /// bytes (matching the same auto-detection the Process Viewer already
    /// does, per WASPER_XR_BINARY_0.2.md's "Compatibility" note).
    /// </summary>
    public static WASPerPrintJob FromFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (IsBinaryMagic(stream))
            return FromBinary(stream);

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return FromJson(reader.ReadToEnd());
    }

    private static bool IsBinaryMagic(Stream stream)
    {
        var magic = new byte[8];
        int read = stream.Read(magic, 0, 8);
        stream.Position = 0;
        return read == 8 && Encoding.ASCII.GetString(magic) == BinaryMagic;
    }

    public static WASPerPrintJob FromJson(string json)
    {
        WasperXrPackageDto package = JsonSerializer.Deserialize<WasperXrPackageDto>(json, DtoOptions)
            ?? throw new FormatException("Could not parse .wasperxr JSON payload.");

        return Convert(package);
    }

    /// <summary>
    /// Reads the binary container: an 8-byte magic, Int32 container version,
    /// and a compression byte, uncompressed and read directly; everything
    /// after that is GZip-compressed and read as one .NET BinaryWriter
    /// stream, field order matching WasperXrBinaryPackage.Write exactly.
    /// </summary>
    public static WASPerPrintJob FromBinary(Stream stream)
    {
        using (var header = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            string magic = Encoding.ASCII.GetString(header.ReadBytes(8));
            if (magic != BinaryMagic)
                throw new FormatException($"Not a .wasperxr binary container (magic was '{magic}').");

            int containerVersion = header.ReadInt32();
            if (containerVersion != BinaryContainerVersion)
                throw new FormatException($"Unsupported .wasperxr binary container version {containerVersion}.");

            byte compression = header.ReadByte();
            if (compression != BinaryGzipCompression)
                throw new FormatException($"Unsupported .wasperxr binary compression byte {compression}.");
        }

        using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var payload = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: false);
        return ReadBinaryPayload(payload);
    }

    public static WASPerPrintJob FromBinaryFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return FromBinary(stream);
    }

    private static WASPerPrintJob Convert(WasperXrPackageDto package)
    {
        WasperXrCoordinatesDto c = package.Coordinates;
        var coordinates = new CoordinateFrame(
            Frame: c.Frame,
            Units: c.Units,
            MetresPerUnit: c.MetresPerUnit,
            Handedness: c.Handedness,
            UpAxis: c.UpAxis,
            // .wasperxr JSON (schema 0.1.0) carries absolute positions, not
            // origin-relative offsets like the 0.2.0 binary container does --
            // so there is no origin to record here.
            Origin: Vec3.Zero);

        var metadata = new PrintJobMetadata(
            JobId: package.JobId,
            Name: package.JobId,
            Revision: package.Revision,
            TimestampUtc: package.TimestampUtc,
            PluginVersion: package.PluginVersion,
            Coordinates: coordinates);

        List<PathBranch> branches = (package.Payload.Paths ?? new List<WasperXrPathDto>())
            .Select(ConvertBranch)
            .ToList();

        List<PathSegment> segments = (package.Payload.Motions ?? new List<WasperXrMotionDto>())
            .OrderBy(m => m.Index)
            .Select((m, i) => ConvertSegment(m, i))
            .ToList();

        List<Layer> layers = BuildLayers(branches, segments);
        BeadProperties defaultBead = BuildDefaultBead(branches);

        WasperXrSummaryDto summary = package.Payload.Summary;
        var statistics = new PrintJobStatistics(
            TotalLengthModelUnits: segments.Sum(s => s.LengthModelUnits),
            ExtrusionLengthModelUnits: segments.Where(s => s.Type == MotionType.Print).Sum(s => s.LengthModelUnits),
            TravelLengthModelUnits: segments.Where(s => s.Type != MotionType.Print).Sum(s => s.LengthModelUnits),
            LayerCount: summary.LayerCount,
            EstimatedDurationSeconds: summary.DurationSeconds);

        // Schema 0.1.0 stored summary.kpis as a compact key/value object. Newer
        // JSON producers use the same structured KPI array as the binary path.
        // Accept both so old exported jobs remain valid viewer inputs.
        List<PrintJobKpi> kpis = ConvertJsonKpis(summary.Kpis);

        List<ContextMesh> contextMeshes = (package.Payload.Meshes ?? new List<WasperXrMeshDto>())
            .Select(ConvertMesh)
            .ToList();

        return new WASPerPrintJob(
            metadata, branches, segments, layers, defaultBead, statistics, kpis, contextMeshes);
    }

    private static PrintJobKpi ConvertKpi(WasperXrKpiDto k) => new(
        Key: k.Key,
        Label: k.Label,
        Group: k.Group,
        Unit: k.Unit,
        Value: k.Value,
        TextValue: k.TextValue);

    private static List<PrintJobKpi> ConvertJsonKpis(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return new List<PrintJobKpi>();

        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Select(item => item.Deserialize<WasperXrKpiDto>(DtoOptions))
                .Where(item => item != null)
                .Select(item => ConvertKpi(item!))
                .ToList();
        }

        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("summary.kpis must be an object or an array.");

        var result = new List<PrintJobKpi>();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            double? value = property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetDouble(out double numericValue)
                    ? numericValue
                    : null;
            string? textValue = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : null;
            result.Add(new PrintJobKpi(
                Key: $"fabrication.{property.Name}",
                Label: LegacyKpiLabel(property.Name),
                Group: "Fabrication",
                Unit: LegacyKpiUnit(property.Name),
                Value: value,
                TextValue: textValue));
        }
        return result;
    }

    private static string LegacyKpiLabel(string key) => key switch
    {
        "unitsCode" => "Units code",
        "timeMinutes" => "Estimated time",
        "pathLength" => "Path length",
        "volume" => "Volume",
        "massKg" => "Mass",
        "layers" => "Layers",
        _ => key
    };

    private static string LegacyKpiUnit(string key) => key switch
    {
        "timeMinutes" => "min",
        "massKg" => "kg",
        "layers" => "count",
        _ => string.Empty
    };

    private static PathBranch ConvertBranch(WasperXrPathDto p)
    {
        Dictionary<string, List<double>> values = p.Values ?? new Dictionary<string, List<double>>();
        List<double> layerHeight = values.GetValueOrDefault("layerHeight") ?? new List<double>();
        List<double> layerWidth = values.GetValueOrDefault("layerWidth") ?? new List<double>();
        List<double> flow = values.GetValueOrDefault("flow") ?? new List<double>();
        List<double> printSpeed = values.GetValueOrDefault("printSpeed") ?? new List<double>();

        // "layerWidth" in the exported JSON is the nominal width; the schema
        // carries flow as a separate per-point multiplier rather than a
        // second width series (unlike WasperPrintPath's LayerW/LayerWf split
        // in Grasshopper). Recompute the flow-adjusted width here so both
        // series exist on PathBranch as documented.
        List<double> flowAdjustedWidth = layerWidth
            .Select((w, i) => w * (i < flow.Count ? flow[i] : 1.0))
            .ToList();

        return new PathBranch(
            BranchIndex: p.BranchIndex,
            BranchPath: p.BranchPath,
            LayerIndex: p.LayerIndex,
            Role: (PathRole)p.Role,
            StrokeId: p.StrokeId,
            Closed: p.Closed,
            Positions: (p.Positions ?? new List<double[]>()).Select(ToVec3).ToList(),
            XAxes: (p.XAxes ?? new List<double[]>()).Select(ToVec3).ToList(),
            YAxes: (p.YAxes ?? new List<double[]>()).Select(ToVec3).ToList(),
            ZAxes: (p.ZAxes ?? new List<double[]>()).Select(ToVec3).ToList(),
            LayerHeight: layerHeight,
            LayerWidthNominal: layerWidth,
            LayerWidthFlowAdjusted: flowAdjustedWidth,
            PrintSpeed: printSpeed);
    }

    private static PathSegment ConvertSegment(WasperXrMotionDto m, int index) => new(
        Index: index,
        Type: ParseMotionType(m.Type),
        LayerIndex: m.LayerIndex,
        BranchIndex: m.BranchIndex,
        BranchPath: m.BranchPath,
        PointIndex: m.PointIndex,
        Role: (PathRole)m.Role,
        From: ToVec3(m.From),
        To: ToVec3(m.To),
        FeedrateMmPerMinute: m.FeedrateMmPerMinute,
        LengthModelUnits: m.LengthModelUnits,
        StartTimeSeconds: m.StartTimeSeconds,
        EndTimeSeconds: m.EndTimeSeconds);

    private static MotionType ParseMotionType(string type) => type.ToLowerInvariant() switch
    {
        "print" => MotionType.Print,
        "travel" => MotionType.Travel,
        "zhop" => MotionType.ZHop,
        _ => throw new FormatException($"Unknown .wasperxr motion type '{type}'.")
    };

    private static List<Layer> BuildLayers(IReadOnlyList<PathBranch> branches, IReadOnlyList<PathSegment> segments)
    {
        var layers = new List<Layer>();
        foreach (int layerIndex in segments.Select(s => s.LayerIndex).Distinct().OrderBy(i => i))
        {
            List<int> indices = segments
                .Select((s, i) => (Segment: s, Index: i))
                .Where(x => x.Segment.LayerIndex == layerIndex)
                .Select(x => x.Index)
                .ToList();

            double height = branches
                .FirstOrDefault(b => b.LayerIndex == layerIndex)?
                .LayerHeight.FirstOrDefault() ?? 0.0;

            layers.Add(new Layer(layerIndex, height, indices.Min(), indices.Max()));
        }
        return layers;
    }

    private static BeadProperties BuildDefaultBead(IReadOnlyList<PathBranch> branches)
    {
        List<double> widths = branches.SelectMany(b => b.LayerWidthNominal).ToList();
        List<double> heights = branches.SelectMany(b => b.LayerHeight).ToList();
        return new BeadProperties(
            NominalWidth: widths.Count > 0 ? widths.Average() : 0.0,
            NominalHeight: heights.Count > 0 ? heights.Average() : 0.0);
    }

    // ---- Binary (schema 0.2.0) reading. Field order below matches
    // WasperXrBinaryPackage.Write exactly; see that file for the
    // authoritative layout if this ever needs re-verifying. ----

    private static WASPerPrintJob ReadBinaryPayload(BinaryReader reader)
    {
        _ = reader.ReadString(); // schemaVersion, e.g. "0.2.0" -- not currently gated on; a minor version bump shouldn't break this reader
        _ = reader.ReadString(); // type tag, always "wasper.xr.printPlan"
        string jobId = reader.ReadString();
        int revision = reader.ReadInt32();
        string timestampUtc = reader.ReadString();
        string pluginVersion = reader.ReadString();
        string frame = reader.ReadString();
        string units = reader.ReadString();
        double metresPerUnit = reader.ReadDouble();
        string handedness = reader.ReadString();
        string upAxis = reader.ReadString();
        Vec3 origin = ReadPoint64(reader);

        bool boundsValid = reader.ReadBoolean();
        if (boundsValid)
        {
            _ = ReadPoint64(reader); // bounds min -- not modeled on PrintJobStatistics yet (M2 computes bounds client-side instead); read here only to keep the stream position correct
            _ = ReadPoint64(reader); // bounds max
        }

        int branchCount = reader.ReadInt32();
        var branches = new List<PathBranch>(branchCount);
        for (int i = 0; i < branchCount; i++)
            branches.Add(ReadBinaryBranch(reader, origin));

        int motionCount = reader.ReadInt32();
        var rawMotions = new List<BinaryMotionDto>(motionCount);
        for (int i = 0; i < motionCount; i++)
            rawMotions.Add(ReadBinaryMotion(reader, origin));

        int layerCount = reader.ReadInt32();
        _ = reader.ReadInt32(); // total point count across all branches -- branches already carry their own point counts individually
        double durationSeconds = reader.ReadDouble();

        // KPI section (added after M3's playback controls): written as the
        // very last thing in the payload by WasperXrBinaryPackage.Write, so
        // files written before this feature simply end here instead. There
        // is no length-prefixed "extra bytes" block anywhere else in this
        // format to detect that in advance, so treat hitting the end of the
        // stream as "no KPI section" rather than gating on schemaVersion --
        // safe because nothing is read after this.
        List<PrintJobKpi> kpis = TryReadBinaryKpis(reader);

        // DisablePlayback flag (added for Sm05 XR Scene Params, 2026-08-19): written
        // immediately after the KPI section by WasperXrBinaryPackage.Write, so it is only
        // present in files exported after this feature landed. Same EOF-tolerant pattern as
        // TryReadBinaryKpis just above -- a missing flag (old export, or one that stopped at
        // the KPI section's own EOF) simply defaults to false, i.e. "viewer keeps its own
        // playback controls," which matches every package written before this existed.
        bool disablePlayback = TryReadBinaryDisablePlayback(reader);

        // SimulationParameter (added same day, right after DisablePlayback): the actual 0-1
        // progress value behind that flag. Same EOF-tolerant pattern; defaults to 1.0 ("fully
        // printed") for packages written before this field existed, matching the writer's own
        // default and PrintJobMetadata's.
        double simulationParameter = TryReadBinarySimulationParameter(reader);
        List<ContextMesh> contextMeshes = TryReadBinaryContextMeshes(reader, origin);
        ViewerStyle? viewerStyle = TryReadBinaryViewerStyle(reader);

        List<PathSegment> segments = BuildSegmentsFromBinaryMotions(rawMotions, branches);
        List<Layer> layers = BuildLayers(branches, segments);
        BeadProperties defaultBead = BuildDefaultBead(branches);

        var coordinates = new CoordinateFrame(frame, units, metresPerUnit, handedness, upAxis, Vec3.Zero);
        var metadata = new PrintJobMetadata(
            JobId: jobId,
            Name: jobId,
            Revision: revision,
            TimestampUtc: DateTimeOffset.TryParse(timestampUtc, out DateTimeOffset parsed) ? parsed : DateTimeOffset.UtcNow,
            PluginVersion: pluginVersion,
            Coordinates: coordinates,
            DisablePlayback: disablePlayback,
            SimulationParameter: simulationParameter);

        var statistics = new PrintJobStatistics(
            TotalLengthModelUnits: segments.Sum(s => s.LengthModelUnits),
            ExtrusionLengthModelUnits: segments.Where(s => s.Type == MotionType.Print).Sum(s => s.LengthModelUnits),
            TravelLengthModelUnits: segments.Where(s => s.Type != MotionType.Print).Sum(s => s.LengthModelUnits),
            LayerCount: layerCount,
            EstimatedDurationSeconds: durationSeconds);

        return new WASPerPrintJob(
            metadata, branches, segments, layers, defaultBead, statistics, kpis, contextMeshes, viewerStyle);
    }

    // Mirrors WasperXrBinaryPackage's (not yet written) KPI section byte for
    // byte: Int32 count, then per item Key/Label/Group/Unit strings, a bool
    // for whether Value is present (followed by a double if so), and a bool
    // for whether TextValue is present (followed by a string if so). Wrapped
    // in a try/catch because it is the last read in the stream -- old
    // packages that predate this feature simply run out of bytes here,
    // which is the expected, non-error case for them.
    private static List<PrintJobKpi> TryReadBinaryKpis(BinaryReader reader)
    {
        try
        {
            int count = reader.ReadInt32();
            var kpis = new List<PrintJobKpi>(count);
            for (int i = 0; i < count; i++)
            {
                string key = reader.ReadString();
                string label = reader.ReadString();
                string group = reader.ReadString();
                string unit = reader.ReadString();
                double? value = reader.ReadBoolean() ? reader.ReadDouble() : null;
                string? textValue = reader.ReadBoolean() ? reader.ReadString() : null;
                kpis.Add(new PrintJobKpi(key, label, group, unit, value, textValue));
            }
            return kpis;
        }
        catch (EndOfStreamException)
        {
            return new List<PrintJobKpi>();
        }
    }

    // See the DisablePlayback comment at its call site in ReadBinaryPayload -- this is the
    // very last field in the container, so a missing one (old export) is expected, not an
    // error.
    private static bool TryReadBinaryDisablePlayback(BinaryReader reader)
    {
        try
        {
            return reader.ReadBoolean();
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    // See the SimulationParameter comment at its call site in ReadBinaryPayload -- now the
    // actual very last field in the container. 1.0 ("fully printed") is the same fallback the
    // writer itself defaults to, so a missing value behaves identically to an explicit 1.0.
    private static double TryReadBinarySimulationParameter(BinaryReader reader)
    {
        try
        {
            return reader.ReadDouble();
        }
        catch (EndOfStreamException)
        {
            return 1.0;
        }
    }

    private static List<ContextMesh> TryReadBinaryContextMeshes(BinaryReader reader, Vec3 origin)
    {
        try
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 100000)
                throw new FormatException($"Invalid context mesh count {count}.");

            var meshes = new List<ContextMesh>(count);
            for (int meshIndex = 0; meshIndex < count; meshIndex++)
            {
                string id = reader.ReadString();
                byte red = reader.ReadByte();
                byte green = reader.ReadByte();
                byte blue = reader.ReadByte();
                byte alpha = reader.ReadByte();

                int vertexCount = reader.ReadInt32();
                if (vertexCount < 0 || vertexCount > 50000000)
                    throw new FormatException($"Invalid vertex count {vertexCount} for context mesh '{id}'.");
                var vertices = new List<Vec3>(vertexCount);
                for (int i = 0; i < vertexCount; i++)
                    vertices.Add(ReadRelativeVec3(reader, origin));

                int normalCount = reader.ReadInt32();
                if (normalCount < 0 || normalCount > 50000000)
                    throw new FormatException($"Invalid normal count {normalCount} for context mesh '{id}'.");
                var normals = new List<Vec3>(normalCount);
                for (int i = 0; i < normalCount; i++)
                    normals.Add(ReadVec3(reader));

                int indexCount = reader.ReadInt32();
                if (indexCount < 0 || indexCount > 150000000 || indexCount % 3 != 0)
                    throw new FormatException($"Invalid triangle index count {indexCount} for context mesh '{id}'.");
                var indices = new List<int>(indexCount);
                for (int i = 0; i < indexCount; i++)
                    indices.Add(reader.ReadInt32());

                meshes.Add(new ContextMesh(
                    id, vertices, normals, indices, red, green, blue, alpha));
            }
            return meshes;
        }
        catch (EndOfStreamException)
        {
            return new List<ContextMesh>();
        }
    }

    private static ViewerStyle? TryReadBinaryViewerStyle(BinaryReader reader)
    {
        try
        {
            return new ViewerStyle(
                reader.ReadString(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32());
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static PathBranch ReadBinaryBranch(BinaryReader reader, Vec3 origin)
    {
        int branchIndex = reader.ReadInt32();
        string branchPath = reader.ReadString();
        int layerIndex = reader.ReadInt32();
        int role = reader.ReadInt32();
        _ = reader.ReadString(); // roleName -- redundant with `role`, same convention as WasperPathRoleMetadata.RoleName
        int strokeId = reader.ReadInt32();
        bool closed = reader.ReadBoolean();
        int pointCount = reader.ReadInt32();

        var positions = new List<Vec3>(pointCount);
        for (int i = 0; i < pointCount; i++)
            positions.Add(ReadRelativeVec3(reader, origin));

        var zAxes = new List<Vec3>(pointCount);
        for (int i = 0; i < pointCount; i++)
            zAxes.Add(ReadVec3(reader));

        List<double> layerHeight = ReadSeries(reader, pointCount);
        List<double> layerWidthNominal = ReadSeries(reader, pointCount);
        List<double> layerWidthFlowAdjusted = ReadSeries(reader, pointCount);

        // The binary container only stores each point's height axis (the
        // plane's ZAxis) -- unlike the JSON schema, it has no X/Y axis
        // fields at all (see WasperXrBinaryPackage.WriteBranch, which writes
        // positions then ZAxis and nothing else orientation-wise).
        // Reconstruct a travel tangent per point from neighbouring
        // positions and derive the width axis as
        // normalize(cross(zAxis, tangent)). Checked against the JSON
        // fixture's own axes (xAxis=(1,0,0), zAxis=(0,0,1) gives
        // cross(z,x)=(0,1,0), which is exactly its stored yAxis) so this
        // reproduces the same right-handed convention JSON-sourced branches
        // already use -- PathBranch's contract stays identical regardless
        // of which import path produced it.
        (List<Vec3> xAxes, List<Vec3> yAxes) = ReconstructTangentFrame(positions, zAxes);

        return new PathBranch(
            BranchIndex: branchIndex,
            BranchPath: branchPath,
            LayerIndex: layerIndex,
            Role: (PathRole)role,
            StrokeId: strokeId,
            Closed: closed,
            Positions: positions,
            XAxes: xAxes,
            YAxes: yAxes,
            ZAxes: zAxes,
            LayerHeight: layerHeight,
            LayerWidthNominal: layerWidthNominal,
            LayerWidthFlowAdjusted: layerWidthFlowAdjusted,
            PrintSpeed: new List<double>()); // not carried by the binary container
    }

    private static (List<Vec3> XAxes, List<Vec3> YAxes) ReconstructTangentFrame(
        IReadOnlyList<Vec3> positions, IReadOnlyList<Vec3> zAxes)
    {
        int n = positions.Count;
        var xAxes = new List<Vec3>(n);
        var yAxes = new List<Vec3>(n);
        for (int i = 0; i < n; i++)
        {
            Vec3 tangent = n < 2
                ? new Vec3(1, 0, 0)
                : Normalize(Subtract(positions[Math.Min(i + 1, n - 1)], positions[Math.Max(i - 1, 0)]));
            Vec3 up = i < zAxes.Count ? zAxes[i] : new Vec3(0, 0, 1);

            Vec3 width = Normalize(Cross(up, tangent));
            if (width.X * width.X + width.Y * width.Y + width.Z * width.Z < 1e-12)
            {
                // Tangent parallel to up (a vertical move) -- cross product
                // degenerates. Fall back to a stable perpendicular instead.
                Vec3 fallback = Math.Abs(up.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
                width = Normalize(Cross(up, fallback));
            }

            xAxes.Add(tangent);
            yAxes.Add(width);
        }
        return (xAxes, yAxes);
    }

    private static Vec3 Subtract(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static Vec3 Cross(Vec3 a, Vec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    private static Vec3 Normalize(Vec3 v)
    {
        double length = Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
        return length > 1e-9 ? new Vec3(v.X / length, v.Y / length, v.Z / length) : v;
    }

    private static BinaryMotionDto ReadBinaryMotion(BinaryReader reader, Vec3 origin)
    {
        byte type = reader.ReadByte();
        int layerIndex = reader.ReadInt32();
        int branchIndex = reader.ReadInt32();
        int pointIndex = reader.ReadInt32();
        double durationSeconds = reader.ReadDouble();
        Vec3 from = ReadRelativeVec3(reader, origin);
        Vec3 to = ReadRelativeVec3(reader, origin);
        return new BinaryMotionDto(type, layerIndex, branchIndex, pointIndex, durationSeconds, from, to);
    }

    // No feedrate, length, role, or start/end time are stored per motion in
    // the binary container (only a duration -- see WasperXrBinaryPackage.
    // WriteMotion). All four are derived here: length from the two points,
    // feedrate by inverting WasperMotion's own DurationMinutes = Length /
    // Feedrate, role by looking up the owning branch (a branch carries one
    // role for its whole length in this model), and start/end time by
    // summing durations in the order motions were written -- which is
    // already chronological, since WasperMotionPlan is an ordered list.
    private static List<PathSegment> BuildSegmentsFromBinaryMotions(
        IReadOnlyList<BinaryMotionDto> rawMotions, IReadOnlyList<PathBranch> branches)
    {
        var segments = new List<PathSegment>(rawMotions.Count);
        double elapsed = 0.0;

        for (int i = 0; i < rawMotions.Count; i++)
        {
            BinaryMotionDto m = rawMotions[i];
            double length = m.From.DistanceTo(m.To);
            double durationMinutes = m.DurationSeconds / 60.0;
            double feedrate = durationMinutes > 1e-9 ? length / durationMinutes : 0.0;
            double start = elapsed;
            double end = elapsed + m.DurationSeconds;
            elapsed = end;

            PathBranch? owningBranch = branches.FirstOrDefault(b => b.BranchIndex == m.BranchIndex);

            segments.Add(new PathSegment(
                Index: i,
                Type: (MotionType)m.Type,
                LayerIndex: m.LayerIndex,
                BranchIndex: m.BranchIndex,
                BranchPath: owningBranch?.BranchPath,
                PointIndex: m.PointIndex,
                Role: owningBranch?.Role ?? PathRole.Undefined,
                From: m.From,
                To: m.To,
                FeedrateMmPerMinute: feedrate,
                LengthModelUnits: length,
                StartTimeSeconds: start,
                EndTimeSeconds: end));
        }

        return segments;
    }

    private static List<double> ReadSeries(BinaryReader reader, int pointCount)
    {
        byte marker = reader.ReadByte();
        switch (marker)
        {
            case 0:
                return new List<double>();
            case 1:
                double constant = reader.ReadSingle();
                return Enumerable.Repeat(constant, pointCount).ToList();
            case 2:
                int seriesCount = reader.ReadInt32();
                var values = new List<double>(seriesCount);
                for (int i = 0; i < seriesCount; i++)
                    values.Add(reader.ReadSingle());
                return values;
            default:
                throw new FormatException($"Unknown .wasperxr binary series marker {marker}.");
        }
    }

    private static Vec3 ReadRelativeVec3(BinaryReader reader, Vec3 origin)
    {
        float x = reader.ReadSingle();
        float y = reader.ReadSingle();
        float z = reader.ReadSingle();
        return new Vec3(x + origin.X, y + origin.Y, z + origin.Z);
    }

    private static Vec3 ReadVec3(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static Vec3 ReadPoint64(BinaryReader reader) =>
        new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());

    private sealed record BinaryMotionDto(
        byte Type, int LayerIndex, int BranchIndex, int PointIndex,
        double DurationSeconds, Vec3 From, Vec3 To);

    private static Vec3 ToVec3(double[] xyz) => new(xyz[0], xyz[1], xyz[2]);

    private static ContextMesh ConvertMesh(WasperXrMeshDto mesh)
    {
        List<int> indices = new();
        foreach (int[] face in mesh.Faces ?? new List<int[]>())
        {
            if (face.Length == 3)
                indices.AddRange(face);
            else if (face.Length >= 4)
            {
                indices.Add(face[0]); indices.Add(face[1]); indices.Add(face[2]);
                indices.Add(face[0]); indices.Add(face[2]); indices.Add(face[3]);
            }
        }

        int[] color = mesh.ColorsRgba?.FirstOrDefault() ?? new[] { 170, 174, 182, 255 };
        byte Channel(int index, int fallback) =>
            (byte)Math.Clamp(index < color.Length ? color[index] : fallback, 0, 255);
        return new ContextMesh(
            mesh.Id ?? "context",
            (mesh.Vertices ?? new List<double[]>()).Select(ToVec3).ToList(),
            (mesh.Normals ?? new List<double[]>()).Select(ToVec3).ToList(),
            indices,
            Channel(0, 170), Channel(1, 174), Channel(2, 182), Channel(3, 255));
    }

    private sealed record WasperXrPackageDto(
        string SchemaVersion,
        string Type,
        string JobId,
        int Revision,
        DateTimeOffset TimestampUtc,
        string PluginVersion,
        WasperXrCoordinatesDto Coordinates,
        WasperXrPayloadDto Payload);

    private sealed record WasperXrCoordinatesDto(
        string Frame,
        string Units,
        double MetresPerUnit,
        string Handedness,
        string UpAxis);

    private sealed record WasperXrPayloadDto(
        List<WasperXrMeshDto>? Meshes,
        List<WasperXrPathDto>? Paths,
        List<WasperXrMotionDto>? Motions,
        WasperXrSummaryDto Summary);

    private sealed record WasperXrMeshDto(
        string? Id,
        List<double[]>? Vertices,
        List<double[]>? Normals,
        List<int[]>? Faces,
        List<int[]>? ColorsRgba);

    private sealed record WasperXrPathDto(
        int BranchIndex,
        string BranchPath,
        int LayerIndex,
        int Role,
        string RoleName,
        int StrokeId,
        bool Closed,
        List<double[]>? Positions,
        List<double[]>? XAxes,
        List<double[]>? YAxes,
        List<double[]>? ZAxes,
        Dictionary<string, List<double>>? Values);

    private sealed record WasperXrMotionDto(
        int Index,
        string Type,
        int LayerIndex,
        int BranchIndex,
        string? BranchPath,
        int PointIndex,
        int Role,
        string RoleName,
        double[] From,
        double[] To,
        double FeedrateMmPerMinute,
        double LengthModelUnits,
        double StartTimeSeconds,
        double EndTimeSeconds);

    private sealed record WasperXrSummaryDto(
        int LayerCount,
        double DurationSeconds,
        JsonElement Kpis);

    private sealed record WasperXrKpiDto(
        string Key,
        string Label,
        string Group,
        string Unit,
        double? Value,
        string? TextValue);
}
