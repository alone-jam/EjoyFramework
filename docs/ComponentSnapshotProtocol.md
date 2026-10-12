# Component snapshot protocol (EJCS v1)

## Scope

An **offline homogeneous full-component batch**, not a transport or a session implementation.
Each included entity has one standalone value of the selected component schema/revision. There
is no baseline dependency, delta mask, compression, entity spawning, despawning, World mutation,
prediction, acknowledgement advancement or cross-component transaction in this codec.

“Full” means **each included component value is complete**. It does not mean a complete World,
complete recipient interest set, or complete set of components on an entity. Omission (including
an empty batch) never means despawn or component removal.

The header's epochs and processed-command tick are untrusted claims. Successful decoding is
**not authentication, authorization, commit proof, replay protection or a full-World transaction**.
Admission/lifecycle rules and the later atomic application of validated values belong to an owner.

## Wire format

All fixed integers are **unsigned little-endian**. There is no native struct serialization.
Exactly one frame is consumed; trailing bytes are invalid. No version downgrade or optional
header extension exists in v1.

| Offset | Bytes | Field | Rule |
|---:|---:|---|---|
| 0 | 4 | magic | `0x53434A45`, bytes `45 4A 43 53` (`EJCS`), distinct from `EJTI` |
| 4 | 2 | version | `1` |
| 6 | 2 | header size | `48` |
| 8 | 8 | sessionEpoch | nonzero |
| 16 | 4 | worldEpoch | nonzero |
| 20 | 8 | snapshotId | nonzero, per-recipient snapshot identity |
| 28 | 4 | serverTick | nonzero |
| 32 | 4 | lastProcessedCommandTick | zero allowed, must be `<= serverTick` |
| 36 | 2 | componentSchemaId | nonzero |
| 38 | 2 | codecRevision | nonzero |
| 40 | 4 | stateBitCount | fixed for the selected schema/revision; zero-bit tags allowed |
| 44 | 4 | entityCount | zero allowed |

Records start at byte 48. Each record is independently byte-aligned:

| Relative offset | Bytes | Field |
|---:|---:|---|
| 0 | 4 | networkId, nonzero |
| 4 | 4 | spawnGeneration, nonzero |
| 8 | `ceil(stateBitCount / 8)` | explicit full-state payload |

Records are **strictly ascending by unsigned networkId**. Repeated network IDs are rejected,
including repeated IDs with different generations. Generation is not a secondary sorting key.
A network key is not a local ECS handle.

State fields use the existing LSB-first `NetBitWriter`/`NetBitReader`. A codec must consume exactly
`stateBitCount` bits, with zero remaining bits, for **every** record. Each partial last byte has
zero high unused bits. This rule applies to every state payload, not just the final record.
Zero-bit states have no payload bytes; their records still contain the eight-byte entity key.

`frameBytes = 48 + entityCount * (8 + ((stateBitCount + 7) / 8))`.
The implementation validates both axes, uses `ulong` for the ceiling/product/sum, and checks the
frame-byte cap before narrowing to `int`, slicing record storage, or entering any record loop.
There are no padding bytes between records or after the frame.

The independent golden fixture in `ComponentSnapshotTestData` encodes two 13-bit states. Its
first state uses allowed flags `5` in three bits and signed position `211` biased by `512` in ten
bits, producing `9D 16`. This fixture is an example layout, not a production schema registration.

## Limits

`ComponentSnapshotLimits` is a readonly value with a validating constructor. Axes are independent
admission budgets, not buffer allocations and not a guarantee that all three maxima fit together.

| Budget | Default | Allowed configuration |
|---|---:|---:|
| MaxRecords | 256 | 0 .. 16,384 |
| MaxStateBits | 2,048 (256 bytes) | 0 .. 65,536 (8 KiB) |
| MaxFrameBytes | 131,072 (128 KiB) | 48 .. 8,388,608 (8 MiB) |

These defaults accommodate small batches while bounding parser work and payload width. The
hard caps remain conservative offline limits, **not** UDP/transport packet-size recommendations.
A legal record-count/bit-width combination may still exceed the byte cap and will be rejected.
No allocation follows a count read from the frame. Scratch and destination are caller-owned.

Invalid constructor arguments throw `ArgumentOutOfRangeException`. `default(ComponentSnapshotLimits)`
is invalid, not a shorthand for defaults. `default(ComponentSnapshotFrameCodec<,>)` consequently
fails closed with `InvalidLimits`. Use the one-argument codec constructor for documented defaults.

## API and codec contract

Namespace: `EjoyFramework.GamePlay.Netcode.Sync`.

```csharp
public interface IComponentStateCodec<TState> where TState : unmanaged
{
    ushort ComponentSchemaId { get; }
    ushort CodecRevision { get; }
    uint StateBitCount { get; }
    bool TryWrite(ref NetBitWriter writer, in TState state);
    bool TryRead(ref NetBitReader reader, out TState state);
}

public readonly struct ComponentSnapshotFrameCodec<TState, TCodec>
    where TState : unmanaged
    where TCodec : struct, IComponentStateCodec<TState>
{
    public ComponentSnapshotFrameCodec(TCodec codec);
    public ComponentSnapshotFrameCodec(TCodec codec, ComponentSnapshotLimits limits);

    public bool TryEncode(in ComponentSnapshotHeader header,
        ReadOnlySpan<ComponentStateRecord<TState>> records, Span<byte> destination,
        out int bytesWritten, out ComponentSnapshotError error);

    public bool TryDecode(ReadOnlySpan<byte> frame, in ComponentSnapshotContext expected,
        Span<ComponentStateRecord<TState>> scratch, out ComponentSnapshotHeader header,
        out int recordCount, out ComponentSnapshotError error);
}
```

