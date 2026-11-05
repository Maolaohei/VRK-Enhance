# VRK_UpScale — 免 VR 模式的超分辨率 / 锐化

替代方案说明：**XeSS 在这个游戏里做不到**，本插件改用 AMD FSR1（EASU + RCAS，MIT 许可）。

## 为什么不是 XeSS

以下全部是在本机实测取证得到的结论，不是猜测：

| 检查项 | 实测结果 | 结论 |
|---|---|---|
| 官方 SDK（github.com/intel/xess，main）有无 DX11？ | 有 `inc/xess/xess_d3d11.h`、`lib/libxess_dx11.lib`、`bin/libxess_dx11.dll`(156016 B) | DX11 路径存在 |
| `libxess_dx11.dll` 是否附带模型权重？ | 无；strings 含 `INTC_D3D11_XessSelectNetworkModel` / `INTC_D3D11_XessSetVelocityScale`，NN 推理下沉到 Intel 驱动 | 只有 Intel 显卡可用（本机 Arc B580 符合） |
| 能否修改 SDK 源码？ | `LICENSE.txt` = Intel Simplified Software License，只允许二进制原样分发 | **没有"开源 XeSS 实现"** |
| 运动矢量是否可选？ | DLL 内错误串 `pVelocityTexture is NULL`，缺了直接报错 | **强制必需** |
| 官方 Unity 插件（GameTechDev/XeSSUnityPlugin）能否用？ | 要求 Unity 2022.3.39f1+ / Unity 6 | 本游戏是 **Unity 5.4.5f1**，完全不可用 |
| 本游戏管线能否提供 MV + 深度？ | 内建管线，无 motion vector pass | MV 必须自己造，等于重写渲染管线 |

没有运动矢量就无法用 XeSS。要在 Unity 5.4 里造 MV，需要改游戏的所有着色器，属于不可维护的改动，所以选用 FSR1：不需要 MV、不需要深度、单帧即可工作、MIT 许可可自由分发。

## 交付物

| 文件 | 说明 |
|---|---|
| `BepInEx/plugins/VRK_UpScale.dll` | 托管插件（16896 B） |
| `VR_Kanojo_Data/Plugins/vrkup.dll` | 原生 D3D11 插件（141182 B，x64） |
| `BepInEx/plugins/_src/VRK_UpScale/VRK_UpScale.cs` | 托管源码 |
| `BepInEx/plugins/_src/VRK_UpScale/native/vrkup.cpp` | 原生源码 |
| `BepInEx/plugins/_src/VRK_UpScale/native/{fullscreen,easu,rcas}.hlsl` | FSR 着色器源 |
| `BepInEx/plugins/_src/VRK_UpScale/native/shader_blobs.h` | 已编译字节码（内嵌进 DLL） |
| `BepInEx/plugins/_src/VRK_UpScale/_verify/` | 静态验证器（net8 + Mono.Cecil） |

`vrkup.dll` 放在 `VR_Kanojo_Data/Plugins/` 是因为 Unity 只从那里加载原生插件，同时避免 BepInEx 把它当托管程序集扫到。

## 工作方式

* 着色器用 Windows SDK 的 `fxc` **离线编译**成 `ps_5_0` / `vs_5_0` 字节码内嵌进 DLL，运行时**不依赖 d3dcompiler.dll**，因此不会出现"着色器编译失败导致黑屏/白屏"。
* D3D11 设备不是从 Unity 的回调里拿的（回调在 5.4 上不可靠），而是从 Unity 传进来的 `ID3D11Texture2D` 反查：`ID3D11Resource::GetDevice` → `GetImmediateContext`。纯指针，无 ABI 风险。
* 每帧先保存全套 D3D11 状态（RTV/DSV、viewport、input layout、topology、VS、PS、sampler s0、SRV t0、CB b0、RS/BS/DS、blendFactor、sampleMask、stencilRef），画完原样恢复，不会污染 Unity 的渲染状态。
* **任何失败都回退**：`VRKU_Init`/`VRKU_Run` 返回非 0，托管侧立刻 `Graphics.Blit(src, dest)` 画原图。也就是说原生插件坏掉时画面与不装插件完全一致，不会白屏。

