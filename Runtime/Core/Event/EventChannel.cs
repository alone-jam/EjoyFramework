//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Threading;

namespace EjoyFramework.Core.Event
{
    /// <summary>
    /// struct 事件直发通道：每种事件类型一个通道，处理器按 <c>in</c> 收到事件本体。
    /// 与 <see cref="IEventManager"/> 的 GameEventArgs 事件相比：无引用池往返、无按 id 查表、无虚调用、无装箱，
    /// 用于每帧高频事件（命中、伤害、网络输入、ECS 系统间通知）。低频、需要 sender / 多态参数的事件继续用 GameEventArgs。
    ///
    /// 取得方式：<c>GameEntry.Event.GetChannel&lt;T&gt;()</c>（由事件管理器每帧派发排队事件，关停时统一关闭），
    /// 或自行 <c>new EventChannel&lt;T&gt;()</c>（自己调用 <see cref="DispatchQueued"/> / <see cref="Shutdown"/>，如 ECS World 私有通道）。
    /// 返回的通道引用应缓存下来，不要每帧取。
    ///
    /// 线程契约（全部始终生效，不随构建配置裁剪）：
    ///   • 通道属于一个线程（所属线程）。事件管理器创建的通道属于 Framework 主线程；自建通道在第一次调用
    ///     Subscribe / FireNow 等所属线程 API 时绑定到调用线程。
    ///   • 只有 <see cref="Fire"/> 可在任意线程调用：事件复制进加锁队列，由所属线程下一次 <see cref="DispatchQueued"/> 派发。
    ///   • 其余 API（订阅、退订、FireNow、DispatchQueued、Shutdown）只能在所属线程调用，否则抛 FrameworkException。
    ///
    /// 派发语义：
    ///   • 顺序：按订阅顺序调用；同一通道的排队事件按 Fire 的先后顺序派发。
    ///   • 重入：处理器内可订阅 / 退订 / 再次 FireNow。派发中新订阅的处理器不参与本轮（但参与处理器内嵌套的 FireNow）；
    ///     派发中退订的处理器若本轮还没轮到，就不再被调用（这点与 GameEventArgs 事件的快照语义不同，
    ///     更适合"先到的处理器销毁了后面的订阅者"的场景）。处理器内 Fire 的事件在下一次 DispatchQueued 派发。
    ///   • 异常：单个处理器抛出的异常记录到 FrameworkLog.Error，不影响其余处理器，也不会打断排队事件的派发。
    ///   • 重复订阅同一处理器（同一目标 + 方法）抛出；Unsubscribe 找不到处理器时抛出，TryUnsubscribe 返回 false。
    ///   • 关停后：Fire 被丢弃、订阅被忽略（打一条警告）、退订不抛出（适合 OnDestroy 晚于关停的情况）。
    ///
    /// 零分配：订阅数组只增不减，稳态（容量够用、处理器委托已缓存）下订阅 / 退订 / FireNow / Fire / DispatchQueued 都不分配；
    /// 排队缓冲按 2 倍扩容后复用。注意 <c>Subscribe(OnHit)</c> 这种方法组写法每次都会新建委托，应缓存到字段。
    /// </summary>
    public sealed class EventChannel<T> : IEventChannel where T : struct
    {
        private static readonly string s_TypeName = typeof(T).Name;

        // 订阅数组：[0, m_SlotCount) 为已用槽位；派发期间退订只把槽位置 null（墓碑），最外层派发结束后压实。
        private EventChannelHandler<T>[] m_Handlers;
        private int m_SlotCount;
        private int m_HandlerCount;
        private int m_DispatchDepth;
        private bool m_HasTombstones;
        private int m_OwnerThreadId;
        private volatile bool m_IsShutdown;

        // 双缓冲队列：Fire 在锁内写 m_Queue；DispatchQueued 在锁内交换后，锁外派发 m_DispatchBuffer。
        private readonly object m_QueueLock = new object();
        private T[] m_Queue;
        private int m_QueueCount;
        private T[] m_DispatchBuffer;
        private bool m_DispatchingQueue;

        /// <summary>创建自建通道：所属线程在第一次调用所属线程 API 时绑定。</summary>
        public EventChannel()
            : this(0)
        {
        }

        /// <param name="ownerThreadId">所属线程 id；0 表示首次使用时绑定。</param>
        internal EventChannel(int ownerThreadId)
        {
            m_Handlers = new EventChannelHandler<T>[4];
            m_Queue = Array.Empty<T>();
            m_DispatchBuffer = Array.Empty<T>();
            m_OwnerThreadId = ownerThreadId;
        }

        /// <summary>已订阅的处理器数。</summary>
        public int HandlerCount
        {
            get { return m_HandlerCount; }
        }

