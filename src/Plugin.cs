using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Pathea;
using Pathea.FrameworkNs;
using Pathea.Porting.Utilities;
using Pathea.ScenarioNs;
using Pathea.UISystemV2.UI;
using Pathea.UISystemV2.UIControl;
using UnityEngine;
using UnityExtensions;
using UThreadPriority = UnityEngine.ThreadPriority;

namespace SandrockFastDoors
{
    public enum HideMode
    {
        Never,
        CachedScenesOnly,
        Always,
        Sky // same as Always (kept for configs from v1.1); the cover drawn is set by TransitionCover
    }

    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        /// <summary>Drop settings written by older versions so the .cfg doesn't keep stale keys.</summary>
        private void RemoveOrphans(string section, params string[] keys)
        {
            try
            {
                var prop = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries");
                var dict = prop?.GetValue(Config, null) as IDictionary<ConfigDefinition, string>;
                if (dict == null) return;
                bool changed = false;
                foreach (var k in keys)
                {
                    var def = new ConfigDefinition(section, k);
                    if (dict.Remove(def))
                    {
                        changed = true;
                        Logger.LogInfo($"Removed obsolete setting [{section}] {k} (replaced in this version)");
                    }
                }
                if (changed) Config.Save();
            }
            catch (Exception e) { Logger.LogWarning("Could not clean old settings: " + e.Message); }
        }

        public const string Guid = "combovine.sandrock.fastdoors";
        public const string Name = "Sandrock Fast Doors";
        public const string Version = "1.3.0";

        internal static ManualLogSource Log;

        // [Doors]
        internal static ConfigEntry<bool> SkipDoorAnimation;
        // [LoadingScreen]
        internal static ConfigEntry<bool> FastLoadingScreen;
        internal static ConfigEntry<float> MinDisplaySeconds;
        internal static ConfigEntry<float> CloseDelaySeconds;
        internal static ConfigEntry<float> FadeTimeScale;
        internal static ConfigEntry<HideMode> HideLoadingScreen;
        internal static ConfigEntry<CoverStyle> TransitionCoverStyle;
        internal static ConfigEntry<float> MinCoverSeconds;
        internal static ConfigEntry<string> CoversFolder;
        internal static ConfigEntry<int> ConfigVersion;
        internal static ConfigEntry<float> RevealDelaySeconds;
        internal static ConfigEntry<int> RevealDelayFrames;
        internal static ConfigEntry<float> RevealFadeSeconds;
        internal static ConfigEntry<float> SkyCoverTimeoutSeconds;
        // [Loading]
        internal static ConfigEntry<bool> HighLoadingPriority;
        internal static ConfigEntry<int> AsyncUploadTimeSliceMs;
        internal static ConfigEntry<bool> SkipUnloadUnusedAssets;
        internal static ConfigEntry<int> FullCleanupEveryNTransitions;
        // [Cache]
        internal static ConfigEntry<bool> KeepInteriorsLoaded;
        internal static ConfigEntry<int> MaxCachedInteriors;
        internal static ConfigEntry<string> ExcludedScenes;
        // [Preload]
        internal static ConfigEntry<bool> PreloadOnSaveLoad;
        internal static ConfigEntry<string> PreloadScenes;
        // [Debug]
        internal static ConfigEntry<bool> VerboseLogging;

