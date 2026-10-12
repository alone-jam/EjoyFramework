//------------------------------------------------------------
// EjoyGame Framework — GamePlay
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

namespace EjoyFramework.GamePlay.Netcode.Session
{
    public enum TickCommandAdmissionStatus : byte
    {
        /// <summary>Default result is not acceptance, including when an attempted decode threw.</summary>
        NotEvaluated,
        Accepted,
        Closed,
        Faulted,
        TickInFlight,
        DecodeInProgress,
        FrameRejected,
        StaleTick,
        OutsideWindow,
        /// <summary>A valid command was already admitted at this tick; not a byte-equality claim.</summary>
        Duplicate,
        CommandRejected,
        PayloadNotFullyConsumed,
        /// <summary>A different tagged slot is occupied; never overwrite it, even if an invariant was violated.</summary>
        SlotOccupied
    }

    /// <summary>Packet admission is not a packet acknowledgment or simulation completion.</summary>
    public readonly struct TickCommandAdmissionResult
    {
        public TickCommandAdmissionStatus Status { get; }
        /// <summary>Only FrameRejected carries a frame-codec error; other outcomes use None.</summary>
        public TickInputFrameError FrameError { get; }

        internal TickCommandAdmissionResult(TickCommandAdmissionStatus status,
            TickInputFrameError frameError = TickInputFrameError.None)
        { Status = status; FrameError = frameError; }
    }
}
