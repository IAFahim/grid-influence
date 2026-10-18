using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace Gi.Entities.Authoring
{
    public sealed class GiProfileBaker : Baker<GiProfileAuthoring>
    {
        public override void Bake(GiProfileAuthoring authoring)
        {
            var profile = authoring.Profile;
            if (profile == null)
            {
                GiBakerLog.Errors(authoring, "profile", new List<string> { "No profile is assigned." });
                return;
            }

            DependsOn(profile);
            foreach (var layer in profile.Layers) DependsOn(layer);
            foreach (var stamp in profile.Stamps) DependsOn(stamp);

            var issues = profile.Check();
            if (issues.Count > 0)
            {
                var errors = new List<string>();
                foreach (var issue in issues) errors.Add(issue.ToString());
                GiBakerLog.Errors(authoring, "profile", errors);
                return;
            }

            var blob = GiProfileBlobBuilder.Create(profile.ToSpec(), Allocator.Persistent);
            AddBlobAsset(ref blob, out _);
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new GiProfileReference { Value = blob });
        }
    }
}
