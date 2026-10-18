using BovineLabs.Core.ConfigVars;
using Unity.Burst;
using UnityEngine;

namespace Gi.Entities
{
    [Configurable]
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

        [ConfigVar("gi.guard", true, "In checked builds, report two Gi systems that read and write one Gi world at once.")]
        public static readonly SharedStatic<bool> Guard = SharedStatic<bool>.GetOrCreate<GuardKey>();

        [ConfigVar("gi.draw.heatmap", true, "Draw the Gi heatmap in the Scene view overlay and the Field window.")]
        public static readonly SharedStatic<bool> Heatmap = SharedStatic<bool>.GetOrCreate<HeatmapKey>();

        [ConfigVar("gi.record", false, "Record every Gi input of every tick into a ring buffer so a range can be replayed, diffed and exported.")]
        public static readonly SharedStatic<bool> Record = SharedStatic<bool>.GetOrCreate<RecordKey>();

        [ConfigVar("gi.record.ticks", 600, "Ticks the flight recorder keeps.")]
        public static readonly SharedStatic<int> RecordTicks = SharedStatic<int>.GetOrCreate<RecordTicksKey>();

        [ConfigVar("gi.record.keyframe", 120, "Ticks between flight recorder keyframes.")]
        public static readonly SharedStatic<int> RecordKeyframe = SharedStatic<int>.GetOrCreate<RecordKeyframeKey>();
        [ConfigVar("gi.draw.enabled", true, "Master toggle for the runtime Gi field draw in the game view.")]
        public static readonly SharedStatic<bool> DrawEnabled = SharedStatic<bool>.GetOrCreate<DrawEnabledTag>();

        [ConfigVar("gi.draw.layer", 0, "Layer index the runtime field draw shows its heat for.")]
        public static readonly SharedStatic<int> DrawLayer = SharedStatic<int>.GetOrCreate<DrawLayerTag>();

        [ConfigVar("gi.draw.heat", true, "Draw influence as filled heat tiles in the game view.")]
        public static readonly SharedStatic<bool> DrawHeat = SharedStatic<bool>.GetOrCreate<DrawHeatTag>();

        [ConfigVar("gi.draw.cells", false, "Draw one quad per cell instead of per 32x32 tile. Expensive on large grids.")]
        public static readonly SharedStatic<bool> DrawCells = SharedStatic<bool>.GetOrCreate<DrawCellsTag>();

        [ConfigVar("gi.draw.bounds", true, "Draw each grid's world bounds as a lifted cuboid.")]
        public static readonly SharedStatic<bool> DrawBounds = SharedStatic<bool>.GetOrCreate<DrawBoundsTag>();

        [ConfigVar("gi.draw.sources", true, "Draw a marker and a stamp ring on every placed Gi source.")]
        public static readonly SharedStatic<bool> DrawSources = SharedStatic<bool>.GetOrCreate<DrawSourcesTag>();

        [ConfigVar("gi.draw.bindings", true, "Draw live binding results: gradient arrows, max lines, nearest links.")]
        public static readonly SharedStatic<bool> DrawBindings = SharedStatic<bool>.GetOrCreate<DrawBindingsTag>();

        [ConfigVar("gi.draw.values", false, "Label each heat tile with its strongest value.")]
        public static readonly SharedStatic<bool> DrawValues = SharedStatic<bool>.GetOrCreate<DrawValuesTag>();

        [ConfigVar("gi.draw.cull", true, "Frustum-cull tiles, sources and binding results against the draw camera.")]
        public static readonly SharedStatic<bool> DrawCull = SharedStatic<bool>.GetOrCreate<DrawCullTag>();

        [ConfigVar("gi.draw.cull-other", true, "Editor only: also cull-test against the other view's camera so draws show in both views.")]
        public static readonly SharedStatic<bool> DrawCullOther = SharedStatic<bool>.GetOrCreate<DrawCullOtherTag>();

        [ConfigVar("gi.draw.height", 0.05f, "World height the field draw is lifted to.")]
        public static readonly SharedStatic<float> DrawHeight = SharedStatic<float>.GetOrCreate<DrawHeightTag>();

