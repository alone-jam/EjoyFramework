using System;
using EjoyFramework.Core.Ecs;
using UnityEngine;

namespace EjoyFramework.Core.Unity
{
    /// <summary>
    /// 场景拥有的固定 tick 世界、时钟与分阶段循环。自动模式只由 Update 驱动；禁用时暂停，销毁时释放。
    /// 注册走 Loop.Add；外部不得单独驱动/重置/释放借用的 Clock、Loop、World。系统实例仍由调用方拥有。
    /// Inspector 时钟配置在 Initialize 时生效，运行中修改需先 Shutdown 再 Initialize。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("EjoyFramework/Core/ECS World")]
    public sealed class EcsWorldComponent : MonoBehaviour
    {
        [SerializeField] private EcsUpdateMode m_UpdateMode = EcsUpdateMode.Update;
        [SerializeField] private EcsTimeSource m_TimeSource = EcsTimeSource.Scaled;
        [SerializeField, Range(1, 1000)] private int m_TickRate = 30;
        [SerializeField, Min(1)] private int m_MaxTicksPerFrame = 4;
        [SerializeField] private double m_MaxFrameDelta = 0.25;
        // Unity 可能在加载线程上构造组件；不能在字段初始化器中捕获所属线程。
        private int m_OwnerThreadId;
        private World m_World;
        private TickClock m_Clock;
        private EcsLoop m_Loop;
        private bool m_Destroyed;

        public EcsUpdateMode UpdateMode
        {
            get => NormalizeMode(m_UpdateMode);
            set { RequireOwnerThread(); m_UpdateMode = NormalizeMode(value); }
        }

        public EcsTimeSource TimeSource
        {
            get => m_TimeSource;
            set { RequireOwnerThread(); ValidateTimeSource(value); m_TimeSource = value; }
        }

        public World World => m_World ?? throw new InvalidOperationException("ECS world has not been initialized.");
        public EcsLoop Loop => m_Loop ?? throw new InvalidOperationException("ECS world has not been initialized.");
        public TickClock Clock => m_Clock ?? throw new InvalidOperationException("ECS world has not been initialized.");
        public bool IsInitialized => m_World != null;

        private void Awake() => Initialize();

        public void Initialize()
        {
            RequireOwnerThread();
            if (m_Destroyed) throw new ObjectDisposedException(nameof(EcsWorldComponent));
            if (m_World != null) return;
            EcsUpdateMode mode = NormalizeMode(m_UpdateMode);
            ValidateTimeSource(m_TimeSource);
            var clock = new TickClock(m_TickRate, m_MaxTicksPerFrame, m_MaxFrameDelta);
            var world = new World();
            EcsLoop loop;
            try { loop = new EcsLoop(world, clock); }
            catch { world.Dispose(); throw; }
            m_World = world;
            m_Clock = clock;
            m_Loop = loop;
            m_UpdateMode = mode;
        }

        /// <summary>兼容 float 帧时长的入口；现在累积固定 tick，不再把可变 dt 直接交给模拟系统。</summary>
        public void Step(float deltaTime) => Step((double)deltaTime);

        /// <summary>
        /// 推进一个帧，返回完成的 tick 数；无 tick 时仍运行表现阶段。禁用组件不禁止显式 Step。
        /// 异常时停止自动推进、保留世界；故障循环需调用方恢复世界后 ResetClock，再显式重新启用组件。
        /// </summary>
        public int Step(double deltaTime)
        {
            RequireOwnerThread();
            try { return Loop.RunFrame(deltaTime); }
            catch { enabled = false; throw; }
        }

        /// <summary>仅重置时钟和循环故障状态，不回滚世界，也不自动启用组件。</summary>
        public void ResetClock(uint tick)
        {
            RequireOwnerThread();
            Loop.ResetClock(tick);
        }

        private void Update()
        {
            if (!isActiveAndEnabled || m_World == null) return;
            try
            {
                if (NormalizeMode(m_UpdateMode) == EcsUpdateMode.Manual) return;
                ValidateTimeSource(m_TimeSource);
                Step(m_TimeSource == EcsTimeSource.Unscaled ? Time.unscaledDeltaTime : Time.deltaTime);
            }
            catch { enabled = false; throw; }
        }

        public void Shutdown()
        {
            RequireOwnerThread();
            if (m_World == null) return;
            // 拒绝推进过程中的关停，不能先释放 World 再由 Loop.Dispose 报错。
            if (m_Loop.IsRunning) throw new InvalidOperationException("Cannot shut down a running ECS loop.");
            // World 检查外部 Query 的遍历守卫；被拒绝时必须保留完整循环。
            m_World.Dispose();
            m_Loop.Dispose();
            m_Loop = null;
            m_Clock = null;
            m_World = null;
        }

        private void OnDestroy()
        {
            Shutdown();
            m_Destroyed = true;
        }

        private static EcsUpdateMode NormalizeMode(EcsUpdateMode mode)
        {
            // 值 2 是旧 FixedUpdate 的序列化值；保留读取兼容，但不再注册 FixedUpdate 回调。
            if ((int)mode == 2) return EcsUpdateMode.Update;
            if (mode != EcsUpdateMode.Manual && mode != EcsUpdateMode.Update)
                throw new ArgumentOutOfRangeException(nameof(mode));
            return mode;
        }

        private static void ValidateTimeSource(EcsTimeSource source)
        {
            if (source != EcsTimeSource.Scaled && source != EcsTimeSource.Unscaled)
                throw new ArgumentOutOfRangeException(nameof(source));
        }

        private void RequireOwnerThread()
        {
            int current = Environment.CurrentManagedThreadId;
            int owner = System.Threading.Interlocked.CompareExchange(ref m_OwnerThreadId, current, 0);
            if (owner != 0 && current != owner)
                throw new InvalidOperationException("Use EcsWorldComponent only on its owning Unity thread.");
        }
    }
}
