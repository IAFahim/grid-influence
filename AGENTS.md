# Repository rules

GridInfluence is a standalone .NET library for chunked sparse integer influence fields. It has no
dependency on any timeline package and must not gain one.

## Code rules

- Production code contains no explanatory comments. Names, types, file boundaries, and tests carry
  the design.
- The warm data path is unmanaged: no managed arrays, LINQ, delegates, boxing, reflection, locks,
  or exceptions on tick or query paths. Borrowed caller-owned columns are the only exception.
  Unsafe code requires a stated lifetime, aliasing, alignment, and concurrency proof in
  `docs/model.md`.
- Generated or written files are deterministic and culture-independent.
- Correctness receipts precede timing. Benchmarks consume success, playback, and output state;
  warm scalar playback must allocate 0 B.

## Validation

```sh
dotnet build GridInfluence.slnx -c Release -m:1
dotnet test GridInfluence.slnx -c Release --no-build
dotnet run --project samples/scene -c Release --no-build -- --scene samples/scene/assets/scene.json --out /tmp/frames
dotnet run --project benchmarks -c Release --no-build -- --verify
```

Package changes also require `dotnet pack` of both packages plus a version review. Never move a
published tag.
