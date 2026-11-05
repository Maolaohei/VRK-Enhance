// VRK_TexPlus — 纹理 Plus（BepInEx 版）
// 目标: Unity 5.4.5f1 / .NET 3.5 (mscorlib 2.0.5.0) / BepInEx 5.4.12
//
// 说明: 补丁/纹理plus 文件夹里实际只有 IPA(Illusion Plugin Architecture) 安装器，
// 不含任何贴图或纹理插件；IPA 又会改写 VR_Kanojo.exe 的 PE 头，和 fix.exe 的
// 字节补丁互相冲突。所以这里用 BepInEx 插件实现同等的"纹理增强"效果：
//   1) 强制全分辨率贴图 (QualitySettings.masterTextureLimit = 0)
//   2) 强制各向异性过滤 (QualitySettings.anisotropicFiltering = ForceEnable)
//   3) 把每个 Texture 的 anisoLevel 提到 16 —— 关键项，游戏本体里的贴图多半是 1，
//      anisotropicFiltering 只是"上限许可"，贴图自身 anisoLevel=1 时依然模糊
//   4) 可选: Bilinear -> Trilinear，可选 mipMapBias 负值让远处贴图更锐
// 全部在内存里改运行时对象，不写盘、不替换游戏文件。

using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace VRKanojo.TexPlus
{
    [BepInPlugin(Guid, "VRK Texture Plus", "1.3.0")]
    public class TexPlusPlugin : BaseUnityPlugin
    {
        public const string Guid = "vrk.texplus";

        internal static ManualLogSource Log;

        internal static ConfigEntry<int> AnisoLevel;
        internal static ConfigEntry<bool> ForceTrilinear;
        internal static ConfigEntry<bool> FullResolution;
        internal static ConfigEntry<float> MipMapBias;
        internal static ConfigEntry<float> RescanSeconds;
        internal static ConfigEntry<int> PerFrame;
        internal static ConfigEntry<bool> Verbose;

        private void Awake()
        {
            Log = Logger;

            // v1.28.0: VR 入口下一律不启用 —— 4608 张单目贴图提到 aniso 16 + 三线性过滤,
            // 在头显里是白付的带宽/采样开销, 还会因为改纹理状态触发驱动重编译。
            if (System.Environment.GetEnvironmentVariable("VRKANOJO_VR") == "1")
            {
                if (Log != null)
                    Log.LogInfo("[TexPlus] VRKANOJO_VR=1 -> texture tweaks disabled in VR mode");
                return;
            }

            FullResolution = Config.Bind("Texture", "FullResolution", true,
                "强制 masterTextureLimit=0 与 anisotropicFiltering=ForceEnable（游戏默认就是这两项，插件用于防止被其它插件降档）。");
            AnisoLevel = Config.Bind("Texture", "AnisoLevel", 16,
                "把每张贴图的 anisoLevel 提到该值(1~16)。游戏原始贴图多为 1, 斜视时会糊成一片; 16 = 最锐。");
            ForceTrilinear = Config.Bind("Texture", "ForceTrilinear", true,
                "Bilinear 过滤改写为 Trilinear(过渡更顺)。");
            MipMapBias = Config.Bind("Texture", "MipMapBias", 0f,
                "mipMapBias: 负数=更锐(如 -1), 0=不动。过大负值会闪烁, 建议 -0.5 ~ -1。");

            RescanSeconds = Config.Bind("Scan", "RescanSeconds", 0f,
                "每隔多少秒重新扫一遍贴图(新场景/新角色加载后自动补)。0 = 只在场景加载时扫(推荐)。\n" +
                "大于 0 会周期性调用 Resources.FindObjectsOfTypeAll 做全堆枚举, 每次都是一个 GC 尖峰, 会拖累 1% low。");
            PerFrame = Config.Bind("Scan", "PerFrame", 48,
                "分帧扇扫预算: 每帧最多处理多少张贴图。"
                + "设成 48 可以把单帧 15ms 的扫描尖峰摊平到十几帧。");
            Verbose = Config.Bind("Debug", "Verbose", false, "输出每轮扫描统计。");

            GameObject host = new GameObject("VRK_TexPlus_Runtime");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<TexPlusRuntime>();

            Log.LogInfo("[TexPlus] v1.2.0 ready. aniso=" + AnisoLevel.Value
                + " trilinear=" + ForceTrilinear.Value
                + " mipBias=" + MipMapBias.Value
                + " rescan=" + RescanSeconds.Value + "s");
        }
    }

    internal class TexPlusRuntime : MonoBehaviour
    {
        private float _nextScan;
        private int _lastTotal = -1;
        private int _lastChanged;
        // 已经处理过的贴图 instanceID —— 避免每轮扫描对同一张贴图重复写属性(native 调用)
        private readonly System.Collections.Generic.HashSet<int> _done = new System.Collections.Generic.HashSet<int>();
        private int _cfgAniso = -1;
        private bool _cfgTrilinear;
        private float _cfgBias;
        private float[] _burst = null;
        private int _burstIndex;
        private float _burstAt;
        private readonly float[] DefaultBurst = new float[] { 0.5f, 2f, 5f, 10f, 20f };
        // 分帧扫描: 一次枚举之后, 每帧只处理 PerFrame 张贴图, 免得单帧 15ms 尖峰
        private Object[] _pending;
        private int _pendingIndex;
        private string _pendingTag;
        private int _scanTotal;
        private int _scanChanged;

        private void Start()
        {
            try
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
            }
            catch (Exception e)
            {
                TexPlusPlugin.Log.LogWarning("[TexPlus] sceneLoaded hook failed: " + e.Message);
            }
            ApplyGlobals();
            Scan("startup");
            ScheduleBurst(0f);
        }

        private void OnDestroy()
        {
            try
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
            }
            catch
            {
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ScheduleBurst(0.2f);
        }

        private void ScheduleBurst(float firstDelay)
        {
            _burst = DefaultBurst;
            _burstIndex = 0;
            _burstAt = Time.realtimeSinceStartup + firstDelay + _burst[0];
        }

        private void Update()
        {
            // 上一轮扫描还没走完 -> 继续按预算推进(不阻塞帧)
            if (_pending != null)
            {
                ProcessSome(TexPlusPlugin.PerFrame.Value);
                return;
            }

            float now = Time.realtimeSinceStartup;

            // 场景加载后的连续几次补扫(角色/衣物是之后才实例化出来的)
            if (_burst != null)
            {
                if (now >= _burstAt)
                {
                    Scan("scene+" + _burst[_burstIndex].ToString("0.#") + "s");
                    _burstIndex++;
                    if (_burstIndex >= _burst.Length)
                        _burst = null;
                    else
                        _burstAt = now + (_burst[_burstIndex] - _burst[_burstIndex - 1]);
                }
                return;
            }

            float period = TexPlusPlugin.RescanSeconds.Value;
            if (period <= 0.01f)
                return;
            if (now >= _nextScan)
            {
                _nextScan = now + period;
                Scan("periodic");
            }
        }

        private void ApplyGlobals()
        {
            if (!TexPlusPlugin.FullResolution.Value)
                return;
            try
            {
                if (QualitySettings.masterTextureLimit != 0)
                    QualitySettings.masterTextureLimit = 0;
                if (QualitySettings.anisotropicFiltering != AnisotropicFiltering.ForceEnable)
                    QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            }
            catch (Exception e)
            {
                TexPlusPlugin.Log.LogWarning("[TexPlus] global quality apply failed: " + e.Message);
            }
        }

        private void Scan(string tag)
        {
            ApplyGlobals();

            int targetAniso = Mathf.Clamp(TexPlusPlugin.AnisoLevel.Value, 1, 16);
            bool trilinear = TexPlusPlugin.ForceTrilinear.Value;
            float bias = TexPlusPlugin.MipMapBias.Value;
            bool setBias = Mathf.Abs(bias) > 0.001f;

            // 配置变了就重新处理所有贴图, 否则跳过已处理过的(省掉几千次 native setter)
            if (targetAniso != _cfgAniso || trilinear != _cfgTrilinear || !Mathf.Approximately(bias, _cfgBias))
            {
                _cfgAniso = targetAniso;
                _cfgTrilinear = trilinear;
                _cfgBias = bias;
                _done.Clear();
            }

            try
            {
                _pending = Resources.FindObjectsOfTypeAll(typeof(Texture));
            }
            catch (Exception e)
            {
                TexPlusPlugin.Log.LogWarning("[TexPlus] texture enumeration failed: " + e.Message);
                _pending = null;
                return;
            }

            _pendingIndex = 0;
            _pendingTag = tag;
            _scanTotal = 0;
            _scanChanged = 0;

            // 头一帧给一个较大的预算, 让"刚进场景"那批贴图立刻生效
            ProcessSome(Mathf.Max(TexPlusPlugin.PerFrame.Value, 256));
        }

        /// <summary>
        /// 只处理 <paramref name="budget"/> 项就返回, 剩下的由 Update() 下一帧接着做。
        /// </summary>
        private void ProcessSome(int budget)
        {
            if (_pending == null)
                return;
            if (budget < 1)
                budget = 1;

            int targetAniso = _cfgAniso > 0 ? _cfgAniso : 16;
            bool trilinear = _cfgTrilinear;
            float bias = _cfgBias;
            bool setBias = Mathf.Abs(bias) > 0.001f;

            int visited = 0;
            int n = _pending.Length;
            while (_pendingIndex < n && visited < budget)
            {
                Texture t = _pending[_pendingIndex] as Texture;
                _pendingIndex++;
                visited++;
                if (t == null)
                    continue;
                if (t is RenderTexture) // 后处理中间缓冲, 不动
                    continue;
                _scanTotal++;

                int id = t.GetInstanceID();
                if (_done.Contains(id))
                    continue;

                try
                {
                    bool dirty = false;
                    if (t.anisoLevel < targetAniso)
                    {
                        t.anisoLevel = targetAniso;
                        dirty = true;
                    }
                    if (trilinear && t.filterMode == FilterMode.Bilinear)
                    {
                        t.filterMode = FilterMode.Trilinear;
                        dirty = true;
                    }
                    if (setBias && !Mathf.Approximately(t.mipMapBias, bias))
                    {
                        t.mipMapBias = bias;
                        dirty = true;
                    }
                    if (dirty)
                        _scanChanged++;
                    _done.Add(id);
                }
                catch
                {
                    // 个别贴图(例如 GPUSkinning 用到的)不可写, 跳过
                }
            }

            if (_pendingIndex < n)
                return; // 还没扫完, 下帧继续

            bool report = TexPlusPlugin.Verbose.Value || _lastTotal < 0 || _pendingTag == "startup";
            if (report)
            {
                TexPlusPlugin.Log.LogInfo("[TexPlus] scan(" + _pendingTag + "): textures=" + _scanTotal
                    + " upgraded=" + _scanChanged + " aniso<=" + targetAniso
                    + " trilinear=" + trilinear + " mipBias=" + bias);
            }
            _lastTotal = _scanTotal;
            _lastChanged = _scanChanged;
            _pending = null;
        }
    }
}
