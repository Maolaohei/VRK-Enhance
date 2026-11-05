# VRK_FlatFPS — 免VR白屏修复 + FPS 键位

生成时间: 2026-11-04 16:19 (Asia/Shanghai)
环境: VR Kanojo 1.07 / Unity 5.4.5f1 / BepInEx 5.4.12.0 (HarmonyX 2.5.1) / .NET 3.5 (mscorlib 2.0)

- 安装位置: `BepInEx/plugins/VRK_FlatFPS.dll`  (v1.3.0, SHA-256 `add292c20d6b7785535c1c731ebbe352bfea287888aecf5aeb6a366680c166e1`)
- 源码: `BepInEx/plugins/_src/VRK_FlatFPS/VRK_FlatFPS.cs`
- 旧插件已停用: `BepInEx/plugins/VRK_PlayWithoutVR.dll` -> `VRK_PlayWithoutVR.dll.disabled`
- 游戏本体文件未改动: `VR_Kanojo.exe` `65517dfd...799fb9e`，`VR_Kanojo_Data/Managed/Assembly-CSharp.dll` `ce4058f7...caebe2b1`

## 一、白屏根因（静态证据）

`VRK_PlayWithoutVR.dll` 的 `LogoScene.Start` Postfix（反编译 92-113 行）在 Logo 场景第一帧做了：

```csharp
OVRCameraRig rig = Camera.main.gameObject.GetComponentInParent<OVRCameraRig>();
rig.gameObject.AddComponent<IllusionCamera>().Set(...);
rig.gameObject.SetActive(false);   // ← 杀协程
rig.gameObject.SetActive(true);
```

- `VRCameraFade` 组件挂在 `MainCamera` 上（`Manager.Scene.InitCameraRig` 中 `MainCamera_.gameObject.AddComponent<VRCameraFade>()`），
  白色遮罩 `Fader/FadePanel` 也 parent 到 `Camera.main.transform`（`VRCameraFade.Awake`）。
- `OVRCameraRig` 是 `Camera.main` 的**祖先**，`SetActive(false)` 会停掉该层级上所有协程。
- `VRCameraFade.BeginFade`（迭代器 `<BeginFade>c__IteratorE1::MoveNext`）开头 `m_IsFading = true`，
  只有循环跑完才 `m_IsFading = false`；全程序只有 `<BeginFade>` 和 `<BeginFadeOut>` 两个 MoveNext 写这个字段。
- 遮罩颜色 `m_FadeColor = Color.white`，`BeginFade` 第一步就是 `color = Lerp(white, transparent, 0)` = **纯白**。
- 协程在第一帧被杀死 => `m_IsFading` 永久 true、遮罩永久 alpha≈1 => **进画面就白屏**；
  同时 `LogoScene.Update` 的 `State.FadeNow` 分支等 `!VRCameraFade_.IsFading`，永远等不到 => Logo 场景永不推进。

另一条独立风险（也已处理）：`LogoScene.Start` 里 `Debug.Log("OVRManager... " + OVRManager.instance.trackingOriginType)`
对 `OVRManager.instance` 无 null 检查，无 HMD 且场景里没有 OVRManager 时报 NRE，
`state = State.Fade` 之后的语句全部不执行。

## 二、本插件做了什么

| # | Harmony 目标 | 行为 |
|---|---|---|
| 1 | `InitScene2.CheckHMD` Prefix | `GlobalVRKanojo.ForceMouse_=true; __result=true;` 直接走平面模式，不依赖 `VRDevice`、不碰 `Game` 单例（旧插件会 NRE）。 |
| 2 | `LogoScene.Start` Prefix | 先把 `VRCameraFade_` / `Canvas_.worldCamera` 补好；`OVRManager.instance == null` 时跳过会 NRE 的原方法，只做 `ConfigEffector.EffectAllDisable()`。**不再做任何 SetActive 切换。** |
| 3 | `LogoScene.Start` Finalizer | 吞掉异常并记日志，保证 Logo 状态机能走下去。 |
| 4 | `IllusionCamera.KeyInput_Value` Prefix | 只改它收到的 KeyCode 实参：`RightArrow→D`、`LeftArrow→A`、`UpArrow→W`、`DownArrow→S`。小键盘旋转、PageUp/PageDown、End/Home、括号 FOV 键全部保持原样。 |
| 6 | 运行时 `HelpOverlay` | 右上角操作指南面板：`F7` 显示/隐藏，字号按分辨率自适应、宽度随文字自适应，半透明底（alpha 0.62），只用 `GUI.Box`+`GUI.Label`，不创建任何可交互控件 → 不吃鼠标点击、不吃键盘。中文用 `Font.CreateDynamicFontFromOSFont("Microsoft YaHei UI"…)` 动态字体渲染（内置 Arial 无中文字形）。 |
| 5 | 运行时 `FlatFpsRuntime` | **按住 Alt（左/右）释放鼠标指针，松手自动重新锁定**；按住期间自由视角本帧即停用（`ReadHoldKeys()` 在 `DoFreeLook()` 之前求值），不会出现"指针对准 UI 但镜头还在转"。白屏看门狗：`IsFading=true` 且 alpha 连续 2 秒不变 → 判定协程已死，强制 `m_IsFading=false` + alpha=0；alpha 全不透且同场景停滞 8 秒 → 同样清屏。另有鼠标自由视角、指针锁定（F10 解锁）、每场景诊断日志。 |

配置: `BepInEx/config/vrk.flatfps.cfg`（首次运行自动生成）

```
[General]  ForceFlatMode=true, LockCursor=true, CursorHoldKey="LeftAlt,RightAlt"
[Fade]     RepairStuckFade=true, StuckFadeSeconds=2.0, CoveredSceneSeconds=8.0
[Camera]   FreeLook=true, MouseSensitivity=0.12, InvertY=false
[Keys]     UseFpsKeys=true
[Help]     ShowOnStart=true, AutoHideSeconds=5, Hotkey=F7, Scale=1.0
[Debug]    Verbose=false
```

## 三、未运行游戏的验证方式（全部通过）

`dotnet run` 运行本目录 `_verify`（Mono.Cecil 21 项检查，结果 `RESULT: ALL CHECKS PASSED (0 failures)`）：

1. 插件程序集身份：引用 mscorlib 2.0.5.0 / UnityEngine / Assembly-CSharp / 0Harmony，`[BepInPlugin("vrk.flatfps")]` 存在。
2. 补丁目标真实存在且签名匹配：`InitScene2.CheckHMD`(private bool)、`LogoScene.Start`(private void)、
   `IllusionCamera.KeyInput_Value`(private bool(float&, float, KeyCode, KeyCode))。
3. 反射/引用到的成员真实存在：`VRCameraFade.m_IsFading/m_FadeImage/IsFading/M_FadeImage`、`LogoScene.VRCameraFade_/Canvas_`、
   `IllusionCamera.rotate/Rotate`、`Manager.Scene.VRCameraFade_/MainCamera_`、`GlobalVRKanojo.ForceMouse_`、`OVRManager.instance`、`ConfigEffector.EffectAllDisable`。
4. 键位重映射仿真：扫描 `IllusionCamera.KeyInput` 的 8 个 `KeyInput_Value` 调用点，
   仅 `(275,276)→(100,97)`、`(273,274)→(119,115)` 两处变化，其余 6 处（Keypad 旋转 / Page / End / FOV / roll）键位不变。
5. Harmony 绑定签名：`Prefix(ref bool __result) : bool`、`Prefix(LogoScene __instance) : bool`、
   `Finalizer(Exception __exception) : Exception`、`Prefix(ref KeyCode addKey, ref KeyCode subKey) : void`。
6. `[HarmonyPatch]` 特性里的 (Type, method) 参数逐个解析到 Assembly-CSharp 真实方法，不会在 PatchAll 抛异常。
7. 白屏机理证明：全程序只有 `<BeginFade>` / `<BeginFadeOut>` 的 MoveNext 写 `m_IsFading`；
   BeginFade 先置 true、循环结束才置 false、`Image.set_color` 两次；`LogoScene.Update` 读 `get_IsFading`；
   `LogoScene.Start` 对 `OVRManager.instance` 无 null 检查。
11. 自动显示：`HelpOverlay.HandleAutoShow()` 检测到活动场景离开 7 个加载场景(Init/Init2/CheckHMD/SetHMD/Logo2/Logo/Attention) 后，
    自动 `_visible=true` 并把 `_autoHideAt = realtimeSinceStartup + AutoHideSeconds(默认 5.0)`；`Update()` 到点自动隐藏；`_armed` 保证一次运行只弹一次；
    手动按 Hotkey 会清掉倒计时(接管显示)。
10. 指针释放：`ReadHoldKeys()` 在 `Update` 里先于 `DoFreeLook()`（IL_004a vs IL_0069），`DoCursor` 用 `GetKey`（按住式）而非 `GetKeyDown`；
    旧粘性开关字段 `_cursorLocked` 已移除；游戏本体从不读 `Fire2`/`Fire3`，裸按左 Alt 不会触发任何游戏动作。
9. 操作指南：`HelpOverlay` 含 `OnGUI`/`Update`；`GUI.*` 调用只有 `set_depth/Box/Label`（无可交互控件）；
   `CreateDynamicFontFromOSFont` 与 `Enum.Parse(KeyCode)` 均被引用；游戏本体 F 键只用到 F2/F8/F11，**F7 空闲**。
8. 冲突扫描：11 个插件里只有 `VRK_PlayWithoutVR.dll` 调用 `GameObject.SetActive` 且引用 `IllusionCamera`，已停用。

## 四、回滚

```
move BepInEx\plugins\VRK_FlatFPS.dll BepInEx\plugins\VRK_FlatFPS.dll.disabled
move BepInEx\plugins\VRK_PlayWithoutVR.dll.disabled BepInEx\plugins\VRK_PlayWithoutVR.dll
```

## v1.4.0 — 白屏取证 + 多实例看门狗 + 孤儿面板兜底

### 上一轮为什么"看门狗没动作"

老版看门狗只认 `Manager.Scene.Instance.VRCameraFade_` 这一个引用（且 `Update()` 里带 `_fade != null` 前置条件）。
实测日志里 `[FlatFPS] repaired white screen:` **一行都没有**，说明真正盖住屏幕的那块面板并不是它盯着的那个实例（或者当时引用还是 null）。
`VRCameraFade.Awake()` 会 `Instantiate("Prefabs/Fader")` 并**强制 `m_FadeImage.enabled = true`**，序列化颜色是白色 —— 只要 `FadeIn()` 没跑（或被 `m_IsFading == true` 挡住），屏幕就是永久纯白，而游戏逻辑与音频照常推进。

### 本版改动

