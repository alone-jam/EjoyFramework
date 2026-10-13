//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System.Collections.Generic;

namespace EjoyFramework.Core.Input
{
    internal sealed class InputManager : FrameworkModule, IInputManager
    {
        private readonly Dictionary<string, InputBinding> m_Bindings = new Dictionary<string, InputBinding>();
        private readonly Stack<InputContext> m_ContextStack = new Stack<InputContext>();
        private IInputHelper m_Helper;
        // 缓存 helper 方法委托：GetButton* 每帧调用，method-group→delegate 转换每次都会分配一个 Func。
        // 在 SetHelper 时一次性绑定，避免每帧 GC。helper 为空时三者均为 null（Query 据此早退）。
        private System.Func<string, bool> m_GetKey;
        private System.Func<string, bool> m_GetKeyDown;
        private System.Func<string, bool> m_GetKeyUp;

        public InputManager() { m_ContextStack.Push(InputContext.Game); }

        public override int Priority { get { return 0; } }

        // 必需配置自检：依赖外部注入的 IInputHelper 才能读取按键/轴；未注入时所有查询静默返回默认值。
        public override bool RequiresConfiguration { get { return true; } }
        public override bool IsModuleConfigured { get { return m_Helper != null; } }
        public override string ConfigurationHint { get { return "InputManager needs IInputHelper — call GameEntry.Input.SetHelper(...) (or the framework's helper-injection) before use."; } }

        public override void Update(float a, float b) { }
        public override void Shutdown()
        {
            m_Bindings.Clear();
            m_ContextStack.Clear();
            m_ContextStack.Push(InputContext.Game);
        }

        public void SetHelper(IInputHelper helper)
        {
            Framework.EnsureMainThread(nameof(SetHelper));
            if (helper == null) throw new FrameworkException("Input helper is invalid.");
            m_Helper = helper;
            // 一次性缓存委托，供每帧 GetButton* 无分配复用。
            m_GetKey = helper.GetKey;
            m_GetKeyDown = helper.GetKeyDown;
            m_GetKeyUp = helper.GetKeyUp;
        }

        public void SetBinding(string actionName, InputBinding binding)
        {
            Framework.EnsureMainThread(nameof(SetBinding));
            if (string.IsNullOrEmpty(actionName)) throw new FrameworkException("actionName is empty.");
            if (binding == null) throw new FrameworkException("binding is null.");
            m_Bindings[actionName] = binding;
        }

        public InputBinding GetBinding(string actionName)
        {
            return m_Bindings.TryGetValue(actionName, out var b) ? b : null;
        }

        public void ClearBindings()
        {
            Framework.EnsureMainThread(nameof(ClearBindings));
            m_Bindings.Clear();
        }

        public InputContext CurrentContext
        {
            get { return m_ContextStack.Count > 0 ? m_ContextStack.Peek() : InputContext.Game; }
        }

        public void PushContext(InputContext context)
        {
            Framework.EnsureMainThread(nameof(PushContext));
            m_ContextStack.Push(context);
        }

        public void PopContext()
        {
            Framework.EnsureMainThread(nameof(PopContext));
            if (m_ContextStack.Count > 1) m_ContextStack.Pop();
        }

        public bool TryGetPointerPosition(out float x, out float y)
        {
            x = y = 0;
            if (CurrentContext != InputContext.Game || !(m_Helper is IPointerInputHelper pointer)) return false;
            if (pointer.TryGetPointerPosition(out x, out y)) return true;
            x = y = 0; return false;
        }

        // ===== 查询 =====
        // 只在 InputContext.Game 下生效；其他 context 屏蔽业务输入

        public float GetAxis(string actionName)
        {
            if (CurrentContext != InputContext.Game || m_Helper == null) return 0f;
            if (!m_Bindings.TryGetValue(actionName, out var b)) return 0f;
            if (b.Kind == BindingKind.Axis) return m_Helper.GetAxisRaw(b.Primary);
            // 按键模式映射到 -1/0/1
            return m_Helper.GetKey(b.Primary) ? 1f : 0f;
        }

        public bool GetButton(string actionName) => Query(actionName, m_GetKey);
        public bool GetButtonDown(string actionName) => Query(actionName, m_GetKeyDown);
        public bool GetButtonUp(string actionName) => Query(actionName, m_GetKeyUp);

        public bool IsTouchSupported => m_Helper != null && m_Helper.IsTouchSupported;

        private bool Query(string actionName, System.Func<string, bool> getter)
        {
            if (CurrentContext != InputContext.Game || getter == null) return false;
            if (!m_Bindings.TryGetValue(actionName, out var b)) return false;
            if (getter(b.Primary)) return true;
            if (!string.IsNullOrEmpty(b.Secondary) && getter(b.Secondary)) return true;
            return false;
        }
    }
}
