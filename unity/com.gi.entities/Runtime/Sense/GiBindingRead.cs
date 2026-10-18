using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Gi.Entities
{
    public static unsafe class GiBindingRead
    {
        public static bool TryType(in GiBindingField field, out TypeIndex index)
        {
            index = field.TypeHash == 0ul ? TypeIndex.Null : TypeManager.GetTypeIndexFromStableTypeHash(field.TypeHash);
            return index != TypeIndex.Null;
        }

        public static bool Has(EntityManager manager, Entity entity, in GiBindingField field)
        {
            return TryType(field, out var index) && manager.HasComponent(entity, ComponentType.FromTypeIndex(index));
        }

        public static bool TryFloat(EntityManager manager, Entity entity, in GiBindingField field, out float value)
        {
            value = 0f;
            if (field.Size != sizeof(float) || !TryType(field, out var index)) return false;

            var type = ComponentType.FromTypeIndex(index);
            if (!manager.HasComponent(entity, type)) return false;

            var info = TypeManager.GetTypeInfo(index);
            if (info.IsZeroSized || field.Offset + field.Size > info.TypeSize) return false;

            var handle = manager.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(index));
            var storage = manager.GetStorageInfo(entity);
            var bytes = storage.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref handle, info.TypeSize);
            var start = storage.IndexInChunk * info.TypeSize + field.Offset;
            value = *(float*)((byte*)bytes.GetUnsafeReadOnlyPtr() + start);
            return true;
        }
    }
}
