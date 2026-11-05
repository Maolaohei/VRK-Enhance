# Third-party notices

VRK Enhance itself is MIT licensed (see [LICENSE](LICENSE)). It does not contain any
Illusion game assets, Unity engine binaries, or Intel XeSS SDK code.

The release archive bundles a few third-party components so that the kit works offline.
Everything below is redistributed **unmodified**, in the exact form its upstream published.

## What ships in the release archive

| Component | Version | License | Where | Modified |
| --- | --- | --- | --- | --- |
| BepInEx (core, preloader, HarmonyX/0Harmony, MonoMod) | 5.4.23.6 x64 | **MIT** (© 2018 Bepis) | `payload/bepinex/BepInEx_win_x64_5.4.23.6.zip` | no |
| UnityDoorstop (`winhttp.dll`, `doorstop_config.ini`) | 4.6.0 | **LGPL-2.1** | same zip | no |
| Mono.Cecil | 0.11.x | **MIT** (© 2008–2015 Jb Evain) | `fixtures/Mono.Cecil.dll` (build/verify tooling only, not loaded by the game) | no |
| FidelityFX FSR 1.0 headers (`ffx_a.h`, `ffx_fsr1.h`) | FSR 1.0 | **MIT** (© 2021 Advanced Micro Devices, Inc.) | `plugins/VRK_UpScale/native/` (compiled into our own HLSL) | no |

License texts are shipped next to the files they cover:

- `payload/bepinex/LICENSE-BepInEx-MIT.txt`
- `payload/bepinex/LICENSE-UnityDoorstop-LGPL-2.1.txt`
- `LICENSE` (this repository, includes the FSR / Mono.Cecil MIT notices)

## LGPL-2.1 compliance for UnityDoorstop

UnityDoorstop is the only LGPL-2.1 component in the bundle. How the obligations are met:

1. **License text is included** — `payload/bepinex/LICENSE-UnityDoorstop-LGPL-2.1.txt`.
2. **Corresponding source is available** — the identical version lives at
   <https://github.com/NeighTools/UnityDoorstop/tree/v4.6.0> (tag `v4.6.0`, the release asset
   this file came from). We ship no patches, so upstream source == shipped source.
3. **The library is not statically linked and stays replaceable** — `winhttp.dll` is a standalone
   DLL that the game loads by name from its own folder. Anyone may delete it and drop in a
   different build (or none at all) without touching our code; that is what the
   *relink / relink-with-modified-library* freedom amounts to for a DLL-loaded component.
4. **No extra restrictions** — our MIT-licensed code adds nothing on top of the LGPL terms, and
   the installer treats these files as ordinary payload it copies and can delete again
   (`安装面板.cmd` → restore). Removing BepInEx is fully supported.

If you prefer to obtain BepInEx/Doorstop yourself, that works too: download
`BepInEx_win_x64_5.4.23.6.zip` from <https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6>
(sha256 `6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755`) and drop it at
`payload/bepinex/` — the installer extracts whatever zip is there.

## MIT attribution

For BepInEx, HarmonyX, MonoMod, Mono.Cecil and FidelityFX FSR the MIT license requires keeping
the copyright notice and permission notice. They are reproduced verbatim in the files linked
above; the short form follows.

```
BepInEx            Copyright (c) 2018 Bepis                    — MIT
HarmonyX           Copyright (c) BepInEx contributors          — MIT
MonoMod            Copyright (c) 2015–2020 MonoMod contributors — MIT
Mono.Cecil         Copyright (c) 2008–2015 Jb Evain             — MIT
FidelityFX FSR 1.0 Copyright (c) 2021 Advanced Micro Devices    — MIT
```

## Not distributed here

- Unity engine / UnityPlayer / `Assembly-CSharp.dll` — property of Unity Technologies and Illusion.
- VR カノジョ game data (`VR_Kanojo_Data/**`, `abdata/**`, configuration and save files).
- Intel XeSS SDK binaries — only referenced in the docs as "not viable for this project".
- ManlyMarco's *Play Without VR* plugin binary — superseded by `VRK_FlatFPS`; the original file in
  the game folder is renamed to `VRK_PlayWithoutVR.dll.disabled` by the installer, never copied here.

## Trademarks

VR カノジョ / VR Kanojo is a trademark of its respective owner. BepInEx, UnityDoorstop, AMD
FidelityFX, Intel XeSS and Unity are trademarks of their respective owners. This project is an
unofficial community tool and is not affiliated with, endorsed by, or supported by any of them.

---

# 第三方组件说明（中文）

VRK Enhance 本身是 MIT 许可（见 [LICENSE](LICENSE)）。仓库不含任何 Illusion 游戏资源、Unity 引擎二进制或
Intel XeSS SDK 代码。

发布包里附带了几个第三方组件，目的是让整套工具能离线安装。**全部原样分发，未做任何修改。**

| 组件 | 版本 | 许可证 | 位置 |
| --- | --- | --- | --- |
| BepInEx（core / preloader / HarmonyX / MonoMod） | 5.4.23.6 x64 | **MIT**（© 2018 Bepis） | `payload/bepinex/BepInEx_win_x64_5.4.23.6.zip` |
| UnityDoorstop（`winhttp.dll` 等） | 4.6.0 | **LGPL-2.1** | 同一个 zip |
| Mono.Cecil | 0.11.x | **MIT** | `fixtures/Mono.Cecil.dll`（仅构建/校验用，游戏不加载） |
| FidelityFX FSR 1.0 头文件 | FSR 1.0 | **MIT**（© 2021 AMD） | `plugins/VRK_UpScale/native/` |
| 准星贴图 | — | **本项目自绘**（`fixtures/make_crosshair.py`，MIT） | `plugins/VRK_FlatFPS` 内嵌资源（128×128 RGBA） |

**LGPL-2.1（只涉及 UnityDoorstop）是怎么满足的**：① 许可证全文随包（`payload/bepinex/LICENSE-UnityDoorstop-LGPL-2.1.txt`）；
② 对应源码就是上游 tag `v4.6.0`（我们没打补丁，上游源码＝交付源码）；③ `winhttp.dll` 是独立文件、由游戏按名字加载，
任何人可以删掉或换成别的构建，我们的代码完全不依赖它的具体实现（这就是"可重新链接"在 DLL 形态下的等价物）；
④ 我们的 MIT 代码不额外附加限制，安装器把它当普通负载复制/删除，卸载 BepInEx 是被支持的路径。

也可以自己准备：从 <https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6> 下载同名 zip
（sha256 `6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755`）放进 `payload/bepinex/` 即可。
