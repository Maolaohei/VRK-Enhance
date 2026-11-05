# 配置参考

装好插件后，配置有三个入口，改的其实是同一批文件：

1. **`配置面板.cmd`** —— 图形界面，左树右值，保存前自动备份到 `BepInEx/config/_backup/`
2. 直接编辑 `BepInEx/config/vrk.*.cfg`
3. 游戏内 `ConfigurationManager`（本套件默认把它停用，需要时在安装面板勾掉 `devtools` 组件）

改完**重启游戏**生效。

## 文件一览

| 文件 | 对应插件 | 键数 |
| --- | --- | --- |
| `vrk.flatfps.cfg` | VRK_FlatFPS（免 VR 运行 + 交互） | 最多 |
| `vrk.upscale.cfg` | VRK_UpScale（超分） | 15 |
| `vrk.perf.cfg` | VRK_Perf（帧率 / 加载） | 12 |
| `vrk.prewarm.cfg` | VRK_PreWarm（预热） | 9 |
| `vrk.texplus.cfg` | VRK_TexPlus（贴图） | 10 |
| `vrk.unlock.cfg` | VRK_Unlock（解锁） | 8 |

## vrk.flatfps.cfg

### [Camera] 视角与相机

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `EyeHeight` | 1.28 | 第一人称眼高（米）。游戏内 `PageUp`/`PageDown` 每次 ±5cm 实时生效 |
| `EyeHeightEnabled` | true | 关掉则完全交给头显/游戏 |
| `ViewToggle` / `ViewToggleKey` | true / F | 三视角循环：第一 → 第三 → 自由 |
| `FirstPersonMoveSpeed` | 2.0 | 第一人称移动速度（m/s） |
| `FreeMoveSpeed` / `FreeSprintMultiplier` | 3 / 3 | 自由视角速度、Shift 倍率 |
| `MouseSpeed` | 800 | 鼠标灵敏度，500 ≈ 10°/100px，800 ≈ 16°/100px |
| `MouseHandGrab` / `MouseHandTeleport` / `MouseBodyCaress` | true | 左键当手用（瞬移 + 抓取 + 触摸） |
| `GazeLookDistance` | 3.5 | 游戏凝视射线距离（原版 2.0，够不到远处） |
| `SyncGameGazeCamera` | true | 把游戏三条凝视射线的相机对齐到渲染眼位 |
| `MenuSceneCameraFix` | true | 菜单场景冻结 `IllusionCamera`，避免菜单被搬出视野 |
| `FirstPersonRotateRig` | true | 第一人称左右转向作用在 `CameraRig` 上（游戏按 rig 判断朝向） |

### [Input] 输入

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `UseFpsKeys` | true | 方向键 → WASD、小键盘 → 数字键 |
| `InteractKey` | E | 触摸/交互键 |
| `GazeGrabDistance` | 2.5 | 凝视抓取距离（原版硬编码 1.5） |
| `DirectMenuClick` / `ClickInGame` / `PhysicsClick` | true | 三种点击通路 |
| `ForceNonController` | true | 把游戏 `controlType` 切成 0（键鼠），否则 E 键触摸不生效 |
| `ForceEnableGaze` | true | 打开 `BodyTouchManager.enableGaze` |
| `DisableIme` | true | 摘掉输入法（WASD 不再被中文输入法吃掉） |
| `UiPointerSpace` | Auto | `Auto`/`Screen`/`Render`，0.85 渲染下的点击坐标系 |

### [Visual] 视觉辅助

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Crosshair` / `CrosshairSize` / `CrosshairAlpha` | false / 56 / 0.85 | 原版准星（`crosshair.png`） |
| `AlwaysShowGazeRing` / `GazeRingMode` | true / Aim | 凝视光环常显、吸附命中点 |
| `GazeRingMaxDistance` / `GazeRingHideWhenNoHit` | 8 / true | 射线长度、没命中时隐藏 |
| `HoverReticle` | true | 可交互物四角方框 + 按键提示 |

### 其它分组

- `[Fade]`：白屏修复（`ClearPanelOnAwake`、`RepairStuckFade`、`HardKillSeconds`）
- `[Help]`：帮助面板（`ShowOnStart`、`Hotkey=F7`、`AutoHideSeconds=5`、`Scale`）
- `[Diagnostics]`：`Verbose`、`CameraReport`、`SaveFrames`、`DumpKey=F10`、`ClickDebug`、`InteractionLog`、`GazeRingLog`、`GazeAimLog`

## vrk.upscale.cfg

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Mode` | RenderScale | `Sharpen`=同分辨率 RCAS 锐化；`RenderScale`=真超分 |
| `Scale` | 0.85 | 渲染比例（0.85 → 2176×1224，省 27.8% 像素） |
| `Sharpness` | 0.2 | RCAS 强度（0=最锐，2=最柔） |
| `NativePass` | false | 原生 FSR1 层开关；默认 false 由 Unity 缩放，见 README「已知边界」 |
| `OutputWidth` | 0 | 0=跟随屏幕 |

## vrk.perf.cfg

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `VSync` | 0 | 0=关；-1=不改；1=开；2=隔帧 |
| `TargetFrameRate` | 0 | 0=不限；-1=不改 |
| `GcOnSceneLoad` | true | 只在 7 个加载场景做一次 GC 收口 |
| `ManagedLoadingPriority` | true | 加载期 High、正片 Low |
| `PinOvrRecommendedMsaa` | true | 反射关掉 `OVRManager.useRecommendedMSAALevel`（它会每帧改 MSAA） |

## vrk.prewarm.cfg

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `WarmShaders` | false | `Shader.WarmupAllShaders()`；Unity 启动已预热，我们默认不再做 |
| `WarmMaterials` / `WarmTextures` | true | `SetPass(0)` / `GetNativeTexturePtr()` 触达 |
| `FrameBudgetMs` / `TotalBudgetMs` | 6 / 2000 | 分帧预算 |
| `SkipLoadingScenes` | false | 加载场景是否跳过 |

## vrk.texplus.cfg

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `AnisoLevel` | 16 | 各向异性上限（导入默认多为 1） |
| `ForceTrilinear` | true | 双线性 → 三线性 |
| `MipMapBias` | 0 | 负值更锐、更容易闪烁 |
| `PerFrame` | 48 | 每帧处理多少张贴图（避免进场尖峰） |
| `RescanSeconds` | 0 | 0=只在场景加载时扫 |

## vrk.unlock.cfg

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | true | 总开关：启动后在菜单场景执行一次解锁并写盘 |
| `UnlockAll` | true | 调用游戏自带 `ALLClearData()`（剧情 + 互动 + 体位表 + 结局标记） |
| `UnlockStory` / `UnlockInteractive` / `UnlockLessons` | true | `UnlockAll=false` 时分别单独解锁剧情 / 互动 / `allHInfo` |
| `FlushToDisk` | true | 解锁后立刻 `Game.Save()` 写回 `UserData/save/save.dat` |
| `RetrySeconds` | 2 | 找不到 `Game` 单例时的重试间隔 |

想恢复正常进度：把 `Enabled=false` 重启一次即可（旧档请先备份 `UserData/save`）。

## 还原单个设置

配置面板里选中某个键 → 点 **Reset to default** → **Save**；它会从 `payload/config/` 里取插件出厂值写回，并留一份 `.bak`。
