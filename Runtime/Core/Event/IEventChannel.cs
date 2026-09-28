//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

namespace EjoyFramework.Core.Event
{
    /// <summary>
    /// 事件管理器驱动 <see cref="EventChannel{T}"/> 的非泛型入口：每帧派发排队事件、关停时统一关闭。
    /// </summary>
    internal interface IEventChannel
    {
        int DispatchQueued();

        void Shutdown();
    }
}
