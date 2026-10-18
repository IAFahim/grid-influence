using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Gi.Entities.Samples.Stealth
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(GiSystemGroup))]
    [BurstCompile]
    public partial struct StealthGuardSystem : ISystem
    {
        private EntityQuery _guards;
        private ComponentLookup<StealthAlert> _alerts;

        public void OnCreate(ref SystemState state)
        {
            _guards = state.GetEntityQuery(ComponentType.ReadWrite<StealthGuard>(), ComponentType.ReadWrite<LocalTransform>(),
                ComponentType.ReadOnly<StealthAlert>());
            state.RequireForUpdate(_guards);
            _alerts = state.GetComponentLookup<StealthAlert>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _alerts.Update(ref state);
            state.Dependency = new StealthGuardJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                Time = (float)SystemAPI.Time.ElapsedTime,
                Alerts = _alerts,
            }.Schedule(_guards, state.Dependency);
        }

        [BurstCompile]
        private partial struct StealthGuardJob : IJobEntity
        {
            public float DeltaTime;
            public float Time;
            public ComponentLookup<StealthAlert> Alerts;

            private void Execute(Entity entity, ref StealthGuard guard, ref LocalTransform transform)
            {
                if (Alerts.IsComponentEnabled(entity))
                {
                    var toNoise = guard.Where - new float2(transform.Position.x, transform.Position.z);
                    if (math.lengthsq(toNoise) > 1e-4f)
                    {
                        var heading = math.atan2(toNoise.y, toNoise.x);
                        transform.Rotation = quaternion.RotateY(math.PI * 0.5f - heading);
                    }

                    return;
                }

                var sweep = guard.SweepPhase + Time * math.radians(guard.SweepRate);
                transform.Rotation = quaternion.RotateY(math.PI * 0.5f - sweep);
            }
        }
    }
}