### 两种模式

* `Sharpen`（默认）：全分辨率下只跑 RCAS。不改相机设置，纯锐化，最安全。
* `RenderScale`：把场景按 `Scale` 渲染进离屏 RT，再用 EASU 放大 + RCAS 锐化输出到屏幕。会重接相机的 `targetTexture` 并额外建一个不渲染任何物体的呈现相机，所以默认关闭。VR 模式（`VRDevice.isPresent`）下插件整体不激活——立体渲染被全屏后处理会破坏双眼画面。

## 配置

`BepInEx/config/vrk.upscale.cfg`，首次运行由 BepInEx 生成。

| 节 | 键 | 默认 | 说明 |
|---|---|---|---|
| General | Enabled | true | 总开关，false 时完全不加载 |
| General | Mode | Sharpen | `Sharpen` 或 `RenderScale` |
| General | Sharpness | 0.2 | RCAS 强度 0–1，FSR 官方默认 0.2 |
| General | OutputWidth | 0 | 仅 RenderScale：目标宽度，0 = 当前屏宽 |
| RenderScale | Scale | 0.75 | 仅 RenderScale：场景渲染比例 0.25–1.0 |
| Debug | Verbose | false | 每次建管线打一行日志 |
| Debug | StatusKey | F9 | 按键把状态写进日志，空字符串禁用 |

F9 状态行长这样：

```
status: native v10200 state=1 frames=1234 fails=0 mode=Sharpen sharpness=0.20 device='Intel(R) Arc(TM) B580 Graphics' error=''
```

`state`：1 = 管线就绪，0 = 尚未建立，负数 = 失败（-1 着色器/资源创建失败，-2 拿不到设备，-3 分辨率非法）。`fails` 持续增长说明原生侧在丢弃帧，画面会退回未处理结果。

## 静态验证（未启动游戏）

全程没有运行 `VR_Kanojo.exe`，也没有运行 `VR_Kanojo_fix.exe`。

```bash
cd BepInEx/plugins/_src/VRK_UpScale/_verify
dotnet build -c Release
dotnet run -c Release --no-build -- \
  ../../../VRK_UpScale.dll \
  ../../../../VR_Kanojo_Data/Plugins/vrkup.dll
```

**67 项全 PASS**，覆盖：

* 程序集身份：mscorlib 2.0.5.0（net35）、`TargetRuntime.Net_2_0`、引用 UnityEngine/BepInEx、**不引用 Assembly-CSharp**（零游戏类型耦合）
* 插件元数据：GUID `vrk.upscale`、版本 1.0.0
* 原生绑定：只 P/Invoke `kernel32!LoadLibraryW` 与 `GetProcAddress`（无 d3d11/dxgi 导入），7 个委托全是 `StdCall`，9 个 `VRKU_*` 导出名全部以 `ldstr` 出现，`Marshal.GetDelegateForFunctionPointer` 存在
* 配置面：7 个键 + 3 个节全部绑定
* D3D11 门禁：`SystemInfo.graphicsDeviceType` 与枚举值 2 比较
* 渲染钩子：`OnRenderImage(RenderTexture, RenderTexture)` 签名正确、每帧只调一次 `Run`、传两个 `GetNativeTexturePtr`、4 处 `Graphics.Blit` 回退
* 相机管线：24 位深度 RT、`Camera.targetTexture` 设置/清理、presenter 相机
* 生命周期：`sceneLoaded` 订阅 + 退订、`OnDestroy` 调 `VRKU_Shutdown`、`DontDestroyOnLoad`
* 原生二进制：PE32+ / machine 0x8664、12 个导出齐全、不链接 `d3dcompiler`
* 安全性质：不调用 `SetActive`、不碰 PlayerPrefs

## 重新构建

托管侧（必须 net35 引用，不能用 net4x）：

