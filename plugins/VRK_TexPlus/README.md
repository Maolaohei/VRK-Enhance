# VRK_TexPlus — 纹理 Plus（BepInEx 版）

生成时间: 2026-11-04 16:34 (Asia/Shanghai)
安装位置: `BepInEx/plugins/VRK_TexPlus.dll` (v1.0.0, SHA-256 `902172cc0beb822fede644253b8a7ce5846c3c9a62e9df0f8e49f741b075469b`)
源码: `BepInEx/plugins/_src/VRK_TexPlus/VRK_TexPlus.cs`
配置: `BepInEx/config/vrk.texplus.cfg`（首次启动游戏时生成）

## 1. 为什么不是直接用 `补丁/纹理plus`

`补丁/纹理plus` 里实际只有 **IPA (Illusion Plugin Architecture)** 本体，没有任何贴图或纹理插件：

```
补丁/纹理plus/
  IPA.exe                        (2018-10-09, IPA.Patcher 引导补丁器)
  Mono.Cecil.dll
  IPA/Data/Managed/IllusionInjector.dll   (IPA 注入器)
  IPA/Data/Managed/IllusionPlugin.dll     (IPA 插件 API)
  IPA/Launcher.exe
```

- 类型清单确认是原版 IPA：`IPA.Patcher.PatchedModule` / `VirtualizedModule` / `BackupManager`、`IllusionInjector.Injector` / `PluginManager`，以及 `get_PluginsFolder → IPA/Plugins`。
- `IPA/Plugins/` 目录不存在，包里也没有任何 `.dll` 是纹理相关插件 → 纹理 payload 缺失。
- 真正使用 IPA 需要运行 `IPA.exe` 改写 `VR_Kanojo.exe` 的 PE 头（并生成备份），这会：
  1. 与 `VR_Kanojo_fix.exe` 的硬编码字节补丁冲突（偏移 0x98bd83 / 0x98bd8b 上必须是原始 SHA-NI 指令，PE 头变了偏移就漂移，补丁会校验失败）；
  2. 与 doorstop(`winhttp.dll`) + BepInEx 形成双注入（IPA 与 BepInEx 各自加载插件体系）；
  3. IPA 自 2018 年起已停止维护。
- **所以本插件用 BepInEx 实现同等的纹理增强，完全不碰 `VR_Kanojo.exe`。**

## 2. 本插件做了什么

游戏本体 `QualitySettings` 里 `anisotropicTextures = 2 (ForceEnable)`、`textureQuality = 0 (全分辨率)` 本来就是最高档
（`VR_Kanojo_Data/globalgamemanagers` 5 个质量档 Performance/Normal/Quality/Hi 全部如此），
所以单改全局项没有效果 —— 真正决定"糊不糊"的是**每张贴图对象自己的 `anisoLevel`**（多数导入贴图是 1）。

| 步骤 | API | 作用 |
| --- | --- | --- |
| 1 | `QualitySettings.masterTextureLimit = 0` | 强制全分辨率贴图，防止被其它插件降档 |
| 2 | `QualitySettings.anisotropicFiltering = ForceEnable` | 允许各向异性过滤 |
| 3 | `Texture.anisoLevel = 16`（先读后写，已够大的跳过） | **关键项**：斜视地面的文字/皮肤/衣物不再糊成一片 |
| 4 | `FilterMode.Bilinear → Trilinear` | 过滤过渡更顺，减少色带 |
| 5 | `Texture.mipMapBias`（默认 0 = 不动） | 需要更锐可设 -1，代价是远处闪烁 |

- 枚举方式：`Resources.FindObjectsOfTypeAll(typeof(Texture))`，**跳过 RenderTexture**（后处理的中间缓冲，动了会破坏画面）。
- 时机：启动扫一次 → 每次 `sceneLoaded` 后按 0.5/2/5/10/20s 补扫 5 轮（角色、衣物是场景加载后才实例化的）→ 之后按 `RescanSeconds`（默认 20s）周期补扫。
- 只在内存里改运行时对象，**不写盘、不替换游戏文件、无 Harmony 补丁**（`0Harmony` 未被引用，物理上无法改坏游戏逻辑）。

## 3. 配置

```ini
[Texture]
FullResolution = true    # 强制全分辨率 + 各向异性许可
AnisoLevel = 16          # 1~16，贴图各向异性等级
ForceTrilinear = true    # Bilinear -> Trilinear
MipMapBias = 0           # 负数更锐，如 -0.5 / -1

[Scan]
RescanSeconds = 20       # 周期补扫间隔，0 = 只在场景加载时扫

[Debug]
Verbose = false          # 每轮扫描统计
```

