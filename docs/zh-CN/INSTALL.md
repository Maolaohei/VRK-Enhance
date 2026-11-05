# 安装 / 还原

三种方式：

- **图形面板**（最省事）：双击 `安装面板.cmd`，勾选组件 → 点【安装选中】/【还原选中】。
- **命令行一键**：双击 `一键安装.cmd` 走文字菜单，或者用 `安装推荐.cmd` / `安装全部.cmd` / `还原全部.cmd` 固定组合。
- **手动复制**：见文末「手动安装」，适合只要一两个组件、或想自己控制版本的情况。

所有脚本都只做两类动作：**复制文件**、**给文件加/去 `.disabled` 后缀**。不写注册表、不动游戏本体、不动存档。

---

## 1. 图形面板（`安装面板.cmd`）

双击仓库根目录的 `安装面板.cmd`（也可以直接跑 `tools/gui.ps1`），会打开一个窗口：

```
+-------------------------------------------------------+
| 组件（[已装] 表示当前已生效）                          |
|  [ ] 免 VR 核心（白屏修复 / 相机 / 键位 / 点击 / 准星）|
|  [ ] FSR1 超分（EASU + RCAS）                          |
|  ...                                                   |
| [全选][全不选][安装选中][还原选中][安装推荐][关闭]      |
| [ ] 试运行（DryRun：只显示会改什么，不动文件）          |
| [日志窗口]                                             |
+-------------------------------------------------------+
```

要点：

- 组件标题后面带 `[已装]` 的，表示 `state/manifest.json` 里有它的记录。
- 勾选后点 **安装选中**；想撤销就点 **还原选中**（会弹一次确认）。
- 不确定会改什么就先勾上 **试运行**，日志里只列出动作、不落盘。
- 日志同时写进 `state/gui-last.log`，窗口关掉也不会丢。
- 面板只是壳，真正干活的是 `tools/setup.ps1` —— 所以命令行能做的事面板都能做。

命令行等价物：

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gui.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gui.ps1 -SelfTest   rem 自检：建界面 + 跑一次 DryRun，结果写 state\gui-selftest.txt
```

`tools/install.ps1` 是同一个安装器的英文名转发器，参数原样透传给 `setup.ps1`（给习惯敲 `install.ps1` 的人用）。

---

### 游戏目录怎么找

脚本不需要你填路径，按下面顺序自己找，找到第一个含 `VR_Kanojo.exe` 的目录就用它：

| 顺序 | 依据 |
| --- | --- |
| 1 | 你显式传的 `-GameDir` |
| 2 | 仓库所在目录本身（`VRK-Enhance` 被放进游戏根目录时命中） |
| 3 | 仓库往上 1~4 层父目录（放在 `VRK-Enhance`、`tools` 等子目录里也能找到） |
| 4 | Steam 库：注册表 `SteamPath`/`InstallPath` + 各库的 `steamapps/libraryfolders.vdf`，查 `steamapps/common/VR_Kanojo` |
| 5 | 各盘符常见位置：`<盘>:\SteamLibrary\steamapps\common\VR_Kanojo`、`<盘>:\Steam\steamapps\common\VR_Kanojo`、`<盘>:\Games\VR_Kanojo`、`<盘>:\VR_Kanojo` |

命中后会打印来源，例如：

```
  游戏目录（自动定位，Steam library: E:\SteamLibrary）: E:\SteamLibrary\steamapps\common\VR_Kanojo
```

查一下当前会装到哪（不写任何文件）：

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -EchoGameDir
```

五步都没命中（比如游戏装在移动硬盘的自定义目录、又没登记进 Steam 库），脚本会直接报错并给出两种办法：

```bat
rem 1) 把 VRK-Enhance 放进游戏根目录，重跑
rem 2) 手动指定
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -GameDir "D:\Games\VR_Kanojo" -Components all -Yes
```

图形面板里同样不需要填：状态栏会写「游戏目录（自动定位）：…」；认不出来时会变红提示，点 **【选择目录…】** 挑一次即可（只在本次运行内生效，不改任何配置）。

## 2. 命令行一键安装

双击仓库根目录的 `一键安装.cmd`（需要 PowerShell，Windows 10/11 自带），会出现菜单：

