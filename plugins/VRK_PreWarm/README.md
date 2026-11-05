# VRK_PreWarm v1.0.0

加载期预热插件：把场景刚打开时那段卡顿，摊到加载画面里去。

- 插件 GUID：`vrk.prewarm`
- 程序集：`BepInEx/plugins/VRK_PreWarm.dll`（9728 B，sha256 `1194660e71047042bdc686fa08214f1da7856b4ec6fb7f0ea5a0d208a4a0fd65`）
- 配置：`BepInEx/config/vrk.prewarm.cfg`
- 依赖：只有 `mscorlib` / `UnityEngine` / `BepInEx`。**不引用 Assembly-CSharp、不用 Harmony**，和游戏代码零耦合。

## 1. 它做什么

每次场景加载完成后（`yield return null` 让场景里的对象先跑完自己的 Awake/Start），按顺序做三件事：

| 阶段 | 调用 | 作用 |
| --- | --- | --- |
| 1 | `Shader.WarmupAllShaders()` | 每个会话只做一次，把 build 里所有着色器变体编译掉。这是"第一次出现新材质就顿一下"的根源。 |
| 2 | `Material.SetPass(0)` | 对当前已加载的每个 Material 解析它真正要用的那个变体。 |
| 3 | `Texture.GetNativeTexturePtr()` | 触发贴图上传到显存，避免进场景后贴图一张张"刷"出来。 |

全部带时间片：每帧最多 `FrameBudgetMs`（默认 6ms）就用 `yield return null` 让出一帧，单场景累计超过 `TotalBudgetMs`（默认 2000ms）就停手，日志里写 `(budget reached)`。游戏全程正常运行，不阻塞。

每场景一行日志：

```
prewarm 'Title': materials=412 textures=631 shaders=no 38.4ms
```

## 2. 为什么要这么写（重要）

`VRK_PreWarm` 的 `Awake()` **只绑定配置 + 创建一个空 GameObject**，绝不碰任何图形 API。

原因是在 Unity 5.4.5f1 里，BepInEx 跑插件 `Awake()` 的时刻早于 Unity 建好图形设备：此时 SystemInfo / GfxDevice 的后备单例还是 null，任何图形 icall 直接空指针解引用，进程在窗口出现之前就 `0xC0000005` 死掉，Windows 错误报告再把尸体挂 2 分钟——用户看到的就是"进游戏未响应"。

这个坑已经在 `VRK_UpScale` 上真实发生过一次（`VR_Kanojo.exe+0x3750A9`，见 `../VRK_UpScale/README.md`）。所以本插件的所有引擎调用都放在协程里，且首帧先 `yield return null`。

静态检查工具 `../_audit/AwakeCheck/` 就是专门扫这条规则的：

```
cd BepInEx/plugins/_src/_audit/AwakeCheck
dotnet build -c Release
./bin/Release/net8.0/awakecheck.exe ../../../VRK_PreWarm.dll ../../../VRK_Perf.dll ../../../VRK_TexPlus.dll ../../../VRK_UpScale.dll
```

## 3. 配置

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | true | 总开关 |
| `WarmShaders` | true | 每次会话调一次 `Shader.WarmupAllShaders()` |
| `WarmMaterials` | true | 遍历 Material 调 `SetPass(0)` |
| `WarmTextures` | true | 遍历 Texture 触发显存上传 |
| `SkipLoadingScenes` | false | 跳过 7 个加载场景（Init/Init2/CheckHMD/SetHMD/Logo2/Logo/Attention）。保持 false 可以让着色器预热藏在加载画面后面。 |
| `FrameBudgetMs` | 6 | 每帧预算 |
| `TotalBudgetMs` | 2000 | 单场景总预算 |
| `Verbose` | false | 打每阶段耗时 |

调参建议：

- 首帧还是顿 → `FrameBudgetMs` 提到 10（只在加载画面上多占一点）。
- 觉得加载变慢 → `TotalBudgetMs` 降到 800，或 `WarmShaders` 关掉（影响最大的一项）。
- 只要贴图预热 → `WarmShaders=false`、`WarmMaterials=false`。

## 4. 静态验证（不运行游戏）

`_verify/` 是一个 net8 + Mono.Cecil 检查器，**50 项全 PASS**：

```
cd BepInEx/plugins/_src/VRK_PreWarm/_verify
dotnet build -c Release
./bin/Release/net8.0/prewarmverify.exe ../../../VRK_PreWarm.dll
```

覆盖：

- **A 依赖面**：mscorlib 2.x、UnityEngine、BepInEx；无 Assembly-CSharp / 0Harmony / System.IO。
- **B 身份**：`vrk.prewarm`、version 1.0.0、`BaseUnityPlugin`、`MonoBehaviour`。
- **C 启动安全**：`PreWarmPlugin.Awake` 与 `PreWarmRuntime.Awake` 里出现任何 `SystemInfo / VRDevice / QualitySettings / Shader / Material / Texture / Camera / Resources / Application` 调用即 FAIL（这条就是防 0xC0000005 的护栏）。
- **D 预热原语**：`Shader.WarmupAllShaders`、`Material.SetPass`、`Texture.GetNativeTexturePtr`、`Resources.FindObjectsOfTypeAll`、`RenderTexture` 排除、`Time.realtimeSinceStartup` 预算、`GetActiveScene`。
- **E 配置**：7 个字段 + 两个节名 + 两个默认值（6.0f / 2000.0f）。
- **F 生命周期**：`sceneLoaded` 订阅 / `OnDestroy` 退订 / `OnSceneLoaded` / `Start` 启动首轮 / `Warm` 是协程 / 迭代器 `MoveNext` 存在 / `yield return null` 至少 4 处。
- **G 无破坏性**：不 `SetActive`、不 `Destroy`、不写 `QualitySettings`、不碰 `timeScale` / `targetFrameRate`、无 P/Invoke。

## 5. 回滚

```
rm BepInEx/plugins/VRK_PreWarm.dll
rm BepInEx/config/vrk.prewarm.cfg     # 可选
```

只删 DLL 即可，配置留着无害（下次装回来自动复用）。


## v1.1.0 — 不再预热着色器

`[PreWarm] WarmShaders` 默认由 `true` 改为 **`false`**。

实测：Unity 5.4 自己在启动阶段就做了 `Shader warmup: 25 shaders, 145 combinations, 0.414s`；我们的 `Shader.WarmupAllShaders()` 在其之上又花了 **889.5 ms**（日志 `prewarm '': ... shaders=yes 889.5ms`），属于全额浪费。

现在默认只做材质/贴图预热（`materials=161 textures=275`，分帧后约 15 ms 摊到多帧）。想让游戏启动时再预热一遍全部着色器，把 `WarmShaders = true` 打开即可。
