# VRK_Perf — 免VR 场景下的帧数 / 1% low 优化（不降画质）

插件 GUID：`vrk.perf`　版本：1.0.0　文件：`BepInEx/plugins/VRK_Perf.dll`
目标：**不降低任何画质项**，只消除"白送的"掉帧与卡顿来源。

## 为什么是这个思路

先实测了游戏的质量档全表（`VR_Kanojo_Data/globalgamemanagers`，5 档）：

| 档位 | vSync | AA | 阴影 | 阴影分辨率 | 各向异性 | 贴图 | LOD bias |
|---|---|---|---|---|---|---|---|
| None | 1 | 0 | 关 | 10 | ForceEnable | 满 | 2.0 |
| Performance / Normal | 1 | 2x | 硬 | Low | ForceEnable | 满 | 2.0 |
| Quality（当前生效） | 1 | **4x** | 软 | Low | ForceEnable | 满 | 2.0 |
| Hi | 1 | **8x** | 软 | Low | ForceEnable | 满 | 2.0 |

结论：阴影分辨率已经是 Low、各向异性已经是 ForceEnable、贴图已经是全分辨率 —— **全局质量项没有"免费的帧"可捡**。
真正的白送损耗只有三处：垂直同步、加载期的托管 GC、以及 OVRManager 每帧改写 MSAA。

## 三个改动

### 1. 关垂直同步（默认开）
`[Frame] VSync = 0`

游戏 5 个质量档全部 `vSyncCount = 1`。在 2560x1440 @240Hz 上，一旦帧时间偶尔超过 4.17ms，
vsync 会把这一帧拖到下一个刷新周期，出现 240 → 120 → 240 的节奏抖动 —— 这正是 **1% low 最典型的形态**。
关掉 vsync = 帧时间不再被刷新率锁死，1% low 立刻贴近平均帧。

代价：可能出现轻微画面撕裂（Tearing）。若显示器支持 G-Sync / FreeSync，请在驱动里开启（面板内限制帧率到 237 更稳），
这样既无撕裂也无不必要等待。

### 2. 加载期主动 GC（默认开）
`[Loading] GcOnSceneLoad = true`

只在 7 个加载/淡出场景（`Init / Init2 / CheckHMD / SetHMD / Logo2 / Logo / Attention`）做一次
`GC.Collect() + GC.WaitForPendingFinalizers()`，把全停顿回收挪到玩家看不见的黑屏/淡出期，
而不是在剧情中途由一次普通分配触发。同时把加载期的 `Application.backgroundLoadingPriority`
提到 `High`（加载更快），进入正片后降回 `Low`（不抢主线程时间片）。

### 3. 拦掉 OVRManager 每帧改 MSAA（默认开）
`[Compatibility] PinOvrRecommendedMsaa = true`

反编译确认 `OVRManager.Update()` 里有这么一段：

```csharp
if (useRecommendedMSAALevel && QualitySettings.antiAliasing != display.recommendedMSAALevel)
{
    Debug.Log("The current MSAA level is ...");            // 每帧字符串拼接 + Debug.Log（还要抓栈）
    QualitySettings.antiAliasing = display.recommendedMSAALevel;  // 把画质档的 4x 悄悄改成 0
}
```

免 VR 运行时没有真实头显，`display.recommendedMSAALevel` 与画质档的 4x 长期不一致 →
**既每帧刷日志吃 CPU，又可能把 4x MSAA 降成 0（这是降画质）**。
本插件用纯反射把 `OVRManager.instance.useRecommendedMSAALevel` 置 false，
在 0.5s / 2s / 5s 三次重试（OVRManager 是场景里现 instantiate 的）。**不使用 Harmony，不修改任何游戏方法体。**

## 配置（`BepInEx/config/vrk.perf.cfg`，首次运行自动生成）

```ini
[Frame]
## -1 = 不改, 0 = 关闭垂直同步, 1 = 开, 2 = 隔帧
VSync = 0
## -1 = 不改, 0 = 不设上限（推荐）
TargetFrameRate = 0

[Loading]
GcOnSceneLoad = true
ManagedLoadingPriority = High / Low

[Diagnostics]
CameraReport = true
Verbose = false

[Compatibility]
PinOvrRecommendedMsaa = true
```

