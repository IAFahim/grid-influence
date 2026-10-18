using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gi.Entities.Authoring;
using Gi.Entities.Tests.Authored;
using Gi.Entities.Tests.Support;
using Gi.Validation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiAuthoringTests
    {
        private const string Folder = "Assets/GiAuthoringTests";

        [SetUp]
        public void SetUp()
        {
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", "GiAuthoringTests");
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            Undo.ClearAll();
        }

        private static GiProfile NewProfile(string name = "Profile")
        {
            var profile = ScriptableObject.CreateInstance<GiProfile>();
            AssetDatabase.CreateAsset(profile, Folder + "/" + name + ".asset");
            profile.Grids.Add(new GiGridEntry { Power = 6, Origin = Vector2.zero, Size = 64f });
            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static GiStampAsset NewStamp(string name, GiStampShape shape = GiStampShape.Box)
        {
            var stamp = ScriptableObject.CreateInstance<GiStampAsset>();
            stamp.Shape = shape;
            AssetDatabase.CreateAsset(stamp, Folder + "/" + name + ".asset");
            return stamp;
        }

        private static List<string> HelpTexts(UnityEditor.Editor editor)
        {
            return editor.CreateInspectorGUI().Query<HelpBox>().ToList().Select(box => box.text).ToList();
        }

        private static void Edit(Object target, System.Action<SerializedObject> change)
        {
            var so = new SerializedObject(target);
            change(so);
            so.ApplyModifiedProperties();
        }

        [Test]
        public void EveryGridRuleShowsTheValidationTextInTheInspector()
        {
            var profile = NewProfile();
            GiLayerStack.Add(new SerializedObject(profile), GiLayerKind.Persistent);

            Edit(profile, so => so.FindProperty("Grids").GetArrayElementAtIndex(0).FindPropertyRelative("Power").intValue = 4);
            GiValidation.TryGrid(4, 0f, 0f, 64f, out var power);
            Assert.That(HelpTexts(UnityEditor.Editor.CreateEditor(profile)), Does.Contain("Grid 0: " + power));

            Edit(profile, so =>
            {
                var grid = so.FindProperty("Grids").GetArrayElementAtIndex(0);
                grid.FindPropertyRelative("Power").intValue = 6;
                grid.FindPropertyRelative("Size").floatValue = -1f;
            });
            GiValidation.TryGrid(6, 0f, 0f, -1f, out var size);
            Assert.That(HelpTexts(UnityEditor.Editor.CreateEditor(profile)), Does.Contain("Grid 0: " + size));
        }

        [Test]
        public void EveryStampRuleShowsTheValidationTextInTheInspector()
        {
            var box = NewStamp("Box");
            Edit(box, so => so.FindProperty("Width").intValue = 0);
            GiValidation.TryRectangle(0, box.Height, box.Value, out var rectangle);
            Assert.That(HelpTexts(UnityEditor.Editor.CreateEditor(box)), Is.EqualTo(new[] { "Stamp 0: " + rectangle }));

            var disk = NewStamp("Disk", GiStampShape.Disk);
            Edit(disk, so => so.FindProperty("Radius").intValue = 0);
            GiValidation.TryRound(0, disk.Arc, disk.Value, out var round);
            Assert.That(HelpTexts(UnityEditor.Editor.CreateEditor(disk)), Is.EqualTo(new[] { "Stamp 0: " + round }));

            var fine = NewStamp("Fine");
            Assert.That(HelpTexts(UnityEditor.Editor.CreateEditor(fine)), Is.Empty);
        }

        [Test]
        public void EveryDerivedLayerRuleShowsTheValidationTextInTheInspector()
        {
            var profile = NewProfile();
            var so = new SerializedObject(profile);
            var a = GiLayerStack.Add(so, GiLayerKind.Persistent);
            var sum = GiLayerStack.Add(so, GiLayerKind.Sum);
            Assert.That(GiLayerStack.SetInput(new SerializedObject(sum), false, a), Is.True);
            Edit(sum, serialized => serialized.FindProperty("FirstWeight").intValue = 40000);
            GiValidation.TryDerivedLayer(40000, sum.SecondWeight, sum.Shift, out var weights);
            Assert.That(HelpTexts(UnityEditor.Editor.CreateEditor(sum)), Is.EqualTo(new[] { "Layer 1: " + weights }));

            Edit(sum, serialized =>
            {
                serialized.FindProperty("FirstWeight").intValue = 1;
                serialized.FindProperty("Shift").intValue = 16;
            });
            GiValidation.TryDerivedLayer(1, sum.SecondWeight, 16, out var shift);
            Assert.That(HelpTexts(UnityEditor.Editor.CreateEditor(sum)), Is.EqualTo(new[] { "Layer 1: " + shift }));
        }

        [Test]
        public void ADerivedLayerWithoutInputsSaysSo()
        {
            var profile = NewProfile();
            var so = new SerializedObject(profile);
            GiLayerStack.Add(so, GiLayerKind.Persistent);
            var max = GiLayerStack.Add(so, GiLayerKind.Max);
            var texts = HelpTexts(UnityEditor.Editor.CreateEditor(max));
            Assert.That(texts, Does.Contain("Layer 1: A derived layer needs its first input layer chosen."));
            Assert.That(texts, Does.Contain("Layer 1: A derived layer needs its second input layer chosen."));
            Assert.That(texts.Count(t => t.Contains(GiProfileCheck.ForwardReference)), Is.EqualTo(0));
        }

        [Test]
        public void SavingAnAssetTwiceGivesByteIdenticalYaml()
        {
            var profile = NewProfile("Twice");
            var so = new SerializedObject(profile);
            var a = GiLayerStack.Add(so, GiLayerKind.Persistent);
            var b = GiLayerStack.Add(so, GiLayerKind.Persistent);
            var sum = GiLayerStack.Add(so, GiLayerKind.Sum);
            GiLayerStack.SetInput(new SerializedObject(sum), false, a);
            GiLayerStack.SetInput(new SerializedObject(sum), true, b);
            var stamp = NewStamp("Stamp");
            Edit(profile, serialized =>
            {
                var stamps = serialized.FindProperty("Stamps");
                stamps.InsertArrayElementAtIndex(0);
                stamps.GetArrayElementAtIndex(0).objectReferenceValue = stamp;
            });

            AssetDatabase.SaveAssets();
            var path = AssetDatabase.GetAssetPath(profile);
            var first = File.ReadAllBytes(path);

            EditorUtility.SetDirty(profile);
            foreach (var layer in profile.Layers) EditorUtility.SetDirty(layer);
            AssetDatabase.SaveAssets();
            var second = File.ReadAllBytes(path);

            AssetDatabase.ForceReserializeAssets(new[] { path });
            var third = File.ReadAllBytes(path);
            Assert.That(second, Is.EqualTo(first));
            Assert.That(third, Is.EqualTo(first));
        }

        [Test]
        public void NoInspectorActionCreatesAForwardReference()
        {
            var profile = NewProfile();
            var so = new SerializedObject(profile);
            var a = GiLayerStack.Add(so, GiLayerKind.Persistent);
            var b = GiLayerStack.Add(so, GiLayerKind.Persistent);
            var sum = GiLayerStack.Add(so, GiLayerKind.Sum);
            var max = GiLayerStack.Add(so, GiLayerKind.Max);
            Assert.That(GiLayerStack.SetInput(new SerializedObject(sum), false, a), Is.True);
            Assert.That(GiLayerStack.SetInput(new SerializedObject(sum), true, b), Is.True);
            Assert.That(GiLayerStack.SetInput(new SerializedObject(max), false, sum), Is.True);
            Assert.That(GiLayerStack.SetInput(new SerializedObject(max), true, b), Is.True);

            Assert.That(GiLayerStack.Candidates(sum), Is.EqualTo(new[] { a, b }));
            Assert.That(GiLayerStack.Candidates(a), Is.Empty);
            Assert.That(GiLayerStack.SetInput(new SerializedObject(sum), false, max), Is.False, "a later layer cannot be an input");
            Assert.That(GiLayerStack.SetInput(new SerializedObject(sum), false, sum), Is.False, "a layer cannot read itself");
            Assert.That(GiLayerStack.SetInput(new SerializedObject(a), false, b), Is.False, "a base layer has no earlier candidates");

            var before = profile.Layers.ToArray();
            Assert.That(GiLayerStack.Move(so, 2, 0), Is.False, "moving the sum above its inputs");
            Assert.That(GiLayerStack.Move(so, 0, 2), Is.False, "moving an input below the sum");
            Assert.That(GiLayerStack.Move(so, 1, 3), Is.False, "moving an input below its readers");
            Assert.That(profile.Layers.ToArray(), Is.EqualTo(before));
            Assert.That(GiLayerStack.Move(so, 0, 1), Is.True, "swapping two base layers is fine");
            Assert.That(profile.Layers[0], Is.SameAs(b));
            Assert.That(GiLayerStack.HasForwardReference(profile.Layers), Is.False);

            Assert.That(GiLayerStack.Remove(so, 0), Is.True);
            Assert.That(sum.Second, Is.Null, "removing an input clears the readers");
            Assert.That(max.Second, Is.Null);
            Assert.That(GiLayerStack.HasForwardReference(profile.Layers), Is.False);
            Assert.That(profile.Check().Any(issue => issue.Message == GiProfileCheck.ForwardReference), Is.False);
        }

        [Test]
        public void EveryEditCanBeUndoneAndRedone()
        {
            var profile = NewProfile();
            var so = new SerializedObject(profile);

            var first = GiLayerStack.Add(so, GiLayerKind.Persistent);
            Assert.That(profile.Layers.Count, Is.EqualTo(1));
            Undo.PerformUndo();
            Assert.That(profile.Layers.Count, Is.EqualTo(0));
            Undo.PerformRedo();
            Assert.That(profile.Layers.Count, Is.EqualTo(1));
            Assert.That(profile.Layers[0], Is.Not.Null);
            first = profile.Layers[0];

            Undo.IncrementCurrentGroup();
            Edit(profile, serialized => serialized.FindProperty("Grids").GetArrayElementAtIndex(0).FindPropertyRelative("Power").intValue = 9);
            Assert.That(profile.Grids[0].Power, Is.EqualTo(9));
            Undo.PerformUndo();
            Assert.That(profile.Grids[0].Power, Is.EqualTo(6));
            Undo.PerformRedo();
            Assert.That(profile.Grids[0].Power, Is.EqualTo(9));

            Undo.IncrementCurrentGroup();
            var second = GiLayerStack.Add(so, GiLayerKind.Persistent);
            Undo.IncrementCurrentGroup();
            Assert.That(GiLayerStack.Move(so, 0, 1), Is.True);
            Assert.That(profile.Layers[1], Is.SameAs(first));
            Undo.PerformUndo();
            Assert.That(profile.Layers[0], Is.SameAs(first));
            Undo.PerformRedo();
            Assert.That(profile.Layers[1], Is.SameAs(first));
            Assert.That(profile.Layers[0], Is.SameAs(second));

            Undo.IncrementCurrentGroup();
            GiLayerStack.Remove(so, 0);
            Assert.That(profile.Layers.Count, Is.EqualTo(1));
            Undo.PerformUndo();
            Assert.That(profile.Layers.Count, Is.EqualTo(2));
            Undo.PerformRedo();
            Assert.That(profile.Layers.Count, Is.EqualTo(1));
        }

        [Test]
        public void TheAuthoredStandardProfileHashesLikeTheScenarioProfile()
        {
            var scenario = GiScenarioProfile.Standard();
            var profile = GiAuthoredProfile.FromScenario(scenario);

            Assert.That(profile.ToSpec().Hash, Is.EqualTo(scenario.ToSpec().Hash));
            Assert.That(profile.Check(), Is.Empty);
        }

        [Test]
        public void ARasterStampFromAGrayTextureMapsEachPixelToASignedCell()
        {
            var gray = new byte[] { 0, 128, 255, 64 };
            Assert.That(GiStampAsset.FromGray(gray, 2, 2), Is.EqualTo(new sbyte[] { -128, 0, 127, -64 }));
        }
    }
}
