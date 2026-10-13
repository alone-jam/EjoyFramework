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
