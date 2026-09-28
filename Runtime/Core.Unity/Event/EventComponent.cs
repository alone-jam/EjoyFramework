//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using EjoyFramework.Core.Event;
using System;
using UnityEngine;

namespace EjoyFramework.Core.Unity
{
    /// <summary>
    /// 事件组件。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("EjoyFramework/Core/Event")]
    public sealed class EventComponent : GameFrameworkComponent
    {
        private IEventManager m_EventManager;

        protected override void Awake()
        {
            base.Awake();
            m_EventManager = Framework.GetModule<IEventManager>();
            if (m_EventManager == null)
            {
                Log.Fatal("Event manager is invalid.");
                return;
            }
        }

        /// <summary>
        /// 获取事件处理函数的数量。
        /// </summary>
        public int EventHandlerCount
        {
            get { return m_EventManager.EventHandlerCount; }
        }

        /// <summary>
        /// 获取事件数量。
        /// </summary>
        public int EventCount
        {
            get { return m_EventManager.EventCount; }
        }

        /// <summary>
        /// 获取事件处理函数的数量。
        /// </summary>
        public int Count(int id)
        {
            return m_EventManager.Count(id);
        }

        /// <summary>
        /// 检查是否存在事件处理函数。
        /// </summary>
        public bool Check(int id, EventHandler<GameEventArgs> handler)
        {
            return m_EventManager.Check(id, handler);
        }

        /// <summary>
        /// 订阅事件处理函数。
        /// </summary>
        public void Subscribe(int id, EventHandler<GameEventArgs> handler)
        {
            m_EventManager.Subscribe(id, handler);
        }

        /// <summary>
        /// 取消订阅事件处理函数。
        /// </summary>
        public void Unsubscribe(int id, EventHandler<GameEventArgs> handler)
        {
            m_EventManager.Unsubscribe(id, handler);
        }

        /// <summary>
        /// 设置默认事件处理函数。
        /// </summary>
        public void SetDefaultHandler(EventHandler<GameEventArgs> handler)
        {
            m_EventManager.SetDefaultHandler(handler);
        }

        /// <summary>
        /// 抛出事件，线程安全。
        /// </summary>
        public void Fire(object sender, GameEventArgs e)
        {
            m_EventManager.Fire(sender, e);
        }

        /// <summary>
        /// 抛出事件立即模式。
        /// </summary>
        public void FireNow(object sender, GameEventArgs e)
        {
            m_EventManager.FireNow(sender, e);
        }

        /// <summary>
        /// 获取 struct 事件通道（每帧高频事件用，返回值应缓存）。
        /// </summary>
        public EventChannel<T> GetChannel<T>() where T : struct
        {
            return m_EventManager.GetChannel<T>();
        }
    }
}
