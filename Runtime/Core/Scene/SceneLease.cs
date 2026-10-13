using System;
namespace EjoyFramework.Core.Scene
{
    public enum SceneLeaseState { Loading, Ready, Releasing, Released, Failed, ReleaseFailed }

    /// <summary>
    /// Owns one additive scene request. Disposing during a non-cancellable native load
    /// suppresses delivery and unloads immediately after success. Keep the manager/helper
    /// alive until Released/Failed; ReleaseFailed retains ownership and Dispose retries.
    /// UserData identity routes callbacks, so unrelated scene operations cannot settle it.
    /// </summary>
    public sealed class SceneLease : IDisposable
    {
        private readonly ISceneManager m_Manager;
        private bool m_DisposeRequested;
        public string AssetName { get; }
        public SceneLeaseState State { get; private set; }
        public string Error { get; private set; }
        public bool IsTerminal => State==SceneLeaseState.Released || State==SceneLeaseState.Failed;
        public SceneLease(ISceneManager manager,string assetName,int priority=0)
        {
            m_Manager=manager ?? throw new ArgumentNullException(nameof(manager));
            if(string.IsNullOrEmpty(assetName))throw new ArgumentException("Scene name required.",nameof(assetName));
            AssetName=assetName;State=SceneLeaseState.Loading;
            manager.LoadSceneSuccess+=Loaded;manager.LoadSceneFailure+=LoadFailed;
            manager.UnloadSceneSuccess+=Unloaded;manager.UnloadSceneFailure+=UnloadFailed;
            try{manager.LoadScene(assetName,priority,this);}
            catch(Exception ex){State=SceneLeaseState.Failed;Error=ex.Message;Detach();}
        }
        private void Loaded(object sender,LoadSceneSuccessEventArgs e)
        {
            if(!ReferenceEquals(e.UserData,this)||State!=SceneLeaseState.Loading)return;
            State=SceneLeaseState.Ready;if(m_DisposeRequested)Release();
        }
        private void LoadFailed(object sender,LoadSceneFailureEventArgs e)
        {
            if(!ReferenceEquals(e.UserData,this)||State!=SceneLeaseState.Loading)return;
            Error=e.ErrorMessage;State=SceneLeaseState.Failed;Detach();
        }
        private void Unloaded(object sender,UnloadSceneSuccessEventArgs e)
        {
            if(!ReferenceEquals(e.UserData,this)||State!=SceneLeaseState.Releasing)return;
            State=SceneLeaseState.Released;Detach();
        }
        private void UnloadFailed(object sender,UnloadSceneFailureEventArgs e)
        {
            if(!ReferenceEquals(e.UserData,this)||State!=SceneLeaseState.Releasing)return;
            Error=e.ErrorMessage;State=SceneLeaseState.ReleaseFailed;
        }
        public void Dispose()
        {
            m_DisposeRequested=true;
            if(State==SceneLeaseState.Ready || State==SceneLeaseState.ReleaseFailed)Release();
        }
        private void Release()
        {
            State=SceneLeaseState.Releasing;Error=null;
            try{m_Manager.UnloadScene(AssetName,this);}
            catch(Exception ex){Error=ex.Message;State=SceneLeaseState.ReleaseFailed;}
        }
        private void Detach()
        {
            m_Manager.LoadSceneSuccess-=Loaded;m_Manager.LoadSceneFailure-=LoadFailed;
            m_Manager.UnloadSceneSuccess-=Unloaded;m_Manager.UnloadSceneFailure-=UnloadFailed;
        }
    }
}
