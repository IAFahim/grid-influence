```

BenchmarkDotNet v0.15.8, Linux Omarchy
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 2.38GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Jit      : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4


```
| Method        | Job      | IterationCount | IterationTime | LaunchCount | WarmupCount | Mean     | Error     | StdDev    | Median   | Gen0     | Gen1     | Gen2     | Allocated |
|-------------- |--------- |--------------- |-------------- |------------ |------------ |---------:|----------:|----------:|---------:|---------:|---------:|---------:|----------:|
| PpmEncode1024 | Jit      | 10             | 250ms         | Default     | 8           | 3.413 ms | 0.0653 ms | 0.0432 ms | 3.395 ms | 175.0000 | 175.0000 | 175.0000 |      2 MB |
| PpmEncode1024 | ShortRun | 3              | Default       | 1           | 3           | 3.439 ms | 0.9424 ms | 0.0517 ms | 3.443 ms | 191.4063 | 191.4063 | 191.4063 |      2 MB |
