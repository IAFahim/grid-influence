using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Gi.Entities.Editor
{
    public readonly struct GiFieldChoice
    {
        public readonly string Name;
        public readonly int Offset;
        public readonly int Size;

        public GiFieldChoice(string name, int offset, int size)
        {
            Name = name;
            Offset = offset;
            Size = size;
        }

        public override string ToString()
        {
            return Name + " (" + Size + " B at " + Offset + ")";
        }
    }

    public static class GiFieldCatalog
    {
        public static List<Type> ComponentTypes()
        {
            var types = new List<Type>();
            foreach (var type in UnityEditor.TypeCache.GetTypesDerivedFrom<IComponentData>())
            {
                if (!type.IsValueType || type.IsGenericType || type.IsAbstract) continue;
                if (!UnsafeUtility.IsUnmanaged(type)) continue;

                types.Add(type);
            }

            types.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            return types;
        }

        public static List<GiFieldChoice> Fields(Type type)
        {
            var fields = new List<GiFieldChoice>();
            foreach (var info in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!UnsafeUtility.IsUnmanaged(info.FieldType)) continue;

                fields.Add(new GiFieldChoice(info.Name, UnsafeUtility.GetFieldOffset(info), UnsafeUtility.SizeOf(info.FieldType)));
            }

            fields.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : string.CompareOrdinal(a.Name, b.Name));
            return fields;
        }
    }
}
