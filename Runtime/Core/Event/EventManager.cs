//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace EjoyFramework.Core.Event
{
    /// <summary>
    /// 事件管理器。
    /// 两条通道：GameEventArgs 事件（按 id 订阅，池化引用参数，见 <see cref="IEventManager"/>）与
    /// struct 事件通道（<see cref="GetChannel{T}"/>，每帧高频事件）。Update 先派发 GameEventArgs 队列，
    /// 再按通道创建顺序派发各通道的排队事件；关停时两者一并关闭。
    /// </summary>
    internal sealed class EventManager : FrameworkModule, IEventManager
    {
        private readonly EventPool<GameEventArgs> m_EventPool;

        // struct 通道表：创建与查找在锁内（任意线程可取通道）；Update 在锁内取 (数组, 数量) 快照后锁外派发，
        // 追加时先复制到新数组再扩容，已取到的快照始终有效。
        private readonly object m_ChannelLock = new object();
        private readonly Dictionary<Type, IEventChannel> m_ChannelMap = new Dictionary<Type, IEventChannel>();
        private IEventChannel[] m_Channels = new IEventChannel[8];
        private int m_ChannelCount;
        private bool m_ChannelsShutdown;

        /// <summary>
        /// 初始化事件管理器的新实例。
        /// </summary>
        public EventManager()
        {
            m_EventPool = new EventPool<GameEventArgs>(EventPoolMode.AllowNoHandler | EventPoolMode.AllowMultiHandler);
        }

        /// <summary>
        /// 获取事件处理函数的数量。
        /// </summary>
        public int EventHandlerCount
        {
            get
            {
                return m_EventPool.EventHandlerCount;
            }
        }

        /// <summary>
        /// 获取事件数量。
        /// </summary>
        public int EventCount
        {
            get
            {
                return m_EventPool.EventCount;
            }
        }

        /// <summary>
        /// 获取游戏框架模块优先级。Priority 90：事件分发底座，仅次于 Coroutine。
        /// </summary>
        public override int Priority
        {
            get
            {
                return 90;
            }
        }

        /// <summary>
        /// 事件管理器轮询。
        /// </summary>
        public override void Update(float elapseSeconds, float realElapseSeconds)
        {
            m_EventPool.Update(elapseSeconds, realElapseSeconds);

            IEventChannel[] channels;
            int count;
            lock (m_ChannelLock)
            {
                channels = m_Channels;
                count = m_ChannelCount;
            }

            for (int i = 0; i < count; i++)
            {
                try
                {
                    channels[i].DispatchQueued();
                }
                catch (Exception ex)
                {
                    // 只可能是线程契约被违反（通道被别的线程占用）：记录后继续派发其余通道。
                    FrameworkLog.Error("EventManager：派发 struct 事件通道失败：{0}", ex);
                }
            }
        }

        /// <summary>
        /// 关闭并清理事件管理器。
        /// </summary>
        public override void Shutdown()
        {
            m_EventPool.Shutdown();

            IEventChannel[] channels;
            int count;
            lock (m_ChannelLock)
            {
                m_ChannelsShutdown = true;
                channels = m_Channels;
                count = m_ChannelCount;
            }

            for (int i = 0; i < count; i++)
            {
                try
                {
                    channels[i].Shutdown();
                }
                catch (Exception ex)
                {
                    FrameworkLog.Error("EventManager：关闭 struct 事件通道失败：{0}", ex);
                }
            }
        }

        /// <summary>
        /// 获取 struct 事件通道（同一类型始终返回同一实例）。任意线程可调用；返回值应缓存。
        /// </summary>
        public EventChannel<T> GetChannel<T>() where T : struct
        {
            lock (m_ChannelLock)
            {
                IEventChannel existing;
                if (m_ChannelMap.TryGetValue(typeof(T), out existing))
                {
                    return (EventChannel<T>)existing;
                }

                EventChannel<T> channel = new EventChannel<T>(Framework.MainThreadId);
                if (m_ChannelsShutdown)
                {
                    // 关停后才取的通道（如晚到的 OnDestroy 退订）：直接处于关停态，退订不抛、Fire 丢弃。
                    channel.MarkShutdown();
                }

                m_ChannelMap.Add(typeof(T), channel);
                if (m_ChannelCount == m_Channels.Length)
                {
                    IEventChannel[] grown = new IEventChannel[m_Channels.Length * 2];
                    Array.Copy(m_Channels, grown, m_ChannelCount);
                    m_Channels = grown;
                }

                m_Channels[m_ChannelCount++] = channel;
                return channel;
            }
        }

        /// <summary>
        /// 获取事件处理函数的数量。
        /// </summary>
        public int Count(int id)
        {
            return m_EventPool.Count(id);
        }

        /// <summary>
        /// 检查是否存在事件处理函数。
        /// </summary>
        public bool Check(int id, EventHandler<GameEventArgs> handler)
        {
            return m_EventPool.Check(id, handler);
        }

        /// <summary>
        /// 订阅事件处理函数。任意线程（订阅表加锁）。
        /// </summary>
        public void Subscribe(int id, EventHandler<GameEventArgs> handler)
        {
            m_EventPool.Subscribe(id, handler);
        }

        /// <summary>
        /// 取消订阅事件处理函数。任意线程；关停后不抛出。
        /// </summary>
        public void Unsubscribe(int id, EventHandler<GameEventArgs> handler)
        {
            m_EventPool.Unsubscribe(id, handler);
        }

        /// <summary>
        /// 尝试取消订阅。不存在则返回 false。任意线程。
        /// </summary>
        public bool TryUnsubscribe(int id, EventHandler<GameEventArgs> handler)
        {
            return m_EventPool.TryUnsubscribe(id, handler);
        }

        /// <summary>
        /// 设置默认事件处理函数。任意线程。
        /// </summary>
        public void SetDefaultHandler(EventHandler<GameEventArgs> handler)
        {
            m_EventPool.SetDefaultHandler(handler);
        }

        /// <summary>
        /// 抛出事件，线程安全（可从任意线程调用，事件在主线程 EventPool.Update 时派发）。
        /// </summary>
        public void Fire(object sender, GameEventArgs e)
        {
            // 故意不加 EnsureMainThread：Fire 设计上从工作线程入队是合法用法。
            m_EventPool.Fire(sender, e);
        }

        /// <summary>
        /// 抛出事件立即模式：处理器在调用线程同步执行。
        /// </summary>
        public void FireNow(object sender, GameEventArgs e)
        {
            // 池本身任意线程一致；主线程断言针对的是处理器（通常调用 Unity API）的线程要求，开发期提示。
            Framework.EnsureMainThread(nameof(FireNow));
            m_EventPool.FireNow(sender, e);
        }
    }
}
