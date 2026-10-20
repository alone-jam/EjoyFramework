//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using EjoyFramework.Core.Fsm;
using EjoyFramework.Core.Procedure;
using System;
using System.Collections;
using UnityEngine;

namespace EjoyFramework.Core.Unity
{
    /// <summary>
    /// 流程组件。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("EjoyFramework/Core/Procedure")]
    public sealed class ProcedureComponent : GameFrameworkComponent
    {
        private IProcedureManager m_ProcedureManager;
        private ProcedureBase m_EntranceProcedure;

        [SerializeField]
        private string m_EntranceProcedureTypeName = null;

        [SerializeField]
        private string[] m_AvailableProcedureTypeNames = null;

        protected override void Awake()
        {
            base.Awake();
            m_ProcedureManager = Framework.GetModule<IProcedureManager>();
            if (m_ProcedureManager == null)
            {
                Log.Fatal("Procedure manager is invalid.");
                return;
            }
        }

        private IEnumerator Start()
        {
            ProcedureBase[] procedures = new ProcedureBase[m_AvailableProcedureTypeNames != null ? m_AvailableProcedureTypeNames.Length : 0];
            for (int i = 0; i < procedures.Length; i++)
            {
                // 经生成的工厂表创建（零反射）；Inspector 仍存类型全名。Editor 未登记时内部反射兜底。
                procedures[i] = GeneratedHelperFactory.Create(m_AvailableProcedureTypeNames[i], null) as ProcedureBase;
                if (procedures[i] == null)
                {
                    Log.Error("Can not create procedure '{0}'. Run EjoyFramework/Core/CodeGen/Generate Helper Factories.", m_AvailableProcedureTypeNames[i]);
                    yield break;
                }

                if (m_AvailableProcedureTypeNames[i] == m_EntranceProcedureTypeName)
                {
                    m_EntranceProcedure = procedures[i];
                }
            }

            if (m_EntranceProcedure == null)
            {
                Log.Error("Entrance procedure is invalid.");
                yield break;
            }

            IFsmManager fsmManager = Framework.GetModule<IFsmManager>();
            if (fsmManager == null)
            {
                Log.Fatal("FSM manager is invalid.");
                yield break;
            }

            m_ProcedureManager.Initialize(fsmManager, procedures);

            // Resume on the next player-loop frame after every component Start has run.
            // WaitForEndOfFrame is not guaranteed to resume in headless batch mode.
            yield return null;

            m_ProcedureManager.StartProcedure(m_EntranceProcedure.GetType());
        }

        /// <summary>
        /// 获取当前流程。
        /// </summary>
        public bool IsInitialized => m_ProcedureManager != null && m_ProcedureManager.IsInitialized;

        public ProcedureBase CurrentProcedure
        {
            get { return m_ProcedureManager.CurrentProcedure; }
        }

        /// <summary>
        /// 获取当前流程持续时间。
        /// </summary>
        public float CurrentProcedureTime
        {
            get { return m_ProcedureManager.CurrentProcedureTime; }
        }

        /// <summary>
        /// 是否存在流程。
        /// </summary>
        public bool HasProcedure<T>() where T : ProcedureBase
        {
            return m_ProcedureManager.HasProcedure<T>();
        }

        /// <summary>
        /// 获取流程。
        /// </summary>
        public ProcedureBase GetProcedure<T>() where T : ProcedureBase
        {
            return m_ProcedureManager.GetProcedure<T>();
        }
    }
}
