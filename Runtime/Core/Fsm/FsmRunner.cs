using System;
namespace EjoyFramework.Core.Fsm
{
    /// <summary>
    /// Explicit owner-scoped driver for the existing FSM implementation. Useful for
    /// independent simulations and fixed clocks; never registered with the global manager.
    /// Dispose invokes leave/destroy callbacks and returns the FSM to its reference pool.
    /// </summary>
    public sealed class FsmRunner<T> : IDisposable where T : class
    {
        private Fsm<T> m_Fsm;
        private bool m_Updating;
        public IFsm<T> StateMachine => m_Fsm ?? throw new ObjectDisposedException(nameof(FsmRunner<T>));
        public FsmRunner(T owner, params FsmState<T>[] states) { m_Fsm=Fsm<T>.Create(string.Empty,owner,states); }
        public void Start<TState>() where TState:FsmState<T>
        {
            if(m_Fsm==null)throw new ObjectDisposedException(nameof(FsmRunner<T>));
            m_Fsm.Start<TState>();
        }
        public void Tick(float elapsed, float realElapsed)
        {
            if(m_Fsm==null)throw new ObjectDisposedException(nameof(FsmRunner<T>));
            if(m_Updating)throw new InvalidOperationException("FSM driver cannot be ticked recursively.");
            if(float.IsNaN(elapsed)||float.IsInfinity(elapsed)||elapsed<0||float.IsNaN(realElapsed)||float.IsInfinity(realElapsed)||realElapsed<0)
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            m_Updating=true;
            try { m_Fsm.Update(elapsed,realElapsed); } finally { m_Updating=false; }
        }
        public void Dispose()
        {
            if(m_Updating)throw new InvalidOperationException("Dispose the driver outside its update callback.");
            var fsm=m_Fsm;m_Fsm=null;fsm?.Shutdown();
        }
    }
}
