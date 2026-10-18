using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Gi.Entities.Performance
{
    [SetUpFixture]
    public sealed class GiPerfLogFixture
    {
        [OneTimeTearDown]
        public void Flush()
        {
            GiPerfLog.Flush();
        }
    }

    public static class GiPerfLog
    {
        private static readonly SortedDictionary<string, double> Values = new SortedDictionary<string, double>(StringComparer.Ordinal);

        public static string Backend
        {
            get
            {
#if UNITY_EDITOR
                return "editor-coreclr";
#elif ENABLE_IL2CPP
                return "il2cpp-player";
#else
                return "coreclr-player";
#endif
            }
        }

        public static string Host => SystemInfo.processorType.Trim();

        public static int EnvInt(string name, int fallback)
        {
            var text = Environment.GetEnvironmentVariable(name);
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;
        }

        public static void Record(string key, double value)
        {
            Values[key] = value;
        }

        public static string Path()
        {
            var overridePath = Environment.GetEnvironmentVariable("GI_PERF_OUT");
            if (!string.IsNullOrEmpty(overridePath)) return overridePath;

            return System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "Logs", "gi-perf-" + Backend + ".json"));
        }

        public static string ToJson()
        {
            var text = new StringBuilder();
            text.Append("{\n  \"backend\": \"").Append(Backend).Append("\",\n  \"host\": \"").Append(Host.Replace("\"", "'")).Append("\",\n  \"values\": {\n");
            var first = true;
            foreach (var pair in Values)
            {
                if (!first) text.Append(",\n");
                first = false;
                text.Append("    \"").Append(pair.Key).Append("\": ").Append(pair.Value.ToString("F6", CultureInfo.InvariantCulture));
            }

            text.Append("\n  }\n}\n");
            return text.ToString();
        }

        public static void Flush()
        {
            if (Values.Count == 0) return;

            var path = Path();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
        }
    }
}
