//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;

namespace EjoyFramework.Core
{
    /// <summary>
    /// 事件池。
    /// 修复：
    ///   1) Update 用双队列 swap，分发期间锁完全释放（生产者不阻塞）。
    ///   2) HandleEvent 对 handler 列表 snapshot 后再迭代，handler 内 Subscribe/Unsubscribe 不破坏当前轮派发。
    ///   3) 单个 handler 抛异常被 try-catch 隔离，不影响后续 handler。
    ///   4) 删除死代码 m_DefaultHandlers 字段。
    ///   5) 提供 TryUnsubscribe（不存在的 handler 返回 false）。
    ///   6) WS5-M2：订阅表加锁，线程契约不再依赖 Release 下被裁剪的 EnsureMainThread；
    ///      模式判断改位运算（Mono 的 Enum.HasFlag 会装箱）；关停后 Fire 直接回收、退订不抛出，关停时逐个事件报告未退订的处理器。
    ///
    /// 线程契约（始终生效）：
    ///   • Fire：任意线程。事件入加锁队列，由 Update 所在线程派发。关停后直接回收 e。
    ///   • Subscribe / Unsubscribe / TryUnsubscribe / Check / Count / EventHandlerCount / SetDefaultHandler：任意线程。
    ///     订阅表在锁内修改；派发在锁内拍快照、锁外调用处理器，所以另一线程退订返回后，
    ///     已拍好快照的那一轮派发仍可能再调用该处理器一次。
    ///   • Update：同一时刻只允许一个线程、且不可在处理器内重入，违反时抛出（双队列交换只对单一消费者成立）。
    ///   • FireNow：在调用线程同步执行处理器；池本身的状态在任意线程都一致，但处理器通常要求主线程，
    ///     EventManager 在编辑器 / 开发构建下对 FireNow 做主线程断言。
    /// </summary>
    internal sealed partial class EventPool<T> where T : BaseEventArgs
    {
        // 用于 ToArray-style snapshot 的临时缓冲池：每次 dispatch 借出，结束归还。
        // 线程本地：不同线程同时 FireNow 时各用各的缓冲。
        [ThreadStatic]
        private static EventHandler<T>[] s_HandlerScratch;
        // 重入深度：handler 内同步调用 FireNow 会重入 HandleEvent。
        // depth==0 时使用共享 s_HandlerScratch（零分配快路径）；
        // depth>0（嵌套）时为本次派发分配独立数组，避免内层覆盖/清空外层正在迭代的 scratch。
        [ThreadStatic]
        private static int s_Depth;

        private readonly MultiDictionary<int, EventHandler<T>> m_EventHandlers;
        // 订阅表锁：表的读写与派发快照都在锁内；处理器本身在锁外调用。
        private readonly object m_HandlerLock = new object();
        // 双队列 swap：m_PendingEvents 由 Fire 写入，m_DispatchEvents 由 Update 消费。
        // 不能锁 m_PendingEvents 本身：Update 会交换该字段，等待旧锁的生产者可能随后写入新队列。
        private readonly object m_QueueLock = new object();
        private Queue<Event> m_PendingEvents;
        private Queue<Event> m_DispatchEvents;
        private readonly EventPoolMode m_EventPoolMode;
        private EventHandler<T> m_DefaultHandler;
        // 正在执行 Update 的线程 id；0 表示空闲。
        private int m_UpdatingThreadId;
        private volatile bool m_IsShutdown;

        public EventPool(EventPoolMode mode)
        {
            m_EventHandlers = new MultiDictionary<int, EventHandler<T>>();
            m_PendingEvents = new Queue<Event>();
            m_DispatchEvents = new Queue<Event>();
            m_EventPoolMode = mode;
            m_DefaultHandler = null;
        }

        /// <summary>
        /// 已订阅的处理器**总数**（跨所有事件 ID 求和；不是事件 ID 种类数）。
        /// O(K) where K = 事件 ID 种类数；用于调试/统计，非热点路径。
        /// </summary>
        public int EventHandlerCount
        {
            get
            {
                lock (m_HandlerLock)
                {
                    int total = 0;
                    foreach (var kv in m_EventHandlers)
                    {
                        total += kv.Value.Count;
                    }
                    return total;
                }
            }
        }

        public int EventCount
        {
            get
            {
                lock (m_QueueLock) { return m_PendingEvents.Count; }
            }
        }

        /// <summary>是否已关停。</summary>
        public bool IsShutdown
        {
            get { return m_IsShutdown; }
        }

        public void Update(float elapseSeconds, float realElapseSeconds)
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            int busy = Interlocked.CompareExchange(ref m_UpdatingThreadId, current, 0);
            if (busy != 0)
            {
                throw new FrameworkException(busy == current
                    ? "EventPool.Update 不可在事件处理器内重入调用。"
                    : Utility.Text.Format("EventPool.Update 正在线程 {0} 上执行，不能在线程 {1} 并发调用：派发只属于一个线程。", busy, current));
            }

