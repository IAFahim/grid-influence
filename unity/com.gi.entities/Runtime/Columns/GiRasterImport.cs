namespace Gi.Entities
{
    public static class GiRasterImport
    {
        public static sbyte[] FromGray(byte[] gray, int width, int height)
        {
            var cells = new sbyte[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                cells[y * width + x] = (sbyte)(gray[y * width + x] - 128);
            return cells;
        }

        public static byte Gray(byte red, byte green, byte blue, byte alpha)
        {
            var luma = (red * 54 + green * 183 + blue * 19 + 128) >> 8;
            return (byte)((luma * alpha + 127) / 255);
        }
    }
}
