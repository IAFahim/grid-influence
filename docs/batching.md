# Deferred batches for concurrent hosts

Design and implementation tracker: [source batches #34](https://github.com/IAFahim/grid-influence/issues/34),
[tile preparation #35](https://github.com/IAFahim/grid-influence/issues/35),
[revision tokens #37](https://github.com/IAFahim/grid-influence/issues/37),
[transform-only updates #38](https://github.com/IAFahim/grid-influence/issues/38),
[borrowed transform ranges #39](https://github.com/IAFahim/grid-influence/issues/39).
`World.ApplyBatch`, source commands, creation targets, per-command results, the `World.Revision`
token, and parallel tile footprint preparation are implemented in `1.2.0-alpha.2`; checked
consumption with `World.Token`, `UpdateTransform`, and `World.SyncTransforms` land in
`1.2.0-alpha.3`. Direct mutations and batch calls remain
exclusive per world. Concurrent submissions belong in host-owned buffers, not concurrent `Place` calls.

## Processing contract

Hosts record commands outside Gi storage. A coordinator seals a batch, joins its writers and
previous Gi readers, ingests its source changes, processes the world, and publishes results.
Unrelated game jobs can continue. Recording into independent host buffers may overlap queries
against the last processed field; ingestion and processing may not overlap those queries.

Sealing has an explicit acceptance boundary. Later input belongs to the next batch. The host
retains accepted input until application or an acknowledged failure; allocator rotation is not
permission to discard a batch. Skipping processing does not advance timers or silently clear
recorded events. Calling `ProcessAll` advances every live world once, including worlds with no
new inputs. One coordinator owns each Gi world across all host systems and ECS worlds.

## API call matrix

| Existing surface | Real caller | Batch treatment and required semantics |
| --- | --- | --- |
| `World.New`, `Free`, `CopyWorldIds` | Bootstrap, streamed level lifetime, coordinator | Exclusive lifetime boundary. Join in-flight input, processing, readers, and result users before freeing/reusing a world handle. |
| `Grid.New`, `Layer.New`, `Layer.Sum`, `Min`, `Max`, `Mask` | Bootstrap or controlled graph changes | Exclusive structural phase. Freeze grid/layer records while source and tile workers use them. Preserve derived-layer dependencies. |
| Grid/layer `Count`, `CopyIds`, `TryGetSpec` | Tools, buffer sizing, coordinator | Read during a stable metadata phase; do not race world/grid/layer creation or destruction. |
| `Stamp.New`, `Box`, `Tent`, `Bell`, `Disk`, `Cone`, `Dome`, `Free` | Runtime catalog owner | Register at runtime before producer jobs use byte handles. Catalog mutation is exclusive across worlds; a deferred request also retains its stamp until consumed/cancelled. |
| `World.Place` | Spawn bursts, persistent emitters, transient effects | Existing direct API keeps its immediate integer result. Deferred recording returns a scoped ticket; creation produces a real source ID or explicit failure. Same-batch commands may target the ticket. |
| `Move`, `Turn`, `Scale`, `SetGain` | Movement, facing, size, combat/AI systems | Preserve partial-field updates and stable command order. A full-state input is a fast path only when one producer owns all those fields. `SetGain`, including setting the existing value, cancels fading. |
| `UpdateTransform` | Every-frame pose snapshots of existing emitters | Exactly ordered `Move`+`Turn`+`Scale` — position, heading, and scale with the direct calls' comparisons and quantization — while never assigning gain or cancelling a fade/expiry. Use it for movement while effects fade; keep `Update`/`SetGain` for real gain changes. |
| `World.Sync` | Many existing sources changing position together | Position-only column ingestion stays as it is: stale-handle validation and bit-exact position comparison, one coalescing pending op per source. |
| `World.SyncTransforms` | Every-frame pose columns owned by a native host | Borrows compact ID/X/Y (+ optional heading/scale) native columns per `World.TransformRange` in caller order; null heading/scale preserves; stale rows report one mask bit instead of per-row results; no host flattening, command records, or result arrays. |
| `Remove` | Despawn, owner destruction, expiry cleanup | Order against updates, cancel pending creations, and retain cleanup until acknowledged. A recycled slot must not let a stale source ID affect its new occupant. |
| `Fade`, `Expire` | Effects, gameplay scheduling | Ordered operations, not plain last-value fields. Preserve cancellation, start gain, relative tick, journal entries, and ordering against `SetGain`/`Remove`. |
| `Process`, `ProcessAll` | Explicit simulation phase | Consume only sealed, accepted work; tick once per chosen processing step. No producer may mutate the batch being consumed. |
| `Query`, `QueryAt`, `QueryRegion`, `QueryMax`, `QueryGradient` | Parallel gameplay readers | Read the last completed field; join before ingestion/processing and wait for publication before reading the new field. |
| `TrySense*`, including source exclusion | AI sensing, self-exclusion | Same read fence; source columns and pending operations are part of the read set. Exclusion uses the last applied placement, not an unprocessed desired position. |
| `SenseColumn`, `ChangedMask` | Large AI query batches | Reuse the current column APIs and their worker thresholds. Concurrent calls need disjoint output columns/masks; ingestion and processing still wait for these readers. |
| `Covers`, `Tick`, `Changed`, `ChangedTiles` | Routing, incremental consumers | Stable read phase. Publish changed lists, epochs, and derived results together after processing finishes. |
| `Clear`, `Record`, `StopRecording`, `Rewind` | Reset, replay, rollback | Ordered exclusive controls at a batch boundary. Define pending-input cancellation/retention explicitly; preserve timer and source generations. Reject obsolete queued epochs after clear/rewind. |

## Ordering and source identity

Producer registration order and producer-local sequence define logical order. Hardware thread
indexes, wall-clock arrival, and per-thread stream traversal do not. A source has one worker
owner during ingestion, even when several producers submitted commands for it. Merge only
operations whose observable semantics allow it. In particular, a full-state snapshot from a
movement producer must not replace a gain written by a different producer.

An internal complete-state record carries world positions, heading, scale, and gain. World,
layer, and stamp handles remain bytes; gain remains signed and bounded by Gi's actual range.
Source IDs remain full `int` values: the 24-bit slot and 7-bit generation cannot be reduced to
a slot index. Invalid/stale handles retain the direct API's behavior.

Deferred creation uses a batch/producer-scoped ticket and result mapping. Tickets are not
encoded as ordinary source IDs. A host owner can disappear before creation completes: retain
the request identity, cancel before ingestion when possible, or remove the resulting source
afterward. Do not lose the result just because the original entity no longer has its component.

## Data and worker stages

1. Record typed creation, mutation, removal, and scheduling inputs into producer-owned storage.
   Complete-state inputs use a direct array fast path when counts and ownership are known.
2. Seal, count, validate ordering/identity, and normalize conflicting inputs by source. Reuse
   retained scratch and prefix offsets; avoid sorting the already-unique full-state path.
3. Reserve source slots, source-column capacity, and operation ranges before exposing worker
   pointers. Validate result capacity before application. Source-slot exhaustion is a per-command
   failure; native memory growth follows Gi's existing allocation contract.
4. Apply disjoint source assignments in parallel. Preserve the earliest applied state and final
   desired state in one pending operation per slot. Reduce partition-local counters afterward;
   do not increment shared lengths/free lists from workers.
5. Commit ordered timer/journal work without concurrent heap/list mutation. Keep the existing
   timer-before-deposit processing semantics and rewind parity.
6. Calculate footprints into partition-owned ranges. Gather tile requirements by key, prepare
   sparse maps and buffers, then publish stable pointers. No pointer survives an arena resize.
7. Reuse Gi's current adaptive deposit, crowded-rebuild, and tile resolution machinery. One
   worker owns a `(grid, layer, tile)` while different tiles execute concurrently. Shared map
   removal/freeing and index publication happen after worker joins; derived layers follow inputs.
8. Publish result IDs and field availability. Reclaim input memory only after every relevant
   producer, consumer, result publisher, and reader is finished.

The core stays dependency-free. Synchronous batch methods borrow native ranges only until all
internal workers join; they do not retain caller spans/pointers. A future asynchronous API would
need a separate explicit lifetime contract. Ordinary .NET callers own explicit recording lanes;
Unity's worker-index streams are not a general managed-thread primitive.

`World.Sync`, `SenseColumn`, and `ChangedMask` already establish a native-column host boundary.
Extend that machinery instead of building an unrelated batch engine. Position-only callers
should not pay for full-state fields or event ordering they do not use.

## Implemented C# API

```csharp
World.SourceCommand[] commands =
[
    World.SourceCommand.Place(layer, x, y, stamp, 8),
    World.SourceCommand.Move(World.SourceTarget.CreatedBy(0), nextX, nextY),
    World.SourceCommand.Fade(World.SourceTarget.CreatedBy(0), 0, 20),
    World.SourceCommand.Remove(anotherSourceId)
];
var results = new World.SourceResult[commands.Length];

// After sealing input and joining its producers and the world's Gi readers:
var status = World.ApplyBatch(world, commands, results);
if (status != World.BatchStatus.Applied) return;
int createdSource = results[0].SourceId;
bool created = results[0].Status == World.SourceResultStatus.Applied;
World.Process(world);
```

Arrays illustrate the contract; unmanaged spans use the same entry point without copying.
Commands are 32-byte unmanaged values. Results contain the source ID and explicit status.
`CreatedBy` references a `Place` command index in this batch only. A failed creation returns
`-1`; subsequent targets of that creation report `InvalidSource`. Existing IDs convert implicitly
to `SourceTarget`. `Update` applies position, heading, scale, and gain in the serial API's order;
its gain assignment cancels fading even when the gain is unchanged. `UpdateTransform` applies
exactly ordered `Move`, `Turn`, then `Scale` — position compares with `==`, heading and scale
ignore non-finite values and quantize exactly like `Turn`/`Scale` — and never assigns gain or
touches fade/expiry state, so it is the movement snapshot that preserves a running fade.
An unchanged transform is a no-op that reports no changed tiles. `Turn` and `Scale` ignore
non-finite values and use the same quantization as their direct counterparts.

Malformed commands, invalid creation references, an invalid world, insufficient result space,
overlapping input/output, or more than 16,777,216 commands reject the whole batch before source
mutation or result writes. Valid structure may still contain stale sources, invalid layers/stamps,
or source-slot exhaustion: inspect every `SourceResult.Status`. Unrelated valid commands apply.
`Applied` does not mean every command succeeded. The API borrows buffers only until it returns.
It preserves pending deposits and rewind schedules, but does not call `Process` or advance time.

The application owns sealing, producer order, durable backlog, cancellation, and lifetime epochs.
No host API is added to Gi. Exclusive direct calls remain available between batch calls.

## Ingestion implementation

Small full-state batches validate and apply in one inline pass. Large unique full-state batches
avoid hashing; repeated sources fall back to ordered grouping. General batches reserve slots
against a virtual free ring, validate generation-tagged IDs, and retain one row per affected slot.
Two worker phases prepare final states and write disjoint columns/operation ranges using the
existing `ResolvePool`. The caller commits timer heaps and schedule journals after joining.
Fresh placement undo records preserve the direct `Place` layer and identity semantics, including
reused slots. Cross-layer slot reuse also fixes an existing crowded-processing defect: retraction
and application now count/skip against their respective layers rather than treating the whole
operation as belonging to its target layer ([#36](https://github.com/IAFahim/grid-influence/issues/36)).
No public mutation replay loop or new scheduler is used.

Tile footprint preparation is parallel too (#35): when a deferred batch acquires the pool,
workers claim 64-op chunks and write per-worker placements, 16 B key-carrying fragments, and
(grid, layer, tile) prep keys; the owning thread stitches them by prefix offsets and a
counting sort; a second round gives each (grid, layer) task one writer, which sorts its tiles,
allocates and grows blocks, and marks dirty in ascending order before apply workers see
anything. Fragments never hold block pointers, so no pointer survives an arena resize.
`dotnet run --project benchmarks -c Release --no-build -- --stages` reports preparation,
application, and resolution costs per `Process` against the forced-serial build.

## Checked consumption with world tokens

A host that queues work across frames cannot tell from a world id alone whether the world it
recorded against is still the world that id names. `World.Capture(world)` seals the id and the
world's revision — an `int` that changes on every observable boundary: assignment in
`World.New` (fresh or reused slot), `Clear`, `Rewind`, and `Free`. Mutations, `Process`, and
`ProcessAll` leave it alone. Gi checks the token itself at consumption:

```csharp
var token = World.Capture(world);
// Producers fill caller-owned native input; the host joins producers and Gi readers.
var status = World.ApplyBatch(token, commands, results);   // World.Sync(token, …) matches
if (status == World.BatchStatus.Applied)
    World.Process(token);
```

`ApplyBatch(token, …)`, `Sync(token, …)`, and `Process(token)` validate that the world is
allocated **and** its revision matches before reading command payloads, writing results, or
changing source state, timers, journals, pending work, or ticks. A live slot whose revision
moved returns `BatchStatus.StaleRevision`; an unallocated or freed slot returns
`BatchStatus.InvalidWorld`. Rejection is total: results keep their prior contents and the
world stays byte-identical, so a stale `Process(token)` never ticks a slot's new occupant.
`World.Capture` never throws and never fails: capturing a dead or never-allocated slot yields
a token with `IsValid == false` (revision 0 — real revisions start at 1, and a freed slot
keeps its nonzero last value, so nonzero never implies existence). Validate once per consumed
batch or range set; rows never repeat the revision.

The revision is an `int` in the token and in the runtime store — no widened copy — and
comparison is equality, so the only wrap hazard is a stale token falsely matching after exactly
2³² lifecycle transitions on one slot. This is revision validation under the existing exclusive
mutation phase: a token check is not a lock, concurrent same-world `ApplyBatch` calls remain
unsafe, and `Free`/`Clear`/`Rewind` must not race a consumer — the host still joins producer,
writer, and reader fences before lifecycle controls. `Rewind` rejects old queued epochs but
does not restore host-side entity ownership: a paired ECS rollback must restore its own
source mapping. The 7-bit source-generation wrap is a separate limit and is not solved by a
world token.

## Borrowed transform ranges

`World.SyncTransforms(token, ranges)` consumes the pose columns a native host already owns —
ECS chunks, job outputs, or any stable native storage — without flattening them into commands.
Each `World.TransformRange` (56 bytes) borrows an ID column (`int*`), X and Y columns
(`float*`), optional heading and scale columns (`float*`; null preserves the current value),
a row count, and an optional invalid mask (`ulong*` with `InvalidWords` words). A
position-only row is 12 bytes of column payload; a full pose row is 20. There are zero full
input copies — one planning scan reads the borrowed columns directly:

```csharp
var token = World.Capture(world);
// Producers finished; each descriptor borrows stable native columns.
ReadOnlySpan<World.TransformRange> ranges = stackalloc[]
{
    new World.TransformRange
    {
        Sources = sourceIds, Xs = xs, Ys = ys, Headings = headings, Scales = scales,
        Invalid = invalidRows, InvalidWords = (count + 63) >> 6, Count = count
    }
};
var status = World.SyncTransforms(token, ranges);
if (status == World.BatchStatus.Applied)
    World.Process(token);
```

Semantics: each row is exactly `UpdateTransform` — ordered `Move`+`Turn`+`Scale` that never
assigns gain or cancels a fade/expiry — with one documented difference from the ordered calls:
positions compare bit-exactly as in `World.Sync`, so `-0.0` and `+0.0` are different poses and
NaN payload bits copy verbatim, where ordered `Move`/`UpdateTransform` compare with `==`.
Heading and scale ignore non-finite values and quantize exactly like `Turn`/`Scale`. Rows
apply in range order then row order; duplicate source IDs fold last-writer-wins into one
merged pending op per source, with no host-side dedup. Invalid rows — dead, stale-generation,
or out-of-range IDs — set their mask bit (bit `row & 63` of word `row >> 6`; cleared words
leave tail bits zero, words beyond `(Count + 63) / 64` untouched) and the call still returns
`Applied`, so partial staleness stays distinguishable from whole-call rejection.

Validation happens in a fixed order before any output write or state change: the token first
(`StaleRevision`/`InvalidWorld` — before any payload byte is read), then descriptor structure
(`InvalidRange`: negative counts, missing required pointers, or `InvalidWords` below
`(Count + 63) / 64`), then overflow (`TooManyCommands` past 16,777,216 total rows), then
aliasing (`OverlappingBuffers` when a writable mask overlaps any column or another mask —
read-only columns may share storage). A rejected call writes nothing: masks keep their
contents and the world stays byte-identical.

Paths and scratch: under 8,192 total rows one inline pass on the caller enqueues the same
coalescing ops `Move`/`Turn`/`Scale` would, with zero scratch. Above that, a sequential
caller-thread scan folds rows into one entry per unique source — seeded from the current
columns, or from the slot's pending op so an unprocessed earlier edit merges rather than
chains, preserving that op's original `From*` for rewind — and the existing pooled batch-apply
phase writes source columns and ops in parallel before the workers join. Invalid bits are all
written during the caller-thread scan, so no mask word has two writers. The retained O(N)
scratch of the large path is one 136-byte batch entry plus 4 bytes of hash lookup per unique
source — no per-row command links, no result rows, no flattened copy. The columns are borrowed
only for the synchronous call and no caller pointer survives the return; the host retains the
storage through the consumer handle and a producer must never write a consumed range.

## Verification before throughput claims

`BatchSemanticsTests` establishes observable serial behavior for deferred visibility,
coalescing, generation reuse, fade cancellation, self-exclusion, idle ticking, and rewind.
Use those behaviors as an oracle for batch tests, then vary producer order, partition count,
worker completion order, shape overlap, grid scale, and storage growth. Cover rejected input,
owner destruction, pending ticket cancellation, shutdown, and batches spanning multiple frames.

Verify warm allocation bounds, the complete unsafe proof, tests, samples, and both intrinsic
modes before reporting stage timings. Report recording, normalization/reservation, source
ingestion, structural tile preparation, and tile processing separately. Reuse the existing
small-work inline path instead of paying parallel setup cost for every batch.

`SourceBatchTests` compares batches with ordered direct calls across every stamp shape, multiple
grids, derived layers, pending operations, stale/reused IDs, timer cancellation, expiry, rewind,
rejection, large parallel batches, duplicate fallback, and concurrent independent worlds.
`dotnet run --project benchmarks -c Release -- --batch` checks parity and warm allocation before
printing serial-versus-batch ingestion timing. It excludes host recording and tile processing
from the timed interval; transport and end-to-end Unity timing require the host consumer.
`dotnet run --project benchmarks -c Release -- --sync` does the same for borrowed ranges:
parity against ordered calls and `ApplyBatch`, 0 B warm, then serial/batch/borrowed ingestion
timing at 64, 4,096, 65,536, and 100,000 rows over a mixed unchanged/moving population with
sparse lifecycle events, plus the payload and scratch figures above.
