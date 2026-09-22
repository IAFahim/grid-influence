# Bench results

Workload: 4,000 sources, 256×256 grid over 1024 world units, ~16×16-cell footprints
(sparse `Stamp.Box(16,16,60)` vs marks `Stamp.Box(60)` + bound 32 ≈ 17×17 coverage),
100k random cell queries. Release build, min over reps.

Deposit-engine numbers are after the profile-driven perf pass on this branch (branchless Q16
rounding, pre-clipped band arrays, vectorized raster taps, Fibonacci-hash PageMap, ScaleQ8
precompute, delta SetGain deposit).

| per frame                | marks (clean-api) | deposit engine (this branch) |
| ------------------------ | ----------------: | ---------------------------: |
| unchanged `Process`      |           41.6 µs |                       0.0 µs |
| move all + process       |          124.5 µs |                      888 µs  |
| move 200 + process       |           51.1 µs |                      212 µs  |
| clear + place + process  |          199.0 µs |                      573 µs  |
| single-source change     |         ~62 µs*   |                        6 µs  |
| cell query               |      1 040 ns     |                      2.9 ns  |

\* marks re-emits every source every `Process`; the deposit engine resolves only dirty tiles.

End-to-end on the described loop (move all + 100k queries): deposit ≈ **1.2 ms** vs marks ≈
**104 ms**. Marks wins when queries/frame ≲ ~700 at full churn — writes are ~7× cheaper;
queries are ~360× slower because each cell scans every mark in its tile.

Memory: marks ≈ 16 B/source + ~128 KB per (grid,layer) of mark/tile buffers; deposit engine =
12.5 KB per live tile (difference array + dense buffer + resolved page), allocated only where
sources touch.