```bash
cd "/e/SteamLibrary/steamapps/common/VR_Kanojo"
dotnet "/c/Program Files/dotnet/sdk/8.0.422/Roslyn/bincore/csc.dll" \
  -noconfig -nostdlib+ -target:library -langversion:6 -optimize+ -nologo \
  -out:BepInEx/plugins/VRK_UpScale.dll \
  -r:VR_Kanojo_Data/Managed/mscorlib.dll \
  -r:VR_Kanojo_Data/Managed/System.dll \
  -r:VR_Kanojo_Data/Managed/System.Core.dll \
  -r:VR_Kanojo_Data/Managed/UnityEngine.dll \
  -r:BepInEx/core/BepInEx.dll \
  BepInEx/plugins/_src/VRK_UpScale/VRK_UpScale.cs
```

原生侧：

```bash
cd BepInEx/plugins/_src/VRK_UpScale/native
g++ -m64 -shared -O2 -std=c++14 -fno-exceptions -fno-rtti \
  -o ../../../../../VR_Kanojo_Data/Plugins/vrkup.dll vrkup.cpp \
  -ld3d11 -ldxgi -luuid -lole32 -static-libgcc -static-libstdc++
```

注意 MinGW 已自带 `strcpy_s` / `sprintf_s`（sec_api 头），不要再自己写同名 shim，否则报 `ambiguating new declaration`；另外 `ID3D11Resource::GetDevice` 返回 `void`，不能用 `FAILED(...)` 包。

着色器（改 HLSL 后需要重新生成 `shader_blobs.h`）：

```bash
FX="/c/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/fxc.exe"
"$FX" //nologo //T vs_5_0 //E main //Fo fullscreen.cso fullscreen.hlsl
"$FX" //nologo //T ps_5_0 //E main //Fo easu.cso easu.hlsl
"$FX" //nologo //T ps_5_0 //E main //Fo rcas.cso rcas.hlsl
```

## 回滚

```bash
rm BepInEx/plugins/VRK_UpScale.dll VR_Kanojo_Data/Plugins/vrkup.dll
```

或把配置里 `Enabled` 改成 `false`。游戏本体文件（`VR_Kanojo.exe`、`Assembly-CSharp.dll`）从未被改动。

---

## v1.1.0 — 修掉"通过 VR_Kanojo_fix.exe 进入游戏未响应"

### 现象

部署 v1.0.0 之后第一次启动（2026-11-04 16:55:17，pid 14720）：
进程存在但 `MainWindowTitle` 为空、`Responding=True`、WS 231 MB、CPU 仅 1.5 s；约 2 分钟后进程消失。
`VR_Kanojo_fix.log` 补出 `exit: wait=0x0 code=0xc0000005 (-1073741819)`。

→ 不是卡死，是**窗口还没出现就访问违例崩溃**，Windows 错误报告（WER）把死掉的进程挂着等，看起来才像"未响应"。

### 取证链

| 证据 | 值 |
| --- | --- |
| 事件日志 Application Error id=1000 | `16:57:13 c0000005 off=0x3750a9 pid=14720 mod=VR_Kanojo.exe` |
| 16:57 之前的最后一次崩溃 | `14:32:16 off=0x60558 mod=msvcrt.dll`；再往前 `13:13–14:00` 全是 `off=0x98bfa3`（SHA-1 尾声，`VR_Kanojo_fix.exe` 已修好） |
| `BepInEx/LogOutput.log` | 0 字节（DiskLogListener 非即时刷新，进程被 AV 直接杀死，缓冲区丢失） |
| `BepInEx/cache/*.dat` | 16:57 已写入 → 链式加载器跑完了，Harmony 也打过补丁 |
| 部署时间 | `VRK_UpScale.dll` + `vrkup.dll` 于 **16:25** 落地；16:25–16:55 之间没有任何崩溃事件 |

### 崩溃点定位

```
1403750a0: sub  $0x28,%rsp
1403750a4: call 0x1402048a0        ; 取某个单例（读全局 0x141322e08）
1403750a9: mov  0x11cc(%rax),%eax  ; <-- 此处 AV，rax 为 0
```

`.data` 里有一对紧邻的 67 项表（名字表 file `0x1276C58`、函数指针表 file `0x1276E70`，间距正好 `67*8`）：

