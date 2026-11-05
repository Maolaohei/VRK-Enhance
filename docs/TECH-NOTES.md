# 技术笔记：每一条根因的取证链

这里记的不是"改了什么"，而是**怎么确定是它**。所有结论都在不启动游戏的前提下取得：反汇编、IL dump、序列化文件二进制解析、WER 事件字段、minidump 解析、以及运行期日志（用户在跑，我们只读结果）。

游戏版本：VR カノジョ 1.07 / Unity **5.4.5f1** / x64 / D3D11。
CPU：i5-13600KF（**支持 SHA 扩展**，这点很关键）。GPU：Arc B580。

---

## 1. 随机闪退 = UnityPlayer 的 SHA-1 寄存器分配 bug

### 现象

| 崩溃码 | 模块/偏移 | 出现时机 |
| --- | --- | --- |
| `0xC0000005` | `VR_Kanojo.exe+0x98BFA3` | 早期，13:31–13:41 十个 dump 全是它 |
| `0xC0000005` | `msvcrt.dll+0x60558` | 偶发 |
| `0xC0000374`（heap corruption） | `ntdll.dll+0x1150A9` | 后期，进场景几秒后 |

同一个产品三种崩溃形态，很容易误判成三个 bug。实际上是一个。

### 反汇编

```
14098bd0a:  mov  %rsp,%rax          ; ★ 帧基准存进 RAX（溢出保存 xmm6-xmm9 也用 [rax-0x48..0x18]）
14098bd1b:  movaps %xmm6,-0x48(%rax)
...
14098bd83:  lea  0x40(%rsi),%rax    ; ★ 主循环把 RAX 当成"数据指针+0x40"用掉了
14098bd8b:  cmovne %rax,%rsi
...
14098bfa3:  movaps -0x48(%rax),%xmm6  ; ★ 尾声用被踩坏的 RAX 恢复 xmm6
14098bfb5:  mov  %rax,%rsp            ; ★ 连栈指针都从 RAX 恢复
```

`0x98BD83`–`0x98BFC0` 逐条核对过：**全程没有 `mov %rsp,%rax` 重载**，也没有其它补救。

### 为什么"平时没事"

只有走 SHA-NI 多块循环（输入 > 64 字节）才会踩到 RAX。短输入走另一条路径，完全正常。所以表现是"玩很久都没事，某个加载点突然崩"。

### 两种结局

- `rsi-8` 落在未映射页 → 当场 `0xC0000005 @ +0x98BFA3`，minidump 里 `params[0]=0x0, params[1]=0xFFFFFFFFFFFFFFFF`（反推 `rax=0x47`，即 `rsi=0x7`）
- `rsi-8` 可读 → 不报错，但 **RSP 被指向堆里的数据**，`ret` 从堆中弹返回地址 → 崩溃地点与原因完全无关（`msvcrt` / `ntdll` / 堆损坏）

### 谁在调用它

`Assembly-CSharp` 里唯一沾哈希的是 `hMACSHA.ComputeHash(bytes)`（托管实现，不进这段原生代码）→ 真正触发者是 **Unity 自己的 AssetBundle / 资源校验**，这也解释了为什么崩在切场景加载资源的时候。

### 修法

```
+0x98BD83  48 8D 46 40   lea    rax,[rsi+0x40]  ->  4C 8D 56 40  lea    r10,[rsi+0x40]
+0x98BD8B  48 0F 45 F0   cmovne rsi,rax         ->  49 0F 45 F2  cmovne rsi,r10
```

R10 是纯临时寄存器，替换后语义完全相同 —— 这正是编译器本该发出的指令。启动器在 `CREATE_SUSPENDED` 状态写这两处，**只改内存不改磁盘**，所以 `VR_Kanojo.exe` 的 SHA-256 永远是原版。

---

## 2. 白屏是"序列化数据"问题，不是渲染问题

### 早期误判

第一版猜的是"相机没渲染 / 图像特效炸了 / Intel Arc 驱动"。全部排除：`VR_Kanojo_fix_stdout.txt` 里没有一条 shader 编译错误，`Shader warmup: 25 shaders 145 combinations 0.414s` 正常完成，`Renderer: Intel(R) Arc(TM) B580` 也正常初始化。

### 决定性取证：直接解析 SerializedFile

UnityPy 1.25 读这张表会抛 `ValueError: Expected to read 36 bytes, but only read 32 bytes`（不支持 Unity 5.4 的 typetree），所以改成**手写二进制解析** `VR_Kanojo_Data/resources.assets`：