            try
            {
                // 1) swap 队列：持锁仅一瞬。
                Queue<Event> toDispatch;
                lock (m_QueueLock)
                {
                    if (m_PendingEvents.Count == 0)
                    {
                        return;
                    }
                    toDispatch = m_PendingEvents;
                    m_PendingEvents = m_DispatchEvents;
                    m_DispatchEvents = toDispatch;
                }

                // 2) 锁外分发，生产者线程可继续 Fire 到 m_PendingEvents。
                while (toDispatch.Count > 0)
                {
                    Event eventNode = toDispatch.Dequeue();
                    HandleEvent(eventNode.Sender, eventNode.EventArgs);
                    ReferencePool.Release(eventNode);
                }
            }
            finally
            {
                Volatile.Write(ref m_UpdatingThreadId, 0);
            }
        }

        public void Shutdown()
        {
            // 先置关停标记（与 Fire 同锁），此后的 Fire 直接回收，不会滞留在再也不会被 Update 的队列里。
            lock (m_QueueLock)
            {
                m_IsShutdown = true;
            }

            Clear();
            ClearAllHandlers();
        }

        public void Clear()
        {
            // 逐个出队并释放池化的 Event 结点与其 EventArgs；直接 Clear() 会丢弃这些引用 → 泄漏。
            // 关停期其他模块仍可能 Fire（事件入 m_PendingEvents 但 Update 不再运行），此处必须回收。
            lock (m_QueueLock)
            {
                while (m_PendingEvents.Count > 0)
                {
                    Event node = m_PendingEvents.Dequeue();
                    T args = node.EventArgs;
                    if (args != null) ReferencePool.Release(args);   // Event.Clear 不会释放内嵌 args
                    ReferencePool.Release(node);
                }
            }
            // 注：Update 单线程且不可重入，Shutdown 不会打断 Update，故 m_DispatchEvents 此刻必为空，无需处理。
        }

        private void ClearAllHandlers()
        {
            lock (m_HandlerLock)
            {
                // 逐个事件报告仍未退订的处理器（开发期定位漏退订）；清除后这些处理器的 Unsubscribe 不再抛出。
                foreach (var kv in m_EventHandlers)
                {
                    FrameworkLog.Debug("EventPool 关停：事件 {0} 仍有 {1} 个处理器未退订，已一并清除。", kv.Key, kv.Value.Count);
                }

                m_EventHandlers.Clear();
                m_DefaultHandler = null;
            }
        }

        public int Count(int id)
        {
            lock (m_HandlerLock)
            {
                GameLinkedListRange<EventHandler<T>> range = default(GameLinkedListRange<EventHandler<T>>);
                if (m_EventHandlers.TryGetValue(id, out range))
                {
                    return range.Count;
                }

                return 0;
            }
        }

        public bool Check(int id, EventHandler<T> handler)
        {
            if (handler == null)
            {
                throw new FrameworkException("Event handler is invalid.");
            }

            lock (m_HandlerLock)
            {
                return m_EventHandlers.Contains(id, handler);
            }
        }

        public void Subscribe(int id, EventHandler<T> handler)
        {
            if (handler == null)
            {
                throw new FrameworkException("Event handler is invalid.");
            }

            lock (m_HandlerLock)
            {
                if ((m_EventPoolMode & EventPoolMode.AllowMultiHandler) == 0)
                {
                    GameLinkedListRange<EventHandler<T>> range = default(GameLinkedListRange<EventHandler<T>>);
                    if (m_EventHandlers.TryGetValue(id, out range) && range.Count > 0)
                    {
                        throw new FrameworkException(Utility.Text.Format("Event '{0}' not allow multi handler.", id));
                    }
                }

                if ((m_EventPoolMode & EventPoolMode.AllowDuplicateHandler) == 0)
                {
                    if (m_EventHandlers.Contains(id, handler))
                    {
                        throw new FrameworkException(Utility.Text.Format("Event '{0}' not allow duplicate handler.", id));
                    }
                }

                m_EventHandlers.Add(id, handler);
            }
        }

        public void Unsubscribe(int id, EventHandler<T> handler)
        {
            if (handler == null)
            {
                throw new FrameworkException("Event handler is invalid.");
            }

            bool removed;
            lock (m_HandlerLock)
            {
                removed = m_EventHandlers.Remove(id, handler);
            }

            // 关停已清掉全部订阅：OnDestroy 晚于关停时的退订属正常时序，不抛出。
            if (!removed && !m_IsShutdown)
            {
                throw new FrameworkException(Utility.Text.Format("Event '{0}' not exists specified handler.", id));
            }
        }

        /// <summary>
        /// 尝试取消订阅。不存在时返回 false 而非抛异常，适合 GameObject 销毁路径调用。
        /// </summary>
        public bool TryUnsubscribe(int id, EventHandler<T> handler)
        {
            if (handler == null) return false;
            lock (m_HandlerLock)
            {
                return m_EventHandlers.Remove(id, handler);
            }
        }

