// WASPer_DendroBridge.cs
// Optional runtime bridge between WASPer fields and Dendro (OpenVDB) volumes.
//
// Dendro (DendroGH / DendroAPI) does not expose a native "evaluate the grid
// at an arbitrary point" function -- only meshing, CSG, filtering and
// closest-point queries. So unlike the Isopod bridge (which forwards a live
// double ValueAt(Point3d) delegate), this bridge round-trips through a mesh:
//
//   Dendro volume -> DendroVolume.Display mesh -> WasperField.FromMesh(...)
//   WASPer field  -> marching-cubes mesh        -> new DendroVolume(mesh, settings)
//
// All Dendro types are resolved purely by reflection so WASPer_3DP has no
// compile-time dependency on DendroGH.dll; Dendro just needs to be loaded in
// the current Grasshopper session.
//
// Assembly-identity note: Grasshopper can end up with more than one loaded
// copy of DendroGH.dll (e.g. a Yak-installed release alongside a dev build).
// .NET reflection treats same-named types from different assembly copies as
// distinct Types, so constructor/method lookups must always be resolved
// against the SAME assembly that produced the actual live object in hand
// (never via an independent AppDomain-wide scan) -- otherwise a perfectly
// real "(Mesh, DendroSettings)" constructor can appear to not exist.

using System;
using System.Reflection;

using Grasshopper.Kernel.Types;

using Rhino.Geometry;

namespace WASPer_3DP
{
    internal static class WasperDendroBridge
    {
        private const string VolumeTypeName    = "DendroGH.DendroVolume";
        private const string SettingsTypeName  = "DendroGH.DendroSettings";
        private const string VolumeGooTypeName = "DendroGH.VolumeGOO";

        internal static object Unwrap(object value)
        {
            object current = value;

            for (int depth = 0; depth < 6 && current is IGH_Goo goo; depth++)
            {
                if (goo is GH_ObjectWrapper wrapper && wrapper.Value != null)
                {
                    current = wrapper.Value;
                    continue;
                }

                object scriptValue;
                try { scriptValue = goo.ScriptVariable(); }
                catch { scriptValue = null; }

                if (scriptValue == null || ReferenceEquals(scriptValue, current))
                    break;

                current = scriptValue;
            }

            return current;
        }

        /// <summary>Resolve and validate a Dendro volume from an arbitrary (possibly wrapped) input.</summary>
        internal static bool TryGetVolume(object source, out object volume, out string sourceType, out string error)
        {
            volume = null;
            sourceType = "";
            error = "";

            object candidate = Unwrap(source);
            if (candidate == null)
            {
                error = "The Dendro volume input is null.";
                return false;
            }

            Type candidateType = candidate.GetType();
            sourceType = candidateType.FullName ?? candidateType.Name;

            if (!IsTypeOrBaseType(candidateType, VolumeTypeName))
            {
                error = $"Input type '{sourceType}' is not a Dendro Volume ({VolumeTypeName}).";
                return false;
            }

            PropertyInfo isValid = candidateType.GetProperty("IsValid", BindingFlags.Instance | BindingFlags.Public);
            if (isValid != null && isValid.PropertyType == typeof(bool))
            {
                bool valid = (bool)isValid.GetValue(candidate);
                if (!valid)
                {
                    error = "The Dendro volume is not valid (empty or disposed grid).";
                    return false;
                }
            }

            volume = candidate;
            return true;
        }

        /// <summary>Resolve an optional Dendro settings object, or construct a default DendroSettings if none was supplied.</summary>
        internal static bool TryGetOrCreateSettings(object source, out object settings, out string error)
            => TryGetOrCreateSettings(source, null, out settings, out error);