| 对象 | 字段 | 实测值 |
| --- | --- | --- |
| `pid 4366` / `pid 4459` `VRKanojo.Utils.VRCameraFade` | `m_FadeImage` | `0`（空）/ `4388` |
| | `m_FadeColor` | `(1, 1, 1, 1)` **不透明白** |
| | `m_FadeInOnSceneLoad` | `false` |
| | `m_FadeInOnStart` | `false` |
| `pid 4388` `UnityEngine.UI.Image` | `m_Color` | `(1.0, 1.0, 1.0, 1.0)` **纯白不透明** |

序列化字段顺序（无 `[SerializeField]` 的运行时字段不落盘）：

```
_Fade(int) m_FadeImage(PPtr) m_DefaultSnapshot m_FadedSnapshot
m_FadeColor(Color) m_FadeDuration(float)
m_FadeInOnSceneLoad(bool) m_FadeInOnStart(bool) VROverlay_(PPtr)
```

### 结论

那块面板**天生就是不透明白布**，只有 `LogoScene` 显式调 `FadeIn()` 才会擦掉它；而 `FadeIn()` 的实现是：

```csharp
if (!m_IsFading) { _Fade = Fade.In; StartCoroutine(BeginFade(m_FadeColor, m_FadeOutColor, duration)); }
```

`m_IsFading` 为真时**静默 return**；`BeginFade` 在协程体里先置 `m_IsFading = true`，循环结束才置回 false。协程一旦被杀（例如旧插件 `SetActive(false)/(true)` 那一下），`m_IsFading` 永远为真，面板永远白。

全程序只有 `<BeginFade>` 与 `<BeginFadeOut>` 的 `MoveNext` 写 `m_IsFading` —— 这是静态证明，不是猜测。

### 修法

1. `VRCameraFade.Awake` 之后把 `M_FadeImage.color` 的 alpha 清 0（rgb 保留）
2. 多实例看门狗：每秒 `FindObjectsOfTypeAll(typeof(VRCameraFade))` 重扫，`alpha` 连续 N 秒不变且 `IsFading==true` → 反射写 `m_IsFading=false` + 清 alpha
3. 同一个遮罩修 2 次还复现 → 直接停用整个 Fade 面板的 Canvas（硬升级）

---

## 3. 启动后"未响应"100 秒 = SteamVR 客户端阻塞

### 取证

自动 dump 脚本在 `Responding=False` 时才抓包，抓到两份 minidump；解析主线程栈（按"栈里 VR_Kanojo.exe 帧数"排序定位主线程）：

```
ntdll+0xC8889 <- KERNELBASE+0x73F1E <- VCRUNTIME140+0x1CE01
  <- vrclient_x64.dll (十余层) <- ucrtbase+0x33B39 <- vrclient_x64+0x4A0000
  <- openvr_api.dll+0x1D72 <- VR_Kanojo.exe+0x6FB613 <- VR_Kanojo.exe+0x104C828
```

CPU 时间 1.2 s / 100 s 墙钟 → **纯等待，不是死循环**。

### 机制

Unity 的 OpenVR 后端在 BepInEx 之前调用 `openvr_api.dll`，后者同步等 `vrclient_x64.dll`／`vrserver.exe`。SteamVR 没起 → 等满超时（约 100 秒）才返回失败：

```
VR: OpenVR Error! OpenVR failed initialization with error code Unknown error (310)
```

### 修法

- 免 VR 启动器：启动前把 `VR_Kanojo_Data/Plugins/openvr_api.dll` 改名 `.disabled`（游戏直接认为没有 OpenVR 后端，秒过）
- VR 启动器：先 `tasklist` 检测 `vrserver.exe`，没有就 `steam://rungameid/250820` 拉起 SteamVR 并**最多等 90 秒**，起来了才启动游戏

顺带一个多线程细节：`WaitForSingleObject` 超时返回 `0x102`（`WAIT_TIMEOUT`）时游戏还在跑，日志里会看到 `exit: wait=0x102 code=0x103 (259)` —— `0x103` 是 `STILL_ACTIVE`。

---

## 4. 进了场景只看到一片渐变

### 现象

截帧 PNG 采样（自写 PNG 解码，不依赖任何图像库）：角 `(157,187,255)`、边 `(199,212,255)`、中心 `(218,225,255)` —— 从浅蓝到近白的全屏径向渐变，而相机清屏色是 `(0,140,197)`。也就是说**有东西铺满了屏，但该显示的 UI 一个都没有**。

### 取证

`Manager.Scene.SetCameraRigPos`（反编译第 106028 行）：

```csharp
case OculusRift:
    if (OVRManager.instance.trackingOriginType == FloorLevel)
        DefPos_ = new Vector3(pos_.x, 0f, pos_.z);   // ★ Y 被写死 0
    else
        DefPos_ = pos_;
```

