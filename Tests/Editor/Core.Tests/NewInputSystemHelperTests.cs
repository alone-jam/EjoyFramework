#if EJOY_INPUTSYSTEM
using System.Linq;
using EjoyFramework.Core.Unity;
using NUnit.Framework;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace EjoyFramework.Tests
{
    public sealed class NewInputSystemHelperTests : InputTestFixture
    {
        [Test] public void PointerPositionFlowsThroughManagerAndHonorsContext()
        {
            var manager = new EjoyFramework.Core.Input.InputManager();
            EjoyFramework.Core.Framework.MarkMainThread();
            manager.SetHelper(new NewInputSystemHelper());
            Assert.That(manager.TryGetPointerPosition(out _,out _),Is.False);
            var mouse=InputSystem.AddDevice<Mouse>();Set(mouse.position,new UnityEngine.Vector2(123,456));
            Assert.That(manager.TryGetPointerPosition(out var x,out var y),Is.True);
            Assert.That(x,Is.EqualTo(123));Assert.That(y,Is.EqualTo(456));
            manager.PushContext(EjoyFramework.Core.Input.InputContext.Menu);
            Assert.That(manager.TryGetPointerPosition(out x,out y),Is.False);Assert.That(x+y,Is.Zero);
            manager.PopContext();Assert.That(manager.TryGetPointerPosition(out _,out _),Is.True);
            InputSystem.RemoveDevice(mouse);Assert.That(manager.TryGetPointerPosition(out _,out _),Is.False);
            manager.Shutdown();
        }
        [TestCase(0, MouseButton.Left)]
        [TestCase(1, MouseButton.Right)]
        [TestCase(2, MouseButton.Middle)]
        [TestCase(3, MouseButton.Forward)]
        [TestCase(4, MouseButton.Back)]
        public void MouseOnly_HeldPressRelease_MapToCurrentDevice(int index, MouseButton button)
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var helper = new NewInputSystemHelper();
            string name = "Mouse" + index;
            Assert.That(Keyboard.current, Is.Null);
            InputSystem.QueueStateEvent(mouse, new MouseState().WithButton(button));
            InputSystem.Update();
            Assert.That(helper.GetKey(name), Is.True);
            Assert.That(helper.GetKeyDown(name.ToLowerInvariant()), Is.True);
            InputSystem.QueueStateEvent(mouse, new MouseState());
            InputSystem.Update();
            Assert.That(helper.GetKey(name), Is.False);
            Assert.That(helper.GetKeyUp(name), Is.True);
            InputSystem.RemoveDevice(mouse);
            Assert.That(helper.GetKey(name), Is.False);
            Assert.That(helper.GetKeyDown(name), Is.False);
            Assert.That(helper.GetKeyUp(name), Is.False);
        }
        [Test] public void MissingButtonsAndDevices_ReturnFalse()
        {
            var helper = new NewInputSystemHelper();
            Assert.That(helper.GetKey("Mouse0"), Is.False);
            InputSystem.AddDevice<Mouse>();
            foreach (var name in new[] { null, "", "Mouse5", "Mouse6", "Mouse7", "Mouse-1", "Mouse01" })
            {
                Assert.That(helper.GetKey(name), Is.False);
                Assert.That(helper.GetKeyDown(name), Is.False);
                Assert.That(helper.GetKeyUp(name), Is.False);
            }
        }
        [TestCase("GamepadSouth", GamepadButton.South)]
        [TestCase("GamepadNorth", GamepadButton.North)]
        [TestCase("GamepadWest", GamepadButton.West)]
        [TestCase("GamepadEast", GamepadButton.East)]
        [TestCase("GamepadLeftShoulder", GamepadButton.LeftShoulder)]
        [TestCase("GamepadRightShoulder", GamepadButton.RightShoulder)]
        [TestCase("GamepadLeftStickPress", GamepadButton.LeftStick)]
        [TestCase("GamepadRightStickPress", GamepadButton.RightStick)]
        [TestCase("GamepadStart", GamepadButton.Start)]
        [TestCase("GamepadSelect", GamepadButton.Select)]
        [TestCase("GamepadDpadUp", GamepadButton.DpadUp)]
        [TestCase("GamepadDpadDown", GamepadButton.DpadDown)]
        [TestCase("GamepadDpadLeft", GamepadButton.DpadLeft)]
        [TestCase("GamepadDpadRight", GamepadButton.DpadRight)]
        public void GamepadOnly_ButtonEdgesFollowReplacementDevice(string name, GamepadButton button)
        {
            var helper = new NewInputSystemHelper();
            Assert.That(helper.GetKey(name), Is.False);
            var pad = InputSystem.AddDevice<Gamepad>();
            Assert.That(Keyboard.current, Is.Null);
            InputSystem.QueueStateEvent(pad, new GamepadState().WithButton(button)); InputSystem.Update();
            Assert.That(helper.GetKey(name.ToLowerInvariant()), Is.True);
            Assert.That(helper.GetKeyDown(name), Is.True);
            InputSystem.QueueStateEvent(pad, new GamepadState()); InputSystem.Update();
            Assert.That(helper.GetKeyUp(name), Is.True);
            InputSystem.RemoveDevice(pad);
            Assert.That(helper.GetKey(name), Is.False);
            pad = InputSystem.AddDevice<Gamepad>();
            Assert.That(helper.GetKeyDown(name), Is.False);
            InputSystem.QueueStateEvent(pad, new GamepadState().WithButton(button)); InputSystem.Update();
            Assert.That(helper.GetKeyDown(name), Is.True);
        }

        [Test] public void GamepadAxesAndTriggersFlowThroughContextAndDisconnect()
        {
            var manager = new EjoyFramework.Core.Input.InputManager();
            EjoyFramework.Core.Framework.MarkMainThread();
            manager.SetHelper(new NewInputSystemHelper());
            var names = new[] { "GamepadLeftStickX", "GamepadLeftStickY", "GamepadRightStickX", "GamepadRightStickY", "GamepadLeftTrigger", "GamepadRightTrigger" };
            foreach (var name in names)
            {
                manager.SetBinding(name, EjoyFramework.Core.Input.InputBinding.Axis(name.ToLowerInvariant()));
                Assert.That(manager.GetAxis(name), Is.Zero);
            }
            var pad = InputSystem.AddDevice<Gamepad>();
            InputSystem.QueueStateEvent(pad, new GamepadState { leftStick = new UnityEngine.Vector2(1, -1), rightStick = new UnityEngine.Vector2(-1, 1), leftTrigger = 0.75f, rightTrigger = 1 });
            InputSystem.Update();
            var expected = new[] { 1f, -1f, -1f, 1f, 0.75f, 1f };
            for (int i = 0; i < names.Length; i++) Assert.That(manager.GetAxis(names[i]), Is.EqualTo(expected[i]).Within(0.001f));
            manager.SetBinding("Fire", EjoyFramework.Core.Input.InputBinding.Key("GamepadRightTrigger"));
            Assert.That(manager.GetButtonDown("Fire"), Is.True);
            manager.PushContext(EjoyFramework.Core.Input.InputContext.Menu);
            foreach (var name in names) Assert.That(manager.GetAxis(name), Is.Zero);
            Assert.That(manager.GetButton("Fire"), Is.False);
            manager.PopContext();
            Assert.That(manager.GetButton("Fire"), Is.True);
            InputSystem.QueueStateEvent(pad, new GamepadState()); InputSystem.Update();
            Assert.That(manager.GetButtonUp("Fire"), Is.True);
            InputSystem.RemoveDevice(pad);
            foreach (var name in names) Assert.That(manager.GetAxis(name), Is.Zero);
            manager.Shutdown();
        }

        [Test] public void KeyboardAliasCache_FollowsReplacementKeyboard()
        {
            var helper = new NewInputSystemHelper();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter)); InputSystem.Update();
            Assert.That(helper.GetKey("Return"), Is.True);
            InputSystem.RemoveDevice(keyboard);
            keyboard = InputSystem.AddDevice<Keyboard>();
            Assert.That(helper.GetKey("Return"), Is.False);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter)); InputSystem.Update();
            Assert.That(helper.GetKeyDown("Return"), Is.True);
        }
    }
}
#endif