```
[0]  UnityEngine.SystemInfo::get_processorType
...
[9]  UnityEngine.SystemInfo::get_graphicsDeviceType
[10] UnityEngine.SystemInfo::get_usesOpenGLTextureCoords
[11] UnityEngine.SystemInfo::get_graphicsDeviceVersion
```

`funcs[10] = 0x1403750A0` —— 命中崩溃点。全 Managed + 全部插件里**没有任何代码调用 `usesOpenGLTextureCoords`**，而 `get_graphicsDeviceType` 只有一个调用者：本插件 `UpScalePlugin.Awake()`。

即：**BepInEx 在 Unity 建好图形设备之前就跑了插件 `Awake()`**，此时 SystemInfo / GfxDevice 单例仍是 null，`SystemInfo.graphicsDeviceType` 的 icall 解引用空指针 → `0xC0000005`。

### 修法

1. `UpScalePlugin.Awake()` 里彻底删除 `SystemInfo.graphicsDeviceType` 与 `VRDevice.isPresent` 判断（源码里留了长注释说明原因）。
2. 设备门禁搬到新方法 `UpScalePlugin.CheckGate()`，由 `UpScaleRuntime.CheckGateDeferred()` 协程在**两帧之后**调用，此时设备必然已就绪。
3. `UpScaleRuntime.Update()` 在 `GateDone && GateOk` 之前直接 return，绝不提前碰相机。
4. 版本号 1.0.0 → 1.1.0。

### 验证（不运行游戏）

- 静态扫描器 `_src/_audit/AwakeCheck`：旧 DLL `UpScalePlugin.Awake` 命中 3 处 BANNED（`SystemInfo::get_graphicsDeviceType` ×2、`VRDevice::get_isPresent`）；新 DLL **0 处**。
- 本目录验证器扩到 **71 项全 PASS**，新增 N 组 4 项：Awake 不碰引擎、`CheckGate()` 存在、`CheckGate` 读 `graphicsDeviceType`、门禁由协程延迟执行。
- 其余插件同步扫描：`VRK_Perf` / `VRK_TexPlus` / `VRK_FlatFPS` 的 `Awake` 全部 clean。

### 如果还崩

已把 `HKCU\...\Windows Error Reporting\LocalDumps\VR_Kanojo.exe` 配成 `DumpType=1`、`DumpCount=16`（此前 10 个 dump 是 13:3x 那批，系统级 `DumpCount` 默认 10 已经写满，所以 16:57 那次没留下转储）。之后再崩会落新 dump 到 `C:\Users\Admin\AppData\Local\CrashDumps`，可以直接离线走栈定位。

## v1.2.0 — 修掉“纯色界面 / 只剩背景”

### 现象
进入正片后整屏只有一层渐变背景（截帧 `vrk_frame_Title_1.png` 两次运行**逐字节相同**），
Title 的 UI、角色、任何东西都不出现；音频正常；`VRK_UpScale` 日志显示
`sharpen mode attached to 'CenterEyeAnchor'`，而 `VRKU_Run failed` 一次都没打。

### 根因（D3D11 API 契约）
1. 相机上挂了 `OnRenderImage` 之后，**Unity 不再自己渲染 `dest`**，把整帧交给插件填；
   插件不填，屏幕上就只剩清屏色 —— 这就是“纯色”。
2. 旧实现的 sharpen 路径是「RCAS 渲染到临时贴图 `g_temp` → `CopyResource(dst, g_temp)`」，
   而 `g_temp` 的格式取自 **`src`**（`EnsurePipeline` 里的 `CreateTemp(g_dstW, g_dstH, g_fmt)`，
   `g_fmt = srcFmt`）。
3. `ID3D11DeviceContext::CopyResource` 要求**源与目标格式、尺寸完全一致**。
   Unity 的 `dest` 通常是交换链 `B8G8R8A8_UNORM`，`src` 是 `R8G8B8A8_*` 系，
   两者格式不同 → `CopyResource` **静默失败**，且旧代码从不检查 HRESULT。
