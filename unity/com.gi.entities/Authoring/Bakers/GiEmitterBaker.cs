using System.Collections.Generic;
using Unity.Entities;

namespace Gi.Entities.Authoring
{
    public sealed class GiEmitterBaker : Baker<GiEmitterAuthoring>
    {
        public override void Bake(GiEmitterAuthoring authoring)
        {
            DependsOn(authoring.Profile);
            DependsOn(authoring.Layer);
            DependsOn(authoring.Stamp);

            var errors = new List<string>();
            if (!GiSourceResolve.Emitter(authoring, out var settings, errors))
            {
                GiBakerLog.Errors(authoring, "emitter", errors);
                return;
            }

            var entity = GetEntity(authoring.Moves ? TransformUsageFlags.Dynamic : TransformUsageFlags.Renderable);
            var commands = new GiBakerCommands(this, entity);
            GiEmitterBuilder.Apply(ref commands, settings);
        }
    }
}
