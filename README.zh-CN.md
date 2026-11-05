# VRK Enhance

> English: [README.md](README.md)

> **VR カノジョ 的增强套件** —— 让它在桌面上跑得稳、看得清、玩得下去。

原版 `VR Kanojo` 是 2016 年用 Unity 5.4.5f1 做的 VR 独占作品。它在今天的机器上有三类问题：

1. **跑不起来**：exe 里有一处 SHA-1 寄存器分配 bug，在支持 SHA-NI 的 CPU（12 代以后的 Intel / Zen 4 以后的 AMD）上随机 `0xC0000005` 闪退；没有头显时 OpenVR 初始化会阻塞主线程约 100 秒，Windows 直接标"未响应"；Logo 场景的白色 Fade 面板一旦协程被打断就永久遮屏。
2. **玩不下去**：`InitScene2.CheckHMD()` 返回 false 就进不了游戏；相机是第三人称环绕、视角被固定；菜单与角色交互全靠 VR 手柄射线，鼠标点不到、触摸碰不到。
3. **不好看**：贴图各向异性过滤是 1x、没有超分、垂直同步锁在 240Hz 上跑不满、每次进场都要重新预热着色器。

这个仓库把上面三类问题一层层拆开解决，做成 **7 个可独立安装的 BepInEx 插件 + 1 个启动器 + 1 套安装/还原/配置工具**。

---

## 一、做什么

| 层 | 组件 | 干什么 |
| --- | --- | --- |
| 启动层 | `VR_Kanojo_fix.exe`（启动器） | 启动游戏前先在内存里修掉 exe 的 SHA-1 寄存器 bug（不改磁盘文件，`VR_Kanojo.exe` 哈希永久不变）；免 VR 入口会顺手关掉 `openvr_api.dll`；VR 入口会先检测并拉起 SteamVR，最多等 90 秒，起不来就不启动游戏（避免那 100 秒假死） |
| 兼容层 | `VRK_FlatFPS` | 免 VR 运行：第一/第三/自由三种视角、WASD + 鼠标、FPS 键位、准星与凝视光环、E 键触摸、键鼠点菜单；同时修掉白屏、眼高、相机被搬走、点击穿透遮罩等一系列问题 |
| 增强层 | `VRK_UpScale` | FSR1（EASU + RCAS）超分：以 0.85 比例渲染，输出到原生分辨率 |
| 增强层 | `VRK_TexPlus` | 全分辨率贴图 + 16x 各向异性 + 三线性过滤 + mip 偏移，分帧扫描不卡顿 |
| 增强层 | `VRK_Perf` | 关垂直同步、限帧、加载期 GC 收口、掐掉 `OVRManager` 每帧改 MSAA 的代码 |
| 增强层 | `VRK_PreWarm` | 进场贴图/材质分帧预热，削掉进场景的卡顿尖峰 |
| 内容层 | `VRK_Unlock` | 一键解锁全部回想、剧情、玩法分支（调用游戏自己的 `ALLClearData()`，不手改存档） |
| 工具层 | 安装器 / 配置面板 | 图形化选择装哪几个、一键还原、可视化调全部 113 个配置项 |

---

## 二、有什么用

### 1. 从"打不开"到"能玩"

- **闪退**：只有走启动器才安全。启动器把 `0x98BD83` 的 `lea rax,[rsi+0x40]` 改成写 `r10`、`0x98BD8B` 的 `cmovne rsi,rax` 改成 `rsi,r10`，让函数帧指针寄存器（`rax` 存着原始 `rsp`）不被数据指针踩掉。不改磁盘，进程退出即失效。
- **100 秒未响应**：免 VR 入口把 `openvr_api.dll` 改名，Unity 的 OpenVR 后端直接加载失败、秒退；VR 入口先确认 `vrserver.exe` 在跑，再放行。
- **白屏**：`VRCameraFade.Awake` 后置补丁把序列化里那块 `alpha=1` 的白板清零，另加看门狗处理"协程被打断导致 `m_IsFading` 永久 true"的情况。

### 2. 从"能玩"到"好玩"

- **三种视角**：第一人称（视点就是你的眼睛，WASD 走动，与戴头显的感受一致）、第三人称（游戏原版环绕）、自由飞行（看场景、找机位）。
- **鼠标真的能交互**：直连点击（绕过 EventSystem 整条链）、物理射线点击、`VRInteractiveItem` 悬停/点击、E 键触摸（`CheckGazeTouch` 原路调用）、凝视光环吸附到实际命中点。
- **凝视类互动不再"要站对位置"**：把游戏自己那三条凝视射线的相机来源对齐到渲染眼位，眼高用 PageUp/PageDown 调完立刻生效。

