```

BenchmarkDotNet v0.15.8, Linux Omarchy
AMD Ryzen 5 8500G w/ Radeon 740M Graphics 3.34GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Jit      : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4


```
| Method        | Job      | IterationCount | IterationTime | LaunchCount | WarmupCount | StampCount | Extent | Decay | Mean         | Error        | StdDev      | Median       | Ratio | RatioSD | Allocated | Alloc Ratio |
|-------------- |--------- |--------------- |-------------- |------------ |------------ |----------- |------- |------ |-------------:|-------------:|------------:|-------------:|------:|--------:|----------:|------------:|
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **256**    | **False** |     **169.5 μs** |      **6.15 μs** |     **3.66 μs** |     **169.3 μs** |  **1.00** |    **0.03** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 256    | False |     181.6 μs |      3.12 μs |     1.63 μs |     180.9 μs |  1.07 |    0.02 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |              |             |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 256    | False |     163.0 μs |    127.53 μs |     6.99 μs |     158.9 μs |  1.00 |    0.05 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 256    | False |     183.8 μs |     38.14 μs |     2.09 μs |     183.1 μs |  1.13 |    0.04 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |              |             |              |       |         |           |             |
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **256**    | **True**  |   **1,849.9 μs** |     **34.50 μs** |    **22.82 μs** |   **1,838.3 μs** |  **1.00** |    **0.02** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 256    | True  |     369.6 μs |     32.49 μs |    21.49 μs |     361.0 μs |  0.20 |    0.01 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |              |             |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 256    | True  |   1,888.0 μs |    679.14 μs |    37.23 μs |   1,876.0 μs |  1.00 |    0.02 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 256    | True  |     354.5 μs |     53.58 μs |     2.94 μs |     355.8 μs |  0.19 |    0.00 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |              |             |              |       |         |           |             |
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **1024**   | **False** |     **319.1 μs** |     **14.47 μs** |     **9.57 μs** |     **318.1 μs** |  **1.00** |    **0.04** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 1024   | False |     507.3 μs |     10.16 μs |     5.32 μs |     506.0 μs |  1.59 |    0.05 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |              |             |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 1024   | False |     321.4 μs |     92.28 μs |     5.06 μs |     318.6 μs |  1.00 |    0.02 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 1024   | False |     505.7 μs |     63.36 μs |     3.47 μs |     506.2 μs |  1.57 |    0.02 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |              |             |              |       |         |           |             |
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **1024**   | **True**  |  **28,025.6 μs** |    **386.18 μs** |   **255.43 μs** |  **27,972.4 μs** |  **1.00** |    **0.01** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 1024   | True  |   3,047.4 μs |     39.53 μs |    26.15 μs |   3,040.8 μs |  0.11 |    0.00 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |              |             |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 1024   | True  |  30,148.3 μs | 25,821.00 μs | 1,415.34 μs |  29,669.9 μs |  1.00 |    0.06 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 1024   | True  |   3,757.0 μs | 15,410.54 μs |   844.70 μs |   3,490.3 μs |  0.12 |    0.02 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |              |             |              |       |         |           |             |
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **2048**   | **False** |   **1,496.9 μs** |     **24.21 μs** |    **14.41 μs** |   **1,496.8 μs** |  **1.00** |    **0.01** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 2048   | False |     576.9 μs |      1.50 μs |     0.89 μs |     577.1 μs |  0.39 |    0.00 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |              |             |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 2048   | False |   1,562.6 μs |    364.54 μs |    19.98 μs |   1,558.3 μs |  1.00 |    0.02 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 2048   | False |     588.5 μs |    145.22 μs |     7.96 μs |     588.7 μs |  0.38 |    0.01 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |              |             |              |       |         |           |             |
| **NaiveScatter**  | **Jit**      | **10**             | **250ms**         | **Default**     | **8**           | **256**        | **2048**   | **True**  | **119,783.1 μs** |    **719.11 μs** |   **427.93 μs** | **119,767.1 μs** |  **1.00** |    **0.00** |         **-** |          **NA** |
| FieldPipeline | Jit      | 10             | 250ms         | Default     | 8           | 256        | 2048   | True  |   4,364.6 μs |    247.77 μs |   163.89 μs |   4,293.1 μs |  0.04 |    0.00 |         - |          NA |
|               |          |                |               |             |             |            |        |       |              |              |             |              |       |         |           |             |
| NaiveScatter  | ShortRun | 3              | Default       | 1           | 3           | 256        | 2048   | True  | 118,969.3 μs |  8,098.63 μs |   443.91 μs | 118,781.0 μs |  1.00 |    0.00 |         - |          NA |
| FieldPipeline | ShortRun | 3              | Default       | 1           | 3           | 256        | 2048   | True  |   4,228.7 μs |  1,879.32 μs |   103.01 μs |   4,172.9 μs |  0.04 |    0.00 |         - |          NA |