启动日志形如：

```
[TexPlus] v1.0.0 ready. aniso=16 trilinear=True mipBias=0 rescan=20s
[TexPlus] scan(startup): textures=1234 upgraded=1100 aniso<=16 trilinear=True mipBias=0
```

显存/性能开销：`anisoLevel=16` 会增加各向异性采样成本（Intel Arc B580 上基本可忽略）。若掉帧先降到 `AnisoLevel = 8`。

## 4. 静态验证（不运行游戏）

`_verify/`（net8 + Mono.Cecil）：

```
cd BepInEx/plugins/_src/VRK_TexPlus/_verify
dotnet run --no-build -- <path>/VRK_TexPlus.dll
```

26 项全部 PASS，覆盖：
1. 程序集引用（mscorlib 2.0.5.0 / BepInEx 5.4.12 / UnityEngine），无 `System.IO`、无 `0Harmony`
2. `[BepInPlugin]` guid = `vrk.texplus`
3. **不引用任何游戏类型**（Game/Manager/IllusionCamera/LogoScene… 全为 0）→ 不会影响免VR 与键位补丁
4. 5 个纹理 API 调用面齐全，先 `get_anisoLevel` 再 `set_anisoLevel`
5. `sceneLoaded` 订阅 + `OnDestroy` 退订（不泄漏）、跳过 `RenderTexture`
6. 配置默认值：`AnisoLevel=16`、`RescanSeconds=20`、`MipMapBias=0`

## 5. 回滚

删除 `BepInEx/plugins/VRK_TexPlus.dll`（以及可选的 `BepInEx/config/vrk.texplus.cfg`）即可完全恢复，游戏本体未被修改。

## 6. v1.1.0 变更（性能修正，画质效果不变）

v1.0.0 的 `RescanSeconds` 默认 20 秒，会周期性 `Resources.FindObjectsOfTypeAll(typeof(Texture))` 全堆枚举，
每次都是一次 GC 尖峰（几千个对象的数组分配 + 每张贴图重复写 native 属性），会拖累 1% low。1.1.0：

| 改动 | 效果 |
|---|---|
| `RescanSeconds` 默认 `20` → **`0`** | 只在场景加载时扫；加载后 0.5/2/5/10/20 秒补扫 5 轮，之后彻底停下 |
| 去掉中间 `Texture[] textures` 拷贝 | 少一次与贴图数量等长的数组分配 |
| 新增 `HashSet<int> _done`，按 `GetInstanceID()` 去重 | 同一张贴图只写一次 `anisoLevel/filterMode/mipMapBias`，省掉几千次 native setter |
| 配置变更（aniso/trilinear/bias）时清空 `_done` 重扫 | 改了配置依然立即生效 |

画笔质效果与 1.0.0 完全一致，只是不再周期性打扰主线程。
验证器扩到 **30 项**（新增 E 组：`_done` 为 HashSet、`Scan` 内用 `GetInstanceID` 去重、
存在 `_cfgAniso/_cfgTrilinear/_cfgBias`、配置变化时调用 `Clear()`），全部 PASS。


## 7. v1.2.0 — 分帧扫描（消掉单帧 15.4 ms 尖峰）

v1.1.0 的 `Scan()` 会在场景加载后一次性遍历几百张贴图并逐张写 `anisoLevel` / `filterMode` / `mipMapBias`。实测 Title 场景一次 `Scan` 花 **15.4 ms**（161 材质 / 275 贴图），等于一帧卡顿。

v1.2.0 拆成两段：

- `Scan(tag)` 只做枚举，把候选塞进 `_pending` 列表（本身很便宜）
- 新增 `ProcessSome(int budget)`：每帧只处理 `budget` 张贴图，处理完才打汇总日志。`Update()` 每帧调一次，预算来自新配置 `[Scan] PerFrame`
- 场景切换时先突击 `Mathf.Max(PerFrame, 256)` 张（新场景的贴图优先），其余摊到后续帧

新增配置：

```ini
[Scan]
## 每帧最多处理多少张贴图（0 = 不限制，退回 v1.1.0 行为）
PerFrame = 48
```

默认 48 → 275 张贴图分摊到约 6 帧，单帧增量约 2–3 ms，肉眼无感。
