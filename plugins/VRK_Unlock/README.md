# VRK Unlock All v1.0.0

一个只做一件事的 BepInEx 插件：**把游戏里"还锁着的内容"一次性解锁，并写进存档。**

- Plugin GUID：`vrk.unlock`
- 依赖：BepInEx 5.4.12（不需要 Harmony，不引用 `Assembly-CSharp`，纯反射）
- 配置文件：`BepInEx/config/vrk.unlock.cfg`

## 1. 它到底做了什么

不猜存档格式、不硬改 `save.dat`，而是**直接调用游戏自己写好的开发用函数**（`Manager.Game`，反编译第 89642 行起）：

| 调用 | 作用 |
| --- | --- |
| `InitData()`（仅当列表为空时） | 重载 `StoryList` / `InteractiveList` / `LessonList`，供下面两个函数遍历 |
| `ALLClearData()` | `isStartMainFlag=true`、`isEnding=true`、`isFirst=false`、`isKiss/isAibu/isHoushi/isSonyu=true`；`storyPlayDict_`、`interactivePlayDict_` 按列表全填 `1`；`allHInfo` 按 `lessonDict_` 全填 `1` |
| `ALLScenarioData()` / `ALLInteraciveData()` | `UnlockAll=false` 时的分项版本（注意游戏原始拼写就是 `Interacive`） |
| `Save()` | 把上面的结果写回 `UserData/save/save.dat` |

`storyPlayDict_` 的键形如 `vk_adv_00_001_02`（`StoryList.fileName + "_" + fileNo`），游戏用 `ContainsKey` / `== 0` 判定"这段剧情是否已看过"，所以填满即等于全解锁。

## 2. 安全边界

- **只在菜单/加载场景动手**（`Init` / `Init2` / `CheckHMD` / `SetHMD` / `Logo2` / `Logo` / `Attention` / `Title`），正片里什么都不做 —— 因为 `InitData()` 会清零计数器，正片里跑会打断当前进度。
- **只跑一次**（`_done` 幂等），最多重试 `MaxAttempts=30` 次（每次间隔 `RetrySeconds=2`），找不到 `Game` 单例就放弃并写日志。
- 游戏里存在**两份**同名类：`Manager.Game` 与拼写错误的 `GmaeManager.Game`，两份都会被尝试，谁先可用就用谁。
- 与 VR 无关：VR 入口下同样生效（它改的是存档内容，不是输入/渲染）。
- 已通过静态验证 33 项（`_verify/`，见下），`awakecheck` 判定 `UnlockPlugin.Awake clean`（不在 Awake 里碰任何图形/VR API）。

## 3. 配置

```ini
[Unlock]
Enabled = true         # 总开关
UnlockAll = true       # 一次全给（推荐）
UnlockStory = true     # UnlockAll=false 时：只解锁剧情
UnlockInteractive = true
UnlockLessons = true   # 体位/爱抚计数表
FlushToDisk = true     # 立刻写回 save.dat
RetrySeconds = 2
MaxAttempts = 30

[Debug]
Verbose = false
```

## 4. 日志长什么样

```
[Info :VRK Unlock All] VRK Unlock All 1.0.0 ready. enabled=True unlockAll=True
[Info :VRK Unlock All] unlock done on 'Manager.Game' in 'Title': story=63 interactive=41 hinfo=58 saved=True
```

看到 `saved=True` 就说明存档已改写；之后正常退出/进入游戏都会沿用。

## 5. 回滚

- 临时关掉：`vrk.unlock.cfg` 里 `Enabled = false`
- 彻底移除：删掉 `BepInEx/plugins/VRK_Unlock.dll`
- 恢复旧存档：解锁前的存档没有被覆盖备份，如需回退请用自己留的备份；或删掉 `UserData/save/save.dat` 让游戏重新开一局。

## 6. 静态验证

```bash
cd "BepInEx/plugins/_src/VRK_Unlock/_verify"
dotnet build -c Release
./bin/Release/net8.0/unlockverify.exe "BepInEx/plugins/VRK_Unlock.dll"
```

当前结果：**33 passed, 0 failed**（依赖面 / 元数据 / 反射目标 / 幂等与重试 / 场景门禁 / Awake 安全 六组）。
