# launcher/

两个 `.bat` 入口 + `VR_Kanojo_fix.exe`（内存补丁启动器）。

## 为什么必须有它

UnityPlayer 5.4.5f1 的 SHA-1 例程有寄存器分配 bug（详见 [`../docs/TECH-NOTES.md`](../docs/TECH-NOTES.md) 第 1 节）：

```
+0x98BD0A  mov  %rsp,%rax        ; 帧基准进了 RAX
+0x98BD83  lea  0x40(%rsi),%rax  ; 主循环把 RAX 当数据指针踩掉
+0x98BFA3  movaps -0x48(%rax),%xmm6
+0x98BFB5  mov  %rax,%rsp        ; 连栈指针都从被踩的 RAX 恢复
```

不修就是随机 `0xC0000005` / `0xC0000374`。启动器在 `CREATE_SUSPENDED` 状态下改两条指令（`rax → r10`，语义等价），**只改进程内存，不动磁盘文件**，所以文件校验与 Steam 完整性检查都不受影响。

## 文件

| 文件 | 说明 |
| --- | --- |
| `启动_免VR模式.bat` | 把 `VR_Kanojo_Data/Plugins/openvr_api.dll` 改名 `.disabled`（消灭 ~100 秒 SteamVR 阻塞）→ 启动游戏 → 退出后改名回来 |
| `启动_VR模式.bat` | 先检测 `vrserver.exe`，没起就用 `steam://rungameid/250820` 拉起 SteamVR 并最多等 90 秒；起来了才恢复 `openvr_api.dll` 并启动游戏；退出后重新改名 |
| `VR_Kanojo_fix.exe` | 实际的内存补丁启动器（301 KB，MinGW 构建） |
| `src/fix_launcher.c` | 同一行为的 **清理室重写** 源码，可自行编译验证 |

两个 `.bat` 都支持 `set VRK_DRY=1` 干跑：只打印将要做什么，不启动任何东西、不改任何文件名。想确认逻辑是否正常时先这么跑一遍。

## 自己编译启动器

```bat
rem MinGW-w64
gcc -O2 -m64 -o VR_Kanojo_fix.exe src/fix_launcher.c

rem 或 MSVC
cl /O2 src/fix_launcher.c
```

编译出来的 exe 必须和 `VR_Kanojo.exe` 放在同一目录（它按自身所在目录找游戏）。

## 日志

启动器会在同目录写 `VR_Kanojo_fix.log`：

```
=== VRKanojo-Fix start ===
cmdline: "E:\...\VR_Kanojo.exe"
pid=14720
imageBase=%p
patched sha1 lea rax->r10 at %p
patched sha1 cmovne rax->r10 at %p
applied 2/2 patches
exit: wait=0x0 code=0x0 (0)
```

- `applied 2/2 patches` —— 补丁成功
- `bytes mismatch at %p` —— 游戏版本不是 5.4.5f1（偏移变了），启动器**不会**盲改，只报警
- `exit: wait=0x102 code=0x103 (259)` —— 启动器自己等超时（`WAIT_TIMEOUT`），游戏仍在运行（`STILL_ACTIVE`）
- `exit: code=0xc0000005` / `0xc0000374` —— 游戏本体崩了，看 `docs/TECH-NOTES.md` 排查

## 注意

- **不要**直接双击 `VR_Kanojo.exe` 或从 Steam 启动：绕过启动器就没有那个内存补丁，SHA-1 bug 依旧在
- 两个 `.bat` 是纯 ASCII + CRLF 写的；如果你用编辑器改过，注意别让 Windows 把它变成只有 LF（`cmd` 会读错行）
