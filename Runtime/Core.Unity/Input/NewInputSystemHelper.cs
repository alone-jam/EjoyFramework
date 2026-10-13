//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

#if EJOY_INPUTSYSTEM
using System;
using System.Collections.Generic;
using EjoyFramework.Core.Input;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace EjoyFramework.Core.Unity
{
    /// <summary>
    /// New Input System (UnityEngine.InputSystem) 的 IInputHelper 实现，本项目首选后端。
    ///
    /// 接受与 LegacyInputHelper 相同的 KeyCode 风格按键名（"Space" "Return" "A" ...）与
    /// Unity 经典轴名（"Horizontal" "Vertical" "Mouse X" "Mouse Y"），从而上层 InputManager
    /// 的绑定无需改动即可在新旧后端间切换。
    ///
    /// 性能：首次遇到某个 keyName 时把 KeyCode 风格名解析成 InputSystem 的 <see cref="Key"/> 枚举
    /// 并缓存（解析失败缓存为 null），热路径只做字典查找；按帧查询直接读 Keyboard.current 的
    /// <see cref="KeyControl"/>，无反射 / 无装箱。
    /// </summary>
    public sealed class NewInputSystemHelper : IInputHelper, IPointerInputHelper
    {
        // keyName -> Key（解析失败缓存为 null，避免逐帧重复解析）。
        private readonly Dictionary<string, Key?> m_KeyCache =
            new Dictionary<string, Key?>(StringComparer.OrdinalIgnoreCase);

        public bool IsTouchSupported => Touchscreen.current != null;

        public bool TryGetPointerPosition(out float x, out float y)
        {
            x = y = 0;
            var pointer = Pointer.current;
            if (pointer == null) return false;
            var position = pointer.position.ReadValue();
            x = position.x; y = position.y; return true;
        }

        public bool GetKey(string keyName)
        {
            var c = ResolveKeyControl(keyName);
            return c != null && c.isPressed;
        }

        public bool GetKeyDown(string keyName)
        {
            var c = ResolveKeyControl(keyName);
            return c != null && c.wasPressedThisFrame;
        }

        public bool GetKeyUp(string keyName)
        {
            var c = ResolveKeyControl(keyName);
            return c != null && c.wasReleasedThisFrame;
        }

        /// <summary>
        /// 映射 Unity 经典轴名到新输入系统读数（[-1,1] / 屏幕 delta）。
        /// 未知轴名返回 0，与 legacy 在缺失轴时的容错语义保持一致。
        /// </summary>
        public float GetAxisRaw(string axisName)
        {
            if (string.IsNullOrEmpty(axisName)) return 0f;

            // 经典轴名（大小写不敏感）。
            if (string.Equals(axisName, "Horizontal", StringComparison.OrdinalIgnoreCase))
                return HorizontalAxis();
            if (string.Equals(axisName, "Vertical", StringComparison.OrdinalIgnoreCase))
                return VerticalAxis();
            if (string.Equals(axisName, "Mouse X", StringComparison.OrdinalIgnoreCase))
                return Mouse.current != null ? Mouse.current.delta.x.ReadValue() : 0f;
            if (string.Equals(axisName, "Mouse Y", StringComparison.OrdinalIgnoreCase))
                return Mouse.current != null ? Mouse.current.delta.y.ReadValue() : 0f;
            if (string.Equals(axisName, "Mouse ScrollWheel", StringComparison.OrdinalIgnoreCase))
                return Mouse.current != null ? Mouse.current.scroll.y.ReadValue() : 0f;

            return 0f;
        }

        // ===== 轴合成 =====

        private static float HorizontalAxis()
        {
            float v = 0f;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) v -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) v += 1f;
            }
            if (v == 0f)
            {
                var gp = Gamepad.current;
                if (gp != null) v = gp.leftStick.x.ReadValue();
            }
            return v;
        }

        private static float VerticalAxis()
        {
            float v = 0f;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v -= 1f;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v += 1f;
            }
            if (v == 0f)
            {
                var gp = Gamepad.current;
                if (gp != null) v = gp.leftStick.y.ReadValue();
            }
            return v;
        }

        // ===== Key 解析 =====

        /// <summary>解析 KeyCode 风格名到当前设备的 ButtonControl；标准 Mouse0–Mouse4 无需键盘。</summary>
        private ButtonControl ResolveKeyControl(string keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return null;
            // Resolve pointer buttons before requiring a keyboard (mouse-only devices are valid).
            // Input System's standard Mouse exposes five buttons; absent Mouse5/6 return null.
            if (keyName.Length == 6 && keyName.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase))
            {
                var mouse = Mouse.current;
                if (mouse == null) return null;
                switch (keyName[5])
                {
                    case '0': return mouse.leftButton;
                    case '1': return mouse.rightButton;
                    case '2': return mouse.middleButton;
                    case '3': return mouse.forwardButton;
                    case '4': return mouse.backButton;
                    case '5': case '6': return null;
                }
            }
            var kb = Keyboard.current;
            if (kb == null) return null;

            if (!m_KeyCache.TryGetValue(keyName, out var cached))
            {
                cached = MapName(keyName);
                m_KeyCache[keyName] = cached;
            }
            if (!cached.HasValue) return null;
            return kb[cached.Value];
        }

        /// <summary>
        /// 把 KeyCode 风格名映射到 InputSystem 的 <see cref="Key"/> 枚举。
        /// 先处理需要别名的特例，再回退到大小写不敏感的枚举解析。
        /// </summary>
        private static Key? MapName(string name)
        {
            switch (name.ToLowerInvariant())
            {
                // 与 UnityEngine.KeyCode 命名不同、需要别名的特例。
                case "return": return Key.Enter;
                case "keypadenter": return Key.NumpadEnter;
                case "leftcontrol": return Key.LeftCtrl;
                case "rightcontrol": return Key.RightCtrl;
                case "leftcommand":
                case "leftapple":
                case "leftwindows": return Key.LeftWindows;
                case "rightcommand":
                case "rightapple":
                case "rightwindows": return Key.RightWindows;
                case "alpha0": return Key.Digit0;
                case "alpha1": return Key.Digit1;
                case "alpha2": return Key.Digit2;
                case "alpha3": return Key.Digit3;
                case "alpha4": return Key.Digit4;
                case "alpha5": return Key.Digit5;
                case "alpha6": return Key.Digit6;
                case "alpha7": return Key.Digit7;
                case "alpha8": return Key.Digit8;
                case "alpha9": return Key.Digit9;
                case "keypad0": return Key.Numpad0;
                case "keypad1": return Key.Numpad1;
                case "keypad2": return Key.Numpad2;
                case "keypad3": return Key.Numpad3;
                case "keypad4": return Key.Numpad4;
                case "keypad5": return Key.Numpad5;
                case "keypad6": return Key.Numpad6;
                case "keypad7": return Key.Numpad7;
                case "keypad8": return Key.Numpad8;
                case "keypad9": return Key.Numpad9;
            }

            // 直接枚举解析覆盖大多数名字（Space/A/W/Escape/Tab/F1/LeftShift/UpArrow...）。
            return Enum.TryParse<Key>(name, ignoreCase: true, out var parsed) && parsed != Key.None
                ? parsed
                : (Key?)null;
        }
    }
}
#endif
