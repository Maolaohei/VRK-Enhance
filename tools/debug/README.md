# tools/debug/ — 排障脚本

这些不是插件的一部分，是定位问题用的独立小工具。都不需要游戏在跑。

| 文件 | 用途 |
| --- | --- |
| `dmpstack.py` | 解析 Windows **minidump**，按"栈里 VR_Kanojo.exe 帧数"找主线程，输出 `模块+偏移` 栈帧。依赖 `ModuleListStream(4)` / `ThreadList(3)` / `Memory64List(9)`；x64 `CONTEXT` 的 `Rip=0xF8`、`Rsp=0x98`。用法：`python dmpstack.py <file.dmp>` |
| `dmpinfo.py` | 只读 `ExceptionStream(6)` + `ModuleListStream(4)`，打印异常码、异常地址落在哪个模块的哪个偏移、`NumberParameters` 与参数值。判 `0xC0000005` 的读写地址、判 `0xC0000374` 直接看码。 |
| `pngsample.py` | 纯 `zlib` 解 PNG，采样四角/四边/中心像素。用来判定"白屏"到底是不是纯色、是不是渐变（不要装图像库）。 |
| `dump_game.ps1` | 对 `VR_Kanojo.exe` 主动抓 full dump（`MiniDumpWithFullMemory` 等 0x1129 组合），走 P/Invoke `MiniDumpWriteDump`。 |
| `stallwatch.ps1` | 只在 `Responding=False` 时才抓 dump 的看门狗 —— 抓"未响应"现场必须用它，否则抓到的都是正常运行态。 |

## 关于 WER 的坑

`HKCU\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\VR_Kanojo.exe` 建好 `DumpFolder` / `DumpType=2` / `DumpCount` 也不一定有用：**落盘是由服务 `WerSvc` 执行的**，它停了就什么都不产出（`Get-Service WerSvc` 一看便知），而起服务需要管理员权限。这也是为什么这里选择自己 P/Invoke 抓 dump。
