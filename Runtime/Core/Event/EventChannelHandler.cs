//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

namespace EjoyFramework.Core.Event
{
    /// <summary>
    /// <see cref="EventChannel{T}"/> 的处理函数。事件按 <c>in</c> 引用传入：大结构体不按处理器逐个复制，处理器也不能改写它。
    /// lambda 需写出参数类型：<c>(in HitEvent e) =&gt; ...</c>。
    /// </summary>
    public delegate void EventChannelHandler<T>(in T e) where T : struct;
}