        /// <summary>
        /// Same as <see cref="TryGetOrCreateSettings(object, out object, out string)"/>, but when a default
        /// DendroSettings has to be constructed, <paramref name="preferredAssembly"/> (typically the assembly of
        /// a Dendro object already in hand, e.g. a wired-in volume) is tried first so the created settings stay
        /// in the same loaded copy of Dendro as everything else in the component.
        /// </summary>
        internal static bool TryGetOrCreateSettings(object source, Assembly preferredAssembly, out object settings, out string error)
        {
            settings = null;
            error = "";

            object candidate = Unwrap(source);
            if (candidate != null)
            {
                Type candidateType = candidate.GetType();
                if (IsTypeOrBaseType(candidateType, SettingsTypeName))
                {
                    settings = candidate;
                    return true;
                }

                error = $"Input type '{candidateType.FullName}' is not Dendro Settings ({SettingsTypeName}).";
                return false;
            }

            Type settingsType = preferredAssembly?.GetType(SettingsTypeName, false, false)
                ?? FindLoadedType(SettingsTypeName);

            if (settingsType == null)
            {
                error = "Dendro is not loaded. Install/load Dendro in Grasshopper, then recompute this component.";
                return false;
            }

            try
            {
                settings = Activator.CreateInstance(settingsType);
                return settings != null;
            }
            catch (Exception ex)
            {
                error = "Could not create default Dendro settings: " + ex.Message;
                return false;
            }
        }

        /// <summary>Reads a public double property (e.g. VoxelSize) off a Dendro settings/volume object, if present.</summary>
        internal static double GetDouble(object source, string propertyName, double fallback)
        {
            if (source == null) return fallback;

            PropertyInfo property = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || property.PropertyType != typeof(double)) return fallback;

            try { return (double)property.GetValue(source); }
            catch { return fallback; }
        }

        /// <summary>Writes a public double property on a reflected optional-dependency object.</summary>
        internal static bool TrySetDouble(object source, string propertyName, double value, out string error)
        {
            error = "";
            if (source == null)
            {
                error = $"Cannot set {propertyName}: settings object is null.";
                return false;
            }

            PropertyInfo property = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || property.PropertyType != typeof(double) || !property.CanWrite)
            {
                error = $"Loaded settings type '{source.GetType().FullName}' has no writable double property '{propertyName}'.";
                return false;
            }

