using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRKanojo.Perf
{
    /// <summary>
    /// VRK Perf — 只做"不损失画质"的运行成本治理, 不碰任何渲染质量选项。
    ///
    /// 做四件事:
    ///   1) 垂直同步 / 帧率上限。Unity 5.4 的 vSyncCount=1 在 240Hz 屏上会把帧率钉到 240/120/80 的
    ///      分频点, GPU 只要撑不住 240 就会掉到 120 并产生节奏抖动 —— 这是 1% low 最典型的来源。
    ///      关掉 vsync 让帧时间连续, 是零画质成本的收益。
    ///   2) 加载期主动 GC。Unity 5.4 用 Boehm 全停顿 GC, 游戏内的堆增长最终会在某个随机帧触发
    ///      一次几十毫秒的回收 —— 那一下就是 1% low 的坑。在加载/淡出屏期间主动回收, 把尖峰挪到
    ///      玩家看不见的时候。
    ///   3) 后台资源加载优先级。加载屏给 High 尽快过, 游戏内给 Low 不抢主线程。
    ///   4) 相机清单诊断(只打日志, 不改变任何行为), 用来确认没有多余的相机在重复渲染。
    ///
    /// 刻意不引用 Assembly-CSharp、不使用 Harmony: 这个插件与游戏逻辑零耦合, 不会影响任何已有补丁。
    /// </summary>
    [BepInPlugin(Guid, "VRK Perf (no visual loss)", Version)]
    public class PerfPlugin : BaseUnityPlugin
    {
        // 真 VR 模式(由启动器设置 VRKANOJO_VR=1)下不得关 vSync / 改 targetFrameRate
        // —— VR 靠帧同步保证延迟, 本插件整体不生效。
        internal static readonly bool VrMode =
            System.Environment.GetEnvironmentVariable("VRKANOJO_VR") == "1";

        public const string Guid = "vrk.perf";
        public const string Version = "1.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<int> VSync;
        internal static ConfigEntry<int> TargetFrameRate;
        internal static ConfigEntry<bool> GcOnSceneLoad;
        internal static ConfigEntry<bool> ManagedLoadingPriority;
        internal static ConfigEntry<bool> CameraReport;
        internal static ConfigEntry<bool> PinOvrRecommendedMsaa;
        internal static ConfigEntry<bool> Verbose;

        private void Awake()
        {
            Log = Logger;

            VSync = Config.Bind("Frame", "VSync", 0,
                "垂直同步覆盖: -1 = 不改动游戏设置, 0 = 关闭(推荐), 1 = 开启, 2 = 隔帧。\n" +
                "240Hz 屏上 vSync=1 会在撑不住 240 时掉到 120 并产生节奏抖动, 关掉通常明显改善 1% low。");

            TargetFrameRate = Config.Bind("Frame", "TargetFrameRate", 0,
                "帧率上限(仅在 VSync=0 时生效): -1 = 不改动, 0 = 不限制, 其它 = 上限。\n" +
                "撕裂明显时设成 刷新率-3(例如 237)或 120, 帧时间会更稳。");

            GcOnSceneLoad = Config.Bind("Loading", "GcOnSceneLoad", true,
                "进入加载/淡出场景时主动 GC.Collect(), 把全停顿回收挪到看不见的时候。\n" +
                "游戏过程中不做任何强制回收。");

            ManagedLoadingPriority = Config.Bind("Loading", "ManagedLoadingPriority", true,
                "加载屏期间把 Application.backgroundLoadingPriority 提到 High, 游戏内降回 Low。");

            CameraReport = Config.Bind("Diagnostics", "CameraReport", false,
                "每个场景打印一次启用的相机清单(只写日志, 不改变行为)。用于确认没有多余相机在重复渲染。");

            PinOvrRecommendedMsaa = Config.Bind("Compatibility", "PinOvrRecommendedMsaa", true,
                "把 OVRManager.useRecommendedMSAALevel 关掉。\n" +
                "无头显时 OVRManager.Update 每帧都会比较 QualitySettings.antiAliasing 与 display.recommendedMSAALevel,\n" +
                "不一致就 Debug.Log 并强制改写 MSAA —— 既可能把画质档的 4x MSAA 悄悄降成 0, 又可能每帧刷日志吃 CPU。\n" +
                "关掉后游戏自己的抗锯齿设置说了算(纯反射, 不使用 Harmony, 不修改任何方法体)。");

            Verbose = Config.Bind("Diagnostics", "Verbose", false, "打印每次设置的生效结果。");

            GameObject host = new GameObject("VRK_Perf_Runtime");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<PerfRuntime>();

            Log.LogInfo("[Perf] v" + Version + " vsync=" + DescribeVSync(VSync.Value)
                + " cap=" + DescribeCap(TargetFrameRate.Value)
                + " gcOnLoad=" + GcOnSceneLoad.Value
                + " loadingPriority=" + ManagedLoadingPriority.Value
                + " msaaPin=" + PinOvrRecommendedMsaa.Value
                + " vrMode=" + VrMode);
        }

        internal static string DescribeVSync(int v)
        {
            if (v < 0) return "untouched";
            return v == 0 ? "off" : (v == 1 ? "on" : "every2nd");
        }

        internal static string DescribeCap(int v)
        {
            if (v < 0) return "untouched";
            return v == 0 ? "unlimited" : (v + "fps");
        }
    }

    internal class PerfRuntime : MonoBehaviour
    {
        // 与 VRK_FlatFPS 使用同一份加载场景名单(从 globalgamemanagers 的 build 列表取)
        private static readonly HashSet<string> LoadingScenes = new HashSet<string>
        {
            "Init", "Init2", "CheckHMD", "SetHMD", "Logo2", "Logo", "Attention",
        };

        private float _reportAt;
        private bool _reportPending;
        private bool _subscribed;

        // OVRManager 是场景里现 instantiate 出来的, 所以分几个时间点反射重试
        private static readonly float[] PinTimes = new float[] { 0.5f, 2f, 5f };
        private bool _pinActive;
        private int _pinIndex;
        private float _pinAt;

        private void Start()
        {
            TrySubscribe();
            ApplyAll("startup");
            SchedulePin();
        }

        private void OnDestroy()
        {
            if (!_subscribed) return;
            try { SceneManager.sceneLoaded -= OnSceneLoaded; }
            catch { }
            _subscribed = false;
        }

        private void TrySubscribe()
        {
            try
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                _subscribed = true;
            }
            catch (Exception e)
            {
                PerfPlugin.Log.LogWarning("[Perf] sceneLoaded hook failed: " + e.Message);
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            string name = scene.name ?? string.Empty;

            if (PerfPlugin.ManagedLoadingPriority.Value)
            {
                try
                {
                    Application.backgroundLoadingPriority = LoadingScenes.Contains(name)
                        ? ThreadPriority.High : ThreadPriority.Low;
                }
                catch { }
            }

            if (PerfPlugin.GcOnSceneLoad.Value && LoadingScenes.Contains(name))
            {
                try
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                catch { }
            }

            ApplyAll("scene:" + name);

            if (PerfPlugin.PinOvrRecommendedMsaa.Value && !_pinActive)
                SchedulePin();

            if (PerfPlugin.CameraReport.Value)
            {
                ReportCameras("on-load");
                _reportAt = Time.realtimeSinceStartup + 3f;
                _reportPending = true;
            }
        }

        private void ApplyAll(string tag)
        {
            if (PerfPlugin.VrMode)
                return;

            int vsync = PerfPlugin.VSync.Value;
            if (vsync >= 0 && QualitySettings.vSyncCount != vsync)
            {
                QualitySettings.vSyncCount = vsync;
                if (PerfPlugin.Verbose.Value)
                    PerfPlugin.Log.LogInfo("[Perf] vSyncCount=" + vsync + " (" + tag + ")");
            }

            int cap = PerfPlugin.TargetFrameRate.Value;
            if (cap >= 0 && Application.targetFrameRate != cap)
            {
                Application.targetFrameRate = cap;
                if (PerfPlugin.Verbose.Value)
                    PerfPlugin.Log.LogInfo("[Perf] targetFrameRate=" + cap + " (" + tag + ")");
            }
        }

        private void Update()
        {
            // 刻意保持零分配: 只有到点的那一帧才进入日志分支
            if (_pinActive && Time.realtimeSinceStartup >= _pinAt)
                TickPin();

            if (!_reportPending) return;
            if (Time.realtimeSinceStartup < _reportAt) return;
            _reportPending = false;
            ReportCameras("+3s");
        }

        private void SchedulePin()
        {
            if (!PerfPlugin.PinOvrRecommendedMsaa.Value)
            {
                _pinActive = false;
                return;
            }
            _pinActive = true;
            _pinIndex = 0;
            _pinAt = Time.realtimeSinceStartup + PinTimes[0];
        }

        private void TickPin()
        {
            if (TryPinOvrRecommendedMsaa())
            {
                _pinActive = false;
                return;
            }
            _pinIndex++;
            if (_pinIndex >= PinTimes.Length)
            {
                _pinActive = false;
                PerfPlugin.Log.LogWarning("[Perf] OVRManager not found; msaa pin skipped (OVR 未实例化, 无需处理)");
                return;
            }
            _pinAt = Time.realtimeSinceStartup + PinTimes[_pinIndex];
        }

        private bool TryPinOvrRecommendedMsaa()
        {
            try
            {
                Type t = Type.GetType("OVRManager, Assembly-CSharp");
                if (t == null) return false;

                PropertyInfo pi = t.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                object inst = pi == null ? null : pi.GetValue(null, null);
                if (inst == null) return false;

                FieldInfo f = t.GetField("useRecommendedMSAALevel", BindingFlags.Public | BindingFlags.Instance);
                if (f == null) return false;

                bool cur = (bool)f.GetValue(inst);
                if (cur)
                {
                    f.SetValue(inst, false);
                    PerfPlugin.Log.LogInfo("[Perf] OVRManager.useRecommendedMSAALevel -> false"
                        + " (MSAA 保持画质档设定 = " + QualitySettings.antiAliasing + "x)");
                }
                return true;
            }
            catch (Exception e)
            {
                PerfPlugin.Log.LogWarning("[Perf] OVR msaa pin failed: " + e.GetType().Name + ": " + e.Message);
                return true; // 出错就不再重试
            }
        }

        private void ReportCameras(string tag)
        {
            try
            {
                Camera[] cams = Camera.allCameras;
                if (cams == null || cams.Length == 0)
                {
                    PerfPlugin.Log.LogInfo("[Perf] cameras(" + tag + "): none enabled");
                    return;
                }

                StringBuilder sb = new StringBuilder(256);
                sb.Append("[Perf] cameras(").Append(tag).Append("): ").Append(cams.Length);
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera c = cams[i];
                    if (c == null) continue;
                    sb.Append("\n  [").Append(i).Append("] ").Append(c.name)
                      .Append(" depth=").Append(c.depth)
                      .Append(" rect=").Append(c.pixelWidth).Append('x').Append(c.pixelHeight)
                      .Append(" target=").Append(c.targetTexture == null ? "screen" : c.targetTexture.name)
                      .Append(" hdr=").Append(c.hdr)
                      .Append(" culling=0x").Append(c.cullingMask.ToString("X8"));
                }
                PerfPlugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                PerfPlugin.Log.LogWarning("[Perf] camera report failed: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
