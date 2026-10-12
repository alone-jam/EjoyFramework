//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.GamePlay.Netcode.Transport
{
    /// <summary>
    /// Optional bounded event-pump capability. Existing <see cref="INetTransport.Poll"/> callers
    /// retain the transport's ordinary parameterless polling behavior.
    /// </summary>
    /// <remarks>
    /// Budgets limit events consumed and payload bytes delivered by this pump, not callback CPU time,
    /// send/lifecycle work, queued memory, allocations, or transport-identity trust. They do not make
    /// a transport thread-safe; its existing owner-thread contract still applies to other operations.
    /// </remarks>
    public interface IBudgetedNetTransport : INetTransport
    {
        /// <summary>
        /// Processes queued events in FIFO order, consuming at most the smaller of
        /// <paramref name="maxEvents"/> and the queue count captured at entry.
        /// Events enqueued by callbacks on this instance wait for a later poll. If lifecycle work
        /// clears the captured queue, this poll stops; replacement events also wait for a later poll.
        /// </summary>
        /// <remarks>
        /// <para>Every consumed event costs one event unit, including connection/disconnection events,
        /// zero-byte data, and discarded oversized packets. A zero event budget does no queue work.</para>
        /// <para>Data delivered to <see cref="INetTransport.OnData"/> costs its payload length once per
        /// event, not once per subscriber. The sum cannot exceed <paramref name="maxDataBytes"/>.
        /// A packet that fits the total byte budget but not the remaining budget stays at the head;
        /// no later event may overtake it.</para>
        /// <para>With a positive total byte budget, an individual packet larger than that total is
        /// permanently discarded under one event charge, without OnData, a byte charge, or a synthetic
        /// disconnect event. This intentional loss applies even to reliable delivery methods and
        /// prevents an oversized head from stalling the queue indefinitely.</para>
        /// <para>With a zero byte budget, positive-length data instead pauses the pump at the head
        /// without discarding it. Only leading control and zero-byte events can be consumed.</para>
        /// <para>Both Poll overloads share a single-consumer guard. Valid overlapping or recursive calls
        /// on the same instance throw, including zero-budget calls. This explicitly rejects previously
        /// unsupported reentry; ordinary parameterless polling is unchanged. If a callback leaves this
        /// exception uncaught, it is subject to the transport's existing handler-exception isolation.</para>
        /// </remarks>
        /// <param name="maxEvents">Nonnegative maximum number of events to consume.</param>
        /// <param name="maxDataBytes">Nonnegative maximum total data bytes to deliver in this call.</param>
        /// <exception cref="ArgumentOutOfRangeException">Either budget is negative (validated first).</exception>
        /// <exception cref="InvalidOperationException">Another Poll on this instance is active.</exception>
        void Poll(int maxEvents, int maxDataBytes);
    }
}
