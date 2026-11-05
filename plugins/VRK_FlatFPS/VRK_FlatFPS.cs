// VRK_FlatFPS — VR Kanojo 免VR(平面模式)修复 + FPS 键位
// 目标: Unity 5.4.5f1 / .NET 3.5 (mscorlib 2.0) / BepInEx 5.4.12 / HarmonyX 2.x
//
// 解决两个问题:
//  1) 白屏: 旧插件 VRK_PlayWithoutVR 在 LogoScene.Start 后对 OVRCameraRig 做
//     SetActive(false)/SetActive(true)，Unity 会停止该层级上所有协程，
//     导致 VRCameraFade.BeginFade 协程被杀死 -> m_IsFading 永远为 true、
//     白色 Fade 遮罩永远停在 alpha=1 -> 进入即白屏且 Logo 场景永远不推进。
//     本插件不再做任何 SetActive 切换，并加一个看门狗主动修复卡死的淡入淡出。
//  2) 键位: 把 IllusionCamera.KeyInput_Value 收到的方向键改成 WASD，
//     同时保留鼠标看视角(可选无按键自由视角)。

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRKanojo;
using VRKanojo.Utils;
using Object = UnityEngine.Object;

namespace VRKanojo.FlatFPS
{
    [BepInPlugin(Guid, "VRK Flat Mode + FPS Keys", "1.41.0")]
    public class FlatFpsPlugin : BaseUnityPlugin
    {
        /// <summary>
        /// VR \u6a21\u5f0f\u5f00\u5173: \u7531\u542f\u52a8\u5668\u8bbe\u7f6e\u73af\u5883\u53d8\u91cf VRKANOJO_VR=1\u3002
        /// \u771f VR \u65f6\u672c\u63d2\u4ef6\u53ea\u4fdd\u7559\u767d\u5c4f\u770b\u95e8\u72d7\u4e0e\u8bca\u65ad\u70ed\u952e,
        /// \u5f3a\u5236\u514d VR\u3001\u773c\u9ad8\u3001\u7b2c\u4e00\u4eba\u79f0\u3001\u81ea\u7531\u89c6\u89d2\u3001\u9f20\u6807 UI \u6865\u7b49\u5168\u90e8\u4e0d\u751f\u6548\u3002
        /// \u8bfb\u73af\u5883\u53d8\u91cf\u4e0d\u4f1a\u89e6\u53d1 Unity \u5f15\u64ce\u8c03\u7528, Awake \u91cc\u8bfb\u4e5f\u5b89\u5168\u3002
        /// </summary>
        internal static readonly bool VrMode =
            Environment.GetEnvironmentVariable("VRKANOJO_VR") == "1";

        public const string Guid = "vrk.flatfps";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ForceFlatMode;
        internal static ConfigEntry<bool> LockCursor;
        internal static ConfigEntry<string> CursorHoldKey;
        internal static ConfigEntry<bool> RepairStuckFade;
        internal static ConfigEntry<float> StuckFadeSeconds;
        internal static ConfigEntry<float> CoveredSceneSeconds;
        internal static ConfigEntry<bool> FreeLook;
        internal static ConfigEntry<int> MouseSpeed;
        internal static ConfigEntry<bool> InvertY;
        internal static ConfigEntry<bool> UseFpsKeys;
        internal static ConfigEntry<bool> HelpShowOnStart;
        internal static ConfigEntry<string> HelpHotkey;
        internal static ConfigEntry<float> HelpScale;
        internal static ConfigEntry<float> HelpAutoHideSeconds;
        internal static ConfigEntry<bool> Verbose;
        internal static ConfigEntry<bool> SaveFrames;
        internal static ConfigEntry<int> MaxShots;
        internal static ConfigEntry<float> HardKillSeconds;
        internal static ConfigEntry<bool> ClearPanelOnAwake;
        internal static ConfigEntry<bool> MenuSceneCameraFix;
        internal static ConfigEntry<bool> EyeHeightEnabled;
        internal static ConfigEntry<float> EyeHeight;
        internal static ConfigEntry<bool> FlatUiInput;
        internal static ConfigEntry<bool> MenuCursorLock;
        internal static ConfigEntry<bool> DirectMenuClick;
        internal static ConfigEntry<bool> ClickDebug;
        internal static ConfigEntry<string> GameScenes;
        internal static ConfigEntry<float> FrameGapSeconds;
        internal static ConfigEntry<bool> HideGazeRing;
        internal static ConfigEntry<bool> Crosshair;             // v1.19.0 中心准星
        internal static ConfigEntry<int> CrosshairSize;
        internal static ConfigEntry<float> CrosshairAlpha;
        internal static ConfigEntry<bool> CrosshairFirstPersonOnly;
        internal static ConfigEntry<bool> AlwaysShowGazeRing;    // v1.20.0
        internal static ConfigEntry<bool> LegacyUiBridge;        // v1.21.0
        internal static ConfigEntry<bool> InteractionLog;        // v1.21.0
        internal static ConfigEntry<string> InteractKey;         // v1.23.0 P0: E 键交互
        internal static ConfigEntry<float> LookDistance;         // v1.23.0 P0: 注视/触摸判定距离
        internal static ConfigEntry<bool> HoverReticle;          // v1.23.0 P1/P2: 命中可交互物时的准星反馈
        internal static ConfigEntry<float> GazeGrabDistance;
        internal static ConfigEntry<bool> ForceNonController;      // v1.26.0: 把 controlType 压成 0(键鼠模式), 否则游戏的凝视触摸整条链是关闭的
        internal static ConfigEntry<bool> ForceEnableGaze;         // v1.26.0: 把 BodyTouchManager.enableGaze 打开
        internal static ConfigEntry<bool> MouseHandGrab;           // v1.30.0: 左键当手抓可抓取物
        internal static ConfigEntry<bool> DisableIme;              // v1.30.0: 摘掉输入法, WASD 不再被吞
        internal static ConfigEntry<bool> MouseHandTeleport;       // v1.31.0: 按住左键把手瞬移到准星命中点
        internal static ConfigEntry<float> MouseHandReach;         // v1.31.0: 瞬移射线长度
        internal static ConfigEntry<bool> MouseBodyCaress;
        internal static ConfigEntry<bool> MouseHandLog;
        internal static ConfigEntry<bool> ClickProc;
        internal static ConfigEntry<bool> ClickVRItem;              // v1.37.0: 优先用 VRInteractiveItem.OnClick 驱动交互                 // v1.36.0: 左键兜底调用游戏触摸反应 InteractiveProc               // v1.35.0: 打印手部瞄准/抓取细节          // v1.32.0: 左键走游戏的凝视交互(蓝光部位)
        internal static ConfigEntry<bool> GazeAimLog;               // v1.33.0: 打「准星命中 vs 游戏相机命中」的对照日志
        internal static ConfigEntry<string> UiPointerSpace;          // v1.34.0: uGUI 指针空间 Auto/Screen/Render
        internal static ConfigEntry<bool> FirstPersonRotateRig;
        internal static ConfigEntry<bool> SyncGameGazeCamera;                       // v1.38.0: 把游戏的凝视射线相机对齐到真实眼位
        internal static ConfigEntry<float> GazeLookDistance;                        // v1.38.0: 游戏"注视判定"的距离    // v1.27.0: 第一人称把 yaw 转到 rig 上, 让游戏菜单/UI 的朝向跟着视角     // v1.24.0: 注视触摸的抓取距离(游戏里硬编码 1.5m)
        internal static ConfigEntry<float> GazeRingDistance;
        internal static ConfigEntry<string> GazeRingMode;        // v1.22.0: Aim = 吸附到命中点, Center = 固定屏幕中心
        internal static ConfigEntry<float> GazeRingMaxDistance;  // v1.22.0
        internal static ConfigEntry<bool> GazeRingHideWhenNoHit; // v1.25.0: 没打到东西就不显示(否则会像个钉在画面中央的假准星)
        internal static ConfigEntry<bool> GazeRingLog;           // v1.22.0
        internal static ConfigEntry<string> DumpKeyRaw;
        internal static ConfigEntry<bool> CenterProbe;
        internal static ConfigEntry<bool> PhysicsClick;
        internal static ConfigEntry<bool> ClickInGame;
        internal static ConfigEntry<bool> ViewToggle;
        internal static ConfigEntry<string> ViewToggleKey;
        internal static ConfigEntry<float> FirstPersonDistance;
        internal static ConfigEntry<float> ThirdPersonDistance;
        internal static ConfigEntry<float> FreeMoveSpeed;
        internal static ConfigEntry<float> FreeSprintMultiplier;
        internal static ConfigEntry<float> FirstPersonMoveSpeed;
        internal static ConfigEntry<bool> KeepOvrInput;
        internal static ConfigEntry<float> ProbeDelay;

        private static HashSet<string> _sceneSet;
        private static string _sceneSetRaw;

        /// <summary>只有白名单里的场景才让 IllusionCamera 驱动相机。</summary>
        internal static bool IsGameScene(string scene)
        {
            if (string.IsNullOrEmpty(scene))
                return false;
            return GameSceneSet().Contains(scene);
        }

