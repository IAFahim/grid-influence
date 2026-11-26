using Unity.Entities;

namespace Gi.Entities
{
    public interface IGiEntityCommands
    {
        void AddComponent<T>() where T : unmanaged, IComponentData;

        void AddComponent<T>(in T component) where T : unmanaged, IComponentData;

        void SetComponentEnabled<T>(bool enabled) where T : unmanaged, IEnableableComponent;
    }

    public struct GiEntityManagerCommands : IGiEntityCommands
    {
        private EntityManager _manager;
        private readonly Entity _entity;

        public GiEntityManagerCommands(EntityManager manager, Entity entity)
        {
            _manager = manager;
            _entity = entity;
        }

        public void AddComponent<T>() where T : unmanaged, IComponentData
        {
            _manager.AddComponent<T>(_entity);
        }

        public void AddComponent<T>(in T component) where T : unmanaged, IComponentData
        {
            _manager.AddComponentData(_entity, component);
        }

        public void SetComponentEnabled<T>(bool enabled) where T : unmanaged, IEnableableComponent
        {
            _manager.SetComponentEnabled<T>(_entity, enabled);
        }
    }
}
