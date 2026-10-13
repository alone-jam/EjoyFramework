//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using EjoyFramework.Core.Resource;
using UnityEditor;
using UnityEngine;

namespace EjoyFramework.Core.Unity.Editor.Resource
{
    /// <summary>
    /// AssetBundle 打包入口。
    ///
    /// 流程：
    ///   1) 收集资产：扫 IncludeDirectories，按 ExcludePatterns/ExcludeExtensions 过滤
    ///   2) 用 BundleNameResolver 把每个资产映射到 bundle 名
    ///   3) 调 BuildPipeline.BuildAssetBundles 出包
    ///   4) 用 BuildPipeline 返回的 AssetBundleManifest + 自建索引产出 EjoyFramework.Core AssetManifest（JSON）
    ///   5) 可选拷贝到 StreamingAssets/AssetBundles/&lt;platform&gt;/
    ///
    /// 注意：此 builder 在执行期间使用 AssetImporter.assetBundleName 临时改动；
    /// 完成后会恢复（除非中途异常退出 — 那种情况调用方需要重新打一次或手动清理）。
    /// </summary>
    public static class AssetBundleBuilder
    {
        /// <summary>
        /// 用指定配置出包到指定平台。返回 Manifest 实例（也已写盘）。
        /// </summary>
        public static AssetManifest BuildAll(AssetBundleBuildConfig config, BuildTarget target)
        {
            if (config == null) throw new ArgumentNullException("config");

            string platform = target.ToString();
            string outputRoot = Path.IsPathRooted(config.OutputDirectory)
                ? config.OutputDirectory
                : Path.Combine(Directory.GetCurrentDirectory(), config.OutputDirectory);
            string outputDir = Path.Combine(outputRoot, platform);

            Debug.Log("[AssetBundleBuilder] Starting build → " + outputDir);

            if (config.CleanBeforeBuild && Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
            Directory.CreateDirectory(outputDir);

            // 1) 收集资产
            List<string> allAssets = CollectAssets(config);
            Debug.Log("[AssetBundleBuilder] Collected " + allAssets.Count + " assets");

            // 2) 解析 bundle 名（按规则）
            var resolver = new BundleNameResolver(config);
            var assetToBundle = new Dictionary<string, string>(StringComparer.Ordinal);
            var bundleToAssets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string assetPath in allAssets)
            {
                long size = TryGetFileSize(assetPath);
                string bundleName = resolver.Resolve(assetPath, size);
                if (string.IsNullOrEmpty(bundleName)) continue;
                assetToBundle[assetPath] = bundleName;
                List<string> list;
                if (!bundleToAssets.TryGetValue(bundleName, out list))
                {
                    list = new List<string>();
                    bundleToAssets[bundleName] = list;
                }
                list.Add(assetPath);
            }
            Debug.Log("[AssetBundleBuilder] Resolved into " + bundleToAssets.Count + " bundles");

            // 3) 用 AssetBundleBuild[] API（不依赖 AssetImporter.assetBundleName，避免污染工程）
            var builds = new AssetBundleBuild[bundleToAssets.Count];
            int bi = 0;
            foreach (var kv in bundleToAssets)
            {
                builds[bi++] = new AssetBundleBuild
                {
                    assetBundleName = kv.Key,
                    assetNames = kv.Value.ToArray(),
                };
            }

            BuildAssetBundleOptions options = ResolveBuildOptions(config, target);
            Debug.Log("[AssetBundleBuilder] Build options for " + platform + ": " + options);
            AssetBundleManifest unityManifest;
            try
            {
                unityManifest = BuildPipeline.BuildAssetBundles(outputDir, builds, options, target);
            }
            catch (Exception ex)
            {
                Debug.LogError("[AssetBundleBuilder] BuildPipeline.BuildAssetBundles threw: " + ex);
                throw;
            }
            if (unityManifest == null) throw new InvalidOperationException("BuildPipeline.BuildAssetBundles returned null manifest.");

            // 4) 产出 EjoyFramework.Core AssetManifest
            AssetManifest manifest = BuildEjoyManifest(config, platform, outputDir, unityManifest, assetToBundle, bundleToAssets);
            WriteManifest(manifest, outputDir);

            // 5) 拷贝到 StreamingAssets
            if (config.CopyToStreamingAssets)
            {
                CopyToStreamingAssets(outputDir, platform);
            }

            Debug.Log("[AssetBundleBuilder] Done. " + manifest.Bundles.Count + " bundles, " + manifest.Assets.Count + " assets.");
            return manifest;
        }