        internal static HashSet<string> GameSceneSet()
        {
            string raw = GameScenes != null ? GameScenes.Value : "";
            if (_sceneSet == null || raw != _sceneSetRaw)
            {
                _sceneSetRaw = raw;
                HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string part in raw.Split(new char[] { ',', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    set.Add(part.Trim());
                _sceneSet = set;
            }
            return _sceneSet;
        }

        internal static bool InteractHeld()
        {
        if (VrMode)
            return false;
        KeyCode k = InteractKeyCache.Get();
        return k != KeyCode.None && Input.GetKey(k);
    }

        // v1.24.0: Transpiler 注入点 —— CheckGazeTouch 里原本硬编码的 1.5f 抓取距离改读这个字段。
        //          (用 ldsfld 读静态字段，比 call 属性 getter 少一层 ABI 依赖)
        public static float GazeGrabRuntime = 1.5f;

        internal static void SyncGazeGrab()
        {
            float v = 1.5f;
            if (!VrMode)
            {
                ConfigEntry<float> c = GazeGrabDistance;
                if (c != null)
                    v = Mathf.Clamp(c.Value, 1.0f, 10.0f);
            }
            GazeGrabRuntime = v;
        }

        private void Awake()
        {
            Log = Logger;

            ForceFlatMode = Config.Bind("General", "ForceFlatMode", true,
                "在 InitScene2.CheckHMD 直接返回 true 并把 GlobalVRKanojo.ForceMouse_ 置真，不依赖 VRDevice。");
            LockCursor = Config.Bind("General", "LockCursor", true,
                "锁定鼠标指针(VR 平面模式用中心准星选 UI)。按住 CursorHoldKey 可临时释放。");
            CursorHoldKey = Config.Bind("General", "CursorHoldKey", "LeftAlt,RightAlt",
                "按住即释放鼠标指针的按键(KeyCode 名称, 逗号分隔多个)。松手自动重新锁定。留空=永不释放。");

            RepairStuckFade = Config.Bind("Fade", "RepairStuckFade", true,
                "看门狗: 淡入淡出卡死时强制清屏(修复白屏)。");
            StuckFadeSeconds = Config.Bind("Fade", "StuckFadeSeconds", 2.0f,
                "IsFading=true 且 Alpha 连续N秒不变 -> 判定协程已死, 强制清除遮罩。");
            CoveredSceneSeconds = Config.Bind("Fade", "CoveredSceneSeconds", 8.0f,
                "Alpha 全白/全不透明且同一场景停留N秒 -> 判定卡在遮罩后, 强制清除。");

            FreeLook = Config.Bind("Camera", "FreeLook", true,
                "无鼠标按键时直接用鼠标移动视角(FPS 习惯)。关掉则维持原版:按住左键拖动才转视角。");
            MouseSpeed = Config.Bind("Camera", "MouseSpeed", 800,
                "鼠标速度(类似 DPI 的数值量级, 越大越快)。"
                + "500 ≈ 每 100 像素转 10°, 800 ≈ 16°, 1200 ≈ 24°。"
                + "原版的 0.12 实际只有约 1.2°/100px, 点一下手的角度都不够。");
            InvertY = Config.Bind("Camera", "InvertY", false, "反转纵向视角。");

            UseFpsKeys = Config.Bind("Keys", "UseFpsKeys", true,
                "方向键 -> WASD: Up=W Down=S Left=A Right=D。小键盘(旋转/升降)与括号/FOV 键不变。");

            HelpShowOnStart = Config.Bind("Help", "ShowOnStart", true,
                "进入游戏(离开初始化/Logo 场景)后自动显示右上角操作指南。任何时候按 Hotkey 可显示/隐藏。");
            HelpAutoHideSeconds = Config.Bind("Help", "AutoHideSeconds", 5.0f,
                "自动显示后多少秒自动隐藏。0 = 一直显示(等同 ShowOnStart+不隐藏)。");
            HelpHotkey = Config.Bind("Help", "Hotkey", "F7",
                "显示/隐藏操作指南的按键(KeyCode 名称, 如 F7 / H / F1)。留空 = 禁用。");
            HelpScale = Config.Bind("Help", "Scale", 1.0f,
                "操作指南缩放(0.6 ~ 2.0)。默认字号按分辨率自动换算, 面板宽度随文字自适应。");

            Verbose = Config.Bind("Debug", "Verbose", false, "输出详细诊断日志。");
            HardKillSeconds = Config.Bind("Fade", "HardKillSeconds", 3.0f,
                "同一遮罩被修复后仍在N秒内重新盖住屏幕 -> 直接停用整个 Fade 面板(彻底根治白屏)。0=不停用。");
            ClearPanelOnAwake = Config.Bind("Fade", "ClearPanelOnAwake", true,
                "VRCameraFade.Awake 后把遮罩面板 alpha 清零(面板序列化颜色是纯白 alpha=1, 而 FadeInOnStart/FadeInOnSceneLoad 都是 false -> 不擦就永久白屏)。");
            MenuSceneCameraFix = Config.Bind("Camera", "MenuSceneCameraFix", true,
                "菜单/加载场景里冻结 IllusionCamera 并把相机复位到头部原点。免VR 时 IllusionCamera 每帧 transform.position = CalcPos()（focus 前方 distance=1.5 米），而 Title 等菜单是绕着玩家原点摆的 VR 界面 -> 相机一移走，菜单就整片出画，只剩背景渐变。");
            GameScenes = Config.Bind("Camera", "GameScenes",
                "Interactive,DressRoom,TrueLesson,Route,InteractiveRoute,VR_Houshi,VR_Sonyu,kiss,aibu,3rd,MainTutorial,Ending,beforeAibu,beforeHoushi,beforeSonyu,beforeKiss,PhotoRoom,CharaViewer,VK_Adv,VK_Adv_Bathroom,BathRoom",
                "只有这些场景才允许 IllusionCamera 移动相机；其余场景（Init/Init2/Logo/Logo2/Attention/Title/VK_Adv? 等）一律按菜单处理。逗号分隔。");
            FrameGapSeconds = Config.Bind("Diagnostics", "FrameGapSeconds", 1.5f,
                "单帧耗时超过该秒数就记一条 long frame 日志，用来定位“长时间未响应”卡在哪一帧。0 = 关闭。");
            Crosshair = Config.Bind("Visual", "Crosshair", true,
                "屏幕中心准星(从 旧版 Play Without VR 插件里拿回来的图标: 红色圆环 + 四个刻度\u3002"
                + "\n开关影响免VR 模式; VR 模式下由头显准星代替\u3002");
            CrosshairSize = Config.Bind("Visual", "CrosshairSize", 56,
                "\u51c6星直径(\u50cf\u7d20, \u6309 1440p \u4e3a\u57fa\u51c6\u968f\u5206\u8fa8\u7387\u7f29\u653e): 40 = \u5c0f, 56 = \u9002\u4e2d, 80 = \u5927\u3002");
            CrosshairAlpha = Config.Bind("Visual", "CrosshairAlpha", 0.85f,
                "\u51c6\u661f\u4e0d\u900f\u660e\u5ea6 0-1\u3002");
            CrosshairFirstPersonOnly = Config.Bind("Visual", "CrosshairFirstPersonOnly", false,
                "true = \u53ea\u5728\u7b2c\u4e00\u4eba\u79f0\u663e\u793a\u51c6\u661f; false = \u7b2c\u4e09\u4eba\u79f0/\u81ea\u7531\u89c6\u89d2\u4e5f\u663e\u793a\u3002");
            LegacyUiBridge = Config.Bind("Input", "LegacyUiBridge", false,
                "v1.21.0 \u9ed8\u8ba4\u5173\u95ed\u3002\u5f00\u542f\u65f6\u4f1a\u5728\u83dc\u5355\u573a\u666f\u5efa\u4e00\u5957 EventSystem/GraphicRaycaster \u6865(\u65e7 v1.8.0 \u65b9\u6848)\u3002"
                + "\n\u76f4\u8fde\u70b9\u51fb(v1.9.0) \u4e0e\u7269\u7406\u70b9\u51fb(v1.13.0) \u5df2\u7ecf\u4e0d\u4f9d\u8d56\u5b83, \u800c\u8fd9\u5957\u6865\u4f1a\u8ba9 EventSystem.current \u88ab\u6211\u4eec\u63a5\u7ba1, \u53ef\u80fd\u5e72\u6270\u6e38\u620f\u81ea\u5df1\u7684\u60ac\u505c/\u70b9\u51fb\u5224\u5b9a\u3002");
            InteractKey = Config.Bind("Input", "InteractKey", "E",
                "v1.23.0 \u4ea4\u4e92\u952e\u3002\u6309\u4f4f\u5b83 = \u6e38\u620f\u539f\u751f\u7684\u201c\u51b3\u5b9a/\u89e6\u6478\u201d(\u7b49\u4ef7\u4e8e\u6309\u4f4f\u9f20\u6807\u5de6\u952e), \u4f46\u4e0d\u4f1a\u50cf\u5de6\u952e\u90a3\u6837\u540c\u65f6\u62d6\u52a8\u955c\u5934\u3002"
                + "\n\u7a7a\u5b57\u7b26\u4e32 = \u5173\u95ed\u3002\u53ef\u586b E / F / Q / Space \u7b49 KeyCode \u540d\u3002");
            GazeGrabDistance = Config.Bind("Input", "GazeGrabDistance", 2.5f,
                "v1.24.0 注视触摸的抓取距离。游戏在 BodyTouchManager.CheckGazeTouch 里硬编码了 1.5 米:"
                + "\n相机离女主超过 1.5 米就抓不到(第三人称恰好卡在边界), 所以改成可调。"
                + "\n默认 2.5 米, 合理范围 1~10。");
            ForceNonController = Config.Bind("Input", "ForceNonController", true,
                "v1.26.0 游戏里 controlType==0 才代表键鼠模式. 现在是 1(VR 手柄), 于是"
                + " BodyTouchManager.Update_GazeCaress 与 CheckGazeTouch 整条凝视触摸链都被跳过, "
                + "按 E 也没反应. 打开后插件在每次进入场景时把 controlType 写成 0.");
            ForceEnableGaze = Config.Bind("Input", "ForceEnableGaze", true,
                "v1.26.0 BodyTouchManager 上的 enableGaze 是场景里序列化的开关, 默认关着. "
                + "打开后插件每进一个场景就把它置 true, 让凝视触摸真正生效.");
            MouseHandGrab = Config.Bind("Input", "MouseHandGrab", true,
                "免VR 时把左键当成右手: 射线命中可抓取物就 GrabBegin, 松开 ForceRelease。");
            DisableIme = Config.Bind("Input", "DisableIme", true,
                "把游戏窗口的输入法上下文摘掉, 中文输入法不再吃掉 WASD。");
            MouseHandTeleport = Config.Bind("Input", "MouseHandTeleport", true,
                "\u6309\u4f4f\u5de6\u952e\u65f6\u628a\u6e38\u620f\u7684 Hand \u77ac\u79fb\u5230\u51c6\u661f\u547d\u4e2d\u70b9, "
                + "\u8ba9\u624b\u90e8\u78b0\u649e\u4f53\u771f\u6b63\u538b\u4e0a\u53bb(HandPad \u8fd9\u7c7b\u9760 OnTriggerEnter \u7684\u624d\u80fd\u54cd\u5e94)\u3002");
            MouseHandReach = Config.Bind("Input", "MouseHandReach", 5f,
                "\u9f20\u6807\u5f53\u624b\u65f6\u7684\u5c04\u7ebf\u957f\u5ea6(\u7c73)\u3002");
            MouseBodyCaress = Config.Bind("Input", "MouseBodyCaress", true,
                "\u5de6\u952e\u76f4\u63a5\u8c03\u6e38\u620f\u81ea\u5e26\u7684\u51dd\u89c6\u4ea4\u4e92"
                + "(BodyTouchManager.CheckGazeTouch)\u3002\u5973\u4e3b\u8eab\u4e0a\u53d1\u5149\u7684\u90e8\u4f4d"
                + "\u53ea\u80fd\u8d70\u8fd9\u6761\u8def\u624d\u4f1a\u771f\u6b63\u88ab\u6293\u4f4f\u3002");
            MouseHandLog = Config.Bind("Input", "MouseHandLog", false,
                "\u6253\u5370\u9f20\u6807\u624b\u7784\u51c6/\u6293\u53d6\u7684\u8be6\u7ec6\u65e5\u5fd7\uff08\u8c03\u8bd5\u7528\uff09\u3002");
            ClickProc = Config.Bind("Input", "ClickProc", true,
                "左键命中后, 若对象不是 uGUI, 则调用游戏的 "
                + "InteractiveProc(碰撞体名) —— 等价于 VR 手贴上去的反应。");
            ClickVRItem = Config.Bind("Input", "ClickVRItem", false,
                "左键命中 VRInteractiveItem 时直接触发它的 Down/Click/Up 事件(游戏图标都走这条)。");
            GazeAimLog = Config.Bind("Diagnostics", "GazeAimLog", true,
                "\u6253\u5370\u300c\u51c6\u661f\u547d\u4e2d\u70b9 vs \u6e38\u620f\u76f8\u673a\u547d\u4e2d\u70b9\u300d"
                + "\u7684\u5bf9\u7167\u65e5\u5fd7, \u7528\u4e8e\u786e\u8ba4\u89e6\u53d1\u70b9\u504f\u79fb\u3002");
            UiPointerSpace = Config.Bind("Input", "UiPointerSpace", "Auto",
                "uGUI \u6307\u9488\u5750\u6807\u7a7a\u95f4: Auto / Screen / Render\u3002"
                + "RenderScale(\u6e32\u67d3\u7f29\u653e) \u4e0b\u76f8\u673a\u6e32\u67d3\u5230\u66f4\u5c0f\u7684\u79bb\u5c4f\u76ee\u6807, "
                + "\u6240\u6709 ScreenPointToRay / GraphicRaycaster \u90fd\u6309\u76ee\u6807\u50cf\u7d20\u8ba1\u7b97\u3002"
                + "Auto = \u753b\u9762\u4e0d\u662f ScreenSpaceOverlay \u65f6\u7528 Render \u7a7a\u95f4\u3002");
            FirstPersonRotateRig = Config.Bind("Camera", "FirstPersonRotateRig", true,
                "v1.27.0 第一人称把左右转向写到 CameraRig 根节点(而不是相机局部), 这样游戏按 rig 朝向摆的菜单/UI "
                + "会跟着你的视角走; 上下俯仰仍留在相机上, 菜单不会被掰歪.");
            LookDistance = Config.Bind("Camera", "LookDistance", 3.5f,
                "v1.23.0 \u6e38\u620f\u81ea\u5e26\u7684\u201c\u6ce8\u89c6/\u89e6\u6478\u5224\u5b9a\u201d\u539f\u672c\u53ea\u6709 2 \u7c73(PlayerLookingCheck.raycastDis)\u3002"
                + "\n\u7b2c\u4e09\u4eba\u79f0\u4e0b\u76f8\u673a\u9000\u5230\u89d2\u8272\u8eab\u540e 1.5 \u7c73, 2 \u7c73\u5c31\u591f\u4e0d\u5230\u4eba, \u8fd9\u91cc\u653e\u5bbd\u5230 3.5 \u7c73(2~10 \u53ef\u8c03)\u3002");
            HoverReticle = Config.Bind("Visual", "HoverReticle", true,
                "v1.23.0 \u51c6\u661f\u5bf9\u51c6\u53ef\u4ea4\u4e92\u7269(\u5973\u4e3b\u8eab\u4f53/\u53ef\u78b0\u9053\u5177)\u65f6, \u5c4f\u5e55\u4e2d\u5fc3\u753b\u56db\u89d2\u65b9\u6846 + \u201cE \u4ea4\u4e92\u201d\u63d0\u793a\u3002"
                + "\n\u5149\u73af(GazeRingMode=Aim)\u4f1a\u540c\u65f6\u653e\u5927 20%\u3002\u4e0d\u559c\u6b22\u6539 false\u3002");
            InteractionLog = Config.Bind("Diagnostics", "InteractionLog", false,
                "v1.21.0 \u6b63\u7247\u573a\u666f\u91cc\u6bcf\u6b21\u5de6\u952e\u90fd\u8bb0\u4e00\u884c: \u5c04\u7ebf\u547d\u4e2d\u4e86\u8c01/\u547d\u4e2d\u7269\u4f53\u4e0a\u6709\u54ea\u4e9b\u7ec4\u4ef6/EventSystem \u72b6\u6001\u3002"
                + "\n\u7528\u6765\u5b9a\u4f4d\u201c\u6709\u65f6\u5019\u65e0\u6cd5\u548c\u5973\u4e3b\u4ea4\u4e92\u201d: \u70b9\u4e0d\u52a8\u7684\u90a3\u6b21\u5bf9\u6bd4\u6b63\u5e38\u90a3\u6b21\u7684\u8f93\u51fa\u5c31\u884c\u3002\u4e0d\u9700\u8981\u65f6\u6539 false \u5173\u6389\u3002");
            AlwaysShowGazeRing = Config.Bind("Visual", "AlwaysShowGazeRing", true,
                "\u51dd\u89c6\u5149\u73af\u5e38\u663e: \u6ca1\u7784\u51c6\u4efb\u4f55\u53ef\u4ea4\u4e92\u4e1c\u897f\u65f6\u4e5f\u628a\u5b83\u56fa\u5b9a\u5728\u89c6\u7ebf\u6b63\u4e2d; \u7784\u4e2d\u6309\u94ae\u65f6\u4ecd\u7136\u4ece\u6e38\u620f\u539f\u751f\u903b\u8f91\u62ff\u5230\u653e\u5927/\u8fdb\u5ea6\u53cd\u9988\u3002\n"
                + "true = \u5e38\u663e(\u63a8\u8350, \u76f8\u5f53\u4e8e\u514dVR \u4e0b\u7684\u51c6\u661f), false = \u5b8c\u5168\u4ea4\u7ed9\u6e38\u620f(\u53ea\u5728\u53ef\u4ea4\u4e92\u7269\u4f53\u4e0a\u51fa\u73b0)\u3002");
            GazeRingMode = Config.Bind("Visual", "GazeRingMode", "Aim",
                "v1.22.0 \u5149\u73af\u5b9a\u4f4d\u65b9\u5f0f\u3002Aim = \u6bcf\u5e27\u4ece\u76f8\u673a\u5c04\u7ebf\u6253\u4e00\u6761\u7269\u7406\u5c04\u7ebf, \u5149\u73af\u8d34\u5728\u547d\u4e2d\u70b9\u4e0a(\u8ddf\u7740\u4f60\u770b\u7684\u4e1c\u897f\u8d70); "
                + "Center = \u56fa\u5b9a\u5728\u5c4f\u5e55\u6b63\u4e2d\u5fc3 GazeRingDistance \u7c73\u5904\u3002");
            GazeRingMaxDistance = Config.Bind("Visual", "GazeRingMaxDistance", 8f,
                "v1.22.0 Aim 模式下射线的最大长度(米)。超过这个距离没命中就回到屏幕中心。v1.25.0 默认从 2.5 放宽到 8。");
            GazeRingHideWhenNoHit = Config.Bind("Visual", "GazeRingHideWhenNoHit", true,
                "v1.25.0 关闭时: 视线没打到任何东西, 光环会挂在相机正前方 1.5 米处 —— 看上去就像个钉在画面中央、"
                + "相机转它也不动的假准星。开启(默认)后这种时候直接隐藏光环, 只有真的贴到物体表面时才显示。");
            GazeRingLog = Config.Bind("Diagnostics", "GazeRingLog", false,
                "v1.22.0 每秒打一行光环位置/距离/命中物, 用来证明它在动。");
            GazeRingDistance = Config.Bind("Visual", "GazeRingDistance", 1.5f,
                "\u5149\u73af\u5e38\u663e\u65f6\u79bb\u773c\u775b\u7684\u8ddd\u79bb(\u7c73) 0.5-4: \u592a\u8fd1\u4f1a\u906e\u624b, \u592a\u8fdc\u4f1a\u53d8\u5f97\u5f88\u5927\u3002\u5149\u73af\u4f1a\u6309\u8ddd\u79bb\u7b49\u6bd4\u7f29\u653e, \u89c6\u89d2\u5c3a\u5bf8\u6052\u5b9a\u3002");
            HideGazeRing = Config.Bind("Visual", "HideGazeRing", false,
                "\u514d VR \u65f6\u9690\u85cf\u51dd\u89c6\u5149\u73af (VRKanojo_GazePointerRing)\u3002"
                + "\u5b83\u662f VR \u5934\u663e\u51dd\u89c6\u6e38\u6807\uff0c\u65e0\u5934\u663e\u65f6\u4f1a\u50cf\u4e2a\u7269\u4f53\u6302\u5728\u5c4f\u5e55\u6b63\u4e2d\u592e\u3002");
            DumpKeyRaw = Config.Bind("Diagnostics", "DumpKey", "F10",
                "\u6309\u8fd9\u4e2a\u952e\u7acb\u5373\u8f93\u51fa\u4e00\u4efd\u5b8c\u6574\u6e32\u67d3\u6e05\u5355 (Graphic + Renderer + Canvas)\u3002");
            ViewToggle = Config.Bind("Camera", "ViewToggle", true,
                "\u5f00\u5173\uff1a\u5141\u8bb8\u7528\u70ed\u952e\u5728\u7b2c\u4e00/\u7b2c\u4e09\u4eba\u79f0\u4e4b\u95f4\u5207\u6362\u3002");
            ViewToggleKey = Config.Bind("Camera", "ViewToggleKey", "F",
                "\u5207\u6362\u89c6\u89d2\u7684\u6309\u952e\uff08KeyCode \u540d\uff0c\u9ed8\u8ba4 F\uff09\u3002");
            FirstPersonDistance = Config.Bind("Camera", "FirstPersonDistance", 0.05f,
                "\u7b2c\u4e00\u4eba\u79f0\u65f6\u76f8\u673a\u8ddd\u79bb\u7126\u70b9\u7684\u503c\uff08\u8d8a\u5c0f\u8d8a\u8d34\u8138\uff0c0.02~0.1\uff09\u3002");
            FirstPersonMoveSpeed = Config.Bind("Camera", "FirstPersonMoveSpeed", 2.0f,
                "\u7b2c\u4e00\u4eba\u79f0\u4e0b WASD \u884c\u8d70\u901f\u5ea6\uff08\u7c73/\u79d2\uff09\uff0c\u6309\u4f4f Shift \u4e58 FreeSprintMultiplier\u3002");
            FreeMoveSpeed = Config.Bind("Camera", "FreeMoveSpeed", 3.0f,
                "\u81ea\u7531\u89c6\u89d2\u7684\u79fb\u52a8\u901f\u5ea6\uff08\u7c73/\u79d2\uff09\uff0c\u6309\u4f4f Shift \u4e58 FreeSprintMultiplier\u3002");
            FreeSprintMultiplier = Config.Bind("Camera", "FreeSprintMultiplier", 3.0f,
                "\u81ea\u7531\u89c6\u89d2\u4e0b\u6309\u4f4f Shift \u7684\u500d\u7387\u3002");
            ThirdPersonDistance = Config.Bind("Camera", "ThirdPersonDistance", 1.5f,
                "\u7b2c\u4e09\u4eba\u79f0\u6062\u590d\u65f6\u7684\u8ddd\u79bb\uff08\u539f\u7248\u9ed8\u8ba4 1.5\uff09\u3002");
            ClickInGame = Config.Bind("Input", "ClickInGame", true,
                "\u6b63\u7247\u573a\u666f\u91cc\u4e5f\u5141\u8bb8\u76f4\u8fde\u70b9\u51fb\uff08\u5e73\u677f\u83dc\u5355\u8fd9\u7c7b\u4e16\u754c\u7a7a\u95f4 UI \u53ea\u80fd\u8fd9\u6837\u70b9\uff09\u3002");
            PhysicsClick = Config.Bind("Input", "PhysicsClick", true,
                "\u5f00\u542f\u7269\u7406\u5c04\u7ebf\u70b9\u51fb\uff1a\u5bf9\u51c6 3D \u7269\u4f53\uff08\u5982\u5e73\u677f\u83dc\u5355\uff09\u4e5f\u80fd\u89e6\u53d1 Button / IPointerClickHandler\u3002");
            KeepOvrInput = Config.Bind("Input", "KeepOvrInput", true,
                "\u4e0d\u62c6\u6e38\u620f\u81ea\u5e26\u7684 OVRInputModule / OVRRaycaster\uff08\u5b83\u4eec\u662f\u6e38\u620f\u83dc\u5355\u7684\u539f\u751f\u8f93\u5165\u94fe\u8def\uff09\u3002");
            CenterProbe = Config.Bind("Diagnostics", "CenterProbe", true,
                "\u8fdb\u5165\u6e38\u620f\u573a\u666f\u540e\u81ea\u52a8\u63a2\u6d4b\u5c4f\u5e55\u6b63\u4e2d\u592e\u7684\u7269\u4f53\uff08\u6253\u4e00\u884c\u65e5\u5fd7\uff0c\u65b9\u4fbf\u5b9a\u4f4d\u4e0d\u660e\u7269\u4f53\uff09\u3002");
            ProbeDelay = Config.Bind("Diagnostics", "ProbeDelay", 4.0f,
                "\u76ee\u6807\u573a\u666f\u8fdb\u5165\u540e\u591a\u5c11\u79d2\u505a\u4e00\u6b21\u4e2d\u5fc3\u63a2\u6d4b\u3002");
            EyeHeightEnabled = Config.Bind("Camera", "EyeHeightEnabled", true,
                "免VR 时把相机抬到站立眼高。游戏在 FloorLevel 追踪原点下把 CameraRig 的 Y 强制写成 0"
                + "（Manager.Scene.SetCameraRigPos: DefPos_ = new Vector3(pos_.x, 0f, pos_.z)），"
                + "真机时由头显位姿补上 1.6 米；没有头显就是 0 米，相机卡在地板里，"
                + "房间地板被剔除、只剩巨大的室外地面球(道具 props/tamatama/tama, 包围盒 32x24x32 米)铺满下半屏。");
            EyeHeight = Config.Bind("Camera", "EyeHeight", 1.28f,
                "站立眼高(米)。写入 OVRCameraRig.trackingSpace.localPosition.y，OVRCameraRig.UpdateAnchors 会据此把眼睛摆到该高度。");
            SyncGameGazeCamera = Config.Bind("Camera", "SyncGameGazeCamera", true,
                "把游戏自带的凝视射线(VREyeRaycaster / BodyTouchManager / InteractiveLookingStatus) "
                + "的起点对齐到真正渲染的眼位。关掉就回到原版行为。");
            GazeLookDistance = Config.Bind("Camera", "GazeLookDistance", 3.5f,
                "游戏 InteractiveLookingStatus 的注视判定距离(原版 2m)。第三人称相机退到 1.5m "
                + "之后, 2m 很容易够不到目标。");
            FlatUiInput = Config.Bind("Input", "FlatUiInput", true,
                "免VR 菜单鼠标点击桥。Title 等菜单只用 OVRRaycaster（从 VR 射线指针发射），没有头显时鼠标点不到按钮。"
                + "开启后会给每个世界空间 Canvas 补 GraphicRaycaster、把 OVRRaycaster 停用，并保证 EventSystem 上有 StandaloneInputModule。");
            MenuCursorLock = Config.Bind("Input", "MenuCursorLock", false,
                "菜单/加载场景是否锁定鼠标指针。false=菜单里指针始终可见可移动（能点按钮），true=像正片一样锁在屏幕中心。");
            DirectMenuClick = Config.Bind("Input", "DirectMenuClick", true,
                "免VR 直连点击：菜单里按下左键时，直接计算光标落在哪个 Button 上并调用它的 onClick，"
                + "完全不依赖 EventSystem/GraphicRaycaster/OVRRaycaster 这条链。上一版只补了 raycaster 仍然点不动，"
                + "说明链上有别的东西在吞点击（例如 order=32767 的全屏 Fader 面板），所以这里绕过整条链。");
            ClickDebug = Config.Bind("Input", "ClickDebug", false,
                "把每次点击尝试的结果写进日志：命中的按钮路径、或被挡在哪个 Graphic 上、或压根没命中。"
                + "确认能点之后可以关掉。");

            SaveFrames = Config.Bind("Diagnostics", "SaveFrames", false,
                "进入正式场景后把渲染结果存成 PNG(游戏根目录 vrk_frame_*.png), 用于白屏取证。");
            MaxShots = Config.Bind("Diagnostics", "MaxShots", 3,
                "最多存几张取证图(每场景最多1张)。");

            if (ForceFlatMode.Value && !VrMode)
            {
                GlobalVRKanojo.ForceMouse_ = true;
            }

            // v1.28.1: VR 入口下一个补丁都不打 —— 键位重映射、CheckHMD 放行、LogoScene、VRCameraFade、
            // IllusionCamera.LateUpdate、点击/触摸这些全都不生效, 游戏按键与 UI 100% 回归原生逻辑。
            if (FlatFpsPlugin.VrMode)
            {
                Log.LogInfo("[FlatFPS] VR mode: no Harmony patches applied (game logic fully native).");
            }
            else try
            {
                Harmony harmony = new Harmony(Guid);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.LogInfo("[FlatFPS] Harmony patches applied.");
            }
            catch (Exception e)
            {
                Log.LogError("[FlatFPS] PatchAll failed: " + e);
            }

            GameObject host = new GameObject("VRK_FlatFPS_Runtime");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<FlatFpsRuntime>();
            host.AddComponent<HelpOverlay>();
            if (VrMode)
                HelpOverlay.Toast("VR 模式：免VR 相关功能已关闭，请戴上头显", 5f);

            Log.LogInfo("[FlatFPS] v" + Info.Metadata.Version + " ready. flat=" + ForceFlatMode.Value
                + " vrMode=" + VrMode
                + " fadeWatchdog=" + RepairStuckFade.Value
                + " clearOnAwake=" + ClearPanelOnAwake.Value
                + " menuCamFix=" + MenuSceneCameraFix.Value
                + " freeLook=" + FreeLook.Value
                + " fpsKeys=" + UseFpsKeys.Value
                + " helpKey=" + HelpHotkey.Value
                + " holdCursor=" + CursorHoldKey.Value);
        }
    }

    // ------------------------------------------------------------------
    // 1. 免 VR: 直接在 CheckHMD 返回 true，不做 VRDevice 判断、不碰 Game 单例
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(InitScene2), "CheckHMD")]
    internal static class PatchCheckHmd
    {
        private static bool _reported;

