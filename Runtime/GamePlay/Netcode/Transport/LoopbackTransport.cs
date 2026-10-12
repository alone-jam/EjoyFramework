//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using EjoyFramework.Core;

namespace EjoyFramework.GamePlay.Netcode.Transport
{
    /// <summary>
    /// 进程内回环传输（确定性，<b>无套接字 / 无线程</b>）。用于单元测试与单进程内的 Host+Client。
    /// <para>
    /// 通过 <see cref="CreatePair"/> 创建一对已配对的服务器 / 客户端实例：二者共享一个内部路由器，
    /// 所有投递都进入<b>各实例自己的事件队列</b>，仅在该实例的 <see cref="Poll()"/> 中按入队顺序排空触发，
    /// 因此完全确定性且有序。支持多个客户端连接同一服务器（connectionId 自增分配）。
    /// </para>
    /// 单线程使用，非线程安全；发送、路由及生命周期操作仍遵守原有 owner-thread 契约。
    /// <para>两个 Poll 重载共享单消费者守卫，拒绝同实例并发 / 回调重入（抛出
    /// <see cref="InvalidOperationException"/>）；这是对原先不支持的重入行为的显式收紧，
    /// 并不使其余操作线程安全。参数为空的 Poll 仍按入口队列计数快照派发。</para>
    /// <para><see cref="Poll(int, int)"/> 只限制事件消费数及投递数据字节数，不限制回调耗时、
    /// 发送 / 生命周期成本或队列内存，也不认证连接身份。原有 payload 拷贝、集合快照、
    /// GetInvocationList 及异常日志分配仍然存在，并非零 GC 传输。</para>
    /// </summary>
    public sealed class LoopbackTransport : IBudgetedNetTransport
    {
        // 排队事件的种类。
        private enum EventKind
        {
            Connected,
            Disconnected,
            Data,
        }

        // 单条排队事件。Data 携带 payload 的独立副本（投递语义为“拷贝”，与真实传输一致）。
        private readonly struct QueuedEvent
        {
            public readonly EventKind Kind;
            public readonly int ConnectionId;
            public readonly byte[] Payload;

            public QueuedEvent(EventKind kind, int connectionId, byte[] payload)
            {
                Kind = kind;
                ConnectionId = connectionId;
                Payload = payload;
            }
        }

        // 配对双方共享的路由器：维护“服务器端实例”与每个客户端实例之间的连接映射。
        private sealed class LoopbackRouter
        {
            public LoopbackTransport Server;

            // connectionId -> 对应客户端实例。
            public readonly Dictionary<int, LoopbackTransport> Clients = new Dictionary<int, LoopbackTransport>();

            // 客户端实例 -> 其在服务器侧的 connectionId（一个客户端实例只连一个服务器，故唯一）。
            public readonly Dictionary<LoopbackTransport, int> ClientIds = new Dictionary<LoopbackTransport, int>();

            private int m_NextConnectionId;

            // 注册一个客户端连接，返回分配的 connectionId。
            public int RegisterClient(LoopbackTransport client)
            {
                int id = m_NextConnectionId++;
                Clients[id] = client;
                ClientIds[client] = id;
                return id;
            }

            // 注销一个客户端连接，返回其 connectionId（不存在返回 -1）。
            public int UnregisterClient(LoopbackTransport client)
            {
                if (!ClientIds.TryGetValue(client, out int id))
                {
                    return -1;
                }

                ClientIds.Remove(client);
                Clients.Remove(id);
                return id;
            }
        }

        private readonly LoopbackRouter m_Router;
        private readonly Queue<QueuedEvent> m_EventQueue = new Queue<QueuedEvent>();
        // 仅防止两个消费者交错 Peek / Dequeue；不保护并发生产者或生命周期操作。
        private int m_PollActive;
        // Shutdown 清空队列后使有界 Poll 的入口快照失效，避免消费回调中新入队的替代事件。
        private int m_QueueClearVersion;

        private bool m_IsServer;
        private bool m_IsClient;
        private NetConnectionState m_ClientState = NetConnectionState.Disconnected;

        // 本地（客户端角色）connectionId，连接成功后由路由器分配；服务器角色下恒为 -1。
        private int m_LocalConnectionId = -1;

        /// <inheritdoc />
        public bool IsServer
        {
            get { return m_IsServer; }
        }