        // ===== 资产收集 =====

        private static List<string> CollectAssets(AssetBundleBuildConfig config)
        {
            var includes = (config.IncludeDirectories == null || config.IncludeDirectories.Count == 0)
                ? new[] { "Assets" }
                : ExpandRoots(config.IncludeDirectories);

            string[] guids = AssetDatabase.FindAssets(string.Empty, includes);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>(guids.Length);
            var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid).Replace('\\', '/');
                if (string.IsNullOrEmpty(path)) continue;
                if (!seen.Add(path)) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;
                if (ShouldExclude(path, config)) continue;
                string skipReason;
                if (!IsAssetBundleCandidate(path, out skipReason))
                {
                    CountSkip(skipped, skipReason);
                    continue;
                }
                result.Add(path);
            }
            if (skipped.Count > 0)
                Debug.Log("[AssetBundleBuilder] Skipped non-bundle assets: " + FormatSkipSummary(skipped));

            return result;
        }

        private static string[] ExpandRoots(List<string> dirs)
        {
            var arr = new string[dirs.Count];
            for (int i = 0; i < dirs.Count; i++)
            {
                string d = dirs[i].Replace('\\', '/').TrimEnd('/');
                arr[i] = d.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || d.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                    ? d
                    : ("Assets/" + d);
            }
            return arr;
        }

        private static bool IsAssetBundleCandidate(string assetPath, out string reason)
        {
            Type mainAssetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            if (mainAssetType == null)
            {
                reason = "missing main asset type";
                return false;
            }

            // SceneAsset is an editor representation of a runtime-loadable scene.
            if (IsEditorOnlyAssetType(mainAssetType))
            {
                reason = "editor-only asset type";
                return false;
            }

            UnityEngine.Object mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (mainAsset == null)
            {
                reason = "missing main asset";
                return false;
            }

            reason = null;
            return true;
        }

        private static bool IsEditorOnlyAssetType(Type type)
        {
            return type != typeof(SceneAsset) && type.Namespace != null &&
                type.Namespace.StartsWith("UnityEditor", StringComparison.Ordinal);
        }

        private static void CountSkip(Dictionary<string, int> skipped, string reason)
        {
            if (string.IsNullOrEmpty(reason)) reason = "unknown";

            int count;
            skipped.TryGetValue(reason, out count);
            skipped[reason] = count + 1;
        }

        private static string FormatSkipSummary(Dictionary<string, int> skipped)
        {
            var parts = new List<string>(skipped.Count);
            foreach (var kv in skipped)
            {
                parts.Add(kv.Key + "=" + kv.Value);
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(", ", parts.ToArray());
        }

        private static bool ShouldExclude(string assetPath, AssetBundleBuildConfig config)
        {
            string ext = Path.GetExtension(assetPath).ToLowerInvariant();
            if (config.ExcludeExtensions != null)
            {
                for (int i = 0; i < config.ExcludeExtensions.Count; i++)
                {
                    if (string.Equals(config.ExcludeExtensions[i], ext, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            if (config.ExcludePatterns != null && GlobMatcher.MatchAny(config.ExcludePatterns, assetPath)) return true;
            return false;
        }

        private static long TryGetFileSize(string assetPath)
        {
            try { return new FileInfo(assetPath).Length; } catch { return 0; }
        }

        // 计算文件字节的真实 MD5（十六进制小写）。失败返回空串（PatchManager 会跳过该 bundle 的 MD5 校验）。
        private static string ComputeFileMd5(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return string.Empty;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] hash = md5.ComputeHash(stream);
                    var sb = new System.Text.StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return string.Empty; }
        }

        // ===== Manifest 生成 =====

        private static AssetManifest BuildEjoyManifest(AssetBundleBuildConfig config, string platform, string outputDir,
            AssetBundleManifest unityManifest,
            Dictionary<string, string> assetToBundle,
            Dictionary<string, List<string>> bundleToAssets)
        {
            var manifest = new AssetManifest
            {
                Version = 1,
                AppVersion = config.AppVersion,
                Platform = platform,
            };

            string[] allBundles = unityManifest.GetAllAssetBundles();
            foreach (string bundleName in allBundles)
            {
                string bundlePath = Path.Combine(outputDir, bundleName);
                long size = File.Exists(bundlePath) ? new FileInfo(bundlePath).Length : 0;
                uint crc; BuildPipeline.GetCRCForAssetBundle(bundlePath, out crc);
                var info = new BundleInfo
                {
                    Name = bundleName,
                    RelativePath = bundleName,
                    Size = size,
                    Crc = crc,
                    Hash = unityManifest.GetAssetBundleHash(bundleName).ToString(),
                    Md5 = ComputeFileMd5(bundlePath),   // 真实文件 MD5，供热更下载完整性校验
                };
                string[] deps = unityManifest.GetDirectDependencies(bundleName);
                if (deps != null && deps.Length > 0) info.Dependencies.AddRange(deps);

                List<string> assetsInBundle;
                if (bundleToAssets.TryGetValue(bundleName, out assetsInBundle))
                {
                    foreach (string a in assetsInBundle) info.AssetNames.Add(a);
                }
                manifest.Bundles.Add(info);
            }

            foreach (var kv in assetToBundle)
            {
                string assetPath = kv.Key;
                var info = new AssetInfo
                {
                    Name = assetPath,
                    BundleName = kv.Value,
                    IsScene = assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase),
                };
                var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (mainAsset != null) info.TypeName = mainAsset.GetType().FullName;
                manifest.Assets.Add(info);
            }
            return manifest;
        }

        private static void WriteManifest(AssetManifest manifest, string outputDir)
        {
            string json = JsonUtility.ToJson(manifest, true);
            string path = Path.Combine(outputDir, "manifest.json");
            File.WriteAllText(path, json);
            Debug.Log("[AssetBundleBuilder] Manifest written: " + path);
        }

        private static void CopyToStreamingAssets(string outputDir, string platform)
        {
            string target = Path.Combine(Application.streamingAssetsPath, "AssetBundles", platform);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(outputDir, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                // 跳过 .manifest（Unity 自带的非运行时文件）
                if (name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(target, name), true);
            }
            AssetDatabase.Refresh();
            Debug.Log("[AssetBundleBuilder] Copied to StreamingAssets: " + target);
        }

        // ===== 构建选项（按平台分支） =====

        /// <summary>
        /// 把压缩模式 + 目标平台映射成 BuildAssetBundleOptions。
        ///
        /// 平台 → 压缩策略：
        ///   - 移动端 (Android / iOS)：强制 LZ4 chunk-based 压缩 (ChunkBasedCompression)。
        ///     chunk-based 支持流式按需解压，移动端从 persistentDataPath 加载时内存峰值更低、
        ///     首帧加载更平滑；LZMA 的整包解压会造成卡顿与内存尖峰，不适合移动端运行时。
        ///   - PC (Standalone / 其它)：沿用 config.Compression 配置。LZMA（最高压缩比）在 PC 上
        ///     可接受——磁盘 I/O 快、内存充裕，整包解压代价可控。
        ///
        /// 无论平台，统一附加：
        ///   - StrictMode：任意子资产打包失败即整体失败，避免静默产出残缺包。
        /// 说明：DeterministicAssetBundle 在 Unity 5.0+ 的新打包系统中始终启用，已废弃，无需显式指定。
        /// </summary>
        private static BuildAssetBundleOptions ResolveBuildOptions(AssetBundleBuildConfig config, BuildTarget target)
        {
            BuildAssetBundleOptions options;
            bool isMobile = target == BuildTarget.Android || target == BuildTarget.iOS;
            if (isMobile)
            {
                // 移动端固定 LZ4 chunk-based，便于流式加载；config 的 PC 压缩配置在此被显式覆盖。
                options = BuildAssetBundleOptions.ChunkBasedCompression;
            }
            else
            {
                // PC 平台采用配置指定的压缩模式（LZMA 可接受）。
                options = TranslateCompression(config.Compression);
            }

            // 跨平台统一开启严格模式（确定性产物在新打包系统中默认启用）。
            options |= BuildAssetBundleOptions.StrictMode;
            return options;
        }

        private static BuildAssetBundleOptions TranslateCompression(AssetBundleCompressionMode mode)
        {
            switch (mode)
            {
                case AssetBundleCompressionMode.Uncompressed: return BuildAssetBundleOptions.UncompressedAssetBundle;
                case AssetBundleCompressionMode.LZ4: return BuildAssetBundleOptions.ChunkBasedCompression;
                case AssetBundleCompressionMode.LZMA: return BuildAssetBundleOptions.None;
                default: return BuildAssetBundleOptions.ChunkBasedCompression;
            }
        }
    }
}
