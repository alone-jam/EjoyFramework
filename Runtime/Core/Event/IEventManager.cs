//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.Core.Event
{
    /// <summary>
    /// 事件管理器接口。
    /// 线程契约（始终生效，不随构建配置裁剪）：
    ///   • Fire：任意线程，事件在主线程下一次轮询时派发。
    ///   • Subscribe / Unsubscribe / TryUnsubscribe / Check / Count / SetDefaultHandler：任意线程（订阅表加锁）；
    ///     另一线程退订返回后，已开始的那一轮派发仍可能再调用该处理器一次。
    ///   • FireNow：处理器在调用线程同步执行，按惯例只在主线程调用（编辑器 / 开发构建下断言）。
    ///   • 关停后 Fire 的事件直接回收、Unsubscribe 不再抛出；Subscribe / SetDefaultHandler 忽略注册，参数校验不变。
    /// 每帧高频事件改用 <see cref="GetChannel{T}"/> 的 struct 通道（无引用池往返、无装箱）。
    /// </summary>
    public interface IEventManager
    {
        /// <summary>
        /// 获取事件处理函数的数量。
        /// </summary>
        int EventHandlerCount { get; }

        /// <summary>
        /// 获取事件数量。
        /// </summary>
        int EventCount { get; }

        /// <summary>
        /// 获取事件处理函数的数量。
        /// </summary>
        /// <param name="id">事件类型编号。</param>
        /// <returns>事件处理函数的数量。</returns>
        int Count(int id);

        /// <summary>
        /// 检查是否存在事件处理函数。
        /// </summary>
        /// <param name="id">事件类型编号。</param>
        /// <param name="handler">要检查的事件处理函数。</param>
        /// <returns>是否存在事件处理函数。</returns>
        bool Check(int id, EventHandler<GameEventArgs> handler);

        /// <summary>
        /// 订阅事件处理函数。
        /// </summary>
        /// <param name="id">事件类型编号。</param>
        /// <param name="handler">要订阅的事件处理函数。</param>
        void Subscribe(int id, EventHandler<GameEventArgs> handler);

        /// <summary>
        /// 取消订阅事件处理函数。
        /// </summary>
        void Unsubscribe(int id, EventHandler<GameEventArgs> handler);

        /// <summary>
        /// 尝试取消订阅事件处理函数。不存在时返回 false 而非抛异常。
        /// 适合 GameObject.OnDestroy 路径调用，避免重复 Unsubscribe 崩溃。
        /// </summary>
        bool TryUnsubscribe(int id, EventHandler<GameEventArgs> handler);

        /// <summary>
        /// 设置默认事件处理函数。
        /// </summary>
        /// <param name="handler">要设置的默认事件处理函数。</param>
        void SetDefaultHandler(EventHandler<GameEventArgs> handler);

        /// <summary>
        /// 抛出事件，这个操作是线程安全的。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        void Fire(object sender, GameEventArgs e);

        /// <summary>
        /// 抛出事件立即模式：处理器在调用线程同步执行，只在主线程调用。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        void FireNow(object sender, GameEventArgs e);

        /// <summary>
        /// 获取 struct 事件通道：同一类型始终返回同一实例，由事件管理器每帧派发其排队事件、关停时关闭。
        /// 任意线程可调用；返回值应缓存。通道的线程契约见 <see cref="EventChannel{T}"/>。
        /// </summary>
        /// <typeparam name="T">事件结构体类型。</typeparam>
        EventChannel<T> GetChannel<T>() where T : struct;
    }
}
