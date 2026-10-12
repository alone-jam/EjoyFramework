# Typed prediction correction example

`PredictionCorrectionScenario` is a closed, one-entity example in `EjoyGame.Samples.EcsDemo`, not a reusable rollback coordinator. It owns its World, loop, input log, two authoritative stores and two prediction histories. It exposes value copies, never mutable World/loop/registration handles.

```csharp
using var sample = new PredictionCorrectionScenario(); // Epoch 1, tick 1, zero initial state.
sample.Step(1); // Tick 2: position 1, velocity 1.
sample.Step(1); // Tick 3: position 3, velocity 2.

// One trusted producer supplies a complete pair. Transport/session authentication is external.
sample.PublishAuthority(sample.Epoch, 1, new CorrectionPosition(1), new CorrectionVelocity(10));
var result = sample.TryCorrect(1);
// Corrected: both tick-1 anchors become (1, 10), then replay produces (12, 11), (24, 12).
// Position matched within tolerance, but its authoritative anchor was still rewritten.
```

## Fixed scope and semantics

- Exactly one entity, with `CorrectionPosition` and `CorrectionVelocity` containing all mutable simulation state. A stateless system uses fixed 8 Hz ticks: `velocity += acceleration; position += velocity` in checked integer arithmetic.
- Position tolerance is one fixed unit; velocity comparison is exact. These are sample policies, not framework defaults or a cross-platform determinism guarantee.
- Acceleration is limited to `[-1024, 1024]`. History/input capacities are independently limited to `[1, 1024]`. The initial tick and epoch must be nonzero. Ticks never wrap.
- Inputs are copied into one preallocated ring with explicit tick and epoch tags. Replay reads only the recorded input for that exact tick; overwritten, missing or wrong-epoch entries never fall back to zero/current input.
- `PublishAuthority` accepts one copied pair from one explicit producer, for this owner's epoch and an increasing tick. Its pair marker is published only after both stores accept; equal numeric ticks alone do not establish a common remote origin. Future authority may be staged but cannot be corrected before the real clock reaches it.

## Correction ordering

`TryCorrect(baselineTick, maxReplayTicks = 128)` targets the current real tick. Its synchronous, read-only preflight checks the closed entity/membership scope and exact capture identity, published authority pair/epoch/ticks/values, loop range/budget/fault/pending work, both prediction verifications, and every required input. Invalid ranges are checked before scanning a potentially large interval. A first mismatch never skips validation of the other prediction.

- `Matched`: **all prechecks still run**, but no restoration, truncation, replay or capture occurs.
- `ResyncRequiredNoChange(reason)`: a prerequisite is missing/invalid; World, clock, histories, inputs and published authority remain unchanged.
- `Corrected`: one two-target `TryRestoreExact`, truncate both histories, record both authoritative anchors (including any matched component), then explicit replay and dual capture. An empty replay range still restores and replaces the current-tick anchors.

All storage/replay outcomes are checked. After restoration begins, an error is **not** a no-change result: the original exception propagates and `RequiresRecovery` blocks further Step/PublishAuthority/TryCorrect calls. This includes anchor rebuilding failures before the loop itself faults. Previously changed values or captured rows remain. Forward execution and partial authority-publication failures also require recovery. There is no automatic ResetClock, retry or rollback; dispose/recreate, with coherent resynchronization policy supplied by the application.

## Deliberate limits

The pair restore is not a replay filter. This example has no participant injection, extra entities, system counters, random stream, mutable configuration or external simulation state. Adding any of those would invalidate its two-component completeness claim.

Observable scope checks reject extra/replaced/despawning entities, changed memberships, a wrong loop World, changed owned-clock configuration, or replaced capture. Public runtime APIs do not enumerate arbitrary system registrations or extra component types. The closed sample API prevents those changes; it is **not** a defense against reflection, unsafe access, external threads, or native-memory corruption. Tests deliberately bypass private boundaries to model failures; such mutations are unsupported usage, not an extension API. No production reflection or fault-injection hook is present.

The capture sink writes both histories from local World value copies. A failure during the second write may leave the first row captured; the owner recovery gate is the boundary, not a cross-store transaction. IO purity, authenticated sessions and authoritative completeness remain application responsibilities.

## Validation status

Standalone regression tests are included by the ECS test project and cover no-change snapshots, missing/mismatched pairs and inputs, closed scope, paired anchor replacement, empty/max-tick ranges, recovery failures and warmed allocation detection. Tests were authored before implementation but **not built or executed** for this slice. Coverage, runtime allocations and repeated-run stability remain unverified.
