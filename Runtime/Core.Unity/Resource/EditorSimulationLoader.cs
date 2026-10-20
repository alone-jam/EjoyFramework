//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using EjoyFramework.Core.Coroutines;
using EjoyFramework.Core.Resource;
using UnityEditor;
using UnityEngine;

namespace EjoyFramework.Core.Unity.Resource
{
    /// <summary>
    /// 编辑器模拟 Loader：直接用 AssetDatabase 加载资产，跳过 AB 打包流程。
    /// 用于开发期快速迭代；运行时 build 必须用 AssetBundleLoader。
    ///
    /// 行为约定：
    ///   - LoadAssetAsync 同步完成；业务代码不应假设回调一定异步（与 AB Loader 一致：AB 命中缓存时也可同步回调）
    ///   - LoadSceneAsync/UnloadSceneAsync 只完成资源准备/释放；原生场景由 DefaultSceneHelper 独占挂载/卸载
    ///   - HasAsset：优先查 manifest，否则 fallback 到 AssetDatabase.AssetPathToGUID
    ///   - UnloadAsset：编辑期 AssetDatabase 加载的资产由 Editor 引用持有，无需手动卸载（noop）
    ///   - 引用计数：编辑器模式不做计数（与 AB Loader 行为不一致，仅用于功能性验证而非生命周期校验）
    /// </summary>
    public sealed class EditorSimulationLoader : IResourceLoader
    {
        private const string CoroutineTag = "EditorSimulationLoader";
        private readonly ICoroutineManager m_Coroutine;
        private readonly Dictionary<string, AssetInfo> m_Assets = new Dictionary<string, AssetInfo>(StringComparer.Ordinal);
        private bool m_Initialized;
        private int m_LoadingTaskCount;
        private int m_LoadedAssetCount;

        public EditorSimulationLoader(ICoroutineManager coroutine)
        {
            if (coroutine == null) throw new FrameworkException("Coroutine manager is required for EditorSimulationLoader.");
            m_Coroutine = coroutine;
        }

        public bool IsInitialized { get { return m_Initialized; } }
        public bool RequiresManifest { get { return false; } }
        public int LoadedBundleCount { get { return 0; } }
        public int LoadedAssetCount { get { return m_LoadedAssetCount; } }
        public int LoadingTaskCount { get { return m_LoadingTaskCount; } }

        public void Initialize(string readOnlyPath, string readWritePath, string currentVariant,
            AssetManifest manifest, Action onComplete, Action<string> onFailure)
        {
            try
            {
                m_Assets.Clear();
                if (manifest != null && manifest.Assets != null)
                {
                    foreach (var info in manifest.Assets)
                    {
                        if (info != null && !string.IsNullOrEmpty(info.Name)) m_Assets[info.Name] = info;
                    }
                    FrameworkLog.Info("EditorSimulationLoader: loaded {0} asset entries from manifest", m_Assets.Count);
                }
                else
                {
                    FrameworkLog.Info("EditorSimulationLoader: no manifest, will resolve assetName as AssetDatabase path on demand");
                }
                m_Initialized = true;
                if (onComplete != null) onComplete();
            }
            catch (Exception ex)
            {
                if (onFailure != null) onFailure(ex.Message);
            }
        }