        private static bool Prefix(ref bool __result)
        {
            if (FlatFpsPlugin.VrMode)
                return true;            // \u771f VR: \u4ea4\u7ed9\u6e38\u620f\u81ea\u5df1\u7684 CheckHMD

            if (!FlatFpsPlugin.ForceFlatMode.Value)
                return true;

            GlobalVRKanojo.ForceMouse_ = true;
            __result = true; // 让 InitSetting 走 Logo 分支, 并让 InitCameraRig 挂 IllusionCamera

            if (!_reported)
            {
                _reported = true;
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] InitScene2.CheckHMD -> true (flat mode), GlobalVRKanojo.ForceMouse_=true");
            }
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 2. 白屏相关: LogoScene.Start 前补好 VRCameraFade_ 引用;
    //    OVRManager 不存在时跳过会 NRE 的原方法, 只保留平面模式需要的部分。
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(LogoScene), "Start")]
    internal static class PatchLogoStart
    {
        private static readonly FieldInfo FadeField = AccessTools.Field(typeof(LogoScene), "VRCameraFade_");
        private static readonly FieldInfo CanvasField = AccessTools.Field(typeof(LogoScene), "Canvas_");
        private static bool _reported;

        private static bool Prefix(LogoScene __instance)
        {
            Camera cam = Camera.main;
            if (cam == null)
                return true;

            try
            {
                VRCameraFade fade = cam.GetComponent<VRCameraFade>();
                if (fade == null && FlatFpsPlugin.ForceFlatMode.Value)
                {
                    fade = cam.gameObject.AddComponent<VRCameraFade>();
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] MainCamera had no VRCameraFade -> added one (else no fade/timeout control).");
                }

                if (FadeField != null && __instance != null && FadeField.GetValue(__instance) == null && fade != null)
                    FadeField.SetValue(__instance, fade);

                Canvas canvas = CanvasField != null && __instance != null ? CanvasField.GetValue(__instance) as Canvas : null;
                if (canvas != null && canvas.worldCamera == null)
                    canvas.worldCamera = cam;
            }
            catch (Exception e)
            {
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] LogoScene.Start prefix: " + e.GetType().Name + ": " + e.Message);
            }

            if (FlatFpsPlugin.ForceFlatMode.Value && OVRManager.instance == null)
            {
                // 原方法会在这行 NRE: Debug.Log(... + OVRManager.instance.trackingOriginType)
                // 之后 state 与 VRCameraFade_ 的赋值都不会执行 -> Logo 状态机卡死。
                // 平面模式下这里直接接管, 只做安全的部分。
                try
                {
                    ConfigEffector effector = cam.GetComponent<ConfigEffector>();
                    if (effector != null)
                        effector.EffectAllDisable();
                }
                catch (Exception e)
                {
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] EffectAllDisable: " + e.Message);
                }

                if (!_reported)
                {
                    _reported = true;
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] LogoScene.Start: OVRManager absent -> original skipped (flat-safe path).");
                }
                return false;
            }

            return true;
        }

        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] LogoScene.Start threw " + __exception.GetType().Name
                    + ": " + __exception.Message + " (swallowed, logo state machine continues)");
            }
            return null;
        }
    }
    // ------------------------------------------------------------------
    // 根因修复: VRCameraFade 的遮罩面板序列化颜色 = 纯白 alpha 1,
    // 而 prefab 上 m_FadeInOnStart / m_FadeInOnSceneLoad 都是 false
    // (已用 SerializedFile 直接解析 resources.assets 证实:
    //   VRCameraFade pid 4366 FadeColor=(1,1,1,1) InOnStart=0 InOnSceneLoad=0
    //   pid 4459 同上, 其 m_FadeImage(pid 4388) 的 m_Color=(1,1,1,1) 纯白不透明)
    // 于是 Awake 里 `m_FadeImage.enabled = true` 之后没有任何代码把它擦掉,
    // 除非 LogoScene 主动调 FadeIn; 一旦那条路径没走到, 就是永久白屏 + 有声音。
    // 这里在 Awake 结束后直接把 alpha 清零: 显式 FadeIn/FadeOut 仍然从白色开始,
    // 只是"没人来擦"的那种状态不复存在。
    // ------------------------------------------------------------------
    // v1.23.0 P0: E 键 = 游戏原生的"决定/触摸"输入。
    // VK_Input.Update() 每帧调用 CheckDecision() 再交给 UpdateState(ref decision, flag) 推导按下/松开边沿,
    // 所以这里只要把 E 或进返回值, 整套 DecisionDown/DecisionUp 就自动带边沿, 不需要动游戏其它逻辑。
    [HarmonyPatch(typeof(Manager.VK_Input), "CheckDecision")]
    internal static class PatchCheckDecision
    {
        private static void Postfix(ref bool __result)
        {
            if (FlatFpsPlugin.VrMode || __result)
                return;
            if (FlatFpsPlugin.InteractHeld())
                __result = true;
        }
    }

    // v1.24.0: 交互键的缓存解析 —— 每帧 Enum.Parse 会持续产生垃圾，这里只在配置变化时解析一次。
    internal static class InteractKeyCache
    {
        internal static string Raw;
        internal static KeyCode Code = KeyCode.None;

        internal static KeyCode Get()
        {
            string raw = (FlatFpsPlugin.InteractKey != null) ? FlatFpsPlugin.InteractKey.Value : "E";
            if (raw == Raw)
                return Code;
            Raw = raw;
            Code = KeyCode.None;
            if (string.IsNullOrEmpty(raw))
                return Code;
            try { Code = (KeyCode)Enum.Parse(typeof(KeyCode), raw.Trim(), true); }
            catch (Exception) { Code = KeyCode.None; }
            return Code;
        }
    }

    // v1.24.0: 触摸/爱抚的输入走的是 VK_Input.H_ActStart() / GazeTouchIn_L()（内部只有 Input.GetMouseButtonDown(0)），
    //          跟 CheckDecision 完全无关 —— 所以只改 CheckDecision 时"瞄准女主显示可交互却没反应"。
    //          这两个 Postfix 把交互键折进返回值，等价于按下鼠标左键但不拖镜头。
    [HarmonyPatch]
    internal static class PatchTouchStart
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Manager.VK_Input), "H_ActStart");
            yield return AccessTools.Method(typeof(Manager.VK_Input), "GazeTouchIn_L");
        }

        private static void Postfix(ref bool __result)
        {
            if (FlatFpsPlugin.VrMode || __result)
                return;
            if (!FlatFpsPlugin.InteractHeld())
                return;
            if (!FlatFpsPlugin.IsGameScene(FlatFpsRuntime.ActiveSceneName()))
                return;
            __result = true;
        }
    }

    // v1.24.0: CheckGazeTouch 里 float num = 1.5f; 是"从多近能抓到她"的硬编码距离。
    //          把这条 ldc.r4 换成读配置的静态属性，另外两个 ldc.r4 不碰。
    [HarmonyPatch(typeof(BodyTouchManager), "CheckGazeTouch")]
    internal static class PatchGazeGrabDistance
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var list = new List<CodeInstruction>(instructions);
            FieldInfo gf = typeof(FlatFpsPlugin).GetField("GazeGrabRuntime", BindingFlags.Public | BindingFlags.Static);
            if (gf == null)
                return list;
            int hits = 0;
            foreach (var ins in list)
            {
                if (ins.opcode == OpCodes.Ldc_R4 && ins.operand != null && ins.operand is float
                    && Mathf.Abs((float)ins.operand - 1.5f) < 0.0001f)
                {
                    ins.opcode = OpCodes.Ldsfld;
                    ins.operand = gf;
                    hits++;
                }
            }
            if (hits == 0)
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] CheckGazeTouch: 1.5f 常量没找到, 抓取距离保持游戏原值");
            else
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] CheckGazeTouch: 抓取距离已改为可配置 (" + hits + " 处)");
            return list;
        }
    }

    // v1.23.0 P0: 放宽游戏自带的注视/触摸判定距离(PlayerLookingCheck.raycastDis 默认 2 米)。
    [HarmonyPatch(typeof(PlayerLookingCheck), "Start")]
    internal static class PatchLookingStart
    {
        private static void Postfix(PlayerLookingCheck __instance)
        {
            if (FlatFpsPlugin.VrMode)
                return;
            float d = (FlatFpsPlugin.LookDistance != null) ? FlatFpsPlugin.LookDistance.Value : 3.5f;
            d = Mathf.Clamp(d, 2f, 10f);
            System.Reflection.FieldInfo fi = AccessTools.Field(typeof(PlayerLookingCheck), "raycastDis");
            if (fi != null)
            {
                fi.SetValue(__instance, d);
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] PlayerLookingCheck.raycastDis -> " + d.ToString("F2") + " m");
            }
        }
    }

    // v1.20.0: 常显模式下不让 OVRGazePointer.Update 把光环 Hide 掉(它每帧按 visibilityStrength 决定显隐),
    // 位置/缩放交给 FlatFpsRuntime.KeepGazeRingVisible() 自己算, 免得和中心眼 anchor 打架。
    [HarmonyPatch(typeof(OVRGazePointer), "Update")]
    internal static class PatchGazePointerUpdate
    {
        private static bool Prefix()
        {
            if (FlatFpsPlugin.VrMode)
                return true;
            if (!FlatFpsPlugin.AlwaysShowGazeRing.Value || FlatFpsPlugin.HideGazeRing.Value)
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(VRCameraFade), "Awake")]
    internal static class PatchFadeAwake
    {
        private static bool _logged;

        private static void Postfix(VRCameraFade __instance)
        {
            if (!FlatFpsPlugin.ClearPanelOnAwake.Value || __instance == null)
                return;
            try
            {
                Image img = __instance.M_FadeImage;
                if (img == null)
                    return;
                Color c = img.color;
                // 这块面板是全屏 Image: 就算 alpha=0, raycastTarget=true 也会把鼠标点击整片吞掉
                // (它的 Canvas order=32767, 任何按钮都在它下面), 一并关掉。
                img.raycastTarget = false;
                if (c.a <= 0.001f)
                    return;
                img.color = new Color(c.r, c.g, c.b, 0f);
                if (!_logged && FlatFpsPlugin.Log != null)
                {
                    _logged = true;
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] cleared opaque fade panel at Awake (was alpha="
                        + c.a.ToString("F2") + ", IsFading=" + __instance.IsFading + ") - white screen root cause");
                }
            }
            catch (Exception e)
            {
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] clear fade panel failed: " + e.Message);
            }
        }
    }

    // ------------------------------------------------------------------
    // 纯色界面根因: 免VR 时 IllusionCamera（第三人称角色相机控制器）在 LateUpdate
    // 末尾 `transform.position = CalcPos()` 把整个相机搬到 focus 前方 distance=1.5 米,
    // 而 Title/Attention 这些菜单是绕玩家原点摆的 WorldSpace VR 界面 —— 相机一移走,
    // 菜单全部出画, 只剩下相机 clear color 的背景渐变 = 用户看到的“纯色界面 + 有声音”。
    // 这里在非游戏场景直接跳过 LateUpdate（不动相机），菜单期间相机就停在头部原点。
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(IllusionCamera), "LateUpdate")]
    internal static class PatchCameraLateUpdate
    {
        private static bool Prefix(IllusionCamera __instance)
        {
            // v1.27.1 修 BUG: VR 模式必须把相机完全交还游戏, 否则 VR 下 IllusionCamera 被停用、头显位姿无人驱动。
            if (FlatFpsPlugin.VrMode)
                return true;
            bool game = FlatFpsPlugin.IsGameScene(FlatFpsRuntime.ActiveSceneName());
            // 第一人称 / 自由视角: 相机归本插件管, 游戏不要再写 transform.position。
            if (game && FlatFpsRuntime.ViewMode != 1)
                return false;
            if (!FlatFpsPlugin.MenuSceneCameraFix.Value)
                return true;
            return game;
        }
    }


    // ------------------------------------------------------------------
    // 3. 键位: 把 IllusionCamera.KeyInput() 里所有 KeyCode 常量整体重映射。
    //    为什么改 IL 常量而不是"改 KeyInput_Value 的入参": 小键盘 5(俯仰回正) 与
    //    小键盘 +/(灵敏度) 是直接 Input.GetKey 写的, 根本走不到 KeyInput_Value。
    //    一次扫描 + 一张表 = 覆盖全部键位; 单趟替换, 所以可以安全做链式映射
    //    (270 KeypadPlus -> 61 Equals, 同时原来 FOV 的 61 Equals 被搬到 91 LeftBracket)。
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(IllusionCamera), "KeyInput")]
    internal static class PatchKeyInput
    {
        // 左列=原版键值, 右列=免VR 键位
        internal static readonly Dictionary<int, int> Map = new Dictionary<int, int>
        {
            { 275, 100 }, // RightArrow  -> D
            { 276, 97 },  // LeftArrow   -> A
            { 273, 119 }, // UpArrow     -> W
            { 274, 115 }, // DownArrow   -> S
            { 262, 54 },  // Keypad6     -> Alpha6  (左右转视角)
            { 260, 52 },  // Keypad4     -> Alpha4
            { 264, 56 },  // Keypad8     -> Alpha8  (俯仰)
            { 258, 50 },  // Keypad2     -> Alpha2
            { 261, 53 },  // Keypad5     -> Alpha5  (俯仰回正)
            { 270, 61 },  // KeypadPlus  -> Equals  (灵敏度+)
            { 269, 45 },  // KeypadMinus -> Minus   (灵敏度-)
            { 61, 91 },   // Equals      -> LeftBracket (FOV 下移让位给灵敏度)
            { 47, 114 }   // Slash       -> R (重置视角: 翻滚归零; 游戏自己也把 R 当 VR 回中键)
        };

        private static bool _reported;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ins in instructions)
            {
                if (FlatFpsPlugin.UseFpsKeys.Value
                    && (ins.opcode == OpCodes.Ldc_I4 || ins.opcode == OpCodes.Ldc_I4_S))
                {
                    int v;
                    if (ins.operand is int)
                        v = (int)ins.operand;
                    else if (ins.operand is sbyte)
                        v = (sbyte)ins.operand;
                    else if (ins.operand is byte)
                        v = (byte)ins.operand;
                    else
                        v = -1;

                    int nv;
                    if (v >= 0 && Map.TryGetValue(v, out nv))
                    {
                        if (nv <= 127)
                        {
                            ins.opcode = OpCodes.Ldc_I4_S;
                            ins.operand = (sbyte)nv;
                        }
                        else
                        {
                            ins.opcode = OpCodes.Ldc_I4;
                            ins.operand = nv;
                        }
                        if (!_reported && FlatFpsPlugin.Log != null)
                        {
                            _reported = true;
                            FlatFpsPlugin.Log.LogInfo("[FlatFPS] KeyInput remapped: arrows->WASD, keypad 6/4/8/2/5 -> 6/4/8/2/5, "
                                + "keypad +/- -> =/-, FOV '=' -> '['");
                        }
                    }
                }
                yield return ins;
            }
        }
    }

    // ------------------------------------------------------------------
    // 4. 运行时: 淡入淡出看门狗 + 自由视角 + 指针锁定 + 诊断
    // ------------------------------------------------------------------
    // ===== v1.35.0: 手部震动除零 =====
    // 免VR 时 OVRHaptics.Config.SampleSizeInBytes 从未被初始化(仍为 0),
    // 于是 VRKanojoTouch.Hand.OnTriggerStay() 一碰到 TouchCollider 层就:
    //   m_proceduralClip.Add(30); controller.Vibration(clip);
    //   -> OVRHapticsClip(byte[] samples, int samplesCount)
    //   -> Capacity = Samples.Length / Config.SampleSizeInBytes   <-- 0 除
    // 抛出的异常会打断 OnTriggerStay 剩下的 InteractiveProc(...) 调用,
    // 也就是"手明明贴上去了, 女主却毫无反应"的真正原因。
    [HarmonyPatch(typeof(VRKanojoTouch.TrackedController), "Vibration")]
    internal static class PatchHapticsVibration
    {
        private static bool _warned;

        private static bool Prefix(byte[] clip)
        {
            if (FlatFpsPlugin.VrMode) return true;
            if (OVRHaptics.Config.SampleSizeInBytes > 0) return true;
            if (!_warned)
            {
                _warned = true;
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] haptics unavailable (SampleSizeInBytes=0): hand vibration skipped, " +
                        "touch/caress logic keeps running");
            }
            return false;
        }

        private static Exception Finalizer(Exception __exception)
        {
            return null;
        }
    }

    [HarmonyPatch(typeof(VRKanojoTouch.Hand), "OnTriggerStay")]
    internal static class PatchHandTriggerStay
    {
        private static Exception Finalizer(Exception __exception)
        {
            return null;
        }
    }

    internal class FlatFpsRuntime : MonoBehaviour
    {
        private static readonly FieldInfo IsFadingField =
            typeof(VRCameraFade).GetField("m_IsFading", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RotateField =
            typeof(IllusionCamera).GetField("rotate", BindingFlags.Instance | BindingFlags.NonPublic);

        private Camera _cam;
        private VRCameraFade _fade;
        private IllusionCamera _ill;

        private float _nextRefresh;

        // --- 白屏取证 / 多实例看门狗 ---
        private struct FadeTrack
        {
            public int Id;
            public float LastAlpha;
            public float Stuck;
            public float Covered;
            public int Repairs;
        }

        private readonly List<FadeTrack> _tracks = new List<FadeTrack>();
        private VRCameraFade[] _fades = new VRCameraFade[0];
        private float _nextFadeScan;
        private int _shotCount;
        private string _shotScene = "";

        // --- 孤儿白色面板兜底(没有 VRCameraFade 组件的 Fader 画布) ---
        private Image[] _orphanImgs = new Image[0];
        private readonly Dictionary<int, float> _orphanTimer = new Dictionary<int, float>();
        private float _nextOrphanScan;
        private bool _cursorFree;
        private KeyCode[] _holdKeys = new KeyCode[] { KeyCode.LeftAlt, KeyCode.RightAlt };
        private string _holdKeysRaw = "";
        private Action<IllusionCamera, Vector3> _rotateAction;
        private bool _rotateActionTried;

        private string _lastScene = "";
        private float _sceneTime;
        private bool _sceneReported;
        private bool _probed;

        private void Start()
        {
            Refresh(true);
        }

        private void Update()
        {
            FlatFpsPlugin.SyncGazeGrab();
            float rawDt = Time.unscaledDeltaTime;
            if (FlatFpsPlugin.FrameGapSeconds.Value > 0f && rawDt >= FlatFpsPlugin.FrameGapSeconds.Value)
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] long frame " + rawDt.ToString("F2") + "s in '"
                    + ActiveSceneName() + "' t=" + Time.realtimeSinceStartup.ToString("F2"));

            float now = Time.realtimeSinceStartup;
            if (now >= _nextRefresh)
            {
                _nextRefresh = now + 0.5f;
                Refresh(false);
            }

            if (FlatFpsPlugin.RepairStuckFade.Value)
                Watchdog(Time.unscaledDeltaTime);

            // 先判定「按住释放键」状态, 让自由视角本帧就让位给鼠标
            _cursorFree = ReadHoldKeys();

            if (FlatFpsPlugin.FreeLook.Value && !_cursorFree)
                DoFreeLook();

            DoCursor();
            DoDirectMenuClick();
            DoPhysicsClick();
            HandleDumpKey();
            HandleInteractProbe();
            HandleMouseHand();
            EnsureImeOff();
            HandleViewKey();
            TickHideGazeRing(Time.unscaledDeltaTime);
            DoSceneDiagnostics(Time.unscaledDeltaTime);
        }

        private bool _poseResetLogged;
        private KeyCode _dumpKey = KeyCode.F10;
        private string _dumpKeyRaw = "F10";
        private bool _gazeHidden;
        private int _gazeTargetId;
        private float _gazeTick;
        private float _viewScanAt;
        private bool _viewScanDone;
        private object _gazeType;
        private System.Reflection.PropertyInfo _gazeInstance;
        private bool _uiBridged;
        private OVRCameraRig _rig;
        private bool _eyeHeightLogged;
        private bool _hmdChecked;
        private bool _hmdPresent;

        private void LateUpdate()
        {
            // 真 VR: 相机位姿/旋转全归头显, 眼高与菜单复位都不能插手
            if (FlatFpsPlugin.VrMode)
                return;

            float udt = Time.unscaledDeltaTime;
            if (_viewMode == 0)
                DoFirstPerson(udt);
            else if (_viewMode == 2)
                DoFreeView(udt);
            // v1.25.0: 放在视角旋转之后, 光环用的是这一帧的相机朝向(原来在旋转之前, 落后一帧)
            KeepGazeRingVisible();
            UpdateGazeHover();
            if (_viewMode == 2)
                return;             // 自由视角下眼高/菜单复位都不该插手
            DoEyeHeight();
            TickGazeCameraSync();

            if (!FlatFpsPlugin.MenuSceneCameraFix.Value || _cam == null)
                return;
            if (FlatFpsPlugin.IsGameScene(ActiveSceneName()))
                return;
            // 无头显时 OVRCameraRig 每帧把 anchor 写成 identity; 这里兜底: 谁把它横向挪走过就掰回来。
            // 只清 x/z 与旋转, 竖直方向归 DoEyeHeight() 管(眼高)。
            Vector3 lp = _cam.transform.localPosition;
            if (Mathf.Abs(lp.x) > 0.01f || Mathf.Abs(lp.z) > 0.01f ||
                Quaternion.Angle(_cam.transform.localRotation, Quaternion.identity) > 1f)
            {
                _cam.transform.localPosition = new Vector3(0f, lp.y, 0f);
                _cam.transform.localRotation = Quaternion.identity;
                if (!_poseResetLogged && FlatFpsPlugin.Log != null)
                {
                    _poseResetLogged = true;
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] menu camera was at " + lp.ToString("F2")
                        + " -> reset to head origin (this is what pushes the menu UI out of view)");
                }
            }
            // 有些场景脚本会在自己的 Update/LateUpdate 里重新锁指针; 这里在 LateUpdate 末尾再压一次,
            // 保证菜单里指针一定解锁+可见, 否则鼠标动不了也就无从点击。
            DoCursor();
        }

        /// <summary>
        /// 免VR 眼高。游戏在 FloorLevel 追踪原点下把 CameraRig 的 Y 写死成 0
        /// （Manager.Scene.SetCameraRigPos: DefPos_ = new Vector3(pos_.x, 0f, pos_.z)），
        /// 真机时由头显位姿补上约 1.6 米；没有头显就是 0 米，相机卡在地板里 ——
        /// 地板被背面剔除，只剩 32x24x32 米的室外地面球 props/tamatama/tama 铺满下半屏。
        /// 抬升 OVRCameraRig.trackingSpace 后，OVRCameraRig.UpdateAnchors 会据此把眼睛摆到该高度。
        /// </summary>
        private void DoEyeHeight()
        {
            if (FlatFpsPlugin.EyeHeightEnabled == null || !FlatFpsPlugin.EyeHeightEnabled.Value)
                return;

            // 有头显时 trackingSpace 由运行时/头显位姿管，绝不能叠加 1.6 米。
            // 这个读取放在 LateUpdate 里，图形设备早已就绪（在 Awake 里读才会 0xC0000005，见 UpScale 的崩溃记录）。
            if (!_hmdChecked)
            {
                _hmdChecked = true;
                try { _hmdPresent = UnityEngine.VR.VRDevice.isPresent; }
                catch { _hmdPresent = false; }
            }
            if (_hmdPresent)
                return;

            if (_rig == null)
                _rig = (OVRCameraRig)UnityEngine.Object.FindObjectOfType(typeof(OVRCameraRig));
            float h = (_eyeOverride > 0f) ? _eyeOverride : FlatFpsPlugin.EyeHeight.Value;
            if (h < 0f) h = 0f;

            // v1.39.0: PageUp/PageDown 改眼高后, 游戏那三条凝视射线的参考点必须立刻跟着变,
            // 否则"看着够不着"的判定会滞后一整个周期(甚至一直停在旧高度)。
            if (Mathf.Abs(h - _lastSyncedEye) > 0.001f)
            {
                bool first = _lastSyncedEye < 0f;
                _lastSyncedEye = h;
                _gazeSyncDueAt = 0f;                 // 下一帧就跑一次同步
                if (!first && FlatFpsPlugin.Log != null)
                {
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] eye height -> " + h.ToString("F2")
                        + " m, re-syncing game gaze rays (real-time)");
                }
            }

            if (_rig != null && _rig.trackingSpace != null)
            {
                // 游戏在 FloorLevel 下把 CameraRig 的 Y 写死 0，眼高靠头显位姿加；
                // 但 EyeLevel 下它把 rig 直接摆到眼高，这时再加 h 就会变成 3.2 米。
                // 所以先看 rig 根节点的 Y：已经离地就不再重复加。
                float rigY = _rig.transform.position.y;
                float want = (Mathf.Abs(rigY) > 0.35f) ? 0f : h;
                Vector3 ts = _rig.trackingSpace.localPosition;
                if (Mathf.Abs(ts.y - want) > 0.004f || Mathf.Abs(ts.x) > 0.004f || Mathf.Abs(ts.z) > 0.004f)
                {
                    _rig.trackingSpace.localPosition = new Vector3(0f, want, 0f);
                    if (!_eyeHeightLogged && FlatFpsPlugin.Log != null)
                    {
                        _eyeHeightLogged = true;
                        FlatFpsPlugin.Log.LogWarning("[FlatFPS] no HMD: rigY=" + rigY.ToString("F2")
                            + " trackingSpace " + ts.ToString("F2") + " -> (0.00, " + want.ToString("F2") + ", 0.00)"
                            + " => eye at " + (rigY + want).ToString("F2") + " m"
                            + " (PageUp/PageDown 第一人称下可微调)");
                    }
                }
                return;
            }

            if (_cam != null)
            {
                Vector3 lp = _cam.transform.localPosition;
                if (Mathf.Abs(lp.y - h) > 0.004f)
                {
                    _cam.transform.localPosition = new Vector3(lp.x, h, lp.z);
                    if (!_eyeHeightLogged && FlatFpsPlugin.Log != null)
                    {
                        _eyeHeightLogged = true;
                        FlatFpsPlugin.Log.LogWarning("[FlatFPS] no OVRCameraRig: eye anchor y "
                            + lp.y.ToString("F2") + " -> " + h.ToString("F2"));
                    }
                }
            }
        }

        private bool ReadHoldKeys()
        {
            string raw = FlatFpsPlugin.CursorHoldKey.Value;
            if (raw != _holdKeysRaw)
            {
                _holdKeysRaw = raw;
                List<KeyCode> list = new List<KeyCode>();
                foreach (string part in raw.Split(new char[] { ',', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        KeyCode k = (KeyCode)Enum.Parse(typeof(KeyCode), part.Trim(), true);
                        if (k != KeyCode.None)
                            list.Add(k);
                    }
                    catch
                    {
                        FlatFpsPlugin.Log.LogWarning("[FlatFPS] CursorHoldKey '" + part + "' is not a KeyCode name, ignored");
                    }
                }
                if (list.Count == 0)
                {
                    list.Add(KeyCode.LeftAlt);
                    list.Add(KeyCode.RightAlt);
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] CursorHoldKey '" + raw + "' unusable -> LeftAlt");
                }
                _holdKeys = list.ToArray();
            }

            for (int i = 0; i < _holdKeys.Length; i++)
                if (Input.GetKey(_holdKeys[i]))
                    return true;
            return false;
        }

        // ---------------- reference discovery ----------------
        private void Refresh(bool first)
        {
            _cam = Camera.main;
            if (_cam == null && Manager.Scene.IsInstance())
                _cam = Manager.Scene.Instance.MainCamera_;

            VRCameraFade fade = null;
            if (Manager.Scene.IsInstance())
                fade = Manager.Scene.Instance.VRCameraFade_;
            if (fade == null && _cam != null)
                fade = _cam.GetComponent<VRCameraFade>();
            _fade = fade;

            IllusionCamera ill = null;
            if (_cam != null)
                ill = _cam.GetComponent<IllusionCamera>();

            // 只在游戏自己的 InitCameraRig 已经跑完(CameraRig_ 已存在)后补挂，
            // 否则可能和游戏各挂一个 -> 双重位移。
            bool rigReady = Manager.Scene.IsInstance() && Manager.Scene.Instance.CameraRig_ != null;
            bool gameScene = FlatFpsPlugin.IsGameScene(ActiveSceneName());
            if (ill == null && _cam != null && GlobalVRKanojo.ForceMouse_ && rigReady && gameScene)
            {
                ill = _cam.gameObject.AddComponent<IllusionCamera>();
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] added IllusionCamera on " + _cam.name + " (game did not)");
            }
            _ill = ill;

            // 菜单/加载场景: 冻结第三人称相机控制器, 否则它把整个相机搬到 focus 前方 1.5 米,
            // 而 Title 这种菜单是绕玩家原点摆的 WorldSpace VR 界面 -> UI 整片出画, 只剩背景纯色。
            if (ill != null && FlatFpsPlugin.MenuSceneCameraFix.Value)
            {
                bool want = gameScene;
                if (ill.enabled != want)
                {
                    ill.enabled = want;
                    if (FlatFpsPlugin.Log != null)
                        FlatFpsPlugin.Log.LogInfo("[FlatFPS] IllusionCamera on '" + PathOf(_cam.transform)
                            + "' enabled=" + want + " (scene='" + ActiveSceneName() + "')");
                }
            }

            // 平面模式: 相机不再走立体路径，避免 OVR 相关代码把屏幕留白
            if (GlobalVRKanojo.ForceMouse_)
            {
                try
                {
                    Camera[] cams = Camera.allCameras;
                    for (int i = 0; i < cams.Length; i++)
                    {
                        Camera c = cams[i];
                        if (c != null && c.stereoTargetEye != StereoTargetEyeMask.None)
                            c.stereoTargetEye = StereoTargetEyeMask.None;
                    }
                }
                catch (Exception e)
                {
                    if (FlatFpsPlugin.Verbose.Value)
                        FlatFpsPlugin.Log.LogWarning("[FlatFPS] stereo pin: " + e.Message);
                }
            }

            if (first && FlatFpsPlugin.Log != null)
            {
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] cam=" + (_cam != null ? _cam.name : "<null>")
                    + " fade=" + (_fade != null) + " illusionCamera=" + (_ill != null)
                    + " scene='" + ActiveSceneName() + "'");
            }
        }

        // ---------------- fade watchdog (white screen fix) ----------------
        private static readonly string[] LoaderScenes =
            { "Init", "Init2", "CheckHMD", "SetHMD", "Logo2", "Logo", "Attention" };

        internal static bool IsLoaderScene(string scene)
        {
            for (int i = 0; i < LoaderScenes.Length; i++)
                if (LoaderScenes[i] == scene)
                    return true;
            return false;
        }

        private void ScanFades()
        {
            Object[] objs = Resources.FindObjectsOfTypeAll(typeof(VRCameraFade));
            List<VRCameraFade> list = new List<VRCameraFade>(objs.Length);
            for (int i = 0; i < objs.Length; i++)
            {
                VRCameraFade f = objs[i] as VRCameraFade;
                if (f == null || f.gameObject == null)
                    continue;
                if (!f.gameObject.activeInHierarchy && !f.IsFading)
                    continue;
                list.Add(f);
            }
            _fades = list.ToArray();
        }

        private int TrackIndex(int id)
        {
            for (int i = 0; i < _tracks.Count; i++)
                if (_tracks[i].Id == id)
                    return i;
            return -1;
        }

        private void Watchdog(float dt)
        {
            float now = Time.realtimeSinceStartup;
            if (now >= _nextFadeScan)
            {
                _nextFadeScan = now + 1f;
                ScanFades();
            }

            bool inLoader = IsLoaderScene(ActiveSceneName());

            for (int i = 0; i < _fades.Length; i++)
                WatchOne(_fades[i], dt, inLoader);

            if (_fade != null && _fade.gameObject != null && TrackIndex(_fade.GetInstanceID()) < 0)
                WatchOne(_fade, dt, inLoader);

            WatchOrphanFaders(dt, inLoader);
        }

        private void WatchOne(VRCameraFade fade, float dt, bool inLoader)
        {
            if (fade == null)
                return;

            Image img;
            float alpha;
            bool fading;
            try
            {
                img = fade.M_FadeImage;
                fading = fade.IsFading;
                alpha = img != null ? img.color.a : -1f;
            }
            catch (Exception e)
            {
                if (FlatFpsPlugin.Verbose.Value)
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] watchdog probe: " + e.Message);
                return;
            }

            int id = fade.GetInstanceID();
            int idx = TrackIndex(id);
            FadeTrack t;
            if (idx < 0)
            {
                t = new FadeTrack();
                t.Id = id;
                t.LastAlpha = -1f;
                _tracks.Add(t);
                idx = _tracks.Count - 1;
            }
            else
            {
                t = _tracks[idx];
            }

            if (img == null)
            {
                if (fading)
                {
                    t.Stuck += dt;
                    if (t.Stuck >= FlatFpsPlugin.StuckFadeSeconds.Value)
                    {
                        t.Stuck = 0f;
                        SetIsFading(fade, false);
                        FlatFpsPlugin.Log.LogWarning("[FlatFPS] repaired: IsFading stuck true with null fade image (scene='"
                            + ActiveSceneName() + "')");
                    }
                }
                else
                {
                    t.Stuck = 0f;
                }
                SaveTrack(idx, t);
                return;
            }

            if (fading)
            {
                t.Covered = 0f;
                if (t.LastAlpha >= 0f && Mathf.Abs(alpha - t.LastAlpha) <= 1e-4f)
                    t.Stuck += dt;
                else
                    t.Stuck = 0f;

                if (t.Stuck >= FlatFpsPlugin.StuckFadeSeconds.Value)
                {
                    t.Stuck = 0f;
                    t.Repairs++;
                    RepairFade(fade, "coroutine frozen while IsFading=true (alpha=" + alpha.ToString("F3") + ")");
                    CheckEscalate(fade, ref t);
                }
            }
            else
            {
                t.Stuck = 0f;
                if (inLoader || alpha < 0.99f)
                {
                    t.Covered = 0f;
                }
                else
                {
                    t.Covered += dt;
                    if (t.Covered >= FlatFpsPlugin.CoveredSceneSeconds.Value)
                    {
                        t.Covered = 0f;
                        t.Repairs++;
                        RepairFade(fade, "scene covered by opaque fade with no fade running (alpha=" + alpha.ToString("F3") + ")");
                        CheckEscalate(fade, ref t);
                    }
                }
            }

            t.LastAlpha = alpha;
            SaveTrack(idx, t);
        }

        private void ScanOrphanFaders()
        {
            Object[] objs = Resources.FindObjectsOfTypeAll(typeof(Image));
            List<Image> list = new List<Image>();
            float limit = Screen.width * 0.9f;
            for (int i = 0; i < objs.Length; i++)
            {
                Image img = objs[i] as Image;
                if (img == null || !img.enabled || !img.gameObject.activeInHierarchy)
                    continue;
                if (img.color.a < 0.99f)
                    continue;
                if (PathOf(img.transform).IndexOf("Fade", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Rect r = img.rectTransform.rect;
                if (Mathf.Abs(r.width) < limit)
                    continue;
                list.Add(img);
            }
            _orphanImgs = list.ToArray();
        }

        private void WatchOrphanFaders(float dt, bool inLoader)
        {
            float now = Time.realtimeSinceStartup;
            if (now >= _nextOrphanScan)
            {
                _nextOrphanScan = now + 1f;
                ScanOrphanFaders();
            }

            for (int i = 0; i < _orphanImgs.Length; i++)
            {
                Image img = _orphanImgs[i];
                if (img == null)
                    continue;
                int id = img.GetInstanceID();

                if (img.color.a < 0.99f)
                {
                    _orphanTimer.Remove(id);
                    continue;
                }

                float t;
                if (!_orphanTimer.TryGetValue(id, out t))
                    t = 0f;

                if (inLoader)
                {
                    _orphanTimer[id] = 0f;
                    continue;
                }

                t += dt;
                _orphanTimer[id] = t;

                if (t >= FlatFpsPlugin.CoveredSceneSeconds.Value)
                {
                    _orphanTimer[id] = 0f;
                    try
                    {
                        Color c = img.color;
                        img.color = new Color(c.r, c.g, c.b, 0f);
                        FlatFpsPlugin.Log.LogWarning("[FlatFPS] repaired orphan white panel '" + PathOf(img.transform)
                            + "' (alpha=1 for " + FlatFpsPlugin.CoveredSceneSeconds.Value.ToString("F0") + "s, no fade runner)");
                    }
                    catch (Exception e)
                    {
                        FlatFpsPlugin.Log.LogWarning("[FlatFPS] orphan repair failed: " + e.Message);
                    }
                }
            }
        }

        private void SaveTrack(int idx, FadeTrack t)
        {
            if (idx >= 0 && idx < _tracks.Count)
                _tracks[idx] = t;
        }

        private void CheckEscalate(VRCameraFade fade, ref FadeTrack t)
        {
            if (FlatFpsPlugin.HardKillSeconds.Value <= 0f || t.Repairs < 2)
                return;

            try
            {
                Image img = fade.M_FadeImage;
                if (img == null)
                    return;
                Canvas canvas = img.canvas;
                GameObject target = canvas != null ? canvas.gameObject
                    : (img.transform.parent != null ? img.transform.parent.gameObject : null);
                if (target == null || !target.activeSelf)
                    return;
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] hardened: deactivating fader panel '" + PathOf(target.transform)
                    + "' after " + t.Repairs + " repairs");
                target.SetActive(false);
                t.Repairs = 0;
            }
            catch (Exception e)
            {
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] escalate failed: " + e.Message);
            }
        }

        private void RepairFade(VRCameraFade fade, string why)
        {
            try
            {
                Image img = fade.M_FadeImage;
                if (img != null)
                {
                    Color c = img.color;
                    img.color = new Color(c.r, c.g, c.b, 0f);
                    if (!img.enabled)
                        img.enabled = true;
                }
                SetIsFading(fade, false);
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] repaired white screen: " + why + " (scene='"
                    + ActiveSceneName() + "' target='" + PathOf(fade.transform) + "')");
            }
            catch (Exception e)
            {
                FlatFpsPlugin.Log.LogError("[FlatFPS] RepairFade failed: " + e);
            }
        }

        private static void SetIsFading(VRCameraFade fade, bool value)
        {
            try
            {
                if (IsFadingField != null && fade != null)
                    IsFadingField.SetValue(fade, value);
            }
            catch (Exception e)
            {
                if (FlatFpsPlugin.Verbose.Value)
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] set m_IsFading: " + e.Message);
            }
        }


        // ---------------- free look ----------------
        private void DoFreeLook()
        {
            if (_ill == null || _viewMode != 1)
                return;
            if (_ill.Lock || _ill.OnUI)
                return;
            if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2))
                return; // 原版按住鼠标的拖动逻辑优先, 避免双重旋转
            if (Cursor.lockState != CursorLockMode.Locked)
                return;

            float sx = Input.GetAxis("Mouse X");
            float sy = Input.GetAxis("Mouse Y");
            if (Mathf.Abs(sx) < 1e-4f && Mathf.Abs(sy) < 1e-4f)
                return;

            // MouseSpeed 800 -> 1.6 (游戏的 "Mouse X" 轴本身已经缩放过, 这里是乘数)
            float k = Mathf.Max(0.01f, FlatFpsPlugin.MouseSpeed.Value / 500f);
            float pitch = -sy * k;
            if (FlatFpsPlugin.InvertY.Value)
                pitch = -pitch;

            InvokeRotate(_ill, new Vector3(pitch, sx * k, 0f));
        }

        private void InvokeRotate(IllusionCamera target, Vector3 delta)
        {
            if (!_rotateActionTried)
            {
                _rotateActionTried = true;
                try
                {
                    MethodInfo mi = typeof(IllusionCamera).GetMethod("Rotate",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (mi != null)
                        _rotateAction = (Action<IllusionCamera, Vector3>)Delegate.CreateDelegate(
                            typeof(Action<IllusionCamera, Vector3>), mi);
                }
                catch (Exception e)
                {
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] bind IllusionCamera.Rotate failed: " + e.Message);
                }
            }

            if (_rotateAction != null)
            {
                _rotateAction(target, delta);
                return;
            }

            // 退化路径: 直接累加 rotate 字段(不做 ±80 夹取, 极少触发)
            if (RotateField != null)
            {
                Vector3 cur = (Vector3)RotateField.GetValue(target);
                RotateField.SetValue(target, cur + delta);
            }
        }

        // ---------------- cursor ----------------
        /// <summary>
        /// 免VR 菜单鼠标点击桥。本作菜单完全靠 OVRRaycaster（从 VR 射线指针发射的 raycaster），
        /// 没有头显就没有射线, 鼠标点不到任何按钮。这里在菜单/加载场景里:
        ///  1) 保证 EventSystem 上跑的是 StandaloneInputModule(鼠标), 其它模块(OVRInputModule)停用;
        ///  2) 给每个世界空间 Canvas 补 GraphicRaycaster 并把 worldCamera 指向当前相机;
        ///  3) 停用 Canvas 上的 OVRRaycaster, 免得它用无效射线抢输入。
        /// 正片场景一律不动, 保持游戏原本的交互。
        /// </summary>
        private void EnsureFlatUi()
        {
            if (FlatFpsPlugin.VrMode)
                return;

            if (FlatFpsPlugin.FlatUiInput == null || !FlatFpsPlugin.FlatUiInput.Value)
                return;
            if (_uiBridged)
                return;
            if (FlatFpsPlugin.IsGameScene(ActiveSceneName()))
                return;
            // v1.21.0: 直连点击 / 物理点击都已经不依赖 EventSystem 桥, 而这套桥会把 EventSystem.current
            // 换成我们自己的 StandaloneInputModule, 并给场景 Canvas 强塞 GraphicRaycaster ——
            // 游戏自己的悬停/点击判定(以及 IllusionCamera.OnUI)会因此看到"指针在 UI 上", 表现为偶发点不到人。
            // 默认关掉; 真遇到直连点击失效的怪场景再开。
            if (FlatFpsPlugin.LegacyUiBridge == null || !FlatFpsPlugin.LegacyUiBridge.Value)
                return;

            _uiBridged = true;
            int rays = 0, ovrOff = 0;
            bool esMade = false;

            EventSystem es = EventSystem.current;
            if (es == null)
            {
                GameObject go = new GameObject("VRK_FlatFPS_EventSystem");
                Object.DontDestroyOnLoad(go);
                es = go.AddComponent<EventSystem>();
                go.AddComponent<StandaloneInputModule>();
                esMade = true;
            }
            else
            {
                BaseInputModule[] mods = es.GetComponents<BaseInputModule>();
                bool keepOvr = FlatFpsPlugin.KeepOvrInput == null || FlatFpsPlugin.KeepOvrInput.Value;
                for (int i = 0; i < mods.Length; i++)
                {
                    if (mods[i] == null || mods[i] is StandaloneInputModule)
                        continue;
                    // OVRInputModule 是游戏 VR 菜单的原生输入链路, 停掉它 = 菜单指针事件全断。默认保留。
                    if (keepOvr && mods[i].GetType().Name.IndexOf("OVR", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    if (mods[i].enabled)
                        mods[i].enabled = false;
                }
                if (es.GetComponent<StandaloneInputModule>() == null)
                    es.gameObject.AddComponent<StandaloneInputModule>();
            }

            Object[] all = Resources.FindObjectsOfTypeAll(typeof(Canvas));
            for (int i = 0; i < all.Length; i++)
            {
                Canvas cv = all[i] as Canvas;
                if (cv == null || cv.gameObject.scene.name == null)
                    continue;
                if (cv.renderMode == RenderMode.ScreenSpaceOverlay)
                    continue;

                GraphicRaycaster gr = cv.GetComponent<GraphicRaycaster>();
                if (gr == null)
                {
                    gr = cv.gameObject.AddComponent<GraphicRaycaster>();
                    gr.ignoreReversedGraphics = true;
                    rays++;
                }
                // Unity 5.4 的 GraphicRaycaster 没有 worldCamera 字段(6.x 之后才加),
                // 它内部用的就是 Canvas.worldCamera, 所以相机挂到 Canvas 上。
                if (cv.worldCamera == null && _cam != null)
                    cv.worldCamera = _cam;

                if (FlatFpsPlugin.KeepOvrInput == null || !FlatFpsPlugin.KeepOvrInput.Value)
                {
                    BaseRaycaster[] rs = cv.GetComponents<BaseRaycaster>();
                    for (int j = 0; j < rs.Length; j++)
                    {
                        BaseRaycaster r = rs[j];
                        if (r == null || r is GraphicRaycaster)
                            continue;
                        if (r.GetType().Name.IndexOf("OVRRaycaster", StringComparison.Ordinal) >= 0 && r.enabled)
                        {
                            r.enabled = false;
                            ovrOff++;
                        }
                    }
                }
            }

            ClearRaycastBlockers(ActiveSceneName());

            if (FlatFpsPlugin.Log != null)
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] flat UI bridge in '" + ActiveSceneName()
                    + "': eventSystem=" + (esMade ? "created" : "patched")
                    + " graphicRaycaster+=" + rays + " ovrRaycaster-off=" + ovrOff
                    + " -> 左键可直接点菜单(指针在菜单里不会锁定)");
        }

        /// <summary>
        /// 只读诊断: 场景里到底有几个 IllusionCamera、挂在谁身上、enabled 状态。
        /// 游戏在 InitCameraRig() 里会对 MainCamera_ 和 CameraRig_ 各挂一次,
        /// 两份同时跑会互相搬相机(位移翻倍/抖动), 这里先把事实打出来。
        /// </summary>
        // ---------------- v1.30.0 鼠标当手 / 关闭输入法 ----------------

        [System.Runtime.InteropServices.DllImport("imm32.dll")]
        private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string cls, string title);

        private IntPtr _imeWnd;
        private float _imeNextAt;
        private bool _imeLogged;
        private bool _imeDisabledThread;

        // 中文输入法激活时按键先进输入法, Unity 的 Input.GetKey 收不到,
        // 表现就是 WASD 必须先切英文。这里把窗口的 IME 上下文摘掉。
        [System.Runtime.InteropServices.DllImport("imm32.dll")]
        private static extern bool ImmDisableIME(int idThread);

        private void EnsureImeOff()
        {
            if (!FlatFpsPlugin.DisableIme.Value) return;

            // Unity 自己的开关每帧都压一遍(开销极小, 而且 Unity 会在失焦/切场景时重置它)
            try { Input.imeCompositionMode = IMECompositionMode.Off; } catch { }

            float now = Time.realtimeSinceStartup;
            if (now < _imeNextAt) return;
            _imeNextAt = now + 0.25f;   // 4Hz: 窗口重建/换焦点后能立刻补回来

            try
            {
                if (!_imeDisabledThread)
                {
                    _imeDisabledThread = true;
                    try { ImmDisableIME(-1); } catch { }
                }

                if (_imeWnd == IntPtr.Zero)
                {
                    // Unity 的窗口类名固定是 UnityWndClass
                    _imeWnd = FindWindowW("UnityWndClass", null);
                    if (_imeWnd == IntPtr.Zero) _imeWnd = FindWindowW(null, Application.productName);
                    if (_imeWnd == IntPtr.Zero) _imeWnd = GetActiveWindow();
                    if (_imeWnd != IntPtr.Zero)
                        FlatFpsPlugin.Log.LogInfo("[FlatFPS] IME target window hwnd=" + _imeWnd);
                }
                if (_imeWnd == IntPtr.Zero) return;

                IntPtr old = ImmAssociateContext(_imeWnd, IntPtr.Zero);
                if (!_imeLogged)
                {
                    _imeLogged = true;
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] IME detached (prev context=" + old +
                                              ") - Chinese IME can no longer swallow WASD");
                }
            }
            catch (Exception e)
            {
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] IME detach failed: " + e.Message);
            }
        }

        private object _handGrabbed;   // Grabbable
        private object _handUsed;      // Hand
        private bool _handHeld;
        private Vector3? _handTarget;
        private Vector3 _handHome;
        private bool _handHomeValid;
        private string _handHitName;
        private string _handHitLogged;
        private System.Reflection.FieldInfo _grabFlagField;
        private bool _grabFlagTouched;
        private string _handGrabLogged;

        // 左键当右手: 命中 Grabbable -> GrabBegin(hand, collider); 松开 -> hand.ForceRelease(grabbable)
        // 同时把 Hand.GrabFlag 置真, 让"需要 VR 手"的姿态逻辑走游戏自带的鼠标分支。
        // 按住左键 = 把游戏里那只 Hand 瞬移到准星命中点, 让它的抓取碰撞体真正
        // 压到目标上, 由游戏自己的 OnTriggerEnter / GrabBegin 触发交互。
        // 只对"命中点"做瞬移, 松开就复位, 不碰手部动画。
        private void HandleMouseHand()
        {
            if (FlatFpsPlugin.VrMode) return;
            if (!FlatFpsPlugin.MouseHandGrab.Value) return;
            if (!FlatFpsPlugin.IsGameScene(ActiveSceneName())) return;
            if (_cursorFree) return;
            if (_cam == null) return;

            bool down = Input.GetMouseButtonDown(0);
            bool held = Input.GetMouseButton(0);

            if (down && !_handHeld)
            {
                _handHeld = true;
                _caressActive = false;
                if (FlatFpsPlugin.MouseBodyCaress != null && FlatFpsPlugin.MouseBodyCaress.Value && TryGazeTouch())
                {
                    // 游戏自己已经 GrabBegin 了, 不要再瞬移手, 否则会和爱抚动画打架
                    _handHeld = true;
                    return;
                }
                RaycastHit hit;
                Ray ray = _cam.ScreenPointToRay(MousePixel());
                float reach = Mathf.Clamp(FlatFpsPlugin.MouseHandReach.Value, 0.5f, 20f);
                if (Physics.Raycast(ray, out hit, reach, ~0, QueryTriggerInteraction.Collide))   // v1.36.1: 交互图标/VKHit 多是 trigger, 必须让射线命中
                {
                    _handTarget = hit.point + hit.normal * 0.02f;
                    _handHitName = hit.collider.name;
                    object hand = FindHand();
                    if (hand != null && FlatFpsPlugin.MouseHandTeleport.Value)
                    {
                        Transform ht = ((MonoBehaviour)hand).transform;
                        if (!_handHomeValid)
                        {
                            _handHome = ht.position;
                            _handHomeValid = true;
                        }
                        _handUsed = hand;
                    }
                    object grab = FindGrabbable(hit.collider.gameObject);
                    if (grab != null)
                    {
                        try
                        {
                            System.Reflection.MethodInfo m = grab.GetType().GetMethod("GrabBegin",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (m != null)
                            {
                                m.Invoke(grab, new object[] { _handUsed, hit.collider });
                                _handGrabbed = grab;
                                SetGrabFlag(_handUsed, true);
                                string tag = grab.GetType().Name + "@" + hit.collider.name;
                                if (_handGrabLogged != tag)
                                {
                                    _handGrabLogged = tag;
                                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] mouse-hand grab -> " + tag);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            FlatFpsPlugin.Log.LogWarning("[FlatFPS] mouse-hand GrabBegin failed: " + e.Message);
                        }
                    }
                    if (_handHitLogged != _handHitName)
                    {
                        _handHitLogged = _handHitName;
                if (FlatFpsPlugin.MouseHandLog != null && FlatFpsPlugin.MouseHandLog.Value)
                            FlatFpsPlugin.Log.LogInfo("[FlatFPS] mouse-hand aim -> '" + _handHitName +
                                                  "' at " + _handTarget.Value.ToString("F2") +
                                                  (grab != null ? " (grabbable)" : " (physics only)"));
                    }
                }
                else
                {
                    _handTarget = null;
                    _handHitName = null;
                }
            }

            // 按住期间持续压住手的位置(游戏每帧会写手, 所以每帧都要压一遍)
            if (held && _handTarget.HasValue && _handUsed != null)
            {
                try
                {
                    ((MonoBehaviour)_handUsed).transform.position = _handTarget.Value;
                }
                catch { }
            }

            if (!held && _handHeld)
            {
                _handHeld = false;
                if (_caressActive)
                {
                    _caressActive = false;
                    ReleaseGazeTouch();
                    return;
                }
                _handTarget = null;
                try
                {
                    if (_handGrabbed != null && _handUsed != null)
                    {
                        System.Reflection.MethodInfo fr = _handUsed.GetType().GetMethod("ForceRelease",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (fr != null) fr.Invoke(_handUsed, new object[] { _handGrabbed });
                    }
                    if (_handUsed != null && _handHomeValid)
                        ((MonoBehaviour)_handUsed).transform.position = _handHome;
                    SetGrabFlag(_handUsed, false);
                }
                catch (Exception e)
                {
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] mouse-hand release failed: " + e.Message);
                }
                _handGrabbed = null;
                _handUsed = null;
            }
        }

        // v1.32.0: 直接调游戏的凝视交互 —— 这是"发蓝光部位"唯一的真实入口。
        // BodyTouchManager.CheckGazeTouch(Hand.HANDEDNESS) 会自己从相机打射线,
        // 命中最近的 BodyCaressGrabbable 并 GrabBegin, 后续爱抚/动画/音效全由游戏接管。
        private object _touchMgr;
        private System.Reflection.MethodInfo _gazeTouchMi;
        private System.Reflection.MethodInfo _handReleaseMi;
        private object _handednessR;
        private bool _caressActive;

        private bool TryGazeTouch()
        {
            try
            {
                if (!ResolveTouch()) return false;

                // v1.33.0: 不再用游戏 camTrans 的射线 —— 从"准星正中心"自己打,
                // 这样抓到的点和准星必然一致。
                Ray aim = _cam != null
                    ? _cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f))
                    : new Ray(transform.position, transform.forward);
                float reach = Mathf.Clamp(FlatFpsPlugin.GazeGrabDistance.Value, 0.5f, 10f);

                object best = null;
                float bestDist = reach;
                string bestName = null;
                Array arr = _touchBodyCaress();
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        object g = arr.GetValue(i);
                        if (g == null) continue;
                        if (IsGrabbed(g)) continue;
                        Collider gc = GrabColliderOf(g);
                        if (gc == null) continue;
                        RaycastHit h;
                        if (!gc.Raycast(aim, out h, reach)) continue;
                        if (h.distance > bestDist) continue;
                        best = g;
                        bestDist = h.distance;
                        bestName = gc.name;
                    }
                }

                // 对照: 游戏自己那条射线打到哪里(只记录, 不参与判定)
                string gameHit = "n/a";
                if (FlatFpsPlugin.GazeAimLog != null && FlatFpsPlugin.GazeAimLog.Value)
                    gameHit = ProbeGameCamHit(arr, reach);

                if (best == null)
                {
                    if (FlatFpsPlugin.GazeAimLog != null && FlatFpsPlugin.GazeAimLog.Value && FlatFpsPlugin.Log != null)
                        FlatFpsPlugin.Log.LogInfo("[FlatFPS] gaze aim: crosshair=none game=" + gameHit +
                                                  " reach=" + reach.ToString("F1"));
                    return false;
                }

                object handedness = _handednessR;
                System.Reflection.MethodInfo gb = best.GetType().GetMethod("GrabBegin",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null, new Type[] { handedness.GetType() }, null);
                if (gb == null) gb = best.GetType().GetMethod("GrabBegin");
                if (gb == null) return false;
                gb.Invoke(best, new object[] { handedness });

                if (!IsCaressGrabbed()) return false;
                _caressActive = true;
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] gaze touch -> grabbed '" + bestName + "' d=" +
                                              bestDist.ToString("F2") + " (from screen centre; game ray=" + gameHit + ")");
                return true;
            }
            catch (Exception e)
            {
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] gaze touch failed: " + e.Message);
                return false;
            }
        }

        // 把 bodyCaress 数组缓存下来(每次进入新场景会换实例)
        private FieldInfo _bodyCaressFi;
        private object _bodyCaressOwner;

        private Array _touchBodyCaress()
        {
            try
            {
                if (_touchMgr == null) return null;
                if (_bodyCaressFi == null || !ReferenceEquals(_bodyCaressOwner, _touchMgr))
                {
                    _bodyCaressFi = AccessTools.Field(_touchMgr.GetType(), "bodyCaress");
                    _bodyCaressOwner = _touchMgr;
                }
                if (_bodyCaressFi == null) return null;
                return _bodyCaressFi.GetValue(_touchMgr) as Array;
            }
            catch { return null; }
        }

        private static Collider GrabColliderOf(object g)
        {
            try
            {
                PropertyInfo p = g.GetType().GetProperty("GrabCollider", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p != null) return p.GetValue(g, null) as Collider;
                FieldInfo f = AccessTools.Field(g.GetType(), "GrabCollider");
                return f == null ? null : f.GetValue(g) as Collider;
            }
            catch { return null; }
        }

        private static bool IsGrabbed(object g)
        {
            try
            {
                PropertyInfo p = g.GetType().GetProperty("IsGrabbed", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p != null) return (bool)p.GetValue(g, null);
                return false;
            }
            catch { return false; }
        }

        // 只用于日志: 复制游戏那条射线的命中结果
        private string ProbeGameCamHit(Array arr, float reach)
        {
            try
            {
                if (_touchMgr == null || arr == null) return "n/a";
                FieldInfo cf = AccessTools.Field(_touchMgr.GetType(), "camTrans");
                Transform ct = cf == null ? null : cf.GetValue(_touchMgr) as Transform;
                if (ct == null) return "camTrans=null";
                Ray ray = new Ray(ct.position, ct.rotation * Vector3.forward);
                string best = "none";
                float bd = reach;
                for (int i = 0; i < arr.Length; i++)
                {
                    object g = arr.GetValue(i);
                    if (g == null) continue;
                    Collider gc = GrabColliderOf(g);
                    if (gc == null) continue;
                    RaycastHit h;
                    if (gc.Raycast(ray, out h, reach) && h.distance <= bd)
                    {
                        bd = h.distance;
                        best = gc.name + "@" + bd.ToString("F2");
                    }
                }
                float fwdErr = Vector3.Angle(ct.forward, _cam != null ? _cam.transform.forward : Vector3.forward);
                return best + " camFwdErr=" + fwdErr.ToString("F1") + "deg camPos=" + ct.position.ToString("F2");
            }
            catch { return "probe-error"; }
        }

        private void ReleaseGazeTouch()
        {
            try
            {
                if (ResolveTouch() && _handReleaseMi != null)
                {
                    _handReleaseMi.Invoke(_touchMgr, new object[] { _handednessR });
                    if (FlatFpsPlugin.Log != null)
                        FlatFpsPlugin.Log.LogInfo("[FlatFPS] gaze touch released");
                }
            }
            catch (Exception e)
            {
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] gaze touch release failed: " + e.Message);
            }
        }

        private bool IsCaressGrabbed()
        {
            try
            {
                System.Reflection.FieldInfo uf = AccessTools.Field(_touchMgr.GetType(), "useHand");
                if (uf == null) return false;
                Array arr = uf.GetValue(_touchMgr) as Array;
                if (arr == null || arr.Length < 2) return false;
                return arr.GetValue(1) != null;
            }
            catch { return false; }
        }

        private bool ResolveTouch()
        {
            if (_touchMgr != null && _gazeTouchMi != null) return true;
            UnityEngine.Object[] bts = Resources.FindObjectsOfTypeAll(typeof(BodyTouchManager));
            for (int i = 0; i < bts.Length; i++)
            {
                MonoBehaviour mb = bts[i] as MonoBehaviour;
                if (mb == null || mb.gameObject.scene.name == null) continue;
                _touchMgr = mb;
                break;
            }
            if (_touchMgr == null) return false;
            Type t = _touchMgr.GetType();
            _gazeTouchMi = t.GetMethod("CheckGazeTouch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            _handReleaseMi = t.GetMethod("HandForceRelease", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (_handednessR == null)
            {
                Type ht = AccessTools.TypeByName("Hand.HANDEDNESS");
                if (ht == null)
                {
                    Type[] all = t.Assembly.GetTypes();
                    for (int i = 0; i < all.Length; i++)
                        if (all[i].IsEnum && all[i].Name == "HANDEDNESS") { ht = all[i]; break; }
                }
                if (ht != null) _handednessR = Enum.Parse(ht, "R");
            }
            return _gazeTouchMi != null && _handednessR != null;
        }

        private static object FindGrabbable(GameObject go)
        {
            Transform t = go.transform;
            for (int i = 0; i < 8 && t != null; i++, t = t.parent)
            {
                Component[] cs = t.GetComponents(typeof(Component));
                for (int k = 0; k < cs.Length; k++)
                {
                    if (cs[k] == null) continue;
                    string n = cs[k].GetType().Name;
                    if (n == "BodyCaressGrabbable") return null;   // 爱抚交给游戏自己的凝视路径
                    if (n == "Grabbable") return cs[k];
                }
            }
            return null;
        }

        private static object FindHand()
        {
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour));
            for (int i = 0; i < all.Length; i++)
            {
                MonoBehaviour mb = all[i] as MonoBehaviour;
                if (mb == null) continue;
                if (mb.GetType().Name != "Hand") continue;
                if (mb.gameObject.scene.name == null) continue;
                return mb;
            }
            return null;
        }

        private void SetGrabFlag(object hand, bool on)
        {
            if (hand == null) return;
            try
            {
                if (_grabFlagField == null)
                    _grabFlagField = hand.GetType().GetField("GrabFlag",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (_grabFlagField != null)
                {
                    _grabFlagField.SetValue(hand, on);
                    if (on && !_grabFlagTouched)
                    {
                        _grabFlagTouched = true;
                        FlatFpsPlugin.Log.LogInfo("[FlatFPS] Hand.GrabFlag -> true (mouse pose path enabled)");
                    }
                }
            }
            catch { }
        }

        private void HandleDumpKey()
        {
            string raw = FlatFpsPlugin.DumpKeyRaw == null ? "F10" : FlatFpsPlugin.DumpKeyRaw.Value;
            if (raw != _dumpKeyRaw)
            {
                _dumpKeyRaw = raw;
                try { _dumpKey = (KeyCode)System.Enum.Parse(typeof(KeyCode), raw, true); }
                catch { _dumpKey = KeyCode.F10; }
            }
            if (!Input.GetKeyDown(_dumpKey))
                return;
            FlatFpsPlugin.Log.LogInfo("[FlatFPS] on-demand dump requested (" + _dumpKey + ") in '" + ActiveSceneName() + "'");
            DumpRenderState("ondemand");
            DumpGraphics();
        }

        // 0 = 第一人称(和戴 VR 一样的第一人称, 可用 WASD 走动)
        // 1 = 第三人称(游戏的 IllusionCamera 环绕视角)
        // 2 = 自由视角(相机完全自己开, 可飞行)
        private static int _viewMode = 0;
        internal static int ViewMode { get { return _viewMode; } }

        private static float _savedDistance = -1f;
        private static FieldInfo _distField;
        private static KeyCode _viewKey = KeyCode.F;
        private static string _viewKeyRaw;

        // 第一人称: 相机局部姿态自己写, 眼睛高度由 trackingSpace 提供
        private static float _fpYaw;
        private static float _fpPitch;
        private bool _fpInit;
        private static Vector3 _lastRigPos;
        private bool _rigPosInit;

        // 运行期眼高：第一人称下 PageUp/PageDown 实时微调，-1 = 用配置值
        private static float _eyeOverride = -1f;
        private static float _eyeSpamAt = -1f;
        private bool _fpLogged;

        // 自由视角: 相机世界位姿完全自己写
        private static float _freeYaw;
        private static float _freePitch;
        private bool _freeInit;

        /// <summary>
        /// 切换视角时的一次性准备。
        /// 0 = 真实第一人称: 相机不再交给 IllusionCamera, 从当前朝向继续;
        /// 1 = 第三人称: 把 distance 恢复成原版的环绕距离, 之后完全交回游戏;
        /// 2 = 自由视角: 下一帧从当前世界位姿接管。
        /// </summary>
        private void EnterMode(int mode)
        {
            if (mode == 0)
            {
                _fpInit = false;
                Transform t = _cam == null ? null : _cam.transform;
                if (t != null)
                {
                    Vector3 e = t.localEulerAngles;
                    _fpPitch = (e.x > 180f) ? (e.x - 360f) : e.x;
                    _fpYaw = e.y;
                    _fpInit = true;
                    if (FlatFpsPlugin.FirstPersonRotateRig != null && FlatFpsPlugin.FirstPersonRotateRig.Value
                        && RigRoot() != null)
                        _fpYaw = RigRoot().eulerAngles.y;   // v1.27.0: yaw 归 rig 管, 进第一人称不跳方向
                }
                _rigPosInit = true;
                _lastRigPos = (RigRoot() == null) ? Vector3.zero : RigRoot().position;
                _fpLogged = false;
            }
            else if (mode == 1)
            {
                float d = (_savedDistance > 0.2f) ? _savedDistance : FlatFpsPlugin.ThirdPersonDistance.Value;
                SetDistanceAll(MaxFloat(d, 0.2f));
                _savedDistance = -1f;
                Transform t = _cam == null ? null : _cam.transform;
                if (t != null)
                    t.localPosition = Vector3.zero;
            }
            else
            {
                _freeInit = false;
            }
        }

        private static void SetDistanceAll(float d)
        {
            FieldInfo fi = _distField;
            if (fi == null)
            {
                fi = typeof(IllusionCamera).GetField("distance",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _distField = fi;
            }
            if (fi == null)
                return;
            Object[] all = Resources.FindObjectsOfTypeAll(typeof(IllusionCamera));
            for (int i = 0; i < all.Length; i++)
            {
                IllusionCamera ic = all[i] as IllusionCamera;
                if (ic == null)
                    continue;
                try { fi.SetValue(ic, d); } catch { }
            }
        }

        private void HandleViewKey()
        {
            if (FlatFpsPlugin.VrMode)
                return;             // \u771f VR \u4e0d\u5207\u89c6\u89d2, \u76f8\u673a\u5f52\u5934\u663e

            if (FlatFpsPlugin.ViewToggle == null || !FlatFpsPlugin.ViewToggle.Value)
                return;
            string raw = (FlatFpsPlugin.ViewToggleKey == null) ? "F" : FlatFpsPlugin.ViewToggleKey.Value;
            if (raw != _viewKeyRaw)
            {
                _viewKeyRaw = raw;
                try { _viewKey = (KeyCode)System.Enum.Parse(typeof(KeyCode), raw, true); }
                catch { _viewKey = KeyCode.F; }
            }
            if (!FlatFpsPlugin.IsGameScene(ActiveSceneName()))
                return;
            if (!Input.GetKeyDown(_viewKey))
                return;

            _viewMode = (_viewMode + 1) % 3;
            EnterMode(_viewMode);
            if (FlatFpsPlugin.Log != null)
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] view mode -> " + ViewModeName(_viewMode)
                    + " (" + _viewKey + ")");
            HelpOverlay.Toast(HelpOverlay.ViewModeToastText(_viewMode), 2.2f);
        }

        private static string ViewModeName(int mode)
        {
            if (mode == 0) return "第一人称(VR 视角)";
            if (mode == 2) return "自由视角";
            return "第三人称(环绕)";
        }

        /// <summary>
        /// 玩家所在的根节点(相机 rig)。第一人称的 WASD 是"人走过去", 所以要移动它而不是相机。
        /// </summary>
        private Transform RigRoot()
        {
            try
            {
                if (Manager.Scene.Instance != null && Manager.Scene.Instance.CameraRig_ != null)
                    return Manager.Scene.Instance.CameraRig_.transform;
            }
            catch { }
            if (_cam == null)
                return null;
            Transform t = _cam.transform;
            while (t.parent != null && t.parent.parent != null)
                t = t.parent;
            return t;
        }

        /// <summary>
        /// 真实第一人称(与戴 VR 一致): 视点就是玩家的眼睛。
        /// 相机局部姿态清零, 眼高由 OVRCameraRig.trackingSpace 提供;
        /// 鼠标只转相机(不移动它), WASD 平移整个 rig —— 于是"人走动, 视野跟着走"。
        /// 游戏自己的 IllusionCamera 已被 Prefix 关掉, 不会来抢相机。
        /// </summary>
        private void DoFirstPerson(float dt)
        {
            if (FlatFpsPlugin.VrMode)
                return;             // \u771f VR \u65f6\u773c\u775b\u4f4d\u59ff\u7531\u5934\u663e\u51b3\u5b9a

            if (_viewMode != 0 || _cam == null)
                return;
            if (!FlatFpsPlugin.IsGameScene(ActiveSceneName()))
                return;
            Transform t = _cam.transform;

            if (!_fpInit)
            {
                _fpInit = true;
                Vector3 e = t.localEulerAngles;
                _fpPitch = (e.x > 180f) ? (e.x - 360f) : e.x;
                _fpYaw = e.y;
            }

            // 第一人称下 PageUp/PageDown 不做别的事，就用来调眼高（5cm 一档）
            float cfgH = FlatFpsPlugin.EyeHeight.Value;
            if (_eyeOverride < 0f)
                _eyeOverride = cfgH;
            if (Input.GetKeyDown(KeyCode.PageUp))
            {
                _eyeOverride = Mathf.Clamp(_eyeOverride + 0.05f, 0.30f, 2.50f);
                HelpOverlay.Toast("\u773c\u9ad8 " + _eyeOverride.ToString("F2") + " m", 1.2f);
            }
            if (Input.GetKeyDown(KeyCode.PageDown))
            {
                _eyeOverride = Mathf.Clamp(_eyeOverride - 0.05f, 0.30f, 2.50f);
                HelpOverlay.Toast("\u773c\u9ad8 " + _eyeOverride.ToString("F2") + " m", 1.2f);
            }

            if (!_fpLogged)
            {
                _fpLogged = true;
                Transform rr = RigRoot();
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] first person engaged: rig="
                        + (rr == null ? "null" : rr.position.ToString("F2"))
                        + " camWorld=" + t.position.ToString("F2")
                        + " rigY=" + (rr == null ? 0f : rr.position.y).ToString("F2")
                        + " (eye = rigY + trackingSpace.y)");
            }

            float k = Mathf.Max(0.01f, FlatFpsPlugin.MouseSpeed.Value / 500f) * 10f;
            if (FlatFpsPlugin.FreeLook.Value && !_cursorFree)
            {
                _fpYaw += Input.GetAxis("Mouse X") * k;
                _fpPitch += (FlatFpsPlugin.InvertY.Value ? Input.GetAxis("Mouse Y") : -Input.GetAxis("Mouse Y")) * k;
                _fpPitch = Mathf.Clamp(_fpPitch, -89f, 89f);
            }

            // 眼睛就在 rig 原点(局部零位), 朝向由鼠标决定
            t.localPosition = Vector3.zero;

            Transform rig = RigRoot();
            if (rig == null)
            {
                t.localRotation = Quaternion.Euler(_fpPitch, _fpYaw, 0f);
                return;
            }

            // v1.27.0: 左右转向写到 rig 根节点, 上下俯仰留在相机 —— 游戏的菜单是按 rig 朝向摆的,
            // 以前只转相机, 于是"画面转了但游戏仍以为你朝原方向", 按 ESC 的菜单仍在背后。
            if (FlatFpsPlugin.FirstPersonRotateRig != null && FlatFpsPlugin.FirstPersonRotateRig.Value)
            {
                rig.localRotation = Quaternion.Euler(0f, _fpYaw, 0f);
                t.localRotation = Quaternion.Euler(_fpPitch, 0f, 0f);
                if (!_rigYawLogged)
                {
                    _rigYawLogged = true;
                    if (FlatFpsPlugin.Log != null)
                        FlatFpsPlugin.Log.LogInfo("[FlatFPS] first person: yaw applied to rig '" + PathOf(rig)
                            + "' (game menus follow the view), pitch stays on the camera");
                }
            }
            else
            {
                t.localRotation = Quaternion.Euler(_fpPitch, _fpYaw, 0f);
            }
            if (!_rigPosInit)
            {
                _rigPosInit = true;
                _lastRigPos = rig.position;
            }

            float sp = FlatFpsPlugin.FirstPersonMoveSpeed.Value;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                sp *= FlatFpsPlugin.FreeSprintMultiplier.Value;

            Vector3 fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            Vector3 right = Vector3.ProjectOnPlane(t.right, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            right.Normalize();

            Vector3 mv = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) mv += fwd;
            if (Input.GetKey(KeyCode.S)) mv -= fwd;
            if (Input.GetKey(KeyCode.D)) mv += right;
            if (Input.GetKey(KeyCode.A)) mv -= right;
            if (mv.sqrMagnitude > 1e-6f)
                rig.position += mv.normalized * sp * Mathf.Max(0f, dt);

            _lastRigPos = rig.position;
        }

        /// <summary>
        /// 自由视角: 相机脱离一切(人和 rig), 世界位姿完全由我们写。
        /// 鼠标看、WASD 平移、PageUp/PageDown 升降、Shift 加速。
        /// </summary>
        private void DoFreeView(float dt)
        {
            if (FlatFpsPlugin.VrMode)
                return;

            if (_viewMode != 2 || _cam == null)
                return;
            if (!FlatFpsPlugin.IsGameScene(ActiveSceneName()))
                return;
            Transform t = _cam.transform;
            if (!_freeInit)
            {
                _freeInit = true;
                Vector3 e = t.eulerAngles;
                _freePitch = (e.x > 180f) ? (e.x - 360f) : e.x;
                _freeYaw = e.y;
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] free view engaged at " + t.position.ToString("F2")
                        + " yaw=" + _freeYaw.ToString("F1") + " pitch=" + _freePitch.ToString("F1"));
            }

            float k = Mathf.Max(0.01f, FlatFpsPlugin.MouseSpeed.Value / 500f) * 10f;
            if (FlatFpsPlugin.FreeLook.Value && !_cursorFree)
            {
                _freeYaw += Input.GetAxis("Mouse X") * k;
                _freePitch += (FlatFpsPlugin.InvertY.Value ? Input.GetAxis("Mouse Y") : -Input.GetAxis("Mouse Y")) * k;
                _freePitch = Mathf.Clamp(_freePitch, -89f, 89f);
            }
            t.rotation = Quaternion.Euler(_freePitch, _freeYaw, 0f);

            float sp = FlatFpsPlugin.FreeMoveSpeed.Value;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                sp *= FlatFpsPlugin.FreeSprintMultiplier.Value;
            Vector3 mv = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) mv += t.forward;
            if (Input.GetKey(KeyCode.S)) mv -= t.forward;
            if (Input.GetKey(KeyCode.D)) mv += t.right;
            if (Input.GetKey(KeyCode.A)) mv -= t.right;
            if (Input.GetKey(KeyCode.PageUp)) mv += Vector3.up;
            if (Input.GetKey(KeyCode.PageDown)) mv -= Vector3.up;
            if (mv.sqrMagnitude > 1e-6f)
                t.position += mv.normalized * sp * Mathf.Max(0f, dt);
        }

        private static float MaxFloat(float a, float b) { return a > b ? a : b; }

        private void TickHideGazeRing(float dt)
        {
            if (FlatFpsPlugin.VrMode)
                return;             // \u51dd\u89c6\u5149\u73af\u5c31\u662f VR \u7684\u51c6\u661f, \u5207\u52ff\u85cf

            _gazeTick += dt;
            if (_gazeTick < 0.5f)
                return;
            _gazeTick = 0f;

            // 每 0.5s 都做一次"视角吸附物"体检(每场景一次), 用来回答"屏幕正中那个东西到底是什么"。
            if (!_viewScanDone)
            {
                _viewScanAt += 0.5f;
                if (_viewScanAt >= 4f && _cam != null)
                {
                    _viewScanDone = true;
                    ScanViewAttached(ActiveSceneName());
                }
            }

            if (FlatFpsPlugin.HideGazeRing == null || !FlatFpsPlugin.HideGazeRing.Value)
                return;
            try
            {
                if (_gazeType == null)
                {
                    System.Type t = null;
                    // OVRGazePointer 在 Assembly-CSharp(游戏本体) 里, 不是 UnityEngine
                    Assembly asm = typeof(InitScene2).Assembly;
                    t = asm.GetType("VRKanojo.OVRGazePointer") ?? asm.GetType("OVRGazePointer");   // v1.27.1 修 BUG: 少了命名空间, 以前恒为 null
                    if (t == null)
                    {
                        System.Type[] all = asm.GetTypes();
                        for (int i = 0; i < all.Length; i++)
                            if (all[i].Name == "OVRGazePointer") { t = all[i]; break; }
                    }
                    if (t == null)
                        return;   // 这个版本没有凝视光环, 下次也不用再找
                    _gazeType = t;
                    _gazeInstance = t.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                }
                object inst = _gazeInstance == null ? null : _gazeInstance.GetValue(null, null);
                if (inst == null)
                    return;   // 还没被创建, 下次再试
                Component c = inst as Component;
                if (c == null || c.gameObject == null)
                    return;

                int id = c.gameObject.GetInstanceID();
                bool fresh = (id != _gazeTargetId);
                if (fresh)
                {
                    _gazeTargetId = id;
                    _gazeHidden = false;
            _gazeTargetId = 0;
            _viewScanAt = 0f;
            _viewScanDone = false;
                }

                // 关键 1: 不能 SetActive(false)! OVRGazePointer 的射线判定就在这个对象上,
                // 关掉它 = 游戏自己的"凝视 + 决定"点击链断掉, 正片里平板菜单再也点不动。
                // 只关渲染器, 组件与碰撞体存活。
                // 关键 2: 不能"藏一次就完事"。游戏会随场景重建这个光环(instance 变新),
                // 也会把子渲染器重新打开, 所以这里每 0.5s 复核一次, 新实例/被重开都再压一遍。
                Renderer[] rs = c.gameObject.GetComponentsInChildren<Renderer>(true);
                int killed = 0;
                for (int i = 0; i < rs.Length; i++)
                {
                    if (rs[i] != null && rs[i].enabled) { rs[i].enabled = false; killed++; }
                }
                if (killed > 0)
                {
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] hid gaze ring visual '" + c.gameObject.name
                        + "' (id=" + id + " renderers off=" + killed
                        + (fresh ? ", new instance" : ", re-shown by game")
                        + "; component kept alive so the game's gaze-click still works)");
                }
                _gazeHidden = true;
            }
            catch { }
        }

        /// <summary>
        /// 视角吸附物审计: 谁一直贴在相机前面/屏幕正中? 把相机 1.2 米内、或挂在相机层级下的
        /// 所有 Renderer 全部列出来(路径 / 网格 / 尺寸 / 材质 / 距离), 用来判定"跟着视角动的那个东西"。
        /// </summary>
        private void ScanViewAttached(string scene)
        {
            if (FlatFpsPlugin.Log == null || _cam == null)
                return;
            Vector3 cp = _cam.transform.position;
            Object[] all = Resources.FindObjectsOfTypeAll(typeof(Renderer));
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i] as Renderer;
                if (r == null || r.gameObject.scene.name == null)
                    continue;
                Transform tr = r.transform;
                bool underCam = tr.IsChildOf(_cam.transform);
                float dist = Vector3.Distance(r.bounds.center, cp);
                if (!underCam && dist > 1.2f)
                    continue;
                n++;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
                string meshName = "-";
                if (mf != null && mf.sharedMesh != null) meshName = mf.sharedMesh.name;
                else if (smr != null && smr.sharedMesh != null) meshName = smr.sharedMesh.name;
                Vector3 sz = r.bounds.size;
                Material m = r.sharedMaterial;
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] view-attached['" + scene + "'] " + n + ". '" + PathOf(tr)
                    + "' underCam=" + underCam + " dist=" + dist.ToString("F2")
                    + " size=" + sz.x.ToString("F2") + "x" + sz.y.ToString("F2") + "x" + sz.z.ToString("F2")
                    + " mesh='" + meshName + "'" + (smr != null ? "(skinned)" : "")
                    + " shader='" + (m != null && m.shader != null ? m.shader.name : "-") + "'"
                    + " layer=" + r.gameObject.layer + " en=" + r.enabled);
            }
            FlatFpsPlugin.Log.LogInfo("[FlatFPS] view-attached['" + scene + "'] total = " + n
                + " renderer(s) closer than 1.2m or parented to the camera");
        }

        // v1.25.1: 进场景时把游戏里所有"准星/光环"对象点名一遍, 方便确认玩家看到的到底是哪个对象。
        // v1.26.0: 让游戏的"凝视触摸"这条链真正打开 —— 两个前置条件都在游戏侧:
        //   (1) Manager.Config.IntputData.controlType == 0  (键鼠模式, 1=VR 手柄)
        //   (2) BodyTouchManager.enableGaze == true         (场景里序列化的开关)
        // 任一不满足, Update_GazeCaress / CheckGazeTouch 直接 return, 按 E 不会有任何反应。
        private void ApplyTouchBridge(string scene)
        {
            // v1.27.1 修 BUG: VR 模式下把手柄模式(controlType=1)改成键鼠会直接废掉双手交互。
            if (FlatFpsPlugin.VrMode)
                return;
            try
            {
                if (FlatFpsPlugin.ForceNonController != null && FlatFpsPlugin.ForceNonController.Value)
                {
                    Type ct2 = AccessTools.TypeByName("Manager.Config");
                    object inputData = null;
                    if (ct2 != null)
                    {
                        // IntputData 其实是静态属性, AccessTools.Field 会打 HarmonyX 警告, 这里直接查
                        FieldInfo sf = ct2.GetField("IntputData",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (sf != null)
                            inputData = sf.GetValue(null);
                        if (inputData == null)
                        {
                            PropertyInfo sp = ct2.GetProperty("IntputData", BindingFlags.Public | BindingFlags.Static);
                            if (sp != null)
                                inputData = sp.GetValue(null, null);
                        }
                    }
                    if (inputData != null)
                    {
                        FieldInfo cf = AccessTools.Field(inputData.GetType(), "controlType");
                        if (cf != null)
                        {
                            object cur = cf.GetValue(inputData);
                            if (!(cur is byte) || (byte)cur != 0)
                            {
                                cf.SetValue(inputData, (byte)0);
                                if (FlatFpsPlugin.Log != null)
                                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] controlType " + cur + " -> 0 (non-controller mode: gaze touch enabled)");
                            }
                        }
                    }
                }
                if (FlatFpsPlugin.ForceEnableGaze != null && FlatFpsPlugin.ForceEnableGaze.Value)
                {
                    UnityEngine.Object[] bts = Resources.FindObjectsOfTypeAll(typeof(BodyTouchManager));
                    int fixedN = 0;
                    for (int i = 0; i < bts.Length; i++)
                    {
                        MonoBehaviour mb = bts[i] as MonoBehaviour;
                        if (mb == null || mb.gameObject.scene.name == null)
                            continue;
                        FieldInfo eg = AccessTools.Field(mb.GetType(), "enableGaze");
                        if (eg == null || eg.GetValue(mb) is bool && (bool)eg.GetValue(mb))
                            continue;
                        eg.SetValue(mb, true);
                        fixedN++;
                    }
                    if (fixedN > 0 && FlatFpsPlugin.Log != null)
                        FlatFpsPlugin.Log.LogInfo("[FlatFPS] enableGaze -> true on " + fixedN + " BodyTouchManager(s) in '" + scene + "'");
                }
            }
            catch (Exception ex)
            {
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] touch bridge failed: " + ex.Message);
            }
        }

        // v1.26.0: 按交互键时打一行状态, 用来判断"链是不是开着、当前有没有抓住东西"
        private void HandleInteractProbe()
        {
            if (FlatFpsPlugin.InteractionLog == null || !FlatFpsPlugin.InteractionLog.Value)
                return;
            if (!FlatFpsPlugin.InteractHeld())
                return;
            if (Time.realtimeSinceStartup - _interactProbeAt < 0.5f)
                return;
            _interactProbeAt = Time.realtimeSinceStartup;
            try
            {
                string ct = "?";
                Type ct2 = AccessTools.TypeByName("Manager.Config");
                if (ct2 != null)
                {
                    FieldInfo sf = AccessTools.Field(ct2, "IntputData");
                    object inputData = sf != null ? sf.GetValue(null) : null;
                    if (inputData != null)
                    {
                        FieldInfo cf = AccessTools.Field(inputData.GetType(), "controlType");
                        if (cf != null)
                            ct = cf.GetValue(inputData).ToString();
                    }
                }
                UnityEngine.Object[] bts = Resources.FindObjectsOfTypeAll(typeof(BodyTouchManager));
                int gazeOn = 0;
                foreach (UnityEngine.Object o in bts)
                {
                    MonoBehaviour mb = o as MonoBehaviour;
                    if (mb == null || mb.gameObject.scene.name == null)
                        continue;
                    FieldInfo eg = AccessTools.Field(mb.GetType(), "enableGaze");
                    if (eg != null && eg.GetValue(mb) is bool && (bool)eg.GetValue(mb))
                        gazeOn++;
                }
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] interact key E -> scene='" + ActiveSceneName()
                        + "' controlType=" + ct + " enableGazeManagers=" + gazeOn + "/" + bts.Length
                        + " gazeHit=" + (HoverInteractable ? (HoverName ?? "?") : "none"));
            }
            catch (Exception)
            {
            }
        }

        private void DumpReticles(string tag)
        {
            if (FlatFpsPlugin.InteractionLog == null || !FlatFpsPlugin.InteractionLog.Value)
                return;
            try
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour));
                int n = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour mb = all[i] as MonoBehaviour;
                    if (mb == null)
                        continue;
                    string tn = mb.GetType().Name;
                    if (tn != "OVRGazePointer" && tn != "Reticle" && tn != "OVRProgressIndicator")
                        continue;
                    Transform tt = mb.transform;
                    if (tt == null || tt.gameObject.scene.name == null)
                        continue;
                    if (n == 0 && FlatFpsPlugin.Log != null)
                        FlatFpsPlugin.Log.LogInfo("[FlatFPS] reticles in '" + tag + "':");
                    n++;
                    if (FlatFpsPlugin.Log != null)
                        FlatFpsPlugin.Log.LogInfo("[FlatFPS]   reticle[" + n + "] " + tn
                            + " '" + PathOf(tt) + "' active=" + tt.gameObject.activeSelf
                            + " enabled=" + mb.enabled
                            + " pos=" + tt.position.ToString("F3")
                            + " scale=" + tt.localScale.x.ToString("F3"));
                }
                if (n == 0 && FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] reticles in '" + tag + "': none");
            }
            catch (Exception)
            {
            }
        }

        private void DumpIllusionCameras()
        {
            if (FlatFpsPlugin.Log == null)
                return;
            Object[] all = Resources.FindObjectsOfTypeAll(typeof(IllusionCamera));
            int n = 0;
            string s = "";
            for (int i = 0; i < all.Length; i++)
            {
                IllusionCamera ic = all[i] as IllusionCamera;
                if (ic == null || ic.gameObject.scene.name == null)
                    continue;
                n++;
                s += " [" + n + "] '" + PathOf(ic.transform) + "' enabled=" + ic.isActiveAndEnabled;
            }
            FlatFpsPlugin.Log.LogInfo("[FlatFPS] IllusionCamera instances = " + n + ":" + s
                + "  (>1 说明游戏自己挂了两份, 正片场景会双重位移)");
        }

        /// <summary>
        /// 免VR 直连点击。uGUI 的点击要靠 EventSystem → BaseInputModule → BaseRaycaster 这条链，
        /// 本作整条链都是为 VR 射线搭的(OVRRaycaster + OVRInputModule)，没有头显就是死的。
        /// 上一版补了 GraphicRaycaster 仍然点不动, 说明还有东西在吞事件(例如 order=32767 的全屏 Fader 面板)。
        /// 所以这里绕过整条链: 左键按下时把所有 Button 的矩形投影到屏幕, 看光标落在哪个里,
        /// 取层级最靠上的那个, 直接派发 pointerDown/Up/Click(Button 自己实现 IPointerClickHandler → onClick)。
        /// 正片场景完全不参与, 游戏自己的鼠标逻辑不受影响。
        /// </summary>
        // RenderScale mode renders the scene into a smaller offscreen target, so Unity
        // hands out raycast coordinates in that smaller pixel space. Without this the
        // cursor and every click land in the wrong place.
        internal static Vector2 MousePixel()
        {
            Vector2 p = Input.mousePosition;
            Camera c = Camera.main;
            if (c != null && c.targetTexture != null)
            {
                p.x *= (float)c.targetTexture.width / Mathf.Max(1, Screen.width);
                p.y *= (float)c.targetTexture.height / Mathf.Max(1, Screen.height);
            }
            return p;
        }

        // v1.34.0: uGUI 的命中测试走 Camera.ScreenPointToRay / GraphicRaycaster,
        // 相机一旦渲染到离屏目标, 这些 API 期望的就是"目标像素"而不是屏幕像素;
        // 只有 ScreenSpaceOverlay 画布始终用屏幕像素。分错了点就会整体偏移。
        internal static Vector2 MouseScreen()
        {
            return Input.mousePosition;
        }

        internal static bool RenderScaleActive()
        {
            Camera c = Camera.main;
            return c != null && c.targetTexture != null;
        }

        internal static Vector2 UiPoint(Canvas canvas)
        {
            string mode = FlatFpsPlugin.UiPointerSpace != null ? FlatFpsPlugin.UiPointerSpace.Value : "Auto";
            bool useRender;
            if (mode == "Screen") useRender = false;
            else if (mode == "Render") useRender = true;
            else useRender = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay;
            if (useRender && RenderScaleActive()) return MousePixel();
            return MouseScreen();
        }

        private void DoDirectMenuClick()
        {
            if (FlatFpsPlugin.VrMode)
                return;             // VR \u7528 OVR \u5c04\u7ebf, \u4e0d\u9700\u8981\u9f20\u6807\u76f4\u63a5\u70b9

            if (FlatFpsPlugin.DirectMenuClick == null || !FlatFpsPlugin.DirectMenuClick.Value)
                return;
            bool inGame = FlatFpsPlugin.IsGameScene(ActiveSceneName());
            if (inGame && (FlatFpsPlugin.ClickInGame == null || !FlatFpsPlugin.ClickInGame.Value))
                return;
            if (_cursorFree || !Input.GetMouseButtonDown(0))
                return;
            if (ClickTakenThisFrame())
                return;                       // v1.41.0: 本帧已处理过点击

            Vector3 m = MousePixel();
            Button best = null;
            int bestOrder = int.MinValue, bestDepth = -1;
            string bestPath = "";
            List<Button> cand = new List<Button>();
            CollectButtons(cand);
            int seen = cand.Count, under = 0;

            for (int i = 0; i < cand.Count; i++)
            {
                Button b = cand[i];
                if (b == null)
                    continue;
                if (!b.isActiveAndEnabled || !b.interactable)
                    continue;
                RectTransform rt = b.transform as RectTransform;
                if (rt == null)
                    continue;
                Canvas c = b.GetComponentInParent<Canvas>();
                Vector2 pt = UiPoint(c);
                if (!RectTransformUtility.RectangleContainsScreenPoint(rt, pt, _cam))
                    continue;
                under++;
                int order = (c != null) ? c.sortingOrder : 0;
                int depth = DepthOf(b.transform);
                if (best == null || order > bestOrder || (order == bestOrder && depth > bestDepth))
                {
                    best = b;
                    bestOrder = order;
                    bestDepth = depth;
                    bestPath = PathOf(b.transform);
                }
            }

            if (FlatFpsPlugin.ClickDebug != null && FlatFpsPlugin.ClickDebug.Value && FlatFpsPlugin.Log != null)
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] click at " + (int)m.x + "," + (int)m.y + " (render space)"
                    + " scene='" + ActiveSceneName() + "' buttons=" + seen + " under-cursor=" + under
                    + ((best != null) ? (" -> '" + bestPath + "' order=" + bestOrder) : " -> nothing"));

            if (best != null)
                InvokeButton(best);
        }

        /// <summary>
        /// 收集场景里所有可点的 Button。FindObjectsOfTypeAll 在 5.4 里对
        /// DontDestroyOnLoad / 运行时挂载的对象时常返回不全(本作相机与部分 Canvas 就是这种),
        /// 所以再用 Canvas.GetComponentsInChildren(true) 补一遍, 两边按 InstanceID 去重。
        /// </summary>
        private static void CollectButtons(List<Button> outList)
        {
            HashSet<int> seen = new HashSet<int>();
            Object[] all = Resources.FindObjectsOfTypeAll(typeof(Button));
            for (int i = 0; i < all.Length; i++)
            {
                Button b = all[i] as Button;
                if (b == null || b.gameObject.scene.name == null)
                    continue;
                if (seen.Add(b.GetInstanceID()))
                    outList.Add(b);
            }
            Object[] cans = Resources.FindObjectsOfTypeAll(typeof(Canvas));
            for (int i = 0; i < cans.Length; i++)
            {
                Canvas c = cans[i] as Canvas;
                if (c == null || c.gameObject.scene.name == null)
                    continue;
                Button[] kids = c.GetComponentsInChildren<Button>(true);
                for (int k = 0; k < kids.Length; k++)
                {
                    Button b = kids[k];
                    if (b == null || seen.Contains(b.GetInstanceID()))
                        continue;
                    seen.Add(b.GetInstanceID());
                    outList.Add(b);
                }
            }
        }

        private static int DepthOf(Transform t)
        {
            int d = 0;
            while (t != null)
            {
                d++;
                t = t.parent;
            }
            return d;
        }

        private static void InvokeButton(Button b)
        {
            GameObject go = b.gameObject;
            if (EventSystem.current == null)
            {
                // 正片场景里往往没有 EventSystem, PointerEventData 会在构造时断言失败,
                // 直接触发 onClick 最稳(Button 的 onClick 就是它 IPointerClickHandler 的实现体)。
                try { b.onClick.Invoke(); } catch { }
                return;
            }
            PointerEventData ped = new PointerEventData(EventSystem.current);
            Canvas cv = go.GetComponentInParent<Canvas>();
            ped.position = FlatFpsRuntime.UiPoint(cv);
            ped.button = PointerEventData.InputButton.Left;
            ped.clickCount = 1;
            ped.pointerCurrentRaycast = new RaycastResult { gameObject = go };
            ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
        }

        /// <summary>
        /// 全屏透明挡板清理: 透明(alpha≈0)却 raycastTarget=true 的 Graphic 会把点击整片吞掉。
        /// 本作最典型的就是挂在相机的 Fader 面板(order=32767, scale=15, 距离 0.26 米)。
        /// 只处理"非可交互控件 + 覆盖屏幕 60% 以上 + 几乎全透明"的 Graphic, 有 Selectable 的一律放过。
        /// </summary>
        private void ClearRaycastBlockers(string scene)
        {
            if (_cam == null)
                return;
            float screenArea = (float)Screen.width * (float)Screen.height;
            Object[] all = Resources.FindObjectsOfTypeAll(typeof(Graphic));
            int cleared = 0;
            string names = "";
            for (int i = 0; i < all.Length; i++)
            {
                Graphic g = all[i] as Graphic;
                if (g == null || !g.raycastTarget || g.gameObject.scene.name == null)
                    continue;
                if (g.color.a > 0.02f)
                    continue;
                if (g.GetComponent<Selectable>() != null || g.GetComponentInParent<Button>() != null)
                    continue;
                RectTransform rt = g.transform as RectTransform;
                if (rt == null)
                    continue;
                Vector3[] c = new Vector3[4];
                rt.GetWorldCorners(c);
                float minx = float.MaxValue, maxx = float.MinValue;
                float miny = float.MaxValue, maxy = float.MinValue;
                bool ok = true;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 s = _cam.WorldToScreenPoint(c[k]);
                    if (s.z < 0f)
                    {
                        ok = false;
                        break;
                    }
                    if (s.x < minx) minx = s.x;
                    if (s.x > maxx) maxx = s.x;
                    if (s.y < miny) miny = s.y;
                    if (s.y > maxy) maxy = s.y;
                }
                if (!ok)
                    continue;
                float area = Mathf.Max(0f, maxx - minx) * Mathf.Max(0f, maxy - miny);
                if (area < screenArea * 0.6f)
                    continue;
                g.raycastTarget = false;
                cleared++;
                if (cleared <= 4)
                    names += " '" + g.name + "'";
            }
            if (cleared > 0 && FlatFpsPlugin.Log != null)
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] disabled " + cleared
                    + " full-screen transparent raster blockers in '" + scene + "':" + names);
        }

        private void DoCursor()
        {
            if (FlatFpsPlugin.VrMode)
                return;             // VR \u91cc\u4e0d\u9501\u6307\u9488

            // 按住 = 释放(可见+可移动), 松手 = 重新锁定(中心准星)
            // 菜单/加载场景不锁(否则鼠标动不了, 点不到按钮), 除非显式打开 MenuCursorLock
            bool menu = !FlatFpsPlugin.IsGameScene(ActiveSceneName());
            bool want = FlatFpsPlugin.LockCursor.Value && !_cursorFree
                        && (!menu || FlatFpsPlugin.MenuCursorLock.Value);
            if (want)
            {
                if (Cursor.lockState != CursorLockMode.Locked)
                    Cursor.lockState = CursorLockMode.Locked;
                if (Cursor.visible)
                    Cursor.visible = false;
            }
            else
            {
                if (Cursor.lockState != CursorLockMode.None)
                    Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible)
                    Cursor.visible = true;
            }
        }

        // ---------------- diagnostics ----------------

        /// <summary>
        /// 探测屏幕正中央那个物体到底是什么：先用物理射线，再用投影兜底，各打一行日志。
        /// 目的：无需人工描述，日志里直接给出完整层级路径 + 组件 + 网格 + 材质 + 距离。
        /// </summary>
        // v1.34.0: 一次打印"屏幕 / 相机渲染目标 / UI 指针空间"三者关系,
        // 有任何一项不一致(比如 0.85 缩放没被正确换算)都能在这里看出来。
        private void ProbeRenderScale(string scene)
        {
            try
            {
                Camera c = Camera.main;
                string tgt = (c != null && c.targetTexture != null)
                    ? (c.targetTexture.width + "x" + c.targetTexture.height)
                    : "backbuffer";
                string mode = FlatFpsPlugin.UiPointerSpace != null ? FlatFpsPlugin.UiPointerSpace.Value : "Auto";
                int overlay = 0, world = 0, cam2 = 0;
                Canvas[] cvs = Resources.FindObjectsOfTypeAll(typeof(Canvas)) as Canvas[];
                if (cvs != null)
                {
                    for (int i = 0; i < cvs.Length; i++)
                    {
                        Canvas cv = cvs[i];
                        if (cv == null || cv.gameObject.scene.name == null) continue;
                        if (cv.renderMode == RenderMode.ScreenSpaceOverlay) overlay++;
                        else if (cv.renderMode == RenderMode.WorldSpace) world++;
                        else cam2++;
                    }
                }
                string uiSpace = (RenderScaleActive() && (world + cam2) > 0) ? "render" : "screen";
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] render probe['" + scene + "']: screen=" +
                        Screen.width + "x" + Screen.height + " camTarget=" + tgt +
                        " mouse=" + MousePixel().ToString("F0") + " uiPointer=" + mode + "(=>" + uiSpace + ")" + " canvases=overlay" + overlay + "/world" + world + "/cam" + cam2);
            }
            catch { }
        }

        private void ProbeCenter(string scene)
        {
            if (_cam == null || FlatFpsPlugin.Log == null)
                return;

            Transform camT = _cam.transform;
            Vector3 origin = camT.position;
            Vector3 dir = camT.forward;

            try
            {
                RaycastHit hit;
                if (Physics.Raycast(origin, dir, out hit, 8f))
                {
                    Transform t = hit.collider != null ? hit.collider.transform : null;
                    Renderer r = null;
                    if (t != null)
                    {
                        r = t.GetComponent<Renderer>();
                        if (r == null)
                            r = t.GetComponentInParent<Renderer>();
                    }
                    string extra = "";
                    if (r != null)
                    {
                        extra = " renderer=" + r.GetType().Name
                              + " mat=" + (r.sharedMaterial != null ? r.sharedMaterial.name : "none")
                              + " enabled=" + r.enabled;
                        MeshFilter mf = r.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                            extra += " mesh=" + mf.sharedMesh.name
                                   + " verts=" + mf.sharedMesh.vertexCount;
                        SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
                        if (smr != null)
                            extra += " bones=" + (smr.bones != null ? smr.bones.Length : 0)
                                   + " rootBone=" + (smr.rootBone != null ? smr.rootBone.name : "none");
                    }
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] center object '" + scene + "' (raycast): "
                        + (t != null ? PathOf(t) : "<none>")
                        + " dist=" + hit.distance.ToString("F2") + "m"
                        + " layer=" + (t != null ? t.gameObject.layer : -1)
                        + " collider=" + (hit.collider != null ? hit.collider.GetType().Name : "none")
                        + extra);
                }
                else
                {
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] center object '" + scene
                        + "' (raycast): nothing within 8m - falling back to projection");
                }
            }
            catch (Exception e)
            {
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] center probe raycast failed: " + e.Message);
            }

            // 投影兜底：找“渲染包围盒中心投影到屏幕上最接近屏幕中心”的三个对象
            try
            {
                float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
                Renderer[] all = Resources.FindObjectsOfTypeAll(typeof(Renderer)) as Renderer[];
                if (all == null)
                    return;
                int best = -1, second = -1, third = -1;
                float bd = float.MaxValue, sd = float.MaxValue, td = float.MaxValue;
                int limit = Mathf.Min(all.Length, 6000);
                for (int i = 0; i < limit; i++)
                {
                    Renderer r = all[i];
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                        continue;
                    if (r.gameObject.scene.name == null)
                        continue;
                    Vector3 sp = _cam.WorldToScreenPoint(r.bounds.center);
                    if (sp.z <= 0.05f)
                        continue;
                    float d = Vector2.Distance(new Vector2(sp.x, sp.y), new Vector2(cx, cy));
                    if (d < bd) { third = second; td = sd; second = best; sd = bd; best = i; bd = d; }
                    else if (d < sd) { third = second; td = sd; second = i; sd = d; }
                    else if (d < td) { third = i; td = d; }
                }
                string[] tag = { "1st", "2nd", "3rd" };
                int[] idx = { best, second, third };
                float[] dd = { bd, sd, td };
                for (int k = 0; k < 3; k++)
                {
                    if (idx[k] < 0)
                        continue;
                    Renderer r = all[idx[k]];
                    if (r == null)
                        continue;
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    string mesh = (mf != null && mf.sharedMesh != null) ? mf.sharedMesh.name : "-";
                    SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
                    if (smr != null && smr.sharedMesh != null)
                        mesh = smr.sharedMesh.name + " (skinned)";
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] center " + tag[k] + " '" + scene + "': "
                        + PathOf(r.transform) + " screenDist=" + dd[k].ToString("F0") + "px"
                        + " world=" + r.bounds.center.ToString("F2")
                        + " size=" + r.bounds.size.ToString("F2")
                        + " camDist=" + Vector3.Distance(origin, r.bounds.center).ToString("F2") + "m"
                        + " layer=" + r.gameObject.layer + " mesh=" + mesh
                        + " mat=" + (r.sharedMaterial != null ? r.sharedMaterial.name : "none"));
                }
            }
            catch (Exception e)
            {
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] center probe projection failed: " + e.Message);
            }
        }


        /// <summary>
        /// 物理射线点击：不依赖 EventSystem 的指针位置，直接用相机射向鼠标点（锁指针时即屏幕中心）。
        /// 命中 3D 碰撞体后再沿层级找 Button / IPointerClickHandler 并派发，
        /// 这样“平板菜单”这种挂在 3D 模型上的世界空间 UI 也能点。
        /// </summary>
        private readonly System.Collections.Generic.HashSet<string> _hitLogged = new System.Collections.Generic.HashSet<string>();
        // v1.41.0: "点一次出两次" 的防护 —— 一帧只处理一次点击, 同一个 Button 250ms 内只触发一次
        private int _clickFrame = -1;
        private float _lastBtnAt = -10f;
        private Button _lastBtn;

        private bool ClickTakenThisFrame()
        {
            if (_clickFrame == Time.frameCount) return true;
            _clickFrame = Time.frameCount;
            return false;
        }

        private bool ButtonThrottled(Button b)
        {
            if (b == null) return false;
            if (_lastBtn == b && Time.unscaledTime - _lastBtnAt < 0.25f) return true;
            _lastBtn = b;
            _lastBtnAt = Time.unscaledTime;
            return false;
        }


        // 每个物体只完整记录一次(含组件清单), 这样日志里能看到"那两个 R / O 图标到底是什么"
        private void LogFirstHit(GameObject go, Collider col, string how)
        {
            try
            {
                string path = PathOf(go.transform);
                if (!_hitLogged.Add(path)) return;
                if (_hitLogged.Count > 60) return;
                string comps = "";
                Component[] cs = go.GetComponents<Component>();
                for (int i = 0; i < cs.Length && i < 12; i++)
                    comps += (cs[i] == null ? "null" : cs[i].GetType().Name) + " ";
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] first hit '" + path + "' (" + how + ")"
                    + " collider='" + (col == null ? "-" : col.name) + "'"
                    + " layer=" + go.layer + " comps=[" + comps.Trim() + "]");
            }
            catch (Exception) { }
        }

        // 直接驱动游戏的触摸反应链: 与 Hand.OnTriggerStay 里的调用完全一致
        internal static bool TryInteractiveProc(string colName)
        {
            if (string.IsNullOrEmpty(colName)) return false;
            try
            {
                if (!Singleton<InteractiveCommandController>.IsInstance()) return false;
                InteractiveCommandController icc = Singleton<InteractiveCommandController>.Instance;
                if (icc == null) return false;
                icc.InteractiveProc(colName, 0, 0, 0);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private Collider _gazeCol;
        private VRInteractiveItem _vrHovered;
        private string _vrHoverName;

        // v1.37.0: 免VR 的"凝视悬停"。
        // 游戏里大量交互图标(VRLookIcon/TimeIcon/ExitTimeIcon/DressBoxIcon…)不是 uGUI,
        // 而是 VRInteractiveItem 加上 OnOver/OnOut/OnClick 事件:
        //   TimeIconControl.OnEnable  ->  m_InteractiveItem.OnOver += HandleOver;   // 播 "Count" 动画
        //   TimeIconControlChild.EndCount(动画事件) -> TImeIconCountEnd() -> LookSelectJumpToTag()
        // 也就是说"盯着看"本身就会触发, 根本不需要按键。这里把瞄准物映射成 Over/Out。
        private void UpdateGazeHover()
        {
            VRInteractiveItem vii = null;
            if (_gazeCol != null)
            {
                try { vii = _gazeCol.GetComponentInParent<VRInteractiveItem>(); }
                catch (Exception) { vii = null; }
            }
            if (vii == _vrHovered) return;
            if (_vrHovered != null)
            {
                try { _vrHovered.Out(); } catch (Exception) { }
                if (FlatFpsPlugin.MouseHandLog != null && FlatFpsPlugin.MouseHandLog.Value && FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] gaze hover out '" + (_vrHoverName ?? "-") + "'");
            }
            _vrHovered = vii;
            _vrHoverName = vii != null ? FlatFpsRuntime.PathOf(vii.transform) : null;
            if (vii != null)
            {
                try { vii.Over(); } catch (Exception) { }
                if (FlatFpsPlugin.GazeRingLog != null && FlatFpsPlugin.GazeRingLog.Value && FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] gaze hover -> '" + _vrHoverName + "' (VRInteractiveItem.Over)");
            }
        }

        // v1.38.0: "有些注视类互动必须站在特定位置才生效" 的根因 ——
        // 游戏里三条凝视射线各自的相机来源都不是玩家真正在看的相机:
        //   BodyTouchManager.Start()      : camTrans = MainCamera_.GetComponentInChildren<VREyeRaycaster>().M_Camera;   // 只取一次
        //   InteractiveLookingStatus.Start(): camTrans = MainCamera_.transform;  + raycastDis = 2f
        //   VREyeRaycaster.EyeRaycast()    : Physics.Raycast(new Ray(m_Camera.position, m_Camera.forward), ...)
        // 免VR 场景里那个 m_Camera 可能是 VR rig 上的旧节点, 于是射线从别的地方发出,
        // 你必须走到"恰好能被那条射线扫到"的站位, 互动才会触发。这里把它们统一到真实眼位。
        private static int _gazeSyncSceneCount = -1;
        private float _lastSyncedEye = -1f;      // v1.39.0: 上次同步凝视射线时的眼高
        private float _gazeSyncDueAt;            // v1.39.0: 下一次周期同步的时间点

        private void SyncGameGazeCameras(string scene)
        {
            if (FlatFpsPlugin.SyncGameGazeCamera == null || !FlatFpsPlugin.SyncGameGazeCamera.Value) return;
            if (FlatFpsPlugin.VrMode) return;
            if (_cam == null) return;
            Transform eye = _cam.transform;
            int eyeRc = 0, body = 0, look = 0;
            try
            {
                foreach (MonoBehaviour mb in Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)))
                {
                    if (mb == null) continue;
                    if (mb.gameObject.scene.name == null) continue;
                    string tn = mb.GetType().Name;
                    if (tn == "VREyeRaycaster")
                    {
                        FieldInfo f = AccessTools.Field(mb.GetType(), "m_Camera");
                        if (f != null && (Transform)f.GetValue(mb) != eye) { f.SetValue(mb, eye); eyeRc++; }
                    }
                    else if (tn == "BodyTouchManager")
                    {
                        FieldInfo f = AccessTools.Field(mb.GetType(), "camTrans");
                        if (f != null && (Transform)f.GetValue(mb) != eye) { f.SetValue(mb, eye); body++; }
                    }
                    else if (tn == "InteractiveLookingStatus")
                    {
                        FieldInfo f = AccessTools.Field(mb.GetType(), "camTrans");
                        if (f != null && (Transform)f.GetValue(mb) != eye) { f.SetValue(mb, eye); }
                        FieldInfo d = AccessTools.Field(mb.GetType(), "raycastDis");
                        float want = FlatFpsPlugin.GazeLookDistance != null ? FlatFpsPlugin.GazeLookDistance.Value : 3.5f;
                        if (d != null)
                        {
                            object cur = d.GetValue(mb);
                            if (cur is float && Mathf.Abs((float)cur - want) > 0.01f) d.SetValue(mb, want);
                        }
                        look++;
                    }
                }
            }
            catch (Exception e)
            {
                if (FlatFpsPlugin.Log != null) FlatFpsPlugin.Log.LogWarning("[FlatFPS] gaze camera sync failed: " + e.Message);
            }
            if (FlatFpsPlugin.Log != null && (eyeRc + body + look) > 0)
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] game gaze cameras synced['" + scene + "'] eyeRaycaster=" + eyeRc
                    + " bodyTouch=" + body + " looking=" + look + " -> '" + PathOf(eye) + "'");
        }

        // v1.39.0: 眼高变化立刻同步 + 每 2 秒兜底重同步(场景内对象可能晚于我们 Start)
        private void TickGazeCameraSync()
        {
            if (FlatFpsPlugin.VrMode) return;
            if (Time.unscaledTime < _gazeSyncDueAt) return;
            _gazeSyncDueAt = Time.unscaledTime + 2f;
            SyncGameGazeCameras(ActiveSceneName());
        }

        private void DoPhysicsClick()
        {
            if (FlatFpsPlugin.VrMode)
                return;

            if (!FlatFpsPlugin.PhysicsClick.Value)
                return;
            if (!Input.GetMouseButtonDown(0))
                return;
            if (ClickTakenThisFrame())
                return;                       // v1.41.0: 本帧已由另一条点击通路处理
            if (_cam == null)
                return;

            Vector3 sp = MousePixel();
            bool debug = FlatFpsPlugin.ClickDebug.Value
                || (FlatFpsPlugin.InteractionLog != null && FlatFpsPlugin.InteractionLog.Value
                    && FlatFpsPlugin.IsGameScene(ActiveSceneName()));
            Ray ray = _cam.ScreenPointToRay(sp);
            RaycastHit hit;
            bool any = Physics.Raycast(ray, out hit, 30f);
            if (FlatFpsPlugin.ClickDebug.Value)
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] click ray from " + ray.origin.ToString("F2")
                    + " dir " + ray.direction.ToString("F2") + " -> "
                    + (any ? ("HIT " + PathOf(hit.collider.transform) + " d=" + hit.distance.ToString("F2")
                        + " layer=" + hit.collider.gameObject.layer) : "nothing within 30m"));
            if (!any)
                return;

            GameObject go = hit.collider.gameObject;
            Transform t = go.transform;
            string comps = "";
            for (int guard = 0; t != null && guard < 8; guard++, t = t.parent)
            {
                Component[] cs = t.GetComponents<Component>();
                for (int i = 0; i < cs.Length; i++)
                    if (cs[i] != null)
                        comps += cs[i].GetType().Name + " ";
                if (t.GetComponentInParent<Canvas>() == null && guard > 3)
                    break;
            }
            if (debug)
            {
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] click components: " + comps);
                string esInfo = "null";
                if (EventSystem.current != null)
                {
                    esInfo = "'" + EventSystem.current.name + "' mods=[";
                    BaseInputModule[] mods = EventSystem.current.GetComponents<BaseInputModule>();
                    for (int i = 0; i < mods.Length; i++)
                        esInfo += (i > 0 ? "," : "") + mods[i].GetType().Name + (mods[i].enabled ? "+" : "-");
                    esInfo += "] overUI=" + EventSystem.current.IsPointerOverGameObject();
                }
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] interaction state: es=" + esInfo
                    + " cursor=" + Cursor.lockState + " mouse=" + MousePixel().ToString("F0")
                    + " scene=" + ActiveSceneName());
            }

            PointerEventData ped = null;
            if (EventSystem.current != null)
            {
                ped = new PointerEventData(EventSystem.current);
                ped.position = sp;
                ped.button = PointerEventData.InputButton.Left;
                ped.clickCount = 1;
                RaycastResult rr = new RaycastResult();
                rr.gameObject = go;
                rr.distance = hit.distance;
                rr.worldPosition = hit.point;
                rr.screenPosition = sp;
                rr.index = 0;
                ped.pointerCurrentRaycast = rr;
            }

            Button btn = go.GetComponentInParent<Button>();
            if (btn != null && btn.IsInteractable())
            {
                if (ButtonThrottled(btn)) return;
                InvokeButton(btn);
                if (debug)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] physics click -> Button '" + PathOf(btn.transform) + "'");
                return;
            }

            GameObject handler = null;
            try { handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go); }
            catch (Exception) { }
            if (handler != null && ped != null)
            {
                ExecuteEvents.Execute(handler, ped, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(handler, ped, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.ExecuteHierarchy(handler, ped, ExecuteEvents.pointerClickHandler);
                if (debug)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] physics click -> IPointerClickHandler '" + PathOf(handler.transform) + "'");
                return;
            }

            // v1.36.0: uGUI 走不通时, 交给游戏自己的触摸反应系统。
            // Hand.OnTriggerStay 对 TouchCollider / Map 两种 layer 都是调
            //   Singleton<InteractiveCommandController>.Instance.InteractiveProc(collider.name, shake, tickle, grip)
            // 这里直接用它按"瞄到的碰撞体名字"触发一次, 与手真正贴上去等价。
            // v1.37.0: VRInteractiveItem 是游戏自己的"可交互对象"抽象,
            // 图标/身体配件都靠它的 OnClick 事件驱动, 比 InteractiveProc 更准确。
            if (FlatFpsPlugin.ClickVRItem.Value)
            {
                VRInteractiveItem vii2 = null;
                try { vii2 = go.GetComponentInParent<VRInteractiveItem>(); }
                catch (Exception) { vii2 = null; }
                if (vii2 != null)
                {
                    try { vii2.Down(); vii2.Click(); vii2.Up(); }
                    catch (Exception) { }
                    LogFirstHit(go, hit.collider, "VRInteractiveItem.Click");
                    return;
                }
            }

            string colName = hit.collider != null ? hit.collider.name : go.name;
            if (FlatFpsPlugin.ClickProc.Value && TryInteractiveProc(colName))
            {
                LogFirstHit(go, hit.collider, "InteractiveProc");
                return;
            }

            // 剩下的就是真的什么都不响应的物体(OVRInspector / 纯装饰), 只在调试开关下记录
            LogFirstHit(go, hit.collider, "no handler");
            if (FlatFpsPlugin.ClickDebug.Value && PathOf(go.transform).IndexOf("OVRInspector") < 0)
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] physics click hit '" + PathOf(go.transform)
                    + "' but no Button / IPointerClickHandler on it");
        }

        private static Type _grType;
        private static bool _grTypeTried;
        private static OVRGazePointer _grRing;
        private static bool _grPinned;

        private static OVRGazePointer GazeRing()
        {
            if (_grRing != null)
                return _grRing;
            if (!_grTypeTried)
            {
                _grTypeTried = true;
                _grType = typeof(InitScene2).Assembly.GetType("VRKanojo.OVRGazePointer")
                            ?? typeof(InitScene2).Assembly.GetType("OVRGazePointer");   // v1.27.1: 真名是 VRKanojo.OVRGazePointer
            }
            if (_grType == null)
                return null;
            try
            {
                System.Reflection.PropertyInfo p = _grType.GetProperty("instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (p != null)
                    _grRing = p.GetValue(null, null) as OVRGazePointer;
            }
            catch (Exception)
            {
            }
            return _grRing;
        }

        // v1.20.0: \u51dd\u89c6\u5149\u73af\u5e38\u663e
        private void KeepGazeRingVisible()
        {
            // v1.28.0: VR 入口下光环是游戏自己的准星, 我们不碰。
            if (FlatFpsPlugin.VrMode)
                return;
            if (FlatFpsPlugin.VrMode || _cam == null)
                return;
            if (!FlatFpsPlugin.AlwaysShowGazeRing.Value || FlatFpsPlugin.HideGazeRing.Value)
                return;
            OVRGazePointer ring = GazeRing();
            if (ring == null)
                return;
            if (!_grPinned || ring.hidden)
            {
                try
                {
                    ring.Show();
                    _grPinned = true;
                    if (FlatFpsPlugin.Log != null)
                        FlatFpsPlugin.Log.LogInfo("[FlatFPS] gaze ring pinned on screen centre (always-show)");
                }
                catch (Exception)
                {
                }
            }
            Transform t = ring.transform;
            Transform ct = _cam.transform;
            float fallback = Mathf.Clamp(FlatFpsPlugin.GazeRingDistance.Value, 0.3f, 8f);

            // v1.22.0: 默认 Aim 模式 —— 每帧从相机打一条物理射线, 光环贴在命中点上,
            // 这样转动视角时光环会沿着物体表面滑动(和 VR 里的凝视光环一致);
            // 没打到东西才退回"屏幕正中 fallback 米处"。
            string mode = (FlatFpsPlugin.GazeRingMode != null) ? FlatFpsPlugin.GazeRingMode.Value : "Aim";
            Vector3 pos = ct.position + ct.forward * fallback;
            float dist = fallback;
            string hitName = null;
            Collider bestCol = null;
            if (!string.Equals(mode, "Center", StringComparison.OrdinalIgnoreCase))
            {
                float maxD = Mathf.Clamp(
                    (FlatFpsPlugin.GazeRingMaxDistance != null) ? FlatFpsPlugin.GazeRingMaxDistance.Value : 8f,
                    0.5f, 20f);
                int n = Physics.RaycastNonAlloc(ct.position, ct.forward, _gazeHits, maxD, ~0, QueryTriggerInteraction.Collide);   // v1.36.1: 命中 trigger(交互图标/VKHit)
                float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    RaycastHit h = _gazeHits[i];
                    if (h.collider == null || h.distance <= 0.05f)
                        continue;
                    // 排除我们自己: 相机 rig 层级内的东西(IllusionCamera 会在相机上加 CapsuleCollider, 层 30)
                    if (h.collider.gameObject.layer == 30)
                        continue;
                    if (h.collider.transform.IsChildOf(ct.root))
                        continue;
                    if (h.distance < best)
                    {
                        best = h.distance;
                        pos = h.point + h.normal * 0.01f;
                        dist = h.distance;
                        hitName = h.collider.name;
                        bestCol = h.collider;
                    }
                }
            }

            // v1.25.0: 没打到任何东西时, 原来的做法是把光环摆到"相机前方 fallback 米"处,
            //          结果它在屏幕上永远居中、转视角也不动, 看着就像个假准星。默认改成直接隐藏。
            bool hideWhenNoHit = FlatFpsPlugin.GazeRingHideWhenNoHit == null || FlatFpsPlugin.GazeRingHideWhenNoHit.Value;
            if (hitName == null && hideWhenNoHit)
            {
                Renderer rh = ring.GetComponent<Renderer>();
                if (rh != null && rh.enabled)
                    rh.enabled = false;
                if (HoverInteractable || HoverName != null)
                {
                    HoverInteractable = false;
                    HoverName = null;
                }
                _gazeLastHit = null;
                _gazeCol = null;
                return;
            }
            t.position = pos;
            // v1.23.0: 命中物的父链上带 HitCollision(游戏自己用来装"可触摸碰撞体"的容器) => 可交互
            HoverInteractable = false;
            HoverName = null;
            if (bestCol != null)
            {
                HitCollision hc = bestCol.GetComponentInParent<HitCollision>();
                if (hc != null)
                {
                    HoverInteractable = true;
                    HoverName = hc.gameObject.name;
                }
            }
            if (hitName != _gazeLastHit)
            {
                _gazeLastHit = hitName;
                bool ringLog = FlatFpsPlugin.GazeRingLog != null && FlatFpsPlugin.GazeRingLog.Value;
                if (ringLog && FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] gaze ring target -> "
                        + (hitName ?? "(none, hidden)") + " dist=" + dist.ToString("F2")
                        + " pos=" + pos.ToString("F2"));
            }
            _gazeCol = bestCol;   // v1.37.0: 供凝视悬停(VRInteractiveItem.Over/Out)使用
            float mul = ring.depthScaleMultiplier > 0.001f ? ring.depthScaleMultiplier : 0.03f;
            float boost = HoverInteractable ? 1.2f : 1f;
            t.localScale = Vector3.one * (dist * mul * boost);
            Renderer r = ring.GetComponent<Renderer>();
            if (r != null && !r.enabled)
                r.enabled = true;

            if (FlatFpsPlugin.GazeRingLog != null && FlatFpsPlugin.GazeRingLog.Value
                && Time.realtimeSinceStartup >= _gazeLogAt)
            {
                _gazeLogAt = Time.realtimeSinceStartup + 1f;
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] gaze ring: mode=" + mode
                    + " pos=" + pos.ToString("F2") + " dist=" + dist.ToString("F2")
                    + " hit=" + (hitName ?? "none")
                    + " cam=" + ct.position.ToString("F2") + " fwd=" + ct.forward.ToString("F2")
                    + " ringNow=" + t.position.ToString("F2") + " scale=" + t.localScale.x.ToString("F3")
                    + " parent='" + PathOf(t.parent) + "'");
            }
        }

        internal static bool HoverInteractable;
        internal static string HoverName;
        private readonly RaycastHit[] _gazeHits = new RaycastHit[12];
        private float _gazeLogAt;
        private string _gazeLastHit;
        private float _interactProbeAt;
        private bool _rigYawLogged;          // v1.25.0: 上次命中的物体名(变化时才打日志)

        private void DoSceneDiagnostics(float dt)
        {
            string scene = ActiveSceneName();
            float now = Time.realtimeSinceStartup;
            if (scene != _lastScene)
            {
                _lastScene = scene;
                _sceneTime = 0f;
                _sceneReported = false;
                _uiBridged = false;
                _probed = false;
            _gazeHidden = false;
            _gazeTick = 0f;          // 换场景要重新搭一次鼠标点击桥
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] scene -> '" + scene + "' t=" + now.ToString("F2")
                        + " cam=" + (_cam != null) + " fade=" + (_fade != null) + " ill=" + (_ill != null)
                        + " fades=" + _fades.Length);
                DumpIllusionCameras();
            ProbeRenderScale(scene);
                DumpReticles(ActiveSceneName());
                ApplyTouchBridge(ActiveSceneName());
            SyncGameGazeCameras(ActiveSceneName());
            }

            _sceneTime += dt;

            EnsureFlatUi();

            if (!_sceneReported && _sceneTime >= 6f)
            {
                _sceneReported = true;
                string info = "cam=" + (_cam != null) + " fade=" + (_fade != null) + " ill=" + (_ill != null)
                    + " fades=" + _fades.Length;
                for (int i = 0; i < _fades.Length; i++)
                    info += " | " + FadeText(_fades[i]);
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] +6s in '" + scene + "' t=" + now.ToString("F2") + ": " + info);
            }

            if (!_probed && FlatFpsPlugin.CenterProbe.Value
                && _sceneTime >= Mathf.Max(0.5f, FlatFpsPlugin.ProbeDelay.Value))
            {
                _probed = true;
                ProbeCenter(scene);
            }

            if (_sceneTime >= 5f)
                MaybeShoot(scene);
        }

        private static string FadeText(VRCameraFade fade)
        {
            if (fade == null)
                return "fade=null";
            try
            {
                Image img = fade.M_FadeImage;
                return "'" + PathOf(fade.transform) + "' IsFading=" + fade.IsFading
                    + " alpha=" + (img != null ? img.color.a.ToString("F3") : "n/a")
                    + " imgEnabled=" + (img != null && img.enabled);
            }
            catch (Exception e)
            {
                return "fade-probe: " + e.Message;
            }
        }

        internal static string PathOf(Transform t)
        {
            if (t == null)
                return "<null>";
            string s = t.name;
            int guard = 0;
            while (t.parent != null && guard++ < 16)
            {
                t = t.parent;
                s = t.name + "/" + s;
            }
            return s;
        }

        // ---------------- 白屏取证: 存图 + 渲染状态 ----------------
        private void MaybeShoot(string scene)
        {
            if (!FlatFpsPlugin.SaveFrames.Value || scene.Length == 0)
                return;
            if (IsLoaderScene(scene) || _shotScene == scene)
                return;
            if (FlatFpsPlugin.MaxShots.Value > 0 && _shotCount >= FlatFpsPlugin.MaxShots.Value)
                return;
            _shotScene = scene;
            _shotCount++;
            StartCoroutine(CaptureFrame(scene, _shotCount));
        }

        private static string ShotPath(string tag)
        {
            try
            {
                return Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                    + Path.DirectorySeparatorChar + "vrk_frame_" + tag + ".png";
            }
            catch
            {
                return "vrk_frame_" + tag + ".png";
            }
        }

        private IEnumerator CaptureFrame(string scene, int n)
        {
            yield return new WaitForEndOfFrame();
            string tag = scene + "_" + n;
            DumpRenderState(tag);
            try
            {
                int w = Screen.width;
                int h = Screen.height;
                Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0f, 0f, (float)w, (float)h), 0, 0);
                tex.Apply();
                byte[] png = tex.EncodeToPNG();
                Object.Destroy(tex);
                string path = ShotPath(tag);
                File.WriteAllBytes(path, png);
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] frame saved: " + path + " (" + png.Length + " bytes, " + w + "x" + h + ")");
            }
            catch (Exception e)
            {
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] frame save failed: " + e);
            }
        }

        private static string NdcBox(RectTransform rt, Camera cam, out string onScreen)
        {
            onScreen = "?";
            if (rt == null || cam == null)
                return "n/a";
            Vector3[] c = new Vector3[4];
            rt.GetWorldCorners(c);
            float minx = 9f, miny = 9f, maxx = -9f, maxy = -9f;
            bool anyFront = false;
            for (int i = 0; i < 4; i++)
            {
                Vector3 v = cam.WorldToViewportPoint(c[i]);
                if (v.z > 0f)
                    anyFront = true;
                if (v.x < minx) minx = v.x;
                if (v.y < miny) miny = v.y;
                if (v.x > maxx) maxx = v.x;
                if (v.y > maxy) maxy = v.y;
            }
            float cover = Mathf.Clamp01(maxx - minx) * Mathf.Clamp01(maxy - miny);
            onScreen = (anyFront && maxx > 0f && minx < 1f && maxy > 0f && miny < 1f) ? "yes" : "NO";
            return "vp=[" + minx.ToString("F2") + "," + miny.ToString("F2") + " .. " + maxx.ToString("F2") + "," + maxy.ToString("F2")
                 + "] cover=" + (cover * 100f).ToString("F0") + "%";
        }

        private void DumpGraphics()
        {
            Camera cam = _cam;
            if (cam == null)
                cam = Camera.main;
            if (cam == null)
                return;

            int mask = cam.cullingMask;
            Object[] gs = Resources.FindObjectsOfTypeAll(typeof(Graphic));
            int shown = 0;
            for (int i = 0; i < gs.Length && shown < 40; i++)
            {
                Graphic g = gs[i] as Graphic;
                if (g == null || !g.gameObject.activeInHierarchy)
                    continue;
                RectTransform rt = g.rectTransform;
                if (rt == null)
                    continue;
                float dist = Vector3.Distance(cam.transform.position, rt.position);
                if (dist > 12f)
                    continue;
                shown++;
                CanvasRenderer cr = g.canvasRenderer;
                bool inMask = (g.gameObject.layer & mask) != 0;
                string onScreen;
                string box = NdcBox(rt, cam, out onScreen);
                Vector3 ls = rt.lossyScale;
                string sizeM = (rt.rect.width * Mathf.Abs(ls.x)).ToString("F2") + "x" + (rt.rect.height * Mathf.Abs(ls.y)).ToString("F2");
                Canvas cv = g.canvas;
                FlatFpsPlugin.Log.LogInfo("[FlatFPS]  gfx['" + PathOf(g.transform) + "'] "
                    + g.GetType().Name
                    + " en=" + (g.enabled ? 1 : 0)
                    + " col=" + g.color
                    + " cull=" + (cr != null && cr.cull ? 1 : 0)
                    + " ray=" + (g.raycastTarget ? 1 : 0)
                    + " layer=" + g.gameObject.layer + " inMask=" + (inMask ? 1 : 0)
                    + " size=" + sizeM + "m dist=" + dist.ToString("F2")
                    + " " + box + " on=" + onScreen
                    + " canvas=" + (cv != null ? cv.sortingOrder.ToString() : "-")
                    + " shader='" + (g.material != null && g.material.shader != null ? g.material.shader.name : "-") + "'");
            }
            FlatFpsPlugin.Log.LogInfo("[FlatFPS]  gfx total listed=" + shown);

            Object[] rs = Resources.FindObjectsOfTypeAll(typeof(Renderer));
            shown = 0;
            for (int i = 0; i < rs.Length && shown < 25; i++)
            {
                Renderer r = rs[i] as Renderer;
                if (r == null || !r.gameObject.activeInHierarchy || !r.enabled)
                    continue;
                float dist = Vector3.Distance(cam.transform.position, r.bounds.center);
                if (dist > 15f)
                    continue;
                shown++;
                string onScreen;
                string box = r.bounds.size.ToString("F2");
                onScreen = r.isVisible ? "yes" : "NO";
                FlatFpsPlugin.Log.LogInfo("[FlatFPS]  ren['" + PathOf(r.transform) + "'] "
                    + r.GetType().Name
                    + " visible=" + onScreen + " dist=" + dist.ToString("F2") + " bounds=" + box
                    + " layer=" + r.gameObject.layer + " inMask=" + (((r.gameObject.layer & mask) != 0) ? 1 : 0)
                    + " shader='" + (r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-") + "'");
            }
            FlatFpsPlugin.Log.LogInfo("[FlatFPS]  ren total listed=" + shown);
        }

        private void DumpRenderState(string tag)
        {
            try
            {
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] === frame state '" + tag + "' t=" + Time.realtimeSinceStartup.ToString("F2")
                    + " scene='" + ActiveSceneName() + "' screen=" + Screen.width + "x" + Screen.height + " ===");

                Camera[] cams = Camera.allCameras;
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] cameras=" + cams.Length);
                DumpGraphics();
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera c = cams[i];
                    if (c == null)
                        continue;
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS]  cam[" + i + "] '" + PathOf(c.transform) + "'"
                        + " depth=" + c.depth + " clear=" + c.clearFlags + " bg=" + c.backgroundColor
                        + " pos=" + c.transform.position.ToString("F2") + " euler=" + c.transform.eulerAngles.ToString("F1")
                        + " mask=0x" + c.cullingMask.ToString("X8")
                        + " target=" + (c.targetTexture != null ? TextureName(c.targetTexture) : "screen")
                        + " stereo=" + c.stereoTargetEye + " rect=" + c.rect + " ortho=" + c.orthographic);
                }

                Object[] fades = Resources.FindObjectsOfTypeAll(typeof(VRCameraFade));
                for (int i = 0; i < fades.Length; i++)
                {
                    VRCameraFade f = fades[i] as VRCameraFade;
                    if (f != null)
                        FlatFpsPlugin.Log.LogInfo("[FlatFPS]  fade[" + i + "] " + FadeText(f));
                }

                Object[] canvases = Resources.FindObjectsOfTypeAll(typeof(Canvas));
                int shown = 0;
                for (int i = 0; i < canvases.Length && shown < 12; i++)
                {
                    Canvas cv = canvases[i] as Canvas;
                    if (cv == null || !cv.gameObject.activeInHierarchy)
                        continue;
                    shown++;
                    Vector3 cp = cv.transform.position;
                    float cdist = -1f;
                    float cfront = 0f;
                    if (_cam != null)
                    {
                        Vector3 camPos = _cam.transform.position;
                        cdist = Vector3.Distance(camPos, cp);
                        Vector3 dir = cp - camPos;
                        if (dir.sqrMagnitude > 1e-6f)
                            cfront = Vector3.Dot(dir.normalized, _cam.transform.forward);
                    }
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS]  canvas['" + PathOf(cv.transform) + "'] mode=" + cv.renderMode
                        + " worldCamera=" + (cv.worldCamera != null ? cv.worldCamera.name : "null")
                        + " order=" + cv.sortingOrder + " scale=" + cv.scaleFactor.ToString("F2")
                        + " pos=" + cp.ToString("F2") + " dist=" + cdist.ToString("F2")
                        + " front=" + cfront.ToString("F2")+ " facing=" + (_cam != null ? Vector3.Dot(cv.transform.forward, (_cam.transform.position - cp).normalized).ToString("F2") : "?")
                        + " scaleLossy=" + cv.transform.lossyScale.ToString("F3")
                        + " en=" + (cv.enabled ? 1 : 0) + " layer=" + cv.gameObject.layer
                        + " fov=" + (_cam != null ? _cam.fieldOfView.ToString("F1") : "?"));
                }

                Object[] images = Resources.FindObjectsOfTypeAll(typeof(Image));
                int big = 0;
                for (int i = 0; i < images.Length && big < 10; i++)
                {
                    Image img = images[i] as Image;
                    if (img == null || !img.enabled || !img.gameObject.activeInHierarchy)
                        continue;
                    if (img.color.a < 0.9f)
                        continue;
                    Rect r = img.rectTransform.rect;
                    if (Mathf.Abs(r.width) * Mathf.Abs(r.height) < 100000f)
                        continue;
                    big++;
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS]  image['" + PathOf(img.transform) + "'] color=" + img.color
                        + " size=" + r.width.ToString("F0") + "x" + r.height.ToString("F0"));
                }
            }
            catch (Exception e)
            {
                FlatFpsPlugin.Log.LogWarning("[FlatFPS] dump failed: " + e);
            }
        }

        private static string TextureName(Texture t)
        {
            if (t == null)
                return "null";
            return t.name.Length > 0 ? t.name : "<unnamed>";
        }

        internal static string ActiveSceneName()
        {
            try
            {
                return SceneManager.GetActiveScene().name;
            }
            catch
            {
                return "?";
            }
        }
    }

    // ------------------------------------------------------------------
    // 5. 右上角操作指南: F7 显示/隐藏, 自适应字号, 不拦截任何鼠标/键盘输入
    // ------------------------------------------------------------------
    internal class HelpOverlay : MonoBehaviour
    {
        private static readonly string[] Lines =
        {
            "W A S D        前 / 左 / 后 / 右",
            "PageUp  PageDn 上升 / 下降",
            "数字 6 / 4     左右转视角",
            "数字 8 / 2     俯仰      5 回正",
            "End / Home     拉近 / 拉远",
            ". / \\          翻滚  (Shift = 45°)",
            "R              重置视角 (翻滚归零)",
            "F              切视角 第一 / 第三 / 自由 (默认 第一)",
            "第一人称       WASD 走动, 鼠标看 (同 VR 视角)",
            "  眼太高/太矮   PageUp / PageDown 微调 (5cm/次)",
            "第三人称       环绕角色观察 (游戏原版)",
            "自由视角       WASD 平移 PageUp/Dn 升降 Shift 加速",
            "] / [          视野      ; 复位",
            "= / -          键盘灵敏度",
            "鼠标移动       转视角 (无需按键)",
            "按住 Alt       释放鼠标指针 (松手即锁定)",
            "左键 决定   右键 取消/结束   中键 切换",
            "鼠标速度       配置 MouseSpeed (当前 800)",
        };

        private bool _visible;
        private KeyCode _key = KeyCode.F7;
        private bool _keyResolved;
        private string _keyRaw;
        private bool _armed;          // 已检测到"进入游戏"，只自动展示一次
        private float _autoHideAt = -1f;

        // 视角切换提示（左上角，短暂显示，独立于面板是否展开）
        private static string _toastText;
        private static float _toastUntil = -1f;
        private GUIStyle _toastStyle;
        private Texture2D _toastBg;
        private int _toastScreen = -1;
        private Vector2 _toastSize;

        internal static void Toast(string text, float seconds)
        {
            _toastText = text;
            _toastUntil = Time.realtimeSinceStartup + Mathf.Max(0.5f, seconds);
        }

        internal static string ViewModeToastText(int mode)
        {
            if (mode == 0)
                return "\u89c6\u89d2\uff1a\u7b2c\u4e00\u4eba\u79f0  \u2014  \u9f20\u6807\u8f6c\u5934, WASD \u8d70\u52a8";
            if (mode == 2)
                return "\u89c6\u89d2\uff1a\u81ea\u7531\u89c6\u89d2  \u2014  WASD \u5e73\u79fb, PageUp/PageDown \u5347\u964d";
            return "\u89c6\u89d2\uff1a\u7b2c\u4e09\u4eba\u79f0  \u2014  \u73af\u7ed5\u89d2\u8272 (\u6e38\u620f\u539f\u7248)";
        }

        // 初始化/加载类场景不算"进入游戏"，这些场景里不弹面板
        private static readonly HashSet<string> LoaderScenes = new HashSet<string>
        {
            "Init", "Init2", "CheckHMD", "SetHMD", "Logo2", "Logo", "Attention"
        };

        private GUIStyle _boxStyle;
        private GUIStyle _textStyle;
        private Texture2D _bgTex;
        private string _body;
        private Vector2 _bodySize;
        private int _builtScreen = -1;
        private float _builtScale = -1f;

        private void Awake()
        {
            _visible = false; // 面板由"进入游戏"事件触发，避免在 Logo 白屏期间挡视线
        }

        private void Update()
        {
            HandleHotkey();
            HandleAutoShow();

            // 自动隐藏倒计时
            if (_visible && _autoHideAt > 0f && Time.realtimeSinceStartup >= _autoHideAt)
            {
                _visible = false;
                _autoHideAt = -1f;
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] help overlay auto-hidden");
            }
        }

        private void HandleHotkey()
        {
            if (FlatFpsPlugin.VrMode)
                return;
            string raw = FlatFpsPlugin.HelpHotkey.Value;
            if (raw != _keyRaw)
            {
                _keyRaw = raw;
                _keyResolved = TryParseKey(raw, out _key);
                if (!_keyResolved && FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] help hotkey '" + raw + "' is not a KeyCode name; overlay disabled");
            }
            if (!_keyResolved)
                return;

            if (Input.GetKeyDown(_key))
            {
                _visible = !_visible;
                _autoHideAt = -1f; // 手动操作接管，取消倒计时
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] help overlay " + (_visible ? "shown" : "hidden") + " (manual)");
            }
        }

        // 离开 Init/Logo 等加载场景 -> 认为已经进入游戏，自动展示 N 秒
        private void HandleAutoShow()
        {
            if (_armed || FlatFpsPlugin.VrMode)
                return;
            string scene = ActiveScene();
            if (scene.Length == 0 || LoaderScenes.Contains(scene))
                return;

            _armed = true;
            if (!FlatFpsPlugin.HelpShowOnStart.Value)
            {
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogInfo("[FlatFPS] entered '" + scene + "', overlay auto-show disabled");
                return;
            }

            float secs = FlatFpsPlugin.HelpAutoHideSeconds.Value;
            _visible = true;
            _autoHideAt = secs > 0.01f ? Time.realtimeSinceStartup + secs : -1f;
            if (FlatFpsPlugin.Log != null)
                FlatFpsPlugin.Log.LogInfo("[FlatFPS] entered '" + scene + "', overlay shown for "
                    + (secs > 0.01f ? secs.ToString("0.0") + "s" : "until " + FlatFpsPlugin.HelpHotkey.Value));
        }

        private static string ActiveScene()
        {
            try
            {
                return SceneManager.GetActiveScene().name;
            }
            catch
            {
                return "";
            }
        }

        private static bool TryParseKey(string raw, out KeyCode key)
        {
            key = KeyCode.None;
            if (string.IsNullOrEmpty(raw))
                return false;
            try
            {
                object o = Enum.Parse(typeof(KeyCode), raw.Trim(), true);
                key = (KeyCode)o;
                return key != KeyCode.None;
            }
            catch
            {
                return false;
            }
        }

        private void Build()
        {
            float scale = Mathf.Clamp(FlatFpsPlugin.HelpScale.Value, 0.6f, 2.0f);
            int fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height / 100f * scale), 10, 28);

            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.62f));
                _bgTex.Apply();
                _bgTex.hideFlags = HideFlags.HideAndDontSave;
            }

            _boxStyle = new GUIStyle();
            _boxStyle.normal.background = _bgTex;

            _textStyle = new GUIStyle();
            _textStyle.fontSize = fontSize;
            _textStyle.normal.textColor = new Color(0.96f, 0.96f, 0.96f, 1f);
            _textStyle.wordWrap = false;
            _textStyle.richText = false;
            _textStyle.alignment = TextAnchor.UpperLeft;
            _textStyle.padding = new RectOffset(0, 0, 0, 0);

            // 内置 Arial 没有中文字形, 用系统字体动态渲染
            try
            {
                Font f = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun", "Arial" }, fontSize);
                if (f != null)
                {
                    f.hideFlags = HideFlags.HideAndDontSave;
                    _textStyle.font = f;
                }
            }
            catch
            {
                // 拿不到系统字体就退回默认字体, 中文会显示为方块但不影响功能
            }

            _body = BuildBody();
            _bodySize = _textStyle.CalcSize(new GUIContent(_body));
            _builtScreen = Screen.height;
            _builtScale = scale;
        }

        private static string BuildBody()
        {
            List<string> rows = new List<string>();
            rows.Add("VR Kanojo  免VR 操作指南");
            rows.Add(new string('-', 32));
            rows.AddRange(Lines);

            string hk = FlatFpsPlugin.HelpHotkey.Value;
            if (string.IsNullOrEmpty(hk))
                hk = "(未设置)";
            rows.Add(hk.PadRight(15) + "显示 / 隐藏本面板");
            return string.Join("\n", rows.ToArray());
        }

        // ---- \u4e2d\u5fc3\u51c6\u661f (v1.19.0): \u56fe\u50cf\u6765\u81ea\u65e7\u7248 Play Without VR \u63d2\u4ef6\u91cc\u7684 crosshair.png,
        // \u5df2\u79bb\u7ebf\u8f6c\u6210 128x128 RGBA \u5e76\u4f5c\u4e3a\u5185\u5d4c\u8d44\u6e90, \u8fd0\u884c\u65f6\u4e0d\u9700\u8981\u4efb\u4f55\u56fe\u7247\u89e3\u7801\u5668\u3002
        private static Texture2D _crossTex;
        private static bool _crossTried;
        private static int _crossTexScreen = -1;

        private static Texture2D CrosshairTexture()
        {
            if (_crossTried)
                return _crossTex;
            _crossTried = true;
            try
            {
                using (System.IO.Stream st = typeof(HelpOverlay).Assembly
                    .GetManifestResourceStream("VRKanojo.FlatFPS.crosshair.bin"))
                {
                    if (st == null)
                    {
                        if (FlatFpsPlugin.Log != null)
                            FlatFpsPlugin.Log.LogWarning("[FlatFPS] crosshair resource missing (VRKanojo.FlatFPS.crosshair.bin)");
                        return null;
                    }
                    byte[] raw = new byte[st.Length];
                    int read = 0;
                    while (read < raw.Length)
                    {
                        int n = st.Read(raw, read, raw.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    const int N = 128;
                    if (read != N * N * 4)
                    {
                        if (FlatFpsPlugin.Log != null)
                            FlatFpsPlugin.Log.LogWarning("[FlatFPS] crosshair resource size " + read + " != " + (N * N * 4));
                        return null;
                    }
                    Texture2D tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
                    tex.filterMode = FilterMode.Bilinear;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    Color32[] px = new Color32[N * N];
                    for (int y = 0; y < N; y++)
                    {
                        int src = (N - 1 - y) * N * 4;      // Unity 贴图原点在左下, \u6570\u636e\u662f\u4e0a\u4e0b\u7ffb\u8f6c\u7684
                        int dst = y * N;
                        for (int x = 0; x < N; x++)
                        {
                            int i = src + x * 4;
                            px[dst + x] = new Color32(raw[i], raw[i + 1], raw[i + 2], raw[i + 3]);
                        }
                    }
                    tex.SetPixels32(px);
                    tex.Apply(false, true);
                    _crossTex = tex;
                }
            }
            catch (Exception e)
            {
                if (FlatFpsPlugin.Log != null)
                    FlatFpsPlugin.Log.LogWarning("[FlatFPS] crosshair texture failed: " + e.GetType().Name + ": " + e.Message);
            }
            return _crossTex;
        }

        private static Texture2D _lineTex;
        private static GUIStyle _hoverStyle;
        private static int _hoverScreen;

        private static Texture2D LineTex()
        {
            if (_lineTex != null)
                return _lineTex;
            _lineTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _lineTex.SetPixel(0, 0, Color.white);
            _lineTex.Apply(false, true);
            return _lineTex;
        }

        // v1.23.0 P1/P2: 准星对准可交互物 -> 中心四角方框 + "E 交互" 提示。
        // 不吃点击(GUI.DrawTexture/GUI.Label 都不参与 EventSystem 拾取), 无每帧 GC(贴图与样式都缓存)。
        private void DrawHoverReticle()
        {
            if (FlatFpsPlugin.VrMode)
                return;
            if (FlatFpsPlugin.HoverReticle == null || !FlatFpsPlugin.HoverReticle.Value)
                return;
            if (!FlatFpsRuntime.HoverInteractable)
                return;
            Texture2D tex = LineTex();
            if (tex == null)
                return;

            float k = Mathf.Max(0.5f, Screen.height / 1440f);
            float gap = 8f * k;
            float len = 8f * k;
            float th = Mathf.Max(2f, Mathf.Round(2f * k));
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            GUI.depth = -249;
            Color old = GUI.color;
            GUI.color = new Color(1f, 0.86f, 0.42f, 0.95f);
            // 左上
            GUI.DrawTexture(new Rect(cx - gap - len, cy - gap - th * 0.5f, len, th), tex);
            GUI.DrawTexture(new Rect(cx - gap - th * 0.5f, cy - gap - len, th, len), tex);
            // 右上
            GUI.DrawTexture(new Rect(cx + gap, cy - gap - th * 0.5f, len, th), tex);
            GUI.DrawTexture(new Rect(cx + gap - th * 0.5f, cy - gap - len, th, len), tex);
            // 左下
            GUI.DrawTexture(new Rect(cx - gap - len, cy + gap - th * 0.5f, len, th), tex);
            GUI.DrawTexture(new Rect(cx - gap - th * 0.5f, cy + gap, th, len), tex);
            // 右下
            GUI.DrawTexture(new Rect(cx + gap, cy + gap - th * 0.5f, len, th), tex);
            GUI.DrawTexture(new Rect(cx + gap - th * 0.5f, cy + gap, th, len), tex);
            GUI.color = old;

            // 提示文字
            if (_hoverStyle == null || _hoverScreen != Screen.height)
            {
                _hoverScreen = Screen.height;
                int fs = Mathf.Clamp(Mathf.RoundToInt(Screen.height / 95f), 11, 26);
                _hoverStyle = new GUIStyle(GUI.skin.label);
                _hoverStyle.font = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" }, fs);
                _hoverStyle.fontSize = fs;
                _hoverStyle.alignment = TextAnchor.MiddleCenter;
                _hoverStyle.normal.textColor = new Color(1f, 0.92f, 0.66f, 0.95f);
            }
            string key = (FlatFpsPlugin.InteractKey != null && !string.IsNullOrEmpty(FlatFpsPlugin.InteractKey.Value))
                ? FlatFpsPlugin.InteractKey.Value.Trim() : "E";
            GUI.Label(new Rect(cx - 140f, cy + gap + len + 4f, 280f, 30f), key + "  \u4ea4\u4e92", _hoverStyle);
        }

        private void DrawCrosshair()
        {
            if (FlatFpsPlugin.VrMode || !FlatFpsPlugin.Crosshair.Value)
                return;
            if (_crossTexScreen != Screen.height)
            {
                _crossTexScreen = Screen.height;
                _crossTex = null;
                _crossTried = false;
            }
            if (!FlatFpsPlugin.CrosshairFirstPersonOnly.Value || FlatFpsRuntime.ViewMode == 0)
            {
                Texture2D tex = CrosshairTexture();
                if (tex == null)
                    return;
                float scale = Mathf.Max(0.5f, Screen.height / 1440f);
                float size = Mathf.Clamp(FlatFpsPlugin.CrosshairSize.Value * scale, 8f, Screen.height * 0.4f);
                float half = size * 0.5f;
                Rect r = new Rect(Mathf.Round((Screen.width * 0.5f) - half),
                                  Mathf.Round((Screen.height * 0.5f) - half), size, size);
                GUI.depth = -250;
                Color old = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(FlatFpsPlugin.CrosshairAlpha.Value));
                GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
                GUI.color = old;
            }
        }

        private void OnGUI()
        {
            // v1.28.0: VR 入口下不画任何自建 UI(帮助面板 / toast / 准星 / 交互方框)。
            if (FlatFpsPlugin.VrMode)
                return;
            DrawCrosshair();
            DrawHoverReticle();
            DrawToast();

            if (!_visible)
                return;
            if (_textStyle == null || _builtScreen != Screen.height || !Mathf.Approximately(_builtScale, FlatFpsPlugin.HelpScale.Value))
                Build();

            const float margin = 12f;
            float w = Mathf.Ceil(_bodySize.x) + 18f;
            float h = Mathf.Ceil(_bodySize.y) + 14f;
            Rect panel = new Rect(Screen.width - w - margin, margin, w, h);

            GUI.depth = -100; // 画在最上层
            GUI.Box(panel, GUIContent.none, _boxStyle);
            GUI.Label(new Rect(panel.x + 9f, panel.y + 7f, Mathf.Ceil(_bodySize.x), Mathf.Ceil(_bodySize.y)), _body, _textStyle);
        }

        private void BuildToast()
        {
            int fs = Mathf.Clamp(Mathf.RoundToInt(Screen.height / 85f), 14, 34);
            _toastStyle = new GUIStyle();
            _toastStyle.fontSize = fs;
            _toastStyle.normal.textColor = new Color(1f, 0.94f, 0.72f, 1f);
            _toastStyle.alignment = TextAnchor.UpperLeft;
            _toastStyle.wordWrap = false;
            _toastStyle.richText = false;
            _toastStyle.padding = new RectOffset(0, 0, 0, 0);
            try
            {
                Font f = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun", "Arial" }, fs);
                if (f != null)
                    _toastStyle.font = f;
            }
            catch { }
            if (_toastBg == null)
            {
                _toastBg = new Texture2D(1, 1);
                _toastBg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.68f));
                _toastBg.Apply();
            }
            _toastScreen = Screen.height;
        }

        private void DrawToast()
        {
            if (string.IsNullOrEmpty(_toastText) || Time.realtimeSinceStartup > _toastUntil)
                return;
            if (_toastStyle == null || _toastScreen != Screen.height)
                BuildToast();

            _toastSize = _toastStyle.CalcSize(new GUIContent(_toastText));
            float w = Mathf.Ceil(_toastSize.x) + 22f;
            float h = Mathf.Ceil(_toastSize.y) + 14f;
            Rect r = new Rect(14f, 14f, w, h);

            GUI.depth = -200;
            GUI.DrawTexture(r, _toastBg);
            GUI.Label(new Rect(r.x + 11f, r.y + 7f, Mathf.Ceil(_toastSize.x), Mathf.Ceil(_toastSize.y)), _toastText, _toastStyle);
        }
    }
}
