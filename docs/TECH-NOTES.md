# Technical notes

Short English index of the逆向 findings this kit is built on. The long form (Chinese, with full log excerpts and disassembly) is in [zh-CN/TECH-NOTES.md](zh-CN/TECH-NOTES.md).

---

## 1. Stock crash: SHA-1 register allocation in UnityPlayer

`VR_Kanojo.exe+0x98BD0A` does `mov %rsp,%rax` and keeps the frame base in `rax`, spilling xmm6…xmm9 at `[rax-0x48]`…`[rax-0x18]`. The SHA-NI SHA-1 loop then does `lea 0x40(%rsi),%rax` (0x98BD83) — it overwrites the frame base. The epilogue reads `movaps -0x48(%rax),%xmm6` (0x98BFA3) and finally `mov %rax,%rsp` (0x98BFB5).

Two outcomes, both observed:

- `rsi-8` unmapped → `0xC0000005` at `VR_Kanojo.exe+0x98BFA3` (ten crash dumps, access address `0xFFFFFFFFFFFFFFFF`).
- `rsi-8` readable → no exception, but `rsp` is then pointed into heap data; `ret` pops a bogus return address → `msvcrt.dll+0x60558` or `STATUS_HEAP_CORRUPTION` (`0xC0000374`).

The loop is only taken for inputs longer than 64 bytes, which is why it looks random. It fires from Unity's own AssetBundle/resource hashing. Patch (`VR_Kanojo_fix.exe`, in memory only): `lea rax,` → `lea r10,` and `cmovne rsi, rax` → `cmovne rsi, r10`, restoring what the compiler should have emitted.

## 2. White screen: an opaque fade panel with a stuck coroutine

`VRKanojo.Utils.VRCameraFade` instantiates `Prefabs/Fader`, whose panel is `Image` with serialized colour `(1,1,1,1)` and `m_FadeInOnStart = m_FadeInOnSceneLoad = false`. Only `FadeIn()`/`FadeOut()` clear it, by running `BeginFade`, which sets `m_IsFading = true`, lerps white → transparent over `m_FadeDuration` (2 s) and only then sets `m_IsFading = false`.

`BeginFade` is an iterator: kill it (a `SetActive(false)/(true)` toggle, a disabled host, `timeScale = 0`) and `m_IsFading` stays `true` forever. Every later `FadeIn` returns immediately, the panel stays white, the game keeps playing audio behind it.

Fix: clear the alpha in an `Awake` postfix (root cause), plus a watchdog that notices an unchanged alpha and forces `m_IsFading = false`, and after two repairs disables the panel for the session.

## 3. ~100 s startup stall: OpenVR before BepInEx

`openvr_api.dll` (in `VR_Kanojo_Data/Plugins`) loads `vrclient_x64.dll`, which waits for `vrserver`. This happens in Unity's graphics init, i.e. **before** the BepInEx preloader runs, so no plugin can fix it. Stack from a manual minidump (`MiniDumpWriteDump`):

```
ntdll+0xC8889 ← KERNELBASE+0x73F1E ← VCRUNTIME140+0x1CE01
  ← vrclient_x64.dll (a dozen frames) ← ucrtbase+0x33B39
  ← vrclient_x64+0x4A0000 ← openvr_api.dll+0x1D72
  ← VR_Kanojo.exe+0x6FB613 ← VR_Kanojo.exe+0x104C828
```

Windows flags "not responding" after 5 s without a message pump, hence the impression of a hang. The no-VR entry renames `openvr_api.dll` → `.disabled`; the VR entry preflights SteamVR and starts it (`steam://rungameid/250820`) with a 90 s timeout.

## 4. Crash before the window: `SystemInfo` at plugin `Awake`

BepInEx plugin `Awake` runs before the graphics backend is initialised. `SystemInfo.graphicsDeviceType` reaches an icall thunk (`VR_Kanojo.exe+0x3750A0`: `call 0x1402048A0` → `mov 0x11CC(%rax),%eax`) whose singleton (`0x141322E08`) is still null → `0xC0000005`, no window ever appears. WER then holds the corpse for ~2 min, which looks like a freeze.

Rule applied in every plugin: **no engine subsystem access in `Awake`**; the checks moved into a coroutine two frames later. `tools/AwakeCheck` enforces it (it fails the build if `Awake` touches `SystemInfo`/`VRDevice`/`QualitySettings`/`Shader`/`Material`/`Texture`/`Camera`/…).

## 5. Camera model

