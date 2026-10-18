using UnityEngine;

namespace Gi.Entities.Authoring
{
    public enum GiLayerPrecision : byte
    {
        Exact,
        Half,
        Quarter,
        Cell,
        Custom,
    }

    public sealed class GiLayerAsset : ScriptableObject
    {
        [HideInInspector] public GiProfile Owner;
        public GiLayerKind Kind;
        public GiLayerPrecision Precision;
        public byte PhaseBits = 8;
        public byte TurnBits = 16;
        public GiLayerAsset First;
        public GiLayerAsset Second;
        public int FirstWeight = 1;
        public int SecondWeight = 1;
        public int Shift;
        public short MaskLow;
        public short MaskHigh = 1;

        public bool IsBase => Kind == GiLayerKind.Persistent || Kind == GiLayerKind.Transient;

        public void Bits(out byte phaseBits, out byte turnBits)
        {
            switch (Precision)
            {
                case GiLayerPrecision.Exact:
                    phaseBits = 8;
                    turnBits = 16;
                    break;
                case GiLayerPrecision.Half:
                    phaseBits = 1;
                    turnBits = 16;
                    break;
                case GiLayerPrecision.Quarter:
                    phaseBits = 2;
                    turnBits = 16;
                    break;
                case GiLayerPrecision.Cell:
                    phaseBits = 0;
                    turnBits = 16;
                    break;
                default:
                    phaseBits = PhaseBits;
                    turnBits = TurnBits;
                    break;
            }
        }
    }
}
