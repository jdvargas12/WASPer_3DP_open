// WASPer_FieldPreviewRenderer.cs
// GPU viewport preview for sampled WASPer scalar fields.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using Rhino.Display;
using Rhino.Geometry;

namespace WASPer_3DP
{
    internal sealed class WasperFieldPreviewGrid
    {
        internal readonly Box Box;
        internal readonly int NX;
        internal readonly int NY;
        internal readonly int NZ;
        internal readonly float[] Values;
        internal readonly float Minimum;
        internal readonly float Maximum;
        internal readonly double Resolution;
        internal readonly Transform GridToWorld;
        internal readonly Transform WorldToGrid;

        internal WasperFieldPreviewGrid(
            Box box,
            int nx,
            int ny,
            int nz,
            float[] values,
            float minimum,
            float maximum,
            double resolution)
        {
            Box = box;
            NX = nx;
            NY = ny;
            NZ = nz;
            Values = values;
            Minimum = minimum;
            Maximum = maximum;
            Resolution = resolution;
            GridToWorld = BuildGridToWorld(box);
            GridToWorld.TryGetInverse(out WorldToGrid);
        }

        internal bool BracketsZero => Minimum <= 0.0f && Maximum >= 0.0f;
        internal long Count => (long)NX * NY * NZ;

        private static Transform BuildGridToWorld(Box box)
        {
            Point3d origin = box.PointAt(0.0, 0.0, 0.0);
            Vector3d x = box.PointAt(1.0, 0.0, 0.0) - origin;
            Vector3d y = box.PointAt(0.0, 1.0, 0.0) - origin;
            Vector3d z = box.PointAt(0.0, 0.0, 1.0) - origin;

            Transform transform = Transform.Identity;
            transform.M00 = x.X; transform.M01 = y.X; transform.M02 = z.X; transform.M03 = origin.X;
            transform.M10 = x.Y; transform.M11 = y.Y; transform.M12 = z.Y; transform.M13 = origin.Y;
            transform.M20 = x.Z; transform.M21 = y.Z; transform.M22 = z.Z; transform.M23 = origin.Z;
            return transform;
        }
    }

    internal sealed class WasperFieldPreviewRenderer : IDisposable
    {
        private const uint GlBlend = 0x0BE2;
        private const uint GlDepthTest = 0x0B71;
        private const uint GlSrcAlpha = 0x0302;
        private const uint GlOneMinusSrcAlpha = 0x0303;
        private const uint GlTriangles = 0x0004;
        private const uint GlTexture0 = 0x84C0;
        private const uint GlActiveTexture = 0x84E0;
        private const uint GlTexture3D = 0x806F;
        private const uint GlTextureBinding3D = 0x806A;
        private const uint GlTextureMinFilter = 0x2801;
        private const uint GlTextureMagFilter = 0x2800;
        private const uint GlTextureWrapS = 0x2802;
        private const uint GlTextureWrapT = 0x2803;
        private const uint GlTextureWrapR = 0x8072;
        private const int GlLinear = 0x2601;
        private const int GlClampToEdge = 0x812F;
        private const int GlR32F = 0x822E;
        private const uint GlRed = 0x1903;
        private const uint GlFloat = 0x1406;
        private const uint GlVertexShader = 0x8B31;
        private const uint GlFragmentShader = 0x8B30;
        private const uint GlCompileStatus = 0x8B81;
        private const uint GlLinkStatus = 0x8B82;
        private const uint GlInfoLogLength = 0x8B84;
        private const uint GlCurrentProgram = 0x8B8D;
        private const uint GlVertexArrayBinding = 0x85B5;
        private const uint GlDepthWritemask = 0x0B72;
        private const uint GlBlendSrcRgb = 0x80C9;
        private const uint GlBlendDstRgb = 0x80C8;
        private const uint GlBlendSrcAlpha = 0x80CB;
        private const uint GlBlendDstAlpha = 0x80CA;

        private static readonly object DeleteLock = new object();
        private static readonly Queue<uint> PendingTextureDeletes = new Queue<uint>();
        private static readonly Queue<uint> PendingProgramDeletes = new Queue<uint>();
        private static readonly Queue<uint> PendingVertexArrayDeletes = new Queue<uint>();

