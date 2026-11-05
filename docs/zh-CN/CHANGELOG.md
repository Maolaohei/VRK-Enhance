# 变更记录

版本号按插件独立递增。这里按时间顺序记"为什么要改"。

## VRK_FlatFPS

| 版本 | 改动 | 对应症状 |
| --- | --- | --- |
| 1.0.0 | `InitScene2.CheckHMD` 放行 + `LogoScene.Start` 兜底 + 卡死看门狗 + WASD 键位重映射 | 免 VR 无法进入 / 白屏 / 方向键不顺手 |
| 1.1.0 | 右上角帮助面板（F7，雅黑，自适应字号） | 键位太多记不住 |
| 1.2.0 | 指针锁定改为**按住 Alt 释放**（取消粘性开关） | 切窗口太麻烦 |
| 1.3.0 | 面板进游戏显示 5 秒自动隐藏；面板文案改描述式 | 挡住画面 |
| 1.4.0 | 多实例 Fade 看门狗、卡死升级停用 Canvas、截帧 PNG、渲染状态 dump | 白屏仍在 |
| 1.4.1 | 在 `VRCameraFade.Awake` 之后清掉面板不透明 alpha | **白屏根因**（序列化数据） |
| 1.5.0 | 菜单场景冻结 `IllusionCamera`、姿态复位、long frame 日志 | 相机被第三人称控制器搬走，菜单出画 |
| 1.6.0 | 深度图形 dump（Graphic 视口包围盒 / Renderer / Canvas） | 纯色界面定位 |
| 1.7.0 | 眼高补偿（默认 1.28 m，PageUp/PageDown 实时调） | 相机贴地板，看到地面球 |
| 1.8.0 | 菜单 UI 桥（EventSystem + GraphicRaycaster） | 菜单点不动 |
| 1.8.1 | `IllusionCamera` 实例 dump | 诊断 |
| 1.9.0 | 直连点击（RectangleContainsScreenPoint + onClick）+ 清 raycast 遮挡 | 桥建了还是点不动 |
| 1.10.0 | Transpiler 重映射键位（小键盘→主键盘数字/`[`/`-`）、`MouseSpeed=800` | 键位习惯 / 鼠标太慢 |
| 1.10.1 | `R` 重置视角 | 用户要求 |
| 1.10.2 | 诊断日志降级为 Info、`ClickDebug`/`SaveFrames` 默认关 | 日志噪音 |
| 1.11.0 | 隐藏凝视光环（`HideGazeRing`）、`DumpKey=F10` | 屏幕中央有个物体 |
| 1.12.0 | 中心探针（射线 + 投影双路，每次只报一次） | 定位那个物体 |
| 1.13.0 | 物理射线点击（绕开 EventSystem） | 正片点不动 |
| 1.13.2 | **不再 `SetActive` 光环**，只关 Renderer；`ClickInGame`/`PhysicsClick`/`KeepOvrInput` | 关掉光环把游戏自己的点击链一起关掉了 |
| 1.14.0 | 光环改为按实例 ID 复核（游戏会重建它） | 光环又出现 |
| 1.15.0/1.16.0 | 视角切换 F 键三态、自由视角飞行 | 需求 |
| 1.16.1 | 默认第一人称；F 写进帮助面板 | 需求 |
| 1.17.0 | **真实第一人称**（视点=眼睛，WASD 移动人） | 旧的"第一人称"只是把 distance 压到 0.05 |
| 1.17.1 | 切视角左上角 toast | 需求 |
| 1.17.2 | 眼高叠加修正（FloorLevel 下才补）、PageUp/PageDown 微调 | 眼高 3.2 m |
| 1.17.3 | `EyeHeight` 默认 1.28 | 用户实测 |
| 1.18.0 | **VR 模式入口**（`VRKANOJO_VR=1`）：免 VR 功能整体让路 | 需要两个启动入口 |
| 1.19.0 | 还原旧插件的中心准星（内嵌 PNG 资源） | 需求 |
| 1.20.0 | 凝视光环常显 + 吸附命中点 | 光环时有时无 |
| 1.21.0 | 关掉 UI 桥（`LegacyUiBridge=false`）+ `InteractionLog` | 有时候无法和女主交互 |
| 1.22.0 | 光环按 `Physics.RaycastNonAlloc` 吸附到命中点 | 光环不跟随视角 |
| 1.23.0 | E 键接入 `CheckDecision`、`PlayerLookingCheck` 距离、悬停方框 | 需求 |
| 1.24.0 | E 真正接进触摸输入（`H_ActStart`/`GazeTouchIn_L`）+ `CheckGazeTouch` 抓取距离 Transpiler | E 在实战里没用 |
| 1.25.x | 光环距离上限 8 m、无命中隐藏、一帧延迟修正、reticle dump | 准星"呆在原地" |
| 1.26.0 | `ForceNonController`（`controlType→0`）+ `ForceEnableGaze` + E 探针 | E 仍不生效（前置条件不满足） |
| 1.27.0 | 第一人称 yaw 交给 rig（菜单跟随视角） | 画面转了，但游戏以为你朝原方向 |
| 1.27.1 | 修 VR 相机被停用、VR 输入被改键鼠、光环类型名少命名空间 | 自查 |
| 1.28.x | VR 下零补丁、五个插件全部让路、启动器自动检测 SteamVR | VR 模式要"完全原生" |
| 1.29.0 | `MousePixel()`：0.85 渲染比例下的点击坐标换算 | 点击偏 |
| 1.30.0 | 左键当右手（`GrabBegin`）+ 输入法剥离 | 需要 VR 手的互动 |
| 1.31.0 | 把手瞬移到准星命中点再抓；IME 三层摘除 | 左键仍不行 |
| 1.32.0 | 改用游戏自己的 `CheckGazeTouch(HANDEDNESS)` | `BodyCaressGrabbable` 的碰撞体不是命中体 |
| 1.33.0 | 自建瞄准射线（从视口中心，与准星同源） | 触发点与准星有偏差 |
| 1.34.0 | UI 指针空间适配（`UiPoint`）+ 渲染探针 | 0.85 下 UI 点击偏 |
| 1.35.0 | `Vibration` 除零补丁 + `OnTriggerStay` Finalizer + 日志降噪 | 手贴合没反应 |
| 1.36.x | 射线改 `QueryTriggerInteraction.Collide`（trigger 也能命中）；`InteractiveProc` 兜底 | 射线穿过 trigger |
| 1.37.0 | 凝视悬停映射（`VRInteractiveItem.Over/Out`）+ 点击链路四段递进 | 需要"看"触发的图标 |
| 1.38.0 | 三条凝视射线的相机对齐到渲染眼位、距离配置化 | 要站特定位置才生效 |
| 1.39.0 | 眼高变化时实时重同步 | PageUp/PageDown 后射线又错 |
| 1.40.0 | `gaze ring target` 日志接入 `GazeRingLog` | 日志噪音 |
| 1.41.0 | `ClickVRItem` 默认关 + 帧级/按钮级去重 | 一次点两下 |

