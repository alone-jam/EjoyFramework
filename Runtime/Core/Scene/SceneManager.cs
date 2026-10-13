//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace EjoyFramework.Core.Scene
{
    /// <summary>
    /// 场景管理器。骨架实现：维护已加载/加载中/卸载中场景集合；实际加载由 Unity 层 SceneComponent 协程驱动并回调 NotifyXxx 接口。
    /// </summary>
    public sealed class SceneManager : FrameworkModule, ISceneManager
    {
        private readonly HashSet<string> m_LoadedScenes = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, LoadingInfo> m_LoadingScenes = new Dictionary<string, LoadingInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> m_UnloadingScenes = new Dictionary<string, object>(StringComparer.Ordinal);
        private ISceneHelper m_Helper;

        public event EventHandler<LoadSceneSuccessEventArgs> LoadSceneSuccess;
        public event EventHandler<LoadSceneFailureEventArgs> LoadSceneFailure;
        public event EventHandler<LoadSceneUpdateEventArgs> LoadSceneUpdate;
        public event EventHandler<UnloadSceneSuccessEventArgs> UnloadSceneSuccess;
        public event EventHandler<UnloadSceneFailureEventArgs> UnloadSceneFailure;

        // Priority 5：场景切换早于 Entity/UI/Sound 业务，但晚于 Resource。
        public override int Priority { get { return 5; } }

        // 必需配置自检：依赖 SceneHelper 驱动场景加载/卸载。
        public override bool RequiresConfiguration { get { return true; } }
        public override bool IsModuleConfigured { get { return m_Helper != null; } }
        public override string ConfigurationHint { get { return "Call SetHelper(...) before use."; } }

        public void SetHelper(ISceneHelper helper)
        {
            Framework.EnsureMainThread(nameof(SetHelper));
            m_Helper = helper;
        }

        public bool SceneIsLoaded(string n) { return n != null && m_LoadedScenes.Contains(n); }

        public string[] GetLoadedSceneAssetNames()
        {
            var arr = new string[m_LoadedScenes.Count];
            m_LoadedScenes.CopyTo(arr);
            return arr;
        }

        public bool SceneIsLoading(string n) { return n != null && m_LoadingScenes.ContainsKey(n); }

        public string[] GetLoadingSceneAssetNames()
        {
            var arr = new string[m_LoadingScenes.Count];
            int i = 0;
            foreach (var kv in m_LoadingScenes) arr[i++] = kv.Key;
            return arr;
        }

        public bool SceneIsUnloading(string n) { return n != null && m_UnloadingScenes.ContainsKey(n); }

        public string[] GetUnloadingSceneAssetNames()
        {
            var arr = new string[m_UnloadingScenes.Count];
            int i = 0;
            foreach (var kv in m_UnloadingScenes) arr[i++] = kv.Key;
            return arr;
        }

        public void LoadScene(string sceneAssetName, int priority, object userData)
        {
            Framework.EnsureMainThread(nameof(LoadScene));
            if (string.IsNullOrEmpty(sceneAssetName)) throw new FrameworkException("Scene asset name is invalid.");
            if (m_Helper == null) throw new FrameworkException("Scene helper is not set.");
            if (m_LoadedScenes.Contains(sceneAssetName)) throw new FrameworkException(Utility.Text.Format("Scene '{0}' already loaded.", sceneAssetName));
            if (m_LoadingScenes.ContainsKey(sceneAssetName)) throw new FrameworkException(Utility.Text.Format("Scene '{0}' is loading.", sceneAssetName));

            m_LoadingScenes.Add(sceneAssetName, new LoadingInfo { UserData = userData, StartTime = NowSeconds() });
            try
            {
                m_Helper.LoadAsync(sceneAssetName, priority, userData,
                    progress => RaiseUpdate(sceneAssetName, progress, userData),
                    ok => OnLoaded(sceneAssetName, userData),
                    (asset, err) => OnLoadFailed(sceneAssetName, err, userData));
            }
            catch (Exception ex)
            {
                if (m_LoadingScenes.ContainsKey(sceneAssetName)) OnLoadFailed(sceneAssetName, ex.Message, userData);
                else throw;
            }
        }

        public void UnloadScene(string sceneAssetName, object userData)
        {
            Framework.EnsureMainThread(nameof(UnloadScene));
            if (string.IsNullOrEmpty(sceneAssetName)) throw new FrameworkException("Scene asset name is invalid.");
            if (m_Helper == null) throw new FrameworkException("Scene helper is not set.");
            if (!m_LoadedScenes.Contains(sceneAssetName)) throw new FrameworkException(Utility.Text.Format("Scene '{0}' not loaded.", sceneAssetName));

            if (m_UnloadingScenes.ContainsKey(sceneAssetName)) throw new FrameworkException("Scene is already unloading.");
            m_UnloadingScenes.Add(sceneAssetName, userData);
            try
            {
                m_Helper.UnloadAsync(sceneAssetName, userData,
                    ok => OnUnloaded(sceneAssetName, userData),
                    (asset, err) => OnUnloadFailed(sceneAssetName, err, userData));
            }
            catch (Exception ex)
            {
                if (m_UnloadingScenes.ContainsKey(sceneAssetName)) OnUnloadFailed(sceneAssetName, ex.Message, userData);
                else throw;
            }
        }

        public override void Update(float a, float b) { }
        public override void Shutdown()
        {
            m_LoadedScenes.Clear();
            m_LoadingScenes.Clear();
            m_UnloadingScenes.Clear();
        }

        private void OnLoaded(string asset, object userData)
        {
            float duration = 0f;
            LoadingInfo info;
            if (m_LoadingScenes.TryGetValue(asset, out info))
            {
                duration = NowSeconds() - info.StartTime;
                m_LoadingScenes.Remove(asset);
            }
            m_LoadedScenes.Add(asset);

            var h = LoadSceneSuccess;
            if (h != null)
            {
                var args = LoadSceneSuccessEventArgs.Create(asset, duration, userData);
                try { h(this, args); }
                catch (Exception ex) { FrameworkLog.Error("LoadSceneSuccess handler threw: {0}", ex); }
                ReferencePool.Release(args);
            }
        }

        private void OnLoadFailed(string asset, string err, object userData)
        {
            m_LoadingScenes.Remove(asset);
            var h = LoadSceneFailure;
            if (h != null)
            {
                var args = LoadSceneFailureEventArgs.Create(asset, err, userData);
                try { h(this, args); }
                catch (Exception ex) { FrameworkLog.Error("LoadSceneFailure handler threw: {0}", ex); }
                ReferencePool.Release(args);
            }
        }

        private void RaiseUpdate(string asset, float progress, object userData)
        {
            var h = LoadSceneUpdate;
            if (h != null)
            {
                var args = LoadSceneUpdateEventArgs.Create(asset, progress, userData);
                try { h(this, args); }
                catch (Exception ex) { FrameworkLog.Error("LoadSceneUpdate handler threw: {0}", ex); }
                ReferencePool.Release(args);
            }
        }

        private void OnUnloaded(string asset, object userData)
        {
            m_UnloadingScenes.Remove(asset);
            m_LoadedScenes.Remove(asset);
            var h = UnloadSceneSuccess;
            if (h != null)
            {
                var args = UnloadSceneSuccessEventArgs.Create(asset, userData);
                try { h(this, args); }
                catch (Exception ex) { FrameworkLog.Error("UnloadSceneSuccess handler threw: {0}", ex); }
                ReferencePool.Release(args);
            }
        }

        private void OnUnloadFailed(string asset, string err, object userData)
        {
            m_UnloadingScenes.Remove(asset);
            var h = UnloadSceneFailure;
            if (h != null)
            {
                var args = UnloadSceneFailureEventArgs.Create(asset, userData, err);
                try { h(this, args); }
                catch (Exception ex) { FrameworkLog.Error("UnloadSceneFailure handler threw: {0}", ex); }
                ReferencePool.Release(args);
            }
        }

        private static float NowSeconds()
        {
            // 单调时钟，避免系统时钟跳变导致的负数/巨大 Duration，且子秒精度充足。
            return Utility.Timestamp.SecondsF;
        }

        private struct LoadingInfo
        {
            public object UserData;
            public float StartTime;
        }
    }
}
