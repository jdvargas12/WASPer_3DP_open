// Analytic GPU ray-cast preview for printing-path bead segments.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using Rhino.Display;
using Rhino.Geometry;

namespace WASPer_3DP
{
    internal sealed class WasperPrintPathSegmentRenderer : IDisposable
    {
        private const uint GlBlend = 0x0BE2;
        private const uint GlCullFace = 0x0B44;
        private const uint GlDepthTest = 0x0B71;
        private const uint GlTriangles = 0x0004;
        private const uint GlTexture0 = 0x84C0;
        private const uint GlActiveTexture = 0x84E0;
        private const uint GlTexture2D = 0x0DE1;
        private const uint GlTextureBinding2D = 0x8069;
        private const uint GlTextureMinFilter = 0x2801;
        private const uint GlTextureMagFilter = 0x2800;
        private const uint GlTextureWrapS = 0x2802;
        private const uint GlTextureWrapT = 0x2803;
        private const int GlNearest = 0x2600;
        private const int GlClampToEdge = 0x812F;
        private const int GlRgba32F = 0x8814;
        private const uint GlRgba = 0x1908;
        private const uint GlFloat = 0x1406;
        private const uint GlVertexShader = 0x8B31;
        private const uint GlFragmentShader = 0x8B30;
        private const uint GlCompileStatus = 0x8B81;
        private const uint GlLinkStatus = 0x8B82;
        private const uint GlInfoLogLength = 0x8B84;
        private const uint GlCurrentProgram = 0x8B8D;
        private const uint GlVertexArrayBinding = 0x85B5;
        private const uint GlDepthWritemask = 0x0B72;

        private static readonly object DeleteLock = new object();
        private static readonly Queue<uint> PendingTextureDeletes = new Queue<uint>();
        private static readonly Queue<uint> PendingProgramDeletes = new Queue<uint>();
        private static readonly Queue<uint> PendingVertexArrayDeletes = new Queue<uint>();

        private WasperPrintPathPreviewBatch _batch;
        private uint _texture;
        private uint _program;
        private uint _vertexArray;
        private bool _textureDirty = true;
        private string _lastError = "";
        private Uniforms _uniforms;
        private Vector3d _lightDirection = new Vector3d(0.35, -0.45, 0.82);
        private float _ambient = 0.6f;
        private float _shadeStrength = 0.4f;
        private float _profileExponent = 4.0f;

        internal string LastError => _lastError;

        internal void SetBatch(WasperPrintPathPreviewBatch batch)
        {
            if (!ReferenceEquals(_batch, batch))
                _textureDirty = true;
            _batch = batch;
        }

        internal void SetLightDirection(Vector3d lightDirection)
        {
            if (!lightDirection.IsValid || !lightDirection.Unitize())
                lightDirection = -Vector3d.ZAxis;

            // The public input describes the direction in which rays travel.
            // Lambert shading needs the opposite vector: surface -> light.
            _lightDirection = -lightDirection;
        }

        internal void SetShading(double ambient, double shadeStrength)
        {
            _ambient = (float)Math.Max(0.0, Math.Min(1.0, ambient));
            _shadeStrength = (float)Math.Max(0.0, Math.Min(1.0, shadeStrength));
        }

        internal void SetProfileExponent(int exponent)
        {
            _profileExponent = exponent <= 2 ? 2.0f
                : exponent == 3 ? 3.0f
                : exponent <= 5 ? 4.0f
                : 6.0f;
        }

        internal void Clear()
        {
            _batch = null;
            _textureDirty = true;
        }

        internal bool Draw(DisplayPipeline display)
        {
            if (_batch == null || _batch.SegmentCount == 0 || display == null)
                return false;

            try
            {
                WasperPreviewGl.EnsureInitialized();
                FlushDeletes();
                EnsureProgram();
                EnsureTexture();
                DrawSegments(display);
                _lastError = "";
                return true;
            }
            catch (Exception ex)
            {
                _lastError = InnermostMessage(ex);
                return false;
            }
        }