```
  1)        flatfps   免 VR 核心（白屏修复 / 相机 / 键位 / 点击 / 准星）
  2)        upscale   FSR1 超分（EASU + RCAS）
  3)        perf      帧率优化（关 VSync / 解除限帧 / 加载期 GC）
  4)        prewarm   加载期预热（着色器 / 材质 / 贴图，分帧带预算）
  5)        texplus   贴图增强（全分辨率 / 各向异性 16x / 三线性）
  6)        unlock    解锁全部内容（剧情 / 互动 / 体位表）
  7)        launcher  启动器（修 SHA-1 崩溃 + SteamVR 预检 + 两个入口）
  8)        openvr    免 VR：禁用 openvr_api.dll（消灭启动时约 100 秒未响应）
  9)        devtools  关闭开发工具（F1 配置界面 / 运行时调试器 / 坏掉的 ScriptLoader / 旧免VR插件）

  输入编号，逗号分隔（例: 1,2,7）；a=全部；直接回车=取消
```

已经装过的组件左边会标 `[已装]`。

除了交互式菜单，还有三个固定组合：

| 脚本 | 等价于 | 说明 |
| --- | --- | --- |
| `安装推荐.cmd` | `-Components flatfps,upscale,perf,prewarm,texplus,launcher` | 日常用这套：修好 + 提速 + 画质，不动解锁与开发工具 |
| `安装全部.cmd` | `-Components all` | 连 `unlock` / `openvr` / `devtools` 一起装 |
| `还原全部.cmd` | `-Mode restore` | 撤销**所有**已记录的安装动作 |

也可以直接用命令行（脚本在 `tools/setup.ps1`）：

```bat
rem 交互式
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1

rem 指定组件 + 不问确认
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Components flatfps,upscale -Yes

rem 只看会做什么，不落盘
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Components all -DryRun

rem 还原（不带 -Components 就是全部）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Mode restore -Yes

rem 游戏装在别处也可以
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -GameDir "D:\SteamLibrary\steamapps\common\VR_Kanojo"
```

---

## 3. 配置面板（`配置面板.cmd`）

装完插件后双击 **`配置面板.cmd`**，左边按「文件 → 分节 → 键」展开，右边改值，点 **Save** 写回。

- 布尔值自动显示成勾选框，字符串/数字是多行文本框，多值项（如 `GameScenes`）按行改。
- 每个键下面显示 **default:** 出厂值，**Reset to default** 一键取回。
- 保存前把原文件复制到 `BepInEx/config/_backup/<文件名>.<时间戳>.bak`。
- 状态栏显示文件数/键数/配置目录，**Open config folder** 直接开资源管理器。
- 改完**重启游戏**生效。命令行等价：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\configgui.ps1`。
- 键的完整清单与含义见 [CONFIG.md](CONFIG.md)。

## 4. 组件装了什么

| id | 落地位置 | 备注 |
| --- | --- | --- |
| `bepinex` | `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`BepInEx/**`（22 个文件，Doorstop 4.6） | 游戏目录里没有 `BepInEx/core/BepInEx.Preloader.dll` 时才会解包；装任何插件时如果缺 BepInEx 会自动带上 |
| `flatfps` | `BepInEx/plugins/VRK_FlatFPS.dll` | 免 VR 必装。白屏、眼高、WASD、鼠标视角、点菜单、E 交互、准星都在这里面 |
| `upscale` | `BepInEx/plugins/VRK_UpScale.dll` + `VR_Kanojo_Data/Plugins/vrkup.dll` | 原生 DLL 必须放 `VR_Kanojo_Data/Plugins`，Unity 只从那里加载原生插件 |
| `perf` | `BepInEx/plugins/VRK_Perf.dll` | 默认 `VSync=0`、不锁帧；240Hz 屏建议配合显卡驱动侧限帧 |
| `prewarm` | `BepInEx/plugins/VRK_PreWarm.dll` | 每帧带预算地预热，不会卡顿 |
| `texplus` | `BepInEx/plugins/VRK_TexPlus.dll` | 全分辨率 + 各向异性 16x + 三线性，显存换画质 |
| `unlock` | `BepInEx/plugins/VRK_Unlock.dll` | 调用游戏自带的 `ALLClearData()`，**下次进标题界面时**写入存档 |
| `launcher` | `VR_Kanojo_fix.exe`、`启动_免VR模式.bat`、`启动_VR模式.bat`（游戏根目录） | 必须用启动器启动；直接双击 `VR_Kanojo.exe` 仍会随机崩 |
| `openvr` | `VR_Kanojo_Data/Plugins/openvr_api.dll` → `.disabled` | 可逆改名。用 `启动_免VR模式.bat` 时它也会自动做这件事 |
| `devtools` | `ConfigurationManager.dll`、`RuntimeUnityEditor.Bepin5.dll`、`ScriptLoader.dll`、`VRK_PlayWithoutVR.dll` → `.disabled` | 全部可逆；`VRK_PlayWithoutVR` 是会导致白屏的旧插件，必须让它停用 |