        /// <inheritdoc />
        public bool IsClient
        {
            get { return m_IsClient; }
        }

        /// <inheritdoc />
        public NetConnectionState ClientState
        {
            get { return m_ClientState; }
        }

        /// <inheritdoc />
        public event Action<int> OnClientConnected;

        /// <inheritdoc />
        public event Action<int> OnClientDisconnected;

        /// <inheritdoc />
        public event Action<int, ArraySegment<byte>> OnData;

        // 私有构造：实例只能经由 CreatePair 创建，保证服务器 / 客户端共享同一路由器。
        private LoopbackTransport(LoopbackRouter router)
        {
            m_Router = router;
        }

        /// <summary>
        /// 创建一对已配对的回环实例（共享内部路由器）。
        /// <para>返回的 <c>server</c> 已处于服务器角色，<c>client</c> 已处于客户端角色，调用
        /// <c>client.Connect(...)</c>（host/port 被忽略）即可建立连接。</para>
        /// <para>可对同一 <c>server</c> 多次配对客户端：再调一次本方法会得到一个新路由器；
        /// 若需多客户端连接<b>同一</b>服务器，请使用 <see cref="CreateClient"/>。</para>
        /// </summary>
        /// <returns>已配对的 (server, client) 实例元组。</returns>
        public static (INetTransport server, INetTransport client) CreatePair()
        {
            LoopbackRouter router = new LoopbackRouter();

            LoopbackTransport server = new LoopbackTransport(router);
            server.m_IsServer = true;
            router.Server = server;

            LoopbackTransport client = new LoopbackTransport(router);
            client.m_IsClient = true;

            return (server, client);
        }

        /// <summary>
        /// 为已存在的回环服务器再创建一个客户端实例（连接到同一服务器，用于多客户端测试）。
        /// </summary>
        /// <param name="server">由 <see cref="CreatePair"/> 返回的服务器实例。</param>
        /// <returns>新的客户端实例。</returns>
        /// <exception cref="ArgumentException"><paramref name="server"/> 不是回环服务器实例。</exception>
        public static INetTransport CreateClient(INetTransport server)
        {
            LoopbackTransport srv = server as LoopbackTransport;
            if (srv == null || !srv.m_IsServer)
            {
                throw new ArgumentException("server 必须是由 LoopbackTransport.CreatePair 创建的服务器实例。", nameof(server));
            }

            LoopbackTransport client = new LoopbackTransport(srv.m_Router);
            client.m_IsClient = true;
            return client;
        }

        /// <inheritdoc />
        public void StartServer(int port)
        {
            // 回环服务器无需监听端口：CreatePair 时已就绪，此处仅作幂等确认。
            m_IsServer = true;
        }

        /// <inheritdoc />
        public void StopServer()
        {
            if (!m_IsServer)
            {
                return;
            }

            // 断开所有客户端：服务器侧入队 Disconnected，并通知各客户端入队 Disconnected。
            // 先快照，避免在遍历中修改集合。
            List<LoopbackTransport> clients = new List<LoopbackTransport>(m_Router.ClientIds.Keys);
            foreach (LoopbackTransport client in clients)
            {
                int id = m_Router.UnregisterClient(client);
                if (id < 0)
                {
                    continue;
                }

                Enqueue(new QueuedEvent(EventKind.Disconnected, id, null));

                client.m_ClientState = NetConnectionState.Disconnected;
                client.m_LocalConnectionId = -1;
                client.Enqueue(new QueuedEvent(EventKind.Disconnected, id, null));
            }
        }

