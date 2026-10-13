# Arena application consumer regression

The independent EjoyArena consumer now loads its UI, battle prefab and additive scene from AssetBundles. This revealed framework defects not covered by its previous in-memory/Editor test baseline:

- UIManager used a multi-spawn pool for UI forms. Opening the same asset twice could borrow a still-active instance. The instance pool now uses single-spawn semantics. `ReleaseUnusedUIForms()` explicitly evicts closed forms while preserving active ones.
- Scene ownership had no cancellation boundary. `SceneLease` owns a request by its UserData identity. Dispose during native loading waits for completion and compensates success with unload; resources must remain owned until Released/Failed. ReleaseFailed retains ownership and can be retried. SceneManager now reports unload error details and removes pending entries when a helper throws synchronously.
- AssetBundleBuilder excluded all UnityEditor types, silently dropping SceneAsset. Its filter now permits runtime-loadable scene assets while continuing to reject editor-only types.

Tests cover concurrent same-asset forms, partial/final eviction, cancellation before scene completion, load failure, unload failure/retry, throwing helper rollback and the SceneAsset classification. The real game additionally verifies bundled scene entry/exit and resource counters.

The lease does not abort Unity's native scene load. The manager and helper must outlive the lease until terminal state; a failed native unload is not treated as released. This change does not claim all application shutdown or native loader failures are recoverable.

Validation on Unity 6000.4.8f1: framework EditMode 3,425 passed, 0 failed, 8 pre-existing skips; .NET 8 ECS 504 passed; package policy 2,667 files / 1,434 imported GUIDs. EjoyArena's independent full-flow acceptance is recorded in its own evidence directory. Unity 6000.6 remains untested.
