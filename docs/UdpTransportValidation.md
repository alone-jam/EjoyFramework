# UDP transport and tick-session validation

`UdpNetTransport` implements `IBudgetedNetTransport` on its constructing thread and defaults to IPv4 loopback. No background callback thread, DNS lookup, public bind or external service is required. Explicit non-loopback bind addresses are a deployment choice, not used in validation.

Protocol v1 is a new framework protocol, not wire-compatible with FPSSample protocol4. Four-way hello/challenge/confirm/ready exchanges establish endpoint and random session nonces. These reject stale packets; they are not user authentication or encryption. `TickSessionWelcomeCodec` separately validates session/world/schema identity, tick rate and controlled entity identity for the existing tick-authoritative profile.

Reliable messages use ordered stop-and-wait, acknowledgements and retransmission. Messages up to65536bytes split into1100byte fragments. Reassembly validates total size, index/count and session before publishing. Independent best-effort traffic drops obsolete sequences. Limits: default8peers (max128),64queued reliable messages and1MiB reliable payload per peer,4MiB delivered-event payload, bounded event counts and256datagrams perPoll. Reliable queue pressure throws rather than silently losing accepted sends. Incomplete reliable delivery and silent peers time out. This initial implementation is not a congestion-controlled Internet transport or a zero-allocation throughput claim.

Events collected from sockets wait until the nextPoll. Budgeted event delivery obeys the existing captured-queue contract; recursivePoll is rejected, callback exceptions are isolated and counted. Connection IDs are monotonic for an instance. Shutdown clears retained work. No transport ACK means a command has been simulated: authoritative acknowledgement remains the tick-server/snapshot responsibility.

Validation tools:

- `EjoyFramework.GamePlay.Netcode.Session.Tests.csproj`:78tests including real local sockets, fragmentation/caller-buffer lifetime, ordered delivery, budgets/reentry, disconnect/reconnect, malformed data and timeout.
- `EjoyFramework.GamePlay.Netcode.Codec.Tests.csproj`:114tests.
- `EjoyFramework.Udp.ProcessProbe.csproj` with `run_udp_process_probe.py`: separate server and two client OS processes through real loopback UDP relays. Deterministic dropping of every seventh datagram and initial handshake messages,12–48ms delay/reordering and duplicated data exercise retransmission.
- Transport clean and impaired runs pass65000byte messages,10perclient, exact content/order and both disconnects.
- Authority mode uses the actual `TickAuthoritativeServer`, command codec/inbox andEcsLoop at30Hz/60Hz, clients polling at5ms/33ms. Six seconds produce180/360simulation calls respectively despite loss and different send rates. The scalar probe validates infrastructure, not FPS gameplay or client prediction.

The runner can also start an explicitly built Unity validation player with `--player`. Non-development build and Xvfb are required in this cloud environment; development players broadcast editor discovery, and missing a display backend caused a native input crash. The final runner audits newly observed IPv4 UDP bindings/TCP listeners relative to a pre-launch namespace baseline. Per-PID fd inspection is denied by the environment, so this is not process-attributed packet capture. The game transport explicitly binds loopback; no external service or firewall change is used.

Unity consumers may request `--rounds 2` for disconnect/reconnect in the same three processes. Only a new loopback Hello may update a relay client endpoint. The consumer must validate fresh session identity, per-round gameplay thresholds and removal of old state. Startup waits for a complete ready JSON document, including the brief file-create/write interval. Run socket-audited processes separately from starting Unity Editors, because the audit covers the namespace rather than individual PIDs.
