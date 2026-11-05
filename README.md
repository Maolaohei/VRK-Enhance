# VRK Enhance

**An enhancement kit for *VR Kanojo* (Illusion, Unity 5.4.5f1).**
Run it without a headset, upscale it with FSR1, fix a stock crash, unlock content — plus a one-click installer with a graphical panel and a config editor.

> 中文版见 [README.zh-CN.md](README.zh-CN.md)。

---

## What it does

Three layers, each independent:

| Layer | Contents | Who needs it |
| --- | --- | --- |
| **Launch / compatibility** | `VR_Kanojo_fix.exe` (fixes the stock SHA-1 crash by patching two instructions in memory), SteamVR preflight, no-VR entry, VR entry | Everyone — without the fix the game crashes at random while loading |
| **Enhancement** | No-VR core (`VRK_FlatFPS`), FSR1 upscaling (`VRK_UpScale` + `vrkup.dll`), frame pacing (`VRK_Perf`), loading prewarm (`VRK_PreWarm`), texture boost (`VRK_TexPlus`), content unlock (`VRK_Unlock`) | Most people |
| **Tooling** | Installer panel, config editor, one-click install/restore scripts, verified builds | Anyone who would rather not touch files by hand |

Everything is **out-of-tree**: game files are never modified. Each component can be removed again by name, and every file the installer overwrites is backed up first.

## Why you might want it

- **"It will not even start."** The stock `VR_Kanojo.exe` carries a register-allocation bug inside UnityPlayer's SHA-1 routine and dies with `0xC0000005` / `0xC0000374` at random. The launcher patches the two instructions in memory (`lea rax,` → `lea r10,`, `cmovne rsi, rax` → `cmovne rsi, r10`) and leaves the exe on disk untouched.
- **"I do not have a headset."** Without a VR device the game lands on a white screen: an opaque `VRCameraFade` panel whose fade coroutine never runs. The no-VR core clears that panel, plants the eye at a sane height and wires WASD + mouse into the game's own camera controller.
- **"It freezes for a minute, then hangs."** Unity's OpenVR backend waits on `vrclient_x64.dll` before BepInEx is even loaded — about 100 s when SteamVR is not running. The no-VR entry disables `openvr_api.dll`; the VR entry preflights SteamVR and refuses to launch instead of hanging.
- **"It looks soft at 1440p."** FSR1 (EASU + RCAS) runs as a native D3D11 plugin at a 0.85 render scale — about 28 % fewer pixels for the GPU, sharpened on the way to the backbuffer.
- **"Some content is locked."** The unlock component calls the game's own `ALLClearData()` and lets the game write the save itself.

## How well it works

- **Verified builds.** Six plugins with five standalone IL-level verifiers plus a startup-safety scanner: FlatFPS `ALL CHECKS PASSED`, UpScale `81/81`, TexPlus `ALL CHECKS PASSED`, Unlock `33/33`, PreWarm `50/50`, Perf `ALL CHECKS PASSED`, AwakeCheck `banned=0`. Run `verify.cmd`.
- **Runtime evidence, not guesses.** FSR1 logs `native pass is live: 2176x1224 -> 2560x1440` every frame; the fade watchdog logs `cleared opaque fade panel at Awake (was alpha=1.00)`; the camera logs `eye at 1.28 m`; the input path logs `IME detached (prev context=…)` exactly once.
- **Known limits.**
  - No FSR2 / FSR3. Both are temporal: they need a velocity buffer and a jittered projection, Unity 5.4 has neither, and correctness could only be judged by eye on a real machine.
  - The native super-resolution layer ships **off** (`NativePass = false`). A heap-corruption crash (`0xC0000374`) was traced to render-target lifetime in that layer; until it is re-verified, RenderScale mode relies on Unity's own upscale.
  - Replacing the female model with an uncensor pack shifts the skirt mesh (those packs carry one SkinnedMeshRenderer less than the original `cf_base`). The kit does not touch it; set `Female uncensor = None` if you care.
  - In VR mode every plugin disables itself — the game runs fully native.
  - Steam or a plain double-click of `VR_Kanojo.exe` still hits the stock crash. Always launch through the provided entries.

