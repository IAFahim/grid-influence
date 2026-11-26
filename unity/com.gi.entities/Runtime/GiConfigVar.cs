using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Unity.Burst;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Assemblies;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Gi.Entities
{
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class GiConfigVarAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }
        public object DefaultValue { get; }

        public GiConfigVarAttribute(string name, bool defaultValue, string description)
        {
            Name = name;
            DefaultValue = defaultValue;
            Description = description;
        }

        public GiConfigVarAttribute(string name, int defaultValue, string description)
        {
            Name = name;
            DefaultValue = defaultValue;
            Description = description;
        }

        public GiConfigVarAttribute(string name, float defaultValue, string description)
        {
            Name = name;
            DefaultValue = defaultValue;
            Description = description;
        }

        public GiConfigVarAttribute(string name, float r, float g, float b, float a, string description)
        {
            Name = name;
            DefaultValue = new Color(r, g, b, a);
            Description = description;
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class GiConfigurableAttribute : Attribute
    {
    }

    [NoAutoStaticsCleanup]
    public static partial class GiConfigVarManager
    {
        private static bool _initialized;

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void InitializeEditor()
        {
            Initialize();
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InitializeRuntime()
        {
            Initialize();
        }

        public static List<(GiConfigVarAttribute ConfigVar, FieldInfo Field)> FindAll()
        {
            var found = new List<(GiConfigVarAttribute, FieldInfo)>();
#if UNITY_EDITOR
            foreach (var field in TypeCache.GetFieldsWithAttribute<GiConfigVarAttribute>())
            {
                var attribute = field.GetCustomAttribute<GiConfigVarAttribute>();
                if (attribute != null) found.Add((attribute, field));
            }
#else
            foreach (var assembly in CurrentAssemblies.GetLoadedAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name == null || !name.StartsWith("Gi.", StringComparison.Ordinal)) continue;

                foreach (var type in assembly.GetTypes())
                {
                    if (type.GetCustomAttribute<GiConfigurableAttribute>() == null) continue;
                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        var attribute = field.GetCustomAttribute<GiConfigVarAttribute>();
                        if (attribute != null) found.Add((attribute, field));
                    }
                }
            }
#endif
            return found;
        }

        public static void EnsureInitialized()
        {
            if (!_initialized) Initialize();
        }

        public static void Initialize(string[] args = null)
        {
            _initialized = true;
            var entries = FindAll();
            var byName = new Dictionary<string, (GiConfigVarAttribute Attribute, FieldInfo Field)>(entries.Count, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                ApplyValue(entry.Field, entry.ConfigVar.DefaultValue);
                byName[entry.ConfigVar.Name] = entry;
            }

            var commandLine = args ?? Environment.GetCommandLineArgs();
            for (var i = 0; i < commandLine.Length; i++)
            {
                var token = commandLine[i];
                if (string.IsNullOrEmpty(token) || token[0] != '-') continue;

                var key = token.StartsWith("--", StringComparison.Ordinal) ? token.Substring(2) : token.Substring(1);
                var eq = key.IndexOf('=');
                string rawValue = null;
                if (eq > 0)
                {
                    rawValue = key.Substring(eq + 1);
                    key = key.Substring(0, eq);
                }

                if (!byName.TryGetValue(key, out var target)) continue;

                if (rawValue == null && i + 1 < commandLine.Length) rawValue = commandLine[++i];
                if (rawValue != null && TryParse(rawValue, target.Attribute.DefaultValue, out var parsed))
                    ApplyValue(target.Field, parsed);
            }
        }

        private static bool TryParse(string raw, object sample, out object value)
        {
            if (sample is bool)
            {
                if (bool.TryParse(raw, out var b))
                {
                    value = b;
                    return true;
                }

                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bi))
                {
                    value = bi != 0;
                    return true;
                }

                value = null;
                return false;
            }

            if (sample is int)
            {
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    value = n;
                    return true;
                }

                value = null;
                return false;
            }

            if (sample is float)
            {
                if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                {
                    value = f;
                    return true;
                }

                value = null;
                return false;
            }

            value = null;
            return false;
        }

        private static void ApplyValue(FieldInfo field, object value)
        {
            var holder = field.GetValue(null);
            if (holder is SharedStatic<bool> b && value is bool bv) b.Data = bv;
            else if (holder is SharedStatic<int> n && value is int nv) n.Data = nv;
            else if (holder is SharedStatic<float> f && value is float fv) f.Data = fv;
            else if (holder is SharedStatic<Color> c && value is Color cv) c.Data = cv;
        }
    }
}