        private WasperFieldPreviewGrid _grid;
        private System.Drawing.Color _color = System.Drawing.Color.FromArgb(54, 164, 221);
        private float _opacity = 1.0f;
        private bool _textureDirty = true;
        private uint _texture;
        private uint _program;
        private uint _vertexArray;
        private string _lastError = "";
        private Uniforms _uniforms;

        internal string LastError => _lastError;

        internal void SetGrid(WasperFieldPreviewGrid grid, System.Drawing.Color color, double opacity)
        {
            if (!ReferenceEquals(_grid, grid))
                _textureDirty = true;

            _grid = grid;
            _color = color;
            _opacity = (float)Math.Max(0.0, Math.Min(1.0, opacity));
        }

        internal void Clear()
        {
            _grid = null;
            _textureDirty = true;
        }

        internal bool Draw(DisplayPipeline display)
        {
            if (_grid == null || !_grid.BracketsZero || display == null)
                return false;

            if (!WasperPreviewGl.IsSupported)
            {
                _lastError = "GPU field preview is currently available on Windows only.";
                return false;
            }

            try
            {
                WasperPreviewGl.EnsureInitialized();
                FlushDeletes();
                EnsureProgram();
                EnsureTexture();
                DrawRaymarch(display);
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
                    throw new InvalidOperationException("Field preview shader link failed: " + ProgramLog(_program));

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
            if (!_textureDirty && _texture != 0)
                return;

            WasperPreviewGl.GetIntegerv(GlActiveTexture, out int oldActiveTexture);
            WasperPreviewGl.ActiveTexture(GlTexture0);
            WasperPreviewGl.GetIntegerv(GlTextureBinding3D, out int oldTexture);

            try
            {
                if (_texture != 0)
                {
                    WasperPreviewGl.DeleteTextures(1, ref _texture);
                    _texture = 0;
                }

                WasperPreviewGl.GenTextures(1, out _texture);
                WasperPreviewGl.BindTexture(GlTexture3D, _texture);
                WasperPreviewGl.TexParameteri(GlTexture3D, GlTextureMinFilter, GlLinear);
                WasperPreviewGl.TexParameteri(GlTexture3D, GlTextureMagFilter, GlLinear);
                WasperPreviewGl.TexParameteri(GlTexture3D, GlTextureWrapS, GlClampToEdge);
                WasperPreviewGl.TexParameteri(GlTexture3D, GlTextureWrapT, GlClampToEdge);
                WasperPreviewGl.TexParameteri(GlTexture3D, GlTextureWrapR, GlClampToEdge);

                GCHandle handle = GCHandle.Alloc(_grid.Values, GCHandleType.Pinned);
                try
                {
                    WasperPreviewGl.TexImage3D(
                        GlTexture3D,
                        0,
                        GlR32F,
                        _grid.NX,
                        _grid.NY,
                        _grid.NZ,
                        0,
                        GlRed,
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
                WasperPreviewGl.BindTexture(GlTexture3D, (uint)Math.Max(0, oldTexture));
                WasperPreviewGl.ActiveTexture((uint)oldActiveTexture);
            }
        }

        private void DrawRaymarch(DisplayPipeline display)
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
            bool blendWasEnabled = WasperPreviewGl.IsEnabled(GlBlend);
            bool depthWasEnabled = WasperPreviewGl.IsEnabled(GlDepthTest);
            WasperPreviewGl.GetBooleanv(GlDepthWritemask, out byte oldDepthMask);
            WasperPreviewGl.GetIntegerv(GlBlendSrcRgb, out int oldBlendSrcRgb);
            WasperPreviewGl.GetIntegerv(GlBlendDstRgb, out int oldBlendDstRgb);
            WasperPreviewGl.GetIntegerv(GlBlendSrcAlpha, out int oldBlendSrcAlpha);
            WasperPreviewGl.GetIntegerv(GlBlendDstAlpha, out int oldBlendDstAlpha);

            WasperPreviewGl.ActiveTexture(GlTexture0);
            WasperPreviewGl.GetIntegerv(GlTextureBinding3D, out int oldTexture);

            try
            {
                WasperPreviewGl.Enable(GlDepthTest);
                if (_opacity < 0.999f)
                {
                    WasperPreviewGl.Enable(GlBlend);
                    WasperPreviewGl.BlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);
                    WasperPreviewGl.DepthMask(0);
                }
                else
                {
                    WasperPreviewGl.DepthMask(1);
                }

                WasperPreviewGl.UseProgram(_program);
                WasperPreviewGl.BindVertexArray(_vertexArray);
                WasperPreviewGl.BindTexture(GlTexture3D, _texture);

                SetMatrix(_uniforms.ClipToWorld, clipToWorld);
                SetMatrix(_uniforms.WorldToClip, worldToClip);
                SetMatrix(_uniforms.WorldToGrid, _grid.WorldToGrid);
                WasperPreviewGl.Uniform3f(_uniforms.Dimensions, _grid.NX, _grid.NY, _grid.NZ);
                WasperPreviewGl.Uniform1f(_uniforms.StepSize, (float)Math.Max(_grid.Resolution * 0.5, 1e-6));
                WasperPreviewGl.Uniform3f(
                    _uniforms.Color,
                    _color.R / 255.0f,
                    _color.G / 255.0f,
                    _color.B / 255.0f);
                WasperPreviewGl.Uniform1f(_uniforms.Opacity, _opacity);
                WasperPreviewGl.Uniform1i(_uniforms.FieldTexture, 0);
                WasperPreviewGl.DrawArrays(GlTriangles, 0, 3);
            }
            finally
            {
                WasperPreviewGl.BindTexture(GlTexture3D, (uint)Math.Max(0, oldTexture));
                WasperPreviewGl.BindVertexArray((uint)Math.Max(0, oldVertexArray));
                WasperPreviewGl.UseProgram((uint)Math.Max(0, oldProgram));
                WasperPreviewGl.ActiveTexture((uint)oldActiveTexture);
                WasperPreviewGl.DepthMask(oldDepthMask);
                WasperPreviewGl.BlendFuncSeparate(
                    (uint)oldBlendSrcRgb,
                    (uint)oldBlendDstRgb,
                    (uint)oldBlendSrcAlpha,
                    (uint)oldBlendDstAlpha);

                if (blendWasEnabled) WasperPreviewGl.Enable(GlBlend);
                else WasperPreviewGl.Disable(GlBlend);

                if (depthWasEnabled) WasperPreviewGl.Enable(GlDepthTest);
                else WasperPreviewGl.Disable(GlDepthTest);
            }
        }

        private static uint CompileShader(uint type, string source)
        {
            uint shader = WasperPreviewGl.CreateShader(type);
            WasperPreviewGl.ShaderSource(shader, source);
            WasperPreviewGl.CompileShader(shader);
            WasperPreviewGl.GetShaderiv(shader, GlCompileStatus, out int compiled);
            if (compiled == 0)
            {
                string log = ShaderLog(shader);
                WasperPreviewGl.DeleteShader(shader);
                throw new InvalidOperationException("Field preview shader compile failed: " + log);
            }

            return shader;
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
            while (current.InnerException != null)
                current = current.InnerException;
            return current.Message;
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
            _grid = null;
        }

        private readonly struct Uniforms
        {
            internal readonly int ClipToWorld;
            internal readonly int WorldToClip;
            internal readonly int WorldToGrid;
            internal readonly int Dimensions;
            internal readonly int StepSize;
            internal readonly int Color;
            internal readonly int Opacity;
            internal readonly int FieldTexture;

            internal Uniforms(uint program)
            {
                ClipToWorld = WasperPreviewGl.GetUniformLocation(program, "uClipToWorld");
                WorldToClip = WasperPreviewGl.GetUniformLocation(program, "uWorldToClip");
                WorldToGrid = WasperPreviewGl.GetUniformLocation(program, "uWorldToGrid");
                Dimensions = WasperPreviewGl.GetUniformLocation(program, "uDimensions");
                StepSize = WasperPreviewGl.GetUniformLocation(program, "uStepSize");
                Color = WasperPreviewGl.GetUniformLocation(program, "uColor");
                Opacity = WasperPreviewGl.GetUniformLocation(program, "uOpacity");
                FieldTexture = WasperPreviewGl.GetUniformLocation(program, "uField");
            }
        }

        private const string VertexShader = @"#version 330 core
out vec2 vNdc;
void main()
{
    vec2 p;
    if (gl_VertexID == 0) p = vec2(-1.0, -1.0);
    else if (gl_VertexID == 1) p = vec2(3.0, -1.0);
    else p = vec2(-1.0, 3.0);
    vNdc = p;
    gl_Position = vec4(p, 0.0, 1.0);
}";

        private const string FragmentShader = @"#version 330 core
in vec2 vNdc;
out vec4 fragColor;

uniform sampler3D uField;
uniform mat4 uClipToWorld;
uniform mat4 uWorldToClip;
uniform mat4 uWorldToGrid;
uniform vec3 uDimensions;
uniform float uStepSize;
uniform vec3 uColor;
uniform float uOpacity;

vec3 worldPoint(vec2 ndc, float z)
{
    vec4 p = uClipToWorld * vec4(ndc, z, 1.0);
    return p.xyz / p.w;
}

bool intersectBox(vec3 origin, vec3 direction, out float enterT, out float exitT)
{
    vec3 safeDirection = direction;
    safeDirection.x = abs(safeDirection.x) < 1e-9 ? (safeDirection.x < 0.0 ? -1e-9 : 1e-9) : safeDirection.x;
    safeDirection.y = abs(safeDirection.y) < 1e-9 ? (safeDirection.y < 0.0 ? -1e-9 : 1e-9) : safeDirection.y;
    safeDirection.z = abs(safeDirection.z) < 1e-9 ? (safeDirection.z < 0.0 ? -1e-9 : 1e-9) : safeDirection.z;
    vec3 a = (vec3(0.0) - origin) / safeDirection;
    vec3 b = (vec3(1.0) - origin) / safeDirection;
    vec3 lo = min(a, b);
    vec3 hi = max(a, b);
    enterT = max(max(lo.x, lo.y), lo.z);
    exitT = min(min(hi.x, hi.y), hi.z);
    return exitT >= max(enterT, 0.0);
}

float fieldAtWorld(vec3 world)
{
    vec3 grid = (uWorldToGrid * vec4(world, 1.0)).xyz;
    return texture(uField, grid).r;
}

void main()
{
    vec3 nearWorld = worldPoint(vNdc, -1.0);
    vec3 farWorld = worldPoint(vNdc, 1.0);
    vec3 rayDirection = normalize(farWorld - nearWorld);
    vec3 gridOrigin = (uWorldToGrid * vec4(nearWorld, 1.0)).xyz;
    vec3 gridDirection = (uWorldToGrid * vec4(rayDirection, 0.0)).xyz;

    float enterT;
    float exitT;
    if (!intersectBox(gridOrigin, gridDirection, enterT, exitT)) discard;

    float t = max(enterT, 0.0);
    float previousT = t;
    float previousValue = fieldAtWorld(nearWorld + rayDirection * t);
    bool found = abs(previousValue) < 1e-7;
    float hitT = t;

    for (int i = 0; i < 2048 && !found; i++)
    {
        t += uStepSize;
        if (t > exitT) break;
        float value = fieldAtWorld(nearWorld + rayDirection * t);
        if ((previousValue <= 0.0 && value >= 0.0) || (previousValue >= 0.0 && value <= 0.0))
        {
            float low = previousT;
            float high = t;
            float lowValue = previousValue;
            for (int j = 0; j < 7; j++)
            {
                float middle = 0.5 * (low + high);
                float middleValue = fieldAtWorld(nearWorld + rayDirection * middle);
                if ((lowValue <= 0.0 && middleValue >= 0.0) || (lowValue >= 0.0 && middleValue <= 0.0))
                    high = middle;
                else
                {
                    low = middle;
                    lowValue = middleValue;
                }
            }
            hitT = 0.5 * (low + high);
            found = true;
            break;
        }
        previousT = t;
        previousValue = value;
    }

    if (!found) discard;

    vec3 hitWorld = nearWorld + rayDirection * hitT;
    vec3 hitGrid = (uWorldToGrid * vec4(hitWorld, 1.0)).xyz;
    vec3 texel = 1.0 / max(uDimensions - vec3(1.0), vec3(1.0));
    float gx = (texture(uField, hitGrid + vec3(texel.x, 0.0, 0.0)).r - texture(uField, hitGrid - vec3(texel.x, 0.0, 0.0)).r) / (2.0 * texel.x);
    float gy = (texture(uField, hitGrid + vec3(0.0, texel.y, 0.0)).r - texture(uField, hitGrid - vec3(0.0, texel.y, 0.0)).r) / (2.0 * texel.y);
    float gz = (texture(uField, hitGrid + vec3(0.0, 0.0, texel.z)).r - texture(uField, hitGrid - vec3(0.0, 0.0, texel.z)).r) / (2.0 * texel.z);
    vec3 normal = normalize(transpose(mat3(uWorldToGrid)) * vec3(gx, gy, gz));
    if (dot(normal, rayDirection) > 0.0) normal = -normal;

    vec3 lightDirection = normalize(vec3(0.35, -0.45, 0.82));
    float diffuse = max(dot(normal, lightDirection), 0.0);
    vec3 viewDirection = -rayDirection;
    vec3 halfDirection = normalize(lightDirection + viewDirection);
    float specular = pow(max(dot(normal, halfDirection), 0.0), 32.0);
    vec3 shaded = uColor * (0.28 + 0.67 * diffuse) + vec3(0.25 * specular);
    fragColor = vec4(shaded, uOpacity);

    vec4 clip = uWorldToClip * vec4(hitWorld, 1.0);
    float ndcDepth = clip.z / clip.w;
    gl_FragDepth = 0.5 * ndcDepth + 0.5;
}";
    }

