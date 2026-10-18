using BovineLabs.Savanna;
using Unity.Entities;

namespace Gi.Entities
{
    [Save]
    public struct GiSavedSource : IComponentData
    {
        public byte LayerKey;
        public byte StampKey;
        public sbyte AppliedGain;
        public sbyte Gain;
        public float X;
        public float Y;
        public float Heading;
        public float Scale;
        public sbyte FadeFrom;
        public sbyte FadeTo;
        public int FadeTicks;
        public int FadeTicksLeft;
        public int ExpireTicksLeft;

        public GiSourceState ToState()
        {
            return new GiSourceState
            {
                LayerKey = LayerKey,
                StampKey = StampKey,
                Gain = Gain,
                X = X,
                Y = Y,
                Heading = Heading,
                Scale = Scale,
                FadeFrom = FadeFrom,
                FadeTo = FadeTo,
                FadeTicks = FadeTicks,
                FadeTicksLeft = FadeTicksLeft,
                ExpireTicksLeft = ExpireTicksLeft,
            };
        }

        public static GiSavedSource Of(in GiSourceState state, sbyte appliedGain)
        {
            return new GiSavedSource
            {
                LayerKey = state.LayerKey,
                StampKey = state.StampKey,
                AppliedGain = appliedGain,
                Gain = state.Gain,
                X = state.X,
                Y = state.Y,
                Heading = state.Heading,
                Scale = state.Scale,
                FadeFrom = state.FadeFrom,
                FadeTo = state.FadeTo,
                FadeTicks = state.FadeTicks,
                FadeTicksLeft = state.FadeTicksLeft,
                ExpireTicksLeft = state.ExpireTicksLeft,
            };
        }
    }

    [Save]
    public struct GiSavedPulse : IBufferElementData
    {
        public byte LayerKey;
        public byte StampKey;
        public sbyte Gain;
        public float X;
        public float Y;
        public float Heading;
        public float Scale;
        public sbyte FadeFrom;
        public sbyte FadeTo;
        public int FadeTicks;
        public int FadeTicksLeft;
        public int ExpireTicksLeft;

        public GiSourceState ToState()
        {
            return new GiSourceState
            {
                LayerKey = LayerKey,
                StampKey = StampKey,
                Gain = Gain,
                X = X,
                Y = Y,
                Heading = Heading,
                Scale = Scale,
                FadeFrom = FadeFrom,
                FadeTo = FadeTo,
                FadeTicks = FadeTicks,
                FadeTicksLeft = FadeTicksLeft,
                ExpireTicksLeft = ExpireTicksLeft,
            };
        }

        public static GiSavedPulse Of(in GiSourceState state)
        {
            return new GiSavedPulse
            {
                LayerKey = state.LayerKey,
                StampKey = state.StampKey,
                Gain = state.Gain,
                X = state.X,
                Y = state.Y,
                Heading = state.Heading,
                Scale = state.Scale,
                FadeFrom = state.FadeFrom,
                FadeTo = state.FadeTo,
                FadeTicks = state.FadeTicks,
                FadeTicksLeft = state.FadeTicksLeft,
                ExpireTicksLeft = state.ExpireTicksLeft,
            };
        }
    }

    [Save]
    public struct GiSaveHolder : IComponentData
    {
    }
}