## Install

**Easiest:** extract the release, run the installer panel (`安装面板.cmd`), tick what you want, press *Install selected*. The panel finds the game folder by itself (registry → Steam libraries → common paths) and forwards everything to `setup.ps1`.

Scripted equivalents, run from the package folder:

```
一键安装.cmd      interactive menu, pick components
安装推荐.cmd      core + upscale + perf + prewarm + textures + launcher
安装全部.cmd      everything
一键还原.cmd      undo what the installer did
还原全部.cmd      same, without questions
```

Command line:

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -List
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Components all -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Components flatfps,upscale -Yes
powershell -NoProfile -ExecutionPolicy Bypass -File tools\setup.ps1 -Mode restore -Yes
```

Switches: `-GameDir <path>`, `-DryRun`, `-Yes`, `-NoShortcut`, `-ShortcutDir <path>`, `-Lang en|zh|auto`. Default language follows the Windows UI language (`VRK_LANG` overrides it).

Component table, restore semantics and troubleshooting: [docs/INSTALL.md](docs/INSTALL.md).

### Running the game

| Entry | What it does |
| --- | --- |
| `启动_免VR模式.bat` *(no-VR)* | Disables `openvr_api.dll`, launches through the SHA-1 fix. All plugins active. |
| `启动_VR模式.bat` *(VR)* | Checks SteamVR (starts it if needed, 90 s timeout), enables `openvr_api.dll`, launches, restores it on exit. |

The installer can drop both as desktop shortcuts when the launcher component is selected.

## Configure

The config panel (`配置面板.cmd`) edits every key of the six `BepInEx/config/vrk.*.cfg` files and writes a `.bak` per save. Defaults and their meaning: [docs/CONFIG.md](docs/CONFIG.md). Key map: [docs/KEYS.md](docs/KEYS.md).

The three you will actually touch:

```ini
[Camera]
EyeHeight = 1.28        ; no-VR eye height in metres (PageUp/PageDown change it live)
MouseSpeed = 800        ; 800 ≈ 16°/100px
[Visual]
GazeRingMode = Aim      ; the ring sticks to whatever you look at
[Input]
InteractKey = E
```

## Repository layout

```
VRK-Enhance/
├─ 安装面板.cmd / 配置面板.cmd / 一键安装.cmd / 一键还原.cmd ...
├─ tools/        setup.ps1 (install/restore), gui.ps1, configgui.ps1, i18n.ps1
├─ payload/      prebuilt DLLs, vrkup.dll, default configs (shipped with releases)
├─ plugins/      plugin sources + one verifier project each
├─ tools/        AwakeCheck, PerfAudit, debug helpers
├─ launcher/     VR_Kanojo_fix.exe, its C source, the two entry bats
├─ fixtures/     Mono.Cecil.dll, crosshair_128.bin (build inputs)
└─ docs/         INSTALL / CONFIG / KEYS (English) · zh-CN/ (Chinese)
```

## Build from source

```
build.cmd          six managed plugins -> dist\
build-native.cmd   vrkup.dll (needs g++)
build-shaders.cmd  HLSL -> C arrays (needs fxc)
verify.cmd         every verifier + AwakeCheck
```

Needs Windows, .NET SDK 8+ and the game's `Managed` folder for references; the native and shader steps need MinGW g++ and `fxc.exe` from the Windows SDK. Nothing is committed to git automatically.

## License

MIT — see [LICENSE](LICENSE). Third-party: FSR1 headers (MIT, AMD), Mono.Cecil (MIT), Harmony (MIT), BepInEx (LGPL-2.1).

## Disclaimer

Unofficial fan project, not affiliated with Illusion or Steam. Back up your saves (`UserData/save/`). The launcher patches the running process only; `VR_Kanojo.exe` on disk keeps its original hash.