        /// <inheritdoc />
        public void Connect(string host, int port)
        {
            if (m_IsServer)
            {
                return;
            }

            m_IsClient = true;

            // 已连接则忽略重复调用。
            if (m_ClientState == NetConnectionState.Connected)
            {
                return;
            }

            LoopbackTransport server = m_Router.Server;
            if (server == null)
            {
                // 没有配对服务器：连接失败，客户端入队断开事件。
                m_ClientState = NetConnectionState.Disconnected;
                Enqueue(new QueuedEvent(EventKind.Disconnected, -1, null));
                return;
            }

            int connectionId = m_Router.RegisterClient(this);
            m_LocalConnectionId = connectionId;
            m_ClientState = NetConnectionState.Connected;

            // 服务器侧入队 Connected(connectionId)；客户端侧入队 Connected(connectionId)。
            server.Enqueue(new QueuedEvent(EventKind.Connected, connectionId, null));
            Enqueue(new QueuedEvent(EventKind.Connected, connectionId, null));
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            if (!m_IsClient || m_ClientState != NetConnectionState.Connected)
            {
                return;
            }

            int id = m_Router.UnregisterClient(this);
            m_ClientState = NetConnectionState.Disconnected;
            m_LocalConnectionId = -1;

            // 服务器侧入队该连接的 Disconnected；客户端自身也入队 Disconnected。
            LoopbackTransport server = m_Router.Server;
            if (server != null && id >= 0)
            {
                server.Enqueue(new QueuedEvent(EventKind.Disconnected, id, null));
            }

            Enqueue(new QueuedEvent(EventKind.Disconnected, id, null));
        }

        /// <inheritdoc />
        public void Kick(int connectionId)
        {
            if (!m_IsServer)
            {
                return;
            }

            // 找到对应客户端实例并注销该连接；不存在则为空操作（幂等）。
            if (!m_Router.Clients.TryGetValue(connectionId, out LoopbackTransport client))
            {
                return;
            }

            int id = m_Router.UnregisterClient(client);
            if (id < 0)
            {
                return;
            }

            // 服务器侧入队该连接的 Disconnected；并通知该客户端入队 Disconnected 并复位其状态。
            Enqueue(new QueuedEvent(EventKind.Disconnected, id, null));

            client.m_ClientState = NetConnectionState.Disconnected;
            client.m_LocalConnectionId = -1;
            client.Enqueue(new QueuedEvent(EventKind.Disconnected, id, null));
        }

        /// <inheritdoc />
        public void Send(int connectionId, ArraySegment<byte> data, NetDeliveryMethod method = NetDeliveryMethod.ReliableOrdered)
        {
            byte[] copy = ToArrayCopy(data);

            if (m_IsServer)
            {
                // 服务器 -> 指定客户端：在该客户端实例上入队 Data(connectionId)。
                if (m_Router.Clients.TryGetValue(connectionId, out LoopbackTransport client))
                {
                    client.Enqueue(new QueuedEvent(EventKind.Data, connectionId, copy));
                }

                return;
            }

            // 客户端 -> 服务器：忽略 connectionId，在服务器实例上入队 Data(本地 connId)。
            if (m_IsClient && m_ClientState == NetConnectionState.Connected)
            {
                LoopbackTransport server = m_Router.Server;
                if (server != null)
                {
                    server.Enqueue(new QueuedEvent(EventKind.Data, m_LocalConnectionId, copy));
                }
            }
        }

        /// <inheritdoc />
        public void Broadcast(ArraySegment<byte> data, NetDeliveryMethod method = NetDeliveryMethod.ReliableOrdered)
        {
            if (!m_IsServer)
            {
                return;
            }

            byte[] payload = ToArrayCopy(data);

            // 快照客户端集合，避免投递过程中集合被修改。每个目标各拿一份独立副本。
            List<KeyValuePair<int, LoopbackTransport>> targets =
                new List<KeyValuePair<int, LoopbackTransport>>(m_Router.Clients);

            foreach (KeyValuePair<int, LoopbackTransport> pair in targets)
            {
                byte[] copy = new byte[payload.Length];
                Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
                pair.Value.Enqueue(new QueuedEvent(EventKind.Data, pair.Key, copy));
            }
        }

        /// <summary>
        /// 按入口队列计数快照派发全部事件，不限制数据字节数；回调新增事件留到下次 Poll。
        /// </summary>
        /// <exception cref="InvalidOperationException">同一实例已有 Poll 正在执行（包括回调重入）。</exception>
        public void Poll()
        {
            EnterPoll();
            try
            {
                // 保留原有计数快照循环，不委托给带 int.MaxValue 字节上限的重载。
                int count = m_EventQueue.Count;
                for (int i = 0; i < count; i++)
                {
                    QueuedEvent evt = m_EventQueue.Dequeue();
                    Dispatch(evt);
                }
            }
            finally
            {
                Volatile.Write(ref m_PollActive, 0);
            }
        }