| # | 改动 | 目的 |
|---|------|------|
| 1 | `ScanFades()` 每秒 `Resources.FindObjectsOfTypeAll(typeof(VRCameraFade))` | 不再依赖单个引用，场景里所有 Fade 实例都在监控内 |
| 2 | `WatchOne()` 每个实例独立计时（`_tracks`） | 一个实例卡死不会漏判，也不会互相干扰 |
| 3 | `CheckEscalate()`：同一遮罩修复 2 次仍在 `HardKillSeconds` 内重新盖屏 → `Image.canvas.gameObject.SetActive(false)` | 彻底停用这块面板，不再与游戏抢屏幕 |
| 4 | `WatchOrphanFaders()`：扫 `Image` 里名字含 `Fade`、alpha ≥ 0.99、宽度 ≥ 90% 屏幕的**没有 VRCameraFade 组件**的孤儿面板，停留 `CoveredSceneSeconds` 秒后把 alpha 清零 | 覆盖"面板存在但组件没挂/被销毁"的情况 |
| 5 | `CaptureFrame()`：进入正式场景 5 秒后 `WaitForEndOfFrame` → `ReadPixels` → `EncodeToPNG` → 游戏根目录 `vrk_frame_<场景>_<n>.png` | 白屏时**截图是唯一确凿证据**，不用再猜 |
| 6 | `DumpRenderState()`：打印全部相机的 `depth / clearFlags / backgroundColor / position / euler / cullingMask / targetTexture / stereoTargetEye / rect / ortho`，全部 Canvas 的 `renderMode / worldCamera / sortingOrder`，alpha ≥ 0.9 且面积 ≥ 10 万像素的 Image | 一眼看出是"挡板"还是"相机没渲染" |
| 7 | `Refresh()` 里把 `Camera.stereoTargetEye` 强制为 `None`（仅平面模式） | 无头显时不让相机走立体路径 |
| 8 | 所有日志加 `t=<realtimeSinceStartup>` 时间戳 | 定位"长时间未响应"卡在哪一步 |

### 新配置

```ini
[Fade]
HardKillSeconds = 3        ; 修复两次仍复现 -> 停用整块 Fade 面板 (0=不停用)
[Diagnostics]
SaveFrames = true          ; 存 vrk_frame_*.png 取证图
MaxShots = 3               ; 最多存几张(每场景最多1张)
```

### 静态验证

`_verify` 扩到 **74 项**，新增 L 组 19 项（多实例看门狗、升级停用、PNG 取证链路、渲染状态 dump、`stereoTargetEye` 钉死、三个新配置项、孤儿面板兜底）：

```
verify.exe VR_Kanojo_Data/Managed/Assembly-CSharp.dll BepInEx/plugins/VRK_FlatFPS.dll
RESULT: ALL CHECKS PASSED (0 failures)
```

### 怎么用这次的结果定位

跑一次后看：

1. 游戏根目录有没有 `vrk_frame_Title_1.png` —— **有**：直接看颜色，纯白 / 纯黑 / 正常画面，一目了然。
2. `BepInEx/LogOutput.log` 里搜 `repaired white screen` / `repaired orphan white panel` / `hardened:` —— 有就是看门狗已修好。
3. 搜 `cam[0]` —— 看 `bg=` 与 `clear=`：如果是 `clear=SolidColor bg=RGBA(1.000, 1.000, 1.000, 1.000)` 且没有任何 Image 覆盖，那白屏来自相机背景色，需要另做处理。

## v1.4.1 — 白屏根因（序列化数据实证）+ Awake 处清零

### 取证方法

`VR_Kanojo_Data/resources.assets` 里直接按 SerializedFile 二进制布局解析 MonoBehaviour
（头部 `PPtr<GameObject>` 12B + `m_Enabled` 1B + 对齐 + `PPtr<MonoScript>` 12B + `m_Name` 字符串，
之后按字段声明顺序读取；`VRCameraFade` 的 MonoScript 路径 ID = 374，
其序列化字段顺序 = `_Fade(int)` `m_FadeImage(PPtr)` `m_DefaultSnapshot(PPtr)` `m_FadedSnapshot(PPtr)`
`m_FadeColor(Color)` `m_FadeDuration(float)` `m_FadeInOnSceneLoad(bool)` `m_FadeInOnStart(bool)` `VROverlay_(PPtr)`）。

### 解析结果（决定性）

```
MonoBehaviour pid 4366  script(1,374)=VRCameraFade
    _Fade=0  m_FadeImage_pid=0 (null!)  m_FadeColor=(1.0, 1.0, 1.0, 1.0)
    m_FadeDuration=2.0  m_FadeInOnSceneLoad=0  m_FadeInOnStart=0  VROverlay_pid=4254

MonoBehaviour pid 4459  script(1,374)=VRCameraFade
    _Fade=0  m_FadeImage_pid=4388     m_FadeColor=(1.0, 1.0, 1.0, 1.0)
    m_FadeDuration=2.0  m_FadeInOnSceneLoad=0  m_FadeInOnStart=0  VROverlay_pid=5263

MonoBehaviour pid 4388  script(1,28)=UnityEngine.UI.Image   (就是上面那个 m_FadeImage)
    m_Color = (1.0, 1.0, 1.0, 1.0)     ← 纯白，完全不透明
```

结论：

1. 淡入淡出遮罩面板**天生就是一块不透明白布**（`m_Color` alpha = 1）。
2. `m_FadeInOnStart` 与 `m_FadeInOnSceneLoad` **都是 false** → `Start()` 与 `SceneWasLoaded()` 都不会自动
   `FadeIn()`；`Awake()` 反过来还把 `m_FadeImage.enabled = true`。
3. 于是面板只能靠**外部显式调用** `FadeIn()` 才会从白色渐变到透明（游戏里只有 `LogoScene` 的
   `state == Fade` 分支会调）。这条链一旦断（`Manager.Scene.VRCameraFade_` 为 null、或 `m_IsFading`
   卡在 true），屏幕就永久纯白，而游戏逻辑、音频照常运行 —— 正是"有声音但白屏"。
4. `pid 4366` 那一份的 `m_FadeImage` 是 **null**，`Awake()` 会即时 `Instantiate("Prefabs/Fader")`
   并 `GetChild(0)` 取面板，说明这条路径本身还带一份运行时生成的面板 —— 老看门狗只认
   `Manager.Scene.Instance.VRCameraFade_` 时必然会漏掉它。

### 修法

| 位置 | 做法 |
|------|------|
| `VRCameraFade.Awake` **Postfix** | 直接把 `m_FadeImage.color` 的 alpha 清零（rgb 保留）。显式 `FadeIn/FadeOut` 仍然从白色开始、正常渐变；只有"没人来擦"的那种状态被消除。 |
| 看门狗 | v1.4.0 的多实例 + 孤儿面板兜底继续保留，作为二道保险。 |
| 取证 | `vrk_frame_*.png` + 渲染状态 dump 继续保留，万一下次还有问题，图和状态直接指路。 |

配置开关：`[Fade] ClearPanelOnAwake = true`（关掉即回到原版行为）。

### 静态验证

v1.4.1 编译后 `_verify` **106 项全 PASS**（在 v1.4.0 的 100 项基础上加：`PatchFadeAwake` 存在、
`[HarmonyPatch(typeof(VRCameraFade), "Awake")]` 目标解析到 `VRKanojo.Utils.VRCameraFade::Awake`、
Postfix 内调用 `Image.color` 写零、读取 `IsFading` 写证据、`ClearPanelOnAwake` 已 Bind）。

## v1.5.0 — 修掉“纯色界面 + 有声音”

### 现象
1.4.1 之后白屏没了，但菜单阶段仍然：**先卡一段时间未响应，然后进到一个纯色（渐变）界面，背景有游戏声音**。

### 定位（日志 + 截帧双证据）
17:42 那次运行的 `BepInEx/LogOutput.log`：

```
[FlatFPS]  cam[0] 'CameraRig/VRKanojo_OVRCameraRig/TrackingSpace/CenterEyeAnchor'
            clear=Color bg=RGBA(0.000, 0.548, 0.772, 0.020)
            pos=(0.22, -0.07, -1.48)  euler=(357.3, 351.7, 0.0)
[FlatFPS]  canvas['Title Menu Canvas']  mode=WorldSpace  worldCamera=CenterEyeAnchor
[FlatFPS]  canvas['Title Image Canvas'] mode=WorldSpace  worldCamera=null
```

插件自己存下来的 `vrk_frame_Title_1.png`（2560x1440）：**整屏只有相机清屏色/背景渐变，Title 的 UI 一个都不在画面里**。

`pos.z = -1.48` 不是随机数 —— `IllusionCamera` 的默认 `distance = 1.5f`，而它的 `LateUpdate()`
（Assembly-CSharp 第 20643 行）末尾就是 `transform.position = CalcPos();`，即
“把相机放在焦点前方 distance 米”。免 VR 模式下 `GlobalVRKanojo.ForceMouse_ = true`，
游戏自己（`Manager.Scene.InitCameraRig`）和本插件都会给相机挂 `IllusionCamera`，
于是**第三人称相机控制器在菜单场景里把整台相机搬到了玩家原点前 1.5 米、并带上鼠标产生的偏航**。
Title/Logo 的 VR 菜单是绕玩家原点排布的 WorldSpace Canvas，相机一挪就整片出画，
剩下的只有背景色 —— 这就是“纯色界面”，而声音/逻辑完全正常。

### 修法
| # | 改动 | 说明 |
|---|------|------|
| 1 | `PatchCameraLateUpdate` | `[HarmonyPatch(typeof(IllusionCamera), "LateUpdate")]` Prefix 返回 `IsGameScene(scene)`，**菜单/加载场景里所有 IllusionCamera 实例（含游戏自己挂的两份）都不再移动相机** |
| 2 | `IsGameScene()` / `GameScenes` | 只有 21 个正片场景算游戏场景（Interactive、DressRoom、TrueLesson、Route、InteractiveRoute、VR_Houshi、VR_Sonyu、kiss、aibu、3rd、MainTutorial、Ending、beforeAibu、beforeHoushi、beforeSonyu、beforeKiss、PhotoRoom、CharaViewer、VK_Adv、VK_Adv_Bathroom、BathRoom）。其余（Init/Init2/CheckHMD/SetHMD/Logo2/Logo/Attention/Title/Exit/cloud）一律当菜单 |
| 3 | `Refresh()` | 菜单场景 `ill.enabled = false`，游戏场景 `= true`；菜单场景也不再新增 IllusionCamera |
| 4 | `LateUpdate()` 姿态复位 | 菜单场景里相机 `localPosition` 若偏离原点就复位为 `zero/identity`，并打一条 `menu camera was at (…) -> reset to head origin` |
| 5 | `DumpRenderState()` | canvas 行追加 `pos= dist= front=`（UI 是否在视野前方），下一次就能一眼确认 |
| 6 | long frame 日志 | `[Diagnostics] FrameGapSeconds=1.5`，任何单帧超过该秒数就记一条 `long frame …`，用来精确定位“长时间未响应”到底发生在哪一段 |
| 7 | 版本号修正 | 启动横幅原来是写死的 `v1.1.0`，改为读 `Info.Metadata.Version`，并补打 `clearOnAwake=` / `menuCamFix=` |

### 新配置
```ini
[Camera]
MenuSceneCameraFix = true
GameScenes = Interactive,DressRoom,TrueLesson,Route,InteractiveRoute,VR_Houshi,VR_Sonyu,kiss,aibu,3rd,MainTutorial,Ending,beforeAibu,beforeHoushi,beforeSonyu,beforeKiss,PhotoRoom,CharaViewer,VK_Adv,VK_Adv_Bathroom,BathRoom

[Diagnostics]
FrameGapSeconds = 1.5
```

