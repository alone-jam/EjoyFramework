# Tick input protocol v1 — offline framing slice

## Architecture and boundary

The chosen M4 direction is **server-authoritative state synchronization, local prediction/replay, and remote interpolation**. Deterministic lockstep is not the default. The existing ECS work provides authoritative baselines, exact prediction histories, explicit value-only replay, and remote interpolation; this slice supplies only bounded offline command framing.

FPSSample's official `Assets/Scripts/Networking/BitInputStream.cs` / `BitOutputStream.cs` were consulted for LSB-first fields and zero alignment padding. This implementation deliberately does not adopt their unchecked staging reads, assertion-only bounds or dependency on Unity debug helpers. No FPSSample source was copied. Existing `NetWriter`/`NetReader` use little-endian bytes; the new bit streams are separate fixed-capacity span APIs rather than changing their growable-buffer contracts.

There are no changes to legacy sessions, `INetcodeClient`/`INetcodeServer`, message IDs, `float[]` inputs, transport selection, or asmdef references. In particular this is not UDP, reliable delivery, snapshot compression, schema generation, admission, or authentication.

## Bit stream contract

`NetBitWriter` and `NetBitReader` are ref structs borrowing caller-owned spans. Fields are LSB-first within each byte and least-significant field bits are emitted/read first. Constructors start at bit zero; the explicit `long bitLimit` must be between zero and the span's byte length times eight. The overload without a limit permits the full span. `BitPosition`, `BitLimit` and `RemainingBits` use wide arithmetic; byte indexes are bounded by the supplied span.

- `TryWriteBits(uint value, int bitCount)` / `TryReadBits(int bitCount, out uint value)` support widths 0 through 32, including unaligned 32-bit fields.
- Invalid widths or values that do not fit throw `ArgumentOutOfRangeException`. Width zero accepts only value zero when writing and reads zero without advancing.
- Insufficient bits return false without advancing; reads output zero, writes preserve the **entire buffer**. Invalid write arguments also preserve it.
- A successful write clears high unused bits in its last written byte, even over prefilled memory. It does not clear bytes after `WrittenSpan`. Construction and zero-width writes do not clear storage.
- `WrittenByteCount` and borrowed `WrittenSpan` expose `ceil(BitPosition / 8)` bytes without allocation. Readers never implicitly consume padding; pass the precise payload bit length.
- Empty/default spans and streams are valid empty streams. Copies alias memory but maintain independent cursors. The owner must prevent concurrent writes/reuse; these are not shared-thread synchronization objects.

Example command layout used only by tests: 3 button bits followed by a 10-bit acceleration biased by 512. Buttons 5 and acceleration 211 produce `9D 16`, exactly 13 bits. This example is not a registered production command schema.

## Fixed wire header

All multibyte fields are unsigned **little-endian**. Exactly one complete frame is accepted.

```text
Offset  Size  Field
 0       4    Magic = 45 4A 54 49 (EJTI, uint 0x49544A45)
 4       2    Protocol version = 1
 6       2    Header size = 32
 8       8    Session epoch, nonzero
16       4    World epoch, nonzero
20       4    Command simulation tick, nonzero
24       2    COMMAND schema ID, nonzero (not an ECS component schema)
26       2    Command codec revision, nonzero
28       4    Payload bit length
32       N    Payload, N = ceil(payloadBitLength / 8)
```

High unused bits of the last payload byte must be zero. Empty payloads are allowed. No extra/trailing bytes are accepted, and no client-controlled `DeltaTime` exists. `uint.MaxValue` is a representable command tick; the owning timeline must reset before wrap. Framing alone does not decide which ticks may be admitted.

`DefaultMaxPayloadBits` is 8,192 (1,024 bytes). Per-call limits must be in `[0, MaximumPayloadBits]`, with an absolute ceiling of 1,048,576 bits (131,072 bytes). The limit is a format/work bound, not an MTU or rate guarantee. Encode and decode enforce the same limits, with wide ceiling/total-length arithmetic and no allocation based on a hostile length.

## APIs and validation

- `TryEncode(in TickInputFrameHeader, ReadOnlySpan<byte> payload, Span<byte> destination, out int bytesWritten, out TickInputFrameError, int maxPayloadBits = 8192)` validates all metadata, exact payload length, canonical padding and capacity before any destination write. Failure returns zero bytes and leaves destination unchanged. Success changes only the frame prefix.
- `TryDecode(ReadOnlySpan<byte> source, in TickInputFrameContext expected, out TickInputFrameHeader, out ReadOnlySpan<byte> payload, out TickInputFrameError, int maxPayloadBits = 8192)` validates magic, version, fixed header size, nonzero identities, exact expected context, payload size, padding and trailing bytes before publishing decoded data. Failure returns default header and empty payload.
- Success is `true` / error `None`; rejection is `false` with an explicit error. Invalid frame metadata or configured limits use errors, not fallback/default interpretation. Unsupported versions never automatically downgrade.
- Expected session epoch, world epoch, command schema and codec revision must all be nonzero and match exactly. Zero is never a wildcard. Header/context constructors merely hold copied values; the codec is the validation boundary.

Payload source/destination overlap is supported by encoding: payload `Span.CopyTo` memmove occurs **before** header writes, after every validation. Output arguments themselves must occupy separate storage from source/destination; aliasing output variables into wire memory is not supported.

Decoded payload is a **borrowed view**, not owned storage. Consume it during the owning receive callback or before the caller reuses/releases its buffer; do not retain it across an await or pool return. Do not mutate source concurrently with parsing or payload use. Headers are value copies.

## Security and deferred admission

Epoch equality is **not authentication**. Future session/transport admission must establish an authenticated peer/session, input ownership, valid command-tick windows and wrap/reset policy, rate/size budgets, and command-field ranges before applying inputs. Matching command schema/revision does not itself validate the payload's field layout or semantics. The frame codec does not execute commands or touch simulation state.

## Validation status

New NUnit tests were authored first for golden bytes, endian layout, widths/offsets, failure atomicity, canonical padding, hostile lengths, strict context, overlap, explicit command roundtrips and warmed allocation behavior. Allocation tests use Unity's GC constraint conditionally or CLR thread allocation counters, with a positive control.

`Tools~/Netcode/EjoyFramework.GamePlay.Netcode.Codec.Tests.csproj` source-links only these six runtime codec files and three offline test files. It does not include legacy session/transport tests. **No restore, build or test run was performed** for this slice. Coverage and repeated-run stability are unmeasured; future CoreCLR success is not Unity/Mono/IL2CPP validation.