        /// <summary>等待 <see cref="DispatchQueued"/> 的事件数（任意线程可读）。</summary>
        public int QueuedCount
        {
            get
            {
                lock (m_QueueLock)
                {
                    return m_QueueCount;
                }
            }
        }

        /// <summary>所属线程 id；0 表示尚未绑定。</summary>
        public int OwnerThreadId
        {
            get { return Volatile.Read(ref m_OwnerThreadId); }
        }

        /// <summary>是否已关停。</summary>
        public bool IsShutdown
        {
            get { return m_IsShutdown; }
        }

        /// <summary>订阅。重复订阅同一处理器抛出。所属线程调用。</summary>
        public void Subscribe(EventChannelHandler<T> handler)
        {
            if (handler == null)
            {
                throw new FrameworkException("EventChannel.Subscribe：handler 不能为 null。");
            }

            EnsureOwnerThread("Subscribe");
            if (m_IsShutdown)
            {
                FrameworkLog.Warning("EventChannel<{0}> 已关停，忽略订阅 {1}。", s_TypeName, handler.Method.Name);
                return;
            }

            if (IndexOf(handler) >= 0)
            {
                throw new FrameworkException(Utility.Text.Format(
                    "EventChannel<{0}>：处理器 {1} 已订阅，不允许重复订阅。", s_TypeName, handler.Method.Name));
            }

            if (m_SlotCount == m_Handlers.Length)
            {
                // 派发中扩容也安全：派发循环每次都重新读取 m_Handlers，新数组保留相同前缀。
                Array.Resize(ref m_Handlers, m_Handlers.Length * 2);
            }

            m_Handlers[m_SlotCount++] = handler;
            m_HandlerCount++;
        }

        /// <summary>退订；处理器不存在时抛出（关停后不抛）。所属线程调用。</summary>
        public void Unsubscribe(EventChannelHandler<T> handler)
        {
            if (handler == null)
            {
                throw new FrameworkException("EventChannel.Unsubscribe：handler 不能为 null。");
            }

            EnsureOwnerThread("Unsubscribe");
            if (!RemoveHandler(handler) && !m_IsShutdown)
            {
                throw new FrameworkException(Utility.Text.Format(
                    "EventChannel<{0}>：处理器 {1} 未订阅。不确定是否已订阅时用 TryUnsubscribe。", s_TypeName, handler.Method.Name));
            }
        }

        /// <summary>尝试退订；不存在返回 false。所属线程调用。</summary>
        public bool TryUnsubscribe(EventChannelHandler<T> handler)
        {
            if (handler == null)
            {
                return false;
            }

            EnsureOwnerThread("TryUnsubscribe");
            return RemoveHandler(handler);
        }

        /// <summary>处理器是否已订阅。所属线程调用。</summary>
        public bool Check(EventChannelHandler<T> handler)
        {
            if (handler == null)
            {
                throw new FrameworkException("EventChannel.Check：handler 不能为 null。");
            }

            EnsureOwnerThread("Check");
            return IndexOf(handler) >= 0;
        }

        /// <summary>退订全部处理器。派发中调用时，本轮尚未轮到的处理器不再被调用。所属线程调用。</summary>
        public void UnsubscribeAll()
        {
            EnsureOwnerThread("UnsubscribeAll");
            ClearHandlers();
        }

        /// <summary>立即同步派发给全部处理器。所属线程调用。</summary>
        public void FireNow(in T e)
        {
            EnsureOwnerThread("FireNow");
            Invoke(in e);
        }

        /// <summary>
        /// 排队，任意线程可调用：事件被复制进队列，所属线程下一次 <see cref="DispatchQueued"/> 时派发
        /// （事件管理器创建的通道每帧自动派发）。关停后丢弃。
        /// </summary>
        public void Fire(in T e)
        {
            lock (m_QueueLock)
            {
                if (m_IsShutdown)
                {
                    return;
                }

                if (m_QueueCount == m_Queue.Length)
                {
                    Array.Resize(ref m_Queue, m_Queue.Length == 0 ? 8 : m_Queue.Length * 2);
                }

                m_Queue[m_QueueCount++] = e;
            }
        }