    internal static class WasperPreviewGl
    {
        internal static bool IsSupported =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static bool _initialized;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlTexImage3DDelegate(uint target, int level, int internalFormat, int width, int height, int depth, int border, uint format, uint type, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GlCreateShaderDelegate(uint shaderType);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlShaderSourceDelegate(uint shader, int count, IntPtr strings, IntPtr lengths);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlCompileShaderDelegate(uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGetShaderivDelegate(uint shader, uint parameter, out int value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGetShaderInfoLogDelegate(uint shader, int maxLength, out int length, StringBuilder log);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteShaderDelegate(uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GlCreateProgramDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlAttachShaderDelegate(uint program, uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlLinkProgramDelegate(uint program);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGetProgramivDelegate(uint program, uint parameter, out int value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGetProgramInfoLogDelegate(uint program, int maxLength, out int length, StringBuilder log);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteProgramDelegate(uint program);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUseProgramDelegate(uint program);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GlGetUniformLocationDelegate(uint program, [MarshalAs(UnmanagedType.LPStr)] string name);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform1fDelegate(int location, float value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform1iDelegate(int location, int value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform3fDelegate(int location, float x, float y, float z);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniformMatrix4fvDelegate(int location, int count, byte transpose, IntPtr values);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGenVertexArraysDelegate(int count, out uint arrays);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBindVertexArrayDelegate(uint array);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteVertexArraysDelegate(int count, ref uint arrays);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlActiveTextureDelegate(uint texture);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBlendFuncSeparateDelegate(uint sourceRgb, uint destinationRgb, uint sourceAlpha, uint destinationAlpha);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDrawArraysInstancedDelegate(uint mode, int first, int count, int instanceCount);

        private static GlTexImage3DDelegate _texImage3D;
        private static GlCreateShaderDelegate _createShader;
        private static GlShaderSourceDelegate _shaderSource;
        private static GlCompileShaderDelegate _compileShader;
        private static GlGetShaderivDelegate _getShaderiv;
        private static GlGetShaderInfoLogDelegate _getShaderInfoLog;
        private static GlDeleteShaderDelegate _deleteShader;
        private static GlCreateProgramDelegate _createProgram;
        private static GlAttachShaderDelegate _attachShader;
        private static GlLinkProgramDelegate _linkProgram;
        private static GlGetProgramivDelegate _getProgramiv;
        private static GlGetProgramInfoLogDelegate _getProgramInfoLog;
        private static GlDeleteProgramDelegate _deleteProgram;
        private static GlUseProgramDelegate _useProgram;
        private static GlGetUniformLocationDelegate _getUniformLocation;
        private static GlUniform1fDelegate _uniform1f;
        private static GlUniform1iDelegate _uniform1i;
        private static GlUniform3fDelegate _uniform3f;
        private static GlUniformMatrix4fvDelegate _uniformMatrix4fv;
        private static GlGenVertexArraysDelegate _genVertexArrays;
        private static GlBindVertexArrayDelegate _bindVertexArray;
        private static GlDeleteVertexArraysDelegate _deleteVertexArrays;
        private static GlActiveTextureDelegate _activeTexture;
        private static GlBlendFuncSeparateDelegate _blendFuncSeparate;
        private static GlDrawArraysInstancedDelegate _drawArraysInstanced;

        internal static void EnsureInitialized()
        {
            if (_initialized) return;
            if (WglGetCurrentContext() == IntPtr.Zero)
                throw new InvalidOperationException("No active OpenGL viewport context is available.");

            _texImage3D = Load<GlTexImage3DDelegate>("glTexImage3D");
            _createShader = Load<GlCreateShaderDelegate>("glCreateShader");
            _shaderSource = Load<GlShaderSourceDelegate>("glShaderSource");
            _compileShader = Load<GlCompileShaderDelegate>("glCompileShader");
            _getShaderiv = Load<GlGetShaderivDelegate>("glGetShaderiv");
            _getShaderInfoLog = Load<GlGetShaderInfoLogDelegate>("glGetShaderInfoLog");
            _deleteShader = Load<GlDeleteShaderDelegate>("glDeleteShader");
            _createProgram = Load<GlCreateProgramDelegate>("glCreateProgram");
            _attachShader = Load<GlAttachShaderDelegate>("glAttachShader");
            _linkProgram = Load<GlLinkProgramDelegate>("glLinkProgram");
            _getProgramiv = Load<GlGetProgramivDelegate>("glGetProgramiv");
            _getProgramInfoLog = Load<GlGetProgramInfoLogDelegate>("glGetProgramInfoLog");
            _deleteProgram = Load<GlDeleteProgramDelegate>("glDeleteProgram");
            _useProgram = Load<GlUseProgramDelegate>("glUseProgram");
            _getUniformLocation = Load<GlGetUniformLocationDelegate>("glGetUniformLocation");
            _uniform1f = Load<GlUniform1fDelegate>("glUniform1f");
            _uniform1i = Load<GlUniform1iDelegate>("glUniform1i");
            _uniform3f = Load<GlUniform3fDelegate>("glUniform3f");
            _uniformMatrix4fv = Load<GlUniformMatrix4fvDelegate>("glUniformMatrix4fv");
            _genVertexArrays = Load<GlGenVertexArraysDelegate>("glGenVertexArrays");
            _bindVertexArray = Load<GlBindVertexArrayDelegate>("glBindVertexArray");
            _deleteVertexArrays = Load<GlDeleteVertexArraysDelegate>("glDeleteVertexArrays");
            _activeTexture = Load<GlActiveTextureDelegate>("glActiveTexture");
            _blendFuncSeparate = Load<GlBlendFuncSeparateDelegate>("glBlendFuncSeparate");
            _drawArraysInstanced = Load<GlDrawArraysInstancedDelegate>("glDrawArraysInstanced");
            _initialized = true;
        }

        private static T Load<T>(string name) where T : Delegate
        {
            IntPtr address = WglGetProcAddress(name);
            long value = address.ToInt64();
            if (address == IntPtr.Zero || value == 1 || value == 2 || value == 3 || value == -1)
                throw new InvalidOperationException("OpenGL function is unavailable: " + name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        internal static void TexImage3D(uint target, int level, int internalFormat, int width, int height, int depth, int border, uint format, uint type, IntPtr data) => _texImage3D(target, level, internalFormat, width, height, depth, border, format, type, data);
        internal static uint CreateShader(uint type) => _createShader(type);
        internal static void ShaderSource(uint shader, string source)
        {
            IntPtr sourcePtr = Marshal.StringToHGlobalAnsi(source);
            IntPtr sourceArray = Marshal.AllocHGlobal(IntPtr.Size);
            IntPtr lengthPtr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteIntPtr(sourceArray, sourcePtr);
                Marshal.WriteInt32(lengthPtr, Encoding.ASCII.GetByteCount(source));
                _shaderSource(shader, 1, sourceArray, lengthPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(lengthPtr);
                Marshal.FreeHGlobal(sourceArray);
                Marshal.FreeHGlobal(sourcePtr);
            }
        }
        internal static void CompileShader(uint shader) => _compileShader(shader);
        internal static void GetShaderiv(uint shader, uint parameter, out int value) => _getShaderiv(shader, parameter, out value);
        internal static void GetShaderInfoLog(uint shader, int maxLength, out int length, StringBuilder log) => _getShaderInfoLog(shader, maxLength, out length, log);
        internal static void DeleteShader(uint shader) => _deleteShader(shader);
        internal static uint CreateProgram() => _createProgram();
        internal static void AttachShader(uint program, uint shader) => _attachShader(program, shader);
        internal static void LinkProgram(uint program) => _linkProgram(program);
        internal static void GetProgramiv(uint program, uint parameter, out int value) => _getProgramiv(program, parameter, out value);
        internal static void GetProgramInfoLog(uint program, int maxLength, out int length, StringBuilder log) => _getProgramInfoLog(program, maxLength, out length, log);
        internal static void DeleteProgram(uint program) => _deleteProgram(program);
        internal static void UseProgram(uint program) => _useProgram(program);
        internal static int GetUniformLocation(uint program, string name) => _getUniformLocation(program, name);
        internal static void Uniform1f(int location, float value) => _uniform1f(location, value);
        internal static void Uniform1i(int location, int value) => _uniform1i(location, value);
        internal static void Uniform3f(int location, float x, float y, float z) => _uniform3f(location, x, y, z);
        internal static void UniformMatrix4fv(int location, int count, byte transpose, IntPtr values) => _uniformMatrix4fv(location, count, transpose, values);
        internal static void GenVertexArrays(int count, out uint arrays) => _genVertexArrays(count, out arrays);
        internal static void BindVertexArray(uint array) => _bindVertexArray(array);
        internal static void DeleteVertexArrays(int count, ref uint arrays) => _deleteVertexArrays(count, ref arrays);
        internal static void ActiveTexture(uint texture) => _activeTexture(texture);
        internal static void BlendFuncSeparate(uint sourceRgb, uint destinationRgb, uint sourceAlpha, uint destinationAlpha) => _blendFuncSeparate(sourceRgb, destinationRgb, sourceAlpha, destinationAlpha);
        internal static void DrawArraysInstanced(uint mode, int first, int count, int instanceCount) => _drawArraysInstanced(mode, first, count, instanceCount);

        [DllImport("opengl32.dll", EntryPoint = "wglGetCurrentContext")] private static extern IntPtr WglGetCurrentContext();
        [DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = CharSet.Ansi)] private static extern IntPtr WglGetProcAddress(string name);
        [DllImport("opengl32.dll", EntryPoint = "glGenTextures")] internal static extern void GenTextures(int count, out uint textures);
        [DllImport("opengl32.dll", EntryPoint = "glDeleteTextures")] internal static extern void DeleteTextures(int count, ref uint textures);
        [DllImport("opengl32.dll", EntryPoint = "glBindTexture")] internal static extern void BindTexture(uint target, uint texture);
        [DllImport("opengl32.dll", EntryPoint = "glTexParameteri")] internal static extern void TexParameteri(uint target, uint parameter, int value);
        [DllImport("opengl32.dll", EntryPoint = "glTexImage2D")] internal static extern void TexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, IntPtr data);
        [DllImport("opengl32.dll", EntryPoint = "glEnable")] internal static extern void Enable(uint capability);
        [DllImport("opengl32.dll", EntryPoint = "glDisable")] internal static extern void Disable(uint capability);
        [DllImport("opengl32.dll", EntryPoint = "glIsEnabled")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool IsEnabled(uint capability);
        [DllImport("opengl32.dll", EntryPoint = "glBlendFunc")] internal static extern void BlendFunc(uint source, uint destination);
        [DllImport("opengl32.dll", EntryPoint = "glDepthMask")] internal static extern void DepthMask(byte enabled);
        [DllImport("opengl32.dll", EntryPoint = "glDrawArrays")] internal static extern void DrawArrays(uint mode, int first, int count);
        [DllImport("opengl32.dll", EntryPoint = "glGetIntegerv")] internal static extern void GetIntegerv(uint parameter, out int value);
        [DllImport("opengl32.dll", EntryPoint = "glGetBooleanv")] internal static extern void GetBooleanv(uint parameter, out byte value);
    }
}
