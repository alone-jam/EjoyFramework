# Fixed-tick prediction coordinator

`TickPredictionCoordinator<TCommand,TState>` is a reusable engine-independent profile for one locally controlled entity whose complete mutable prediction state is one unmanaged `IPredicted<TState>` component. It owns its EcsLoop/TickClock, fixed-capacity RingBuffer input timeline, PredictionHistoryStore and AuthoritativeBaselineStore. It borrows the World/entity and systems, seals system configuration after admission, and detects component membership/scope changes. It does not copy the ECS sample into a game, implement socket logic, or pretend to roll back an entire physics world.

Contract:

- Admit a complete, nonzero-tick authoritative state with explicit `Reset(epoch,tick,state)`. Reconnect must preserve the new epoch; queued old messages remain old. Reset clears all old inputs/history and clock fraction. Session authentication and complete frame/schema validation precede this API.
- `TryGetSendRange(lastSentTick,redundancyTicks,...)` includes every new command plus the retained, unacknowledged redundant tail. A catch-up batch cannot skip its earliest new commands by selecting only relative to the new prediction head.
- `Predict(command)` records one input and runs exactly one fixed tick. A command is immutable once recorded. Game fields should already be in their wire-quantized form. An independent render cadence cannot change the tick delta.
- `Reconcile(epoch,tick,ack,state)` validates epoch, monotonic snapshot and resolved-input acknowledgement. A transport receipt is not a simulated input acknowledgement. Old/duplicate frames leave state/ack unchanged; wrong epoch cannot cross into a new history.
- Before correction, require the exact historical state, every exact input in `(tick,currentTick]`, and a bounded replay range. A gap/eviction/ahead-authority returns ResyncRequired without changing World, clock, histories or ack. There is no nearby-state fallback or substitution of current input for lost input.
- A component supplies deterministic value-only tolerance. Mismatch restores the authority anchor, truncates/rebuilds prediction history and invokes EcsLoop.Replay to the unchanged current tick. Replay time is explicit IsReplay=true. Forward-only effect systems are skipped. Structural replay is already prohibited by EcsLoop; purity of arbitrary IO/native handles remains a caller contract.
- Once mutation begins, exceptions retain the original exception and fault further execution until a complete explicit Reset. This is not transactional rollback of external side effects. Recreate after entity/component membership changes.
- `PredictionPresentationOffset` preserves render continuity across corrections, then exponentially decays an offset by a supplied half-life. It never blends authoritative simulation state or changes replay results.

The deterministic .NET/Unity tests assert corrected values and history anchors, exact replay counts, forward effect counts, tolerance, duplicate/reorder/epoch rejection, history eviction and explicit resync, replay budget, authority-ahead rejection, invalid ACK, reentry, component lifetime, exception recovery and presentation continuity. Warm generic simulation plus corrections allocates zero managed bytes in the bounded test (not a whole Unity/game performance guarantee).

Independent consumer fault injection adds a 0.9-second client-command blackout and delayed server data by 1.7 seconds, in addition to packet loss, reordering and duplicates. This forces observable input divergence and state-history eviction. The consumer must show actual corrected anchors/replay, fresh-authority resync, resumed prediction, remote-only interpolation and forward-only effect counts. Missing histories are never manufactured in game code.

Scope differences from the fixed FPSSample source: this profile predicts full ticks for one complete state, not its fractional final tick or heterogeneous predicted entity set. Dynamic collision-world history, predicted projectile identity, structural spawn/despawn rollback, 1P/3P animation and original adaptive RTT-based prediction-time tuning are not completed by this change. Static CharacterController stepping requires the existing framework adapter to restore value pose each step. No public/network security or zero-allocation whole-game claim is made.

## Predicted presentation events

`PredictedEventLedger` owns bounded pending event identity `(epoch, owner, lifetime, tick, channel, ordinal)` independently of replayable simulation. A new prediction starts presentation once; complete authoritative inclusive windows confirm it or return rejection transitions. Missing older coverage returns `HistoryExpired`, not a false rejection. Reordered and overlapping windows cannot repeat resolved events. `Close` retracts pending effects on disconnect; `Reset` requires a different scope and optionally seeds a baseline tick to suppress historic effects for a late joiner.

Callers provide the COMPLETE event set in each interval, not only the latest event. Caller buffers must hold pending count plus authoritative event count; malformed input validates before state mutation. Capacity overflow is explicit and must trigger caller resync/backpressure. Scope uniqueness is the session/lifetime owner's responsibility. The ledger does not rewind audio, apply damage, load assets or guarantee durable delivery beyond the retained window; consumers reclaim active speculative effects and separately choose visual expiry. Warm prediction/resolution allocates zero managed bytes; no whole-client allocation claim.

### Same-endpoint reconnect after a lost disconnect

The EjoyArena two-round weapon regression exposed a race: a dropped UDP Bye left the server's old endpoint/nonce alive for the same timeout used by the new client handshake. Rejecting every new nonce at that endpoint could time out the new client before the old server peer expired.

The server now permits one pending replacement handshake per endpoint, bounded by twice the peer budget overall. A Hello alone cannot evict or refresh the old peer. Only a Confirm carrying the fresh server challenge token replaces the old session, dispatching disconnect before connect. Old nonce/token traffic cannot close the replacement. These tokens reject stale traffic; they do not authenticate users.

`LostByeAllowsSameEndpointReconnectOnlyAfterFreshChallengeConfirmation` fails against the prior implementation and passes with this change, including a full one-peer server, lost Bye, delayed old Bye, and replayed old Hello/Confirm. The process probe supports `--drop-disconnects` to deterministically lose every Bye during real-player reconnect validation.
