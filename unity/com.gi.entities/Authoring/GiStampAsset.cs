using System.Collections.Generic;
using UnityEngine;

namespace Gi.Entities.Authoring
{
    public sealed class GiStampAsset : ScriptableObject
    {
        public GiStampShape Shape;
        public ushort Width = 3;
        public ushort Height = 3;
        public ushort Radius = 4;
        public ushort Arc = 360;
        public sbyte Value = 50;
        public sbyte[] Cells = System.Array.Empty<sbyte>();

        public bool IsRaster => Shape == GiStampShape.Raster;

        public GiStampSpec ToSpec(List<sbyte> cells)
        {
            var spec = new GiStampSpec { Shape = Shape, Value = Value };
            if (Shape == GiStampShape.Raster)
            {
                spec.Width = Width;
                spec.Height = Height;
                spec.CellOffset = cells.Count;
                spec.CellCount = Cells.Length;
                cells.AddRange(Cells);
            }
            else if (Shape == GiStampShape.Disk || Shape == GiStampShape.Cone || Shape == GiStampShape.Dome)
            {
                spec.Radius = Radius;
                spec.Arc = Arc;
            }
            else
            {
                spec.Width = Width;
                spec.Height = Height;
            }

            return spec;
        }

        public static sbyte[] FromGray(byte[] gray, int width, int height)
        {
            var cells = new sbyte[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                cells[y * width + x] = (sbyte)(gray[y * width + x] - 128);
            return cells;
        }
    }
}
