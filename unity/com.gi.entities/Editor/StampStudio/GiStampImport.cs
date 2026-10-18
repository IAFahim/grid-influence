using Gi.Entities.Authoring;
using UnityEditor;
using UnityEngine;

namespace Gi.Entities.Editor
{
    public static class GiStampImport
    {
        public const int MaxSide = 256;

        public static bool TryRead(Texture texture, out sbyte[] cells, out int width, out int height, out string error)
        {
            cells = null;
            width = 0;
            height = 0;
            error = null;
            if (texture == null)
            {
                error = "No texture is assigned.";
                return false;
            }

            width = texture.width;
            height = texture.height;
            if (width < 1 || height < 1 || width > MaxSide || height > MaxSide)
            {
                error = "A raster stamp must be 1 through " + MaxSide + " cells on each axis; the texture is " + width + " x " + height + ".";
                return false;
            }

            var pixels = Pixels(texture, width, height);
            var gray = new byte[width * height];
            for (var i = 0; i < gray.Length; i++) gray[i] = GiRasterImport.Gray(pixels[i].r, pixels[i].g, pixels[i].b, pixels[i].a);
            cells = GiRasterImport.FromGray(gray, width, height);
            return true;
        }

        public static bool Apply(SerializedObject stamp, Texture texture, out string error)
        {
            if (!TryRead(texture, out var cells, out var width, out var height, out error)) return false;

            stamp.Update();
            stamp.FindProperty("Shape").enumValueIndex = (int)GiStampShape.Raster;
            stamp.FindProperty("Width").intValue = width;
            stamp.FindProperty("Height").intValue = height;
            var array = stamp.FindProperty("Cells");
            array.arraySize = cells.Length;
            for (var i = 0; i < cells.Length; i++) array.GetArrayElementAtIndex(i).intValue = cells[i];
            stamp.ApplyModifiedProperties();
            return true;
        }

        private static Color32[] Pixels(Texture texture, int width, int height)
        {
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var readable = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                return readable.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(readable);
            }
        }
    }
}
