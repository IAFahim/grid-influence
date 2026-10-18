using System;
using UnityEngine;

namespace Gi.Entities.Presentation
{
    public sealed class GiView : IDisposable
    {
        public const int MaxSide = 512;

        public GiView(string ecsWorld, int grid, int layerKey)
        {
            EcsWorld = ecsWorld;
            Grid = grid;
            LayerKey = layerKey;
        }

        public string EcsWorld { get; }

        public int Grid { get; }

        public int LayerKey { get; }

        public RectInt Region { get; private set; }

        public float ValueScale { get; private set; } = 64f;

        public bool Dirty { get; internal set; } = true;

        public bool Disposed { get; private set; }

        public uint Generation { get; internal set; }

        public GiMirror Mirror { get; private set; }

        public RenderTexture Texture { get; private set; }

        public void SetValueScale(float scale)
        {
            ValueScale = Mathf.Max(1f, scale);
            Dirty = true;
        }

        public void SetRegion(RectInt region)
        {
            Region = region;
            Dirty = true;
            if (Texture != null && (Texture.width != region.width || Texture.height != region.height)) ReleaseTexture();
        }

        internal void Attach(GiMirror mirror, uint generation)
        {
            Detach();
            Mirror = mirror;
            Generation = generation;
            if (Region.width == 0) SetRegion(new RectInt(0, 0, Mathf.Min(mirror.Cells, MaxSide), Mathf.Min(mirror.Cells, MaxSide)));
            if (Texture == null)
            {
                Texture = new RenderTexture(Region.width, Region.height, 0, RenderTextureFormat.ARGB32) { enableRandomWrite = true, filterMode = FilterMode.Point };
                Texture.Create();
            }

            Dirty = true;
        }

        internal void Detach()
        {
            Mirror?.Dispose();
            Mirror = null;
        }

        public void Dispose()
        {
            if (Disposed) return;

            Disposed = true;
            Detach();
            ReleaseTexture();
        }

        private void ReleaseTexture()
        {
            if (Texture == null) return;

            Texture.Release();
            UnityEngine.Object.DestroyImmediate(Texture);
            Texture = null;
        }
    }
}