        [ConfigVar("gi.draw.gridstep", 0.05f, "Extra lift per grid index so overlapping grids do not z-fight.")]
        public static readonly SharedStatic<float> DrawGridStep = SharedStatic<float>.GetOrCreate<DrawGridStepTag>();

        [ConfigVar("gi.draw.scale", 0f, "Heat normalisation divisor. 0 auto-normalises to the strongest value this frame.")]
        public static readonly SharedStatic<float> DrawScale = SharedStatic<float>.GetOrCreate<DrawScaleTag>();

        [ConfigVar("gi.draw.bounds0", 0.3f, 0.8f, 1f, 0.7f, "Bounds colour of the first grid.")]
        public static readonly SharedStatic<Color> DrawBounds0 = SharedStatic<Color>.GetOrCreate<DrawBounds0Tag>();

        [ConfigVar("gi.draw.bounds1", 1f, 0.6f, 0.2f, 0.7f, "Bounds colour of the second grid (higher grids repeat from the first).")]
        public static readonly SharedStatic<Color> DrawBounds1 = SharedStatic<Color>.GetOrCreate<DrawBounds1Tag>();

        [ConfigVar("gi.draw.bounds2", 0.5f, 1f, 0.4f, 0.7f, "Bounds colour of the third grid.")]
        public static readonly SharedStatic<Color> DrawBounds2 = SharedStatic<Color>.GetOrCreate<DrawBounds2Tag>();

        [ConfigVar("gi.draw.bounds3", 1f, 0.4f, 0.8f, 0.7f, "Bounds colour of the fourth grid.")]
        public static readonly SharedStatic<Color> DrawBounds3 = SharedStatic<Color>.GetOrCreate<DrawBounds3Tag>();

        [ConfigVar("gi.draw.tile-outline", 1f, 1f, 1f, 0.12f, "Outline drawn around each heat tile.")]
        public static readonly SharedStatic<Color> DrawTileOutline = SharedStatic<Color>.GetOrCreate<DrawTileOutlineTag>();

        [ConfigVar("gi.draw.heat-cold", 0.15f, 0.45f, 1f, 0.1f, "Heat colour at zero influence.")]
        public static readonly SharedStatic<Color> DrawHeatCold = SharedStatic<Color>.GetOrCreate<DrawHeatColdTag>();

        [ConfigVar("gi.draw.heat-hot", 1f, 0.2f, 0.05f, 0.55f, "Heat colour at the strongest influence.")]
        public static readonly SharedStatic<Color> DrawHeatHot = SharedStatic<Color>.GetOrCreate<DrawHeatHotTag>();

        [ConfigVar("gi.draw.source", 0.3f, 1f, 0.5f, 0.9f, "Source marker and stamp ring colour.")]
        public static readonly SharedStatic<Color> DrawSource = SharedStatic<Color>.GetOrCreate<DrawSourceTag>();

        [ConfigVar("gi.draw.binding-gradient", 0.4f, 1f, 0.4f, 0.9f, "Gradient binding arrow colour.")]
        public static readonly SharedStatic<Color> DrawBindingGradient = SharedStatic<Color>.GetOrCreate<DrawBindingGradientTag>();

        [ConfigVar("gi.draw.binding-max", 1f, 0.3f, 0.9f, 0.9f, "Max binding line-to-best-cell colour.")]
        public static readonly SharedStatic<Color> DrawBindingMax = SharedStatic<Color>.GetOrCreate<DrawBindingMaxTag>();

        [ConfigVar("gi.draw.binding-nearest", 1f, 0.6f, 0.15f, 0.9f, "Nearest binding link and reach circle colour.")]
        public static readonly SharedStatic<Color> DrawBindingNearest = SharedStatic<Color>.GetOrCreate<DrawBindingNearestTag>();

        [ConfigVar("gi.draw.value-color", 1f, 1f, 1f, 0.9f, "Tile value label colour.")]
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