### 3. 从"好玩"到"看着舒服"

- 0.85 渲染比例 → 内部 2176×1224，像素量 **减少 27.8%**，再用 EASU 拉回 2560×1440，边缘比双线性放大干净。
- 贴图侧各向异性拉满，斜视地面/墙面不再糊成一片。
- 关掉垂直同步后，240Hz 屏不再被"跑不满 240 就掉到 120"的节奏抖动绑架（建议配合驱动侧的 G-Sync + 限帧 237）。

---

## 三、怎么样（实测状态）

**静态验证**（不依赖运行游戏，`verify.cmd` 一键跑完）：

| 验证器 | 项数 | 结果 |
| --- | --- | --- |
| `VRK_FlatFPS` | 170+ | ALL PASSED |
| `VRK_UpScale` | 81 | 81 / 81 |
| `VRK_Unlock` | 33 | 33 / 33 |
| `VRK_PreWarm` | 50 | 50 / 50 |
| `VRK_Perf` / `VRK_TexPlus` / `AwakeCheck` | — | ALL PASSED / `banned=0` |

验证器不是数着玩：它们用 Mono.Cecil 读编译产物，逐个断言补丁目标签名、P/Invoke 约定、每帧零分配、画质不被降级、`HarmonyPatch` 目标真实存在（15 个补丁目标 15/15 命中）。

**运行时证据**（`BepInEx/LogOutput.log`）：

```
render-scale mode: 2176x1224 -> 2560x1440                     ×8    ← 每次进场各一次
native pass is live: 2176x1224 -> 2560x1440 (native v10203)   ×8    ← 超分真的在跑
IME detached (prev context=2035741)                           ×1    ← 输入法已摘
DivideByZeroException                                         ×0    ← 震动除零已堵
cleared opaque fade panel at Awake (was alpha=1.00)                 ← 白屏根因已断
```

**已知边界**（不吹）：

- **没有做 FSR2/FSR3**。FSR2 的 DX11 后端官方仓库里没有，且需要运动矢量 + jitter + reactive mask，而 Unity 5.4 没有 velocity pass、`nonJitteredProjectionMatrix` 要 5.6+。硬上只能靠猜，效果不可控，所以留在 FSR1。
- **原生超分层默认关闭**（`vrk.upscale.cfg` 的 `NativePass=false`）。它曾两次触发 `0xC0000374` 堆损坏（`ntdll` 在进程退出路径捕获），加固到 v1.6.x 之后没再复现，但没有长期跑通验证，所以保持关闭、由 Unity 自己做缩放。想试可以改成 `true`。
- **女性去码包会把裙子画到桌子上**。三套流行包里都含 11 处指向不存在文件 `cf_base.resource` 的外部引用，且比原版 `cf_base.unity3d` 少一个 `SkinnedMeshRenderer`（101 vs 102）。默认 A/B 里女性去码是关的（`UncensorLoader.cfg`），男性包自包含、可用。
- **VR 模式功能是原生的**。所有插件在 `VRKANOJO_VR=1` 下自动退场（零 Harmony 补丁），不会有我们的逻辑去干扰头显。

---

## 四、安装

前置条件：游戏根目录已有 **BepInEx 5.4.12 x64**（`winhttp.dll` + `BepInEx/core`）。那是 BepInEx 本体，本仓库不负责安装。

游戏目录不用填：仓库放在游戏根目录时直接命中；放别处也会顺着父目录 → Steam 库（注册表 + `libraryfolders.vdf`）→ 各盘常见路径自己找，认不出来才需要 `-GameDir`。查当前定位结果：`powershell -File tools/setup.ps1 -EchoGameDir`。

### 一键安装（推荐）

双击 **`安装面板.cmd`** → 勾选要装的组件 → 点「安装选中」。也可以直接用现成脚本：

| 脚本 | 作用 |
| --- | --- |
| `安装面板.cmd` | 图形面板：勾选组件、显示已装状态、试运行、装/还原 |
| `一键安装.cmd` | 命令行交互：输入编号选择组件 |
| `安装推荐.cmd` | 装 `flatfps + upscale + perf + prewarm + texplus + launcher` |
| `安装全部.cmd` | 九个组件全装（含 `unlock`、`openvr`、`devtools`） |
| `一键还原.cmd` / `还原全部.cmd` | 按安装记录逐条回滚（含恢复被改名的原文件） |
| `配置面板.cmd` | 可视化编辑全部插件配置（113 个键） |

