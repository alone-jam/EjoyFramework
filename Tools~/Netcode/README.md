# Netcode offline validation

The codec project source-links the new tick-input codec, bounded command inbox, and focused NUnit tests. The session runtime/test projects source-link the real opt-in server, budgeted Loopback, and Core/ECS dependencies; they do not replace ECS or logging with stubs or include the legacy receive-driven session classes.

Current inputs:
- `NetBitReader` / `NetBitWriter`.
- `TickInputFrameHeader`, expected context, error values and codec.
- Golden-byte, boundary, overlap, rejection-atomicity and allocation-detector tests.
- `TickCommandInbox` and a fixed-step owner flow fixture proving that admission itself does not advance simulation.

`EjoyFramework.Core.SessionSupport.csproj` source-links the real engine-independent Core plus the same-tree ECS project for the upcoming connected-session suite; it does not replace logging or ECS with test stubs. Core's existing warnings remain visible rather than being suppressed. Per-project intermediate/output directories prevent the adjacent projects from sharing restore assets.

## Status

The codec, inbox, budgeted Loopback and connected server received scoped C# and security static reviews. The server's sample interface mismatch and revocation-time disposal reentry were fixed and reverified. **No build or test execution has completed for these projects.** Runtime allocation behavior, coverage and Unity/IL2CPP compatibility remain unverified.

When execution resumes, run from the repository root and keep build reports, logs and any restore/cache outputs inside the project according to the workspace rules. The isolated .NET result is not a substitute for the Unity EditMode/PlayMode package suites.

Command admission is implemented as an independent inbox; the connected tick-authoritative profile is a separate opt-in path under review. Neither changes how the legacy `NetcodeServer` processes input.

Contracts: [NetcodeTickProtocol](../../docs/NetcodeTickProtocol.md), [TickCommandAdmission](../../docs/TickCommandAdmission.md), [TickAuthoritativeSession](../../docs/TickAuthoritativeSession.md).
