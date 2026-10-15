# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed - Consumer resource, entity and Unity compatibility issues

- Entity groups use exclusive instance pools: simultaneous entities using the same prefab own
  distinct handles, and only hidden instances are reused (#17).
- `ResourceComponent` rejects the unsupported `Resources` mode before configuring the manager;
  `InitializeAsync` reports the unsupported mode rather than selecting an AssetBundle loader (#16).
- Static physics history indexes registered Collider objects directly, removing the two
  `GetInstanceID()` calls that fail compilation in Unity 6000.6+ (#3).
- Added shared entity lifecycle regressions and a standalone CoreCLR test project in CI, plus
  Unity EditMode tests for mode rejection and static collider registration identity.

The M2/M3/M4 development changes below have received scoped static reviews, but the combined package
has not yet been compiled or exercised in Unity. Historical CoreCLR results for an earlier M2 revision
are not validation of this integrated version; target-runtime allocations and coverage remain unmeasured.

### Added - Allocation-free collections and struct event channels

- Added `RingBuffer<T>`, `Deque<T>`, `BitSet` and `SlotMap<T>` (with generational `SlotHandle`) under
  `Runtime/Core/Collections/` in the `EjoyFramework.Core` namespace. All use struct enumerators and target
  allocation-free steady-state operation. `RingBuffer<T>` addresses elements by wrap-safe sequence numbers for tick history.
- Added `EventChannel<T>` for struct events passed by `in`, and `IEventManager.GetChannel<T>()` /
  `EventComponent.GetChannel<T>()`. The event manager dispatches queued channel events every frame and
  closes the channels on shutdown.

### Added - ECS simulation and prediction foundations

- Added fixed-tick time, phased simulation, per-tick capture, and explicitly eligible value-only replay.
  Replay preserves the real clock, rejects structural changes, and faults on cross-group command leakage.
- Added idempotent two-stage despawn, struct query visitors, capacity reservation, indexed component-type
  lookup, membership lifetimes, and sparse entity partitions with reserved ownership keys.
- Added frozen replication schemas, owned authoritative baselines, interpolation/prediction histories,
  exact-tick verification, and prevalidated single-/two-component state restoration.
- Added the generation-owned ECS streaming decorator: shared creation/despawn-request budgets, physical
  cleanup before IO unload, and persistence-aware cancellation/failure handling. It requires exactly-once IO callbacks.
- Added a closed two-component correction sample with exact recorded inputs and explicit recovery gating.
  It is not a general multi-entity rollback coordinator or a network/session implementation.

### Added - Offline tick-input protocol

- Added caller-buffer bit readers/writers with explicit LSB-first ordering, bounded cursors and canonical padding.
- Added the versioned 32-byte EJTI command-frame header and codec, exact context/length checks, and
  failure-atomic encode/decode with supported payload overlap.
- Added a bounded peer-owned `TickCommandInbox` with exact tagged tick slots, semantic decoding,
  first-valid-wins admission, and separate simulation/completed-command accounting. Admission has no
  simulation callback or client-controlled duration; authenticated routing and ingress budgets remain owner responsibilities.
- Added a narrow source-linked Netcode codec/admission test project. These independent layers do not
  switch legacy sessions, authenticate peers, drive simulation from receipt, or implement a transport.

### Changed

- ECS `ISystem.Update` now receives `in EcsTime`. The Unity driver registers systems through `Loop`,
  accumulates fixed ticks from Update, and maps legacy FixedUpdate configuration to that path.
- Command-buffer destruction now requests despawn. Default queries hide requested entities before
  physical removal; standalone groups flush after successful dispatch, loops run bounded cleanup batches.
- `EjoyFramework.Core` now references `EjoyFramework.Core.Ecs` for the streaming bridge. The ECS assembly
  itself retains zero assembly references. Host projects with copied ECS samples must migrate their copies.

- `EventPool` now locks its subscription table, so `Subscribe`, `Unsubscribe`, `TryUnsubscribe`, `Check`,
  `Count` and `SetDefaultHandler` are safe from any thread. `Update` rejects concurrent and re-entrant calls.
  After shutdown, `Fire` releases its arguments immediately and `Unsubscribe` no longer throws.
  `Subscribe` and `SetDefaultHandler` ignore late registrations under the subscription lock, including
  workers waiting across shutdown; `Subscribe(null)` still throws.
- `RingBuffer` and `Deque` physical indexing avoids integer overflow at large valid capacities.
- `IEventManager` gained the `GetChannel<T>()` member. Custom implementations of the interface must add it.
- Moved `BinaryHeap`, `NoAllocSort`, `GameLinkedList` and `MultiDictionary` into `Runtime/Core/Collections/`.
  Their namespace and Unity GUIDs are unchanged.
- Removed the redundant package prefix from Runtime, Editor and Tests layer directories
  (for example, `Runtime/Core`). Namespace and assembly names and Unity GUIDs are preserved;
  source paths, test fixtures, standalone tools and current documentation follow the new layout.

## [2.1.0] - 2026-08-23

### Added - Runtime performance counters

- Added allocation-free duration and integral value counters under
  `EjoyFramework.Core.Performance`, with value-type measurement scopes and snapshots.

### Fixed

- Fixed `ProcedureComponent.Start` so Unity recognizes and resumes its coroutine in
  Editor, Player, and headless batch mode.
- Configured `DataTableComponent` during `Awake`, before the framework composition-root
  validation runs.

## [2.0.0] - 2026-08-22

### Added — Typed UI routing and binary save codecs

- Added strongly typed `ScreenRoute<TViewModel, TArguments>` and
  `DialogRoute<TViewModel, TArguments, TResult>` contracts. Routed MVVM no longer transports
  arguments or view models through `object userData`, and dialog completion uses allocation-light
  value handles instead of `Task` / `TaskCompletionSource`.
- Added deterministic UI stack synchronization for Back, modal, external close, loaded-close, and
  loading-cancel paths. `UIStack.StackEntry` is now a value snapshot and per-open callback closures
  were removed.
- Added pluggable binary `ISaveCodec` payload serialization with Newtonsoft JSON dictionary support,
  while retaining legacy ESV1 save migration compatibility.
- Added resource batch/cache handles, config blobs, generated helper factories, object tracking,
  zero-GC text helpers, and expanded architecture/performance validation tests.

### Changed — Assembly and lifecycle contracts

- Unity-dependent production assemblies now use the `.Unity` marker; engine-agnostic assemblies no
  longer reference Unity assemblies.
- UI routes are available for registration during `Awake`, while navigation is explicitly gated
  until UI groups and helpers become ready in `Start`.
- Component lookup now separates optional `TryGet` semantics from required dependency resolution.

### Changed — Core renamed to `EjoyFramework.Core`

- The core assembly + folder + root/module namespaces were renamed `EjoyFramework` →
  `EjoyFramework.Core`. Consumers now use `using EjoyFramework.Core;`. **Breaking** for any
  external code referencing the `EjoyFramework` assembly/namespace.
- The core's Unity and Editor satellites were nested under `.Core` for consistency with the
  GamePlay layer: assembly+folder+namespace `EjoyFramework.Unity` (ns `EjoyFramework.Runtime`)
  → `EjoyFramework.Core.Unity`, and `EjoyFramework.Editor` (ns `EjoyFramework.EditorTools`)
  → `EjoyFramework.Core.Unity.Editor`. The family is now uniform:
  `Core` / `Core.Unity` / `Core.Editor` ‖ `GamePlay` / `GamePlay.Unity` / `GamePlay.Editor`.
  (`UIComponentEditor` qualifies `UnityEditor.Editor` to avoid the namespace/type clash.)

### Changed — Architecture: GamePlay parity + Core↔GamePlay layering

- **Battle moved out of core.** `Skill`/`Buff`/`IBattleEntity` (Skill+Buff+DamageCalculator) moved from
  `EjoyFramework` into `EjoyFramework.GamePlay.Battle` — it is game logic, not engine-agnostic
  infrastructure. Still reachable via `Framework.GetModule<ISkillManager>()`/`<IBuffManager>()`.
- **GamePlay managers reachable via `Framework.GetModule<>()`.** All 19 singleton-manager GamePlay
  systems (Tweening, Abilities, AntiCheat, Atmosphere, Chat, CloudSave, Crafting, Experiments,
  Factions, Guide, Items, Matchmaking, Music, Netcode, Quest, TouchInput, Units, Worldmap, Activities)
  are now `FrameworkModule`s with `IXxx` interfaces — parity with core. Constructor dependencies became
  `SetXxx(...)` setter injection; backend-required ones (Chat/CloudSave/Matchmaking) use the
  helper-injection contract. Value/algorithm types stay POCO.
- **Enforced one-way layering** `Core ← GamePlay`: GamePlay references `EjoyFramework`; core and its
  Test/Unity/Editor satellites reference nothing in GamePlay. Battle tests moved to
  `EjoyFramework.GamePlay.Tests`, the Skill/Buff editor window to `EjoyFramework.GamePlay.Unity.Editor`.

### Added — Live-Ops & Platform modules (11)

- Time: server-anchored corrected clock (Unix-ms anchor + local monotonic delta), tamper-resistant
- Timer: centralized delay / repeat / end-of-frame scheduling driven by framework Update
- Http: HTTP manager (BaseUrl + default headers + retry) over an injectable `IHttpClient`
- RemoteConfig: typed remote-config values with local defaults + injectable provider
- Purchase: IAP purchase flow + product catalog + optional server-side receipt validation
- Notification: local + remote push scheduling over an injectable platform
- Analytics: unified event/property funnel over an injectable `IAnalyticsBackend`
- Diagnostics: crash reporting + log sinks / remote logging
- RedDot: red-dot (message-badge) tree with parent propagation
- Performance: FPS/memory sampling surfaced in the Debugger profiler tab
- Serialization: `ByteBuffer` + binary/deep-copy contracts (impls generated by Editor codegen)

### Added — GamePlay layer (`EjoyFramework.GamePlay`, 31 systems)

- New engine-agnostic gameplay layer (`Runtime/EjoyFramework.GamePlay/`) with 31 cross-genre
  systems plus a shared `Core` (GameMath) directory: Abilities, Activities, AntiCheat,
  Atmosphere, Attributes, Battle, Chat, CloudSave, Crafting, Dialogue, Experiments, Factions,
  Guide, Items, Leaderboard, Loot, Mail, Matchmaking, Music, Netcode, Pathfinding, Production,
  Quest, Sequencing, Social, Targeting, Tech, TouchInput, Tweening, Units, Worldmap
- Unity adapters (`Runtime/EjoyFramework.GamePlay.Unity/`) for 7 systems: Abilities, Music,
  Netcode, TouchInput, Tweening, Units, Worldmap
- `EjoyFramework.GamePlay.Tests` EditMode assembly covering the gameplay systems

### Changed

- Test totals recomputed from the source tree (see Tests below)

## [1.0.0] - 2026-05-18

Initial UPM packaging release. Code was previously hosted under `Assets/EjoyFramework*`
and is now relocated to `Packages/com.ejoy.framework/` standard layout.

### Added — Core (Phase 0-11)

- 17 core managers: Event / Fsm / Procedure / Coroutine / ObjectPool / Resource /
  Entity / UI / Sound / Scene / Config / Localization / DataTable / DataNode /
  Setting / Network / Debugger
- AssetBundle pipeline with manifest, ref counting, dependency resolution,
  concurrent request coalescing
- IAssetLoadHandle state machine for cancellable loads
- BaseComponent drives `Framework.Update` from MonoBehaviour
- DebuggerComponent F1-toggle IMGUI with 9 built-in windows

### Added — Expansion (Phase 12-23, 8 modules)

- Patch: hot-update via manifest diff + serial download + md5 verify
- Save: multi-slot + AES-256-CBC + CRC32 envelope + version migration chain
- Streaming: world chunk loader with LoadRadius/UnloadRadius hysteresis + LOD
- Input: action-binding abstraction + InputContext stack
- AI: BehaviorTree with Sequence/Selector/Inverter/Condition/Action + typed Blackboard
- Battle: SkillManager (cooldown + cast events) + BuffManager (DOT/HOT/stacking)
  + DefaultDamageCalculator
- Security: SecureInt/SecureFloat (XOR memory cipher), MessageReplayDetector
  (sliding window), IntegrityCheck (CRC32 IEEE)
- Navigation: grid/path navigation manager (INavigationManager / NavigationManager)
- Network application layer: PacketRegistry, RpcManager, AesPacketCrypto
  (AES-256-CBC + HMAC-SHA256 constant-time MAC)
- Localization extensions: PluralRules, positional/named formatting
- Config toolchain (Editor): CsvParser, ConfigTableSchema, DataRowGenerator,
  ConfigValidator, one-click ConfigExporter
- Performance: PerformanceCollector ring buffer + Debugger profiler tab + CSV export

### Tests

Current counts (recomputed from the source tree):

- `EjoyFramework.Tests` (core EditMode): 549 — 536 `[Test]` + 13 `[TestCase]`
- `EjoyFramework.GamePlay.Tests` (EditMode): 1253 — 1230 `[Test]` + 23 `[TestCase]`
- `EjoyFramework.Tests.PlayMode` (PlayMode): 15 `[UnityTest]`
- 1817 test methods / cases total
