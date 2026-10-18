## 0.1.1

- Field draw: the Refresh, Sources and Bounds jobs no longer carry `[BurstCompile]` — they
  read the library's static state, which Burst cannot compile, so every field-draw job has
  been silently running on the managed fallback since 0.1.0. Heat, Bindings and Publish
  now genuinely Burst-compile. No runtime behavior change.
- Zero warnings on Unity 7000.0.0a9 and a10: static-cleanup attributes on the static-state
  types, the current `FindObjectsByType` overloads, `CurrentAssemblies.GetLoadedAssemblies`,
  and a nullable-clean GiBenchmarks build.
- `compile.sh` fails on Burst errors, not just C# errors.
- Plugin DLLs build deterministically across environments (pinned `RepositoryUrl`).

## 0.1.0

- Initial Gi Entities adapter: authoring, baking, runtime systems, netcode gates,
  presentation (inspector, mirror, field draw), samples, and the editor test suites.