        public bool HasAsset(string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return false;
            if (m_Assets.ContainsKey(assetName)) return true;
            // fallback: 直接当 AssetDatabase 路径查
            return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetName));
        }

        public void LoadAssetAsync(string assetName, Type assetType, int priority,
            LoadAssetCallbacks callbacks, object userData)
        {
            if (!m_Initialized)
            {
                Fail(callbacks, assetName, LoadResourceStatus.NotReady, "EditorSimulationLoader is not initialized.", userData);
                return;
            }
            if (string.IsNullOrEmpty(assetName))
            {
                Fail(callbacks, assetName, LoadResourceStatus.NotExist, "Asset name is invalid.", userData);
                return;
            }

            string assetPath = ResolveAssetPath(assetName);
            if (string.IsNullOrEmpty(assetPath))
            {
                Fail(callbacks, assetName, LoadResourceStatus.NotExist, "Asset not found: " + assetName, userData);
                return;
            }

            UnityEngine.Object asset;
            try
            {
                asset = assetType != null
                    ? AssetDatabase.LoadAssetAtPath(assetPath, assetType)
                    : AssetDatabase.LoadMainAssetAtPath(assetPath);
            }
            catch (Exception ex)
            {
                Fail(callbacks, assetName, LoadResourceStatus.AssetError, "AssetDatabase.LoadAssetAtPath threw: " + ex.Message, userData);
                return;
            }

            if (asset == null)
            {
                Fail(callbacks, assetName, LoadResourceStatus.AssetError, "AssetDatabase returned null for: " + assetPath, userData);
                return;
            }

            if (assetType != null && !assetType.IsInstanceOfType(asset))
            {
                Fail(callbacks, assetName, LoadResourceStatus.TypeError,
                    Utility.Text.Format("Asset '{0}' is {1}, expected {2}.", assetName, asset.GetType().FullName, assetType.FullName),
                    userData);
                return;
            }

            m_LoadedAssetCount++;
            var success = callbacks.LoadAssetSuccessCallback;
            if (success != null)
            {
                try { success(assetName, asset, 0f, userData); }
                catch (Exception ex) { FrameworkLog.Error("LoadAssetSuccessCallback threw for '{0}': {1}", assetName, ex); }
            }
        }

        public void LoadSceneAsync(string sceneAssetName, int priority,
            Action<float> onProgress, Action<string> onSuccess, Action<string, string> onFailure, object userData)
        {
            if (!m_Initialized)
            {
                if (onFailure != null) onFailure(sceneAssetName, "EditorSimulationLoader is not initialized.");
                return;
            }
            if (string.IsNullOrEmpty(sceneAssetName))
            {
                if (onFailure != null) onFailure(sceneAssetName, "Scene asset name is invalid.");
                return;
            }
            // Simulation has no scene bundles. Match AssetBundleLoader: only prepare
            // resources here; DefaultSceneHelper validates and mounts the scene once.
            onProgress?.Invoke(1f);
            onSuccess?.Invoke(sceneAssetName);
        }

        public void UnloadSceneAsync(string sceneAssetName,
            Action<string> onSuccess, Action<string, string> onFailure, object userData)
        {
            if (string.IsNullOrEmpty(sceneAssetName))
            {
                if (onFailure != null) onFailure(sceneAssetName, "Scene asset name is invalid.");
                return;
            }
            // DefaultSceneHelper already performed the native unload.
            onSuccess?.Invoke(sceneAssetName);
        }

        public void UnloadAsset(object asset)
        {
            // 编辑期 AssetDatabase 加载的资产由 Unity 自身管理，无需主动卸载
            if (asset != null) m_LoadedAssetCount = Math.Max(0, m_LoadedAssetCount - 1);
        }

        public void UnloadUnusedAssets(bool performGCCollect)
        {
            if (performGCCollect)
            {
                Resources.UnloadUnusedAssets();
                GC.Collect();
            }
        }

        public void Shutdown()
        {
            m_Assets.Clear();
            m_LoadedAssetCount = 0;
            m_LoadingTaskCount = 0;
            m_Initialized = false;
        }

        // ===== private =====

        private string ResolveAssetPath(string assetName)
        {
            // 优先 manifest 反查（支持业务 assetName 与磁盘路径解耦）
            AssetInfo info;
            if (m_Assets.TryGetValue(assetName, out info))
            {
                // EditorSimulation 直接用 assetName 当路径（manifest 中 Name 即业务键，磁盘路径就是 assetName 本身）
                if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetName))) return assetName;
            }
            // fallback：业务方直接传 AssetDatabase 路径
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetName))) return assetName;
            return null;
        }

        private static void Fail(LoadAssetCallbacks cb, string assetName, LoadResourceStatus status, string msg, object userData)
        {
            FrameworkLog.Warning("EditorSimulationLoader fail: asset='{0}' status={1} msg={2}", assetName, status, msg);
            var f = cb != null ? cb.LoadAssetFailureCallback : null;
            if (f != null)
            {
                try { f(assetName, status, msg, userData); }
                catch (Exception ex) { FrameworkLog.Error("LoadAssetFailureCallback threw for '{0}': {1}", assetName, ex); }
            }
        }

    }
}
#endif
