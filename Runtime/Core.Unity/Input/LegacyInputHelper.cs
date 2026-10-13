//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using EjoyFramework.Core.Input;
using UnityEngine;
using UnityInput = UnityEngine.Input;

namespace EjoyFramework.Core.Unity
{
    /// <summary>
    /// 旧版 Input Manager (UnityEngine.Input) 的 IInputHelper 实现。
    ///
    /// 注意：本项目正式使用 New Input System；此 helper 作为旧 Input 的回退保留（legacy fallback）。
    /// 性能：Enum.TryParse&lt;KeyCode&gt; 为反射 + 装箱，逐帧查询代价高。这里在首次遇到某个 keyName 时
    /// 解析一次并缓存到 m_KeyCache，热路径只做字典查找；无法解析的名字也缓存为 null 避免重复反射。
    /// </summary>
    public sealed class LegacyInputHelper : IInputHelper, IPointerInputHelper
    {
        // keyName -> KeyCode（解析失败缓存为 null，避免逐帧重复反射）。
        private readonly Dictionary<string, KeyCode?> m_KeyCache = new Dictionary<string, KeyCode?>(StringComparer.OrdinalIgnoreCase);

        public bool IsTouchSupported => UnityInput.touchSupported;

        public bool TryGetPointerPosition(out float x, out float y)
        {
            x = y = 0;
            if (!UnityInput.mousePresent) return false;
            var position = UnityInput.mousePosition;
            x = position.x; y = position.y; return true;
        }

        public bool GetKey(string keyName)
        {
            if (TryGetCached(keyName, out var k)) return UnityInput.GetKey(k);
            return false;
        }

        public bool GetKeyDown(string keyName)
        {
            if (TryGetCached(keyName, out var k)) return UnityInput.GetKeyDown(k);
            return false;
        }

        public bool GetKeyUp(string keyName)
        {
            if (TryGetCached(keyName, out var k)) return UnityInput.GetKeyUp(k);
            return false;
        }

        public float GetAxisRaw(string axisName) => UnityInput.GetAxisRaw(axisName);

        private bool TryGetCached(string name, out KeyCode code)
        {
            if (string.IsNullOrEmpty(name)) { code = default; return false; }
            if (!m_KeyCache.TryGetValue(name, out var cached))
            {
                // 首次遇到：解析一次并缓存（成功或失败都缓存，避免逐帧反射 + 装箱）。
                cached = Enum.TryParse<KeyCode>(name, ignoreCase: true, out var parsed) ? parsed : (KeyCode?)null;
                m_KeyCache[name] = cached;
            }
            if (cached.HasValue) { code = cached.Value; return true; }
            code = default;
            return false;
        }
    }
}