        private void Awake()
        {
            Log = Logger;

            SkipDoorAnimation = Config.Bind("Doors", "SkipDoorAnimation", true,
                "Skip the walk-to-door / turn / open-door animation (up to ~3.5 s) that plays before the scene switch starts. Purely cosmetic.");

            FastLoadingScreen = Config.Bind("LoadingScreen", "FastLoadingScreen", true,
                "Remove the artificial delays of the loading screen: 1 s minimum display time, 0.5 s close delay, slow progress-bar animation and fade in/out.");
            MinDisplaySeconds = Config.Bind("LoadingScreen", "MinDisplaySeconds", 0f,
                "Minimum time the loading screen stays visible when FastLoadingScreen is on (game default: 1.0).");
            CloseDelaySeconds = Config.Bind("LoadingScreen", "CloseDelaySeconds", 0f,
                "Delay before the loading screen starts closing after loading finished, when FastLoadingScreen is on (game default: 0.5).");
            FadeTimeScale = Config.Bind("LoadingScreen", "FadeTimeScale", 0f,
                "Multiplier for the loading screen fade in/out durations when FastLoadingScreen is on. 0 = instant cut, 1 = vanilla.");
            HideLoadingScreen = Config.Bind("LoadingScreen", "HideLoadingScreen", HideMode.Always,
                "Replace the loading screen with a full-screen cover (see TransitionCover, default: a random picture). Never = normal (fast) loading screen. CachedScenesOnly = only when the target scene is already in memory. Always / Sky = for every door transition. Never applied when loading a save.");
            TransitionCoverStyle = Config.Bind("LoadingScreen", "TransitionCover", CoverStyle.Image,
                "What is shown instead of the loading screen when it is hidden. Image = random picture from CoversFolder (default; falls back to Black if none). Black = fully opaque black screen, nothing visible. Skybox = the game's sky. Gradient = sky-colored gradient. None = nothing (world visibly loads in). (Replaces the old HiddenScreenCover setting.)");
            CoversFolder = Config.Bind("LoadingScreen", "CoversFolder", "SandrockFastDoors/covers",
                "Folder with the *.png / *.jpg pictures for TransitionCover = Image. Relative paths are relative to BepInEx/plugins. A random one is shown per transition (never the same twice in a row), scaled to fill the screen.");
            MinCoverSeconds = Config.Bind("LoadingScreen", "MinCoverSeconds", 1.0f,
                "Minimum time the cover stays fully opaque.");
            RevealDelaySeconds = Config.Bind("LoadingScreen", "RevealDelaySeconds", 0.1f,
                "Extra time the cover stays after the scene is loaded and the player is placed at the destination.");
            RevealDelayFrames = Config.Bind("LoadingScreen", "RevealDelayFrames", 3,
                "Minimum number of frames the cover stays after the scene reports fully loaded.");
            RevealFadeSeconds = Config.Bind("LoadingScreen", "RevealFadeSeconds", 0.2f,
                "Duration of the fade-out when the cover is removed. 0 = instant.");
            SkyCoverTimeoutSeconds = Config.Bind("LoadingScreen", "SkyCoverTimeoutSeconds", 15f,
                "Safety: the cover is always removed after this many seconds, even if the game never reports the end of loading.");

            HighLoadingPriority = Config.Bind("Loading", "HighLoadingPriority", true,
                "Set Application.backgroundLoadingPriority = High during scene transitions (the game leaves it at Unity's default BelowNormal, which only integrates ~4 ms of loaded data per frame). Restored afterwards.");
            AsyncUploadTimeSliceMs = Config.Bind("Loading", "AsyncUploadTimeSliceMs", 8,
                "QualitySettings.asyncUploadTimeSlice (ms per frame for uploading textures/meshes to the GPU) during scene transitions. Unity default is 2. 0 = don't touch.");
            SkipUnloadUnusedAssets = Config.Bind("Loading", "SkipUnloadUnusedAssets", true,
                "Skip the GC.Collect + Resources.UnloadUnusedAssets passes the game runs twice on every scene switch (a full scan of all loaded objects, often the slowest part). A full cleanup is still done when loading a save, on season change, when entering dungeons, when cached scenes are evicted, and every FullCleanupEveryNTransitions switches.");
            FullCleanupEveryNTransitions = Config.Bind("Loading", "FullCleanupEveryNTransitions", 10,
                "When SkipUnloadUnusedAssets is on: still do the full cleanup every N transitions to keep memory in check. 0 = never (only on save load / season change / dungeons).");

            KeepInteriorsLoaded = Config.Bind("Cache", "KeepInteriorsLoaded", true,
                "Keep interiors (houses, shops, ...) loaded but hidden after you leave them, exactly like the game already does for the town (Main) and your home. Re-entering a cached interior takes only a few frames instead of loading from disk. The game itself drops this cache when you load a save, enter a dungeon or the season changes.");
            MaxCachedInteriors = Config.Bind("Cache", "MaxCachedInteriors", 16,
                "Maximum number of interiors kept in memory by this mod (least recently used ones are unloaded during a transition). Lower it if RAM usage gets too high.");
            ExcludedScenes = Config.Bind("Cache", "ExcludedScenes",
                "Start,CustomPlayer,SkillEditor,VoxelTest,Conference_Storyboard,Univese_Storyboard,SandCar,GraceMissionCave,DesertOutback,WildCave,BuriedRoom,Traveler_1,Traveler_2,Traveler_3",
                "Comma-separated AdditiveScene names that are never cached/preloaded (story-only, minigame or large outdoor scenes). Dungeons are always excluded automatically (the game flushes all cached scenes when entering them).");

            PreloadOnSaveLoad = Config.Bind("Preload", "PreloadOnSaveLoad", false,
                "EXPERIMENTAL. While the loading screen is up after loading a save, also load the interiors listed in PreloadScenes (hidden), so even the FIRST visit is instant. Makes save loading longer and uses a lot more RAM (each interior stays in memory). Requires KeepInteriorsLoaded. Limited by MaxCachedInteriors.");
            PreloadScenes = Config.Bind("Preload", "PreloadScenes",
                "Store,Saloon,Clinic,Guildhall,CivilCorp,Museum,Temple,Smithy,GameCenter,Ranch,Science,School",
                "Comma-separated AdditiveScene names to preload when PreloadOnSaveLoad is on, in priority order. Use 'All' for every cacheable interior (very RAM hungry).");

            VerboseLogging = Config.Bind("Debug", "VerboseLogging", false, "Log more details to BepInEx/LogOutput.log.");

            RemoveOrphans("LoadingScreen", "HiddenScreenCover", "FadeOutSeconds");
            ConfigVersion = Config.Bind("Internal", "ConfigVersion", 0, "Used by the mod to migrate settings between versions. Don't edit.");
            if (ConfigVersion.Value < 3)
            {
                // v1.3: the picture cover replaces the plain black one that v1.2 wrote as default
                if (TransitionCoverStyle.Value == CoverStyle.Black)
                {
                    TransitionCoverStyle.Value = CoverStyle.Image;
                    Logger.LogInfo("Migrated TransitionCover: Black -> Image");
                }
                ConfigVersion.Value = 3;
                Config.Save();
            }
            try
            {
                string folder = CoversFolder.Value ?? "";
                if (!Path.IsPathRooted(folder)) folder = Path.Combine(Paths.PluginPath, folder);
                TransitionCover.LoadCovers(Path.GetFullPath(folder));
            }
            catch (Exception e) { Logger.LogError("Cover folder error: " + e.Message); }
            Log.LogInfo($"{Name} {Version} loading (game version {Application.version}, Unity {Application.unityVersion})");
            Patches.Apply(new Harmony(Guid));
        }
    }

    internal static class Patches
    {
        private static void PlayerEndSceneLoadPostfix()
        {
            try
            {
                var sm = Module<ScenarioModule>.Self;
                TransitionCover.NotifyPlayerPlaced(sm != null ? sm.GetSafePosRot() : null);
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static int applied, failed;

        public static void Apply(Harmony h)
        {
            bool hooksOk;
            try
            {
                hooksOk = Hooks.SelfCheck();
            }
            catch (Exception e)
            {
                hooksOk = false;
                Plugin.Log.LogError("Reflection self-check failed (game updated?): " + e);
            }
            if (!hooksOk)
                Plugin.Log.LogError("Scene-cache / UnloadUnusedAssets / loading-priority features DISABLED because game internals did not match.");
            else
            {
            Try("ScenarioModule.LoadCoroutine (transpiler)", () =>
            {
                var mn = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ScenarioModule), "LoadCoroutine"));
                h.Patch(mn, transpiler: new HarmonyMethod(typeof(Patches), nameof(LoadCoroutineTranspiler)));
            });
            Try("ScenarioModule.SmartUnloadUnusedAssets (transpiler)", () =>
            {
                var mn = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ScenarioModule), "SmartUnloadUnusedAssets"));
                h.Patch(mn, transpiler: new HarmonyMethod(typeof(Patches), nameof(SmartUnloadTranspiler)));
            });
            }
            Try("Portal.GoToScenario (transpiler)", () =>
            {
                var mn = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(Portal), "GoToScenario"));
                h.Patch(mn, transpiler: new HarmonyMethod(typeof(Patches), nameof(PortalTranspiler)));
            });
            Try("LoadingMaskControl.Wait (transpiler)", () =>
            {
                var mn = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(LoadingMaskControl), "Wait"));
                h.Patch(mn, transpiler: new HarmonyMethod(typeof(Patches), nameof(MaskWaitTranspiler)));
            });
            Try("LoadingMaskControl.BeforeLoadingMask/AfterLoadingMask (prefix)", () =>
            {
                h.Patch(AccessTools.Method(typeof(LoadingMaskControl), "BeforeLoadingMask"),
                    prefix: new HarmonyMethod(typeof(Patches), nameof(BeforeLoadingMaskPrefix)));
                h.Patch(AccessTools.Method(typeof(LoadingMaskControl), "AfterLoadingMask"),
                    prefix: new HarmonyMethod(typeof(Patches), nameof(AfterLoadingMaskPrefix)));
            });
            Try("LoadingMaskUI.SetDisplay (prefix) / Update (postfix)", () =>
            {
                h.Patch(AccessTools.Method(typeof(LoadingMaskUI), nameof(LoadingMaskUI.SetDisplay),
                        new[] { typeof(string), typeof(string), typeof(Sprite), typeof(string), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(Patches), nameof(SetDisplayPrefix)));
                h.Patch(AccessTools.Method(typeof(LoadingMaskUI), "Update"),
                    postfix: new HarmonyMethod(typeof(Patches), nameof(MaskUpdatePostfix)));
            });
            Try("Player.OnEndSceneLoad (postfix)", () =>
            {
                h.Patch(AccessTools.Method(typeof(Pathea.ActorNs.Player), "OnEndSceneLoad"),
                    postfix: new HarmonyMethod(typeof(Patches), nameof(PlayerEndSceneLoadPostfix)));
            });
            Plugin.Log.LogInfo($"Patching finished: {applied} ok, {failed} failed.");
        }

        private static void Try(string name, Action a)
        {
            try
            {
                a();
                applied++;
                Plugin.Log.LogInfo("Patched " + name);
            }
            catch (Exception e)
            {
                failed++;
                Plugin.Log.LogError("FAILED to patch " + name + ": " + e);
            }
        }

        // ---------------------------------------------------------------- transpilers

        private static IEnumerable<CodeInstruction> LoadCoroutineTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var unloadUnused = AccessTools.Method(typeof(TimerHub), nameof(TimerHub.UnloadUnused));
            var isRemain = AccessTools.Method(typeof(ScenarioModule), "IsRemainScene");
            var setPosting = AccessTools.PropertySetter(typeof(ScenarioModule), nameof(ScenarioModule.PostingLoading));
            int nUnload = 0, nRemain = 0, nPosting = 0;
            foreach (var ci in instructions)
            {
                if (ci.Calls(unloadUnused))
                {
                    nUnload++;
                    // 1st call = right after the previous scene was unloaded/hidden, before the new one is loaded.
                    // (2nd call is inside the season-change branch and stays vanilla.)
                    if (nUnload == 1)
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = AccessTools.Method(typeof(Hooks), nameof(Hooks.AfterUnloadOrigin));
                    }
                }
                else if (ci.Calls(isRemain))
                {
                    nRemain++;
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(Hooks), nameof(Hooks.IsRemainScene));
                }
                else if (ci.Calls(setPosting))
                {
                    nPosting++;
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(Hooks), nameof(Hooks.SetPostingLoading));
                }
                yield return ci;
            }
            Plugin.Log.LogInfo($"LoadCoroutine: UnloadUnused calls={nUnload} (expected 2, replaced 1), IsRemainScene={nRemain} (expected 1), set_PostingLoading={nPosting} (expected 2)");
            if (nUnload < 1 || nRemain != 1 || nPosting != 2)
                Plugin.Log.LogWarning("LoadCoroutine IL differs from the version this mod was written for (game update?). Some features may not work.");
        }

        private static IEnumerable<CodeInstruction> SmartUnloadTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var unloadUnused = AccessTools.Method(typeof(TimerHub), nameof(TimerHub.UnloadUnused));
            int n = 0;
            foreach (var ci in instructions)
            {
                if (ci.Calls(unloadUnused))
                {
                    n++;
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(Hooks), nameof(Hooks.FinalUnloadUnused));
                }
                yield return ci;
            }
            Plugin.Log.LogInfo($"SmartUnloadUnusedAssets: replaced {n} UnloadUnused call(s) (expected 1)");
        }

        private static IEnumerable<CodeInstruction> PortalTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var playAnim = AccessTools.Field(typeof(Portal), "playAnim");
            int n = 0;
            foreach (var ci in instructions)
            {
                yield return ci;
                if (ci.LoadsField(playAnim))
                {
                    n++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Hooks), nameof(Hooks.FilterPlayAnim)));
                }
            }
            Plugin.Log.LogInfo($"Portal.GoToScenario: hooked {n} playAnim read(s) (expected 1)");
        }

        private static IEnumerable<CodeInstruction> MaskWaitTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            int n = 0;
            foreach (var ci in instructions)
            {
                if (ci.opcode == OpCodes.Ldc_R4 && ci.operand is float f && Math.Abs(f - 0.5f) < 0.0001f)
                {
                    n++;
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(Hooks), nameof(Hooks.GetCloseDelay));
                }
                yield return ci;
            }
            Plugin.Log.LogInfo($"LoadingMaskControl.Wait: replaced {n} delay constant(s) (expected 1)");
        }

        // ---------------------------------------------------------------- loading mask

        private static int hiddenMasks;

        private static bool BeforeLoadingMaskPrefix(ScenarioModule.Arg arg)
        {
            try
            {
                if (Hooks.ShouldHideMask(arg))
                {
                    hiddenMasks++;
                    Plugin.Log.LogInfo($"Loading screen hidden for {arg.fromScene} -> {arg.toScene}");
                    TransitionCover.Show($"{arg.fromScene} -> {arg.toScene}");
                    return false;
                }
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
            return true;
        }

        private static bool AfterLoadingMaskPrefix()
        {
            if (hiddenMasks > 0)
            {
                hiddenMasks--;
                if (hiddenMasks == 0) TransitionCover.RequestHide("scene load complete");
                return false;
            }
            return true;
        }

        private static readonly Dictionary<Tween, float> originalDurations = new Dictionary<Tween, float>();

        private static void ScaleTween(Tween t, bool fast)
        {
            if (t == null) return;
            if (!originalDurations.TryGetValue(t, out float orig))
            {
                orig = t.duration;
                originalDurations[t] = orig;
                if (Plugin.VerboseLogging.Value) Plugin.Log.LogInfo($"Loading screen tween '{t.name}' original duration {orig:0.###}s");
            }
            t.duration = fast ? orig * Mathf.Max(0f, Plugin.FadeTimeScale.Value) : orig;
        }

        private static void SetDisplayPrefix(LoadingMaskUI __instance)
        {
            try
            {
                bool fast = Plugin.FastLoadingScreen.Value;
                MaskRefs.minWaitRef(__instance) = new WaitForSecondsRealtime(fast ? Mathf.Max(0f, Plugin.MinDisplaySeconds.Value) : 1f);
                ScaleTween(MaskRefs.fwdTweenRef(__instance), fast);
                ScaleTween(MaskRefs.backTweenRef(__instance), fast);
                ScaleTween(MaskRefs.tweenBgRef(__instance), fast);
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static void MaskUpdatePostfix(LoadingMaskUI __instance)
        {
            if (!Plugin.FastLoadingScreen.Value) return;
            try
            {
                // Vanilla lerps the bar at 6/s and only lets the screen close once the bar visually reached 100%.
                if (MaskRefs.playendRef(__instance))
                {
                    MaskRefs.curValueRef(__instance) = 1f;
                    var bar = MaskRefs.progressBarRef(__instance);
                    if (bar != null) bar.fillAmount = 1f;
                }
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }

    internal static class MaskRefs
    {
        internal static readonly AccessTools.FieldRef<LoadingMaskUI, WaitForSecondsRealtime> minWaitRef =
            AccessTools.FieldRefAccess<LoadingMaskUI, WaitForSecondsRealtime>("minWait");
        internal static readonly AccessTools.FieldRef<LoadingMaskUI, Tween> fwdTweenRef = AccessTools.FieldRefAccess<LoadingMaskUI, Tween>("fwdTween");
        internal static readonly AccessTools.FieldRef<LoadingMaskUI, Tween> backTweenRef = AccessTools.FieldRefAccess<LoadingMaskUI, Tween>("backTween");
        internal static readonly AccessTools.FieldRef<LoadingMaskUI, Tween> tweenBgRef = AccessTools.FieldRefAccess<LoadingMaskUI, Tween>("tweenBg");
        internal static readonly AccessTools.FieldRef<LoadingMaskUI, bool> playendRef = AccessTools.FieldRefAccess<LoadingMaskUI, bool>("playend");
        internal static readonly AccessTools.FieldRef<LoadingMaskUI, float> curValueRef = AccessTools.FieldRefAccess<LoadingMaskUI, float>("curValue");
        internal static readonly AccessTools.FieldRef<LoadingMaskUI, UnityEngine.UI.Image> progressBarRef =
            AccessTools.FieldRefAccess<LoadingMaskUI, UnityEngine.UI.Image>("progressBar");
    }

    internal static class Hooks
    {
        private static readonly Func<ScenarioModule, AdditiveScene, bool> origIsRemain =
            AccessTools.MethodDelegate<Func<ScenarioModule, AdditiveScene, bool>>(AccessTools.Method(typeof(ScenarioModule), "IsRemainScene"));
        private static readonly Func<ScenarioModule, AdditiveScene, bool> origNeedReleaseAll =
            AccessTools.MethodDelegate<Func<ScenarioModule, AdditiveScene, bool>>(AccessTools.Method(typeof(ScenarioModule), "NeedReleaseAllLoadedSceneAsset"));
        private static readonly Action<ScenarioModule, bool> origSetPosting =
            AccessTools.MethodDelegate<Action<ScenarioModule, bool>>(AccessTools.PropertySetter(typeof(ScenarioModule), nameof(ScenarioModule.PostingLoading)));
        private static readonly Func<string, bool, bool> setSceneObjectsActive =
            AccessTools.MethodDelegate<Func<string, bool, bool>>(AccessTools.Method(typeof(ScenarioModule), "SetSceneObjectsActive"));
        private static readonly FieldInfo loadedScenesField = AccessTools.Field(typeof(ScenarioModule), "loadedScenes");
        private static readonly Type loadedInfoType = AccessTools.Inner(typeof(ScenarioModule), "LoadedSceneInfo");
        private static readonly FieldInfo liScene = AccessTools.Field(loadedInfoType, "scene");
        private static readonly FieldInfo liLightmaps = AccessTools.Field(loadedInfoType, "lightmaps");
        private static readonly FieldInfo liLightmapsMode = AccessTools.Field(loadedInfoType, "lightmapsMode");
        private static readonly FieldInfo liLightProbes = AccessTools.Field(loadedInfoType, "lightProbes");

        // transition state
        private static bool inTransition;
        private static int transitionCount;
        private static bool cleanupThisTransition;
        private static bool deserializeTransition;
        private static AdditiveScene fromScene = AdditiveScene.Max, toScene = AdditiveScene.Max;
        private static bool toWasCached;
        private static readonly Stopwatch stopwatch = new Stopwatch();
        private static UThreadPriority savedPriority;
        private static int savedTimeSlice;
        private static bool settingsChanged;

        private static readonly Dictionary<AdditiveScene, int> lastUsed = new Dictionary<AdditiveScene, int>();
        private static int useTick;

        private static string excludedRaw;
        private static HashSet<AdditiveScene> excluded = new HashSet<AdditiveScene>();

        private static bool Verbose => Plugin.VerboseLogging.Value;

        /// <summary>Forces the static initializer and verifies every game member we rely on exists.</summary>
        internal static bool SelfCheck()
        {
            bool ok = origIsRemain != null && origNeedReleaseAll != null && origSetPosting != null && setSceneObjectsActive != null
                      && loadedScenesField != null && loadedInfoType != null && liScene != null && liLightmaps != null
                      && liLightmapsMode != null && liLightProbes != null;
            Plugin.Log.LogInfo("Reflection self-check: " + (ok ? "OK" : "FAILED"));
            return ok;
        }

        private static HashSet<AdditiveScene> Excluded
        {
            get
            {
                string raw = Plugin.ExcludedScenes.Value ?? "";
                if (raw != excludedRaw)
                {
                    excludedRaw = raw;
                    excluded = new HashSet<AdditiveScene>(ParseScenes(raw));
                }
                return excluded;
            }
        }

        private static IEnumerable<AdditiveScene> ParseScenes(string raw)
        {
            foreach (var part in (raw ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                AdditiveScene s;
                bool ok;
                try { s = (AdditiveScene)Enum.Parse(typeof(AdditiveScene), part.Trim(), true); ok = true; }
                catch { s = AdditiveScene.Max; ok = false; }
                if (ok) yield return s;
                else if (!part.Trim().Equals("All", StringComparison.OrdinalIgnoreCase))
                    Plugin.Log.LogWarning("Unknown scene name in config: '" + part + "'");
            }
        }

        /// <summary>Scenes that this mod keeps in memory (the game itself unloads them on exit).</summary>
        internal static bool IsModCacheable(ScenarioModule sm, AdditiveScene scene)
        {
            if (scene == AdditiveScene.Max || scene == AdditiveScene.Faraway || scene == AdditiveScene.Start || scene == AdditiveScene.CustomPlayer)
                return false;
            if (string.IsNullOrEmpty(sm.GetSceneBundleName(scene))) return false;
            if (origNeedReleaseAll(sm, scene)) return false; // dungeons etc.: the game flushes everything for them
            if (origIsRemain(sm, scene)) return false;       // Main / PlayerHome / Chamber: already cached by the game
            return !Excluded.Contains(scene);
        }

        private static IList LoadedScenes(ScenarioModule sm) => (IList)loadedScenesField.GetValue(sm);

        private static List<AdditiveScene> LoadedSceneIds(ScenarioModule sm)
        {
            var list = new List<AdditiveScene>();
            foreach (var o in LoadedScenes(sm)) list.Add((AdditiveScene)liScene.GetValue(o));
            return list;
        }

        // ------------------------------------------------ called from patched ScenarioModule.LoadCoroutine

        public static bool IsRemainScene(ScenarioModule sm, AdditiveScene scene)
        {
            bool vanilla = origIsRemain(sm, scene);
            if (vanilla) return true;
            try
            {
                if (Plugin.KeepInteriorsLoaded.Value && IsModCacheable(sm, scene))
                {
                    Plugin.Log.LogInfo($"Keeping {scene} loaded (hidden) for instant re-entry");
                    lastUsed[scene] = ++useTick;
                    return true;
                }
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
            return false;
        }

        public static void SetPostingLoading(ScenarioModule sm, bool value)
        {
            origSetPosting(sm, value);
            try
            {
                if (value) OnTransitionBegin(sm);
                else OnTransitionEnd(sm);
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static void OnTransitionBegin(ScenarioModule sm)
        {
            inTransition = true;
            transitionCount++;
            fromScene = sm.FromScene;
            toScene = sm.LoadingScene;
            deserializeTransition = fromScene == AdditiveScene.Max;
            toWasCached = sm.IsCachedScene(toScene);
            int n = Plugin.FullCleanupEveryNTransitions.Value;
            cleanupThisTransition = !Plugin.SkipUnloadUnusedAssets.Value
                                    || deserializeTransition
                                    || sm.seasonChanged
                                    || origNeedReleaseAll(sm, toScene)
                                    || (n > 0 && transitionCount % n == 0);
            stopwatch.Reset();
            stopwatch.Start();

            settingsChanged = false;
            if (Plugin.HighLoadingPriority.Value || Plugin.AsyncUploadTimeSliceMs.Value > 0)
            {
                savedPriority = Application.backgroundLoadingPriority;
                savedTimeSlice = QualitySettings.asyncUploadTimeSlice;
                if (Plugin.HighLoadingPriority.Value) Application.backgroundLoadingPriority = UThreadPriority.High;
                if (Plugin.AsyncUploadTimeSliceMs.Value > 0)
                    QualitySettings.asyncUploadTimeSlice = Mathf.Clamp(Plugin.AsyncUploadTimeSliceMs.Value, 1, 33);
                settingsChanged = true;
            }
            Plugin.Log.LogInfo($"Transition #{transitionCount}: {fromScene} -> {toScene} (target cached: {toWasCached}, full cleanup: {cleanupThisTransition}, loaded scenes: {string.Join(",", LoadedSceneIds(sm).Select(s => s.ToString()).ToArray())})");
        }

        private static void OnTransitionEnd(ScenarioModule sm)
        {
            if (!inTransition) return;
            inTransition = false;
            stopwatch.Stop();
            if (settingsChanged)
            {
                Application.backgroundLoadingPriority = savedPriority;
                QualitySettings.asyncUploadTimeSlice = savedTimeSlice;
                settingsChanged = false;
            }
            var cur = sm.RealCurScene;
            if (cur != AdditiveScene.Max) lastUsed[cur] = ++useTick;
            Plugin.Log.LogInfo($"Transition #{transitionCount} finished: now in {cur}, took {stopwatch.ElapsedMilliseconds} ms (target was cached: {toWasCached})");
        }

        private static object UnloadUnusedOrSkip(string where)
        {
            if (!inTransition || cleanupThisTransition)
            {
                if (Verbose) Plugin.Log.LogInfo("UnloadUnusedAssets (" + where + ")");
                return TimerHub.UnloadUnused();
            }
            if (Verbose) Plugin.Log.LogInfo("Skipped UnloadUnusedAssets (" + where + ")");
            return null; // `yield return null` = wait one frame
        }

        /// <summary>Replaces the first `yield return TimerHub.UnloadUnused()` in LoadCoroutine
        /// (after the old scene was unloaded/hidden, before the new one is loaded).</summary>
        public static object AfterUnloadOrigin()
        {
            try
            {
                var sm = Module<ScenarioModule>.Self;
                if (sm == null) return TimerHub.UnloadUnused();
                var victims = ComputeEvictions(sm);
                var preload = ComputePreloads(sm);
                if (victims.Count == 0 && preload.Count == 0) return UnloadUnusedOrSkip("after unloading previous scene");
                return EvictAndPreload(sm, victims, preload);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
                return TimerHub.UnloadUnused();
            }
        }

        /// <summary>Replaces `TimerHub.UnloadUnused()` inside ScenarioModule.SmartUnloadUnusedAssets (end of every transition).</summary>
        public static object FinalUnloadUnused()
        {
            try { return UnloadUnusedOrSkip("end of transition"); }
            catch (Exception e) { Plugin.Log.LogError(e); return TimerHub.UnloadUnused(); }
        }

        private static List<AdditiveScene> ComputeEvictions(ScenarioModule sm)
        {
            var victims = new List<AdditiveScene>();
            // When KeepInteriorsLoaded is off, allowed = 0: scenes still cached by us (toggled off at runtime / preloaded) get dropped.
            int max = Mathf.Max(0, Plugin.MaxCachedInteriors.Value);
            var cached = LoadedSceneIds(sm).Where(s => s != toScene && IsModCacheable(sm, s)).ToList();
            int allowed = Plugin.KeepInteriorsLoaded.Value ? max - (IsModCacheable(sm, toScene) ? 1 : 0) : 0;
            if (allowed < 0) allowed = 0;
            if (cached.Count <= allowed) return victims;
            foreach (var s in cached.OrderBy(s => lastUsed.TryGetValue(s, out int t) ? t : 0))
            {
                if (cached.Count - victims.Count <= allowed) break;
                victims.Add(s);
            }
            return victims;
        }

        private static List<AdditiveScene> ComputePreloads(ScenarioModule sm)
        {
            var result = new List<AdditiveScene>();
            if (!deserializeTransition || !Plugin.PreloadOnSaveLoad.Value || !Plugin.KeepInteriorsLoaded.Value) return result;
            if (sm.seasonChanged) { Plugin.Log.LogInfo("Preload skipped: season change in progress"); return result; }
            string raw = Plugin.PreloadScenes.Value ?? "";
            IEnumerable<AdditiveScene> wanted = raw.Split(',').Any(p => p.Trim().Equals("All", StringComparison.OrdinalIgnoreCase))
                ? Enum.GetValues(typeof(AdditiveScene)).Cast<AdditiveScene>()
                : ParseScenes(raw);
            var loaded = new HashSet<AdditiveScene>(LoadedSceneIds(sm));
            int budget = Mathf.Max(0, Plugin.MaxCachedInteriors.Value) - loaded.Count(s => IsModCacheable(sm, s)) - (IsModCacheable(sm, toScene) ? 1 : 0);
            foreach (var s in wanted)
            {
                if (budget <= 0) break;
                if (s == toScene || loaded.Contains(s) || result.Contains(s) || !IsModCacheable(sm, s)) continue;
                result.Add(s);
                budget--;
            }
            return result;
        }

        private static IEnumerator EvictAndPreload(ScenarioModule sm, List<AdditiveScene> victims, List<AdditiveScene> preload)
        {
            bool didEvict = false;
            foreach (var s in victims)
            {
                string bundle = sm.GetSceneBundleName(s), name = sm.GetSceneName(s);
                Plugin.Log.LogInfo($"Evicting cached scene {s} (cache limit {Plugin.MaxCachedInteriors.Value})");
                Operation op = null;
                try { op = AssetMgr.UnloadSceneAsync(bundle, name); }
                catch (Exception e) { Plugin.Log.LogError(e); }
                if (op != null) yield return op;
                RemoveLoaded(sm, s);
                lastUsed.Remove(s);
                didEvict = true;
            }

            if (preload.Count > 0)
            {
                var total = Stopwatch.StartNew();
                Plugin.Log.LogInfo($"Preloading {preload.Count} interior(s): {string.Join(",", preload.Select(p => p.ToString()).ToArray())}");
                foreach (var s in preload)
                {
                    if (sm.IsCachedScene(s)) continue;
                    string bundle = sm.GetSceneBundleName(s), name = sm.GetSceneName(s);
                    var sw = Stopwatch.StartNew();
                    Operation op = null;
                    try
                    {
                        // same as the vanilla path for a fresh scene: start with no probes so the recorded set is the scene's own
                        LightmapSettings.lightProbes = null;
                        op = AssetMgr.LoadSceneAsync(bundle, name, true);
                    }
                    catch (Exception e) { Plugin.Log.LogError("Preload of " + s + " failed: " + e); }
                    if (op == null) continue;
                    yield return op;
                    bool ok = false;
                    try
                    {
                        ok = setSceneObjectsActive(name, false);
                        if (ok)
                        {
                            AddLoaded(sm, s);
                            lastUsed[s] = ++useTick;
                        }
                    }
                    catch (Exception e) { Plugin.Log.LogError("Preload of " + s + " failed: " + e); }
                    Plugin.Log.LogInfo(ok ? $"Preloaded {s} in {sw.ElapsedMilliseconds} ms" : $"Preload of {s} did not produce a loaded scene");
                    yield return null;
                }
                Plugin.Log.LogInfo($"Preloading finished in {total.ElapsedMilliseconds} ms");
            }

            if (didEvict || cleanupThisTransition || !Plugin.SkipUnloadUnusedAssets.Value)
                yield return TimerHub.UnloadUnused();
            else
                yield return null;
        }

        private static void RemoveLoaded(ScenarioModule sm, AdditiveScene scene)
        {
            var list = LoadedScenes(sm);
            for (int i = list.Count - 1; i >= 0; i--)
                if ((AdditiveScene)liScene.GetValue(list[i]) == scene) list.RemoveAt(i);
        }

        private static void AddLoaded(ScenarioModule sm, AdditiveScene scene)
        {
            var info = Activator.CreateInstance(loadedInfoType, true);
            liScene.SetValue(info, scene);
            liLightmaps.SetValue(info, LightmapSettings.lightmaps);
            liLightmapsMode.SetValue(info, LightmapSettings.lightmapsMode);
            liLightProbes.SetValue(info, LightmapSettings.lightProbes);
            LoadedScenes(sm).Add(info);
        }

        // ------------------------------------------------ loading screen / doors

        internal static bool ShouldHideMask(ScenarioModule.Arg arg)
        {
            var mode = Plugin.HideLoadingScreen.Value;
            if (mode == HideMode.Never) return false;
            bool Special(AdditiveScene s) => s == AdditiveScene.Max || s == AdditiveScene.Start || s == AdditiveScene.CustomPlayer || s == AdditiveScene.Faraway;
            if (Special(arg.fromScene) || Special(arg.toScene)) return false;
            if (mode == HideMode.Always || mode == HideMode.Sky) return true;
            var sm = Module<ScenarioModule>.Self;
            return sm != null && sm.IsCachedScene(arg.toScene) && !origNeedReleaseAll(sm, arg.toScene) && !sm.seasonChanged;
        }

        public static float GetCloseDelay()
        {
            return Plugin.FastLoadingScreen.Value ? Mathf.Max(0f, Plugin.CloseDelaySeconds.Value) : 0.5f;
        }

        public static bool FilterPlayAnim(bool playAnim)
        {
            if (playAnim && Plugin.SkipDoorAnimation.Value)
            {
                if (Verbose) Plugin.Log.LogInfo("Skipping door walk/open animation");
                return false;
            }
            return playAnim;
        }
    }
}
