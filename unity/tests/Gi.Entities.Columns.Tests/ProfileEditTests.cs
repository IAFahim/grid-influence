using Gi.Entities.Tests.Support;
using Xunit;

namespace Gi.Entities.Columns.Tests
{
    public sealed class ProfileEditTests
    {
        private static GiProfileSpec Base()
        {
            return GiScenarioProfile.Standard().ToSpec();
        }

        private static ulong States(GiProfileSpec spec)
        {
            return GiProfileHash.StateHash(spec.Plane, spec.Grids, spec.Layers, spec.Stamps, spec.Cells);
        }

        private static ulong Bound(GiProfileSpec spec)
        {
            return GiProfileHash.BindingsHash(spec.Bindings);
        }

        private static ulong Full(GiProfileSpec spec)
        {
            return GiProfileHash.Compute(spec.Plane, spec.Grids, spec.Layers, spec.Stamps, spec.Cells, spec.Bindings);
        }

        private static GiBindingSpec ProbeBinding()
        {
            return new GiBindingSpec
            {
                Kind = GiBindingKind.Value,
                Layer = 0,
                Value = new GiBindingField { TypeHash = 7ul, Offset = 0, Size = 2 },
            };
        }

        [Fact]
        public void SectionHashesAreDeterministic()
        {
            var spec = Base();
            Assert.Equal(States(spec), States(spec));
            Assert.Equal(Bound(spec), Bound(spec));
            Assert.Equal(Full(spec), Full(spec));
        }

        [Fact]
        public void PlaneEditsChangeStatesOnly()
        {
            var first = Base();
            var second = Base();
            second.Plane.OriginX += 1f;
            Assert.NotEqual(States(first), States(second));
            Assert.Equal(Bound(first), Bound(second));
            Assert.NotEqual(Full(first), Full(second));
            Assert.Equal(GiProfileEditKind.Rebuild, GiProfileEdit.Classify(States(first), Bound(first), States(second), Bound(second)));
        }

        [Fact]
        public void GridEditsChangeStatesOnly()
        {
            var first = Base();
            var second = Base();
            second.Grids[0].Size += 64f;
            Assert.NotEqual(States(first), States(second));
            Assert.Equal(Bound(first), Bound(second));
            Assert.NotEqual(Full(first), Full(second));
            Assert.Equal(GiProfileEditKind.Rebuild, GiProfileEdit.Classify(States(first), Bound(first), States(second), Bound(second)));
        }

        [Fact]
        public void BaseLayerEditsChangeStatesOnly()
        {
            var first = Base();
            var second = Base();
            second.Layers[0].PhaseBits += 1;
            Assert.NotEqual(States(first), States(second));
            Assert.Equal(Bound(first), Bound(second));
            Assert.NotEqual(Full(first), Full(second));
            Assert.Equal(GiProfileEditKind.Rebuild, GiProfileEdit.Classify(States(first), Bound(first), States(second), Bound(second)));
        }

        [Fact]
        public void DerivedLayerEditsChangeStatesOnly()
        {
            var first = Base();
            var second = Base();
            second.Layers[3].Shift += 1;
            Assert.NotEqual(States(first), States(second));
            Assert.Equal(Bound(first), Bound(second));
            Assert.NotEqual(Full(first), Full(second));
            Assert.Equal(GiProfileEditKind.Rebuild, GiProfileEdit.Classify(States(first), Bound(first), States(second), Bound(second)));
        }

        [Fact]
        public void StampEditsChangeStatesOnly()
        {
            var first = Base();
            var second = Base();
            second.Stamps[0].Value += 1;
            Assert.NotEqual(States(first), States(second));
            Assert.Equal(Bound(first), Bound(second));
            Assert.NotEqual(Full(first), Full(second));
            Assert.Equal(GiProfileEditKind.Rebuild, GiProfileEdit.Classify(States(first), Bound(first), States(second), Bound(second)));
        }

        [Fact]
        public void CellEditsChangeStatesOnly()
        {
            var first = Base();
            var second = Base();
            second.Cells[0] += 1;
            Assert.NotEqual(States(first), States(second));
            Assert.Equal(Bound(first), Bound(second));
            Assert.NotEqual(Full(first), Full(second));
            Assert.Equal(GiProfileEditKind.Rebuild, GiProfileEdit.Classify(States(first), Bound(first), States(second), Bound(second)));
        }

        [Fact]
        public void AddedBindingsChangeBindingsOnly()
        {
            var first = Base();
            var second = Base();
            second.Bindings = new[] { ProbeBinding() };
            Assert.Equal(States(first), States(second));
            Assert.NotEqual(Bound(first), Bound(second));
            Assert.NotEqual(Full(first), Full(second));
            Assert.Equal(GiProfileEditKind.BindingsOnly, GiProfileEdit.Classify(States(first), Bound(first), States(second), Bound(second)));
        }

        [Fact]
        public void TweakedBindingsChangeBindingsOnly()
        {
            var first = Base();
            first.Bindings = new[] { ProbeBinding() };
            var second = Base();
            second.Bindings = new[] { ProbeBinding() };
            second.Bindings[0].Threshold += 1;
            Assert.Equal(States(first), States(second));
            Assert.NotEqual(Bound(first), Bound(second));
            Assert.NotEqual(Full(first), Full(second));
            Assert.Equal(GiProfileEditKind.BindingsOnly, GiProfileEdit.Classify(States(first), Bound(first), States(second), Bound(second)));
        }

        [Fact]
        public void RemovedBindingsChangeBindingsOnly()
        {
            var first = Base();
            first.Bindings = new[] { ProbeBinding() };
            var second = Base();
            Assert.Equal(States(first), States(second));
            Assert.NotEqual(Bound(first), Bound(second));
            Assert.NotEqual(Full(first), Full(second));
            Assert.Equal(GiProfileEditKind.BindingsOnly, GiProfileEdit.Classify(States(first), Bound(first), States(second), Bound(second)));
        }

        [Fact]
        public void IdenticalSectionsClassifyRebuild()
        {
            var spec = Base();
            Assert.Equal(GiProfileEditKind.Rebuild, GiProfileEdit.Classify(States(spec), Bound(spec), States(spec), Bound(spec)));
        }
    }
}