`CameraReport = true` 会在每个场景（以及进场景 3 秒后）往 `BepInEx/LogOutput.log` 打一次
`Camera.allCameras` 清单（name / depth / rect / target / hdr / cullingMask）。
它是只读的诊断：如果发现有两个相机都在渲染同一画面，那就是真正的性能漏洞，可以据此再动手。

## 静态审计证据（不运行游戏）

自建审计器 `plugins/_src/_audit/PerfAudit`（Mono.Cecil，扫每帧消息方法 + 协程 MoveNext）：

- `Assembly-CSharp.dll`：307 个每帧方法，合计约 54336 B/帧 的潜在分配，7120 处热调用，117 个带循环。
  头部：`SteamVR_Menu::OnGUI` 3552 B（SteamVR 示例菜单，本作未启用）、`SteamVR_GazeTracker::Update`（示例类）、
  `OVRManager::Update` 1200 B（就是上面第 3 条）。
- 插件侧稳态分配：`VRK_Perf` **0 B/帧**、`VRK_FlatFPS` 48 B、`VRK_TexPlus` 96 B、
  `VRK_UpScale` 的 1120 B 全在失败回退分支（正常路径是 `Graphics.Blit` + P/Invoke，零分配）、
  `MessageCenter` 96 B、`ConfigurationManager` 240 B。

## 同时修掉的自身问题：VRK_TexPlus 1.1.0

v1.0.0 的 `RescanSeconds` 默认 20 秒，会周期性地 `Resources.FindObjectsOfTypeAll(typeof(Texture))` 做全堆枚举，
每次都是一个 GC 尖峰（几千个对象的数组 + 每张贴图重复写 native 属性）。1.1.0：

- `RescanSeconds` 默认改为 **0**（只在场景加载时扫，加载后 0.5/2/5/10/20 秒补扫 5 轮后彻底停下）；
- 扫描去掉中间 `Texture[]` 拷贝；
- 新增 `HashSet<int> _done`，按 `GetInstanceID()` 去重 —— 同一张贴图只写一次 `anisoLevel/filterMode/mipMapBias`；
- 配置改动（aniso/trilinear/bias 变化）时才清空去重表重扫。

画质效果与 1.0.0 **完全一致**，只是不再周期性打扰主线程。

## 验证（不启动游戏）

```bash
# 50 项静态检查：依赖面 / 每帧零分配 / 默认值 / 只碰这三样 / 画质不降级 / 生命周期
dotnet BepInEx/plugins/_src/VRK_Perf/_verify/bin/Release/net8.0/perfverify.dll \
       BepInEx/plugins/VRK_Perf.dll

# TexPlus 1.1.0 版 30 项
dotnet BepInEx/plugins/_src/VRK_TexPlus/_verify/bin/Release/net8.0/texverify.dll \
       BepInEx/plugins/VRK_TexPlus.dll
```

已实际跑过：`VRK_Perf` 50/50 PASS，`VRK_TexPlus` 30/30 PASS，
回归 `VRK_FlatFPS` 51/51 PASS、`VRK_UpScale` 67/67 PASS。
验证器里包含硬性断言：**从不写 `QualitySettings.antiAliasing / shadows / shadowResolution / pixelLightCount / lodBias`、
从不 `SetQualityLevel`、从不 `SetActive`/`Destroy`/改 `timeScale`** —— 即"画质不降级"不是口头承诺，是编译产物级别的事实。

## 回滚

删掉 `BepInEx/plugins/VRK_Perf.dll`（或把 `[Frame] VSync` 设回 `-1` 只关这一个功能）。
`VRK_TexPlus` 回滚到 1.0.0 只需把 `[Scan] RescanSeconds` 改回 `20`，画质路径不变。


## v1.1.0 — 诊断默认关闭

`[Diagnostics] CameraReport` 默认由 `true` 改为 **`false`**。它会枚举 `Camera.allCameras` 并拼字符串（每场景一次 + 3 秒后一次），属于纯诊断输出。要排查渲染问题时改回 `true`。
