using System;
using System.Collections.Generic;
using BovineLabs.Core.Collections;
using BovineLabs.Core.SingletonCollection;
using Gi.Entities.Tests.Support;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Gi.Entities.Tests
{
    public sealed class GiEcsExecutor : IDisposable
    {
        public readonly GiEcsFixture Fixture;
        public readonly GiScenario Scenario;
        public readonly Entity[] Entities;
        private readonly EntityArchetype[] _archetypes = new EntityArchetype[8];
        private readonly bool[] _spawned;
        private readonly List<Entity> _splats = new List<Entity>();
        private readonly EntityQuery _pulses;

        public GiEcsExecutor(GiScenario scenario, string name = "GiExecutor")
        {
            Scenario = scenario;
            Fixture = new GiEcsFixture(name);
            Fixture.SetProfile(scenario.Profile.ToSpec());
            Entities = new Entity[scenario.EntityCount];
            _spawned = new bool[scenario.EntityCount];
            _pulses = Fixture.Manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<GiPulseSingleton>() },
                Options = EntityQueryOptions.IncludeSystems,
            });
        }

        public GiScenarioHashes Run()
        {
            var perTick = new ulong[Scenario.Ticks.Length][];
            for (var t = 0; t < Scenario.Ticks.Length; t++) perTick[t] = Step(t);
            return new GiScenarioHashes(perTick);
        }

        public ulong[] Step(int tick)
        {
            Queue(tick);
            Fixture.Tick();
            return Hashes();
        }

        public void Queue(int tick)
        {
            var manager = Fixture.Manager;
            foreach (var splat in _splats) manager.DestroyEntity(splat);
            _splats.Clear();
            var pulses = new List<GiPulse>();
            foreach (var op in Scenario.Ticks[tick].Ops) Apply(manager, op, pulses);

            if (pulses.Count > 0)
            {
                var singleton = _pulses.GetSingleton<GiPulseSingleton>();
                var writer = singleton.CreateThreadStream();
                foreach (var pulse in pulses) writer.Write(pulse);
            }
        }

        public ulong[] Hashes()
        {
            GiRuntime.CompleteAll();
            Fixture.Manager.CompleteAllTrackedJobs();
            var frame = Fixture.Frame;
            var hashes = new ulong[Scenario.Profile.Layers.Length];
            for (var l = 0; l < hashes.Length; l++)
            {
                frame.Tables.TryLayer(new GiLayerKey((byte)l), out var layer);
                hashes[l] = GiFieldHash.OfLayer(frame.World, layer);
            }

            return hashes;
        }

        public void Dispose()
        {
            _pulses.Dispose();
            Fixture.Dispose();
        }

        private void Apply(EntityManager manager, GiScenarioOp op, List<GiPulse> pulses)
        {
            switch (op.Kind)
            {
                case GiScenarioOpKind.Spawn:
                    Spawn(manager, op);
                    break;
                case GiScenarioOpKind.Despawn:
                    manager.DestroyEntity(Entities[op.Entity]);
                    break;
                case GiScenarioOpKind.Disable:
                    manager.SetComponentEnabled<GiEmitter>(Entities[op.Entity], false);
                    break;
                case GiScenarioOpKind.Enable:
                    manager.SetComponentEnabled<GiEmitter>(Entities[op.Entity], true);
                    break;
                case GiScenarioOpKind.Move:
                    var moved = manager.GetComponentData<LocalTransform>(Entities[op.Entity]);
                    moved.Position = new float3(op.X, 0f, op.Y);
                    manager.SetComponentData(Entities[op.Entity], moved);
                    break;
                case GiScenarioOpKind.Turn:
                    var turned = manager.GetComponentData<LocalTransform>(Entities[op.Entity]);
                    turned.Rotation = Rotation(op.Heading);
                    manager.SetComponentData(Entities[op.Entity], turned);
                    break;
                case GiScenarioOpKind.Scale:
                    var scaled = manager.GetComponentData<LocalTransform>(Entities[op.Entity]);
                    scaled.Scale = op.Scale;
                    manager.SetComponentData(Entities[op.Entity], scaled);
                    break;
                case GiScenarioOpKind.SetGain:
                    var gain = manager.GetComponentData<GiEmitter>(Entities[op.Entity]);
                    gain.Gain = (sbyte)op.Gain;
                    manager.SetComponentData(Entities[op.Entity], gain);
                    break;
                case GiScenarioOpKind.Retarget:
                    var target = manager.GetComponentData<GiEmitter>(Entities[op.Entity]);
                    target.Layer = new GiLayerKey((byte)op.Layer);
                    target.Stamp = new GiStampKey((byte)op.Stamp);
                    manager.SetComponentData(Entities[op.Entity], target);
                    break;
                case GiScenarioOpKind.Splat:
                    _splats.Add(Fixture.CreateSplat((byte)op.Layer, (byte)op.Stamp, (sbyte)op.Gain, op.X, op.Y));
                    break;
                case GiScenarioOpKind.Pulse:
                    pulses.Add(new GiPulse
                    {
                        Layer = new GiLayerKey((byte)op.Layer),
                        Stamp = new GiStampKey((byte)op.Stamp),
                        X = op.X,
                        Y = op.Y,
                        Heading = op.Heading,
                        Scale = op.Scale,
                        Gain = (sbyte)op.Gain,
                        Ticks = (ushort)op.Ticks,
                        FadeOut = (op.Flags & GiScenarioFlags.FadeOut) != 0,
                    });
                    break;
            }
        }

        private void Spawn(EntityManager manager, GiScenarioOp op)
        {
            var ramp = op.Ticks > 0;
            var oriented = (op.Flags & GiScenarioFlags.Oriented) != 0;
            var scaled = (op.Flags & GiScenarioFlags.Scaled) != 0;
            var key = (ramp ? 1 : 0) | (oriented ? 2 : 0) | (scaled ? 4 : 0);
            if (!_archetypes[key].Valid)
            {
                var types = new List<ComponentType>
                {
                    ComponentType.ReadWrite<GiEmitter>(), ComponentType.ReadWrite<GiFault>(), ComponentType.ReadWrite<LocalTransform>(),
                };
                if (ramp) types.Add(ComponentType.ReadWrite<GiGainRamp>());
                if (oriented) types.Add(ComponentType.ReadWrite<GiOriented>());
                if (scaled) types.Add(ComponentType.ReadWrite<GiScaled>());
                _archetypes[key] = manager.CreateArchetype(types.ToArray());
            }

            var entity = manager.CreateEntity(_archetypes[key]);
            manager.SetComponentData(entity, new GiEmitter
            {
                Layer = new GiLayerKey((byte)op.Layer),
                Stamp = new GiStampKey((byte)op.Stamp),
                Gain = (sbyte)op.Gain,
            });
            manager.SetComponentData(entity, new LocalTransform { Position = new float3(op.X, 0f, op.Y), Rotation = Rotation(op.Heading), Scale = op.Scale });
            if (ramp) manager.SetComponentData(entity, new GiGainRamp { Ticks = (ushort)op.Ticks });
            manager.SetComponentEnabled<GiFault>(entity, false);
            if ((op.Flags & GiScenarioFlags.Disabled) != 0) manager.SetComponentEnabled<GiEmitter>(entity, false);
            Entities[op.Entity] = entity;
            _spawned[op.Entity] = true;
        }

        private static quaternion Rotation(float heading)
        {
            return quaternion.RotateY(math.PI / 2f - heading);
        }
    }
}