配置文件 `BepInEx/config/vrk.*.cfg` **只在不存在时写入**；你已经调过的配置不会被覆盖。

---

## 5. 还原

双击 `一键还原.cmd`。它会读 `state/manifest.json`，逐条反向执行：

- 复制进去的文件 → 删除；如果安装前已存在同名文件，则用 `state/backup/` 里的副本回滚。
- 改过名的文件 → 把 `.disabled` 去掉。

可以只还原一部分：

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Mode restore -Components upscale -DryRun
```

只还原 `upscale`、并先看一眼会做什么。确认无误后去掉 `-DryRun` 再跑一次。

> 还原**不会**动你的存档（`UserData/`）、也不会动 `BepInEx/core`。想彻底回到"从未装过"的状态，还原后再删掉 `BepInEx/`、`winhttp.dll`、`doorstop_config.ini` 即可 —— 那部分是 BepInEx 本体，本仓库不负责安装。

---

## 6. 记录与备份

| 路径 | 内容 |
| --- | --- |
| `state/manifest.json` | 每条安装动作（组件、动作类型、目标路径、备份位置、时间） |
| `state/backup/<原相对路径>` | 被覆盖文件的原始副本 |

`state/` 是**你本机的东西**，已经在 `.gitignore` 里；别把它提交上去。

想重新开始：删掉整个 `state/` 目录（此时还原不再可用，但已装的文件不受影响）。

---

## 7. 手动安装

不想用脚本时，按上表把文件复制过去即可，等价于：

```bat
xcopy /Y payload\plugins\VRK_FlatFPS.dll   BepInEx\plugins\
xcopy /Y payload\plugins\VRK_UpScale.dll   BepInEx\plugins\
xcopy /Y payload\native\vrkup.dll          VR_Kanojo_Data\Plugins\
xcopy /Y launcher\VR_Kanojo_fix.exe        .
xcopy /Y launcher\启动_免VR模式.bat         .
xcopy /Y launcher\启动_VR模式.bat           .
copy   /Y payload\config\vrk.flatfps.cfg   BepInEx\config\
```

手动安装不会产生 `state/manifest.json`，所以 `一键还原.cmd` 无法撤销 —— 要么自己留好备份，要么用脚本装。

---

## 8. 常见问题

**装完进游戏还是白屏 / 一片渐变**

`flatfps` 没生效。检查 `BepInEx/plugins/VRK_FlatFPS.dll` 是否存在，并看 `BepInEx/LogOutput.log` 里有没有 `[FlatFPS] v1.4x ready` 这一行。没有的话说明 BepInEx 没加载到它（`winhttp.dll`、`doorstop_config.ini` 是否还在？）。

**启动还是卡 100 秒**

`openvr_api.dll` 还活着。装 `openvr` 组件，或者直接用 `启动_免VR模式.bat` 启动。

**超分开了但画面没变**

`BepInEx/config/vrk.upscale.cfg` 里 `Mode` 必须是 `RenderScale`（不是 `Sharpen`），且 `NativePass` 为 `true` 时才会走原生 FSR1。日志里应出现 `native pass is live: 2176x1224 -> 2560x1440`。

**为什么每次都提示"未响应"**

Unity 5.4 是同步加载场景，Windows 只要 5 秒没收到窗口消息就标"未响应"。这个提示本身不代表卡死，等它加载完即可；`openvr` 组件解决的是那**多出来的 100 秒**。

**装了解锁进游戏没变化**

`unlock` 是在**进标题界面**时写入存档的。看到标题画面后再进一次剧情列表就有了；如果存档已被别的工具锁定，先退出游戏。