        private void EnsureProgram()
        {
            if (_program != 0)
                return;

            uint vertex = CompileShader(GlVertexShader, VertexShader);
            uint fragment = 0;
            try
            {
                fragment = CompileShader(GlFragmentShader, FragmentShader);
                _program = WasperPreviewGl.CreateProgram();
                WasperPreviewGl.AttachShader(_program, vertex);
                WasperPreviewGl.AttachShader(_program, fragment);
                WasperPreviewGl.LinkProgram(_program);
                WasperPreviewGl.GetProgramiv(_program, GlLinkStatus, out int linked);
                if (linked == 0)
                    throw new InvalidOperationException("Print-path preview shader link failed: " + ProgramLog(_program));

                WasperPreviewGl.GenVertexArrays(1, out _vertexArray);
                _uniforms = new Uniforms(_program);
            }
            finally
            {
                if (vertex != 0) WasperPreviewGl.DeleteShader(vertex);
                if (fragment != 0) WasperPreviewGl.DeleteShader(fragment);
            }
        }

        private void EnsureTexture()
        {
            if (!_textureDirty || _batch == null)
                return;

            WasperPreviewGl.GetIntegerv(GlActiveTexture, out int oldActiveTexture);
            WasperPreviewGl.ActiveTexture(GlTexture0);
            WasperPreviewGl.GetIntegerv(GlTextureBinding2D, out int oldTexture);
            try
            {
                if (_texture == 0)
                    WasperPreviewGl.GenTextures(1, out _texture);

                WasperPreviewGl.BindTexture(GlTexture2D, _texture);
                WasperPreviewGl.TexParameteri(GlTexture2D, GlTextureMinFilter, GlNearest);
                WasperPreviewGl.TexParameteri(GlTexture2D, GlTextureMagFilter, GlNearest);
                WasperPreviewGl.TexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
                WasperPreviewGl.TexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);

                GCHandle handle = GCHandle.Alloc(_batch.SegmentData, GCHandleType.Pinned);
                try
                {
                    WasperPreviewGl.TexImage2D(
                        GlTexture2D,
                        0,
                        GlRgba32F,
                        _batch.SegmentCount * WasperPrintPathPreviewBatch.TexelsPerSegment,
                        1,
                        0,
                        GlRgba,
                        GlFloat,
                        handle.AddrOfPinnedObject());
                }
                finally
                {
                    handle.Free();
                }

                _textureDirty = false;
            }
            finally
            {
                WasperPreviewGl.BindTexture(GlTexture2D, (uint)Math.Max(0, oldTexture));
                WasperPreviewGl.ActiveTexture((uint)oldActiveTexture);
            }
        }

