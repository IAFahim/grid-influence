using System.Collections.Generic;
using BovineLabs.Core.Authoring.EntityCommands;
using Unity.Entities;

namespace Gi.Entities.Authoring
{
    public sealed class GiSplatBaker : Baker<GiSplatAuthoring>
    {
        public override void Bake(GiSplatAuthoring authoring)
        {
            DependsOn(authoring.Profile);
            DependsOn(authoring.Layer);
            DependsOn(authoring.Stamp);

            var errors = new List<string>();
            if (!GiSourceResolve.Splat(authoring, out var settings, errors))
            {
                GiBakerLog.Errors(authoring, "splat", errors);
                return;
            }

            var entity = GetEntity(authoring.Moves ? TransformUsageFlags.Dynamic : TransformUsageFlags.Renderable);
            var commands = new BakerCommands(this, entity);
            GiSplatBuilder.Apply(ref commands, settings);
        }
    }
}
