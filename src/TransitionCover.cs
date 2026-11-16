using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Pathea.ActorNs;
using Pathea.FrameworkNs;
using Pathea.ScenarioNs;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace SandrockFastDoors
{
    public enum CoverStyle
    {
        Image,    // random pre-rendered picture from the covers folder (on an opaque black base)
        Black,    // fully opaque black screen (default)
        Skybox,   // the game's sky (on an opaque black backing)
        Gradient, // sky-colored gradient (opaque)
        None      // no cover (world visibly loads in)
    }

    /// <summary>
    /// Full-screen opaque cover shown instead of the loading screen during a scene switch.
    /// Goes up synchronously on ScenarioModule.PreLoad (first step of LoadCoroutine, before the old scene is hidden),
    /// comes down only after: CompleteLoad fired, the player was placed at the spawn point (Player.OnEndSceneLoad ->
    /// actor.SetScene + CameraModule.ResetToPlayerView), the player really is at that point, the main camera stopped
    /// moving for a few frames, MinCoverSeconds elapsed, plus RevealDelaySeconds / RevealDelayFrames.
    /// </summary>
    internal class TransitionCover : MonoBehaviour
    {
        private static TransitionCover instance;

        private Canvas canvas;
        private Image black;      // always-opaque black backing
        private RawImage content; // sky / gradient on top (Skybox / Gradient styles only)
        private Camera skyCam;
        private Skybox skyComp;
        private RenderTexture rt;
        private Texture2D gradientTex;

        private bool visible;
        private bool hiding;
        private float shownAt;
        private float hideRequestedAt;
        private int hideRequestedFrame;
        private float fadeStartAt = -1f;
        private CoverStyle activeStyle;
        private string reason;

        // placement tracking
        private static bool placementSeen;
        private static bool hasPlacementTarget;
        private static Vector3 placementTarget;
        private Vector3 lastCamPos;
        private Quaternion lastCamRot;
        private int stableFrames;
        private bool loggedForce;

        private static Material lastGoodSky;
        private static readonly AccessTools.FieldRef<UniStorm.UniStormSystem, Material> uniSkyRef = SafeRef();

        private static AccessTools.FieldRef<UniStorm.UniStormSystem, Material> SafeRef()
        {
            try { return AccessTools.FieldRefAccess<UniStorm.UniStormSystem, Material>("m_SkyBoxMaterial"); }
            catch { return null; }
        }

        private static readonly List<Texture2D> covers = new List<Texture2D>();
        private static int lastCover = -1;
        private static readonly System.Random rng = new System.Random();

        /// <summary>Loads every *.png / *.jpg in the folder (called once at plugin start).</summary>
        public static void LoadCovers(string folder)
        {
            covers.Clear();
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    Plugin.Log.LogWarning("Covers folder not found: " + folder + " (Image cover falls back to Black)");
                    return;
                }
                var files = new List<string>();
                foreach (var pattern in new[] { "*.png", "*.jpg", "*.jpeg" })
                    files.AddRange(Directory.GetFiles(folder, pattern));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    try
                    {
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(f), true)) // markNonReadable: frees the CPU copy
                        {
                            Destroy(tex);
                            Plugin.Log.LogWarning("Could not decode cover image " + f);
                            continue;
                        }
                        tex.name = "SandrockFastDoors_" + Path.GetFileNameWithoutExtension(f);
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.filterMode = FilterMode.Bilinear;
                        tex.hideFlags = HideFlags.DontUnloadUnusedAsset; // survive Resources.UnloadUnusedAssets
                        covers.Add(tex);
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("Failed to load cover " + f + ": " + e.Message); }
                }
                Plugin.Log.LogInfo($"Loaded {covers.Count} cover image(s) from {folder}");
            }
            catch (Exception e) { Plugin.Log.LogError("Loading covers failed: " + e); }
        }

        private static Texture2D PickCover()
        {
            covers.RemoveAll(t => t == null);
            if (covers.Count == 0) return null;
            int i = rng.Next(covers.Count);
            if (covers.Count > 1 && i == lastCover) i = (i + 1 + rng.Next(covers.Count - 1)) % covers.Count;
            lastCover = i;
            return covers[i];
        }

        /// <summary>Aspect-fill ("cover"): fill the whole screen, crop the overflow, centered.</summary>
        private void FitCover(Texture tex)
        {
            if (tex == null || tex.height == 0 || Screen.height == 0) { content.uvRect = new Rect(0, 0, 1, 1); return; }
            float img = (float)tex.width / tex.height, scr = (float)Screen.width / Screen.height;
            if (scr > img) { float h = img / scr; content.uvRect = new Rect(0f, (1f - h) * 0.5f, 1f, h); }
            else { float w = scr / img; content.uvRect = new Rect((1f - w) * 0.5f, 0f, w, 1f); }
        }

        public static bool IsVisible => instance != null && instance.visible;

        public static void Show(string why)
        {
            var style = Plugin.TransitionCoverStyle.Value;
            if (style == CoverStyle.None) return;
            try
            {
                if (instance == null)
                {
                    var go = new GameObject("SandrockFastDoors_TransitionCover");
                    DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    instance = go.AddComponent<TransitionCover>();
                    instance.Build();
                }
                placementSeen = false;
                hasPlacementTarget = false;
                instance.ShowInternal(style, why);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Transition cover failed to show: " + e);
            }
        }

        /// <summary>Called from the Player.OnEndSceneLoad postfix: the player was just teleported to the spawn point.</summary>
        public static void NotifyPlayerPlaced(PosRot target)
        {
            placementSeen = true;
            hasPlacementTarget = target != null;
            if (target != null) placementTarget = target.pos;
            if (instance != null && instance.visible && Plugin.VerboseLogging.Value)
                Plugin.Log.LogInfo("Transition cover: player placed" + (target != null ? " at " + target.pos : " (no spawn point, stays in place)"));
        }

        public static void RequestHide(string why)
        {
            if (instance == null || !instance.visible || instance.hiding) return;
            instance.hiding = true;
            instance.hideRequestedAt = Time.unscaledTime;
            instance.hideRequestedFrame = Time.frameCount;
            instance.fadeStartAt = -1f;
            instance.stableFrames = 0;
            instance.loggedForce = false;
            if (Plugin.VerboseLogging.Value) Plugin.Log.LogInfo("Transition cover: hide requested (" + why + ")");
        }

        private void Build()
        {
            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(transform, false);
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; // drawn after every camera, on top of all game canvases
            canvas.sortingOrder = short.MaxValue;
            canvas.overrideSorting = true;
            canvas.pixelPerfect = false;

            black = MakeGraphic<Image>(canvasGo.transform, "Black");
            black.sprite = null;          // plain white quad tinted to pure black
            black.color = Color.black;    // (0,0,0,1) - alpha exactly 1
            content = MakeGraphic<RawImage>(canvasGo.transform, "Content");
            content.enabled = false;
            canvasGo.SetActive(false);
        }

        private static T MakeGraphic<T>(Transform parent, string name) where T : Graphic
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var g = go.AddComponent<T>();
            g.raycastTarget = false; // never eats clicks; input is disabled by the game during loading anyway
            var r = g.rectTransform;
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(-4f, -4f); // a few pixels of overscan
            r.offsetMax = new Vector2(4f, 4f);
            return g;
        }

        private void ShowInternal(CoverStyle style, string why)
        {
            reason = why;
            content.enabled = false;
            if (skyCam != null) skyCam.enabled = false;

            content.uvRect = new Rect(0f, 0f, 1f, 1f);
            if (style == CoverStyle.Image)
            {
                var tex = PickCover();
                if (tex == null) style = CoverStyle.Black;
                else
                {
                    content.texture = tex;
                    FitCover(tex);
                    content.enabled = true;
                    why += " [" + tex.name + "]";
                    reason = why;
                }
            }
            else if (style == CoverStyle.Skybox)
            {
                var sky = FindSkyMaterial();
                if (sky == null) style = CoverStyle.Black;
                else
                {
                    try
                    {
                        EnsureCamera();
                        EnsureRT();
                        skyComp.material = sky;
                        SyncSkyCamera();
                        skyCam.enabled = true;
                        content.texture = rt;
                        content.enabled = true;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Transition cover: sky render failed, using black: " + e.Message);
                        if (skyCam != null) skyCam.enabled = false;
                        style = CoverStyle.Black;
                    }
                }
            }
            else if (style == CoverStyle.Gradient)
            {
                content.texture = BuildGradient();
                content.enabled = true;
            }

            activeStyle = style;
            SetAlpha(1f);
            canvas.gameObject.SetActive(true);
            visible = true;
            hiding = false;
            fadeStartAt = -1f;
            stableFrames = 0;
            shownAt = Time.unscaledTime;
            var cam = Camera.main;
            if (cam != null) { lastCamPos = cam.transform.position; lastCamRot = cam.transform.rotation; }
            Plugin.Log.LogInfo($"Transition cover up ({style}) - {why}");
        }

        private void SetAlpha(float a)
        {
            black.color = new Color(0f, 0f, 0f, a);
            content.color = new Color(1f, 1f, 1f, a);
        }

        private Material FindSkyMaterial()
        {
            Material m = null;
            try
            {
                var uni = UniStorm.UniStormSystem.Instance;
                if (uni != null && uniSkyRef != null) m = uniSkyRef(uni);
            }
            catch { m = null; }
            if (m != null) lastGoodSky = m;
            return m != null ? m : lastGoodSky;
        }

        private void EnsureCamera()
        {
            if (skyCam != null) return;
            var camGo = new GameObject("SkyCamera");
            camGo.transform.SetParent(transform, false);
            skyCam = camGo.AddComponent<Camera>();
            skyCam.cullingMask = 0;
            skyCam.clearFlags = CameraClearFlags.Skybox;
            skyCam.depth = -100;
            skyCam.allowHDR = false;
            skyCam.allowMSAA = false;
            skyCam.enabled = false;
            skyComp = camGo.AddComponent<Skybox>();
            try
            {
                var data = skyCam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = false;
                data.renderShadows = false;
            }
            catch { }
        }

        private void EnsureRT()
        {
            int w = Mathf.Max(64, Screen.width / 2), h = Mathf.Max(64, Screen.height / 2);
            if (rt != null && rt.width == w && rt.height == h) return;
            if (rt != null) { skyCam.targetTexture = null; rt.Release(); Destroy(rt); }
            rt = new RenderTexture(w, h, 24, RenderTextureFormat.Default) { name = "SandrockFastDoors_Sky" };
            rt.Create();
            skyCam.targetTexture = rt;
        }

        private void SyncSkyCamera()
        {
            var main = Camera.main;
            if (main != null && main != skyCam)
            {
                skyCam.transform.SetPositionAndRotation(main.transform.position, Quaternion.Euler(-12f, main.transform.eulerAngles.y, 0f));
                skyCam.fieldOfView = main.fieldOfView;
            }
            skyCam.aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
        }

        private Texture2D BuildGradient()
        {
            Color top = new Color(0.28f, 0.50f, 0.82f), horizon = new Color(0.72f, 0.84f, 0.95f);
            if (gradientTex == null)
                gradientTex = new Texture2D(1, 64, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < 64; y++)
                gradientTex.SetPixel(0, y, Color.Lerp(horizon, top, Mathf.SmoothStep(0f, 1f, y / 63f)));
            gradientTex.Apply(false);
            return gradientTex;
        }

        private bool PlayerAtTarget()
        {
            if (!placementSeen) return false;
            if (!hasPlacementTarget) return true;
            try
            {
                var player = Module<Player>.Self;
                if (player == null || player.actor == null) return true;
                Vector3 p = player.actor.GamePos;
                return (p - placementTarget).sqrMagnitude < 3f * 3f;
            }
            catch { return true; }
        }

        private void LateUpdate()
        {
            if (!visible) return;
            float now = Time.unscaledTime;

            if (activeStyle == CoverStyle.Image) FitCover(content.texture); // handles resolution changes
            if (activeStyle == CoverStyle.Skybox && skyCam != null)
            {
                try { EnsureRT(); SyncSkyCamera(); content.texture = rt; } catch { }
            }

            // camera stability tracking (Cinemachine may still be blending/snapping after the teleport)
            var cam = Camera.main;
            if (cam != null)
            {
                var pos = cam.transform.position;
                var rot = cam.transform.rotation;
                bool still = (pos - lastCamPos).sqrMagnitude < 0.02f * 0.02f && Quaternion.Angle(rot, lastCamRot) < 0.2f;
                stableFrames = still ? stableFrames + 1 : 0;
                lastCamPos = pos;
                lastCamRot = rot;
            }
            else stableFrames++;

            float timeout = Mathf.Max(1f, Plugin.SkyCoverTimeoutSeconds.Value);
            if (fadeStartAt < 0f && now - shownAt > timeout)
            {
                Plugin.Log.LogWarning($"Transition cover safety timeout ({timeout:0.#} s) - removing it");
                fadeStartAt = now;
                hiding = true;
            }

            if (fadeStartAt < 0f)
            {
                if (!hiding) return;
                if (now - shownAt < Mathf.Max(0f, Plugin.MinCoverSeconds.Value)) return;
                if (now - hideRequestedAt < Mathf.Max(0f, Plugin.RevealDelaySeconds.Value)) return;
                if (Time.frameCount - hideRequestedFrame < Mathf.Max(0, Plugin.RevealDelayFrames.Value)) return;
                bool placed = PlayerAtTarget();
                bool camStill = stableFrames >= 3;
                if (!placed || !camStill)
                {
                    // don't wait forever for a camera that keeps moving (e.g. cutscene camera) or an unusual spawn
                    if (now - hideRequestedAt < 3f) return;
                    if (!loggedForce)
                    {
                        loggedForce = true;
                        Plugin.Log.LogInfo($"Transition cover: revealing although placed={placed}, cameraStill={camStill} (3 s after load end)");
                    }
                }
                fadeStartAt = now;
            }

            float fade = Mathf.Max(0f, Plugin.RevealFadeSeconds.Value);
            float a = fade <= 0.0001f ? 0f : 1f - Mathf.Clamp01((now - fadeStartAt) / fade);
            SetAlpha(a);
            if (a <= 0f) HideNow();
        }

        private void HideNow()
        {
            visible = false;
            hiding = false;
            canvas.gameObject.SetActive(false);
            if (skyCam != null) skyCam.enabled = false;
            SetAlpha(1f);
            Plugin.Log.LogInfo($"Transition cover down after {(Time.unscaledTime - shownAt) * 1000f:0} ms ({reason})");
        }
    }
}
