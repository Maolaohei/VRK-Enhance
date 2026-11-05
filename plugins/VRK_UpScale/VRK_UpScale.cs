// VRK_UpScale - FSR1 (EASU + RCAS) upscaling / sharpening for VR Kanojo.
//
// Unity 5.4 cannot compile shaders at runtime and has no post-processing hook
// that accepts native shader bytecode, so the actual GPU work happens in the
// native plugin "vrkup.dll" (see native/vrkup.cpp). This managed half only
// hands native RenderTexture pointers over once per frame and never replaces
// the rendered image with anything the native side failed to produce.
//
// Two modes:
//   Sharpen      - RCAS only. Renders at full resolution, sharpens in place.
//                  Zero changes to camera setup, cannot alter what is drawn.
//   RenderScale  - Renders the scene at Scale x resolution into an offscreen
//                  RenderTexture, then EASU-upscales + RCAS-sharpens it onto
//                  the screen. Off by default because it rewires the camera.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRKanojo.UpScale
{
    [BepInPlugin(Guid, "VRK UpScale (FSR1 EASU+RCAS)", "1.7.0")]
    public class UpScalePlugin : BaseUnityPlugin
    {
        public const string Guid = "vrk.upscale";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Mode;
        internal static ConfigEntry<float> Scale;
        internal static ConfigEntry<float> Sharpness;
        internal static ConfigEntry<int> OutputWidth;
        internal static ConfigEntry<float> SanitySeconds;
        internal static ConfigEntry<bool> NativePass;   // v1.6.0
        internal static ConfigEntry<bool> Verbose;
        internal static ConfigEntry<string> StatusKey;

        internal static string NativePath;

        // Device gate state. Filled in by UpScaleRuntime.CheckGate() a few frames
        // after startup, never from Awake (see the note inside Awake).
        internal static bool GateDone;
        internal static bool GateOk;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true,
                "Turn the whole effect on. When false nothing is loaded and rendering is untouched.");
            Mode = Config.Bind("General", "Mode", "Sharpen",
                "Sharpen = RCAS only at native resolution (safe). RenderScale = render below native and upscale via EASU.");
            Sharpness = Config.Bind("General", "Sharpness", 0.2f,
                "RCAS sharpness, 0 = softest, 1 = sharpest. FSR default is 0.2.");
            Scale = Config.Bind("RenderScale", "Scale", 0.75f,
                "Only used by RenderScale mode: fraction of the screen the scene is rendered at (0.25 - 1.0).");
            SanitySeconds = Config.Bind("Safety", "SanitySeconds", 5.0f,
                "每 N 秒用 Graphics.Blit 先把未处理帧写进目标，保证原生层失效时画面也不会空白。0 = 关闭。");
            NativePass = Config.Bind("General", "NativePass", true,
                "false = skip the native FSR pass entirely and just bilinear-blit. Diagnostic switch: "
                + "if the heap-corruption crash disappears with this off, the native pass is the trigger.");
            OutputWidth = Config.Bind("General", "OutputWidth", 0,
                "Only used by RenderScale: target width. 0 = current screen width.");
            Verbose = Config.Bind("Debug", "Verbose", false,
                "Log one line per pipeline state change.");
            StatusKey = Config.Bind("Debug", "StatusKey", "F9",
                "Key that writes a status report to the log. Empty string disables it.");

            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string[] candidates =
            {
                Path.Combine(Path.Combine(Path.Combine(pluginDir, "VRK_UpScale"), "native"), "vrkup.dll"),
                Path.Combine(Path.Combine(pluginDir, "VRK_UpScale"), "vrkup.dll"),
                Path.Combine(Path.Combine(Application.dataPath, "Plugins"), "vrkup.dll")
            };
            for (int i = 0; i < candidates.Length; ++i)
                if (File.Exists(candidates[i]))
                {
                    NativePath = candidates[i];
                    break;
                }
            if (NativePath == null)
            {
                UpScalePlugin.Log.LogError("vrkup.dll not found, looked in: " + string.Join(" | ", candidates));
                Enabled.Value = false;
                return;
            }

            if (!Enabled.Value)
            {
                Log.LogInfo("disabled by config");
                return;
            }

            // ------------------------------------------------------------------
            // NEVER query SystemInfo / VRDevice from Awake.
            //
            // BepInEx creates the manager object (and therefore runs every plugin's
            // Awake) while Unity is still bringing the graphics subsystem up. In
            // Unity 5.4.5f1 the SystemInfo backing singleton is still null at that
            // moment: the managed getter goes through the icall thunk
            //   VR_Kanojo.exe+0x3750A0 -> +0x1402048A0 -> mov 0x11CC(%rax),%eax
            // and the null dereference is an access violation (0xC0000005) before a
            // window even exists - Windows Error Reporting then parks the dead
            // process for ~2 minutes, which is what looks like "未响应".
            //
            // The whole device gate moved to UpScaleRuntime.CheckGate(), which runs
            // a couple of frames later, after the device is guaranteed to be alive.
            // ------------------------------------------------------------------

            if (!NativeUpScale.Load(NativePath))
            {
                Log.LogError("native plugin not usable: " + NativeUpScale.LastError);
                Enabled.Value = false;
                return;
            }

            if (Verbose.Value)
                Log.LogInfo("native v" + NativeUpScale.Version + " loaded from " + NativePath);

            string m = (Mode.Value ?? "").Trim();
            if (!string.Equals(m, "Sharpen", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(m, "RenderScale", StringComparison.OrdinalIgnoreCase))
            {
                Log.LogWarning("unknown Mode '" + m + "', falling back to Sharpen");
                Mode.Value = "Sharpen";
            }

            GameObject go = new GameObject("VRK_UpScale_Runtime");
            DontDestroyOnLoad(go);
            go.AddComponent<UpScaleRuntime>();
        }

        /// <summary>
        /// Device checks that must never run inside Awake. Returns null when the effect
        /// may activate, otherwise a human readable reason. Managed exceptions are caught
        /// here; a native access violation cannot be, which is exactly why this runs a
        /// couple of frames after the engine finished creating the device.
        /// </summary>
        internal static string CheckGate()
        {
            UnityEngine.Rendering.GraphicsDeviceType dev;
            try
            {
                dev = SystemInfo.graphicsDeviceType;
            }
            catch (Exception e)
            {
                return "SystemInfo.graphicsDeviceType threw " + e.GetType().Name;
            }

            if (dev != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
                return "graphics device is " + dev + ", this plugin only supports Direct3D11 - staying inactive";

            // VR 入口启动时(VRKANOJO_VR=1)一律不接管: 立体渲染不能这么后处理。
            if (System.Environment.GetEnvironmentVariable("VRKANOJO_VR") == "1")
                return "launched by the VR entry (VRKANOJO_VR=1); upscaling is flat-mode only, staying inactive";

            // Stereo rendering cannot be post-processed this way: in VR mode Unity renders
            // each eye into its own target and a full-screen pass would corrupt the pair.
            // Flat mode only.
            try
            {
                if (UnityEngine.VR.VRDevice.isPresent)
                    return "a VR device is active; upscaling applies to flat (no-VR) mode only, staying inactive";
            }
            catch (Exception e)
            {
                return "VRDevice.isPresent threw " + e.GetType().Name;
            }

            GateOk = true;
            GateDone = true;
            return null;
        }
    }

    /// <summary>Thin P/Invoke layer over native/vrkup.cpp.</summary>
    internal static class NativeUpScale
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VersionFn();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr StringFn();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StatusFn();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint CountFn();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitFn(int srcW, int srcH, int dstW, int dstH, float sharpness);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RunFn(IntPtr src, IntPtr dst);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void VoidFn();

        private static IntPtr _module = IntPtr.Zero;
        private static VersionFn _version;
        private static StringFn _lastError;
        private static StringFn _deviceName;
        private static StatusFn _status;
        private static CountFn _frames;
        private static CountFn _failFrames;
        private static InitFn _init;
        private static RunFn _run;
        private static VoidFn _shutdown;

        internal static string LastError = "not loaded";
        internal static bool Loaded { get { return _module != IntPtr.Zero && _run != null; } }

        private static T Bind<T>(string name) where T : class
        {
            IntPtr p = GetProcAddress(_module, name);
            if (p == IntPtr.Zero)
            {
                LastError = "missing export " + name;
                return null;
            }
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        internal static bool Load(string path)
        {
            if (_module != IntPtr.Zero)
                return Loaded;

            if (!File.Exists(path))
            {
                LastError = "file not found: " + path;
                return false;
            }

            _module = LoadLibraryW(path);
            if (_module == IntPtr.Zero)
            {
                LastError = "LoadLibrary failed, win32 error " + Marshal.GetLastWin32Error() + " for " + path;
                return false;
            }

            _version = Bind<VersionFn>("VRKU_Version");
            _lastError = Bind<StringFn>("VRKU_LastError");
            _deviceName = Bind<StringFn>("VRKU_DeviceName");
            _status = Bind<StatusFn>("VRKU_Status");
            _frames = Bind<CountFn>("VRKU_Frames");
            _failFrames = Bind<CountFn>("VRKU_FailFrames");
            _init = Bind<InitFn>("VRKU_Init");
            _run = Bind<RunFn>("VRKU_Run");
            _shutdown = Bind<VoidFn>("VRKU_Shutdown");

            if (_version == null || _init == null || _run == null || _status == null)
            {
                LastError = LastError + " (incomplete export set)";
                _module = IntPtr.Zero;
                return false;
            }

            int v = _version();
            if (v < 10200)
            {
                LastError = "unexpected native version " + v;
                _module = IntPtr.Zero;
                return false;
            }

            LastError = "ok";
            return true;
        }

        internal static int Version { get { return _version == null ? 0 : _version(); } }
        internal static int Status { get { return _status == null ? -99 : _status(); } }
        internal static uint Frames { get { return _frames == null ? 0u : _frames(); } }
        internal static uint FailFrames { get { return _failFrames == null ? 0u : _failFrames(); } }

        internal static string NativeError()
        {
            if (_lastError == null)
                return "";
            IntPtr p = _lastError();
            return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p);
        }

        internal static string DeviceName()
        {
            if (_deviceName == null)
                return "";
            IntPtr p = _deviceName();
            return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p);
        }

        internal static int Init(int sw, int sh, int dw, int dh, float sharpness)
        {
            return _init == null ? -99 : _init(sw, sh, dw, dh, sharpness);
        }

        internal static int Run(IntPtr src, IntPtr dst)
        {
            return _run == null ? -99 : _run(src, dst);
        }

        internal static void Shutdown()
        {
            if (_shutdown != null)
                _shutdown();
        }
    }

    internal class UpScaleRuntime : MonoBehaviour
    {
        private Camera _cam;
        private UpscaleEffect _effect;
        private float _nextScan;
        private float _statusAt;
        private KeyCode _statusKey = KeyCode.None;
        private bool _statusKeyRead;
        private bool _modeLogged;

        private void Awake()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            StartCoroutine(CheckGateDeferred());
        }

        /// <summary>
        /// Two frames of delay before touching SystemInfo/VRDevice: by then the engine has
        /// created the graphics device. See the long note in UpScalePlugin.Awake for the
        /// crash this avoids (VR_Kanojo.exe+0x3750A0, 0xC0000005, no window ever appears).
        /// </summary>
        private System.Collections.IEnumerator CheckGateDeferred()
        {
            yield return null;
            yield return null;

            string reason = UpScalePlugin.CheckGate();
            if (reason != null)
            {
                UpScalePlugin.Log.LogWarning(reason);
                UpScalePlugin.Enabled.Value = false;
                yield break;
            }

            if (UpScalePlugin.Verbose != null && UpScalePlugin.Verbose.Value)
                UpScalePlugin.Log.LogInfo("device gate passed (D3D11, no HMD)");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_cam != null)
                _cam.targetTexture = null;
            DestroyPresenter();
            NativeUpScale.Shutdown();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Camera objects are destroyed on load; forget stale references.
            _cam = null;
            _effect = null;
            DestroyPresenter();
        }

        private static bool RenderScaleMode()
        {
            string m = UpScalePlugin.Mode == null ? "" : (UpScalePlugin.Mode.Value ?? "");
            return string.Equals(m.Trim(), "RenderScale", StringComparison.OrdinalIgnoreCase);
        }

        private void Update()
        {
            if (!NativeUpScale.Loaded)
                return;

            // Nothing may touch the camera or the device before CheckGate() has run.
            if (!UpScalePlugin.GateDone || !UpScalePlugin.GateOk)
                return;

            if (!_statusKeyRead)
            {
                _statusKeyRead = true;
                string raw = UpScalePlugin.StatusKey == null ? "" : (UpScalePlugin.StatusKey.Value ?? "");
                raw = raw.Trim();
                if (raw.Length > 0)
                {
                    try
                    {
                        _statusKey = (KeyCode)Enum.Parse(typeof(KeyCode), raw, true);
                    }
                    catch
                    {
                        UpScalePlugin.Log.LogWarning("StatusKey '" + raw + "' is not a KeyCode, status hotkey disabled");
                        _statusKey = KeyCode.None;
                    }
                }
            }

            if (_statusKey != KeyCode.None && Input.GetKeyDown(_statusKey))
                LogStatus();

            if (Time.realtimeSinceStartup < _nextScan)
                return;
            _nextScan = Time.realtimeSinceStartup + 1.0f;

            Camera cam = PickCamera();
            if (cam == null)
                return;

            if (cam != _cam)
            {
                _cam = cam;
                _effect = null;
                if (UpScalePlugin.Verbose.Value)
                    UpScalePlugin.Log.LogInfo("tracking camera '" + cam.name + "' depth=" + cam.depth +
                                              " size=" + cam.pixelWidth + "x" + cam.pixelHeight);
            }

            if (!RenderScaleMode())
            {
                DestroyPresenter();
                if (_cam.targetTexture != null)
                    _cam.targetTexture = null;

                if (_effect == null)
                {
                    _effect = _cam.gameObject.GetComponent<UpscaleEffect>();
                    if (_effect == null)
                        _effect = _cam.gameObject.AddComponent<UpscaleEffect>();
                    if (!_modeLogged)
                    {
                        _modeLogged = true;
                        UpScalePlugin.Log.LogInfo("sharpen mode attached to '" + _cam.name + "'");
                    }
                }
                _effect.Sharpen = UpScalePlugin.Sharpness.Value;
                _effect.Active = true;
            }
            else
            {
                EnsureRenderScale(_cam);
            }
        }

        private Camera _presenterCam;
        private UpscaleEffect _presenterEffect;
        private RenderTexture _small;

        private void EnsureRenderScale(Camera cam)
        {
            int dw = UpScalePlugin.OutputWidth.Value > 0 ? UpScalePlugin.OutputWidth.Value : Screen.width;
            int dh = dw == Screen.width
                         ? Screen.height
                         : Mathf.Max(16, Mathf.RoundToInt(dw * (float)Screen.height / Mathf.Max(1, Screen.width)));
            float scale = Mathf.Clamp(UpScalePlugin.Scale.Value, 0.25f, 1.0f);
            int sw = Mathf.Max(16, Mathf.RoundToInt(dw * scale));
            int sh = Mathf.Max(16, Mathf.RoundToInt(dh * scale));

            if (_small == null || _small.width != sw || _small.height != sh)
            {
                // 不要 Destroy 一个刚被当作渲染目标的贴图(use-after-free 风险),
                // 复用同一个对象: Release -> 改尺寸 -> Create。
                if (_small == null)
                {
                    _small = new RenderTexture(sw, sh, 24, RenderTextureFormat.ARGB32);
                    _small.filterMode = FilterMode.Bilinear;
                    _small.Create();
                }
                else
                {
                    _small.Release();
                    _small.width = sw;
                    _small.height = sh;
                    _small.Create();
                }
                if (UpScalePlugin.Verbose.Value)
                    UpScalePlugin.Log.LogInfo("render target " + sw + "x" + sh + " -> " + dw + "x" + dh);
            }

            if (cam.targetTexture != _small)
                cam.targetTexture = _small;

            // A camera that renders nothing but owns the final blit to the screen.
            if (_presenterCam == null)
            {
                GameObject go = new GameObject("VRK_UpScale_Presenter");
                go.transform.parent = transform;
                _presenterCam = go.AddComponent<Camera>();
                _presenterCam.clearFlags = CameraClearFlags.Nothing;
                _presenterCam.cullingMask = 0;
                _presenterCam.orthographic = true;
                _presenterCam.orthographicSize = 1f;
                _presenterCam.nearClipPlane = 0.01f;
                _presenterCam.farClipPlane = 2f;
                _presenterCam.useOcclusionCulling = false;
                _presenterEffect = go.AddComponent<UpscaleEffect>();
                UpScalePlugin.Log.LogInfo("render-scale mode: " + _small.width + "x" + _small.height +
                                          " -> " + dw + "x" + dh);
            }

            // v1.7.0: 保证 presenter 一定铺满整屏, 且与屏幕同比例 ——
            // 一旦这里被拉伸/留黑边, 屏幕上看到的画面就和"相机正中心"错位,
            // 准星、鼠标点击、凝视交互全都会偏。
            _presenterCam.rect = new Rect(0f, 0f, 1f, 1f);
            _presenterCam.aspect = (float)Mathf.Max(1, dw) / Mathf.Max(1, dh);
            _presenterCam.depth = cam.depth + 10f;
            _presenterCam.enabled = true;
            _presenterEffect.Active = true;
            _presenterEffect.Sharpen = UpScalePlugin.Sharpness.Value;
            _presenterEffect.Source = _small;
        }

        private void DestroyPresenter()
        {
            // 不销毁相机物体: 场景切换时销毁正在出图的相机同样是 use-after-free 风险。
            // 只把它停用, 场景回来时再启用。
            if (_presenterCam != null)
            {
                _presenterCam.targetTexture = null;
                _presenterCam.enabled = false;
                if (_presenterEffect != null) _presenterEffect.Active = false;
            }
        }

        private Camera PickCamera()
        {
            Camera main = Camera.main;
            if (main != null && main.enabled && main.gameObject.activeInHierarchy && main.cullingMask != 0)
                return main;

            Camera best = null;
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; ++i)
            {
                Camera c = all[i];
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy)
                    continue;
                if (c.cullingMask == 0)
                    continue;
                if (c.name != null && c.name.StartsWith("VRK_UpScale", StringComparison.Ordinal))
                    continue;
                if (best == null || c.depth > best.depth)
                    best = c;
            }
            return best;
        }

        private void LogStatus()
        {
            if (Time.realtimeSinceStartup < _statusAt)
                return;
            _statusAt = Time.realtimeSinceStartup + 0.5f;

            UpScalePlugin.Log.LogInfo(
                "status: native v" + NativeUpScale.Version +
                " state=" + NativeUpScale.Status +
                " frames=" + NativeUpScale.Frames +
                " fails=" + NativeUpScale.FailFrames +
                " mode=" + UpScalePlugin.Mode.Value +
                " sharpness=" + UpScalePlugin.Sharpness.Value.ToString("0.00") +
                " device='" + NativeUpScale.DeviceName() + "'" +
                " error='" + NativeUpScale.NativeError() + "'");
        }
    }

    /// <summary>
    /// Post-resolve hook. Never draws anything itself - it either lets the native
    /// plugin write the destination, or falls back to a plain copy.
    /// </summary>
    internal class UpscaleEffect : MonoBehaviour
    {
        internal bool Active;
        internal float Sharpen = 0.2f;
        internal RenderTexture Source; // set only in RenderScale mode

        // Persistent offscreen target. Unity's GetTemporary pool can hand back a texture
        // whose native pointer is still zero, which used to abort the native pass and -
        // after 600 nulls - disable upscaling for the rest of the session. One texture we
        // own and Create() ourselves keeps the pointer stable.
        private RenderTexture _target;
        private int _targetW;
        private int _targetH;
        private RenderTextureFormat _targetFmt;

        private int _lastW, _lastH, _lastDw, _lastDh;
        private bool _loggedFirstFrame;
        private bool _initFailed;
        private int _failCount;
        private int _nullPtr;

        private bool _loggedEntry;

        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            // First-frame fingerprint. Unity is allowed to hand us a null destination
            // (backbuffer), and an effect that returns without drawing leaves the screen
            // with nothing but the camera clear colour - so record exactly what we get.
            if (!_loggedEntry)
            {
                _loggedEntry = true;
                UpScalePlugin.Log.LogInfo(
                    "OnRenderImage entered: src=" + (src == null ? "null" : src.width + "x" + src.height + "/" + src.format) +
                    " dest=" + (dest == null ? "null(backbuffer)" : dest.width + "x" + dest.height + "/" + dest.format) +
                    " active=" + Active + " loaded=" + NativeUpScale.Loaded + " initFailed=" + _initFailed +
                    " sanity=" + UpScalePlugin.SanitySeconds.Value);
            }

            // Unity hands the LAST image effect a null destination (= the backbuffer).
            // The native layer can only write into a real RTV, so give it one: render
            // into a temporary texture and blit that to the backbuffer afterwards.
            RenderTexture temp = null;
            RenderTexture target = dest;
            if (target == null)
            {
                int tw = Source != null ? Screen.width : (src != null ? src.width : Screen.width);
                int th = Source != null ? Screen.height : (src != null ? src.height : Screen.height);
                if (tw <= 0 || th <= 0)
                {
                    Graphics.Blit(src, (RenderTexture)null);
                    return;
                }
                target = EnsureTarget(tw, th, src != null ? src.format : RenderTextureFormat.ARGB32);
                temp = target;
            }

            bool drew = false;
            try
            {
                drew = RenderInto(src, target);
            }
            catch (Exception e)
            {
                if (_failCount++ < 3)
                    UpScalePlugin.Log.LogWarning("render threw: " + e.GetType().Name + ": " + e.Message);
                drew = false;
            }

            if (temp != null)
            {
                Graphics.Blit(drew ? (Texture)temp : src, (RenderTexture)null);
            }
        }

        /// <summary>
        /// Writes one frame into <paramref name="dest"/> (never null here).
        /// Returns true only when the native pipeline produced the picture;
        /// false means the caller should present the unprocessed source.
        /// </summary>
        private RenderTexture EnsureTarget(int w, int h, RenderTextureFormat fmt)
        {
            // Normalise the format: Unity hands ARGB32 for the backbuffer, and a differing
            // src.format used to force a destroy+recreate in the middle of OnRenderImage.
            // Destroying a texture that may still be bound is a use-after-free in the
            // renderer, so keep ONE object for the whole session and just Release()/Create()
            // it when the size changes.
            if (fmt != RenderTextureFormat.ARGB32 && fmt != RenderTextureFormat.Default)
                fmt = RenderTextureFormat.ARGB32;

            if (_target != null && _targetFmt != fmt)
            {
                // Format can only be changed by rebuilding the texture once.
                _target.Release();
                UnityEngine.Object.Destroy(_target);
                _target = null;
            }

            if (_target == null)
            {
                _target = new RenderTexture(w, h, 0, fmt);
                _target.name = "VRK_UpScale_Target";
                _target.filterMode = FilterMode.Point;
                _target.wrapMode = TextureWrapMode.Clamp;
                _targetFmt = fmt;
                _targetW = w;
                _targetH = h;
                _target.Create();
                UpScalePlugin.Log.LogInfo("offscreen target " + w + "x" + h + " " + fmt +
                                          " created=" + _target.IsCreated());
            }
            else if (_targetW != w || _targetH != h || !_target.IsCreated())
            {
                _target.Release();
                _target.width = w;
                _target.height = h;
                _target.Create();
                _targetW = w;
                _targetH = h;
                UpScalePlugin.Log.LogInfo("offscreen target resized to " + w + "x" + h +
                                          " created=" + _target.IsCreated());
            }
            return _target;
        }

        private void OnDestroy()
        {
            // 场景切换时 Unity 会销毁挂 OnRenderImage 的物体; 这里只释放 GPU 资源,
            // 不 Destroy 贴图对象 —— 在渲染链尾部销毁刚被当作目标的贴图是
            // c0000374(堆损坏) 的高危路径, 交给 GC 收尾更安全。
            if (_target != null)
            {
                _target.Release();
                _target = null;
            }
        }

        private bool RenderInto(RenderTexture src, RenderTexture dest)
        {
            if (!UpScalePlugin.NativePass.Value)
            {
                // Diagnostic path: no native code at all, plain bilinear upscale.
                Graphics.Blit(Source != null ? (Texture)Source : src, dest);
                return true;
            }

            if (!Active || !NativeUpScale.Loaded || _initFailed)
                return false;

            RenderTexture input = Source != null ? Source : src;
            if (input == null)
                return false;

            int sw = input.width, sh = input.height;
            int dw = dest.width, dh = dest.height;

            if (sw != _lastW || sh != _lastH || dw != _lastDw || dh != _lastDh)
            {
                int rc0 = NativeUpScale.Init(sw, sh, dw, dh, Sharpen);
                _lastW = sw;
                _lastH = sh;
                _lastDw = dw;
                _lastDh = dh;
                if (rc0 != 0)
                {
                    _initFailed = true;
                    UpScalePlugin.Log.LogError("VRKU_Init(" + sw + "x" + sh + " -> " + dw + "x" + dh +
                                               ") failed: " + rc0 + " " + NativeUpScale.NativeError());
                    return false;
                }
                if (UpScalePlugin.Verbose.Value)
                    UpScalePlugin.Log.LogInfo("pipeline " + sw + "x" + sh + " -> " + dw + "x" + dh);
            }

            System.IntPtr srcPtr = input.GetNativeTexturePtr();
            System.IntPtr dstPtr = dest.GetNativeTexturePtr();
            if (dstPtr == System.IntPtr.Zero && dest != null && !dest.IsCreated())
            {
                dest.Create();
                dstPtr = dest.GetNativeTexturePtr();
            }
            if (srcPtr == System.IntPtr.Zero || dstPtr == System.IntPtr.Zero)
            {
                _nullPtr++;
                if (_nullPtr == 1 || (_nullPtr % 600) == 0)
                    UpScalePlugin.Log.LogWarning("native texture pointer is null (src=" + (srcPtr == System.IntPtr.Zero ? "null" : "ok")
                        + " dst=" + (dstPtr == System.IntPtr.Zero ? "null" : "ok") + "), skipping native pass #" + _nullPtr);
                if (_nullPtr > 600)
                {
                    // Keep trying: the pointer comes back as soon as the resource is
                    // realised, and a permanent give-up is what made the effect silently
                    // do nothing for the rest of the session.
                    _nullPtr = 0;
                    UpScalePlugin.Log.LogWarning("native pointer still null after 600 frames, resetting the retry window");
                }
                return false;
            }

            int rc = NativeUpScale.Run(srcPtr, dstPtr);
            if (rc == 0)
            {
                if (!_loggedFirstFrame)
                {
                    _loggedFirstFrame = true;
                    UpScalePlugin.Log.LogInfo(
                        "native pass is live: " + sw + "x" + sh + " -> " + dw + "x" + dh +
                        " (native v" + NativeUpScale.Version + ", " +
                        (sw == dw && sh == dh ? "sharpen only" : "EASU+RCAS") +
                        (dest == null ? "" : ", owned target") + ")");
                }
                return true;
            }

            if (_failCount == 0 || (_failCount % 600) == 0)
                UpScalePlugin.Log.LogWarning("VRKU_Run failed (" + rc + "): " + NativeUpScale.NativeError() +
                                             ", drawing unprocessed frame");
            _failCount++;
            return false;
        }
    }
}
