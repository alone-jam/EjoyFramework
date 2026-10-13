# Arena consumer architecture iteration

Real consumer review found a named-pool checkout mismatch, game-owned view lifecycle bookkeeping, brute-force center-distance hitscan and direct pointer backend access. The game also reimplemented cooldowns while the package already offers Abilities.

Added reusable capabilities:
- `PooledObjectSet<TKey,TObject>`: caller-owned single-spawn pool, explicit object-name bucket, keyed references, one retirement callback and delayed return, immediate owner clear. Retiring keys cannot be reused until returned/cleared. Capacity growth and factories can allocate; warmed Touch/Sweep is covered by a zero-GC test. Callbacks must not mutate the set recursively.
- `UnitRaycastQuery`: equal-radius circles over UnitWorld spatial candidates, first entry distance, stable ID tie, existing target filters. Caller synchronizes spatial data. No damage policy or Unity dependency. Candidate enumeration is measured separately; no claim of broad-phase optimality for long diagonal rays.
- `FsmRunner<T>`: scoped explicit driver of existing Fsm implementation, independent clock, deterministic disposal, no registration with global manager.
- optional `IPointerInputHelper` capability with context-aware InputManager query; New/Legacy backends implement it. Keyboard-only helpers remain usable.

Consumer now composes UnitWorld, StraightMover, AbilitySystem/AttributeSet costs, gameplay tags/duration effects and scoped FSM. Its callbacks and content retain game-specific rules. This is an initial architecture correction, not certification of the game as a complete production example. Resource/UI/save lifecycles and broader gameplay/performance remain acceptance gaps.

Validation: Unity 6000.4.8f1 full package EditMode: 3428 total, 3420 passed, 0 failed, 8 pre-existing skips; includes 16 new cases relative to 44be6c7. Evidence: Consumer.Final.Framework.xml. Unity 6000.6 remains untested. Existing PR #4 is not merged by this iteration.
