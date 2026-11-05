# The 100-second "not responding" at startup

**Symptom.** After launching, nothing happens for one to two minutes. The window title says "not responding", the CPU stays near idle, and once it finally appears everything works fine.

**Cause.** `VR_Kanojo_Data/Plugins/openvr_api.dll` loads `vrclient_x64.dll`, which waits for the SteamVR runtime (`vrserver.exe`). That happens during Unity's graphics initialisation — **before** the BepInEx preloader runs, so no plugin can intercept it. Stack from a manual minidump taken while the process was unresponsive (pid 9748, 100 s in, `Responding = False`, CPU 1.4 s):

```
ntdll+0xC8889  ← KERNELBASE+0x73F1E  ← VCRUNTIME140+0x1CE01
  ← vrclient_x64.dll (a dozen frames)  ← ucrtbase+0x33B39
  ← vrclient_x64+0x4A0000  ← openvr_api.dll+0x1D72
  ← VR_Kanojo.exe+0x6FB613  ← VR_Kanojo.exe+0x104C828
```

Windows marks a window "not responding" after 5 s without pumping messages, so the wait looks like a crash or a freeze. It is neither — the process is simply blocked in a synchronous call.

**Fix, depending on your mode.**

| Mode | What the entry does |
| --- | --- |
| No VR | Renames `VR_Kanojo_Data/Plugins/openvr_api.dll` → `openvr_api.dll.disabled` before launching, restores nothing (`启动_免VR模式.bat`, or the `openvr` component) |
| VR | Checks `vrserver.exe`; if it is not running it starts SteamVR (`steam://rungameid/250820`) and polls for up to **90 s**; if SteamVR never appears it exits with code 2 instead of launching a game that would hang |

Both entries go through `VR_Kanojo_fix.exe`, which also applies the SHA-1 patch (see [TECH-NOTES.md](TECH-NOTES.md) §1).

**If you still see the stall:** you launched `VR_Kanojo.exe` directly (or via Steam). The patch and this rename only happen through the two provided entries.

**What the stall is not.** It is not the FSR1 layer, not the prewarm, and not the scene loading itself. Scene loading in Unity 5.4 is synchronous (`Init → Init2 → Logo → Logo2 → Attention → Title`, 20–30 s total, 5–9 s gaps) and produces additional short "not responding" flickers; `VRK_Perf` only shortens those, it does not remove the synchronous design.
