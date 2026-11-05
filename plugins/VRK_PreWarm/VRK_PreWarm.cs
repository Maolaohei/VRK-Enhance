// VRK_PreWarm - hide the first-frame cost of a freshly loaded scene.
//
// What it does, in this order, a couple of frames after every scene load:
//   1) Shader.WarmupAllShaders()  - compiles every shader variant of the build once
//      per session, so the first time a material appears on screen it is already
//      compiled instead of hitching.
//   2) Material.SetPass(0) on every loaded material - forces the variant the object
//      will actually draw with to be resolved while we still have budget.
//   3) Texture.GetNativeTexturePtr() on every loaded Texture2D - forces the upload
//      to VRAM, so textures do not pop in during the first seconds.
//
// Everything is time sliced: FrameBudgetMs of work per frame, TotalBudgetMs per
// scene, then it gives up and logs what it managed to do. The game keeps running
// normally while warming.
//
// SAFETY: this plugin never touches the engine from Awake(). BepInEx runs plugin
// Awake while Unity is still bringing the graphics device up; in Unity 5.4.5f1
// SystemInfo/GfxDevice singletons are still null at that point and any graphics
// icall taken there is an instant 0xC0000005 (see VRK_UpScale for the full story).
// All work therefore happens inside a coroutine that has already yielded a frame.

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRKanojo.PreWarm
{
    [BepInPlugin(Guid, "VRK PreWarm (shader / material / texture)", "1.1.1")]
    public class PreWarmPlugin : BaseUnityPlugin
    {
        // v1.27.1 修 BUG: VR 模式下预热会用 main thread 抢帧(材质 SetPass / 纹理 GetNativeTexturePtr),
        // 头显里就是可见的顿挫, 所以 VR 入口下直接不预热。
        internal static readonly bool VrMode = System.Environment.GetEnvironmentVariable("VRKANOJO_VR") == "1";

        public const string Guid = "vrk.prewarm";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> WarmShaders;
        internal static ConfigEntry<bool> WarmMaterials;
        internal static ConfigEntry<bool> WarmTextures;
        internal static ConfigEntry<bool> SkipLoadingScenes;
        internal static ConfigEntry<float> FrameBudgetMs;
        internal static ConfigEntry<float> TotalBudgetMs;
        internal static ConfigEntry<bool> Verbose;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("PreWarm", "Enabled", true,
                "Master switch. When false the runtime object is never created.");
            WarmShaders = Config.Bind("PreWarm", "WarmShaders", false,
                "Call Shader.WarmupAllShaders() once per session (biggest win, biggest single cost).");
            WarmMaterials = Config.Bind("PreWarm", "WarmMaterials", true,
                "Run Material.SetPass(0) over every loaded material so its variant is compiled up front.");
            WarmTextures = Config.Bind("PreWarm", "WarmTextures", true,
                "Touch GetNativeTexturePtr() on every Texture2D to force the VRAM upload.");
            SkipLoadingScenes = Config.Bind("PreWarm", "SkipLoadingScenes", false,
                "Skip the logo / loading scenes (Init, Init2, CheckHMD, SetHMD, Logo2, Logo, Attention). " +
                "Leave this false so the shader warmup happens behind the loading screen.");
            FrameBudgetMs = Config.Bind("PreWarm", "FrameBudgetMs", 6.0f,
                "Milliseconds of warming allowed per frame before yielding.");
            TotalBudgetMs = Config.Bind("PreWarm", "TotalBudgetMs", 2000.0f,
                "Milliseconds of warming allowed per scene before giving up.");
            Verbose = Config.Bind("Debug", "Verbose", false,
                "Log per phase details instead of one summary line per scene.");

            if (!Enabled.Value)
            {
                Log.LogInfo("disabled by config");
                return;
            }

            // Creating a GameObject is safe here; touching anything graphical is not.
            GameObject go = new GameObject("VRK_PreWarm_Runtime");
            DontDestroyOnLoad(go);
            go.AddComponent<PreWarmRuntime>();
        }
    }

    internal class PreWarmRuntime : MonoBehaviour
    {
        private static readonly HashSet<string> LoaderScenes = new HashSet<string>
        {
            "Init", "Init2", "CheckHMD", "SetHMD", "Logo2", "Logo", "Attention"
        };

        private bool _shadersWarmed;

        private void Awake()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void Start()
        {
            // The very first scene does not fire sceneLoaded for us (we subscribed too late).
            StartCoroutine(Warm(SceneManager.GetActiveScene()));
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            StartCoroutine(Warm(scene));
        }

        private System.Collections.IEnumerator Warm(Scene scene)
        {
            if (PreWarmPlugin.VrMode) { yield break; }

            // One frame of slack: the new scene's objects have run their own Awake/Start
            // by now, and the graphics device has been alive for a while.
            yield return null;

            if (!PreWarmPlugin.Enabled.Value)
                yield break;

            if (PreWarmPlugin.SkipLoadingScenes.Value && LoaderScenes.Contains(scene.name))
                yield break;

            float started = Time.realtimeSinceStartup;
            float totalBudget = PreWarmPlugin.TotalBudgetMs.Value;
            float frameBudget = PreWarmPlugin.FrameBudgetMs.Value;
            int mats = 0;
            int texs = 0;
            bool shaders = false;
            bool aborted = false;

            if (PreWarmPlugin.WarmShaders.Value && !_shadersWarmed)
            {
                _shadersWarmed = true;
                float t0 = Time.realtimeSinceStartup;
                try
                {
                    Shader.WarmupAllShaders();
                    shaders = true;
                }
                catch (Exception e)
                {
                    PreWarmPlugin.Log.LogWarning("Shader.WarmupAllShaders failed: " + e.GetType().Name);
                }
                if (PreWarmPlugin.Verbose.Value)
                    PreWarmPlugin.Log.LogInfo(string.Format("shader warmup {0:0.0}ms", (Time.realtimeSinceStartup - t0) * 1000f));
                yield return null;
            }

            if (PreWarmPlugin.WarmMaterials.Value)
            {
                UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(typeof(Material));
                float frameStart = Time.realtimeSinceStartup;
                for (int i = 0; i < objs.Length; ++i)
                {
                    Material m = objs[i] as Material;
                    if (m != null && m.shader != null)
                    {
                        try
                        {
                            m.SetPass(0);
                            ++mats;
                        }
                        catch (Exception)
                        {
                            // A material that refuses to set a pass is simply not warmable.
                        }
                    }

                    if (Time.realtimeSinceStartup - frameStart >= frameBudget)
                    {
                        frameStart = Time.realtimeSinceStartup;
                        yield return null;
                    }
                    if (Time.realtimeSinceStartup - started >= totalBudget)
                    {
                        aborted = true;
                        break;
                    }
                }
            }

            if (PreWarmPlugin.WarmTextures.Value && !aborted)
            {
                UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(typeof(Texture));
                float frameStart = Time.realtimeSinceStartup;
                for (int i = 0; i < objs.Length; ++i)
                {
                    Texture t = objs[i] as Texture;
                    if (t != null && !(t is RenderTexture))
                    {
                        try
                        {
                            if (t.GetNativeTexturePtr() != IntPtr.Zero)
                                ++texs;
                        }
                        catch (Exception)
                        {
                            // Not uploadable (screen backbuffer, procedural, ...) - ignore.
                        }
                    }

                    if (Time.realtimeSinceStartup - frameStart >= frameBudget)
                    {
                        frameStart = Time.realtimeSinceStartup;
                        yield return null;
                    }
                    if (Time.realtimeSinceStartup - started >= totalBudget)
                    {
                        aborted = true;
                        break;
                    }
                }
            }

            float spent = (Time.realtimeSinceStartup - started) * 1000f;
            PreWarmPlugin.Log.LogInfo(string.Format(
                "prewarm '{0}': materials={1} textures={2} shaders={3} {4:0.0}ms{5}",
                scene.name, mats, texs, shaders ? "yes" : "no", spent, aborted ? " (budget reached)" : ""));
        }
    }
}
