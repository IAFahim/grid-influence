```

BenchmarkDotNet v0.15.8, Linux Omarchy
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 2.38GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Jit      : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4


```
| Method        | Job      | IterationCount | IterationTime | LaunchCount | WarmupCount | StampCount | Extent | Decay | Mean         | Error       | StdDev    | Median       | Ratio | RatioSD | Allocated | Alloc Ratio |
|-------------- |--------- |--------------- |-------------- |------------ |------------ |----------- |------- |------ |-------------:|------------:|----------:|-------------:|------:|--------:|----------:|------------:|
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **256**    | **False** |     **157.5 μs** |     **0.78 μs** |   **0.47 μs** |     **157.4 μs** |  **1.00** |    **0.00** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 256    | False |     206.5 μs |     1.66 μs |   1.10 μs |     206.7 μs |  1.31 |    0.01 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |             |           |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 256    | False |     161.8 μs |    47.53 μs |   2.61 μs |     163.2 μs |  1.00 |    0.02 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 256    | False |     204.8 μs |     6.93 μs |   0.38 μs |     204.7 μs |  1.27 |    0.02 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |             |           |              |       |         |           |             |
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **256**    | **True**  |   **1,844.2 μs** |     **9.10 μs** |   **5.42 μs** |   **1,843.9 μs** |  **1.00** |    **0.00** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 256    | True  |     387.5 μs |     2.43 μs |   1.27 μs |     387.4 μs |  0.21 |    0.00 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |             |           |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 256    | True  |   1,856.2 μs |   347.40 μs |  19.04 μs |   1,846.3 μs |  1.00 |    0.01 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 256    | True  |     392.3 μs |    93.83 μs |   5.14 μs |     392.4 μs |  0.21 |    0.00 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |             |           |              |       |         |           |             |
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **1024**   | **False** |     **317.5 μs** |     **9.55 μs** |   **5.68 μs** |     **320.4 μs** |  **1.00** |    **0.02** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 1024   | False |     914.2 μs |    15.49 μs |   9.22 μs |     908.8 μs |  2.88 |    0.06 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |             |           |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 1024   | False |     311.5 μs |    39.50 μs |   2.17 μs |     311.9 μs |  1.00 |    0.01 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 1024   | False |     910.5 μs |   121.27 μs |   6.65 μs |     907.8 μs |  2.92 |    0.03 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |             |           |              |       |         |           |             |
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **1024**   | **True**  |  **28,357.3 μs** | **1,026.84 μs** | **679.19 μs** |  **28,097.9 μs** |  **1.00** |    **0.03** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 1024   | True  |   3,646.0 μs |    18.40 μs |  10.95 μs |   3,647.5 μs |  0.13 |    0.00 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |             |           |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 1024   | True  |  27,932.2 μs | 5,328.59 μs | 292.08 μs |  27,773.4 μs |  1.00 |    0.01 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 1024   | True  |   3,686.2 μs |   339.97 μs |  18.63 μs |   3,682.1 μs |  0.13 |    0.00 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |             |           |              |       |         |           |             |
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **2048**   | **False** |   **1,496.6 μs** |    **20.37 μs** |  **12.12 μs** |   **1,498.3 μs** |  **1.00** |    **0.01** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 2048   | False |   1,072.6 μs |    12.44 μs |   7.40 μs |   1,072.7 μs |  0.72 |    0.01 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |             |           |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 2048   | False |   1,520.6 μs |   161.86 μs |   8.87 μs |   1,515.8 μs |  1.00 |    0.01 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 2048   | False |   1,077.3 μs |    64.43 μs |   3.53 μs |   1,077.8 μs |  0.71 |    0.00 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |             |           |              |       |         |           |             |
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **2048**   | **True**  | **119,367.0 μs** |   **863.65 μs** | **513.95 μs** | **119,271.7 μs** |  **1.00** |    **0.01** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 2048   | True  |   5,208.2 μs |    42.92 μs |  28.39 μs |   5,201.7 μs |  0.04 |    0.00 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |             |           |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 2048   | True  | 118,919.1 μs |   671.13 μs |  36.79 μs | 118,905.5 μs |  1.00 |    0.00 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 2048   | True  |   5,658.3 μs |   741.14 μs |  40.62 μs |   5,645.4 μs |  0.05 |    0.00 |         - |          NA |