        private void DrawSegments(DisplayPipeline display)
        {
            float[] openGlWorldToClip = display.GetOpenGLWorldToClip(false);
            if (openGlWorldToClip == null || openGlWorldToClip.Length < 16)
                return;

            Transform worldToClip = TransformFromOpenGl(openGlWorldToClip);
            if (!worldToClip.TryGetInverse(out Transform clipToWorld))
                return;

            WasperPreviewGl.GetIntegerv(GlCurrentProgram, out int oldProgram);
            WasperPreviewGl.GetIntegerv(GlVertexArrayBinding, out int oldVertexArray);
            WasperPreviewGl.GetIntegerv(GlActiveTexture, out int oldActiveTexture);
            WasperPreviewGl.ActiveTexture(GlTexture0);
            WasperPreviewGl.GetIntegerv(GlTextureBinding2D, out int oldTexture);
            bool blendWasEnabled = WasperPreviewGl.IsEnabled(GlBlend);
            bool cullWasEnabled = WasperPreviewGl.IsEnabled(GlCullFace);
            bool depthWasEnabled = WasperPreviewGl.IsEnabled(GlDepthTest);
            WasperPreviewGl.GetBooleanv(GlDepthWritemask, out byte oldDepthMask);

            try
            {
                WasperPreviewGl.Enable(GlDepthTest);
                WasperPreviewGl.Disable(GlBlend);
                WasperPreviewGl.Disable(GlCullFace);
                WasperPreviewGl.DepthMask(1);
                WasperPreviewGl.UseProgram(_program);
                WasperPreviewGl.BindVertexArray(_vertexArray);
                WasperPreviewGl.BindTexture(GlTexture2D, _texture);

                SetMatrix(_uniforms.ClipToWorld, clipToWorld);
                SetMatrix(_uniforms.WorldToClip, worldToClip);
                WasperPreviewGl.Uniform3f(
                    _uniforms.Color,
                    _batch.Color.R / 255.0f,
                    _batch.Color.G / 255.0f,
                    _batch.Color.B / 255.0f);
                WasperPreviewGl.Uniform3f(
                    _uniforms.LightDirection,
                    (float)_lightDirection.X,
                    (float)_lightDirection.Y,
                    (float)_lightDirection.Z);
                WasperPreviewGl.Uniform1f(_uniforms.Ambient, _ambient);
                WasperPreviewGl.Uniform1f(_uniforms.ShadeStrength, _shadeStrength);
                WasperPreviewGl.Uniform1f(_uniforms.ProfileExponent, _profileExponent);
                WasperPreviewGl.Uniform1i(_uniforms.Segments, 0);
                WasperPreviewGl.DrawArraysInstanced(GlTriangles, 0, 36, _batch.SegmentCount);
            }
            finally
            {
                WasperPreviewGl.BindTexture(GlTexture2D, (uint)Math.Max(0, oldTexture));
                WasperPreviewGl.BindVertexArray((uint)Math.Max(0, oldVertexArray));
                WasperPreviewGl.UseProgram((uint)Math.Max(0, oldProgram));
                WasperPreviewGl.ActiveTexture((uint)oldActiveTexture);
                WasperPreviewGl.DepthMask(oldDepthMask);

                if (blendWasEnabled) WasperPreviewGl.Enable(GlBlend);
                else WasperPreviewGl.Disable(GlBlend);
                if (cullWasEnabled) WasperPreviewGl.Enable(GlCullFace);
                else WasperPreviewGl.Disable(GlCullFace);
                if (depthWasEnabled) WasperPreviewGl.Enable(GlDepthTest);
                else WasperPreviewGl.Disable(GlDepthTest);
            }
        }

        public void Dispose()
        {
            lock (DeleteLock)
            {
                if (_texture != 0) PendingTextureDeletes.Enqueue(_texture);
                if (_program != 0) PendingProgramDeletes.Enqueue(_program);
                if (_vertexArray != 0) PendingVertexArrayDeletes.Enqueue(_vertexArray);
            }

            _texture = 0;
            _program = 0;
            _vertexArray = 0;
            _batch = null;
        }

        private static void FlushDeletes()
        {
            lock (DeleteLock)
            {
                while (PendingTextureDeletes.Count > 0)
                {
                    uint id = PendingTextureDeletes.Dequeue();
                    WasperPreviewGl.DeleteTextures(1, ref id);
                }
                while (PendingProgramDeletes.Count > 0)
                    WasperPreviewGl.DeleteProgram(PendingProgramDeletes.Dequeue());
                while (PendingVertexArrayDeletes.Count > 0)
                {
                    uint id = PendingVertexArrayDeletes.Dequeue();
                    WasperPreviewGl.DeleteVertexArrays(1, ref id);
                }
            }
        }

        private static uint CompileShader(uint type, string source)
        {
            uint shader = WasperPreviewGl.CreateShader(type);
            WasperPreviewGl.ShaderSource(shader, source);
            WasperPreviewGl.CompileShader(shader);
            WasperPreviewGl.GetShaderiv(shader, GlCompileStatus, out int compiled);
            if (compiled != 0)
                return shader;

            string log = ShaderLog(shader);
            WasperPreviewGl.DeleteShader(shader);
            throw new InvalidOperationException("Print-path preview shader compile failed: " + log);
        }

        private static string ShaderLog(uint shader)
        {
            WasperPreviewGl.GetShaderiv(shader, GlInfoLogLength, out int length);
            if (length <= 1) return "unknown shader error";
            var builder = new StringBuilder(length);
            WasperPreviewGl.GetShaderInfoLog(shader, length, out _, builder);
            return builder.ToString();
        }