`NetworkEntityKey` stores `NetworkId` and `SpawnGeneration`. `ComponentStateRecord<TState>` stores
an `Entity` key and a copied `State`. Header/context/record/key constructors only copy values;
frame methods validate before publishing. Header `EntityCount` must exactly equal the encode
input length. Context specifies exact session/world/schema/revision/bit-width expectations; zero
never means wildcard. Context width zero selects a tag, not any width. The selected `TCodec` must
independently match header schema, revision and bit width even if wire and context agree.

Codec definitions must be deterministic and fixed per schema/revision. Struct calls are constrained
(no interface boxing); each operation uses a copy of the configured codec. Codecs must not depend
on mutable static/shared state, operation history, clocks or previous records. They must not change
the supplied reader/writer's storage or bit limit, retain buffers or allocate per record.

Serialize explicitly chosen fields; validate field ranges, finite floating-point values and allowed
flag bits on both encode and decode. Read must construct an **owned value**. `unmanaged` is not a
semantic validation mechanism: pointer-bearing or borrowed-address state remains prohibited. No
raw-memory serialization, reflection, pointers or automatic traversal of struct layout is allowed.
Malformed values return false; unexpected exceptions are programmer errors, not malformed-frame
results, and propagate. The frame layer does not catch and obscure those exceptions.

There is no second Core schema catalog here and no `Core.Ecs` dependency. Later application code
must cross-check the chosen `TState` and codec against its selected registered component schema.
This generic offline slice adds no module/helper/procedure registration and no codegen output.

## Ownership, nonaliasing and failure behavior

**Strict nonaliasing is a caller precondition, not a runtime overlap detector.** Typed input records,
wire storage, decode scratch and out-parameter storage must be disjoint. In particular, do not use
memory reinterpretation to alias records or `out header`/`out recordCount`/`out error`/`out bytesWritten`
with the wire or scratch. Input values and source bytes remain stable for the entire synchronous
call; each writable workspace is exclusively owned. Invalid aliasing/concurrent mutation is caller
misuse with no guarantees. Independent calls may share a stateless codec but must use disjoint
writable storage. No unsafe/raw-state-memory overlap inspection is performed.

- **Encode success:** only `destination[0..bytesWritten]` is the frame; extra capacity is untouched.
- **Encode false:** `bytesWritten == 0`. Structural preflight failures preserve the destination.
  Typed-codec false, exact-bit-count failure or exception may leave a partial header/records in the
  workspace. The destination is **not atomic** and must not be sent unless the call returns true.
- **Decode success:** all validation is complete, header is published, and only
  `scratch[0..recordCount]` contains the result. Extra scratch capacity is untouched.
- **Decode false:** header is default and count is zero, even when a final record failed after
  earlier values were decoded. Scratch may contain partial work and must remain unpublished.
  The wire source is not modified. Never pass authoritative storage as scratch.
- **Exceptions from codec definition or read/write:** success outputs remain reset just as above.
  Workspace may be partial; the `error` value is not a result on an exceptional exit.
- Decoded values do not borrow receive storage: the wire can be immediately reused after return.
  Copy successful records into the owner's separate storage before reusing scratch. This copy is
  not an authorization decision or a World commit, both of which remain outside this slice.

All record identities/order and all receive payload padding are checked before any typed decode.
Typed validation then checks every included value. A valid first record does not admit the batch.

`ComponentSnapshotError` separates invalid configuration/context/header/codec, magic/version/header
size, exact context mismatches, codec identity mismatch, encode count mismatch, record/state/frame
caps, exact frame length, destination/scratch capacity, invalid or unsorted keys, padding, typed
state rejection and incorrect bit consumption. `None` accompanies true on normal return. When
multiple faults coexist, the first encountered validation error wins; do not rely on another
fault being reported. No error includes raw payloads, exception text or secret data.

## Session integration is deliberately deferred

`SnapshotId` is a per-recipient identity, **not ServerTick and not a transport packet ACK**. It may
exceed the tick range. Identity allocation, replay windows and ensuring retransmissions reuse the
same ID and exact bytes are owner policy; the codec holds no session or retransmission history.
Ticks must not wrap within the same timeline. Epoch rollover and generation reuse policy are also
owner responsibilities.

Any future sending integration must wait for the authoritative session's **LastCompleted** and
**committed processed-command acknowledgement**. Never publish a frame from a pre-capture phase
or advance processed acknowledgement just because this serializer returned true. The header only
checks the numeric `lastProcessedCommandTick <= serverTick` relation; it cannot verify completion
or commitment. Application admission must precede lifecycle mutation, with a separately specified
transaction/rollback policy. None of those mechanisms is added by this offline batch codec.