4. 于是每帧都“成功”返回 0，但 `dest` 从来没有被写过 → 纯色背景 + 有声音。

### 修法（native v10202）
- `VRKU_Run` 里先从 `dst` 取 `D3D11_TEXTURE2D_DESC`，记住 `g_dstFmt`。
- 临时贴图改为按**目标格式**创建：`CreateTemp(g_dstW, g_dstH, g_dstFmt ? g_dstFmt : g_fmt)`。
- **彻底删掉 `CopyResource`**：两条路径最后都直接用 `dst` 自己的格式建 RTV（`GetRtv(dst, outFmt)`）
  并把结果画进去 —— sharpen 模式一趟 RCAS 直接进 `dst`，缩放模式 EASU→临时→RCAS 进 `dst`。
- 新增守卫：`src == dst` 时返回 `-8`（不采样又写同一张贴图），`GetRtv` 失败返回 `-7`，
  两者都会让托管层退回 `Graphics.Blit(src, dest)`，保证**最坏情况下画面也不会消失**。
- `VRKU_VERSION` 10201 → **10202**。

### 验证（不运行游戏）
| 检查 | 结果 |
| --- | --- |
| `abi_test.exe`（LoadLibrary + 逐个取导出，不建 D3D 设备） | `version = 10202`、9 个 `VRKU_*` 全部解析、`Run(NULL,NULL) = -2` 不崩、`Init(...) = 0` |
| `VRK_UpScale/_verify` 静态验证器 | **71 passed, 0 failed** |
| 二进制字符串 | 新守卫串 `src == dst` 已出现在产物里 |
| 旧路径残留 | `grep -c CopyResource` 仅剩注释，无调用 |

### 回滚
把 `BepInEx/plugins/VRK_UpScale.dll` 改名加 `.disabled` 即可整体退出超分；
只想去锐化就设 `BepInEx/config/vrk.upscale.cfg` 的 `[General] Mode = Off`。
### 追加加固（native v10203 / managed 1.2.0）
- **显式光栅化状态**：`DrawFullscreen()` 每次先 `RSSetState(g_rsSolid)`（`D3D11_CULL_NONE`、
  `FILL_SOLID`、`DepthClipEnable=TRUE`）。不再赌 Unity 当前 RS 的剔除模式 —— 全屏三角形
  被剔除同样是“整帧空白”的一种成因。
- **每 5 秒一次保险 Blit**：托管层 `OnRenderImage` 在把帧交给原生层之前，若距上次保险超过
  `[Safety] SanitySeconds`（默认 5.0，0=关闭），先 `Graphics.Blit(src, dest)` 写入一个合法
  的未处理帧。这样即使原生层将来又静默失效，用户最多看到 5 秒异常画面，而不是整段纯色。
- **首帧确认日志**：第一次真正写帧时打印
  `writing the frame straight into Unity's target: 2560x1440 -> 2560x1440 (native v10203, sharpen only)`，
  用户日志里能直接看到“超分确实在工作”。


## v1.3.0 — 让超分真正生效（dest == null 修复）

**问题**：Unity 把 backbuffer 以 `null` 传给相机上最后一个 `OnRenderImage`。旧代码 `if (dest == null) { Graphics.Blit(src, null); return; }` 在调用原生层之前就返回，于是：

- `VRKU_Init` 一次都没执行（日志里永远没有 `pipeline ...` 那一行）
- RCAS 一帧都没跑过 —— "超分" 从头到尾是空转
- 还每帧白付一次 2560x1440 全屏 Blit

**修法**：`OnRenderImage` 拆成两半。

| 位置 | 职责 |
| --- | --- |
| `OnRenderImage(src, dest)` | 只处理 `dest == null`：`RenderTexture.GetTemporary(tw, th, 0, src.format)` 造一个真实 RT，交给 `RenderInto`，最后 `Graphics.Blit(tmp或src, (RenderTexture)null)` 一定把画面吐回屏幕 |
| `RenderInto(src, dest)` | 原来那套 Init / Run / 失败回退，返回 bool |

