//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.Core.Scene
{
    /// <summary>
    /// 场景管理器接口。
    /// </summary>
    public interface ISceneManager
    {
        /// <summary>
        /// 加载场景成功事件。
        /// </summary>
        event EventHandler<LoadSceneSuccessEventArgs> LoadSceneSuccess;

        /// <summary>
        /// 加载场景失败事件。
        /// </summary>
        event EventHandler<LoadSceneFailureEventArgs> LoadSceneFailure;

        /// <summary>
        /// 加载场景更新事件。
        /// </summary>
        event EventHandler<LoadSceneUpdateEventArgs> LoadSceneUpdate;

        /// <summary>
        /// 卸载场景成功事件。
        /// </summary>
        event EventHandler<UnloadSceneSuccessEventArgs> UnloadSceneSuccess;

        /// <summary>
        /// 卸载场景失败事件。
        /// </summary>
        event EventHandler<UnloadSceneFailureEventArgs> UnloadSceneFailure;

        /// <summary>
        /// 获取场景是否已加载。
        /// </summary>
        bool SceneIsLoaded(string sceneAssetName);

        /// <summary>
        /// 获取已加载场景的资源名称。
        /// </summary>
        string[] GetLoadedSceneAssetNames();

        /// <summary>
        /// 获取场景是否正在加载。
        /// </summary>
        bool SceneIsLoading(string sceneAssetName);

        /// <summary>
        /// 获取正在加载场景的资源名称。
        /// </summary>
        string[] GetLoadingSceneAssetNames();

        /// <summary>
        /// 获取场景是否正在卸载。
        /// </summary>
        bool SceneIsUnloading(string sceneAssetName);

        /// <summary>
        /// 获取正在卸载场景的资源名称。
        /// </summary>
        string[] GetUnloadingSceneAssetNames();

        /// <summary>
        /// 加载场景。
        /// </summary>
        void LoadScene(string sceneAssetName, int priority, object userData);

        /// <summary>
        /// 卸载场景。
        /// </summary>
        void UnloadScene(string sceneAssetName, object userData);

        /// <summary>
        /// 设置场景 helper（注入 Unity 实现）。
        /// </summary>
        void SetHelper(ISceneHelper sceneHelper);
    }

    /// <summary>
    /// 加载场景成功事件。
    /// </summary>
    public sealed class LoadSceneSuccessEventArgs : FrameworkEventArgs
    {
        public string SceneAssetName { get; private set; }
        public float Duration { get; private set; }
        public object UserData { get; private set; }

        public override void Clear()
        {
            SceneAssetName = null;
            Duration = 0f;
            UserData = null;
        }

        public static LoadSceneSuccessEventArgs Create(string sceneAssetName, float duration, object userData)
        {
            LoadSceneSuccessEventArgs e = ReferencePool.Acquire<LoadSceneSuccessEventArgs>();
            e.SceneAssetName = sceneAssetName;
            e.Duration = duration;
            e.UserData = userData;
            return e;
        }
    }

    /// <summary>
    /// 加载场景失败事件。
    /// </summary>
    public sealed class LoadSceneFailureEventArgs : FrameworkEventArgs
    {
        public string SceneAssetName { get; private set; }
        public string ErrorMessage { get; private set; }
        public object UserData { get; private set; }

        public override void Clear()
        {
            SceneAssetName = null;
            ErrorMessage = null;
            UserData = null;
        }

        public static LoadSceneFailureEventArgs Create(string sceneAssetName, string errorMessage, object userData)
        {
            LoadSceneFailureEventArgs e = ReferencePool.Acquire<LoadSceneFailureEventArgs>();
            e.SceneAssetName = sceneAssetName;
            e.ErrorMessage = errorMessage;
            e.UserData = userData;
            return e;
        }
    }

    /// <summary>
    /// 加载场景更新事件。
    /// </summary>
    public sealed class LoadSceneUpdateEventArgs : FrameworkEventArgs
    {
        public string SceneAssetName { get; private set; }
        public float Progress { get; private set; }
        public object UserData { get; private set; }

        public override void Clear()
        {
            SceneAssetName = null;
            Progress = 0f;
            UserData = null;
        }

        public static LoadSceneUpdateEventArgs Create(string sceneAssetName, float progress, object userData)
        {
            LoadSceneUpdateEventArgs e = ReferencePool.Acquire<LoadSceneUpdateEventArgs>();
            e.SceneAssetName = sceneAssetName;
            e.Progress = progress;
            e.UserData = userData;
            return e;
        }
    }

    /// <summary>
    /// 卸载场景成功事件。
    /// </summary>
    public sealed class UnloadSceneSuccessEventArgs : FrameworkEventArgs
    {
        public string SceneAssetName { get; private set; }
        public object UserData { get; private set; }

        public override void Clear()
        {
            SceneAssetName = null;
            UserData = null;
        }

        public static UnloadSceneSuccessEventArgs Create(string sceneAssetName, object userData)
        {
            UnloadSceneSuccessEventArgs e = ReferencePool.Acquire<UnloadSceneSuccessEventArgs>();
            e.SceneAssetName = sceneAssetName;
            e.UserData = userData;
            return e;
        }
    }

    /// <summary>
    /// 卸载场景失败事件。
    /// </summary>
    public sealed class UnloadSceneFailureEventArgs : FrameworkEventArgs
    {
        public string SceneAssetName { get; private set; }
        public object UserData { get; private set; }
        public string ErrorMessage { get; private set; }

        public override void Clear()
        {
            SceneAssetName = null;
            ErrorMessage = null;
            UserData = null;
        }

        public static UnloadSceneFailureEventArgs Create(string sceneAssetName, object userData, string errorMessage = null)
        {
            UnloadSceneFailureEventArgs e = ReferencePool.Acquire<UnloadSceneFailureEventArgs>();
            e.SceneAssetName = sceneAssetName;
            e.UserData = userData;
            e.ErrorMessage = errorMessage;
            return e;
        }
    }
}