        /// <summary>
        /// 派发调用前已排队的全部事件，返回派发个数。派发期间新 Fire 的事件留到下一次。
        /// 所属线程调用，不可重入（处理器内调用会抛出）。
        /// </summary>
        public int DispatchQueued()
        {
            EnsureOwnerThread("DispatchQueued");
            if (m_DispatchingQueue)
            {
                throw new FrameworkException(Utility.Text.Format(
                    "EventChannel<{0}>.DispatchQueued 不可重入：处理器内请用 FireNow 同步派发，或 Fire 留到下一次。", s_TypeName));
            }

            T[] batch;
            int count;
            lock (m_QueueLock)
            {
                count = m_QueueCount;
                if (count == 0)
                {
                    return 0;
                }

                batch = m_Queue;
                m_Queue = m_DispatchBuffer;
                m_DispatchBuffer = batch;
                m_QueueCount = 0;
            }

            m_DispatchingQueue = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Invoke(in batch[i]);
                }
            }
            finally
            {
                Array.Clear(batch, 0, count);   // 事件里若有引用字段，不延长其寿命
                m_DispatchingQueue = false;
            }

            return count;
        }

        /// <summary>关停：丢弃排队事件、退订全部处理器；之后 Fire 被丢弃、订阅被忽略。所属线程调用，可重复调用。</summary>
        public void Shutdown()
        {
            EnsureOwnerThread("Shutdown");
            lock (m_QueueLock)
            {
                m_IsShutdown = true;
                Array.Clear(m_Queue, 0, m_QueueCount);
                m_QueueCount = 0;
            }

            ClearHandlers();
        }

        /// <summary>只置关停标记，供事件管理器在关停后新建通道时使用（此时尚无处理器，可在任意线程调用）。</summary>
        internal void MarkShutdown()
        {
            lock (m_QueueLock)
            {
                m_IsShutdown = true;
            }
        }

        private void Invoke(in T e)
        {
            int count = m_SlotCount;
            if (count == 0)
            {
                return;
            }

            m_DispatchDepth++;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    EventChannelHandler<T> handler = m_Handlers[i];
                    if (handler == null)
                    {
                        continue;   // 本轮派发中已退订
                    }

                    try
                    {
                        handler(in e);
                    }
                    catch (Exception ex)
                    {
                        FrameworkLog.Error("EventChannel<{0}> 处理器抛出异常：{1}", s_TypeName, ex);
                    }
                }
            }
            finally
            {
                m_DispatchDepth--;
                if (m_DispatchDepth == 0 && m_HasTombstones)
                {
                    Compact();
                }
            }
        }

        private bool RemoveHandler(EventChannelHandler<T> handler)
        {
            int index = IndexOf(handler);
            if (index < 0)
            {
                return false;
            }

            if (m_DispatchDepth > 0)
            {
                // 派发中不挪动槽位，外层循环的下标保持有效。
                m_Handlers[index] = null;
                m_HasTombstones = true;
            }
            else
            {
                int last = m_SlotCount - 1;
                if (index < last)
                {
                    Array.Copy(m_Handlers, index + 1, m_Handlers, index, last - index);
                }

                m_Handlers[last] = null;
                m_SlotCount = last;
            }

            m_HandlerCount--;
            return true;
        }

        private void ClearHandlers()
        {
            Array.Clear(m_Handlers, 0, m_SlotCount);
            if (m_DispatchDepth > 0)
            {
                m_HasTombstones = true;
            }
            else
            {
                m_SlotCount = 0;
            }

            m_HandlerCount = 0;
        }

        private int IndexOf(EventChannelHandler<T> handler)
        {
            // 先比引用（缓存的委托，零开销），再比目标 + 方法（每次新建的方法组委托）。
            int count = m_SlotCount;
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(m_Handlers[i], handler))
                {
                    return i;
                }
            }

            for (int i = 0; i < count; i++)
            {
                EventChannelHandler<T> current = m_Handlers[i];
                if (current != null && current.Equals(handler))
                {
                    return i;
                }
            }

            return -1;
        }

        private void Compact()
        {
            int write = 0;
            for (int read = 0; read < m_SlotCount; read++)
            {
                EventChannelHandler<T> handler = m_Handlers[read];
                if (handler != null)
                {
                    m_Handlers[write++] = handler;
                }
            }

            Array.Clear(m_Handlers, write, m_SlotCount - write);
            m_SlotCount = write;
            m_HasTombstones = false;
        }

        private void EnsureOwnerThread(string apiName)
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            if (m_OwnerThreadId == current)
            {
                return;
            }

            // 未绑定则绑定到当前线程；已绑定到别的线程则抛出。绑定只发生一次，之后只读。
            int owner = Interlocked.CompareExchange(ref m_OwnerThreadId, current, 0);
            if (owner == 0 || owner == current)
            {
                return;
            }

            throw new FrameworkException(Utility.Text.Format(
                "EventChannel<{0}>.{1} 只能在所属线程（id={2}）调用，当前线程 id={3}。跨线程投递请用 Fire，由所属线程的 DispatchQueued 派发。",
                s_TypeName, apiName, owner, current));
        }
    }
}
