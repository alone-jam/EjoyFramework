# Tick-authority Loopback example

This example runs real in-memory Loopback transport code, not a live socket server. It owns a World with two controlled entities and an opt-in `TickAuthoritativeServer`. Local connection authorization is fixture setup, not internet authentication.

```csharp
using var sample = new TickAuthorityScenario();
sample.Send(peerIndex: 0, tick: 2, amount: 2); // Arrives out of order.
sample.Send(peerIndex: 0, tick: 1, amount: 1);
sample.Send(peerIndex: 1, tick: 2, amount: 3); // Peer 1 has no tick-1 command.
sample.RunFrame(0);                           // Poll only: no simulation.
sample.RunFrame(0.125);                       // Tick 1: totals 1 and 0.
sample.RunFrame(0.125);                       // Tick 2: totals 3 and 3.
```

The sample uses 8 Hz for simple exact intervals; production defaults remain 30 Hz. The game system runs once per World tick and uses the server-issued peer slot to select its controlled entity. Missing input contributes zero instead of repeating an action. Command values are validated in `[0,10]` by the command codec.

Only `sample.RunFrame` drives server polling and simulation. Legacy Loopback factories return `INetTransport`; the sample stores clients through that interface and explicitly validates the server's `IBudgetedNetTransport` capability. Public observations are copies; callers cannot obtain or independently drive its private World, loop, transport or inboxes. `Send` reuses the encoding buffer after Loopback has copied it.

No snapshots, client prediction, remote authentication, encryption or cross-platform determinism are demonstrated. Loopback allocation and transport behavior are not an end-to-end zero-GC guarantee.

The standalone session project includes `TickAuthorityScenarioTests`. Code and tests are written but **not compiled or executed**. See [session profile contracts](../../docs/TickAuthoritativeSession.md).
