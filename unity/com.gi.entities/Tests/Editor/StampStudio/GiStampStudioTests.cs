using Gi.Entities.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using GiStamp = Gi.Stamp;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiStampStudioTests
    {
        private static int DefinedStamps()
        {
            var count = 0;
            for (var stamp = 1; stamp < 256; stamp++)
                if (GiStamp.TryGetSpec((byte)stamp, out _))
                    count++;
            return count;
        }

        private static GiStampAsset Stamp(GiStampShape shape)
        {
            var stamp = ScriptableObject.CreateInstance<GiStampAsset>();
            stamp.Shape = shape;
            stamp.Width = 7;
            stamp.Height = 5;
            stamp.Radius = 6;
            stamp.Value = 40;
            return stamp;
        }

        private static Texture2D Gradient(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            var pixels = new Color32[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = new Color32((byte)(x * 255 / Mathf.Max(1, width - 1)), (byte)(y * 255 / Mathf.Max(1, height - 1)), 90, 255);
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        [Test]
        public void ThePreviewDrawsAndItsHashChangesWithThePose()
        {
            var stamp = Stamp(GiStampShape.Tent);
            var window = EditorWindow.CreateInstance<GiStampStudioWindow>();
            try
            {
                window.Edit(stamp);
                var first = window.LastHash;
                Assert.That(window.LastBounds.Empty, Is.False);
                Assert.That(window.LastBounds.Width, Is.InRange(5, 12));

                window.SetPose(0.5f, 0.25f, 90f, 2f);
                Assert.That(window.LastHash, Is.Not.EqualTo(first));
                var posed = window.LastHash;
                window.SetPose(0.5f, 0.25f, 90f, 2f);
                Assert.That(window.LastHash, Is.EqualTo(posed));
                Assert.That(window.TemporaryStamps, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(stamp);
            }
        }

        [Test]
        public void EditingAnotherStampKeepsAtMostOneTemporaryStamp()
        {
            var baseline = DefinedStamps();
            var window = EditorWindow.CreateInstance<GiStampStudioWindow>();
            var stamps = new[] { Stamp(GiStampShape.Box), Stamp(GiStampShape.Disk), Stamp(GiStampShape.Bell) };
            try
            {
                for (var round = 0; round < 6; round++)
                {
                    window.Edit(stamps[round % stamps.Length]);
                    Assert.That(window.TemporaryStamps, Is.LessThanOrEqualTo(1));
                    Assert.That(DefinedStamps(), Is.LessThanOrEqualTo(baseline + 1));
                }
            }
            finally
            {
                Object.DestroyImmediate(window);
                foreach (var stamp in stamps) Object.DestroyImmediate(stamp);
            }

            Assert.That(DefinedStamps(), Is.EqualTo(baseline));
        }

        [Test]
        public void ClosingTheStudioFreesItsGiWorldAndItsTemporaryStamp()
        {
            var baseline = DefinedStamps();
            var worlds = GiOrphans.LiveWorlds();
            var stamp = Stamp(GiStampShape.Cone);
            var window = EditorWindow.CreateInstance<GiStampStudioWindow>();
            window.Edit(stamp);
            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(worlds + 1));
            Assert.That(DefinedStamps(), Is.EqualTo(baseline + 1));

            Object.DestroyImmediate(window);
            Object.DestroyImmediate(stamp);
            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(worlds));
            Assert.That(DefinedStamps(), Is.EqualTo(baseline));
        }

        [Test]
        public void AnInvalidStampShowsItsReasonInsteadOfAPreview()
        {
            var stamp = Stamp(GiStampShape.Box);
            stamp.Width = 0;
            var window = EditorWindow.CreateInstance<GiStampStudioWindow>();
            try
            {
                window.Edit(stamp);
                Assert.That(window.Preview.HasStamp, Is.False);
                Assert.That(window.TemporaryStamps, Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(stamp);
            }
        }

        [Test]
        public void ImportingTheSameTextureTwiceGivesIdenticalRasterCells()
        {
            var texture = Gradient(16, 12);
            var stamp = Stamp(GiStampShape.Box);
            try
            {
                Assert.That(GiStampImport.TryRead(texture, out var first, out var width, out var height, out _), Is.True);
                Assert.That(GiStampImport.TryRead(texture, out var second, out _, out _, out _), Is.True);
                Assert.That(first, Is.EqualTo(second));
                Assert.That((width, height, first.Length), Is.EqualTo((16, 12, 192)));

                Assert.That(GiStampImport.Apply(new SerializedObject(stamp), texture, out var error), Is.True, error);
                var once = (sbyte[])stamp.Cells.Clone();
                Assert.That(GiStampImport.Apply(new SerializedObject(stamp), texture, out error), Is.True, error);
                Assert.That(stamp.Cells, Is.EqualTo(once));
                Assert.That(stamp.Cells, Is.EqualTo(first));
                Assert.That(stamp.Shape, Is.EqualTo(GiStampShape.Raster));
                Assert.That(GiStampEditor.Check(stamp), Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(stamp);
            }
        }

        [Test]
        public void ATextureThatIsTooLargeIsRefusedWithTheLimit()
        {
            var texture = new Texture2D(300, 4);
            try
            {
                Assert.That(GiStampImport.TryRead(texture, out _, out _, out _, out var error), Is.False);
                Assert.That(error, Does.Contain("256"));
                Assert.That(GiStampImport.TryRead(null, out _, out _, out _, out error), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }
    }
}
