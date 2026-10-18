# Repository rules

Gi is a standalone .NET library for sparse tiled integer influence fields. It has no dependency
on any timeline package and must not gain one.

## One API

The public surface is exactly `World`, `Grid`, `Layer`, `Stamp`. Do not add a second engine, a
parallel field type, or an alternate query surface — this repository carried two engines once
and the re-emitted marks engine was retired for it. Extensions go through the existing handles;
`World.FieldView` (a `ref struct` nested in `World`) and the `Grid` spatial-order helpers are the
only nested additions.

## Code rules

- Production code contains no explanatory comments. Names, types, file boundaries, and tests carry
  the design.
- The warm data path is unmanaged: no managed arrays, LINQ, delegates, boxing, reflection, locks,
  or exceptions on place, process, or query paths. Unsafe code requires a stated lifetime,
  aliasing, alignment, and concurrency proof in `docs/model.md`.
- Generated or written files are deterministic and culture-independent.
- Correctness receipts precede timing. Benchmarks consume success and output state; warm process
  and query must allocate 0 B, with and without hardware intrinsics.

## Validation

```sh
dotnet build Gi.slnx -c Release -m:1
dotnet test Gi.slnx -c Release --no-build
dotnet run --project samples/world -c Release --no-build
dotnet run --project benchmarks -c Release --no-build -- --verify
DOTNET_EnableHWIntrinsic=0 dotnet run --project benchmarks -c Release --no-build -- --verify
```

Changes that can move a timing also run `dotnet run --project benchmarks -c Release --no-build -- --gate` on one
pinned core (`taskset -c 0`), and refresh `benchmarks/gate-baseline.json` with `--gate --update` only
on the host that recorded it.

## Unity work

Applies to anything under `unity/`.

- After every edit run `unity/compile.sh`: it compiles the host project, tests included, with the editor in batch mode (about 25 s, no tests) and must print `errors: 0`. Prefer it, `unity/lint.sh` and the dotnet tests over any Unity test run.
- Prove engine-neutral logic in `unity/com.gi.entities/Runtime/Columns` with dotnet tests first.
- `unity/validate.sh` suites run only in the final verification pass or when the owner asks, never as the inner loop.
- Drive the editor through the `unity` CLI (`unity skill show` prints its guide) rather than hand-built editor command lines.

Package changes also require `dotnet pack src/Gi` plus a version review. Never move a published
tag.
