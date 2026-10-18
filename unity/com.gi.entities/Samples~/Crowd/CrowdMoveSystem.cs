using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Gi.Entities.Samples.Crowd
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(GiSystemGroup))]
    [BurstCompile]
    public partial struct CrowdMoveSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CrowdAgent>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency = new CrowdMoveJob { DeltaTime = SystemAPI.Time.DeltaTime }.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct CrowdMoveJob : IJobEntity
        {
            public float DeltaTime;

            private void Execute(ref LocalTransform transform, ref CrowdAgent agent, in CrowdSteering steering)
            {
                var rng = new Random(agent.Rng);
                var wobble = new float2(rng.NextFloat(-1f, 1f), rng.NextFloat(-1f, 1f));
                agent.Rng = rng.state;
                var direction = steering.Gradient + 0.35f * wobble;
                var length = math.length(direction);
                if (length > 1e-4f) direction /= length;
                var step = direction * agent.Speed * DeltaTime;
                transform.Position += new float3(step.x, 0f, step.y);
            }
        }
    }
}
