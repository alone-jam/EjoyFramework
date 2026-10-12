//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using EjoyFramework.GamePlay.Netcode.Messages;

namespace EjoyFramework.GamePlay.Netcode.Session
{
    /// <summary>
    /// Owner-thread bounded command inbox. A trusted owner supplies context after authentication,
    /// schema agreement and ownership validation; context is never learned from packets. Stores
    /// value copies in W preallocated tagged slots with no growth. Admission never runs simulation,
    /// writes a World, advances a clock or chooses missing-input fallback. No per-packet logging.
    /// Close/recreate with a NEW epoch tuple and coherent baseline on reconnect/reset/owner change.
    /// </summary>
    public sealed class TickCommandInbox<TCommand, TCodec>
        where TCommand : unmanaged
        where TCodec : struct, ITickCommandCodec<TCommand>
    {
        public const int MaximumWindowSize = 1024;
        private struct Slot
        {
            public uint Tick;
            public TCommand Command;
        }

        private readonly int m_OwnerThread = Environment.CurrentManagedThreadId;
        private readonly TickInputFrameContext m_Context;
        private readonly TCodec m_Codec;
        private readonly int m_MaxPayloadBits;
        private readonly Slot[] m_Slots;
        private uint m_LastSimulatedTick;
        private uint m_LastProcessedCommandTick;
        private int m_PendingCommandCount;
        private TickCommandSelection<TCommand> m_Selection;
        private bool m_InFlight;
        private bool m_Decoding;
        private bool m_Faulted;
        private bool m_Closed;

        // Read-only diagnostics remain available on the owner thread after fault/close.
        public TickInputFrameContext Context { get { RequireOwner(); return m_Context; } }
        public int WindowSize { get { RequireOwner(); return m_Slots.Length; } }
        public int PendingCommandCount { get { RequireOwner(); return m_PendingCommandCount; } }
        public uint LastSimulatedTick { get { RequireOwner(); return m_LastSimulatedTick; } }
        /// <summary>Highest actually consumed command tick; zero means none known here, not baseline acknowledgment.</summary>
        public uint LastProcessedCommandTick { get { RequireOwner(); return m_LastProcessedCommandTick; } }
        public bool IsTickInFlight { get { RequireOwner(); return m_InFlight; } }
        public bool IsFaulted { get { RequireOwner(); return m_Faulted; } }
        public bool IsClosed { get { RequireOwner(); return m_Closed; } }

        /// <summary>Baseline zero is valid genesis. Window and payload limits are checked before slot allocation.</summary>
        public TickCommandInbox(in TickInputFrameContext context, TCodec codec, uint lastSimulatedTick = 0,
            int windowSize = 32, int maxPayloadBits = TickInputFrameCodec.DefaultMaxPayloadBits)
        {
            if (context.SessionEpoch == 0 || context.WorldEpoch == 0 || context.CommandSchemaId == 0 || context.CodecRevision == 0)
                throw new ArgumentException("Expected context must have nonzero epochs, command schema and codec revision.", nameof(context));
            if (windowSize < 1 || windowSize > MaximumWindowSize) throw new ArgumentOutOfRangeException(nameof(windowSize));
            if (maxPayloadBits < 0 || maxPayloadBits > TickInputFrameCodec.MaximumPayloadBits)
                throw new ArgumentOutOfRangeException(nameof(maxPayloadBits));
            var identity = codec;
            if (identity.CommandSchemaId != context.CommandSchemaId || identity.CodecRevision != context.CodecRevision)
                throw new ArgumentException("Command codec identity must exactly match the trusted expected context.", nameof(codec));
            m_Context = context;
            m_Codec = codec;
            m_MaxPayloadBits = maxPayloadBits;
            m_LastSimulatedTick = lastSimulatedTick;
            m_Slots = new Slot[windowSize];
        }

        /// <summary>
        /// Admit only S &lt; tick &lt;= S+W, using wide arithmetic. Out-of-order future input is allowed;
        /// first VALID command wins. Duplicate is checked after frame/window validation but before
        /// schema decoding: it means already admitted, not byte-identical or revalidated payload.
        /// Codec runs on a value copy and local reader/command; false/extra bits/throws publish nothing.
        /// Codec exceptions propagate unchanged, do not advance ticks or fault simulation, and leave
        /// the slot free for a later valid attempt. Referenced codec state is NOT deep-copied or sandboxed.
        /// </summary>
        public TickCommandAdmissionResult TryAdmit(ReadOnlySpan<byte> frame)
        {
            RequireOwner();
            if (m_Closed) return Result(TickCommandAdmissionStatus.Closed);
            if (m_Faulted) return Result(TickCommandAdmissionStatus.Faulted);
            if (m_InFlight) return Result(TickCommandAdmissionStatus.TickInFlight);
            if (m_Decoding) return Result(TickCommandAdmissionStatus.DecodeInProgress);
            if (!TickInputFrameCodec.TryDecode(frame, in m_Context, out var header, out var payload,
                out var error, m_MaxPayloadBits))
                return new TickCommandAdmissionResult(TickCommandAdmissionStatus.FrameRejected, error);
            uint tick = header.CommandTick;
            if (tick <= m_LastSimulatedTick) return Result(TickCommandAdmissionStatus.StaleTick);
            if ((ulong)tick > (ulong)m_LastSimulatedTick + (uint)m_Slots.Length)
                return Result(TickCommandAdmissionStatus.OutsideWindow);
            int index = Index(tick);
            if (m_Slots[index].Tick == tick) return Result(TickCommandAdmissionStatus.Duplicate);
            if (m_Slots[index].Tick != 0) return Result(TickCommandAdmissionStatus.SlotOccupied);
            var reader = new NetBitReader(payload, header.PayloadBitLength);
            var codec = m_Codec;
            m_Decoding = true;
            try
            {
                if (!codec.TryDecode(ref reader, out TCommand command)) return Result(TickCommandAdmissionStatus.CommandRejected);
                if (reader.BitPosition != header.PayloadBitLength || reader.RemainingBits != 0)
                    return Result(TickCommandAdmissionStatus.PayloadNotFullyConsumed);
                m_Slots[index] = new Slot { Tick = tick, Command = command };
                m_PendingCommandCount++;
                return Result(TickCommandAdmissionStatus.Accepted);
            }
            finally { m_Decoding = false; }
        }

        /// <summary>
        /// Seals a copied decision for exactly S+1. Missing is a valid selection, not a repeated or
        /// zero-input policy. Nothing is cleared/acknowledged yet; admission is rejected until this
        /// flight completes or fails. No receive path can call this automatically.
        /// </summary>
        public TickCommandSelection<TCommand> BeginTick(uint tick)
        {
            RequireLifecycle();
            if (m_InFlight) throw new InvalidOperationException("A tick is already in flight.");
            if (m_LastSimulatedTick == uint.MaxValue)
                throw new OverflowException("Tick sequence exhausted. Close/recreate on a new epoch tuple before wrap.");
            if (tick != m_LastSimulatedTick + 1) throw new ArgumentOutOfRangeException(nameof(tick), "Begin exactly S+1.");
            var slot = m_Slots[Index(tick)];
            if (slot.Tick != 0 && slot.Tick != tick)
                throw new InvalidOperationException("A different command tick occupies the selected slot.");
            m_Selection = new TickCommandSelection<TCommand>(tick, slot.Tick == tick, slot.Command);
            m_InFlight = true;
            return m_Selection;
        }

        /// <summary>
        /// Call ONLY after successful simulation of the exact selection. Clears its slot and advances
        /// S once. Processed-command tick changes only for actual admitted input; it is not packet ack
        /// and does not prove commands existed at every earlier tick. Does not perform simulation.
        /// </summary>
        public void CompleteTick(uint tick)
        {
            RequireFlight(tick);
            m_Slots[Index(tick)] = default;
            if (m_Selection.HasCommand)
            {
                m_PendingCommandCount--;
                m_LastProcessedCommandTick = tick;
            }
            m_LastSimulatedTick = tick;
            m_InFlight = false;
            m_Selection = default;
        }

        /// <summary>
        /// Terminates the exact flight in a faulted state without advancing or claiming consumption.
        /// Queued values stay retained until Close for diagnostics; execution cannot retry or abort
        /// back into readiness. The caller must reconcile any partial simulation writes externally.
        /// </summary>
        public void FailTick(uint tick)
        {
            RequireFlight(tick);
            m_Faulted = true;
            m_InFlight = false;
            m_Selection = default;
        }

        /// <summary>
        /// Owner-thread terminal/idempotent close, including during a flight. Clears queued values
        /// and the selection but does not undo simulation or advance counters. Late callbacks on
        /// this object return Closed; a replacement requires a new epoch tuple chosen by the owner.
        /// </summary>
        public void Close()
        {
            RequireOwner();
            if (m_Decoding) throw new InvalidOperationException("Cannot close during codec execution.");
            if (m_Closed) return;
            Array.Clear(m_Slots, 0, m_Slots.Length);
            m_PendingCommandCount = 0;
            m_InFlight = false;
            m_Selection = default;
            m_Closed = true;
        }

        private int Index(uint tick) => (int)(tick % (uint)m_Slots.Length);
        private static TickCommandAdmissionResult Result(TickCommandAdmissionStatus status)
            => new TickCommandAdmissionResult(status);
        private void RequireFlight(uint tick)
        {
            RequireLifecycle();
            if (!m_InFlight) throw new InvalidOperationException("There is no tick in flight.");
            if (tick != m_Selection.Tick) throw new ArgumentOutOfRangeException(nameof(tick), "Use the exact selected tick.");
        }
        private void RequireLifecycle()
        {
            RequireOwner();
            if (m_Decoding) throw new InvalidOperationException("Cannot change tick lifecycle during codec execution.");
            if (m_Closed) throw new InvalidOperationException("The command inbox is closed.");
            if (m_Faulted) throw new InvalidOperationException("Execution failed. Close/recreate from a coherent baseline.");
        }
        private void RequireOwner()
        {
            if (Environment.CurrentManagedThreadId != m_OwnerThread)
                throw new InvalidOperationException("Use the command inbox only on its constructing thread.");
        }
    }
}
