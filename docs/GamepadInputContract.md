# Named gamepad input contract

`NewInputSystemHelper` resolves physical gamepad controls through `Gamepad.current` on each query, including when no keyboard exists. Bind these names through `IInputManager`; its context stack suppresses game input in menus. No controller object is cached across device removal/replacement.

Buttons (case insensitive): `GamepadSouth`, `GamepadEast`, `GamepadWest`, `GamepadNorth`, `GamepadLeftShoulder`, `GamepadRightShoulder`, `GamepadLeftTrigger`, `GamepadRightTrigger`, `GamepadLeftStickPress`, `GamepadRightStickPress`, `GamepadStart`, `GamepadSelect`, `GamepadDpadUp/Down/Left/Right`.

Axes: `GamepadLeftStickX/Y`, `GamepadRightStickX/Y` (left/down negative, right/up positive), `GamepadLeftTrigger`, `GamepadRightTrigger` (0..1). Values use the Input System controls and their configured processors. Button trigger thresholds likewise follow Input System settings. Unknown names or absent devices return zero/false. Existing `Horizontal/Vertical` retain keyboard priority and left-stick fallback. These new explicit names are a New Input System contract, not a promise that arbitrary LegacyInputManager axes exist.

Mouse X/Y continue returning pixel deltas; authored legacy sensitivity conversion belongs in the consumer's settings, not in a global backend behavior change. Physical button names deliberately avoid platform-specific joystick indices.

Consumer regression: EjoyArena FPS command sampling against previous commit8ac6eb5 returns zero right-stick look; its device-event test fails (14/15 EditMode pass). Framework changes supply the missing axes/buttons, then the consumer must rerun using the new immutable Git commit. Framework device tests cover held/down/up, no-keyboard operation, removal/replacement and context suppression.