### 关于“长时间未响应”
17:42 那次运行的时间轴（`t=` 秒）：`Init 2.83 → Init2 3.47 → Logo 3.67 → Logo2 8.44 → Attention 13.33 → Title 22.59`。
到 Title 共 22.6 秒，期间三段间隔 4.8s / 4.9s / 9.3s 全部是 **Unity 5.4 同步加载场景**（`Application.LoadLevel` 阻塞主线程），
Windows 只要 5 秒收不到窗口消息就标“未响应”，所以它会闪一下“未响应”标签，但进程 `Responding=True`、CPU 在跑、音频在播。
同期本插件（含 PreWarm 的 `Shader.WarmupAllShaders()` 721 ms、TexPlus 扫 274 张贴图 15 ms）合计 ≈0.8 秒，
其余 22 秒都是游戏本体与既有 mod 的加载开销。新加的 `long frame` 日志会把每次真实停顿报到百分之一秒，
下次日志里只要出现 `long frame` 就说明另有卡点，可以对症。

### 静态验证
`_verify` 共 **119 项全 PASS**（新增：LateUpdate 补丁目标解析、`IsGameScene` 调用、菜单/游戏场景名单、姿态复位、long frame、canvas 视野字段、版本横幅读 `Info.Metadata.Version`）。

## v1.9.0 — 免VR 直连点击（绕过 EventSystem 整条链）

### 为什么 v1.8.0 的"补 raycaster"没解决问题
`v1.8.0` 给每个世界空间 Canvas 补了 `GraphicRaycaster`、把 `Canvas.worldCamera` 指向相机、并保证 `EventSystem` 上有 `StandaloneInputModule`。
真机日志证明该桥确实执行了：

```
[FlatFPS] flat UI bridge in 'Title': eventSystem=patched graphicRaycaster+=1 ovrRaycaster-off=0
```

但点击依旧无效 —— 说明**点击不是死在"没有 raycaster"，而是死在链上的别处**（本作 `order=32767` 的全屏 `Fader` 面板就是典型嫌疑：它是一张 `alpha=0` 的 `Image`，
`raycastTarget` 默认仍是 `true`，会把光标下方的点击整片吞掉；它的 Canvas 排在所有菜单之上）。

### v1.9.0 的做法：完全不经过 uGUI 事件链
| 配置 | 默认 | 作用 |
|---|---|---|
| `[Input] DirectMenuClick` | `true` | 菜单里按下左键时，直接枚举场景里所有 `Button`，把它的 `RectTransform` 投影到屏幕，判断光标落在哪个矩形里，取 `Canvas.sortingOrder` + 层级最靠上的那个，派发 `pointerDown/Up/Click`（`Button` 实现 `IPointerClickHandler` → 直接触发 `onClick`） |
| `[Input] ClickDebug` | `true` | 每次点击尝试都写一行日志：`click at X,Y scene='Title' buttons=N under-cursor=M -> '<按钮路径>' order=K` / `-> nothing` |

配套修复：
1. **`PatchFadeAwake` 额外把 Fade 面板的 `raycastTarget` 置 `false`**（透明也照样吞点击）。
2. **`ClearRaycastBlockers()`**：进场景时把"非可交互控件 + 覆盖屏幕 60% 以上 + alpha ≤ 0.02"的 `Graphic` 的 `raycastTarget` 全部关掉。
3. **`CollectButtons()`**：`Resources.FindObjectsOfTypeAll` 在 Unity 5.4 对 `DontDestroyOnLoad` / 运行时挂载对象返回不全，
   所以再用每个 `Canvas.GetComponentsInChildren<Button>(true)` 补一遍，按 `InstanceID` 去重。
4. **`LateUpdate` 末尾再压一次 `DoCursor()`**：防止场景脚本自己把指针重新锁回中心（锁了就动不了，也就无从点击）。

### 验证（不启动游戏）
```bash
cd BepInEx/plugins/_src/VRK_FlatFPS/_verify
./bin/Release/net8.0/verify.exe ../../../../../VR_Kanojo_Data/Managed/Assembly-CSharp.dll <VRK_FlatFPS.dll>
# 新增 M 组 18 项：DoDirectMenuClick / InvokeButton / CollectButtons / ClearRaycastBlockers / Fade raycastTarget
# RESULT: ALL CHECKS PASSED (0 failures)
```

### 排查用
跑一次，点一下菜单，然后看 `BepInEx/LogOutput.log` 里的 `[FlatFPS] click at ...` 行：
- `buttons=0` → 一个 Button 都没枚举到（场景结构问题）
- `buttons=N under-cursor=0` → 光标不在任何按钮矩形里（相机/投影问题，或指针被锁在中心）
- `-> '路径'` → 已经命中并派发了 `onClick`，若仍无反应则问题在游戏侧回调

## v1.10.0 — 键位整体搬到主键盘 + 鼠标速度可调

### 1. 小键盘数字 -> 主键盘数字（用 IL 常量表整体重映射）

原版 `IllusionCamera.KeyInput()` 里，视角微调与灵敏度用的是**小键盘**；而且其中两条是
`Input.GetKey(KeyCode.Keypad5)` / `Input.GetKey(KeyCode.KeypadPlus)` 这种**直接读键**，
根本不经过 `KeyInput_Value(...)`，所以 v1.9.x 那种"改写 `KeyInput_Value` 入参"的补丁**永远碰不到它们**。

v1.10.0 改成 `[HarmonyPatch(typeof(IllusionCamera), "KeyInput")]` + `Transpiler`：
单趟扫 IL，把 `ldc.i4` 的 KeyCode 常量按表替换。只改 operand、不改指令条数，因此分支
标签与异常块都不用搬，CFG 完全不动。

| 功能 | 原键 | 新键 | KeyCode |
| --- | --- | --- | --- |
| 前后左右 | 方向键 | **W A S D** | 273/274/275/276 -> 119/115/100/97 |
| 左右转视角 | 小键盘 6 / 4 | **数字 6 / 4** | 262/260 -> 54/52 |
| 俯仰 | 小键盘 8 / 2 | **数字 8 / 2** | 264/258 -> 56/50 |
| 俯仰回正 | 小键盘 5 | **数字 5** | 261 -> 53 |
| 键盘灵敏度 | 小键盘 + / - | **= / -** | 270/269 -> 61/45 |
| 视野 FOV | `]` / `=` | **`]` / `[`** | `=`(61) -> `[`(91)（给灵敏度让位） |

`{ 270, 61 }` 与 `{ 61, 91 }` 同时存在也不冲突：单趟替换，每个常量只被访问一次。

> 已知副作用（游戏本体自带，非本插件引入）：`CameraControl.SetCenterSC()` 绑了 `Alpha6`，
> 在启用该类的场景（ADV / 写真机位）里按 6 会顺手把机位回中。VR_Kanojo 主玩法用不到该类。

### 2. 鼠标速度

`[Camera] MouseSpeed`（Int32，默认 **800**）：`k = MouseSpeed / 500f` 作为 `Mouse X/Y` 轴的乘数。

- 800 -> k=1.6 -> 约 **每 100 像素转 16°**（360° 约 2250 像素）
- 500 -> 10°/100px，1200 -> 24°/100px
- 旧的 `MouseSensitivity=0.12` 实际只有约 1.2°/100px，慢到几乎转不动；该键已从配置移除。

### 3. 帮助面板同步

`数字 6 / 4`、`数字 8 / 2 … 5 回正`、`] / [`、`= / - 键盘灵敏度`、`鼠标速度 配置 MouseSpeed (当前 800)`。

### 4. 静态验证

