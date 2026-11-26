using Unity.Burst;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Gi.Entities
{
    [GiConfigurable]
    [NoAutoStaticsCleanup]
    public static class GiDebugConfig
    {
        private struct GuardKey
        {
        }

        private struct HeatmapKey
        {
        }

        private struct RecordKey
        {
        }

        private struct RecordTicksKey
        {
        }

        private struct RecordKeyframeKey
        {
        }

        [GiConfigVar("gi.guard", true, "In checked builds, report two Gi systems that read and write one Gi world at once.")]
        public static readonly SharedStatic<bool> Guard = SharedStatic<bool>.GetOrCreate<GuardKey>();

        [GiConfigVar("gi.draw.heatmap", true, "Draw the Gi heatmap in the Scene view overlay and the Field window.")]
        public static readonly SharedStatic<bool> Heatmap = SharedStatic<bool>.GetOrCreate<HeatmapKey>();

        [GiConfigVar("gi.record", false, "Record every Gi input of every tick into a ring buffer so a range can be replayed, diffed and exported.")]
        public static readonly SharedStatic<bool> Record = SharedStatic<bool>.GetOrCreate<RecordKey>();

        [GiConfigVar("gi.record.ticks", 600, "Ticks the flight recorder keeps.")]
        public static readonly SharedStatic<int> RecordTicks = SharedStatic<int>.GetOrCreate<RecordTicksKey>();

        [GiConfigVar("gi.record.keyframe", 120, "Ticks between flight recorder keyframes.")]
        public static readonly SharedStatic<int> RecordKeyframe = SharedStatic<int>.GetOrCreate<RecordKeyframeKey>();
        [GiConfigVar("gi.draw.enabled", true, "Master toggle for the runtime Gi field draw in the game view.")]
        public static readonly SharedStatic<bool> DrawEnabled = SharedStatic<bool>.GetOrCreate<DrawEnabledTag>();

        [GiConfigVar("gi.draw.layer", 0, "Layer index the runtime field draw shows its heat for.")]
        public static readonly SharedStatic<int> DrawLayer = SharedStatic<int>.GetOrCreate<DrawLayerTag>();

        [GiConfigVar("gi.draw.heat", true, "Draw influence as filled heat tiles in the game view.")]
        public static readonly SharedStatic<bool> DrawHeat = SharedStatic<bool>.GetOrCreate<DrawHeatTag>();

        [GiConfigVar("gi.draw.cells", false, "Draw one quad per cell instead of per 32x32 tile. Expensive on large grids.")]
        public static readonly SharedStatic<bool> DrawCells = SharedStatic<bool>.GetOrCreate<DrawCellsTag>();

        [GiConfigVar("gi.draw.bounds", true, "Draw each grid's world bounds as a lifted cuboid.")]
        public static readonly SharedStatic<bool> DrawBounds = SharedStatic<bool>.GetOrCreate<DrawBoundsTag>();

        [GiConfigVar("gi.draw.sources", true, "Draw a marker and a stamp ring on every placed Gi source.")]
        public static readonly SharedStatic<bool> DrawSources = SharedStatic<bool>.GetOrCreate<DrawSourcesTag>();

        [GiConfigVar("gi.draw.bindings", true, "Draw live binding results: gradient arrows, max lines, nearest links.")]
        public static readonly SharedStatic<bool> DrawBindings = SharedStatic<bool>.GetOrCreate<DrawBindingsTag>();

        [GiConfigVar("gi.draw.values", false, "Label each heat tile with its strongest value.")]
        public static readonly SharedStatic<bool> DrawValues = SharedStatic<bool>.GetOrCreate<DrawValuesTag>();

        [GiConfigVar("gi.draw.cull", true, "Frustum-cull tiles, sources and binding results against the draw camera.")]
        public static readonly SharedStatic<bool> DrawCull = SharedStatic<bool>.GetOrCreate<DrawCullTag>();

        [GiConfigVar("gi.draw.cull-other", true, "Editor only: also cull-test against the other view's camera so draws show in both views.")]
        public static readonly SharedStatic<bool> DrawCullOther = SharedStatic<bool>.GetOrCreate<DrawCullOtherTag>();

        [GiConfigVar("gi.draw.height", 0.05f, "World height the field draw is lifted to.")]
        public static readonly SharedStatic<float> DrawHeight = SharedStatic<float>.GetOrCreate<DrawHeightTag>();

        [GiConfigVar("gi.draw.gridstep", 0.05f, "Extra lift per grid index so overlapping grids do not z-fight.")]
        public static readonly SharedStatic<float> DrawGridStep = SharedStatic<float>.GetOrCreate<DrawGridStepTag>();

        [GiConfigVar("gi.draw.scale", 0f, "Heat normalisation divisor. 0 auto-normalises to the strongest value this frame.")]
        public static readonly SharedStatic<float> DrawScale = SharedStatic<float>.GetOrCreate<DrawScaleTag>();

        [GiConfigVar("gi.draw.bounds0", 0.3f, 0.8f, 1f, 0.7f, "Bounds colour of the first grid.")]
        public static readonly SharedStatic<Color> DrawBounds0 = SharedStatic<Color>.GetOrCreate<DrawBounds0Tag>();

        [GiConfigVar("gi.draw.bounds1", 1f, 0.6f, 0.2f, 0.7f, "Bounds colour of the second grid (higher grids repeat from the first).")]
        public static readonly SharedStatic<Color> DrawBounds1 = SharedStatic<Color>.GetOrCreate<DrawBounds1Tag>();

        [GiConfigVar("gi.draw.bounds2", 0.5f, 1f, 0.4f, 0.7f, "Bounds colour of the third grid.")]
        public static readonly SharedStatic<Color> DrawBounds2 = SharedStatic<Color>.GetOrCreate<DrawBounds2Tag>();

        [GiConfigVar("gi.draw.bounds3", 1f, 0.4f, 0.8f, 0.7f, "Bounds colour of the fourth grid.")]
        public static readonly SharedStatic<Color> DrawBounds3 = SharedStatic<Color>.GetOrCreate<DrawBounds3Tag>();

        [GiConfigVar("gi.draw.tile-outline", 1f, 1f, 1f, 0.12f, "Outline drawn around each heat tile.")]
        public static readonly SharedStatic<Color> DrawTileOutline = SharedStatic<Color>.GetOrCreate<DrawTileOutlineTag>();

        [GiConfigVar("gi.draw.heat-cold", 0.15f, 0.45f, 1f, 0.1f, "Heat colour at zero influence.")]
        public static readonly SharedStatic<Color> DrawHeatCold = SharedStatic<Color>.GetOrCreate<DrawHeatColdTag>();

        [GiConfigVar("gi.draw.heat-hot", 1f, 0.2f, 0.05f, 0.55f, "Heat colour at the strongest influence.")]
        public static readonly SharedStatic<Color> DrawHeatHot = SharedStatic<Color>.GetOrCreate<DrawHeatHotTag>();

        [GiConfigVar("gi.draw.source", 0.3f, 1f, 0.5f, 0.9f, "Source marker and stamp ring colour.")]
        public static readonly SharedStatic<Color> DrawSource = SharedStatic<Color>.GetOrCreate<DrawSourceTag>();

        [GiConfigVar("gi.draw.binding-gradient", 0.4f, 1f, 0.4f, 0.9f, "Gradient binding arrow colour.")]
        public static readonly SharedStatic<Color> DrawBindingGradient = SharedStatic<Color>.GetOrCreate<DrawBindingGradientTag>();

        [GiConfigVar("gi.draw.binding-max", 1f, 0.3f, 0.9f, 0.9f, "Max binding line-to-best-cell colour.")]
        public static readonly SharedStatic<Color> DrawBindingMax = SharedStatic<Color>.GetOrCreate<DrawBindingMaxTag>();

        [GiConfigVar("gi.draw.binding-nearest", 1f, 0.6f, 0.15f, 0.9f, "Nearest binding link and reach circle colour.")]
        public static readonly SharedStatic<Color> DrawBindingNearest = SharedStatic<Color>.GetOrCreate<DrawBindingNearestTag>();

        [GiConfigVar("gi.draw.value-color", 1f, 1f, 1f, 0.9f, "Tile value label colour.")]
        public static readonly SharedStatic<Color> DrawValueColor = SharedStatic<Color>.GetOrCreate<DrawValueColorTag>();

        private struct DrawEnabledTag
        {
        }

        private struct DrawLayerTag
        {
        }

        private struct DrawHeatTag
        {
        }

        private struct DrawCellsTag
        {
        }

        private struct DrawBoundsTag
        {
        }

        private struct DrawSourcesTag
        {
        }

        private struct DrawBindingsTag
        {
        }

        private struct DrawValuesTag
        {
        }

        private struct DrawCullTag
        {
        }

        private struct DrawCullOtherTag
        {
        }

        private struct DrawHeightTag
        {
        }

        private struct DrawGridStepTag
        {
        }

        private struct DrawScaleTag
        {
        }

        private struct DrawBounds0Tag
        {
        }

        private struct DrawBounds1Tag
        {
        }

        private struct DrawBounds2Tag
        {
        }

        private struct DrawBounds3Tag
        {
        }

        private struct DrawTileOutlineTag
        {
        }

        private struct DrawHeatColdTag
        {
        }

        private struct DrawHeatHotTag
        {
        }

        private struct DrawSourceTag
        {
        }

        private struct DrawBindingGradientTag
        {
        }

        private struct DrawBindingMaxTag
        {
        }

        private struct DrawBindingNearestTag
        {
        }

        private struct DrawValueColorTag
        {
        }
    }
}
