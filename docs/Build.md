# Build Pipeline

End-to-end game build for projects using **EjoyFramework**. This document is the canonical reference; any project using the framework (ChentangGuard, future projects, …) follows the same flow.

## Why a framework-level pipeline

EjoyFramework's runtime asset system uses one of two backends:

| Mode | Where it works | Requires |
|---|---|---|
| `EditorSimulation` (default) | Editor only | Nothing extra — reads via AssetDatabase |
| `AssetBundle` | Editor & player builds | **`manifest.json` + bundle files** in `StreamingAssets/AssetBundles/<platform>/` |

When you make a player build, `EditorSimulation` automatically falls back to `AssetBundle`. The runtime then fails immediately if the manifest is missing — that's the symptom:

> `AssetBundle manifest missing at 'Assets/StreamingAssets/AssetBundles/StandaloneWindows64/manifest.json'. Run EjoyFramework > Build > Build AssetBundles (or Build All) before starting a player build.`

The framework now provides a complete pipeline (`FrameworkBuildPipeline`) so every project using EjoyFramework gets a working build flow without writing its own build script.

## One-time setup

1. Run **`EjoyFramework > Build > Open Build Settings`** — this creates `Assets/Editor/EjoyBuildSettings.asset` on first run.
2. In the Inspector for that asset, set:
   - **AssetBundleConfig** — drag in an `AssetBundleBuildConfig` (Create → EjoyFramework → AssetBundle Build Config). The default config compresses with LZ4 and copies the output to StreamingAssets.
   - **PlayerOutputDirectory** — defaults to `Build/`; subfolders per platform are added automatically.
   - **PlayerOutputName** — supports `${productName}` and `${platform}` placeholders.
   - **Scenes** — leave empty to fall back to File → Build Settings's enabled scenes; fill in explicit paths to override.

Commit the `.asset` so teammates and CI share the same configuration.

## Building

| Menu | What it does |
|---|---|
| `EjoyFramework/Build/Build AssetBundles` | AB only for the current active build target. Manifest lands in `Assets/StreamingAssets/AssetBundles/<platform>/`. |
| `EjoyFramework/Build/Build Player` | Player only. Errors out clearly if AB hasn't been built first (no silent fallback to a broken state). |
| `EjoyFramework/Build/Build All` | AB → Player chained. The typical "ship it" button. |
| `EjoyFramework/Build/Clean StreamingAssets Bundles` | Wipes the StreamingAssets bundle directory. Useful before switching platforms or to validate a clean build. |
| `EjoyFramework/Build/Open Build Settings` | Selects the singleton config in the Inspector. |

## CI / batch mode

`FrameworkBuildPipeline.BuildAllCi()` is the entry point for command-line builds:

```bash
Unity \
  -batchmode -quit -nographics \
  -projectPath "$(pwd)" \
  -buildTarget StandaloneWindows64 \
  -executeMethod EjoyFramework.Core.Unity.Editor.Build.FrameworkBuildPipeline.BuildAllCi
```

It runs AB + Player, exits with non-zero on any failure, and logs result + output path on success.

For finer control, call the typed API directly from your own build script:

```csharp
var settings = EjoyBuildSettings.GetOrCreate();
FrameworkBuildPipeline.BuildAssetBundles(settings, BuildTarget.StandaloneWindows64);
BuildReport report = FrameworkBuildPipeline.BuildPlayer(settings, BuildTarget.StandaloneWindows64);
```

Or override the player output path for a one-off (e.g. CI artifact upload location):

```csharp
FrameworkBuildPipeline.BuildAll(BuildTarget.StandaloneWindows64, "artifacts/win-x64/MyGame.exe");
```

## Runtime expectations

After a successful build, `Assets/StreamingAssets/AssetBundles/<platform>/` contains:

```
manifest.json                  ← runtime entry point (always loaded first)
<bundleName>.bundle            ← one file per AssetBundle
<bundleName>.bundle.manifest   ← Unity's per-bundle manifest (informational)
```

At startup, `ResourceComponent.InitializeAsync` looks for the manifest in this order:

1. `persistentDataPath/AssetBundles/<platform>/manifest.json` — for hot updates (PatchManager writes here)
2. `streamingAssetsPath/AssetBundles/<platform>/manifest.json` — shipped baseline

If neither exists when the effective mode is `AssetBundle`, the framework fails fast with the message shown at the top of this document.

## Choosing the right ResourceMode

| Mode | When to use |
|---|---|
| `EditorSimulation` | Inspector default. AssetDatabase-backed loads for fast iteration. Cannot be used in player builds (auto-falls-back to `AssetBundle`). |
| `AssetBundle` | Set this when you want to test the actual shipping data path inside the Editor (after running Build AssetBundles). Forces the runtime to read from StreamingAssets exactly as the player will. |
| `Resources` | Reserved and unsupported. `ResourceComponent` rejects it during Awake and reports failure through `InitializeAsync`; it never selects an AssetBundle fallback. Use one of the supported modes above. |

Set the mode on the **Resource** sub-component of `EjoyFramework.prefab`.

## Common pitfalls

**"AssetBundle manifest missing" on player launch.**
You skipped the AB build. Either use `Build All`, or run `Build AssetBundles` before `Build Player`.

**Bundles built but player still fails to load.**
Check that `AssetBundleBuildConfig.CopyToStreamingAssets = true` (the default). Without this the bundles stay in the project's `AssetBundles/` folder and don't enter the player.

**Switched platforms and player crashes loading bundles.**
Per-platform bundles live in `StreamingAssets/AssetBundles/<platform>/`. Run `Clean StreamingAssets Bundles` then `Build All` after the switch.

**CI build hangs.**
Forgot `-quit`. Or the player build is opening a dialog (e.g. license prompt) — use `-batchmode -nographics`.

**`EjoyBuildSettings.AssetBundleConfig is not assigned`.**
Create one via `Create > EjoyFramework > AssetBundle Build Config`, drag it into the `AssetBundleConfig` field of `EjoyBuildSettings`, commit both assets.

## What got fixed (v1)

- `ResourceComponent` previously checked `m_ResourceMode` (the Inspector field) when deciding whether the manifest was required. In a player build with `EditorSimulation` selected, the runtime fell back to `AssetBundleLoader` but the manifest-loading code still thought we were in EditorSimulation → passed `null` to the loader → fatal error with no actionable message.

  Now `ResourceComponent` tracks the *effective* mode (post-fallback) and requires the manifest accordingly. The error message points to the exact menu item to run.

- The framework now ships `FrameworkBuildPipeline` so every business project gets the same battle-tested build flow without writing one from scratch.