        private static string ProgramLog(uint program)
        {
            WasperPreviewGl.GetProgramiv(program, GlInfoLogLength, out int length);
            if (length <= 1) return "unknown program error";
            var builder = new StringBuilder(length);
            WasperPreviewGl.GetProgramInfoLog(program, length, out _, builder);
            return builder.ToString();
        }

        private static void SetMatrix(int location, Transform transform)
        {
            var values = new[]
            {
                (float)transform.M00, (float)transform.M01, (float)transform.M02, (float)transform.M03,
                (float)transform.M10, (float)transform.M11, (float)transform.M12, (float)transform.M13,
                (float)transform.M20, (float)transform.M21, (float)transform.M22, (float)transform.M23,
                (float)transform.M30, (float)transform.M31, (float)transform.M32, (float)transform.M33
            };

            GCHandle handle = GCHandle.Alloc(values, GCHandleType.Pinned);
            try
            {
                WasperPreviewGl.UniformMatrix4fv(location, 1, 1, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        private static Transform TransformFromOpenGl(float[] values)
        {
            Transform transform = Transform.Identity;
            transform.M00 = values[0];  transform.M01 = values[4];  transform.M02 = values[8];  transform.M03 = values[12];
            transform.M10 = values[1];  transform.M11 = values[5];  transform.M12 = values[9];  transform.M13 = values[13];
            transform.M20 = values[2];  transform.M21 = values[6];  transform.M22 = values[10]; transform.M23 = values[14];
            transform.M30 = values[3];  transform.M31 = values[7];  transform.M32 = values[11]; transform.M33 = values[15];
            return transform;
        }

        private static string InnermostMessage(Exception exception)
        {
            Exception current = exception;
            while (current.InnerException != null) current = current.InnerException;
            return current.Message;
        }

        private readonly struct Uniforms
        {
            internal readonly int Segments;
            internal readonly int ClipToWorld;
            internal readonly int WorldToClip;
            internal readonly int Color;
            internal readonly int LightDirection;
            internal readonly int Ambient;
            internal readonly int ShadeStrength;
            internal readonly int ProfileExponent;

            internal Uniforms(uint program)
            {
                Segments = WasperPreviewGl.GetUniformLocation(program, "uSegments");
                ClipToWorld = WasperPreviewGl.GetUniformLocation(program, "uClipToWorld");
                WorldToClip = WasperPreviewGl.GetUniformLocation(program, "uWorldToClip");
                Color = WasperPreviewGl.GetUniformLocation(program, "uColor");
                LightDirection = WasperPreviewGl.GetUniformLocation(program, "uLightDirection");
                Ambient = WasperPreviewGl.GetUniformLocation(program, "uAmbient");
                ShadeStrength = WasperPreviewGl.GetUniformLocation(program, "uShadeStrength");
                ProfileExponent = WasperPreviewGl.GetUniformLocation(program, "uProfileExponent");
            }
        }

        private const string VertexShader = @"#version 330 core
uniform sampler2D uSegments;
uniform mat4 uWorldToClip;
flat out int vSegment;
noperspective out vec2 vNdc;

const vec3 cube[36] = vec3[36](
    vec3(-1,-1,-1), vec3( 1,-1,-1), vec3( 1, 1,-1),
    vec3(-1,-1,-1), vec3( 1, 1,-1), vec3(-1, 1,-1),
    vec3(-1,-1, 1), vec3( 1, 1, 1), vec3( 1,-1, 1),
    vec3(-1,-1, 1), vec3(-1, 1, 1), vec3( 1, 1, 1),
    vec3(-1,-1,-1), vec3(-1,-1, 1), vec3( 1,-1, 1),
    vec3(-1,-1,-1), vec3( 1,-1, 1), vec3( 1,-1,-1),
    vec3(-1, 1,-1), vec3( 1, 1, 1), vec3(-1, 1, 1),
    vec3(-1, 1,-1), vec3( 1, 1,-1), vec3( 1, 1, 1),
    vec3(-1,-1,-1), vec3(-1, 1, 1), vec3(-1,-1, 1),
    vec3(-1,-1,-1), vec3(-1, 1,-1), vec3(-1, 1, 1),
    vec3( 1,-1,-1), vec3( 1,-1, 1), vec3( 1, 1, 1),
    vec3( 1,-1,-1), vec3( 1, 1, 1), vec3( 1, 1,-1)
);

vec4 segmentTexel(int segment, int offset)
{
    return texelFetch(uSegments, ivec2(segment * 6 + offset, 0), 0);
}

void main()
{
    int segment = gl_InstanceID;
    vec4 t0 = segmentTexel(segment, 0);
    vec4 t1 = segmentTexel(segment, 1);
    vec4 t2 = segmentTexel(segment, 2);
    vec4 t3 = segmentTexel(segment, 3);
    vec4 t4 = segmentTexel(segment, 4);
    vec4 t5 = segmentTexel(segment, 5);

    vec3 local = cube[gl_VertexID];
    bool atA = local.z < 0.0;
    vec3 base = atA ? t0.xyz : t1.xyz;
    vec3 w = normalize(atA ? t2.xyz : t4.xyz);
    vec3 h = normalize(atA ? t3.xyz : t5.xyz);
    vec3 n = normalize(cross(h, w));
    float hw = atA ? t0.w : t1.w;
    float hh = atA ? t2.w : t3.w;
    float ext = t4.w + 0.1 * max(hw, hh);

    vec3 center = base + n * (atA ? -ext : ext);
    vec3 world = center + w * (local.x * hw * 1.2) + h * (local.y * hh * 1.2);
    vec4 clip = uWorldToClip * vec4(world, 1.0);
    gl_Position = clip;
    vNdc = clip.xy / clip.w;
    vSegment = segment;
}";

        // The bead is a swept superellipse with a shared display-controlled
        // exponent: per-endpoint centers, frames, and extents
        // are blended between the two boundary section planes, so consecutive
        // segments share their boundary section exactly (no joint creases).
        // Ellipsoid caps exist only where flagged (open stroke ends).
        private const string FragmentShader = @"#version 330 core
uniform sampler2D uSegments;
uniform mat4 uClipToWorld;
uniform mat4 uWorldToClip;
uniform vec3 uColor;
uniform vec3 uLightDirection;
uniform float uAmbient;
uniform float uShadeStrength;
uniform float uProfileExponent;
flat in int vSegment;
noperspective in vec2 vNdc;
out vec4 fragColor;

vec3 gA; vec3 gB; vec3 gWA; vec3 gHA; vec3 gWB; vec3 gHB; vec3 gNA; vec3 gNB;
float gHwA; float gHwB; float gHhA; float gHhB; float gCap;
bool gCapA; bool gCapB;

float superellipse(float value)
{
    return pow(abs(value), uProfileExponent);
}

float superellipseGradient(float value)
{
    float magnitude = pow(abs(value), uProfileExponent - 1.0);
    return value < 0.0 ? -magnitude : magnitude;
}

vec4 segmentTexel(int segment, int offset)
{
    return texelFetch(uSegments, ivec2(segment * 6 + offset, 0), 0);
}

vec3 worldPoint(vec2 ndc, float z)
{
    vec4 world = uClipToWorld * vec4(ndc, z, 1.0);
    return world.xyz / world.w;
}

// Clips a ray interval to origin + slope*t >= 0. Internal segment ends use
// the same tangent-bisector plane as their neighbour, so clipping both pieces
// to opposite sides removes overlap ridges while preserving one shared bead
// section at the joint.
bool clipPositiveHalfSpace(
    float origin,
    float slope,
    inout float tEnter,
    inout float tExit,
    inout bool entryWasClipped)
{
    const float ParallelEpsilon = 1e-7;
    if (abs(slope) < ParallelEpsilon)
        return origin >= 0.0;

    float crossing = -origin / slope;
    if (slope > 0.0)
    {
        if (crossing > tEnter)
        {
            tEnter = crossing;
            entryWasClipped = true;
        }
    }
    else
    {
        tExit = min(tExit, crossing);
    }
    return tExit > tEnter;
}

// Implicit bead field: negative inside. Between the boundary planes the
// section center, frame, and semi-axes are blended by the relative distance
// to the two planes; beyond a plane the field continues into an ellipsoid
// cap when flagged. Internal ends continue the endpoint ellipse into the
// neighboring segment's overlap zone; returning a positive constant there
// would create a false zero crossing and render a disk at every path sample.
float beadField(vec3 p)
{
    float dA = dot(p - gA, gNA);
    float dB = dot(gB - p, gNB);
    if (dA < 0.0)
    {
        vec3 q = p - gA;
        float x = dot(q, gWA) / gHwA;
        float y = dot(q, gHA) / gHhA;
        float radial = superellipse(x) + superellipse(y);
        if (!gCapA) return radial - 1.0;
        float z = dA / gCap;
        return radial + superellipse(z) - 1.0;
    }
    if (dB < 0.0)
    {
        vec3 q = p - gB;
        float x = dot(q, gWB) / gHwB;
        float y = dot(q, gHB) / gHhB;
        float radial = superellipse(x) + superellipse(y);
        if (!gCapB) return radial - 1.0;
        float z = dB / gCap;
        return radial + superellipse(z) - 1.0;
    }
    float s = dA / max(dA + dB, 1e-9);
    vec3 c = mix(gA, gB, s);
    vec3 w = normalize(mix(gWA, gWB, s));
    vec3 h = normalize(mix(gHA, gHB, s));
    float hw = mix(gHwA, gHwB, s);
    float hh = mix(gHhA, gHhB, s);
    vec3 q = p - c;
    float x = dot(q, w) / hw;
    float y = dot(q, h) / hh;
    return superellipse(x) + superellipse(y) - 1.0;
}

vec3 beadNormal(vec3 p)
{
    float dA = dot(p - gA, gNA);
    float dB = dot(gB - p, gNB);
    if (dA < 0.0)
    {
        vec3 q = p - gA;
        float x = dot(q, gWA) / gHwA;
        float y = dot(q, gHA) / gHhA;
        vec3 n = gWA * (superellipseGradient(x) / gHwA)
            + gHA * (superellipseGradient(y) / gHhA);
        if (gCapA)
        {
            float z = dA / gCap;
            n += gNA * (superellipseGradient(z) / gCap);
        }
        return normalize(n);
    }
    if (dB < 0.0)
    {
        vec3 q = p - gB;
        float x = dot(q, gWB) / gHwB;
        float y = dot(q, gHB) / gHhB;
        vec3 n = gWB * (superellipseGradient(x) / gHwB)
            + gHB * (superellipseGradient(y) / gHhB);
        if (gCapB)
        {
            float z = dB / gCap;
            n -= gNB * (superellipseGradient(z) / gCap);
        }
        return normalize(n);
    }

    float s = clamp(dA / max(dA + dB, 1e-9), 0.0, 1.0);
    vec3 c = mix(gA, gB, s);
    vec3 w = normalize(mix(gWA, gWB, s));
    vec3 h = mix(gHA, gHB, s);
    h = normalize(h - w * dot(h, w));
    float hw = mix(gHwA, gHwB, s);
    float hh = mix(gHhA, gHhB, s);
    vec3 q = p - c;
    float x = dot(q, w) / hw;
    float y = dot(q, h) / hh;
    return normalize(
        w * (superellipseGradient(x) / hw)
        + h * (superellipseGradient(y) / hh));
}

void main()
{
    vec4 t0 = segmentTexel(vSegment, 0);
    vec4 t1 = segmentTexel(vSegment, 1);
    vec4 t2 = segmentTexel(vSegment, 2);
    vec4 t3 = segmentTexel(vSegment, 3);
    vec4 t4 = segmentTexel(vSegment, 4);
    vec4 t5 = segmentTexel(vSegment, 5);

    gA = t0.xyz;  gHwA = max(t0.w, 1e-6);
    gB = t1.xyz;  gHwB = max(t1.w, 1e-6);
    gWA = normalize(t2.xyz);  gHhA = max(t2.w, 1e-6);
    gHA = normalize(t3.xyz);  gHhB = max(t3.w, 1e-6);
    gWB = normalize(t4.xyz);  gCap = max(t4.w, 1e-6);
    gHB = normalize(t5.xyz);
    float flags = t5.w;
    gCapA = mod(flags, 2.0) >= 1.0;
    gCapB = flags >= 2.0;
    gNA = normalize(cross(gHA, gWA));
    gNB = normalize(cross(gHB, gWB));

    vec3 ro = worldPoint(vNdc, -1.0);
    vec3 rd = normalize(worldPoint(vNdc, 1.0) - ro);

    // Bounding sphere around the segment limits the march interval.
    vec3 mid = 0.5 * (gA + gB);
    float radius = 0.5 * length(gB - gA)
        + 1.25 * max(max(gHwA, gHwB), max(gHhA, gHhB)) + gCap;
    vec3 oc = ro - mid;
    float qb = dot(oc, rd);
    float qc = dot(oc, oc) - radius * radius;
    float disc = qb * qb - qc;
    if (disc < 0.0) discard;
    float root = sqrt(disc);
    float tEnter = max(-qb - root, 0.0);
    float tExit = -qb + root;
    if (tExit <= tEnter) discard;

    // Open stroke ends retain their ellipsoid caps. Internal ends are clipped
    // near their shared section planes. A short bead-size-dependent overlap
    // lets both neighbours cover curved joints without restoring the unlimited
    // extensions that caused the original diagonal ridges.
    bool entryWasClipped = false;
    float jointOverlap = 0.20 * max(
        max(gHwA, gHwB),
        max(gHhA, gHhB));
    if (!gCapA && !clipPositiveHalfSpace(
            dot(ro - gA, gNA) + jointOverlap,
            dot(rd, gNA),
            tEnter,
            tExit,
            entryWasClipped))
        discard;
    if (!gCapB && !clipPositiveHalfSpace(
            dot(gB - ro, gNB) + jointOverlap,
            -dot(rd, gNB),
            tEnter,
            tExit,
            entryWasClipped))
        discard;

    const int Steps = 32;
    float dt = (tExit - tEnter) / float(Steps);
    float prevT = tEnter;
    float prevF = beadField(ro + rd * tEnter);
    // Entering through an artificial joint plane is not a visible surface.
    // Its neighbouring segment owns the continuous exterior hit.
    float hitT = prevF < 0.0 && !entryWasClipped ? tEnter : -1.0;

    for (int i = 1; i <= Steps && hitT < 0.0; i++)
    {
        float t = tEnter + dt * float(i);
        float f = beadField(ro + rd * t);
        if (prevF > 0.0 && f <= 0.0)
        {
            float lo = prevT;
            float hi = t;
            for (int j = 0; j < 8; j++)
            {
                float m = 0.5 * (lo + hi);
                if (beadField(ro + rd * m) <= 0.0) hi = m;
                else lo = m;
            }
            hitT = 0.5 * (lo + hi);
            break;
        }
        prevT = t;
        prevF = f;
    }
    if (hitT < 0.0) discard;

    vec3 hit = ro + rd * hitT;
    vec3 normal = beadNormal(hit);
    if (dot(normal, normal) < 1e-18) normal = -rd;
    normal = normalize(normal);
    if (dot(normal, rd) > 0.0) normal = -normal;

    vec3 lightDirection = normalize(uLightDirection);
    float diffuse = max(dot(normal, lightDirection), 0.0);
    vec3 halfDirection = normalize(lightDirection - rd);
    float specular = pow(max(dot(normal, halfDirection), 0.0), 32.0);
    vec3 shaded = uColor * clamp(uAmbient + uShadeStrength * diffuse, 0.0, 1.0)
        + vec3(0.18 * specular);
    fragColor = vec4(shaded, 1.0);
    vec4 clip = uWorldToClip * vec4(hit, 1.0);
    gl_FragDepth = 0.5 * (clip.z / clip.w) + 0.5;
}";
    }
}