The game's no-VR camera is `IllusionCamera` (`LateUpdate` → `transform.position = CalcPos()`), a third-person orbit rig with `distance = 1.5`. With `ForceMouse_` set, `Manager.Scene.InitCameraRig()` attaches it to the main camera **and** to `CameraRig`; the rig's Y is then written to 0 by `SetCameraRigPos` when the tracking origin is `FloorLevel`, so the eye ends up on the floor without a headset (the fix: `EyeHeight`, applied to `OVRCameraRig.trackingSpace`).

First person turns the **rig** for yaw and the **camera** for pitch; the game's menus and props are placed relative to the rig, so turning only the camera makes them appear in the wrong direction.

Screen-centre → world mapping uses `ViewportPointToRay(0.5, 0.5)` rather than `Input.mousePosition` because the pointer is locked during play.

## 6. Interaction chain (why a mouse cannot click anything)

| Layer | Reality |
| --- | --- |
| Menus | `OVRRaycaster` only — no `GraphicRaycaster`, and the pointer is a VR hand ray |
| `IllusionCamera.OnUI` | `EventSystem.current.IsPointerOverGameObject()`; once true, `dragging` never starts and the mouse stops working |
| Touch | `VK_Input.H_ActStart()` / `GazeTouchIn_L()`: `Input.GetMouseButtonDown(0)` — a separate path from `CheckDecision()` |
| Touch gate | `BodyTouchManager.Update_GazeCaress()` requires `enableGaze` **and** `controlType == 0` (the shipped config has `1`) |
| Grab reach | `CheckGazeTouch()` hardcodes `1.5f`, and the third-person camera sits exactly 1.5 m away |
| Icons on the floor | `TimeIconControl` fires on *gaze*, via an animation event — no key at all |

Hence: force `controlType = 0`, force `enableGaze`, rewrite that `1.5f` via a transpiler, drive the hand with the left button (teleport + `GrabBegin`), and keep three independent click paths (UI rectangle, physics raycast + `ExecuteEvents`, `InteractiveProc`).

## 7. FSR1 pipeline (`vrkup.dll`)

`IUnityGraphicsD3D11` is registered by Unity 5.4.5 (found the interface GUID load at `VR_Kanojo.exe+0x1F2D09`), but the plugin does not depend on the callback: it takes `ID3D11Device` from the texture Unity hands to `OnRenderImage` (`ID3D11Resource::GetDevice`, which returns `void`).

Per frame: save the full D3D11 state (RTV/DSV, viewport, input layout, topology, shaders, sampler, SRV, CB, rasterizer/blend/depth-stencil, blend factor, sample mask, stencil ref), run EASU into an owned target, run RCAS into the backbuffer, restore.

Two details that cost a lot of debugging:

- `OnRenderImage` receives `dest == null` for the backbuffer, and `RenderTexture.GetTemporary` then returns a texture whose `GetNativeTexturePtr()` is **0**. Own the target instead (`new RenderTexture(...).Create()`), and force `Camera.targetTexture` + a presenter camera for RenderScale mode (Unity does not apply `_Scale` to `OnRenderImage` on its own).
- The native layer is shipped **off** (`NativePass = false`): a `0xC0000374` heap corruption traced to destroying a render target inside `OnRenderImage`. `1.6.x` stopped destroying, but it stays off until re-verified.

## 8. Misc findings

- `OVRManager.Update()` resets `QualitySettings.antiAliasing` to `display.recommendedMSAALevel` every frame when `useRecommendedMSAALevel` is on — silently dropping 4× MSAA to 0. `VRK_Perf` pins the flag off by reflection.
- `OVRHapticsClip..ctor` computes `Samples.Length / OVRHaptics.Config.SampleSizeInBytes`; the sample size is 0 without a headset → `DivideByZeroException` inside `Hand.OnTriggerStay`, which also killed the touch reaction that follows it.
- The female uncensor packs replace `abdata/chara/cf_base.unity3d` and carry ~11 external references to a `cf_base.resource` that does not exist (FMOD "file not found" ×11), plus one SkinnedMeshRenderer less than the original (102 → 101) — which is why the skirt mesh ends up in odd places.
- `Manager.Game.Save()` layout: `int 100`, `int playCount`, `string playDateTime`, 4 bools, 15 ints, 5 bools, 3 ints, `allHInfo`, `storyPlayDict_`, `interactivePlayDict_`, `customIDInfo`. `lessonDict_`/`pageMax`/`storyListDict_`/`interactiveListDict_` are rebuilt at runtime, not serialized — so the unlock calls `ALLClearData()` and lets the game write it.
- Unity 5.4 loads scenes synchronously: `Init → Init2 → Logo → Logo2 → Attention → Title` takes 20–30 s with 5–9 s gaps between scenes, which Windows reports as "not responding". This is normal, not a bug.
