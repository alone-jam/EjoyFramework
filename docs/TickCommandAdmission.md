# Tick command admission — bounded offline inbox

`TickCommandInbox<TCommand,TCodec>` separates receipt of a validated command from server simulation. It owns one fixed array of tagged command values; it owns **no clock, World, transport, simulation callback or DeltaTime**. It does not modify legacy sessions.

## Trusted setup

The owner constructs an inbox on its owning thread using a copied, immutable `TickInputFrameContext` established **after authentication, schema agreement and input ownership validation**. All calls, including late callbacks on closed objects, must run on that constructing thread; marshaling transport callbacks is the caller's responsibility. Never construct expected context from the incoming frame. Both context and codec identity must be nonzero and match exactly. Epoch matching is not authentication.

- `TCommand : unmanaged` stores copied values, not wire memory.
- `TCodec : struct, ITickCommandCodec<TCommand>` supplies `CommandSchemaId`, `CodecRevision`, and `TryDecode(ref NetBitReader, out TCommand)`.
- The codec declares an explicit bit layout and checks ranges, finite values and allowed flags. Raw struct layout, pointer deserialization and commands borrowing packet memory are not supported.
- Codec configuration/identity must remain stable. Each attempt uses a value copy, but referenced configuration is not deep-copied. Codecs must be deterministic and have no external side effects; the inbox is not a sandbox.
- Initial `LastSimulatedTick` may be zero for genesis. `LastProcessedCommandTick` starts at zero: constructing at a later baseline does not claim earlier commands were consumed.
- Window `W` is fixed at construction, in `[1, 1024]`; there is no growth. Payload limits use the unchanged frame codec's bounds. Slot memory also depends on `TCommand` size.

## Admission

Let `S = LastSimulatedTick`. Only `S < commandTick <= S + W` is eligible, using widened arithmetic. No wrap is inferred near `uint.MaxValue`.

`TryAdmit(frame)` first enforces owner/state, then uses the real frame codec and the stored expected context. It checks the tick window and explicit slot tag. Out-of-order future commands are accepted. A different occupied tag is never evicted.

The first **valid** command at a tick wins. After frame/context/window validation, an already admitted tick returns `Duplicate` without decoding its replacement payload. Duplicate means **already admitted**, not byte-identical, equivalent, or newly validated command data.

For an empty slot, decoding happens on a local reader/codec/command. Publication requires codec success, `BitPosition == PayloadBitLength`, and no remaining bits. Missing/trailing bits or invalid ranges leave the slot free. Schema decoders must advance the supplied reader, not replace it with another stream. Successful admission copies the command; receive buffers may then be reused.

The inbox's own admission/rejection/storage paths return `TickCommandAdmissionResult` without per-packet logging or allocations; codec implementations remain responsible for their own allocations and work. A default result has status `NotEvaluated`, never `Accepted`. `FrameRejected` preserves the frame error; all other statuses carry `FrameError.None`. A throwing codec propagates the original exception, publishes nothing, does not advance counters, and does not set the simulation-fault flag. A later valid packet may retry admission. Decode reentry is rejected and lifecycle changes during decoding throw; external codec side effects still cannot be rolled back.

## Explicit server lifecycle

```text
Ready
  TryAdmit -> queue only; no simulation/time/processed-tick advancement
  BeginTick(S+1) -> InFlight, sealed TickCommandSelection copy

InFlight
  TryAdmit -> TickInFlight rejection
  successful simulation + CompleteTick(exact tick) -> Ready, S advances once
  simulation failure + FailTick(exact tick)        -> Faulted

Faulted
  admission rejected; no begin/complete/retry/abort back into readiness

Ready / InFlight / Faulted
  Close -> Closed; values cleared, no rollback or counter advancement
  late admission callbacks on the old object -> Closed
```

`BeginTick` may select `HasCommand=false`; `Command` is then default, but the inbox chooses **no** fallback. A genuinely admitted default-valued command has `HasCommand=true`. The owner decides whether missing input means neutral input, repetition, or another policy. Waiting for late input must happen **before** BeginTick; a sealed missing selection cannot be refilled.

Call `CompleteTick` only after successful simulation. It clears the selected slot and advances `LastSimulatedTick` to the exact selected tick. `LastProcessedCommandTick` advances only if an admitted command was consumed. It is **not a packet acknowledgment**, and a highest processed tick does not prove input existed at every earlier tick.

`FailTick` does not undo simulation writes or claim command consumption. It ends the flight in a terminal execution-fault state; queued values remain retained until Close. Close can also abandon an in-flight selection without claiming rollback. No automatic clock reset, retry or simulation recovery occurs.

Completing tick `uint.MaxValue` is allowed; the next Begin throws before wrap. All wrong lifecycle calls are programmer exceptions. Owner-thread diagnostics remain readable after fault/close, and Close is idempotent on that thread.

## Replacement and limits

On reconnect, world reset or ownership change, close the old inbox and construct a new one with a **new session/world epoch tuple and a coherent simulation baseline**. The owner must ensure tuple freshness; there is no global epoch registry. Old-object callbacks cannot reach the replacement. Reusing the old tuple defeats stale-context rejection.

Bounded memory does **not** rate-limit CPU or ingress bytes. Repeated invalid/duplicate frames still cost validation work, and arbitrary codec complexity is outside the inbox's control. The owner must budget ingress, decode work and scheduling. This slice performs no real networking or authentication and introduces no live-session integration.

## Tests and integration status

New tests use the real bit/frame codecs and an explicit 43-bit test command layout (biased axis, allowed button bits, finite normalized IEEE-754 gain). They cover sparse ordering/windows, max ticks, invalid-first/duplicate behavior, exact consumption, copied values, missing-input acknowledgment distinctions, fault/close/recreate, threads, codec exceptions/reentry and warmed allocation paths using the existing positive-control detector.

The separate flow fixture owns test-only fixed-step state. A burst of admitted frames changes neither simulation count nor elapsed time; each explicit successful tick advances once. Its missing-input policy is intentionally selected by the test host, not the inbox. Failed simulation demonstrates partial host values are not rolled back.

Tests were authored first but **not compiled or executed**. Existing codecs, protocol documentation, ignore rules and source-linked project remain unchanged; the parent must add these four runtime files and two tests to the project after review. Coverage, allocations, repeated-run stability and Unity/IL2CPP behavior remain unverified.
