# Explicit-tick character motion

The FPS consumer needs friction/projected acceleration and collision correction replay. Existing `ThirdPersonMotor` supplies a different acceleration/jump model, while `ThirdPersonCharacterMotor` drives Unity collision from Update. Keep the existing model and add reusable primitives in the existing ThreeC layer:

- `CharacterVelocitySolver`: stateless horizontal friction/projected acceleration, optional speed limiting, and gravity/terminal velocity. Inputs include explicit duration. Locomotion state, jump counts/durations and sprint eligibility remain game rules. Finite/nonnegative argument validation rejects malformed input before returning state.
- `CharacterControllerStepper`: restores the supplied starting position, executes Unity CharacterController.Move and returns actual displacement divided by the supplied duration. It never uses CharacterController.velocity or Time.deltaTime. It borrows the controller; the caller owns lifetime, shape and collision world.
- The existing ThirdPersonCharacterMotor now delegates collision/teleport to the same stepper, preserving its own Update-driven controller behavior.

Prediction callers restore their value state and recorded commands before EcsLoop.Replay; the loop retains the present clock tick. The stepper restores physical pose from that value state when it differs. Do not reset the clock to the saved tick before replaying to the present. Dynamic obstacles and other players must also be restored to their applicable collision state by the prediction host; these primitives do not provide world-wide rollback, lag compensation or cross-machine deterministic physics.

Tests use real Unity capsule/floor/wall/step/ramp collisions and explicit durations, not a mocked collision result. The game consumer uses framework World/EcsLoop, source-authored motion parameters and game jump state. Test primitives are fixtures, not substitute game maps. Original-map, multi-character, damage impulse, moving-platform and multiplayer prediction acceptance remain separate incomplete work.
