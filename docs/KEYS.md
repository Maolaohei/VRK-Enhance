# 按键表（免 VR / 键鼠）

> 游戏内所有键位都由 `IllusionCamera.KeyInput()` 直接读 `Input.GetKey(KeyCode.XXX)`，其中一部分走 `KeyInput_Value(ref val, speed, addKey, subKey)`。
> `VRK_FlatFPS` 用 **Harmony Transpiler 单趟替换 IL 里的 `KeyCode` 常量**（不改指令条数、不动分支标签），把原本的小键盘键位映射到主键盘，并额外接管了 E（交互）、F（切视角）。

## 移动与视角

| 按键 | 作用 | 备注 |
| --- | --- | --- |
| `W` `A` `S` `D` | 前 / 左 / 后 / 右 | 原是方向键，改成 FPS 习惯 |
| 鼠标移动 | 转视角 | 不需要按住任何键；`[Camera] MouseSpeed=800` 调灵敏度 |
| `PageUp` / `PageDown` | 上升 / 下降 | 第一人称下这两个键改为**实时调眼高**，每次 ±5 cm |
| `F` | 切视角：第一人称 → 第三人称 → 自由 | 左上角会弹提示；`[Camera] ViewToggleKey` |
| `R` | 重置视角（翻滚归零） | 游戏本身也用 R 做 VR 回中，属双保险 |
| `6` / `4` | 左右转视角（yaw） | 原小键盘 6/4 |
| `8` / `2` | 俯仰（pitch） | 原小键盘 8/2 |
| `5` | 俯仰回正 | 原小键盘 5 |
| `.` / `\` | 翻滚 | 按住 `Shift` 时步进 45° |
| `/` | 翻滚回正 | |
| `]` / `[` | 视野（FOV）+ / − | 原 `RightBracket` / `Equals` |
| `;` | FOV 复位 | |
| `=` / `-` | 键盘移动灵敏度 | 原小键盘 `+` / `-` |
| `End` / `Home` | 拉近 / 拉远（第三人称距离） | |
| 滚轮 | 第三人称距离微调 | |
| `Shift` / `Ctrl` | 加速 ×3 / 减速 ×0.25（第三人称） | 游戏原逻辑 |

## 交互

| 按键 | 作用 |
| --- | --- |
| 左键（按住） | 决定 / 点击 UI / 抓取（等同 VR 手的 `GrabBegin`） |
| `E` | 交互（凝视抓取，等同 VR 手柄扳机） |
| 右键 | 取消 / 结束 |
| 中键 | 切换 |
| 按住 `Alt` | 释放鼠标指针（松手立刻锁定） |

> 交互链路有三条，插件按顺序尝试，同一帧只触发一次：
> `Button`（真的 UI 按钮） → `IPointerClickHandler` → `VRInteractiveItem.Down/Click/Up` → `InteractiveCommandController.InteractiveProc(碰撞体名)`。

## 界面

| 按键 | 作用 |
| --- | --- |
| `F7` | 显示 / 隐藏帮助面板（进游戏自动显示 5 秒后隐藏） |
| `F10` | 立即 dump 渲染状态（相机 / Canvas / Graphic / Renderer / 光环），排障用 |
| `F9` | `VRK_UpScale` 输出状态（`[Debug] StatusKey`） |

## 不要用的键

`C` 和 `P`（游戏原逻辑里按住它们会**禁用整个键盘键位输入**）、`Esc`（菜单）、`Home`（部分场景被 `DebugStartUp` 拿去做 ForceMouse 开关）。

## VR 模式

设置 `VRKANOJO_VR=1`（用 `启动_VR模式.bat`）后：

- `VRK_FlatFPS` **完全不打补丁**，键位/准星/帮助面板/自建点击全部不生效，游戏回到原生 VR 逻辑
- `VRK_Perf` / `VRK_PreWarm` / `VRK_TexPlus` / `VRK_UpScale` 各自停用
- 光环（`OVRGazePointer`）是 VR 的准星，由头显控制，插件不再接管
