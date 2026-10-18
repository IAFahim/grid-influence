using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using GiWorld = Gi.World;

namespace Gi.Entities.Samples.Read
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(GiSystemGroup))]
    public partial struct ReadMoteSystem : ISystem
    {
        private EntityQuery _frames;
        private EntityQuery _motes;
        private ComponentLookup<GiFrame> _frameLookup;
        private ComponentLookup<LocalTransform> _transformLookup;
        private ComponentLookup<ReadMote> _moteLookup;

        public void OnCreate(ref SystemState state)
        {
            _frames = state.GetEntityQuery(ComponentType.ReadOnly<GiFrame>());
            _motes = state.GetEntityQuery(ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadWrite<ReadMote>());
            state.RequireForUpdate(_frames);
            state.RequireForUpdate(_motes);
            _frameLookup = state.GetComponentLookup<GiFrame>(true);
            _transformLookup = state.GetComponentLookup<LocalTransform>();
            _moteLookup = state.GetComponentLookup<ReadMote>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _frameLookup.Update(ref state);
            _transformLookup.Update(ref state);
            _moteLookup.Update(ref state);

            var allocator = state.WorldUpdateAllocator;
            var entities = _motes.ToEntityArray(allocator);
            var positions = _motes.ToComponentDataArray<LocalTransform>(allocator);
            var values = CollectionHelper.CreateNativeArray<short>(entities.Length, state.WorldUpdateAllocator, NativeArrayOptions.UninitializedMemory);
            var field = CollectionHelper.CreateNativeArray<ReadField>(1, state.WorldUpdateAllocator, NativeArrayOptions.UninitializedMemory);

            var prepare = new ReadPrepareJob
            {
                Frame = SystemAPI.GetSingletonEntity<GiFrame>(),
                Frames = _frameLookup,
                Field = field,
            }.Schedule(state.Dependency);

            var read = GiRuntime.BeginRead(state.Dependency);
            read = JobHandle.CombineDependencies(read, prepare);
            read = new ReadSenseJob { Field = field, Positions = positions, Values = values }.Schedule(positions.Length, 64, read);
            GiRuntime.EndRead(read);

            var move = new ReadMoveJob
            {
                Entities = entities,
                Values = values,
                Transforms = _transformLookup,
                Motes = _moteLookup,
                DeltaTime = SystemAPI.Time.DeltaTime,
            }.Schedule(entities.Length, 64, JobHandle.CombineDependencies(read, prepare));

            state.Dependency = move;
            entities.Dispose(state.Dependency);
            positions.Dispose(state.Dependency);
            values.Dispose(state.Dependency);
            field.Dispose(state.Dependency);
        }

        private struct ReadField
        {
            public byte World;
            public byte Layer;
            public GiPlane Plane;
            public byte Alive;
        }

        [BurstCompile]
        private struct ReadPrepareJob : IJob
        {
            public Entity Frame;
            [ReadOnly] public ComponentLookup<GiFrame> Frames;
            public NativeArray<ReadField> Field;

            public void Execute()
            {
                var frame = Frames[Frame];
                var layer = (byte)0;
                var alive = frame.Alive && frame.Tables.TryLayer(new GiLayerKey(0), out layer);
                Field[0] = new ReadField
                {
                    World = frame.World,
                    Layer = layer,
                    Plane = frame.Plane,
                    Alive = alive ? (byte)1 : (byte)0,
                };
            }
        }

        private struct ReadSenseJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ReadField> Field;
            [ReadOnly] public NativeArray<LocalTransform> Positions;
            [WriteOnly] public NativeArray<short> Values;

            public void Execute(int i)
            {
                var field = Field[0];
                if (field.Alive == 0)
                {
                    Values[i] = 0;
                    return;
                }

                var position = Positions[i].Position;
                field.Plane.Project(position.x, position.y, position.z, out var x, out var y);
                Values[i] = GiWorld.TrySense(field.World, field.Layer, x, y, out var value) ? value : short.MinValue;
            }
        }

        [BurstCompile]
        private struct ReadMoveJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Entity> Entities;
            [ReadOnly] public NativeArray<short> Values;
            [NativeDisableParallelForRestriction] public ComponentLookup<LocalTransform> Transforms;
            [NativeDisableParallelForRestriction] public ComponentLookup<ReadMote> Motes;
            public float DeltaTime;

            public void Execute(int i)
            {
                var entity = Entities[i];
                var mote = Motes[entity];
                mote.Field = Values[i];
                var speed = mote.Speed * (1f + math.max(0, mote.Field) / 300f);
                var transform = Transforms[entity];
                transform.Position += new float3(math.cos(mote.Heading), 0f, math.sin(mote.Heading)) * (speed * DeltaTime);
                Transforms[entity] = transform;
                Motes[entity] = mote;
            }
        }
    }
}