## VRK_UpScale

| 版本 | 改动 |
| --- | --- |
| 1.0.0 | 托管层 + 原生 FSR1（EASU/RCAS）、D3D11 状态保存恢复、视图缓存 |
| 1.2.0 | `dest == null` 时用临时 RT；首次成功打印日志 |
| 1.2.1 | 无 RT 时 `Blit(src, null)` 兜底（**画面因此才恢复**） |
| 1.3.0 | 拆出 `RenderInto`、持久 RT、`Mode=RenderScale` |
| 1.4.0/1.4.1 | VR 入口让路；空指针重试而不永久放弃 |
| 1.4.2 | 日志改成 `owned target` |
| 1.5.0 | RT 格式归一化 ARGB32，尺寸变化走 `Release()+Create()`（避免 use-after-free） |
| 1.6.0/1.6.1 | `NativePass` 二分开关；`OnDestroy` 只 Release；presenter 相机不销毁 |
| 1.7.0 | presenter 每帧强制 `rect`/`aspect`（否则准星与点击全偏） |

## v1.2.2 — 准星改为自绘

- `fixtures/crosshair_128.bin` 现在由 `fixtures/make_crosshair.py` 生成（纯距离场，不含任何第三方素材）：
  浅红圆环 + 两侧细亮边 + 四根亮色刻度。原来的缓冲区是从 ManlyMarco 的 *Play Without VR* 里提取的，
  那份插件的许可无法从二进制确认。
