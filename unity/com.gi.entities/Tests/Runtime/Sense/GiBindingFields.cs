using System.Reflection;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace Gi.Entities.Tests
{
    public struct ProbeSample : IComponentData
    {
        public short Value;
        public float2 Gradient;
    }

    public struct ProbeGated : IComponentData, IEnableableComponent
    {
        public short Value;
    }

    public struct ProbeCovered : IComponentData, IEnableableComponent
    {
    }

    public struct ProbeNote : IComponentData
    {
        public int Mark;
    }

    public static class GiBindingFields
    {
        public static GiBindingField Of<T>(string field)
            where T : unmanaged, IComponentData
        {
            var info = typeof(T).GetField(field, BindingFlags.Public | BindingFlags.Instance);
            return new GiBindingField
            {
                TypeHash = Hash<T>(),
                Offset = (ushort)UnsafeUtility.GetFieldOffset(info),
                Size = (ushort)UnsafeUtility.SizeOf(info.FieldType),
            };
        }

        public static GiBindingField Tag<T>()
            where T : unmanaged, IComponentData
        {
            return new GiBindingField { TypeHash = Hash<T>() };
        }

        public static ulong Hash<T>()
            where T : unmanaged, IComponentData
        {
            return TypeManager.GetTypeInfo(TypeManager.GetTypeIndex<T>()).StableTypeHash;
        }
    }
}