`_verify` 扩到 **157 项全 PASS**，新增：`PatchKeyInput` 存在、`Transpiler(IEnumerable<CodeInstruction>)`
签名、`Map` 是 `Dictionary<int,int>`、`.cctor` 里 12 组键值齐全、transpiler 内无异常块、
`[HarmonyPatch]` 解析到 `IllusionCamera.KeyInput`、调用点仿真（3 处直接改 + 2 处经局部变量改 +
PageUp/Down、End/Home、`.`/`\` 共 5 处保持原样）。

部署：`VRK_FlatFPS.dll` 49664 B，sha256 `f39836d09139b8a74a0aff4b00f948632abbf971097e1c75c55f65f5cf144458`。

## v1.10.1 — 重置视角键改成 R

`IllusionCamera.KeyInput()` 里的 `Input.GetKey(KeyCode.Slash)`（原 `/`，把 `rotate.z` 与
`keyRotSpeed.z` 归零 = 翻滚回正）在 IL 表里改成 `KeyCode.R`（47 -> 114）。

额外好处：游戏自己本来就把 R 当"VR 回中键" ——
`OVRVRKanojoController.UpdateRecenterPose()`：
`if ((Input.GetKeyDown(KeyCode.R) || VK_Input.RecenterButtonDown) && Singleton<VRCamera>.Instance) VRCamera.VRDeviceReCenter();`
所以按 R 现在是**双保险的重置视角**：翻滚归零 + 游戏原生回中一次。

| 功能 | v1.10.0 | v1.10.1 |
| --- | --- | --- |
| 翻滚回正 / 重置视角 | `/` | **R** |

帮助面板同步为 `R  重置视角 (翻滚归零)`。验证器 **157 项全 PASS**（KeyMap 增至 13 组，覆盖断言同步）。

部署：`VRK_FlatFPS.dll` 49664 B，sha256 `832ebd80f72d4889c246749e09868dd63ce2c55a4a3c0ecff32e36dcc3e32e1a`。


## v1.10.2 — 诊断降噪（默认不落盘、不刷日志）

用户报告里 `LogOutput.log` 226 行中有 106 行是 `[Warning:VRK Flat Mode + FPS Keys]` 的 dump 文本，全部来自诊断路径。改动：

- 14 处 dump 类 `LogWarning` → `LogInfo`（行号 1162/1188/1241/1499/1562/1574/1590/1596/1603/1607/1614/1627/1649/1672）
- `[Input] ClickDebug` 默认 `true` → **`false`**
- `[Diagnostics] SaveFrames` 默认 `true` → **`false`**（原来每个场景结束会 `ReadPixels` 一帧 2560x1440 并落地 4.9 MB PNG）

需要取证时把这两项改回 `true` 即可，行为与 v1.10.1 完全一致。

## v1.10.1

`/`（翻滚回正）→ **`R`**（"重置视角"）。游戏自己本来就绑了 R 做 VR 回中：`OVRVRKanojoController.UpdateRecenterPose()`（反编译第 154194 行）`Input.GetKeyDown(KeyCode.R)` → `Singleton<VRCamera>.Instance.VRDeviceReCenter()`，所以按 R 是双保险。

## v1.10.0 — 键盘改数字键 + 鼠标速度按 DPI 习惯给

- 小键盘 `6/4/8/2/5` → 主键盘 `6/4/8/2/5`；小键盘 `+/-` → `=`/`-`；`]`/`=`(FOV) → `]`/`[`
- 鼠标速度改为 `[Camera] MouseSpeed`（整数，默认 **800**，单位≈DPI 习惯值；换算 `k = MouseSpeed / 500`，即 800 → 16°/100px，旧值 0.12 只有约 1.2°/100px）
- 实现方式：`[HarmonyPatch(typeof(IllusionCamera), "KeyInput")]` + `Transpiler` 单趟替换 IL 常量（13 组映射），**指令条数不变**，分支标签与异常块无需搬移
- 已知副作用：`KeyCode.Alpha6` 被 `CameraControl.SetCenterSC()`（第 100436 行）占用，ADV/写真机位按 6 会顺带回中

## v1.13.0 / v1.13.1 / v1.13.2 — 正片里点不动平板菜单（根因 + 修法）

### 现象
标题界面能点，进入场景（Interactive / 平板 `-SELECT-`）后怎么点都没反应。

### 根因（两条叠加）
1. **我们自己的点击链在正片里根本是关着的**：`DoDirectMenuClick()` 与 `EnsureFlatUi()` 开头都有
   `if (IsGameScene(...)) return;` —— 只有标题/加载场景建立 UI 桥，正片完全不管。
2. **我把游戏自己的点击链也弄死了**：v1.11.0 的 `TickHideGazeRing()` 对
   `OVRGazePointer.instance` 调了 `gameObject.SetActive(false)`。而 `OVRGazePointer` 就是那个
   凝视光环对象本身，它的射线判定（`currentRaycast`）就在这个组件上。无头显时游戏的点击
   判定 = 凝视目标 + `VK_Input.DecisionDown`（左键），目标没了 → 平板永远收不到点击。

### 修法
| 配置 | 默认 | 作用 |
| --- | --- | --- |
| `[Input] ClickInGame` | true | 允许正片场景走我们的直连点击 |
| `[Input] PhysicsClick` | true | 正片用 `Physics.Raycast` 从相机发射线，命中 collider 后取 `Button` 或 `IPointerClickHandler` 派发 |
| `[Input] KeepOvrInput` | true | **不再停用** `OVRInputModule` / `OVRRaycaster`（保留游戏原生链） |
| `[Visual] HideGazeRing` | true | 改为**只关渲染器**（`Renderer.enabled = false`），组件与碰撞体存活，凝视点击继续工作 |
| `[Input] ClickDebug` | false（本次临时置 true） | 每次点击打一行 `[FlatFPS] click …` 说明命中了什么 |

`InvokeButton()` 在没有 `EventSystem` 的正片场景改为直接 `Button.onClick.Invoke()`，
避免 `new PointerEventData(null)` 断言失败。

### 静态验证（`_verify`，共 176 项）
- `DoPhysicsClick` 存在且调用 `Physics::Raycast` / `ScreenPointToRay` / `GetComponentInParent` / `GetEventHandler`
- `ClickInGame` / `PhysicsClick` / `KeepOvrInput` 三个配置键都已 `Config.Bind`
- `InvokeButton` 在无 `EventSystem` 时回退 `onClick.Invoke()`
- `EnsureFlatUi` 读 `KeepOvrInput` ≥2 次（模块与射线器两处门禁）
- **`TickHideGazeRing` 绝不调用 `GameObject.SetActive`**（回归守卫，防止再次打断凝视点击）

## v1.14.0 — 光环"藏一次就完事"的漏洞 + 视角吸附物审计

### 现象
屏幕正中那个百褶状物体**跟着视角动、永远居中**。

### 根因
`TickHideGazeRing()` 里 `_gazeHidden` 是**一次性闩锁**：第一次把渲染器关掉后就永久 return。
但游戏会随场景重建 `OVRGazePointer`（`instance` 变成新对象），新实例的渲染器自然是开着的，
而我们已经不再检查它 → 光环回来了。

### 修法
1. 删掉闩锁，改成 `_gazeTargetId` 记录上次压制的 `GetInstanceID()`；每 0.5s 复核一次：
   - 实例变了（新光环）→ 重新关渲染器并打 `new instance`
   - 实例没变但渲染器被重新打开 → 再关一遍并打 `re-shown by game`
   - 仍然只关 `Renderer.enabled`，**绝不 SetActive**（会断掉游戏凝视点击）
2. 新增 `ScanViewAttached(scene)`：进场景 4s 后把"相机 1.2 米内 **或** 挂在相机层级下"的所有
   Renderer 列出来 —— 路径、是否相机子物体、到相机距离、包围盒尺寸、网格名、是否 SkinnedMesh、
   shader、layer、enabled。这就是回答"跟着视角动的那个东西到底是什么"的直接证据。

日志样例：
```
[FlatFPS] view-attached['Interactive'] 1. 'CameraRig/.../VRKanojo_GazePointerRing' underCam=True dist=0.95 size=2.30x2.30x0.02 mesh='Ring' shader='Unlit/Color' layer=5 en=False
[FlatFPS] view-attached['Interactive'] total = 1 renderer(s) closer than 1.2m or parented to the camera
```

## v1.15.0 / v1.16.0 — 视角切换（第一 / 第三 / 自由）+ 凝视光环还原

### F 键三种视角，循环切换
| 顺位 | 模式 | 实现 |
| --- | --- | --- |
| 1 | 第一人称 | 把 `IllusionCamera.distance` 压到 `FirstPersonDistance`(0.05)，相机落在焦点上；由 `PatchCameraLateUpdate.Prefix` 在游戏 LateUpdate 之前写入，当帧生效 |
| 2 | 第三人称（原版） | 恢复进第一人称前记下的 `distance`（没记下就用 `ThirdPersonDistance` 1.5） |
| 3 | 自由视角 | `Prefix` 直接返回 false，**整个接管**相机：鼠标看、`WASD` 平移、`PageUp/PageDown` 升降、`Shift` 加速；`DoFreeView()` 自己写 `transform.position/rotation` |

切换键与参数都在 `[Camera]`：`ViewToggle`、`ViewToggleKey=F`、`FirstPersonDistance`、`ThirdPersonDistance`、`FreeMoveSpeed=3`、`FreeSprintMultiplier=3`。
自由视角不吃 `DoEyeHeight()` 与菜单相机复位（`LateUpdate` 里提前 return），也不与 `DoFreeLook()` 重复旋转。

### 凝视光环
`[Visual] HideGazeRing` 默认改回 **false**（还原 VR 凝视光环）。光环是游戏自己的凝视游标，
藏掉它会让"瞄准 + 左键"的判定失去目标；现在默认保留，只有显式改成 true 才隐藏（且只关渲染器、
按 `GetInstanceID()` 持续复核，绝不 `SetActive`）。

### 静态验证
- `ApplyView`（静态、反射 `distance`、写字段、读 `_viewMode` 门禁）
- `HandleViewKey`（读 `GetKeyDown` / 配置键名 / 调 `SetDistanceAll`）
- `SetDistanceAll`（`FindObjectsOfTypeAll` 全量下发）
- `DoFreeView`（读鼠标轴、读 WASD、写 `position` / `rotation`）、`FreeMoveSpeed` 与 `FreeSprintMultiplier` 已绑定
- `FlatFpsRuntime.ViewMode` 暴露给 Prefix，`Prefix` 里 `get_ViewMode` 决定是否把相机交给我们
- 面板行含"切视角 第一 / 第三 / 自由"

## v1.17.0 — 真实第一人称（和戴 VR 一致）

之前的"第一人称"只是把 `IllusionCamera.distance` 压到 0.05，本质仍是**环绕相机**（视点绕着角色转），
移动靠的是那套 key 位移。按需求改成真正的三种视角：

| 模式 | 行为 | 谁驱动相机 |
| --- | --- | --- |
| 0 第一人称（默认） | 视点 = 玩家眼睛，鼠标转头，WASD 走动（Shift 加速） | 本插件 `DoFirstPerson()` |
| 1 第三人称 | 游戏原版 `IllusionCamera` 环绕角色 | 游戏 |
| 2 自由视角 | 相机飞出，WASD 平移 + PageUp/PageDown 升降 | 本插件 `DoFreeView()` |

按 `F` 循环切换（`[Camera] ViewToggleKey`，可关）。

实现要点：

- `PatchCameraLateUpdate.Prefix`：`game && ViewMode != 1` 时 **return false**，游戏完全不再写相机。
- `DoFirstPerson()`：`cam.localPosition = Vector3.zero`（眼睛就在 rig 原点），
  `cam.localRotation = Euler(pitch, yaw, 0)`（pitch 夹 ±89°）；
  鼠标增量 `MouseSpeed/500*10`，只在 `FreeLook && !_cursorFree` 时生效；
  WASD 方向取 `Vector3.ProjectOnPlane(cam.forward/right, Vector3.up)` 并归一化 —— **平移的是 `RigRoot()`（人），不是相机**。
- `RigRoot()` 优先 `Manager.Scene.Instance.CameraRig_.transform`（`CameraRig_` 是 `GameObject`，必须 `.transform`）。
- 眼高仍由 `DoEyeHeight()` 写 `OVRCameraRig.trackingSpace.localPosition=(0,h,0)` 提供，
  且我们的姿态写发生在 `LateUpdate`，晚于 `OVRCameraRig.Update()` 里的 `UpdateAnchors()`，不会被覆盖。
- `DoFreeLook()` 只在模式 1 生效，避免和第一人称/自由视角抢旋转。

新增配置：

```ini
[Camera]
FirstPersonMoveSpeed = 2        # 第一人称 WASD 速度 m/s（Shift × FreeSprintMultiplier）
```

验证：`_verify` 全项通过（新增 13 条断言：`EnterMode`/`DoFirstPerson`/`RigRoot` 的行为与依赖）。

### v1.17.1 — 切视角时左上角提示

按 `F` 切换的瞬间，左上角弹一条 2.2 秒的提示（独立于帮助面板是否展开）：

| 模式 | 提示文案 |
| --- | --- |
| 0 | `视角：第一人称  —  鼠标转头, WASD 走动` |
| 1 | `视角：第三人称  —  环绕角色 (游戏原版)` |
| 2 | `视角：自由视角  —  WASD 平移, PageUp/PageDown 升降` |

实现：`HelpOverlay.Toast(text, seconds)` 静态入口写入 `_toastText/_toastUntil`；
`OnGUI()` **先** `DrawToast()` 再判断面板可见性（所以关掉面板也照样提示）；
`DrawToast()` 用 `GUI.DrawTexture` 画半透明底 + `GUI.Label` 写字，字号随分辨率缩放
（`Screen.height/85`，夹 14–34），字体仍走 `Font.CreateDynamicFontFromOSFont`（雅黑优先）。

### v1.17.2 — 第一人称眼高可调 + 防重复叠加

- **PageUp / PageDown 在第一人称下实时调眼高**（5cm 一档，0.30–2.50 m），左上角 toast 显示当前数值。
  第三人称/自由视角里这两个键回到原用途（升/降、PageUp 升降）。
- `DoEyeHeight()` 现在先看 **CameraRig 根节点的 Y**：
  - `|rigY| ≤ 0.35`（游戏用 FloorLevel，把 rig Y 写死 0）→ `trackingSpace.y = 眼高`；
  - `|rigY| > 0.35`（游戏已经把 rig 摆到眼高，EyeLevel 语义）→ `trackingSpace.y = 0`，**不再重复加 1.6 变成 3.2 米**。
- 首次生效时打一行：`no HMD: rigY=… trackingSpace (…) -> (0.00, h, 0.00) => eye at … m`，
  第一人称入场再打一行 `first person engaged: rig=(…) camWorld=(…) rigY=…`，两行就能定位"高度不对"到底错在哪一层。
- `[Camera] EyeHeight`（默认 1.6）仍是**默认**值；运行期用 PageUp/PageDown 调出来的值不写回配置文件（重启回到 1.6）。

### v1.17.3 — 眼高默认改成 1.28 m

用户实测"1.28 左右"最合适（PageUp/PageDown 逐 5cm 试出来的），因此：

- `[Camera] EyeHeight` 默认 **1.6f → 1.28f**（源码与 `BepInEx/config/vrk.flatfps.cfg` 同步改）；
- 运行期覆盖（PageUp/PageDown）仍以它作为起点；
- 验证器加一条断言：插件内必须出现 `ldc.r4 1.28`，防止以后被误改回 1.6。

## v1.18.0 — VR 模式入口（环境变量开关）

游戏根目录现在有两个启动器：

| 入口 | 作用 |
|---|---|
| `启动_VR模式.bat` | 设 `VRKANOJO_VR=1`、把 `VR_Kanojo_Data/Plugins/openvr_api.dll.disabled` 启用回 `openvr_api.dll`，跑同一套 `VR_Kanojo_fix.exe`（SHA-1 崩溃补丁保留）；游戏退出后再把 `openvr_api.dll` 关掉 |
| `启动_免VR模式.bat` | 清掉 `VRKANOJO_VR`、把 `openvr_api.dll` 关掉（消灭 ~100 秒 SteamVR 启动阻塞），跑 `VR_Kanojo_fix.exe` |

`VRKANOJO_VR=1` 时本插件**只保留白屏看门狗与诊断热键**，以下全部不生效：

| 不生效的东西 | 原因 |
|---|---|
| `GlobalVRKanojo.ForceMouse_ = true`、`CheckHMD -> true` | 真 VR 要让游戏自己走头显分支 |
| `EnsureFlatUi` / `DoDirectMenuClick` / `DoPhysicsClick` | VR 用 `OVRRaycaster` 凝视+扳机，鼠标 UI 桥会抢输入 |
| `DoCursor` 锁指针 | VR 里锁鼠标没有意义 |
| `DoEyeHeight` / `DoFirstPerson` / `DoFreeView` / `HandleViewKey` | 相机位姿与旋转归头显，插件插手会和 tracking 打架 |
| `TickHideGazeRing` | 凝视光环就是 VR 准星 |
| `LateUpdate` 整段菜单相机复位 | 会把 rig 拉回原点 |
| 帮助面板自动弹出 | 面板上全是键盘映射，VR 下不成立（F7 仍可手动看） |

启动时会在左上角弹 `VR 模式：免VR 相关功能已关闭，请戴上头显`。
`VRK_Perf` 在 VR 模式下不做任何事（不关 vSync、不改 targetFrameRate）；`VRK_UpScale` 在 VR 模式下不接管渲染。

## v1.19.0 — 还原中心准星（旧插件里那枚）

旧版 `Play Without VR Connected 1.0` 的 `CreateCrosshair()` 用的是它自带的 `crosshair.png`（300×300，橙红圆环 + 上下左右四根刻度线）。v1.19.0 把这枚图原样找回并做成可配置的屏幕中心准星：图已经离线转成 128×128 RGBA 并以**内嵌资源** `VRKanojo.FlatFPS.crosshair.bin` 编进 DLL，运行时不需要任何图片解码器，也不读写磁盘。

| 配置 | 默认 | 说明 |
|---|---|---|
| `[Visual] Crosshair` | `true` | 准星总开关 |
| `[Visual] CrosshairSize` | `56` | 直径（以 1440p 为基准，自动按分辨率缩放）；40 小 / 56 适中 / 80 大 |
| `[Visual] CrosshairAlpha` | `0.85` | 不透明度 |
| `[Visual] CrosshairFirstPersonOnly` | `false` | `true` = 只在第一人称显示；`false` = 三种视角都显示 |

实现要点：
- 用 `GUI.DrawTexture` 在 `OnGUI` 里画（`GUI.depth = -250`，比帮助面板与 toast 都靠前），**不吃鼠标点击**。
- 贴图由 `GetManifestResourceStream("VRKanojo.FlatFPS.crosshair.bin")` 读原始 RGBA 字节 → `SetPixels32` 建 `Texture2D`（按行翻转，Unity 贴图原点在左下）→ `Apply(false, true)`；分辨率变化时自动重建。
- **VR 模式下不画**（头显准星由游戏/运行时提供），`VRKANOJO_VR=1` 时整个准星逻辑短路。
- 凝视光环（`OVRGazePointer` 的圆环）与准星是两回事：`[Visual] HideGazeRing` 现为 `false`，两者会同时出现在屏幕中心；想让准星独占，把 `HideGazeRing` 改成 `true`（正片中光环是鼠标点击的凝视目标，隐藏它会同时影响点击链，v1.13.2 起已确保"只关渲染器、不关对象"）。

### v1.20.0 — 凝视光环常显（关掉静态准星，只留光环）

游戏自己的 `VRKanojo_GazePointerRing`（`VRKanojo.OVRGazePointer`）本来是**条件显隐**的交互反馈：`OVRInputModule` 悬停到可交互物体时 `RequestShow()`，离开后 `RequestHide()` → `visibilityStrength` 衰减到 0 → `Update()` 里 `Hide()`（子物体 `SetActive(false)` + `Renderer.enabled = false`）。所以自由观察时屏幕中心什么都没有，这不是 bug。

v1.20.0 让它**常显**：

| 配置 | 默认 | 说明 |
|---|---|---|
| `[Visual] AlwaysShowGazeRing` | `true` | 常显开关。命中按钮时游戏自己的放大/进度反馈照旧 |
| `[Visual] GazeRingDistance` | `1.5` | 常显时光环离眼睛的距离（米）0.5–4；环按距离等比缩放，**视角尺寸恒定** |
| `[Visual] HideGazeRing` | `false` | 置 `true` 则彻底隐藏光环（常显逻辑也会让路） |
| `[Visual] Crosshair` | `false` | 旧插件的静态准星，现已默认关闭 |

实现要点：
- `[HarmonyPatch(typeof(OVRGazePointer), "Update")]` 的 `Prefix` 在常显模式下返回 `false`：游戏不再每帧 `Hide()`，也不会因 `centerEyeAnchor` 为空而 NRE。
- `FlatFpsRuntime.KeepGazeRingVisible()` 在 `LateUpdate` 里（渲染前最后一刻）把光环摆到 `相机位置 + 前向 × GazeRingDistance`，`localScale = 距离 × depthScaleMultiplier(0.03)`，必要时反射调 `Show()` 拉回可见状态。
- 与 `HideGazeRing` 的关系：`HideGazeRing = true` 优先（隐藏逻辑只关 `Renderer.enabled`，不动 `SetActive`，不影响游戏自己的凝视点击链）。
- 顺带修正：`OVRGazePointer` 实际在 `VRKanojo` 命名空间下，旧的 `GetType("OVRGazePointer")` 恒为 null（功能上被 `HideGazeRing=false` 掩盖），现已改成 `GetType("VRKanojo.OVRGazePointer") ?? GetType("OVRGazePointer")`。

## v1.24.0 — 交互键真正接到"触摸/爱抚"（这是"瞄准女主却没反应"的真因）

### 为什么 v1.23.0 只改 CheckDecision 不够
游戏把输入分成两套，彼此完全独立：

| 用途 | 游戏方法 | 内部实现 |
| --- | --- | --- |
| 菜单/对话"决定" | `Manager.VK_Input.CheckDecision()` | `Input.GetMouseButton(0)` + 手柄 A |
| **触摸/爱抚女主** | `VK_Input.H_ActStart()`、`GazeTouchIn_L()` | 只有 `Input.GetMouseButtonDown(0)` |
| 抓住身体部位 | `BodyTouchManager.CheckGazeTouch()` | 从相机沿视线 `GrabCollider.Raycast(ray, hit, **1.5f**)` |

所以 v1.23.0 让 E 能"决定"，但摸不到人：触摸走的是 `H_ActStart`，抓取距离还被硬编码成 1.5 米 —— 第三人称相机正好退到 1.5 米，等于永远差一点。

### 本版三处补正
| # | 改动 | 实现 |
| --- | --- | --- |
| 1 | 交互键折进触摸输入 | `PatchTouchStart`（`[HarmonyPatch]` + `TargetMethods()`）后置补丁 `H_ActStart` 与 `GazeTouchIn_L`，`__result |= InteractHeld()` |
| 2 | 抓取距离可调 | `PatchGazeGrabDistance` 用 Transpiler 把 `CheckGazeTouch` 里唯一那处 `ldc.r4 1.5` 换成 `ldsfld FlatFpsPlugin::GazeGrabRuntime` |
| 3 | 键位解析不再每帧分配 | `InteractKeyCache.Get()` 缓存 `Enum.Parse` 结果，配置变化才重解析 |

`GazeGrabRuntime` 由 `SyncGazeGrab()` 每帧从 `[Input] GazeGrabDistance`（默认 2.5，1~10）同步；VR 模式下强制回 1.5（保持原版手感）。

### 新增配置
```ini
[Input] InteractKey     = E      ; 交互键(按住=决定/触摸)，空=关闭
[Input] GazeGrabDistance = 2.5   ; 抓取距离，游戏原本硬编码 1.5
```

### 验证（静态，不运行游戏）
- 15 个 Harmony 补丁目标全部在 `Assembly-CSharp.dll` 里按名字+签名解析成功（`typelist` 工具）。
- `CheckGazeTouch` 的 IL 里 `ldc.r4` 常量**只有 1 个**（1.5），Transpiler 替换不会误伤。
- 验证器新增"组 O"18 项：缓存解析、`InteractHeld`、`TargetMethods` 两个目标名、`Postfix(ref bool __result)`、Transpiler 生成 `ldsfld`、`GazeGrabRuntime` 是 `public static float`、`Update` 每帧同步 —— ALL CHECKS PASSED。

## v1.25.0 — 光环"钉在画面中央不动"的真因

### 现象
玩家反馈："居中准星不跟着鼠标移动，一直呆在原地。"

### 机制
`VRKanojo.OVRGazePointer` 只有在 `Update()` 里、并且 `visibilityStrength > 0` 时才更新自己的位置。我们在常显模式下用 `Prefix` 关掉了它的 `Update()`，位置由 `KeepGazeRingVisible()` 每帧计算，逻辑是：

1. 从相机沿视线打 `Physics.RaycastNonAlloc`，最大距离 `GazeRingMaxDistance`；
2. 打到东西 → 光环贴在命中点；
3. **没打到 → 光环摆到「相机前方 1.5 米」**。

第 3 条是坑：房间空处、天花板（2.7 米）、远景都超过旧默认的 2.5 米，于是绝大多数时候走的是第 3 条 —— 光环永远在相机正前方同一点，屏幕上恰好是正中，转视角时它跟着相机一起转，看上去"完全不动"。

### 修法
| # | 改动 | 效果 |
| --- | --- | --- |
| 1 | `GazeRingMaxDistance` 默认 2.5 → **8** | 墙面、地面、人物、家具基本都能命中 |
| 2 | 新配置 `[Visual] GazeRingHideWhenNoHit = true` | 没命中就 `Renderer.enabled = false` 隐藏，不再挂假准星 |
| 3 | `KeepGazeRingVisible()` 从 `LateUpdate` 开头挪到视角旋转之后 | 消掉一帧延迟，转视角时光环贴得跟手 |
| 4 | 命中目标变化时打一行 `gaze ring target -> <名字> dist=…` | 能直接在日志里看到它在跟踪不同物体 |

### 关于"居中准星"
`[Visual] Crosshair` 是 HUD 准星，画在屏幕中心，**按定义就不会移动** —— 移动的是世界。想要"贴在物体上、随视角滑动"的效果请用光环（`AlwaysShowGazeRing = true`），两者建议只开一个。

### v1.25.1 — 取证加强

玩家描述：准星开局在正中，抬头看上方后准星留在原地、脱离视角。

为拿到定论，v1.25.1 增加两处取证（都只在日志里，不改行为）：

| 取证 | 日志样例 | 能回答什么 |
| --- | --- | --- |
| 进场景点名所有准星候选 | `reticles in 'VR_Sonyu':` / `reticle[1] OVRGazePointer '…' active=… enabled=… pos=… scale=…` | 玩家看到的到底是 `OVRGazePointer` 光环、还是游戏的 `Reticle`、还是 `OVRProgressIndicator` |
| 光环逐秒快照（`GazeRingLog = true`） | `gaze ring: mode=Aim pos=(…) cam=(…) fwd=(…) ringNow=(…) scale=… parent='…'` | 我们写入的世界坐标是否真的进了 transform（`ringNow`），以及父节点是谁 |
| 命中目标变更 | `gaze ring target -> <名字> dist=… pos=…` | 是否真的贴在物体表面 |

## v1.26.0 — E 键"实战里不生效"的真因：整条凝视触摸链是关着的

### 代码事实（`Assembly-CSharp`）
    // BodyTouchManager.Update_GazeCaress()
    if (enableGaze && Manager.Config.IntputData.controlType == 0) {   // ← 两个前置条件
        if (!useHand[0] && VK_Input.GazeTouchIn_L()) CheckGazeTouch(Hand.HANDEDNESS.L);
    }

    // BodyTouchManager.CheckGazeTouch()
    if (Manager.Config.IntputData.controlType != 0) return;           // ← 又拦一次

- `controlType`：`0` = 键鼠（非手柄）模式，`1` = VR 手柄模式（设置界面 `VRControllerSwitch_` 写的就是它）。
- `enableGaze`：`BodyTouchManager` 上的 `[SerializeField] private bool`，由场景/prefab 决定，**默认关**。
- 本机 `UserData/Config/Config.xml` 里是 **`<controlType>1</controlType>`** → `Update_GazeCaress` 整段被跳过、`CheckGazeTouch` 第一行就 `return`。v1.24.0 虽然把 E 接进了 `GazeTouchIn_L()`，但这条链根本没被调用，所以"实战中不行"。

### 修法
| # | 改动 | 说明 |
| --- | --- | --- |
| 1 | `[Input] ForceNonController = true` | 每次进场景把 `Manager.Config.IntputData.controlType` 写成 **0**，日志 `controlType 1 -> 0 (non-controller mode: gaze touch enabled)` |
| 2 | `[Input] ForceEnableGaze = true` | 每次进场景把场上所有 `BodyTouchManager.enableGaze` 置 **true**，日志 `enableGaze -> true on N BodyTouchManager(s)` |
| 3 | 按 E 时打诊断 `HandleInteractProbe()` | `interact key E -> scene='…' controlType=0 enableGazeManagers=1/1 gazeHit=<物体名或 none>` |

配合 v1.24.0 已有的 `GazeTouchIn_L` / `H_ActStart` 后置补丁（E 按下即视为触摸开始），E 就能真正触发"需要用手碰"的交互。

## v1.27.0 — 画面转了但"游戏以为你还朝原方向"（ESC 菜单不对着你）

### 现象
转视角后世界正常旋转，但按 ESC 出来的菜单仍摆在原方向（背后/侧面），不会正对当前视线。

### 原因
v1.17.0 的第一人称把左右转向写在**相机局部旋转**上：

    cam.localRotation = Quaternion.Euler(pitch, yaw, 0f);   // 只有渲染用的相机转了

而游戏的"玩家朝向"是 `Manager.Scene.Instance.CameraRig_`（`CameraRig` 根节点）——所有按玩家位置摆放的东西（ESC 菜单、`-SELECT-` 面板、`VRKanojo_Canvas`、凝视光环的世界锚点）都按 **rig 的 forward** 计算。rig 没转，所以游戏认为你还在原位面朝原方向。

### 修法
| 项 | 改动 |
| --- | --- |
| `[Camera] FirstPersonRotateRig = true` | 第一人称下把 **yaw 写到 rig 根节点**：`rig.localRotation = Euler(0, yaw, 0)`；相机只保留 **pitch**：`cam.localRotation = Euler(pitch, 0, 0)` |
| `EnterMode(0)` | 进入第一人称时从 `RigRoot().eulerAngles.y` 取初值，避免方向瞬跳 |
| 兜底 | 找不到 rig 时退回"旋转全放相机上"的老做法 |

把 yaw 交给 rig 后，菜单/UI 的生成朝向与视线一致；pitch 留在相机上，所以低头抬头时菜单不会被掰歪（符合 VR 里"菜单只跟随水平朝向"的直觉）。

日志（首次进入第一人称打印一次）：

    [FlatFPS] first person: yaw applied to rig 'CameraRig/VRKanojo_OVRCameraRig' (game menus follow the view), pitch stays on the camera

## v1.27.1 — 顺手抓到的 bug（回归守卫已加进验证器组 S）

| # | Bug | 后果 | 修法 |
| --- | --- | --- | --- |
| 1 | `PatchCameraLateUpdate.Prefix` 没有先判 `VrMode` | **VR 入口下**第一人称逻辑虽被关掉，但 Prefix 仍 `return false` → 游戏自己的 `IllusionCamera.LateUpdate` 被停用，头显位姿没人驱动 | Prefix 第一行 `if (FlatFpsPlugin.VrMode) return true;` |
| 2 | `ApplyTouchBridge` 没判 `VrMode` | VR 入口下会把 `controlType` 改成 0（键鼠），双手手柄交互直接失效 | 方法首行 `if (VrMode) return;` |
| 3 | `TickHideGazeRing` 里写 `asm.GetType("OVRGazePointer")` | 真名是 `VRKanojo.OVRGazePointer`，永远取到 null → 该功能从未生效（`HideGazeRing` 默认 false 所以没人发现） | 补命名空间，并去掉同一行重复的 `??` |

另修 **VRK_PreWarm v1.1.1**：VR 入口下直接跳过预热（`VrMode` 时 `yield break`）—— 预热是主线程抢帧的，头显里就是可见顿挫。

### 关于"渲染相机 与 游戏朝向 保持一致"
- 第一人称：`yaw` 只写在 `CameraRig` 根节点，`pitch` 只写在相机 → **水平朝向同源**，游戏按 rig 摆的 UI/菜单与渲染视角永远一致。
- 第三人称：相机由游戏 `IllusionCamera` 环绕，rig 朝向即玩家朝向，二者同样一致。
- 自由视角：相机是飞行机位，**刻意脱离**玩家朝向（否则就不能叫自由视角）；切回第一/第三人称时按 `EnterMode` 重新对齐。

## v1.28.0 — VR 入口下"全部自建功能不启用"的完整清单

环境变量 `VRKANOJO_VR=1`（由 `启动_VR模式.bat` 设置）时：

| 插件 | VR 下的行为 |
| --- | --- |
| `VRK_FlatFPS` 1.28.0 | 不强制免VR、不放行 `CheckHMD`、不做菜单相机复位、不接管相机（`PatchCameraLateUpdate` 直接放行）、不做第一/第三人称与自由视角、不改眼高、不驱动准星/光环、不锁指针、不做直连/物理点击、不改 `controlType`/`enableGaze`；**`OnGUI()`、`HandleHotkey()`、`KeepGazeRingVisible()` 全部首行 `VrMode` 早退** —— 帮助面板、toast、准星、交互方框、F7 都不出现；只在启动时弹一句"VR 模式：免VR 相关功能已关闭，请戴上头显" |
| `VRK_Perf` 1.1.0 | `ApplyAll()` 早退：不动 vSync / targetFrameRate（VR 由 SteamVR 管节奏） |
| `VRK_PreWarm` 1.1.1 | `Warm()` 协程 `yield break`：不预热，避免主线程抢帧顿挫 |
| `VRK_TexPlus` 1.3.0 | `Awake()` 直接返回：不改 aniso / 过滤 / mipBias，日志 `VRKANOJO_VR=1 -> texture tweaks disabled in VR mode` |
| `VRK_UpScale` 1.4.0 | `CheckGate()` 判定后早退：不做超分（VR 该用头显驱动的重投影） |

即：VR 入口下只剩游戏本体 + 各插件的"零改动"状态，唯一保留的是 `VR_Kanojo_fix.exe` 对 SHA-1 寄存器的修复（那是防崩溃的，必须保留）。

## v1.28.1 — VR 入口：一个补丁都不打（按键与 UI 100% 原生）

v1.28.0 是把运行期行为都 `VrMode` 早退，但 **Harmony 补丁仍然被装上**（键位重映射、`CheckHMD` 放行、`LogoScene.Start`、`VRCameraFade.Awake`、`IllusionCamera.LateUpdate`、点击/触摸）。只要某个场景里存在 `IllusionCamera` 实例，按键重映射就会生效 —— 不符合"回归原生"。

v1.28.1 改成在 `Awake()` 里：

    if (FlatFpsPlugin.VrMode) Log.LogInfo("[FlatFPS] VR mode: no Harmony patches applied (game logic fully native).");
    else { new Harmony(Guid).PatchAll(...); }

于是 VR 入口下：

| 项目 | 状态 |
| --- | --- |
| 键位重映射（数字键 / WASD / R） | 不打补丁 → 原生 |
| `InitScene2.CheckHMD` 放行 | 不打补丁 → 原生（走真 VR 判定） |
| `LogoScene.Start` / `VRCameraFade.Awake` 修复 | 不打补丁 → 原生淡入淡出 |
| `IllusionCamera` 相机接管 | 不打补丁 → 原生 |
| 直连点击 / 物理点击 / E 交互 | 不打补丁 → 原生 |
| 准星、帮助面板、toast、交互方框 | `OnGUI()` 首行 `VrMode` 早退 → 不画 |
| 光环（`KeepGazeRingVisible` / `TickHideGazeRing`） | 早退 → 不碰 |

`VRK_Perf` / `VRK_PreWarm` / `VRK_TexPlus` / `VRK_UpScale` 同样早退（见上面 v1.28.0 表）。

## 启动器：自动检测 SteamVR，避免 ~100 秒假死

`启动_VR模式.bat` 现在四步：

1. `tasklist /FI "IMAGENAME eq vrserver.exe"` 判断 SteamVR 是否在跑；
2. 不在跑 → `start "" "steam://rungameid/250820"` 拉起 SteamVR，最多等 **90 秒**（每 3 秒轮询一次 `vrserver.exe`）；
3. 90 秒还没起来 → 打印说明并 `exit /b 2`，**不启动游戏**（这正是那 100 秒假死的成因：Unity 的 OpenVR 后端在 `BepInEx` 之前同步等 `vrclient_x64.dll`）；
4. 起来了才启用 `openvr_api.dll` → 跑 `VR_Kanojo_fix.exe` → 退出后自动把 DLL 改回 `.disabled`。

`启动_免VR模式.bat` 三步：先把 `openvr_api.dll` 改名成 `.disabled`（不开 VR 就永远不碰 SteamVR）→ 跑 fix.exe → 结束。

两条路径都带 `VRK_DRY=1` 干跑开关（只看检测结果、不拉起 SteamVR、不启动游戏），实测：

    [1/4] checking SteamVR (vrserver.exe) ...
    [DRY] SteamVR is not running - would start it via steam://rungameid/250820 and wait 90s.

    [1/4] checking SteamVR (vrserver.exe) ...
          SteamVR is running.
    [2/4] enabling OpenVR plugin ...
    [DRY] would launch VR_Kanojo_fix.exe now - stopping here.

## v1.30.0 — 鼠标当手 + 输入法隔离

### 1. 左键当右手（`[Input] MouseHandGrab`，默认 true）

问题：一部分互动（抓取类、H 场景里"需要 VR 手"的姿态）只接 `Hand` 的
`Grabbable.GrabBegin(hand, collider)`，鼠标左键走的是 `VK_Input.H_ActStart`
（`Input.GetMouseButtonDown(0)`），到不了那条链路。

做法（`FlatFpsRuntime.HandleMouseHand()`）：

| 步骤 | 行为 |
| --- | --- |
| 左键按下 | `Physics.Raycast`（相机 → `MousePixel()`，5 米） |
| 命中祖先含 `BodyCaressGrabbable` | **跳出**，交给游戏自己的凝视爱抚路径 |
| 命中祖先含 `Grabbable` | 反射取 `Hand` 实例，调 `GrabBegin(hand, collider)` |
| 同时 | 反射置私有 `Hand.GrabFlag = true`（让 `PoseProc` 走鼠标分支） |
| 松开左键 | `hand.ForceRelease(grabbable)` + `GrabFlag = false` |

日志：`mouse-hand grab -> <类名>@<碰撞体名> (hand=Hand)`、
`Hand.GrabFlag -> true (mouse pose path enabled)`、失败时 `mouse-hand GrabBegin failed: …`。

### 2. 输入法不再吃键（`[Input] DisableIme`，默认 true）

问题：中文输入法激活时，按键先被输入法吞掉，Unity 的 `Input.GetKey` 收不到，
于是 WASD 必须先切换到英文键盘才灵。

做法（`FlatFpsRuntime.EnsureImeOff()`，每 2 秒一次）：

```csharp
Input.imeCompositionMode = IMECompositionMode.Off;     // Unity 侧开关
ImmAssociateContext(hwnd, IntPtr.Zero);                // 摘掉窗口 IME 上下文
```

`hwnd` 用 `FindWindowW(null, Application.productName)`，失败退 `GetActiveWindow()`。
首次成功打 `IME detached (hwnd=…, prev=…) - keyboard always reaches the game`。

### 3. 静态验证（组 W，15 项）

`Awake` 里两个 Bind、两个静态字段、`HandleMouseHand`/`SetGrabFlag`/`FindGrabbable`/
`EnsureImeOff` 存在、`Physics.Raycast`、`GrabBegin`/`ForceRelease` 字面量、
`GrabFlag` 私有字段名、`ImmAssociateContext`、2 秒节流、3 个 P/Invoke 声明、
`Update` 每帧驱动 —— 全部 PASS。

## v1.31.0 — 左键=手（瞬移式）+ 输入法彻底摘除

### 1. 左键当 VR 手：从"反射调 GrabBegin"改为"把手搬过去"
上一版只反射调用 `Grabbable.GrabBegin(hand, collider)`，对 `VRLookIcon/HandPad` 这类
**靠手部碰撞体 `OnTriggerEnter` 触发**的物体完全无效（日志里就是
`physics click hit 'VRLookIcon/HandPad' but no Button / IPointerClickHandler on it`）。

现在的做法对齐 VR 的真实机制：

| 步骤 | 动作 |
| --- | --- |
| 按下左键 | 从准星射线（默认 5 m，`MouseHandReach`）找命中点，记下 `_handHome` |
| 按住期间 | **每帧**把游戏里那只 `Hand` 的 `transform.position` 压到命中点（游戏自己每帧写手，所以要每帧压）|
| 同时 | 若命中物是 `Grabbable`，仍反射 `GrabBegin(hand, collider)` + 私有 `GrabFlag=true` 叠加 |
| 松开 | `ForceRelease` + 手复位到 `_handHome` |

效果：手部碰撞体真的压到物体上，`HandPad`、门把手、可抓取物、身体触碰等
所有靠物理/触发器判定的交互一套通吃。

配置（`[Input]`）：

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `MouseHandGrab` | true | 总开关（关闭即完全不碰手）|
| `MouseHandTeleport` | true | 瞬移式（推荐）；false = 只反射 `GrabBegin`（旧行为）|
| `MouseHandReach` | 5 | 准星射线长度（米）|

日志：`mouse-hand aim -> '<碰撞体名>' at (x, y, z) (grabbable / physics only)`

### 2. 输入法：三层摘除 + 0.25 s 自愈
之前是 2 s 才重挂一次 `ImmAssociateContext(NULL)`，中文输入法有窗口抢回来就能吃掉
一次 WASD。现在：

1. `ImmDisableIME(-1)` —— 当前线程级禁用 IME（每会话一次）；
2. `ImmAssociateContext(hwnd, IntPtr.Zero)` —— 窗口级解绑，**4 Hz 重挂**（窗口重建、
   切焦点、换场景后 0.25 s 内自动补回）；
3. `Input.imeCompositionMode = IMECompositionMode.Off` —— **每帧**压一次 Unity 自己的开关。

窗口句柄优先用 `FindWindowW("UnityWndClass", null)`（Unity 固定窗口类名），
拿不到才退回 `Application.productName` / `GetActiveWindow`。

配置：`[Input] DisableIme = true`

> 说明：这样中文输入法拿不到键盘焦点，WASD 不会再被吞；副作用是游戏内无法输入中文
> （本作没有中文输入需求）。

### 3. 原生超分保持关闭
`vrk.upscale.cfg` 的 `[General] NativePass = false`。此前该键在 `[General]` 与
`[Safety]` 各有一份、值相反，BepInEx 读到的是 `true`（等于没关），现已去重成一份。
要重新开启，需先把原生层改造成"永不销毁在用的渲染目标"再验证。

## v1.32.0 — 左键改走游戏自己的凝视交互（蓝光部位终于能碰）

### 为什么 v1.31.0 的"瞬移手"对蓝光部位仍然无效
日志实证（Interactive 场景点女主）：

```
physics click hit '.../cf_J_Mune01_L/VKHit_Mune01_L' but no Button / IPointerClickHandler on it
mouse-hand aim -> 'VKHit_Mune01_L' at (1.97, 0.68, 0.05) (physics only)
```

`(physics only)` 的意思是：从命中碰撞体往上找不到 `Grabbable`。原因是女主身上的
可交互部位用的是 `BodyCaressGrabbable`（`Grabbable` 的子类），而它的
`GrabCollider` 是**自己身上的碰撞体**（`GrabCollider = GetComponent<Collider>()`），
并不是我们射线打到的那些 `VKHit_*` 命中体 —— 所以"把手搬过去压碰撞体"这条路
对爱抚部位永远压不到东西。

### 现在怎么做
游戏内部本来就有完整实现：

```csharp
// BodyTouchManager
private void CheckGazeTouch(Hand.HANDEDNESS handedness)
{
    if (Manager.Config.IntputData.controlType != 0) return;
    Ray ray = new Ray(camTrans.position, camTrans.rotation * Vector3.forward);
    float num = 1.5f;                                  // ← v1.24.0 已换成 GazeGrabDistance(2.5)
    foreach (var g in bodyCaress)
        if (!g.IsGrabbed && g.GrabCollider.Raycast(ray, out var h, num) && h.distance <= num2)
            bodyCaressGrabbable = g;                   // 取最近
    if (bodyCaressGrabbable != null) bodyCaressGrabbable.GrabBegin(handedness);
}
```

v1.32.0 直接在左键按下时反射调 `BodyTouchManager.CheckGazeTouch(R)`：

| 时机 | 动作 |
| --- | --- |
| 左键按下 | `CheckGazeTouch(Hand.HANDEDNESS.R)` —— 从相机打射线，命中最近的爱抚部位并 `GrabBegin(R)` |
| 判定成功 | 读私有 `useHand[1] != null` 确认真的抓住了；抓住了就**不再瞬移手**（免得和爱抚动画打架）|
| 左键松开 | `BodyTouchManager.HandForceRelease(R)` 干净脱手 |
| 打不到部位 | 回退到 v1.31.0 的瞬移手 / 反射 `GrabBegin`，场景道具（门、平板、可抓物）照旧 |

配置：`[Input] MouseBodyCaress = true`（关闭即回到 v1.31.0 行为）

日志：`gaze touch -> grabbed a body part (right hand)` / `gaze touch released`

## v1.33.0 — 触发点与准星对齐（改用自己的射线）

### 偏移从哪来
v1.32.0 是把判定交给游戏自己的 `CheckGazeTouch`，而它用的射线不是我们瞄准的那条：

```csharp
// BodyTouchManager（反编译第 3706 行）
camTrans = Singleton<Manager.Scene>.Instance.MainCamera_
    .gameObject.GetComponentInChildren<VREyeRaycaster>().M_Camera;
...
Ray ray = new Ray(camTrans.position, camTrans.rotation * Vector3.forward);
```

`camTrans` 是 `MainCamera_` 下面 `VREyeRaycaster` 序列化进来的那个 Transform，
和我们用来渲染、用来画准星、用来做鼠标点击的相机不是同一个节点 —— 位置与朝向都可能
差一点（第一人称的 pitch 写在一个节点上，yaw 写在 rig 上），于是"打中的地方"和
"准星指的地方"就分叉了。

### 现在的做法
`TryGazeTouch()` 完全自己判定，射线改为**视口正中心**：

```csharp
Ray aim = _cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
```

然后照抄游戏的挑选规则（只是换了射线源）：

1. 遍历私有数组 `BodyTouchManager.bodyCaress`；
2. 跳过 `IsGrabbed == true` 的；
3. 对每个部件的 `GrabCollider.Raycast(aim, out hit, GazeGrabDistance)` 取**距离最近**的；
4. 命中就 `GrabBegin(Hand.HANDEDNESS.R)`，再用私有 `useHand[1] != null` 确认真的抓住。

判定与准星共用同一条射线，所以触发点必然落在准星上。

### 对照日志（`[Diagnostics] GazeAimLog = true`）
每次点击会打印一行，把两条射线的结果并排列出来：

```
gaze touch -> grabbed 'cf_J_Mune01_R' d=1.24 (from screen centre; game ray=cf_J_Mune01_R@1.31 camFwdErr=3.4deg camPos=(1.81, 0.68, 0.21))
gaze aim: crosshair=none game=none reach=2.5
```

`camFwdErr` 就是游戏那条射线与准星朝向的夹角 —— 偏移有多大，一眼能看出来。

> 如果仍然觉得差一点：先把 `vrk.upscale.cfg` 的 `[General] Mode` 从 `RenderScale`
> 改成 `Sharpen`（或把 `Scale` 设成 1），排除"0.85 渲染再放大"带来的视觉偏差。

## v1.34.0 — 适配 RenderScale（0.85 渲染）等离屏渲染

`Mode = RenderScale` 时场景不是直接画到屏幕，而是先画进 `2176x1224` 的离屏目标再由
presenter 放大到 `2560x1440`。这带来一个隐蔽的坐标问题：**Unity 的
`Camera.ScreenPointToRay` / `GraphicRaycaster` 在相机渲染到离屏目标时，期望的是
"目标像素"坐标，而不是屏幕像素坐标。**

### 分开处理两个空间
| 用途 | 坐标 |
| --- | --- |
| `Camera.ScreenPointToRay`（物理点击、射线）| **Render**：`Input.mousePosition × target/screen` |
| `ScreenSpaceOverlay` 画布 | **Screen**：原始屏幕像素 |
| 其它画布（WorldSpace / ScreenSpaceCamera）| **Render** |

```csharp
internal static Vector2 MouseScreen() { return Input.mousePosition; }

internal static Vector2 UiPoint(Canvas canvas)   // Auto: 非 Overlay 一律用 Render 空间
```

`DoDirectMenuClick` 现在先取按钮所在 `Canvas`，再按该画布的空间取点做
`RectangleContainsScreenPoint`；`InvokeButton` 的 `PointerEventData.position`
同样按目标画布的空间填。以前一律用 Render 空间，Overlay 画布就会偏 15%。

配置：`[Input] UiPointerSpace = Auto`（可选 `Screen` / `Render` 强制指定）

### 每次进场景打一行渲染探针
```
[FlatFPS] render probe['Interactive']: screen=2560x1440 camTarget=2176x1224 mouse=(2176, 1224) uiPointer=Auto(=>render)
```
`camTarget=backbuffer` 表示没开 RenderScale；两者不一致时，这一行就能看出换算有没有生效。

### UpScale v1.7.0
presenter 相机每帧强制 `rect = (0,0,1,1)`、`aspect = dw/dh`，避免被拉伸或留黑边——
一旦画面被拉伸，"相机正中心"和"屏幕正中心"就不再重合，准星、鼠标、凝视交互会整体偏。

## v1.35.0 — 手部震动除零：左键交互真正的拦路虎

日志里一次会话出现 **330 次** `DivideByZeroException`：

```
OVRHapticsClip..ctor (System.Byte[] samples, Int32 samplesCount)
VRKanojoTouch.TrackedController.Vibration (System.Byte[] clip)
VRKanojoTouch.Hand.OnTriggerStay (UnityEngine.Collider otherCollider)
```

### 机理
`Hand.OnTriggerStay()` 碰到 `TouchCollider` 层就做两件事：

```csharp
m_proceduralClip.Add(30);
controller.Vibration(m_proceduralClip.ToArray());   // ← 先炸在这里
if (Random.Range(0,3) == 1 && ...) InteractiveProc(...);   // ← 永远没机会执行
```

`OVRHapticsClip(byte[] samples, int samplesCount)` 里

```csharp
Capacity = Samples.Length / OVRHaptics.Config.SampleSizeInBytes;
```

免VR 没有头显，`OVRHaptics.Config.SampleSizeInBytes` 从未被 `Initialize()` 赋值，仍是 **0** →
除零异常 → **`OnTriggerStay` 在 `InteractiveProc` 之前就中断**，所以手贴上去毫无反应。

### 修法
```csharp
[HarmonyPatch(typeof(VRKanojoTouch.TrackedController), "Vibration")]
static bool Prefix(byte[] clip) {
    if (OVRHaptics.Config.SampleSizeInBytes > 0) return true;   // 真 VR 照常震动
    return false;                                               // 免VR: 跳过震动, 逻辑继续
}
```

外加 `Hand.OnTriggerStay` 的 `Finalizer` 兜底，保证任何异常都不会再打断触摸逻辑。

### 同时做的降噪（364 KB 日志 / 3800 行 → 预期个位数百分比）
| 之前 | 现在 |
| --- | --- |
| `physics click hit 'OVRInspector'…` ×188 | 仅 `ClickDebug=true` 时记录，且跳过 OVRInspector |
| `mouse-hand aim -> …(physics only)` 每帧 | 仅 `[Input] MouseHandLog=true` 时记录 |
| `interaction state: …` 每次点击 | `InteractionLog` 默认改回 false |
| `AccessTools.Field … IntputData` 警告 ×9 | 改用 `GetField`（`IntputData` 是静态**属性**） |

### 探针修正
以前 `render probe … uiPointer=Auto(=>screen)` 恒为 screen（因为我传了 `null` 画布）。现在统计场景内
画布类型再判定：

```
[FlatFPS] render probe['Interactive']: screen=2560x1440 camTarget=2176x1224 mouse=(1088, 611) uiPointer=Auto(=>render) canvases=overlay0/world5/cam0
```

### 日志里同时确认的事
- `IME detached (prev context=2035741)` 只出现 **1 次** → 输入法拦截生效，不再反复重挂
- 插件版本全部就位：FlatFPS **1.35.0** / UpScale **1.7.0** / TexPlus 1.3.0 / PreWarm 1.1.1 / Perf 1.1.0
- `camTarget=2176x1224` 恒成立 → 0.85 渲染确实在生效

## v1.36.0 / v1.36.1 — 准星"对上了却没反应"的两处根因

### 1. 射线把 trigger 当空气（v1.36.1）
凝视环与左键射线原来都是：

```csharp
Physics.Raycast(..., ~0, QueryTriggerInteraction.Ignore);
```

而游戏里 `TouchCollider` 层、`VKHit_*`、以及地上的交互图标（`VRLookIcon` 系列）**大量是 trigger 碰撞体**。
`Ignore` 会把它们全部穿过，于是：
- 准星明明压在图标上，实际命中的是它背后的 `R_Floor`（日志里 `gaze ring target -> R_Floor` 出现 58 次）
- 左键点下去命中的也是地板，`InteractiveProc("R_Floor")` 自然没有反应

两处都改成 `QueryTriggerInteraction.Collide`。

### 2. uGUI 走不通时的兜底（v1.36.0）
`Hand.OnTriggerStay` 对 `TouchCollider` / `Map` 两种 layer 做的都是同一件事：

```csharp
Singleton<InteractiveCommandController>.Instance
    .InteractiveProc(collider.name, shake, tickle, grip);
```

所以左键命中后若没有 `Button` / `IPointerClickHandler`，就直接按**命中碰撞体的名字**调用这个方法
（`[Input] ClickProc = true`，默认开），等价于 VR 手贴上去。

### 3. "首次命中"日志
每个物体只完整记录一次（上限 60 个，零重复开销）：

```
[FlatFPS] first hit 'VRLookIcon/xxx' (InteractiveProc) collider='xxx' layer=19 comps=[Transform SphereCollider VRInteractiveItem ...]
```

用来确认地上那两个 R / O 图标到底是什么对象、挂了哪些组件。

## v1.37.0 — 那两个图标到底是什么，以及"盯着看就该生效"

日志把它们点名了：

```
[FlatFPS] first hit 'VRLookIcon/lookSelect005_01/TimeIconMain/TimeIcon' (no handler)
          collider='TimeIcon' layer=19
          comps=[Transform Animator TimeIconControlChild BoxCollider VRInteractiveItem]
```

`VRLookIcon/lookSelect005_*` 是"看一眼就跳转"的场景选择图标（地上那两个 R / O）。

### 游戏自己的驱动方式（不需要按键）
```csharp
// TimeIconControl.OnEnable
m_InteractiveItem.OnOver += HandleOver;   // 播 "Count" 动画
m_InteractiveItem.OnOut  += HandleOut;    // 回到 "Idle"

// 动画事件 -> TimeIconControlChild.EndCount()
public void TImeIconCountEnd() {
    Singleton<Manager.Sound>.Instance.Play(...);
    Singleton<CommandController>.Instance.LookSelectJumpToTag(JumpTag_);   // ← 真正的跳转
    m_InteractiveItem.Click();
}
```

也就是说：**VR 里只要凝视够久，动画自己走完就跳了**，全程不按任何键。

### 因此补两件事
1. **凝视悬停**：每帧把瞄准到的 `VRInteractiveItem` 映射成 `Over()`，移开时 `Out()`
   （`UpdateGazeHover()`，与准星共用同一条射线，零额外开销）。
   日志会打 `gaze hover -> '…' (VRInteractiveItem.Over)`。
2. **点击通路顺序**（`DoPhysicsClick`）：
   `Button` → `IPointerClickHandler` → **`VRInteractiveItem.Down()/Click()/Up()`** → `InteractiveProc(碰撞体名)` → 放弃。
   `[Input] ClickVRItem = true` 控制第 3 步。

### 附带确认
- `DivideByZeroException` = **0 次**（v1.35.0 的手部震动修复稳定）
- `TouchCollider_*` / `VKHit_*` / `InteractiveMark` / `clothCollider_*` 全部能被射线命中并走 `InteractiveProc`
- 地上图标（layer 19）、门/书架（layer 11）、身体（layer 8/24/26/28）都能命中，说明 `QueryTriggerInteraction.Collide` 这一改是对的

## v1.38.0 — "有些注视类互动必须站在特定位置才生效"

### 三条凝视射线，三条各自的相机
游戏里所有"看着就能触发"的交互，射线起点都不是你真正在看的那个相机：

| 使用者 | 相机来源 | 距离 |
| --- | --- | --- |
| `BodyTouchManager`（身体抚摸） | `MainCamera_.GetComponentInChildren<VREyeRaycaster>().M_Camera`，**Start() 里只取一次** | `CheckGazeTouch` 硬编码 1.5m |
| `InteractiveLookingStatus`（注视判定/指令） | `MainCamera_.transform` | `raycastDis = 2f` |
| `VREyeRaycaster.EyeRaycast()`（图标 OnOver/OnClick） | `m_Camera`（序列化的 VR 眼节点） | `m_RayLength = 500f` |

`m_Camera` 指向的是 VR rig 上的旧节点。免VR 之后相机被我们的第一人称挪走/转向，
但那三条射线仍然从**旧位置**发出 → 你只有走到"恰好能让那条射线扫到目标"的站位，互动才会触发。

再叠加距离限制：`raycastDis = 2f`，而第三人称相机恰好退到 1.5m，稍微仰俯就够不到。

### 修法
`SyncGameGazeCameras(scene)` 每次进场景把三者统一对齐到真正渲染的眼位：

```csharp
foreach (MonoBehaviour mb in Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour))) {
    switch (mb.GetType().Name) {
        case "VREyeRaycaster":           AccessTools.Field(...,"m_Camera").SetValue(mb, eye); break;
        case "BodyTouchManager":         AccessTools.Field(...,"camTrans").SetValue(mb, eye); break;
        case "InteractiveLookingStatus": AccessTools.Field(...,"camTrans").SetValue(mb, eye);
                                         AccessTools.Field(...,"raycastDis").SetValue(mb, GazeLookDistance); break;
    }
}
```

配置：`[Camera] SyncGameGazeCamera = true`、`[Camera] GazeLookDistance = 3.5`
日志：`game gaze cameras synced['Interactive'] eyeRaycaster=1 bodyTouch=1 looking=1 -> 'CameraRig/.../CenterEyeAnchor'`

## v1.39.0 — 眼高实时适配（PageUp / PageDown 立刻生效）

v1.38.0 只在**进场景时**把游戏那三条凝视射线对齐到眼位。问题是眼高是可调的：

- `PageUp` / `PageDown` 每次改变 `_eyeOverride`（±5cm，夹 0.30–2.50m）
- `DoEyeHeight()` 因此改写 `OVRCameraRig.trackingSpace.localPosition.y`

如果只在场景开始时同步，那么调完眼高之后，游戏的 `BodyTouchManager` / `InteractiveLookingStatus`
仍用旧参考点判断"够不够得着"，表现为：**明明准星压上去了，判定还是不算**。

### 现在的做法
```csharp
if (Mathf.Abs(h - _lastSyncedEye) > 0.001f) {   // 眼高变了
    _lastSyncedEye = h;
    _gazeSyncDueAt = 0f;                        // 下一帧立刻重同步
    Log.LogInfo("eye height -> X.XX m, re-syncing game gaze rays (real-time)");
}
```
外加 `TickGazeCameraSync()` 每 **2 秒**兜底重扫一次（场景内对象可能晚于插件 `Start`，
或者被游戏重新实例化），`LateUpdate` 里紧跟 `DoEyeHeight()` 调用。

`SyncGameGazeCameras()` 内部只在"值真的变了"时才写字段，所以周期扫描不会刷日志。

