using Unity.Entities;

namespace Gi.Entities.Authoring
{
    public readonly struct GiBakerCommands : IGiEntityCommands
    {
        private readonly IBaker _baker;
        private readonly Entity _entity;

        public GiBakerCommands(IBaker baker, Entity entity)
        {
            _baker = baker;
            _entity = entity;
        }

        public void AddComponent<T>() where T : unmanaged, IComponentData
        {
            _baker.AddComponent<T>(_entity);
        }

        public void AddComponent<T>(in T component) where T : unmanaged, IComponentData
        {
            _baker.AddComponent(_entity, component);
        }

        public void SetComponentEnabled<T>(bool enabled) where T : unmanaged, IEnableableComponent
        {
            _baker.SetComponentEnabled<T>(_entity, enabled);
        }
    }
}
