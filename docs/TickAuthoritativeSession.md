# Tick-authoritative server profile

`TickAuthoritativeServer<TCommand,TCodec>` is an explicitly constructed alternative to the legacy `INetcodeServer` path. It owns one budgeted transport pump, peer inboxes, a private `TickClock` and `EcsLoop`; it borrows the World, game systems and optional capture sink.

## Selected path

`RunFrame(serverDelta)` performs exactly one bounded transport poll, then exactly one ECS frame. Receive callbacks only admit command copies. Client packets do not supply simulation duration and never advance the clock.

Before each tick, every authorized participant is checked against the common preceding completed tick, then all selections are sealed. Game systems execute once per World tick, not per peer. `ActiveCommands` is a borrowed tick-scoped span; missing-input policy belongs to those systems.

The completion sink runs after despawn processing. Application capture must finish first; all peer flights are then revalidated and completed without intervening callbacks. Only afterward are `LastCompletedTick` and processed-command diagnostics published. Application capture must stage data rather than advertise uncommitted acknowledgments.

## Authorization and peer identity

- `ConnectionObserved` is not authentication. The owner validates the remote identity, schema and controlled-entity assignment before calling `AuthorizePeer` with a trusted expected context.
- Context is never derived from the incoming packet. Fresh epoch tuples and a coherent baseline are required after reconnect, reset or reassignment; this adapter does not authenticate or globally police epoch freshness.
- Handles contain server-instance identity, slot generation and transport connection ID. Delayed grants/revokes using a reused slot or another server's handle are rejected.
- The transport must not reuse a connection ID while older events for it remain deliverable. Local generations cannot distinguish an ambiguous stale transport callback.
- During a tick, revocation only marks a pending removal. Inboxes are closed after the whole tick commits; the entire removal batch is closed before transport kicks or `PeerClosed` notifications. Notification-triggered further revocations are bounded by peer capacity. Disposal is rejected throughout removal processing, including reentry from a transport's `Kick` implementation.
- Closed-peer diagnostics survive until slot reuse. `LastProcessedCommandTick` advances only for a successfully completed tick with actual input; it is neither packet acknowledgment nor proof that every preceding tick had a command.

## Limits and ownership

Options are immutable. Peer/window counts, poll events/data, and per-peer decode work are bounded. Byte budgets must fit one maximum configured legal frame. Unauthorized/unknown data consumes global poll resources but is dropped before codec execution or inbox allocation. Quotas reset once per server frame, not per packet.

`IBudgetedNetTransport` is mandatory; there is no unbounded TCP fallback. The current bounded Loopback can intentionally discard individually oversized packets, including reliable ones. It bounds consumed events and delivered bytes, not queued memory, arbitrary callback CPU time or all allocations. Loopback still copies payloads and invocation lists; only the warmed nonallocating-fake session path has allocation assertions.

Use the World and transport exclusively. Do not simultaneously drive them with a legacy server, `NetcodeRunner`, `EcsWorldComponent`, another loop or an external poll. Systems are registered before Start. This is not an IO sandbox or a general rollback coordinator.

## Failure semantics

Unexpected transport, codec or owner callback exceptions are caught inside session handlers and latched because transports can swallow subscriber exceptions. They are rethrown at the owned boundary before further useful execution.

Begin/simulation/despawn/capture/completion failure faults the entire session. Active flights fail, inboxes close, subscriptions detach and shutdown is attempted. Original exceptions and already-applied World values remain; the real clock can be ahead of `LastCompletedTick`. Earlier fully committed ticks are not rolled back. Recreate from a coherent baseline and fresh epochs—no automatic retry or clock reset is provided.

Cleanup continues across failures. `CleanupFailure` records the first cleanup error, and a throwing transport cannot be claimed fully shut down. Fault shutdown does not call application peer notifications. Normal removal notifications can themselves fault the session after their completed tick has already committed.

## Example and tests

[TickAuthorityScenario](../Samples~/EcsDemo/TickAuthorityScenario.md) creates two real in-memory Loopback peers, explicitly performs local fixture authorization, sends reordered commands, and demonstrates an explicitly neutral missing-input policy. Its 8 Hz sample configuration does not alter the framework's 30 Hz default.

The standalone session suite source-links the production implementation, Loopback, and real Core/ECS dependencies. No fake ECS or logging runtime is substituted; the test-only transport is used solely for deterministic fault/budget/allocation scenarios.

**Current status:** implementation and tests are written. The C# review found a sample interface mismatch and disposal reentry during idle revocation; both were fixed and statically reverified. The separate security review found no confirmed vulnerabilities within the documented ownership/codec/authorization contracts. No build or tests have run. This profile is not accepted for production use yet; scoped static approvals are not end-to-end runtime validation.