- `VRK_FlatFPS.dll` 用新贴图重新构建（165 KB，sha256 `295797c6…`），五个验证器仍全绿，安装包负载已同步。
- `THIRD-PARTY-NOTICES.md` 不再标注第三方贴图 —— 发布包里已无来源不明的素材。

## v1.2.1 — 许可整理

- 新增 `THIRD-PARTY-NOTICES.md`：逐组件列出许可、UnityDoorstop 的 LGPL-2.1 合规说明、MIT 归属。
- 许可证全文随包：`payload/bepinex/LICENSE-BepInEx-MIT.txt`、
  `payload/bepinex/LICENSE-UnityDoorstop-LGPL-2.1.txt`。
- 修正 v1.2.0 的两处错误：发布包 sha256 写错（`6c57a71441da...` → `6c57a714a892...`）；
  `payload/bepinex/README.txt` 把整个 zip 标成 LGPL-2.1（实际只有 `winhttp.dll` 是，
  BepInEx 本体是 MIT）；`LICENSE` 里「不分发 BepInEx」的表述也一并删掉。

## v1.2.0 — 自带 BepInEx + 按目录记账

- 新增 `bepinex` 组件：随包附带 BepInEx 5.4.23.6（x64，Doorstop 4.6，`payload/bepinex/BepInEx_win_x64_5.4.23.6.zip`，sha256 `6c57a714…`），仅当游戏目录缺少 `BepInEx/core/BepInEx.Preloader.dll` 时解包；选中任何插件组件会自动带上它。卸载只删它解出的 22 个文件与随之空掉的目录。
- `state/manifest.json` 的每条记录都写入所属游戏目录，装到第二个目录（或拿假目录测试）不再串账，`-Mode restore` 只处理你当前传的那个目录。

## 安装器 / 面板

| 版本 | 改动 | 为什么要它 |
| --- | --- | --- |
| 1.0.0 | `tools/setup.ps1`：9 个组件、`state/manifest.json` 记账、`state/backup/` 备份、`-DryRun` | 手动复制十几份文件太容易漏 |
| 1.1.0 | 五个 `.cmd` 包装（交互 / 推荐 / 全部 / 还原全部） | 双击就能用 |
| 1.2.0 | 装 `launcher` 时自动建桌面快捷方式（`VR Kanojo (免VR).lnk` / `VR Kanojo (VR).lnk`，指向两个 bat，图标取 `VR_Kanojo.exe`） | 每次都要进目录双击 bat |
| 1.3.0 | `-List` / `-ListFile` 组件表导出（UTF-8 制表符行） | 给面板喂数据，避免控制台代码页把中文搅碎 |
| 1.5.0 | 游戏目录自动定位（显式 → 仓库父目录 → 上溯 4 层 → Steam 库 → 各盘常见路径）、`-EchoGameDir`；面板显示定位结果并可【选择目录…】 | 用户不该被要求手填路径 |
| 1.6.0 | 新增 `配置面板.cmd` / `tools/configgui.ps1`：可视化编辑 6 个 cfg 共 113 个键，Boolean 勾选框、出厂值回填、保存前 `.bak` 备份 | 用户不该手改 cfg 才能调参 |
| 1.4.0 | `tools/gui.ps1` 图形面板 + `安装面板.cmd`；`tools/install.ps1` 英文名转发器；`-SelfTest` 自检 | 命令行菜单对普通用户还是门槛 |

## 其它

| 插件 | 版本 | 说明 |
| --- | --- | --- |
| `VRK_Perf` | 1.0.0 → 1.1.0 | 关 VSync / 解除限帧 / 加载期 GC / `useRecommendedMSAALevel` 钉死；诊断默认关 |
| `VRK_PreWarm` | 1.0.0 → 1.1.1 | 分帧预热（6 ms/帧，2 s 预算）；VR 下跳过（主线程抢帧在头显里是可见顿挫） |
| `VRK_TexPlus` | 1.0.0 → 1.3.0 | 全分辨率/16x 各向异性；分帧扫描（`PerFrame=48`）；VR 下整插件不启动 |
| `VRK_Unlock` | 1.0.0 | 调用游戏自带 `ALLClearData()` 解锁并写盘；只在菜单场景动手 |
| `launcher` | — | 内存补丁（SHA-1 寄存器 bug）、SteamVR 预检、`VRK_DRY` 干跑 |
