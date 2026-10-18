using Gi.Entities.Presentation;
using Unity.Entities;
using UnityEngine;

namespace Gi.Entities.Editor
{
    public sealed class GiExplainModel
    {
        public const int DefaultCapacity = 32;

        private int _pendingExplain;
        private int _pendingEntity;

        public string EcsWorld { get; private set; }

        public int Capacity { get; set; } = DefaultCapacity;

        public GiExplainResult Latest { get; private set; }

        public GiEntityReport Report { get; private set; }

        public bool Waiting => _pendingExplain != 0 || _pendingEntity != 0;

        public void Explain(string ecsWorld, int layerKey, Vector2 world)
        {
            if (ecsWorld == null) return;

            if (ecsWorld != EcsWorld) Clear();
            EcsWorld = ecsWorld;
            _pendingExplain = GiInspectorBoard.RequestExplain(ecsWorld, new GiExplainRequest
            {
                Layer = (byte)layerKey,
                X = world.x,
                Y = world.y,
                Capacity = Capacity,
            });
        }

        public void Inspect(string ecsWorld, Entity entity)
        {
            if (ecsWorld == null) return;

            EcsWorld = ecsWorld;
            _pendingEntity = GiInspectorBoard.RequestEntity(ecsWorld, entity);
        }

        public int Poll()
        {
            var consumed = 0;
            if (_pendingExplain != 0 && GiInspectorBoard.TryTakeExplain(_pendingExplain, out var explain))
            {
                Latest = explain;
                _pendingExplain = 0;
                consumed++;
            }

            if (_pendingEntity != 0 && GiInspectorBoard.TryTakeEntity(_pendingEntity, out var report))
            {
                Report = report;
                _pendingEntity = 0;
                consumed++;
            }

            return consumed;
        }

        public void Clear()
        {
            Latest = null;
            Report = null;
            _pendingExplain = 0;
            _pendingEntity = 0;
        }
    }
}
