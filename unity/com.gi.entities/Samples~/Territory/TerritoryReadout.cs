using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Gi.Entities.Samples.Territory
{
    public sealed class TerritoryReadout : MonoBehaviour
    {
        private EntityQuery _banners;

        private void Start()
        {
            var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null) return;
            _banners = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<TerritoryBanner>(), ComponentType.ReadOnly<TerritoryHeld>());
        }

        private void OnGUI()
        {
            if (_banners == null || _banners.IsEmptyIgnoreFilter) return;
            if (Unity.Entities.World.DefaultGameObjectInjectionWorld == null) return;

            var manager = Unity.Entities.World.DefaultGameObjectInjectionWorld.EntityManager;
            var banners = _banners.ToEntityArray(Allocator.Temp);
            var red = 0;
            var blue = 0;
            var lost = 0;
            for (var i = 0; i < banners.Length; i++)
            {
                if (!manager.IsComponentEnabled<TerritoryHeld>(banners[i]))
                {
                    lost++;
                    continue;
                }

                var strength = manager.GetComponentData<TerritoryBanner>(banners[i]).Strength;
                if (strength > 3) red++;
                else if (strength < -3) blue++;
                else lost++;
            }

            banners.Dispose();
            GUI.Label(new Rect(16f, 16f, 420f, 20f), "Red holds " + red + " banners, blue holds " + blue + ", " + lost + " stand in the border band.");
        }
    }
}