而 `VK_SceneBase.InitSetting()` 正好把 `trackingOriginType` 设成 `FloorLevel`（OculusRift 平台）。**眼高完全依赖头显位姿**；没有头显时 `InputTracking` 返回 identity → 眼睛卡在地板上。

渲染侧的铁证：`ren['Map00(Clone)/props/tamatama/tama'] MeshRenderer visible=yes bounds=(32.82, 24.32, 32.75)` —— 一个 33×24×33 米的室外地面球，相机在地板高度时它铺满下半屏，就是那团发光渐变（其余 24 个 renderer 包围盒都 < 0.7 米）。

### 修法

插件接管眼高：每帧把 `OVRCameraRig.trackingSpace.localPosition` 设为 `(0, h, 0)`（`OVRCameraRig.UpdateAnchors` 会据此把眼睛摆到站立高度），`h` 默认 1.28 m，第一人称下 `PageUp/PageDown` 每次 ±5 cm 实时调；有头显（`VRDevice.isPresent`）时直接跳过，绝不与真实位姿叠加。

> 注意：`VRDevice.isPresent` 只在 `LateUpdate` 之后读才安全，写在 `Awake` 里会解引用未初始化的单例 → `0xC0000005`（这条教训催生了 `tools/AwakeCheck`）。

---

## 5. 鼠标点不动菜单

### 两段弯路

- **第一版（补 raycaster）**：给 Canvas 塞 `GraphicRaycaster`、把 `EventSystem.current` 换成 `StandaloneInputModule`。日志确认执行了（`flat UI bridge in 'Title': eventSystem=patched graphicRaycaster+=1`），但正片里点不动。
- **第二版（直连点击 / 物理点击）**：绕开 EventSystem，自己 `RectangleContainsScreenPoint` 命中按钮 + `Physics.Raycast` 命中碰撞体。菜单能点了，但交互还是不行 —— 因为真正的拦路虎在别处。

### 真正的坑

```csharp
// IllusionCamera.LateUpdate() 第 20670 行
OnUI = (bool)EventSystem.current && EventSystem.current.IsPointerOverGameObject();
if (!OnUI && !Lock && (GetMouseButtonDown(0/1/2))) dragging = true;
```

我把 `EventSystem.current` 换成自己的 + 给持久挂在相机上的整屏 `Fader` Canvas 强塞了 `GraphicRaycaster` → `IsPointerOverGameObject()` 长期为真 → **拖拽彻底失效**。

而旧插件更狠：`rig.gameObject.SetActive(false); rig.gameObject.SetActive(true);` 直接杀掉挂在 `MainCamera` 上的 `VRCameraFade` 协程 —— 这才是白屏的触发点。

### 修法

- `[Input] LegacyUiBridge = false`：不建 EventSystem、不塞 GraphicRaycaster、不动 `OVRInputModule`
- 直连点击与物理点击都不依赖 EventSystem，所以关掉 UI 桥没有副作用
- 鼠标坐标统一走 `MousePixel()` / `UiPoint(canvas)`（见下节）

---

## 6. 手贴到女主身上没反应 = 一次除零

`BepInEx/LogOutput.log` 里 330 条完全相同栈的异常：

```
DivideByZeroException
  at OVRHapticsClip..ctor(byte[], int32)
  at VRKanojoTouch.TrackedController.Vibration(byte[])
  at VRKanojoTouch.Hand.OnTriggerStay(Collider)
```

`OVRHapticsClip` 的构造函数里 `Capacity = Samples.Length / OVRHaptics.Config.SampleSizeInBytes`，而免 VR 下 `SampleSizeInBytes` 恒为 **0**（从未初始化）。

致重点是调用顺序：

```csharp
// Hand.OnTriggerStay()
m_proceduralClip.Add(30);
controller.Vibration(...);              // ★ 这里抛异常
if (Random.Range(0, 3) == 1 && ...) InteractiveProc(...);   // ★ 永远执行不到
```

异常把后半段的 `InteractiveProc` 整个掐掉 —— 症状就变成"手明明贴上去了，女主没反应"。

修法：`Vibration` 的 `Prefix` 在 `SampleSizeInBytes <= 0` 时直接 `return false`（跳过震动，逻辑继续）；`OnTriggerStay` 再加一个 `Finalizer` 兜底。

---

## 7. 凝视交互"要站到特定位置才行"

三条凝视射线，各自用了一个**不同的**相机来源，而且都不是玩家正在看的那个：

