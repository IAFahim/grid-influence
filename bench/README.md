# bench/

Scratch cross-engine comparison harnesses — not part of the solution build.

- `dep/` — the deposit engine in this worktree (`src/GridInfluence`).
- `marks/` — the marks engine on branch `clean-api`, referenced through a **sibling git worktree**
  (`..\..\..\grid-influence-clean`). That path only resolves when both worktrees sit side by side
  (e.g. `git worktree add ..\grid-influence-clean clean-api`).
- `profile/` — sustained churn loop for `perf record` (box-only or `mix` for half raster stamps):
  `dotnet run -c Release -- 8 mix`.

Each is a console app: `dotnet run -c Release` prints timings. Numbers produced on
i9-14900K / .NET 10 / Release, min over reps — see `RESULTS.md`.
