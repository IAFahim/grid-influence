internal sealed class NaiveGrid(int size)
{
    private readonly int _size = size;
    private readonly int[] _field = new int[size * size];
    private (float X, float Y, int W, int H, int Value, int Gain)[] _sources = new (float, float, int, int, int, int)[64];
    private int _count;

    public int Add(float x, float y, int w, int h, int value, int gain)
    {
        if (_count == _sources.Length) Array.Resize(ref _sources, _sources.Length * 2);
        _sources[_count] = (x, y, w, h, value, gain);
        return _count++;
    }

    public void Move(int source, float x, float y)
    {
        var existing = _sources[source];
        _sources[source] = (x, y, existing.W, existing.H, existing.Value, existing.Gain);
    }

    public void Rebuild()
    {
        Array.Clear(_field);
        for (var s = 0; s < _count; s++)
        {
            var source = _sources[s];
            var cx = (int)source.X - source.W / 2;
            var cy = (int)source.Y - source.H / 2;
            var add = source.Value * source.Gain;
            for (var y = 0; y < source.H; y++)
            {
                var row = (cy + y) * _size + cx;
                for (var x = 0; x < source.W; x++) _field[row + x] += add;
            }
        }
    }

    public short Query(int x, int y)
        => (short)Math.Clamp(_field[y * _size + x], short.MinValue, short.MaxValue);

    public long Sum()
    {
        var sum = 0L;
        foreach (var cell in _field) sum += Math.Clamp(cell, short.MinValue, short.MaxValue);
        return sum;
    }
}
