// WASPer_IsopodBridge.cs
// Optional runtime bridge between WASPer fields and Isopod fields.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

using Grasshopper.Kernel.Types;

using Rhino.Geometry;

namespace WASPer_3DP
{
    internal static class WasperIsopodBridge
    {
        private static readonly object AdapterLock = new object();
        private static readonly Dictionary<Type, Type> AdapterTypes = new Dictionary<Type, Type>();
        private static int _adapterIndex;

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

        internal static bool TryCreateWasperEvaluator(
            object source,
            out Func<Point3d, double> evaluator,
            out string sourceType,
            out string error)
        {
            evaluator = null;
            sourceType = "";
            error = "";

            object field = Unwrap(source);
            if (field == null)
            {
                error = "The Isopod field input is null.";
                return false;
            }

            Type fieldType = field.GetType();
            sourceType = fieldType.FullName ?? fieldType.Name;

            Type isopodInterface = fieldType.GetInterfaces()
                .FirstOrDefault(type => string.Equals(
                    type.FullName,
                    "Isopod.IField",
                    StringComparison.Ordinal));

            bool isIsopodField = IsTypeOrBaseType(fieldType, "Isopod.Field") || isopodInterface != null;
            if (!isIsopodField)
            {
                error = $"Input type '{sourceType}' is not an Isopod Field/IField.";
                return false;
            }

            MethodInfo valueAt = fieldType.GetMethod(
                "ValueAt",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Point3d) },
                null);

            if (valueAt == null && isopodInterface != null)
            {
                valueAt = isopodInterface.GetMethod(
                    "ValueAt",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(Point3d) },
                    null);
            }

            if (valueAt == null || valueAt.ReturnType != typeof(double))
            {
                error = $"Isopod field type '{sourceType}' does not expose double ValueAt(Point3d).";
                return false;
            }

            try
            {
                evaluator = (Func<Point3d, double>)valueAt.CreateDelegate(
                    typeof(Func<Point3d, double>),
                    field);
            }
            catch
            {
                MethodInfo reflectedMethod = valueAt;
                object capturedField = field;
                evaluator = point =>
                {
                    object result = reflectedMethod.Invoke(capturedField, new object[] { point });
                    return result is double scalar ? scalar : Convert.ToDouble(result);
                };
            }

            return true;
        }

        internal static bool TryCreateIsopodField(
            WasperField source,
            out object isopodField,
            out string isopodType,
            out string error)
        {
            isopodField = null;
            isopodType = "";
            error = "";

            if (source?.Evaluator == null)
            {
                error = "The WASPer field input is null or invalid.";
                return false;
            }

            Type baseFieldType = FindLoadedType("Isopod.Field");
            if (baseFieldType == null)
            {
                error = "Isopod.Field is not loaded. Install/load Isopod in Grasshopper, then recompute this component.";
                return false;
            }

            try
            {
                Type adapterType = GetOrCreateAdapterType(baseFieldType);
                isopodField = Activator.CreateInstance(adapterType, source.Evaluator);
                isopodType = adapterType.FullName ?? adapterType.Name;
                return isopodField != null;
            }
            catch (TargetInvocationException ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
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

        private static Type GetOrCreateAdapterType(Type baseFieldType)
        {
            lock (AdapterLock)
            {
                if (AdapterTypes.TryGetValue(baseFieldType, out Type cached))
                    return cached;

                Type created = BuildAdapterType(baseFieldType);
                AdapterTypes.Add(baseFieldType, created);
                return created;
            }
        }

        private static Type BuildAdapterType(Type baseFieldType)
        {
            ConstructorInfo baseConstructor = baseFieldType
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(constructor =>
                    constructor.GetParameters().Length == 0 &&
                    !constructor.IsPrivate);

            if (baseConstructor == null)
                throw new InvalidOperationException(
                    "The loaded Isopod.Field type has no accessible parameterless constructor.");

            MethodInfo valueAt = baseFieldType.GetMethod(
                "ValueAt",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Point3d) },
                null);

            if (valueAt == null || valueAt.ReturnType != typeof(double) || !valueAt.IsVirtual || valueAt.IsFinal)
                throw new InvalidOperationException(
                    "The loaded Isopod.Field type does not provide an overridable double ValueAt(Point3d) method.");

            var assemblyName = new AssemblyName(
                "WASPer_3DP.Isopod.Dynamic." + (++_adapterIndex));
            AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
                assemblyName,
                AssemblyBuilderAccess.Run);
            ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name);

            TypeBuilder typeBuilder = moduleBuilder.DefineType(
                "WASPer_3DP.Dynamic.IsopodFieldAdapter" + _adapterIndex,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                baseFieldType);

            FieldBuilder evaluatorField = typeBuilder.DefineField(
                "_evaluator",
                typeof(Func<Point3d, double>),
                FieldAttributes.Private | FieldAttributes.InitOnly);

            ConstructorBuilder constructorBuilder = typeBuilder.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard,
                new[] { typeof(Func<Point3d, double>) });

            ILGenerator constructorIl = constructorBuilder.GetILGenerator();
            constructorIl.Emit(OpCodes.Ldarg_0);
            constructorIl.Emit(OpCodes.Call, baseConstructor);
            constructorIl.Emit(OpCodes.Ldarg_0);
            constructorIl.Emit(OpCodes.Ldarg_1);
            constructorIl.Emit(OpCodes.Stfld, evaluatorField);
            constructorIl.Emit(OpCodes.Ret);

            MethodBuilder valueAtBuilder = typeBuilder.DefineMethod(
                "ValueAt",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                typeof(double),
                new[] { typeof(Point3d) });

            ILGenerator valueAtIl = valueAtBuilder.GetILGenerator();
            valueAtIl.Emit(OpCodes.Ldarg_0);
            valueAtIl.Emit(OpCodes.Ldfld, evaluatorField);
            valueAtIl.Emit(OpCodes.Ldarg_1);
            valueAtIl.Emit(OpCodes.Callvirt, typeof(Func<Point3d, double>).GetMethod("Invoke"));
            valueAtIl.Emit(OpCodes.Ret);

            typeBuilder.DefineMethodOverride(valueAtBuilder, valueAt);
            return typeBuilder.CreateType();
        }
    }
}
