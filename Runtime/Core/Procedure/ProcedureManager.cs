//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using EjoyFramework.Core.Fsm;
using System;

namespace EjoyFramework.Core.Procedure
{
    /// <summary>
    /// 流程管理器。
    /// </summary>
    internal sealed class ProcedureManager : FrameworkModule, IProcedureManager
    {
        private IFsmManager m_FsmManager;
        private IFsm<IProcedureManager> m_ProcedureFsm;

        public ProcedureManager()
        {
            m_FsmManager = null;
            m_ProcedureFsm = null;
        }

        public override int Priority
        {
            // Priority -100：业务流程总线，必须最后 tick & 最先 Shutdown（依赖上层所有模块）。
            get { return -100; }
        }

        public bool IsInitialized => m_ProcedureFsm != null;

        public ProcedureBase CurrentProcedure
        {
            get
            {
                if (m_ProcedureFsm == null)
                {
                    throw new FrameworkException("You must initialize procedure first.");
                }

                return (ProcedureBase)m_ProcedureFsm.CurrentState;
            }
        }

        public float CurrentProcedureTime
        {
            get
            {
                if (m_ProcedureFsm == null)
                {
                    throw new FrameworkException("You must initialize procedure first.");
                }

                return m_ProcedureFsm.CurrentStateTime;
            }
        }

        public override void Update(float elapseSeconds, float realElapseSeconds)
        {
        }

        public override void Shutdown()
        {
            if (m_FsmManager != null)
            {
                if (m_ProcedureFsm != null)
                {
                    m_FsmManager.DestroyFsm(m_ProcedureFsm);
                    m_ProcedureFsm = null;
                }

                m_FsmManager = null;
            }
        }

        public void Initialize(IFsmManager fsmManager, params ProcedureBase[] procedures)
        {
            Framework.EnsureMainThread(nameof(Initialize));
            if (fsmManager == null)
            {
                throw new FrameworkException("FSM manager is invalid.");
            }

            m_FsmManager = fsmManager;
            m_ProcedureFsm = m_FsmManager.CreateFsm(this, procedures);
        }

        public void StartProcedure<T>() where T : ProcedureBase
        {
            Framework.EnsureMainThread(nameof(StartProcedure));
            if (m_ProcedureFsm == null)
            {
                throw new FrameworkException("You must initialize procedure first.");
            }

            m_ProcedureFsm.Start<T>();
        }

        public void StartProcedure(Type procedureType)
        {
            Framework.EnsureMainThread(nameof(StartProcedure));
            if (m_ProcedureFsm == null)
            {
                throw new FrameworkException("You must initialize procedure first.");
            }

            m_ProcedureFsm.Start(procedureType);
        }

        public bool HasProcedure<T>() where T : ProcedureBase
        {
            if (m_ProcedureFsm == null)
            {
                throw new FrameworkException("You must initialize procedure first.");
            }

            return m_ProcedureFsm.HasState<T>();
        }

        public ProcedureBase GetProcedure<T>() where T : ProcedureBase
        {
            if (m_ProcedureFsm == null)
            {
                throw new FrameworkException("You must initialize procedure first.");
            }

            return m_ProcedureFsm.GetState<T>();
        }
    }
}