        public void SetDefaultHandler(EventHandler<T> handler)
        {
            lock (m_HandlerLock)
            {
                m_DefaultHandler = handler;
            }
        }

        /// <summary>
        /// 抛出事件（线程安全），下一帧 Update 派发。
        /// 所有权约定：调用方移交 e 的所有权；e 必须由 ReferencePool.Acquire 获得，派发后框架自动 Release，调用方不得再持有/复用。
        /// 关停后调用：e 立即被 Release，不派发。
        /// </summary>
        public void Fire(object sender, T e)
        {
            if (e == null)
            {
                throw new FrameworkException("Event is invalid.");
            }

            Event eventNode = Event.Create(sender, e);
            lock (m_QueueLock)
            {
                if (!m_IsShutdown)
                {
                    m_PendingEvents.Enqueue(eventNode);
                    return;
                }
            }

            ReferencePool.Release(e);
            ReferencePool.Release(eventNode);
        }

        /// <summary>
        /// 立即派发，处理器在调用线程同步执行。
        /// 所有权约定：调用方移交 e 的所有权；e 必须由 ReferencePool.Acquire 获得，派发后框架自动 Release，调用方不得再持有/复用。
        /// </summary>
        public void FireNow(object sender, T e)
        {
            if (e == null)
            {
                throw new FrameworkException("Event is invalid.");
            }

            HandleEvent(sender, e);
        }

        private void HandleEvent(object sender, T e)
        {
            // 先把 Id 读进局部变量：下方 Release(e) 之后不能再读 e（use-after-release）。
            int eventId = e.Id;
            int handlerCount = 0;
            EventHandler<T>[] handlers = null;
            EventHandler<T> defaultHandler = null;

            // snapshot 一份 handler 列表（锁内）：handler 内或其他线程的 Subscribe/Unsubscribe 不影响本轮派发。
            // scratch 按进入时的重入深度选取：handler 内同步 FireNow 重入时 s_Depth>0，
            // 会拿到独立数组，绝不会覆盖/清空本轮正在迭代的 handlers。
            lock (m_HandlerLock)
            {
                GameLinkedListRange<EventHandler<T>> range = default(GameLinkedListRange<EventHandler<T>>);
                if (m_EventHandlers.TryGetValue(eventId, out range) && range.IsValid)
                {
                    handlerCount = range.Count;
                    handlers = AcquireScratch(handlerCount, s_Depth);
                    int idx = 0;
                    for (LinkedListNode<EventHandler<T>> n = range.First; n != null && n != range.Terminal; n = n.Next)
                    {
                        handlers[idx++] = n.Value;
                    }
                }
                else
                {
                    defaultHandler = m_DefaultHandler;
                }
            }

            bool noHandlerException = false;
            if (handlerCount > 0)
            {
                s_Depth++;
                try
                {
                    for (int i = 0; i < handlerCount; i++)
                    {
                        EventHandler<T> h = handlers[i];
                        handlers[i] = null; // 释放引用，避免 scratch 持有
                        if (h == null) continue;
                        try
                        {
                            h(sender, e);
                        }
                        catch (Exception ex)
                        {
                            FrameworkLog.Error("Event handler threw on event id={0}: {1}", eventId, ex);
                        }
                    }
                }
                finally
                {
                    s_Depth--;
                }
            }
            else if (defaultHandler != null)
            {
                try
                {
                    defaultHandler(sender, e);
                }
                catch (Exception ex)
                {
                    FrameworkLog.Error("Event default handler threw on event id={0}: {1}", eventId, ex);
                }
            }
            else if ((m_EventPoolMode & EventPoolMode.AllowNoHandler) == 0)
            {
                noHandlerException = true;
            }

            ReferencePool.Release(e);

            if (noHandlerException)
            {
                throw new FrameworkException(Utility.Text.Format("Event '{0}' not allow no handler.", eventId));
            }
        }

        /// <summary>
        /// 获取 handler snapshot 缓冲。depthAtEntry==0（非重入）时复用共享 s_HandlerScratch 实现零分配快路径；
        /// depthAtEntry>0（嵌套派发）时分配一份独立数组，避免内层覆盖外层正在迭代的共享缓冲。
        /// </summary>
        private static EventHandler<T>[] AcquireScratch(int neededCount, int depthAtEntry)
        {
            if (depthAtEntry > 0)
            {
                int local = 8;
                while (local < neededCount) local *= 2;
                return new EventHandler<T>[local];
            }

            EventHandler<T>[] scratch = s_HandlerScratch;
            if (scratch == null || scratch.Length < neededCount)
            {
                int sz = 8;
                while (sz < neededCount) sz *= 2;
                scratch = new EventHandler<T>[sz];
                s_HandlerScratch = scratch;
            }
            return scratch;
        }
    }
}