| 组件 | 相机来源 | 距离 |
| --- | --- | --- |
| `BodyTouchManager.Start()` | `MainCamera_.GetComponentInChildren<VREyeRaycaster>().M_Camera`（只取一次） | `CheckGazeTouch` 里硬编码 `1.5f` |
| `InteractiveLookingStatus.Start()` | `MainCamera_.transform` | 字段 `raycastDis = 2f` |
| `VREyeRaycaster.EyeRaycast()` | `m_Camera.position/forward` | 字段 `m_RayLength = 500f` |

第三人称相机退到 1.5 米外，三条射线全部够不到人；眼高一变，射线原点也跟着错。

修法：运行时反射把三者的 `m_Camera` / `camTrans` 对齐到渲染眼位，把 `raycastDis` 写成配置（默认 3.5 m），并把 `CheckGazeTouch` 里的 `1.5f` 用 **Harmony Transpiler 替换成 `ldsfld GazeGrabRuntime`**（该函数里只有一处 `ldc.r4`，替换安全）。眼高改变时立刻重同步（`|Δh| > 1mm`），另有 2 秒兜底重扫。

---

## 8. 一次点两下

游戏自己已经把鼠标变成点击了：

```csharp
// VRInput.Update()
if (Input.GetButtonDown("Fire1") || Input.GetKeyDown(Return) || VK_Input.DecisionDown) OnDown?.Invoke();
if (Input.GetButtonUp("Fire1")   || Input.GetKeyUp(Return)   || VK_Input.DecisionUp)  { ... OnClick/OnDoubleClick ... }
```

而 `VK_Input.CheckDecision() = flag | Input.GetMouseButton(0)` → 裸左键天然就是 `DecisionDown/Up`。我又合成了一次 `Down/Click/Up`，于是每次点击触发两遍。

修法：`ClickVRItem` 默认关掉（交还原生），自建两条路径加**帧级去重**（`_clickFrame == Time.frameCount`）+ 同一 Button 250 ms 节流。

---

## 9. 0.85 渲染比例下"准星/点击/UI"各自偏一次

`VRK_UpScale` 的 `Mode = RenderScale` 会把场景渲染到 `2176×1224`（0.85）再由 presenter 放大到 2560×1440。此时：

| 消费者 | 它期待的坐标系 | 实际拿到的 |
| --- | --- | --- |
| `Input.mousePosition` | 屏幕像素 2560×1440 | ✔ 屏幕像素 |
| `Camera.ScreenPointToRay` | **渲染目标**像素 2176×1224 | 传了屏幕像素 → 偏 |
| `GraphicRaycaster` | 目标画布的像素空间 | 也吃屏幕像素 → 偏 |
| 准星 | 屏幕中心 | presenter 被拉伸时中心 ≠ 屏幕中心 |

修法：`MousePixel()`（屏幕→渲染目标缩放）+ `UiPoint(canvas)`（按画布类型选空间）+ presenter 每帧强制 `rect=(0,0,1,1)`、`aspect = dw/dh`。

---

## 10. 超分"装了却从没跑过"

日志里连续 600 帧：

```
native texture pointer is null (src=ok dst=null), skipping native pass
giving up on native upscale after 600 null-pointer frames; staying on Graphics.Blit
```

根因：Unity 把 backbuffer 以 `dest == null` 传进最后一个 `OnRenderImage`，而当时用 `RenderTexture.GetTemporary` 造的贴图 `GetNativeTexturePtr()` 返回 0；代码攒够 600 帧就永久放弃，于是**整个会话只成功过一帧**。

修法：自持持久 RT（`EnsureTarget` + 显式 `Create()`）、空指针时先 `dest.Create()` 再重取、删掉"永久放弃"改成重试窗口，最后 `NativePass` 开关可用于二分排障。

顺带一个血泪：这套 naive 的 RT 生命周期管理（在 `OnRenderImage` 里 `Destroy` 刚被当作渲染目标的贴图）会造成 Unity 5.4 的 use-after-free —— 症状就是本文第 1 节表格里的 `0xC0000374`。改成复用同一对象 + 只 `Release()` 之后消失。

---

## 11. 为什么坚持"不跑游戏也能验证"

- 崩溃不可复现：同一份代码，崩在 `0x98BFA3`、崩在 `msvcrt`、或干脆不崩，取决于堆布局
- 存档会被污染：解锁/进度类改动一旦验错就得回滚存档
- 游戏跑着的时候 DLL 被锁，连替换都做不了（`Permission denied`，只能先落 `.new` 再等退出换）

所以验证全部落在静态层：Mono.Cecil 读 IL 断言"补丁目标存在、签名匹配、改的是哪几条指令、`VrMode` 判定在 `PatchAll` 之前"，加上 `AwakeCheck` 保证 `Awake` 不碰图形 API。运行期只用日志核对（用户跑，我们读）。
