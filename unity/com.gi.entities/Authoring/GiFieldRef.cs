using System;
using System.Reflection;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Gi.Entities.Authoring
{
    [Serializable]
    public struct GiFieldRef
    {
        public string Type;
        public string Field;

        public bool IsSet => !string.IsNullOrEmpty(Type);

        public static GiFieldRef Of(Type type, string field = null)
        {
            return new GiFieldRef { Type = type.AssemblyQualifiedName, Field = field ?? string.Empty };
        }

        public bool TryResolve(out GiBindingField resolved, out string error)
        {
            resolved = default;
            error = null;
            if (!IsSet) return true;

            var type = System.Type.GetType(Type);
            if (type == null)
            {
                error = "The component type '" + Type + "' was not found.";
                return false;
            }

            var index = TypeManager.GetTypeIndex(type);
            if (index == TypeIndex.Null || !typeof(IComponentData).IsAssignableFrom(type))
            {
                error = "'" + type.Name + "' is not an unmanaged component type.";
                return false;
            }

            resolved.TypeHash = TypeManager.GetTypeInfo(index).StableTypeHash;
            if (string.IsNullOrEmpty(Field)) return true;

            var info = type.GetField(Field, BindingFlags.Public | BindingFlags.Instance);
            if (info == null)
            {
                error = "'" + type.Name + "' has no public field '" + Field + "'.";
                return false;
            }

            resolved.Offset = (ushort)UnsafeUtility.GetFieldOffset(info);
            resolved.Size = (ushort)UnsafeUtility.SizeOf(info.FieldType);
            return true;
        }
    }
}