因为现在每帧都把真实帧 blit 出去，"保险丝"式的周期 sanity blit 已删除（配置项 `SanitySeconds` 仍可绑定，但不再参与渲染）。

**副作用**：Shappen 模式终于会真的跑 RCAS，画面锐度会立刻变化；性能开销也会第一次实际出现（Arc B580 上约 0.2–0.4 ms @1440p）。

## v1.2.1

`dest == null` 时改用 `Graphics.Blit(src, (RenderTexture)null)`（先保住画面），但整条超分链路仍未接通 —— 见 v1.3.0。

### v1.4.0 — VR 入口下自动让路

- `BepInEx/plugins/_src/VRK_UpScale/VRK_UpScale.cs` 的 `CheckGate()` 里新增一条：
  环境变量 `VRKANOJO_VR=1`（由 `启动_VR模式.bat` 设置）时直接返回
  `launched by the VR entry (VRKANOJO_VR=1); upscaling is flat-mode only, staying inactive`，
  整个插件不挂 `OnRenderImage`。
- 原因：立体渲染每只眼睛各一个目标，全屏后处理会把左右眼画面搅在一起。
  以前靠 `VRDevice.isPresent` 判断，但"用 VR 入口启动但头显还没连上"的那几秒里它会误判成平面模式。
- 验证器 74 项全通过（`VRK_UpScale/_verify`）。

### v1.4.2 — 日志文案修正（无功能变化）

`native pass is live: 2176x1224 -> 2560x1440 (native v10203, EASU+RCAS, owned target)`

旧后缀 `, temp target` 是 v1.3.x 用 `RenderTexture.GetTemporary` 时留下的，v1.4.1 改成自己持有并 `Create()` 的持久目标后该措辞已不准确，改为 `, owned target`。渲染路径、配置项、验证项均未变动。

## v1.6.0 / v1.6.1 — 堆损坏闪退的二分开关与销毁硬化

### 背景
两次闪退签名完全相同：`c0000374 STATUS_HEAP_CORRUPTION`，
`ntdll.dll + 0x1150a9`，都发生在进入正片场景后几秒（12:05 / 12:30）。
两次都开着 `Mode = RenderScale`；改为 RenderScale 之前的会话是稳定的。

### v1.6.0
- 新配置 `[General] NativePass`（默认 true）：`false` 时 `RenderInto` 直接
  `Graphics.Blit` 双线性放大，**完全不调用原生层** —— 用于二分。
- `EnsureRenderScale` 里的 `_small` 不再 `Destroy`，改为 `Release()` +
  改尺寸 + `Create()` 复用同一对象。

### v1.6.1（销毁硬化）
场景切换时 Unity 会销毁挂着 `OnRenderImage` 的物体，之前的代码在
`OnDestroy` 里 `Destroy(_target)`、在 `DestroyPresenter()` 里
`Destroy(_presenterCam.gameObject)` —— 在渲染链尾部销毁刚被当作渲染目标的
贴图/正在出图的相机，是 `c0000374` 的高危路径。

改为：
- `UpscaleEffect.OnDestroy`：只 `_target.Release()`，不 `Destroy` 贴图对象；
- `DestroyPresenter`：只 `_presenterCam.enabled = false` + `_presenterEffect.Active = false`，
  物体保留，下次进场景重新启用。

验证器新增 5 项（`RenderInto` 受 `NativePass` 门控、false 时走 `Blit`、
`EnsureRenderScale` 不再 `Destroy` 且改走 `Release`）→ 81/81 PASS。

## v1.7.0 — presenter 铺满屏幕与比例约束

每帧在 `EnsureRenderScale` 里强制：

```csharp
_presenterCam.rect  = new Rect(0f, 0f, 1f, 1f);
_presenterCam.aspect = (float)dw / dh;
```

原因：presenter 是把离屏图放大到屏幕的唯一出口，一旦它的 rect/aspect 与屏幕不一致，
画面就会被拉伸或留黑边，**"相机正中心"与"屏幕正中心"不再重合** —— 准星、鼠标点击、
凝视交互（射线都从相机中心发出）会整体偏移。锁死这两项后，屏幕中心恒等于相机中心。