            try
            {
                property.SetValue(source, value);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not set {propertyName}: " + (ex.InnerException?.Message ?? ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Reads a Dendro volume's existing Display mesh WITHOUT triggering a remesh (no UpdateDisplay call).
        /// Use this when no explicit settings are connected, so an already-meshed volume (e.g. one just built
        /// by another Dendro component upstream) isn't paid for twice.
        /// </summary>
        internal static bool TryGetExistingDisplayMesh(object volume, out Mesh mesh, out string error)
        {
            mesh = null;
            error = "";

            Type volumeType = volume.GetType();
            PropertyInfo display = volumeType.GetProperty("Display", BindingFlags.Instance | BindingFlags.Public);
            object meshObj = display?.GetValue(volume);

            mesh = meshObj as Mesh;
            if (mesh == null || mesh.Faces.Count == 0)
            {
                mesh = null;
                error = "Dendro volume has no existing displayable mesh (Display was empty or null).";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Forces a Dendro volume to (re)mesh via its own UpdateDisplay, using the given settings (or Dendro's
        /// parameterless UpdateDisplay() when <paramref name="settings"/> is null), then reads back Display.
        /// Use this when explicit settings are connected (the user wants this exact isovalue/adaptivity/voxel
        /// size/bandwidth), or as a one-time fallback when a volume has no existing Display to reuse.
        /// </summary>
        internal static bool TryRemeshDisplay(object volume, object settings, out Mesh mesh, out string error)
        {
            mesh = null;
            error = "";

            Type volumeType = volume.GetType();

            MethodInfo updateDisplay = settings != null
                ? volumeType.GetMethod(
                    "UpdateDisplay",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { settings.GetType() },
                    null)
                : null;

            try
            {
                if (updateDisplay != null)
                {
                    updateDisplay.Invoke(volume, new[] { settings });
                }
                else
                {
                    MethodInfo parameterless = volumeType.GetMethod(
                        "UpdateDisplay", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    parameterless?.Invoke(volume, null);
                }
            }
            catch (TargetInvocationException ex)
            {
                error = "Dendro meshing (UpdateDisplay) failed: " + (ex.InnerException?.Message ?? ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "Dendro meshing (UpdateDisplay) failed: " + ex.Message;
                return false;
            }

            PropertyInfo display = volumeType.GetProperty("Display", BindingFlags.Instance | BindingFlags.Public);
            object meshObj = display?.GetValue(volume);

            mesh = meshObj as Mesh;
            if (mesh == null)
            {
                error = "Dendro volume produced no displayable mesh (Display was empty or null).";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Builds a new Dendro volume from a mesh via reflection (DendroVolume(Mesh, DendroSettings)).
        /// The DendroVolume type is resolved from <paramref name="settings"/>'s own assembly first, so the
        /// constructor lookup can never straddle two different loaded copies of Dendro.
        /// </summary>
        internal static bool TryCreateVolume(
            Mesh mesh,
            object settings,
            out object volume,
            out string volumeType,
            out string error)
        {
            volume = null;
            volumeType = "";
            error = "";

            if (settings == null)
            {
                error = "No Dendro settings were resolved.";
                return false;
            }

            Type dendroVolumeType = settings.GetType().Assembly.GetType(VolumeTypeName, false, false)
                ?? FindLoadedType(VolumeTypeName);

            if (dendroVolumeType == null)
            {
                error = "Dendro is not loaded. Install/load Dendro in Grasshopper, then recompute this component.";
                return false;
            }

            ConstructorInfo constructor = dendroVolumeType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Mesh), settings.GetType() },
                null);

            if (constructor == null)
            {
                error =
                    $"Loaded Dendro type '{dendroVolumeType.FullName}' (from {dendroVolumeType.Assembly.GetName().Name}, " +
                    $"{dendroVolumeType.Assembly.Location}) has no (Mesh, {settings.GetType().Name}) constructor matching the " +
                    $"settings assembly ({settings.GetType().Assembly.GetName().Name}, {settings.GetType().Assembly.Location}). " +
                    "This usually means two different copies of Dendro are loaded at once - check for duplicate DendroGH installs.";
                return false;
            }

            try
            {
                object created = constructor.Invoke(new object[] { mesh, settings });
                if (created == null)
                {
                    error = "Dendro volume construction returned null.";
                    return false;
                }

                PropertyInfo isValid = dendroVolumeType.GetProperty("IsValid", BindingFlags.Instance | BindingFlags.Public);
                if (isValid != null && isValid.PropertyType == typeof(bool) && !(bool)isValid.GetValue(created))
                {
                    error = "Dendro volume conversion failed (mesh may not be valid, or settings are unsuitable).";
                    return false;
                }

                volume = created;
                volumeType = dendroVolumeType.FullName ?? dendroVolumeType.Name;
                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = "Dendro volume construction failed: " + (ex.InnerException?.Message ?? ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "Dendro volume construction failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Wraps a Dendro volume in its native VolumeGOO, when available, so it previews and bakes like a
        /// normal Dendro output. Falls back to the raw volume object if VolumeGOO cannot be constructed.
        /// VolumeGOO is resolved from the volume's own assembly first, for the same identity reasons as
        /// <see cref="TryCreateVolume"/>.
        /// </summary>
        internal static object WrapAsGoo(object volume)
        {
            if (volume == null) return null;

            Type volumeGooType = volume.GetType().Assembly.GetType(VolumeGooTypeName, false, false)
                ?? FindLoadedType(VolumeGooTypeName);

            if (volumeGooType == null) return volume;

            ConstructorInfo constructor = volumeGooType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { volume.GetType() },
                null);

            if (constructor == null) return volume;

            try
            {
                return constructor.Invoke(new[] { volume }) ?? volume;
            }
            catch
            {
                return volume;
            }
        }

        private static bool IsTypeOrBaseType(Type type, string fullName)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                if (string.Equals(current.FullName, fullName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(fullName, false, false); }
                catch { type = null; }

                if (type != null)
                    return type;
            }

            return null;
        }
    }
}
