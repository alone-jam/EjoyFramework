//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections;
using System.IO;
using EjoyFramework.Core.Coroutines;
using EjoyFramework.Core.Resource;
using EjoyFramework.Core.Unity.Resource;
using UnityEngine;
using UnityEngine.Networking;

namespace EjoyFramework.Core.Unity
{
    /// <summary>
    /// 资源组件。完整实现：
    ///   - Inspector 暴露 ResourceMode + 并发上限 + bundle 卸载延迟
    ///   - Awake 阶段注入路径、Mode、根据 Mode 创建对应 Loader
    ///   - Start 阶段加载 manifest（AssetBundle 模式必需；EditorSimulation 模式可缺省）
    ///   - 提供 InitializeAsync 公开 API 给 ProcedureLaunch 调用
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("EjoyFramework/Core/Resource")]
    public sealed class ResourceComponent : GameFrameworkComponent
    {
        private const string UnsupportedResourcesModeError =
            "ResourceMode.Resources is not implemented. Select EditorSimulation in the Editor or "
            + "AssetBundle with generated bundles and a manifest. No fallback loader will be selected.";

        [SerializeField]
        private ResourceMode m_ResourceMode = ResourceMode.EditorSimulation;

        [SerializeField]
        [Tooltip("AssetBundleLoader 同时加载 bundle 的并发上限")]
        private int m_MaxConcurrentBundleLoads = 4;

        [SerializeField]
        [Tooltip("bundle 引用归零后延迟卸载秒数（避免抖动重载）")]
        private float m_BundleUnloadDelay = 5f;

        [SerializeField]
        [Tooltip("常驻 bundle 内存预算（MB）；0 = 不启用（引用归零后按延迟卸载）。启用后引用归零的 bundle 进入温缓存，超预算按 LRU 淘汰。")]
        private int m_MemoryBudgetMB = 0;

        [SerializeField]
        private string m_CurrentVariant = string.Empty;

        [SerializeField]
        [Tooltip("Awake 后是否自动 InitializeAsync（关闭则由 Procedure 决定时机）")]
        private bool m_AutoInitializeOnStart = false;

        private IResourceManager m_ResourceManager;
        private IResourceLoader m_Loader;
        private bool m_InitTriggered;
        private string m_PackageBundleRoot;
        private string m_ReadOnlyBundleRoot;
        private string m_ReadWriteBundleRoot;

        public ResourceMode CurrentMode { get { return m_ResourceMode; } }
        public bool IsInitialized { get { return m_ResourceManager != null && m_ResourceManager.IsInitialized; } }
        public int LoadedBundleCount { get { return m_ResourceManager != null ? m_ResourceManager.LoadedBundleCount : 0; } }
        public int LoadedAssetCount { get { return m_ResourceManager != null ? m_ResourceManager.LoadedAssetCount : 0; } }
        public int LoadingTaskCount { get { return m_ResourceManager != null ? m_ResourceManager.LoadingTaskCount : 0; } }
        public int CachedAssetCount { get { return m_ResourceManager != null ? m_ResourceManager.CachedAssetCount : 0; } }

        protected override void Awake()
        {
            // Reject before registration or changing the shared resource manager's configuration.
            EnsureSupportedMode(m_ResourceMode);
            base.Awake();
            m_ResourceManager = Framework.GetModule<IResourceManager>();
            if (m_ResourceManager == null)
            {
                Log.Fatal("Resource manager is invalid.");
                return;
            }

            // Runtime bundle roots (统一规则，全平台一致)：
            //   - 只读首包资源 → Application.streamingAssetsPath/AssetBundles/<platform>
            //     （PC 安装目录可能在 Program Files，受 UAC 保护，不可写；StreamingAssets 始终只读但可读）。
            //   - 读写 / 热更资源 → Application.persistentDataPath/AssetBundles/<platform>
            //     （所有平台均可写，避免 PC player 在 Application.dataPath 下写入失败）。
            //   - 移动端首包资源同样以 StreamingAssets 为安装源，在初始化时展开到 persistentDataPath。
            // EditorSimulationLoader 忽略这些路径 —— Editor 行为不变。
            string platform = GetPlatformDirName();
            m_PackageBundleRoot = CombinePathOrUri(Application.streamingAssetsPath, "AssetBundles/" + platform);
            m_ReadOnlyBundleRoot = Path.Combine(Application.streamingAssetsPath, "AssetBundles", platform);
            m_ReadWriteBundleRoot = Path.Combine(Application.persistentDataPath, "AssetBundles", platform);
            m_ResourceManager.SetReadOnlyPath(m_ReadOnlyBundleRoot);
            m_ResourceManager.SetReadWritePath(m_ReadWriteBundleRoot);
            if (!string.IsNullOrEmpty(m_CurrentVariant)) m_ResourceManager.SetCurrentVariant(m_CurrentVariant);
            m_ResourceManager.SetMode(m_ResourceMode);

            m_Loader = CreateLoader(m_ResourceMode);
            if (m_Loader != null) m_ResourceManager.SetLoader(m_Loader);
            var bundleLoader = m_Loader as AssetBundleLoader;
            if (bundleLoader != null && m_MemoryBudgetMB > 0) bundleLoader.MemoryBudgetBytes = (long)m_MemoryBudgetMB * 1024L * 1024L;
        }

        private void Start()
        {
            if (m_AutoInitializeOnStart) InitializeAsync(null, null);
        }

        /// <summary>
        /// 异步初始化：读取 manifest（AB 模式必需）→ 让 Loader 完成自检。
        /// 调用方应在 ProcedureLaunch 第一帧调用并等待。
        /// </summary>
        public void InitializeAsync(Action onComplete, Action<string> onFailure)
        {
            if (m_ResourceMode == ResourceMode.Resources)
            {
                if (onFailure != null) onFailure(UnsupportedResourcesModeError);
                else Log.Error(UnsupportedResourcesModeError);
                return;
            }
            if (m_ResourceManager == null) { if (onFailure != null) onFailure("ResourceManager not ready"); return; }
            if (m_InitTriggered) { if (onFailure != null) onFailure("InitializeAsync already triggered"); return; }
            m_InitTriggered = true;

            StartCoroutine(InitializeRoutine(onComplete, onFailure));
        }

        public void SetCurrentVariant(string currentVariant)
        {
            m_ResourceManager.SetCurrentVariant(currentVariant);
        }

        public void LoadAsset(string assetName, LoadAssetCallbacks loadAssetCallbacks)
        {
            m_ResourceManager.LoadAsset(assetName, loadAssetCallbacks);
        }

        public void LoadAsset(string assetName, int priority, LoadAssetCallbacks loadAssetCallbacks)
        {
            m_ResourceManager.LoadAsset(assetName, priority, loadAssetCallbacks);
        }

        public void LoadAsset(string assetName, Type assetType, LoadAssetCallbacks loadAssetCallbacks)
        {
            m_ResourceManager.LoadAsset(assetName, assetType, loadAssetCallbacks);
        }

        public void LoadAsset(string assetName, Type assetType, int priority, LoadAssetCallbacks loadAssetCallbacks)
        {
            m_ResourceManager.LoadAsset(assetName, assetType, priority, loadAssetCallbacks);
        }

        public void UnloadAsset(object asset)
        {
            m_ResourceManager.UnloadAsset(asset);
        }

        public bool HasAsset(string assetName)
        {
            return m_ResourceManager.HasAsset(assetName);
        }

        /// <summary>常驻 bundle 内存预算（字节）；0 = 不启用。仅 AssetBundle 模式有效。</summary>
        public long MemoryBudgetBytes
        {
            get { var l = m_Loader as AssetBundleLoader; return l != null ? l.MemoryBudgetBytes : 0L; }
            set { var l = m_Loader as AssetBundleLoader; if (l != null) l.MemoryBudgetBytes = value; m_MemoryBudgetMB = (int)(value / (1024L * 1024L)); }
        }

        /// <summary>常驻 bundle 总字节（manifest 记录的 Size 之和）。</summary>
        public long ResidentBytes
        {
            get { var l = m_Loader as AssetBundleLoader; return l != null ? l.ResidentBytes : 0L; }
        }

        /// <summary>主动把常驻收缩到 targetBytes（关卡切换 / 内存告警时机）。返回卸载的 bundle 数；非预算模式返回 0。</summary>
        public int TrimResident(long targetBytes)
        {
            var l = m_Loader as AssetBundleLoader;
            return l != null ? l.TrimResident(targetBytes) : 0;
        }

        public void ForceUnloadUnusedAssets(bool performGCCollect)
        {
            // Loader.UnloadUnusedAssets 在 performGCCollect 时已执行 Resources.UnloadUnusedAssets() + GC.Collect()，
            // 这里不再重复触发，避免一次调用做两遍 GC（卡顿翻倍）。
            m_ResourceManager.UnloadUnusedAssets(performGCCollect);
        }

        // ===== 批量预载 + 同步获取 =====

        /// <summary>
        /// 从引用池租用一个资源批次。用法：CreateAssetBatch().Add(a).Add(b).Start()。
        /// 批次用完必须 Release()，否则预载资产不会被卸载。
        /// </summary>
        public AssetBatch CreateAssetBatch()
        {
            return m_ResourceManager.CreateAssetBatch();
        }

        /// <summary>
        /// 同步取已预载（被某个 AssetBatch Pin 住）的资产。未预载或类型不符返回 false。
        /// </summary>
        public bool TryGetCachedAsset<T>(string assetName, out T asset) where T : class
        {
            return m_ResourceManager.TryGetCachedAsset(assetName, out asset);
        }

        /// <summary>
        /// 同步取已预载资产。未预载或类型不符抛 FrameworkException。
        /// </summary>
        public T GetCachedAsset<T>(string assetName) where T : class
        {
            return m_ResourceManager.GetCachedAsset<T>(assetName);
        }

        // ===== private =====

        private IResourceLoader CreateLoader(ResourceMode mode)
        {
            EnsureSupportedMode(mode);
            // AssetBundleLoader 通过 ICoroutineManager 驱动协程（解耦 MonoBehaviour）；
            // CoroutineComponent.Start 注入 Helper，业务调 LoadAsset 时机晚于 Start，时序安全。
            var coroutine = Framework.GetModule<ICoroutineManager>();
            switch (mode)
            {
#if UNITY_EDITOR
                case ResourceMode.EditorSimulation:
                    return new EditorSimulationLoader(coroutine);
#endif
                case ResourceMode.AssetBundle:
                    return new AssetBundleLoader(coroutine, m_MaxConcurrentBundleLoads, m_BundleUnloadDelay);
                default:
                    // Player build with EditorSimulation serialized lands here (the EditorSimulation
                    // case is stripped by #if UNITY_EDITOR). Ship a production-safe loader instead
                    // of a fatal error — the build settings doc covers this.
                    return new AssetBundleLoader(coroutine, m_MaxConcurrentBundleLoads, m_BundleUnloadDelay);
            }
        }

        private static void EnsureSupportedMode(ResourceMode mode)
        {
            if (mode == ResourceMode.Resources) throw new FrameworkException(UnsupportedResourcesModeError);
        }

        private IEnumerator InitializeRoutine(Action onComplete, Action<string> onFailure)
        {
            AssetManifest manifest = null;
            string error = null;

            // Precondition: production loaders drive their async bundle/asset loads through
            // ICoroutineManager. If the scene's framework GameObject is missing CoroutineComponent
            // (a frequent regression when scenes are authored before the component split shipped),
            // the very first LoadAsset would crash deep in AssetBundleLoader with a sparse error.
            // Detect it here at init-time and fail with an actionable message instead.
            if (m_Loader != null && m_Loader.RequiresManifest)
            {
                var coroutine = Framework.GetModule<ICoroutineManager>();
                if (coroutine == null || !coroutine.HasHelper)
                {
                    if (onFailure != null)
                    {
                        onFailure("CoroutineComponent is missing from the scene's framework GameObject. " +
                                  "AssetBundleLoader requires it to drive async loads. " +
                                  "Add the 'EjoyFramework/Core/Coroutine' component beside the other framework Components " +
                                  "(or instantiate EjoyFramework.Core.prefab, which already includes it).");
                    }
                    yield break;
                }
            }

            // 由 Loader 自己声明是否需要 manifest —— Component 不再二次判断 ResourceMode。
            // 即使 Loader 不强求 manifest，磁盘上存在就读，方便业务在 Editor 验证 manifest 内容。
            bool manifestRequired = m_Loader != null && m_Loader.RequiresManifest;

            if (manifestRequired && ShouldUsePersistentBundleRoot())
            {
                yield return PrepareReadWriteBundleCacheRoutine(err => error = err);
                if (error != null)
                {
                    if (onFailure != null) onFailure(error);
                    yield break;
                }
            }

            // Manifest lives at the root of the bundle layout (next to the .ab files).
            // Reuse the same base paths set in Awake so the convention is encoded in exactly one place.
            string rwPath = Path.Combine(m_ReadWriteBundleRoot, "manifest.json");
            string roPath = Path.Combine(m_ReadOnlyBundleRoot, "manifest.json");
            string chosen = File.Exists(rwPath) ? rwPath : roPath;

            if (manifestRequired || File.Exists(chosen))
            {
                yield return LoadManifestRoutine(chosen, json =>
                {
                    if (string.IsNullOrEmpty(json))
                    {
                        if (manifestRequired)
                        {
                            error = "AssetBundle manifest missing at '" + chosen + "'. " +
                                    "Run EjoyFramework.Core > Build > Build AssetBundles (or Build All) before starting a player build.";
                        }
                        return;
                    }
                    try { manifest = JsonUtility.FromJson<AssetManifest>(json); }
                    catch (Exception ex) { error = "Manifest parse failed: " + ex.Message; return; }

                    // Detect "manifest file exists but is empty / unserializable" — would otherwise
                    // present as 'Asset not in manifest' errors deep in load callbacks with no
                    // actionable hint. Required-mode loaders refuse to start with an empty manifest.
                    if (manifestRequired && (manifest == null || manifest.Bundles == null || manifest.Bundles.Count == 0))
                    {
                        error = "AssetBundle manifest at '" + chosen + "' is EMPTY (0 bundles). " +
                                "The shipped build either skipped the AB step or wrote an unserializable manifest. " +
                                "Run EjoyFramework.Core > Build > Build AssetBundles (or Build All), then rebuild the player.";
                    }
                });
            }

            if (error != null)
            {
                if (onFailure != null) onFailure(error);
                yield break;
            }

            bool initDone = false;
            string initError = null;
            m_ResourceManager.InitializeAsync(manifest,
                () => { initDone = true; },
                err => { initError = err; initDone = true; });

            while (!initDone) yield return null;

            if (initError != null) { if (onFailure != null) onFailure(initError); yield break; }
            if (onComplete != null) onComplete();
        }

        private IEnumerator LoadManifestRoutine(string path, Action<string> onLoaded)
        {
            // 本地路径用 Uri.AbsoluteUri 正确 URL-encode 空格/非 ASCII（Windows 下 "file://"+path 会损坏含空格路径）。
            string url = IsUriLikePath(path) ? path : new Uri(path).AbsoluteUri;

            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
#if UNITY_2020_2_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isHttpError && !req.isNetworkError;
#endif
                if (!ok)
                {
                    Log.Warning("Manifest load failed: " + url + " err=" + req.error);
                    onLoaded(null);
                    yield break;
                }
                onLoaded(req.downloadHandler.text);
            }
        }

        private IEnumerator PrepareReadWriteBundleCacheRoutine(Action<string> onFailure)
        {
            string sourceJson = null;
            string sourceManifestPath = CombinePathOrUri(m_PackageBundleRoot, "manifest.json");
            yield return LoadManifestRoutine(sourceManifestPath, json => sourceJson = json);
            AssetManifest cachedManifest = TryLoadManifestFromFile(Path.Combine(m_ReadWriteBundleRoot, "manifest.json"));
            if (string.IsNullOrEmpty(sourceJson))
            {
                if (cachedManifest != null && IsReadWriteCacheComplete(cachedManifest))
                {
                    Log.Warning("Mobile package manifest missing; using existing persistent AssetBundle cache.");
                    yield break;
                }
                if (onFailure != null)
                    onFailure("Mobile AssetBundle cache bootstrap failed: source manifest missing at '" + sourceManifestPath + "'.");
                yield break;
            }

            AssetManifest sourceManifest;
            try { sourceManifest = JsonUtility.FromJson<AssetManifest>(sourceJson); }
            catch (Exception ex)
            {
                if (onFailure != null) onFailure("Mobile AssetBundle cache bootstrap manifest parse failed: " + ex.Message);
                yield break;
            }

            if (sourceManifest == null || sourceManifest.Bundles == null || sourceManifest.Bundles.Count == 0)
            {
                if (onFailure != null) onFailure("Mobile AssetBundle cache bootstrap source manifest is empty.");
                yield break;
            }

            if (cachedManifest != null &&
                ManifestContentEquals(cachedManifest, sourceManifest) &&
                IsReadWriteCacheComplete(cachedManifest))
            {
                yield break;
            }

            for (int i = 0; i < sourceManifest.Bundles.Count; i++)
            {
                BundleInfo bundle = sourceManifest.Bundles[i];
                if (bundle == null || string.IsNullOrEmpty(bundle.Name)) continue;

                string rel = string.IsNullOrEmpty(bundle.RelativePath) ? bundle.Name : bundle.RelativePath;

                // C1 路径遍历防护：source manifest 的相对路径不可信，校验后再 Combine 到读写根目录，
                // 拒绝 ../、绝对路径、盘符等逃逸；命中即让缓存 bootstrap 失败，绝不把文件写到根目录之外。
                if (!Utility.Path.IsSafeRelativePath(rel))
                {
                    string msg = "Mobile AssetBundle cache bootstrap: unsafe relative path '" + rel + "' for bundle '" + bundle.Name + "'.";
                    Log.Error(msg);
                    if (onFailure != null) onFailure(msg);
                    yield break;
                }

                string dst = Path.Combine(m_ReadWriteBundleRoot, rel);
                if (!Utility.Path.IsContainedIn(m_ReadWriteBundleRoot, dst))
                {
                    string msg = "Mobile AssetBundle cache bootstrap: path '" + dst + "' escapes ReadWrite root for bundle '" + bundle.Name + "'.";
                    Log.Error(msg);
                    if (onFailure != null) onFailure(msg);
                    yield break;
                }

                if (IsCachedBundleValid(dst, bundle.Size)) continue;

                string src = CombinePathOrUri(m_PackageBundleRoot, rel);
                string copyError = null;
                yield return CopyReadOnlyFileToReadWriteRoutine(src, dst, err => copyError = err);
                if (copyError != null)
                {
                    if (onFailure != null) onFailure(copyError);
                    yield break;
                }
            }

            try
            {
                WriteAllTextAtomic(Path.Combine(m_ReadWriteBundleRoot, "manifest.json"), sourceJson);
                Log.Info("Mobile AssetBundle cache ready: " + m_ReadWriteBundleRoot);
            }
            catch (Exception ex)
            {
                if (onFailure != null) onFailure("Mobile AssetBundle cache manifest write failed: " + ex.Message);
            }
        }

        private IEnumerator CopyReadOnlyFileToReadWriteRoutine(string src, string dst, Action<string> onFailure)
        {
            string dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = dst + ".tmp";

            if (IsUriLikePath(src))
            {
                TryDeleteFile(temp);
                using (UnityWebRequest req = new UnityWebRequest(src, UnityWebRequest.kHttpVerbGET))
                {
                    req.downloadHandler = new DownloadHandlerFile(temp);
                    yield return req.SendWebRequest();
#if UNITY_2020_2_OR_NEWER
                    bool ok = req.result == UnityWebRequest.Result.Success;
#else
                    bool ok = !req.isHttpError && !req.isNetworkError;
#endif
                    if (!ok)
                    {
                        TryDeleteFile(temp);
                        if (onFailure != null) onFailure("Mobile AssetBundle cache copy failed: " + src + " err=" + req.error);
                        yield break;
                    }
                }
            }
            else
            {
                try { File.Copy(src, temp, true); }
                catch (Exception ex)
                {
                    TryDeleteFile(temp);
                    if (onFailure != null) onFailure("Mobile AssetBundle cache copy failed: " + src + " err=" + ex.Message);
                    yield break;
                }
            }

            try { ReplaceFile(temp, dst); }
            catch (Exception ex)
            {
                TryDeleteFile(temp);
                if (onFailure != null) onFailure("Mobile AssetBundle cache finalize failed: " + dst + " err=" + ex.Message);
            }
        }

        private bool ShouldUsePersistentBundleRoot()
        {
            return Application.platform == RuntimePlatform.Android ||
                   Application.platform == RuntimePlatform.IPhonePlayer;
        }

        private bool IsReadWriteCacheComplete(AssetManifest manifest)
        {
            if (manifest == null || manifest.Bundles == null || manifest.Bundles.Count == 0) return false;
            for (int i = 0; i < manifest.Bundles.Count; i++)
            {
                BundleInfo bundle = manifest.Bundles[i];
                if (bundle == null || string.IsNullOrEmpty(bundle.Name)) continue;
                string rel = string.IsNullOrEmpty(bundle.RelativePath) ? bundle.Name : bundle.RelativePath;

                // C1 路径遍历防护：相对路径不可信。命中可疑路径 / 越界时，无法把该条目当作“已缓存的有效 bundle”，
                // 保守判定为缓存不完整（return false），并打印错误，绝不基于根目录之外的文件做完整性判断。
                if (!Utility.Path.IsSafeRelativePath(rel))
                {
                    Log.Error("ReadWrite cache check: unsafe relative path '" + rel + "' for bundle '" + bundle.Name + "'; treating cache as incomplete.");
                    return false;
                }
                string cachedPath = Path.Combine(m_ReadWriteBundleRoot, rel);
                if (!Utility.Path.IsContainedIn(m_ReadWriteBundleRoot, cachedPath))
                {
                    Log.Error("ReadWrite cache check: path '" + cachedPath + "' escapes ReadWrite root for bundle '" + bundle.Name + "'; treating cache as incomplete.");
                    return false;
                }
                if (!IsCachedBundleValid(cachedPath, bundle.Size)) return false;
            }
            return true;
        }

        private static bool ManifestContentEquals(AssetManifest left, AssetManifest right)
        {
            if (left == null || right == null) return false;
            if (left.Version != right.Version) return false;
            if (!string.Equals(left.AppVersion, right.AppVersion, StringComparison.Ordinal)) return false;
            if (!string.Equals(left.Platform, right.Platform, StringComparison.Ordinal)) return false;
            if (left.Bundles == null || right.Bundles == null || left.Bundles.Count != right.Bundles.Count) return false;

            for (int i = 0; i < left.Bundles.Count; i++)
            {
                BundleInfo a = left.Bundles[i];
                BundleInfo b = right.Bundles[i];
                if (a == null || b == null) return false;
                if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)) return false;
                if (!string.Equals(a.RelativePath, b.RelativePath, StringComparison.Ordinal)) return false;
                if (!string.Equals(a.Hash, b.Hash, StringComparison.Ordinal)) return false;
                if (a.Size != b.Size) return false;
                if (a.Crc != b.Crc) return false;
            }
            return true;
        }

        private static AssetManifest TryLoadManifestFromFile(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                AssetManifest manifest = JsonUtility.FromJson<AssetManifest>(json);
                if (manifest == null || manifest.Bundles == null || manifest.Bundles.Count == 0) return null;
                return manifest;
            }
            catch { return null; }
        }

        private static bool IsCachedBundleValid(string path, long expectedSize)
        {
            if (!File.Exists(path)) return false;
            if (expectedSize <= 0) return true;
            try { return new FileInfo(path).Length == expectedSize; }
            catch { return false; }
        }

        private static string CombinePathOrUri(string root, string relativePath)
        {
            if (string.IsNullOrEmpty(root)) return relativePath;
            string rel = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
            if (IsUriLikePath(root)) return root.Replace('\\', '/').TrimEnd('/') + "/" + rel;
            return Path.Combine(root, relativePath ?? string.Empty);
        }

        private static bool IsUriLikePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.IndexOf("://", StringComparison.Ordinal) >= 0 ||
                   path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteAllTextAtomic(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            ReplaceFile(temp, path);
        }

        private static void ReplaceFile(string temp, string dst)
        {
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(temp, dst);
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (System.Exception ex) { FrameworkLog.Warning("TryDeleteFile '{0}' threw: {1}", path, ex); }
        }

        private static string GetPlatformDirName()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString();
#else
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer: return "StandaloneWindows64";
                case RuntimePlatform.OSXPlayer: return "StandaloneOSX";
                case RuntimePlatform.LinuxPlayer: return "StandaloneLinux64";
                case RuntimePlatform.Android: return "Android";
                case RuntimePlatform.IPhonePlayer: return "iOS";
                default: return Application.platform.ToString();
            }
#endif
        }
    }
}
