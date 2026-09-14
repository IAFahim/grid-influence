using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Perfolizer.Horology;
using GridInfluence;
using GridInfluence.Io;

namespace Benchmarks;

public sealed class InfluenceConfig : ManualConfig
{
    public InfluenceConfig()
    {
        var job = Job.Default
            .WithWarmupCount(8)
            .WithIterationCount(10)
            .WithIterationTime(TimeInterval.FromMilliseconds(250));
        if (Environment.GetEnvironmentVariable("GRIDINFLUENCE_IN_PROCESS") == "1")
            job = job.WithToolchain(InProcessNoEmitToolchain.Instance);
        AddJob(job.WithId("Jit"));
        AddColumn(StatisticColumn.Median);
        AddDiagnoser(MemoryDiagnoser.Default);
        AddExporter(JsonExporter.Full);
        WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(40));
    }
}

[Config(typeof(InfluenceConfig))]
public class TickBenchmarks
{
    [Params(256)]
    public int StampCount { get; set; }

    [Params(256, 1024, 2048)]
    public int Extent { get; set; }

    [Params(true, false)]
    public bool Decay { get; set; }

    private Stamp[] _stamps = [];
    private NaiveField _naive = null!;
    private PipelineField _pipeline = null!;

    [GlobalSetup]
    public void Setup()
    {
        _stamps = Fixtures.BuildStamps(StampCount, Extent);
        _naive = new NaiveField(Extent, Decay ? Fixtures.DecayPerMille : 0, Fixtures.SpreadDenominator);
        _pipeline = new PipelineField(5);
    }

    [GlobalCleanup]
    public void Cleanup() => _pipeline.Dispose();

    [Benchmark(Baseline = true)]
    public int NaiveScatter()
    {
        for (var tick = 0; tick < 4; tick++) _naive.Tick(_stamps);

        return _naive[0, 0];
    }

    [Benchmark]
    public int FieldPipeline()
    {
        if (Decay) for (var tick = 0; tick < 4; tick++) _pipeline.Tick(_stamps);
        else for (var tick = 0; tick < 4; tick++) _pipeline.TickNoDecay(_stamps);

        return (int)_pipeline.Front.FrameId;
    }
}

[Config(typeof(InfluenceConfig))]
public class QueryBenchmarks
{
    private const int Operations = 1024;

    private PipelineField _pipeline = null!;
    private NaiveField _naive = null!;
    private int[] _queryCells = [];
    private int[] _captureBuffer = new int[32 * 32];

    [GlobalSetup]
    public void Setup()
    {
        const int extent = 1024;
        var stamps = Fixtures.BuildStamps(256, extent);
        _pipeline = new PipelineField(5);
        for (var tick = 0; tick < 8; tick++) _pipeline.Tick(stamps);
        _naive = new NaiveField(extent, Fixtures.DecayPerMille, Fixtures.SpreadDenominator);
        for (var tick = 0; tick < 8; tick++) _naive.Tick(stamps);

        _queryCells = new int[Operations * 2];
        var state = 0x1234567u;
        for (var i = 0; i < _queryCells.Length; i += 2)
        {
            state = state * 1664525u + 1013904223u;
            _queryCells[i] = (int)(state % 1023u) + 1;
            state = state * 1664525u + 1013904223u;
            _queryCells[i + 1] = (int)(state % 1023u) + 1;
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _pipeline.Dispose();

    [Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
    public int ReadCellPipeline()
    {
        var reader = _pipeline.Front.AsReader();
        var acc = 0;
        for (var i = 0; i < _queryCells.Length; i += 2) acc += reader.ReadCell(new Int2(_queryCells[i], _queryCells[i + 1]));

        return acc;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public int GradientPipeline()
    {
        var reader = _pipeline.Front.AsReader();
        var acc = 0;
        for (var i = 0; i < _queryCells.Length; i += 2) acc += reader.Gradient(new Int2(_queryCells[i], _queryCells[i + 1])).X;

        return acc;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public float SampleBilinearPipeline()
    {
        var reader = _pipeline.Front.AsReader();
        var acc = 0f;
        for (var i = 0; i < _queryCells.Length; i += 2) acc += reader.SampleBilinear(_queryCells[i] + 0.5f, _queryCells[i + 1] + 0.5f);

        return acc;
    }

    [Benchmark(OperationsPerInvoke = Operations / 32)]
    public int Capture32Pipeline()
    {
        var reader = _pipeline.Front.AsReader();
        var acc = 0;
        for (var i = 0; i < _queryCells.Length; i += 64)
        {
            Capture.Score(reader, new Int2(_queryCells[i], _queryCells[i + 1]), new Int2(32, 32));
            acc += _queryCells[i];
        }

        return acc;
    }
}

[Config(typeof(InfluenceConfig))]
public class IoBenchmarks
{
    private const int Size = 1024;

    private byte[] _encoded = [];
    private WeightMap _map = null!;
    private InfluenceField _field = null!;
    private int[] _region = new int[Size * Size];
    private MemoryStream _stream = new(Size * Size * 2 + 64);

    [GlobalSetup]
    public void Setup()
    {
        _map = new WeightMap(Size, Size, 65535, 32768);
        var state = 0xABCDEF01u;
        var samples = _map.Samples;
        for (var i = 0; i < samples.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            samples[i] = (int)(state % 65535u) - 32768;
        }

        var path = Path.Combine(Path.GetTempPath(), "tlinfluence-bench.pgm");
        Pnm.SaveGraySigned(path, samples, Size, Size, 65535);
        _encoded = File.ReadAllBytes(path);
        File.Delete(path);

        _field = new InfluenceField(GridSpec.FromPowerOfTwo(5, uint.MaxValue));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _map.Dispose();
        _field.Dispose();
    }

    [Benchmark]
    public WeightMap PpmDecode1024()
    {
        var map = new WeightMap(Size, Size, 65535, 32768);
        var header = Pnm.ParseHeader(_encoded);
        unsafe
        {
            Pnm.DecodeGray(header, _encoded, map.SamplePointer, map.Bias);
        }

        map.Dispose();
        return map;
    }

    [Benchmark]
    public long PpmEncode1024()
    {
        _stream.Seek(0, SeekOrigin.Begin);
        Pnm.SaveGray(_map.Samples, Size, Size, _stream, 65535);
        return _stream.Length;
    }

    [Benchmark]
    public void FieldInject1024() => _field.WriteRegion(Int2.Zero, new Int2(Size, Size), _map.Samples);

    [Benchmark]
    public long FieldExport1024()
    {
        _field.ReadRegion(Int2.Zero, new Int2(Size, Size), _region);
        return _region[0];
    }
}