详见 [docs/zh-CN/INSTALL.md](docs/zh-CN/INSTALL.md)。

### 命令行

```bat
rem 装某几个
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Components flatfps,upscale,launcher -Yes

rem 只看会做什么
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Components all -DryRun

rem 还原
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Mode restore -Yes
```

安装会写 `state/manifest.json`（记录每个动作）+ `state/backup/`（被覆盖的原文件），还原就是倒着走这张表。`state/` 不进 git。

### 不要直接双击 `VR_Kanojo.exe`

那样会绕过启动器的内存补丁，SHA-1 bug 照旧。用桌面快捷方式（安装 `launcher` 时自动生成「VR Kanojo (免VR)」「VR Kanojo (VR)」两个），或 `启动_免VR模式.bat` / `启动_VR模式.bat`。

---

## 五、配置

装好后用 **`配置面板.cmd`** 改，或直接编辑 `BepInEx/config/vrk.*.cfg`。改完重启游戏生效；面板保存前会自动备份到 `BepInEx/config/_backup/`。

常用的几个：

| 文件 | 键 | 默认 | 说明 |
| --- | --- | --- | --- |
| `vrk.flatfps.cfg` | `[Camera] EyeHeight` | 1.28 | 第一人称眼高（米），游戏内 PageUp/PageDown 可实时微调 |
| | `[Camera] ViewToggleKey` | F | 切换 第一/第三/自由 视角 |
| | `[Camera] MouseSpeed` | 800 | 鼠标灵敏度（≈16°/100px） |
| | `[Input] InteractKey` | E | 触摸/交互键 |
| | `[Input] GazeGrabDistance` | 2.5 | 凝视抓取距离（原版硬编码 1.5，够不到人） |
| | `[Visual] AlwaysShowGazeRing` | true | 凝视光环常显 |
| `vrk.upscale.cfg` | `[General] Mode` | RenderScale | `Sharpen`=同分辨率锐化 / `RenderScale`=真超分 |
| | `[RenderScale] Scale` | 0.85 | 渲染比例 |
| | `[General] NativePass` | false | 是否启用原生 FSR1 层（见"已知边界"） |
| `vrk.perf.cfg` | `[Frame] VSync` | 0 | 0=关，-1=不改 |
| `vrk.unlock.cfg` | `[Unlock] Enabled` | true | 关掉就还原成正常进度 |

完整清单见 [docs/zh-CN/CONFIG.md](docs/zh-CN/CONFIG.md)。

---

## 六、仓库结构

```
VRK-Enhance/
├─ plugins/          七个插件的源码（各带 _verify/ 静态验证器）
│   ├─ VRK_FlatFPS/      免 VR 运行 + 交互
│   ├─ VRK_UpScale/      FSR1 超分（含 native/ 原生层 C++ 与 HLSL）
│   ├─ VRK_Perf/         帧率与加载
│   ├─ VRK_PreWarm/      预热
│   ├─ VRK_TexPlus/      贴图
│   └─ VRK_Unlock/       解锁
├─ launcher/         启动器源码（C）与两个入口 bat
├─ tools/            setup.ps1 / gui.ps1 / configgui.ps1 + 审计工具
├─ payload/          预编译成品（DLL + vrkup.dll + 默认配置）
├─ fixtures/         Mono.Cecil.dll、准星位图
├─ docs/             INSTALL / CONFIG / KEYS / TECH-NOTES / CHANGELOG
├─ build.cmd         编译插件 + 跑验证器
├─ build-native.cmd  编译原生超分层
├─ build-shaders.cmd 编译 HLSL（需要 Windows SDK 的 fxc）
└─ verify.cmd        只跑验证器
```

## 七、自己构建

需要 .NET SDK（提供 Roslyn `csc`）、MinGW g++（原生层）、Windows SDK `fxc`（着色器，产物已入库可跳过）。

```bat
build.cmd            rem 七个插件 -> dist\，并自动跑验证器
build-native.cmd     rem native\vrkup.dll
build-shaders.cmd    rem 重新生成 shader_blobs.h（可选）
verify.cmd           rem 只校验
```

## 八、许可

MIT（本仓库自有代码）。第三方：Mono.Cecil（MIT）、AMD FidelityFX FSR1（MIT）、Unity 头文件（Unity Companion License）。详见 `LICENSE`。

游戏本体、`BepInEx` 本体、以及 `VR_Kanojo_Data` 里的资源都不在本仓库内。

## 九、免责

改内存、改渲染管线、改存档语义的 mod 都有搞坏客户端或存档的可能。安装器会备份、会记账、能一键还原；但请自己留好存档副本（`UserData/save`）。
