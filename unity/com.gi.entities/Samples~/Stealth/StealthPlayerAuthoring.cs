using System.Collections.Generic;
using BovineLabs.Core.Authoring.EntityCommands;
using Gi.Entities.Authoring;
using Unity.Entities;
using UnityEngine;

namespace Gi.Entities.Samples.Stealth
{
    public sealed class StealthPlayerAuthoring : MonoBehaviour
    {
        public GiProfile Profile;
        public GiLayerAsset Layer;
        public GiStampAsset Step;
        public GiStampAsset Clash;
        [Range(2f, 16f)] public float Speed = 7f;
        [Range(1, 16)] public int StepGain = 3;
        [Range(1, 16)] public int RunGain = 9;
        public ushort RunEveryTicks = 5;

        public sealed class StealthPlayerBaker : Baker<StealthPlayerAuthoring>
        {
            public override void Bake(StealthPlayerAuthoring authoring)
            {
                DependsOn(authoring.Profile);
                DependsOn(authoring.Layer);
                DependsOn(authoring.Step);
                DependsOn(authoring.Clash);

                var errors = new List<string>();
                if (authoring.Profile == null) errors.Add("No profile is assigned.");
                var layer = authoring.Profile == null ? -1 : authoring.Profile.LayerIndex(authoring.Layer);
                var step = authoring.Profile == null ? -1 : authoring.Profile.StampIndex(authoring.Step);
                var clash = authoring.Profile == null ? -1 : authoring.Profile.StampIndex(authoring.Clash);
                if (layer < 0) errors.Add(authoring.Layer == null ? "No layer is assigned." : "The layer is not in the profile's stack.");
                if (step < 0) errors.Add(authoring.Step == null ? "No step stamp is assigned." : "The step stamp is not in the profile's stamp list.");
                if (clash < 0) errors.Add(authoring.Clash == null ? "No clash stamp is assigned." : "The clash stamp is not in the profile's stamp list.");
                if (errors.Count > 0)
                {
                    foreach (var error in errors) Debug.LogError(error, authoring.gameObject);
                    return;
                }

                var entity = GetEntity(TransformUsageFlags.Dynamic);
                var commands = new BakerCommands(this, entity);
                GiSplatBuilder.Apply(ref commands, new GiSplatSettings
                {
                    Layer = new GiLayerKey((byte)layer),
                    Stamp = new GiStampKey((byte)step),
                    Gain = (sbyte)authoring.StepGain,
                });
                AddComponent(entity, new StealthPlayer
                {
                    Layer = new GiLayerKey((byte)layer),
                    Clash = new GiStampKey((byte)clash),
                    Speed = authoring.Speed,
                    RunGain = (sbyte)authoring.RunGain,
                    RunEveryTicks = authoring.RunEveryTicks == 0 ? (ushort)1 : authoring.RunEveryTicks,
                });
            }
        }
    }

    public struct StealthPlayer : IComponentData
    {
        public GiLayerKey Layer;
        public GiStampKey Clash;
        public float Speed;
        public sbyte RunGain;
        public ushort RunEveryTicks;
    }
}