        /// <inheritdoc />
        public void Poll(int maxEvents, int maxDataBytes)
        {
            if (maxEvents < 0)
                throw new ArgumentOutOfRangeException(nameof(maxEvents));
            if (maxDataBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(maxDataBytes));

            EnterPoll();
            try
            {
                PollBudgeted(maxEvents, maxDataBytes);
            }
            finally
            {
                Volatile.Write(ref m_PollActive, 0);
            }
        }

        private void EnterPoll()
        {
            if (Interlocked.CompareExchange(ref m_PollActive, 1, 0) != 0)
                throw new InvalidOperationException("LoopbackTransport.Poll cannot overlap or reenter on the same instance.");
        }

        private void PollBudgeted(int maxEvents, int maxDataBytes)
        {
            int count = Math.Min(m_EventQueue.Count, maxEvents);
            int queueClearVersion = m_QueueClearVersion;
            int remainingBytes = maxDataBytes;
            for (int i = 0; i < count && queueClearVersion == m_QueueClearVersion; i++)
            {
                QueuedEvent evt = m_EventQueue.Peek();
                int dataBytes = evt.Kind == EventKind.Data ? evt.Payload.Length : 0;
                if (dataBytes > remainingBytes)
                {
                    // 暂停，或仅本轮剩余字节不足：保持队头与 FIFO，不能跳过后派发控制事件。
                    if (maxDataBytes == 0 || dataBytes <= maxDataBytes)
                        break;

                    // 单包超过正数总预算：永久丢弃，仍占一次事件预算，但不占投递字节预算。
                    m_EventQueue.Dequeue();
                    continue;
                }

                // Peek 与 Dequeue 之间不调用用户代码；两个 Poll 重载共用同一个消费者守卫。
                m_EventQueue.Dequeue();
                remainingBytes -= dataBytes;
                Dispatch(evt);
            }
        }

        /// <inheritdoc />
        public void Shutdown()
        {
            if (m_IsServer)
            {
                StopServer();
                m_IsServer = false;
            }
            else if (m_IsClient)
            {
                Disconnect();
                m_IsClient = false;
            }

            m_EventQueue.Clear();
            unchecked { m_QueueClearVersion++; }
            m_ClientState = NetConnectionState.Disconnected;
            m_LocalConnectionId = -1;
        }

        // 入队一条事件。
        private void Enqueue(QueuedEvent evt)
        {
            m_EventQueue.Enqueue(evt);
        }

        // 触发单条事件对应的回调。逐处理器隔离：单个处理器抛异常既不影响同一事件的其余处理器，也不
        // 中断本帧后续事件的派发（与 Core EventPool 的逐处理器异常隔离一致）。
        private void Dispatch(QueuedEvent evt)
        {
            switch (evt.Kind)
            {
                case EventKind.Connected:
                    SafeInvoke(OnClientConnected, evt.ConnectionId, evt.Kind);
                    break;

                case EventKind.Disconnected:
                    SafeInvoke(OnClientDisconnected, evt.ConnectionId, evt.Kind);
                    break;

                case EventKind.Data:
                    SafeInvoke(OnData, evt.ConnectionId, new ArraySegment<byte>(evt.Payload), evt.Kind);
                    break;
            }
        }

        // 遍历多播委托的 invocation list，逐处理器 try-catch，单个处理器异常被捕获记日志而不波及其余处理器。
        private static void SafeInvoke(Action<int> handlers, int connId, EventKind kind)
        {
            if (handlers == null) return;
            Delegate[] list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try { ((Action<int>)list[i])(connId); }
                catch (Exception ex)
                {
                    FrameworkLog.Error("LoopbackTransport handler threw on event kind={0}, connId={1}: {2}", kind, connId, ex);
                }
            }
        }

        private static void SafeInvoke(Action<int, ArraySegment<byte>> handlers, int connId, ArraySegment<byte> data, EventKind kind)
        {
            if (handlers == null) return;
            Delegate[] list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try { ((Action<int, ArraySegment<byte>>)list[i])(connId, data); }
                catch (Exception ex)
                {
                    FrameworkLog.Error("LoopbackTransport handler threw on event kind={0}, connId={1}: {2}", kind, connId, ex);
                }
            }
        }

        // 把数据段复制成独立数组（保证投递后调用方可复用其缓冲区）。
        private static byte[] ToArrayCopy(ArraySegment<byte> data)
        {
            if (data.Count == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] copy = new byte[data.Count];
            Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Count);
            return copy;
        }
    }
}
