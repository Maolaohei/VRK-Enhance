# Changelog

Component versions shipped in the current release:

| Component | Version |
| --- | --- |
| `VRK_FlatFPS` — no-VR core | 1.41.0 |
| `VRK_UpScale` — FSR1 | 1.7.0 |
| `VRK_Perf` — frame pacing | 1.1.0 |
| `VRK_PreWarm` — loading prewarm | 1.1.1 |
| `VRK_TexPlus` — textures | 1.3.0 |
| `VRK_Unlock` — content unlock | 1.0.0 |
| `VR_Kanojo_fix.exe` — launcher | SHA-1 patch + SteamVR preflight |
| installer `setup.ps1` / panel `gui.ps1` / editor `configgui.ps1` | 1.6.0 |

Deep, per-version detail lives in the Chinese notes: [zh-CN/CHANGELOG.md](zh-CN/CHANGELOG.md).

---

## v1.1.0 — bilingual tooling

- Installer, panel and config editor are bilingual: `-Lang en|zh|auto` (default = Windows UI language, `VRK_LANG` overrides). All strings go through `tools/i18n.ps1`.
- Docs split into English (`docs/`) and Chinese (`docs/zh-CN/`); README rewritten around *what / why / how well*.
- `payload/config/vrk.unlock.cfg` regenerated without the stray un-commented `Default value:` lines.

## v1.0.0 — first release

- Launcher: fixes the stock UnityPlayer SHA-1 register bug in memory (`lea rax,` → `lea r10,`, `cmovne rsi, rax` → `cmovne rsi, r10`), two entries (no-VR / VR), SteamVR preflight with a 90 s timeout, `VRK_DRY=1` dry run.
- `VRK_FlatFPS`: white screen root cause (opaque `VRCameraFade` panel + stuck `m_IsFading`), eye height, WASD/digits key remap, first/third/free camera, gaze ring that snaps to the aim point, mouse-hand interaction, IME detach, `E` interaction, help overlay with toast.
- `VRK_UpScale` + `vrkup.dll`: FSR1 EASU+RCAS as a native D3D11 plugin, RenderScale 0.85, present pass, format normalisation, persistent render target.
- `VRK_Perf`: vsync off, no frame cap, loading-phase GC, `OVRManager` MSAA pinning.
- `VRK_PreWarm`: shader/material/texture prewarm with per-frame budgeting.
- `VRK_TexPlus`: full resolution, 16× anisotropic, trilinear, per-frame texture budget.
- `VRK_Unlock`: calls the game's own `ALLClearData()` and lets it write the save.
- Installer: nine components, per-component install/restore, `state/manifest.json` bookkeeping, `state/backup/` rollback, desktop shortcuts, graphical panel, config editor.

## Fixes worth calling out

| Symptom | Cause | Fixed in |
| --- | --- | --- |
| Random crash while loading (`0xC0000005`, `0xC0000374`) | Stock register-allocation bug in UnityPlayer's SHA-NI SHA-1 loop; `rax` held the frame base and got clobbered | launcher |
| Permanent white screen with audio | `VRCameraFade` panel is opaque white and `m_IsFading` never clears once its coroutine is killed | FlatFPS 1.4.x |
| ~100 s "not responding" before the window appears | Unity's OpenVR backend blocks on `vrclient_x64.dll` before BepInEx loads | launcher / `openvr` component |
| Crash at startup, before the window exists | Plugin `Awake` touched `SystemInfo.graphicsDeviceType` while the graphics backend was still null | FlatFPS / UpScale 1.1+ |
| Menus unreachable with a mouse | The game only raycasts VR hands (`OVRRaycaster`) | FlatFPS 1.8+ / 1.9 |
| Left click swallowed | Legacy UI bridge made `IllusionCamera.OnUI` permanently true | FlatFPS 1.21 |
| Double clicks | Game already converted the mouse to decisions; the plugin fired them again | FlatFPS 1.41 |
| Ring stuck in the centre | A `Prefix` skipped `OVRGazePointer.Update`, so the ring never moved | FlatFPS 1.20+ |
| `DivideByZeroException` on touch | `OVRHapticsClip` divides by `SampleSizeInBytes`, which is 0 without a headset — and the exception killed the touch reaction | FlatFPS 1.35 |
| Click/aim offset at 0.85 render scale | Game and UI expect target-texture pixels, not screen pixels | FlatFPS 1.34 |
| Heap corruption after enabling native upscaling | Render target destroyed inside `OnRenderImage` | UpScale 1.6.x (native layer shipped off) |
