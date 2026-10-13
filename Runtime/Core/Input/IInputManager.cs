//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.Core.Input
{
    /// <summary>
    /// 输入管理器：把按键 / 轴抽象为业务命名（"MoveX" "Jump"），业务不直接耦合 Unity Input System。
    ///
    /// 用法：
    /// <code>
    /// input.SetBinding("Jump", InputBinding.Key("Space"));
    /// input.PushContext(InputContext.Game);
    /// if (input.GetButtonDown("Jump")) ...
    /// </code>
    /// </summary>
    public interface IInputManager
    {
        /// <summary>注入底层 Input 后端（Unity InputSystem 等）。</summary>
        void SetHelper(IInputHelper helper);

        /// <summary>注册或更新一个按键绑定。</summary>
        void SetBinding(string actionName, InputBinding binding);

        /// <summary>获取绑定（业务侧重映射 UI 用）。</summary>
        InputBinding GetBinding(string actionName);

        /// <summary>清空所有绑定。</summary>
        void ClearBindings();

        /// <summary>当前 input context（栈式：UI 模式 push 后 game 操作不再生效）。</summary>
        InputContext CurrentContext { get; }

        /// <summary>压入 context。</summary>
        void PushContext(InputContext context);

        /// <summary>弹出 context（栈空时不动）。</summary>
        void PopContext();

        // 查询 API（仅 Game context 下生效；其他 context 全部返回 false/0）

        float GetAxis(string actionName);
        bool GetButton(string actionName);
        bool GetButtonDown(string actionName);
        bool GetButtonUp(string actionName);

        /// <summary>Current primary pointer in screen pixels; suppressed outside Game context.
        /// Returns false for helpers without pointer support. Coordinates are zero on failure.</summary>
        bool TryGetPointerPosition(out float x, out float y);

        // 通用指针

        bool IsTouchSupported { get; }
    }

    /// <summary>底层 Input 后端（Unity InputSystem 适配点）。</summary>
    public interface IInputHelper
    {
        bool IsTouchSupported { get; }
        bool GetKey(string keyName);
        bool GetKeyDown(string keyName);
        bool GetKeyUp(string keyName);
        float GetAxisRaw(string axisName);
    }

    /// <summary>Optional pointer backend capability; existing keyboard-only helpers remain valid.</summary>
    public interface IPointerInputHelper
    {
        bool TryGetPointerPosition(out float x, out float y);
    }

    /// <summary>输入上下文：Game / UI / Menu 互斥栈式。</summary>
    public enum InputContext
    {
        /// <summary>正常游戏内输入。</summary>
        Game,
        /// <summary>UI 优先（如打开菜单时禁用 game 输入）。</summary>
        UI,
        /// <summary>过场动画 / 加载时全部禁用。</summary>
        Cutscene,
        /// <summary>暂停 / 菜单。</summary>
        Menu,
    }

    /// <summary>输入绑定：可重映射的按键或轴。</summary>
    [Serializable]
    public sealed class InputBinding
    {
        public BindingKind Kind;
        public string Primary;          // key name / axis name
        public string Secondary;        // alt key (optional)

        public static InputBinding Key(string key, string altKey = null)
            => new InputBinding { Kind = BindingKind.Button, Primary = key, Secondary = altKey };

        public static InputBinding Axis(string axisName)
            => new InputBinding { Kind = BindingKind.Axis, Primary = axisName };
    }

    public enum BindingKind
    {
        Button,
        Axis,
    }
}
