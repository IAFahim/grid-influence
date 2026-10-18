using System.Collections.Generic;
using System.Linq;
using Gi.Entities.Authoring;
using Gi.Entities.Tests.Authored;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiProfileEditorTests
    {
        private readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var made in _made)
                if (made != null)
                    Object.DestroyImmediate(made);
            _made.Clear();
        }

        private GiProfile Profile()
        {
            return GiAuthoredProfile.FromScenario(GiScenarioProfile.Standard(), _made);
        }

        [Test]
        public void TheOverviewShowsTheCoverageMapTheEstimateAndAThumbnailPerLayer()
        {
            var profile = Profile();
            var overview = new GiProfileOverview();
            overview.Show(profile);

            Assert.That(overview.HasMap, Is.True);
            Assert.That(overview.ThumbnailCount, Is.EqualTo(profile.Layers.Count));
            Assert.That(overview.EstimateText, Is.EqualTo(GiProfileOverview.EstimateOf(profile.ToSpec())));
            Assert.That(overview.EstimateText, Does.StartWith("Worst-case Gi memory: "));
            Assert.That(GiProfileEstimate.Worst(profile.ToSpec()), Is.GreaterThan(0));
        }

        [Test]
        public void TheOverviewTellsYouToFixIssuesInsteadOfDrawingThumbnails()
        {
            var profile = Profile();
            profile.Grids[0] = new GiGridEntry { Power = 3, Origin = Vector2.zero, Size = 64f };
            var overview = new GiProfileOverview();
            overview.Show(profile);
            Assert.That(overview.ThumbnailCount, Is.EqualTo(1));
        }

        [Test]
        public void ABindingSizeErrorAppearsInTheInspectorBeforeAnyBake()
        {
            var profile = Profile();
            profile.Bindings.Add(new GiBindingEntry
            {
                Kind = GiBindingKind.Value,
                Layer = profile.Layers[0],
                Value = GiFieldRef.Of(typeof(GiStats), "Commands"),
            });

            var editor = UnityEditor.Editor.CreateEditor(profile);
            var texts = editor.CreateInspectorGUI().Query<HelpBox>().ToList().Select(box => box.text).ToList();
            Assert.That(texts, Does.Contain("Binding 0: " + GiBindingTable.Describe(GiBindingStatus.WrongFieldSize)));
            Object.DestroyImmediate(editor);
        }

        [Test]
        public void TheFieldCatalogListsComponentFieldsWithTheirOffsetsAndSizes()
        {
            var types = GiFieldCatalog.ComponentTypes();
            Assert.That(types, Does.Contain(typeof(GiStats)));
            Assert.That(types, Does.Contain(typeof(GiEmitter)));
            Assert.That(types, Is.Ordered.Using<System.Type>((a, b) => string.CompareOrdinal(a.FullName, b.FullName)));

            var fields = GiFieldCatalog.Fields(typeof(GiStats));
            Assert.That(fields.Select(field => field.Name), Is.EqualTo(new[] { "Commands", "Pulses", "SyncRows", "Splats", "Sensed", "ChangedTiles" }));
            Assert.That(fields.Select(field => field.Offset), Is.EqualTo(new[] { 0, 4, 8, 12, 16, 20 }));
            Assert.That(fields.Select(field => field.Size), Is.All.EqualTo(4));
            Assert.That(fields[3].ToString(), Is.EqualTo("Splats (4 B at 12)"));
            Assert.That(GiFieldCatalog.Fields(typeof(GiEmitter)).Select(field => field.Name), Is.EqualTo(new[] { "Layer", "Stamp", "Gain" }));
        }

        [Test]
        public void AFieldReferenceResolvesToTheTypeHashOffsetAndSizeOfTheField()
        {
            var reference = GiFieldRef.Of(typeof(GiStats), "Splats");
            Assert.That(reference.TryResolve(out var field, out var error), Is.True, error);
            Assert.That(field.TypeHash, Is.EqualTo(Unity.Entities.TypeManager.GetTypeInfo(Unity.Entities.TypeManager.GetTypeIndex<GiStats>()).StableTypeHash));
            Assert.That((field.Offset, field.Size), Is.EqualTo(((ushort)12, (ushort)4)));

            Assert.That(GiFieldRef.Of(typeof(GiStats)).TryResolve(out var whole, out _), Is.True);
            Assert.That((whole.Offset, whole.Size), Is.EqualTo(((ushort)0, (ushort)0)));
            Assert.That(default(GiFieldRef).TryResolve(out var none, out _), Is.True);
            Assert.That(none.TypeHash, Is.EqualTo(0ul));

            Assert.That(GiFieldRef.Of(typeof(GiStats), "Nope").TryResolve(out _, out error), Is.False);
            Assert.That(error, Does.Contain("no public field"));
        }
    }
}
