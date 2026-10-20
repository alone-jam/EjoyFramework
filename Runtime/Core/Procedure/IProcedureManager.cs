//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.Core.Procedure
{
    /// <summary>
    /// 流程管理器接口。
    /// </summary>
    public interface IProcedureManager
    {
        /// <summary>Whether a procedure FSM has been initialized; safe before startup and after shutdown.</summary>
        bool IsInitialized { get; }

        /// <summary>
        /// 获取当前流程。
        /// </summary>
        ProcedureBase CurrentProcedure { get; }

        /// <summary>
        /// 获取当前流程持续时间。
        /// </summary>
        float CurrentProcedureTime { get; }

        /// <summary>
        /// 初始化流程管理器。
        /// </summary>
        /// <param name="fsmManager">有限状态机管理器。</param>
        /// <param name="procedures">流程管理器包含的流程。</param>
        void Initialize(Fsm.IFsmManager fsmManager, params ProcedureBase[] procedures);

        /// <summary>
        /// 开始流程。
        /// </summary>
        /// <typeparam name="T">要开始的流程类型。</typeparam>
        void StartProcedure<T>() where T : ProcedureBase;

        /// <summary>
        /// 开始流程。
        /// </summary>
        /// <param name="procedureType">要开始的流程类型。</param>
        void StartProcedure(Type procedureType);

        /// <summary>
        /// 是否存在流程。
        /// </summary>
        bool HasProcedure<T>() where T : ProcedureBase;

        /// <summary>
        /// 获取流程。
        /// </summary>
        ProcedureBase GetProcedure<T>() where T : ProcedureBase;
    }
}
