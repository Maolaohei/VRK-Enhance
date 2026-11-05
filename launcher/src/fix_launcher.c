/*
 * fix_launcher.c - VR Kanojo UTF-8/VR fix launcher (clean-room reimplementation)
 *
 * WHY THIS EXISTS
 * ---------------
 * UnityPlayer 5.4.5f1 on x64 ships a SHA-1 (SHA-NI) routine whose compiler
 * register allocation is broken: the function keeps the original RSP in RAX
 * as a frame base (mov %rsp,%rax at +0x98BD0A) but the SHA-NI main loop later
 * reuses RAX as a data pointer (lea 0x40(%rsi),%rax at +0x98BD83), and the
 * epilogue reads its saved XMM registers through that clobbered RAX
 * (movaps -0x48(%rax),%xmm6 at +0x98BFA3) and finally restores RSP from it
 * (mov %rax,%rsp at +0x98BFB5).
 *
 *   - if `rsi-8` lands in unmapped memory -> immediate 0xC0000005 at
 *     VR_Kanojo.exe+0x98BFA3 (access address 0xFFFFFFFFFFFFFFFF);
 *   - otherwise the stack pointer is redirected into heap data and the
 *     `ret` pops garbage: crashes elsewhere (msvcrt.dll, 0xC0000374 heap
 *     corruption, silent exits).
 *
 * A crash only happens on inputs longer than one 64-byte SHA block, which is
 * why the game "usually works" and then dies while loading an asset bundle.
 *
 * THE FIX
 * -------
 * Patch the two instructions so the loop uses R10 (a scratch register) and
 * leaves RAX (the frame base) alone:
 *
 *   +0x98BD83  48 8D 46 40    lea    rax,[rsi+0x40]   ->  4C 8D 56 40  lea r10,[rsi+0x40]
 *   +0x98BD8B  48 0F 45 F0    cmovne rsi,rax          ->  49 0F 45 F2  cmovne rsi,r10
 *
 * This is what the compiler should have emitted; it changes no semantics.
 *
 * HOW
 * ---
 * Start the game suspended, resolve the main module base address through
 * Toolhelp32, verify the original bytes (don't blindly patch), write the
 * patched bytes with VirtualProtectEx/VirtualProtect, then resume and wait.
 * Nothing on disk is modified: the patch lives in the process' memory only,
 * so the game's anti-tamper hash check and Steam file validation stay happy.
 *
 * BUILD
 *   x86_64-w64-mingw32-gcc -O2 -o VR_Kanojo_fix.exe fix_launcher.c
 *   (MSVC: cl /O2 fix_launcher.c)
 *
 * This file is MIT licensed. It contains no code from Unity or Illusion.
 */

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>

#define MAX_PATH_LONG 32768

typedef struct PatchSite {
    DWORD  rva;            /* offset inside VR_Kanojo.exe */
    BYTE   expect[4];      /* bytes we require before patching */
    BYTE   replace[4];     /* bytes we write */
    const char *label;
} PatchSite;

static PatchSite g_sites[] = {
    { 0x98BD83, { 0x48, 0x8D, 0x46, 0x40 }, { 0x4C, 0x8D, 0x56, 0x40 }, "sha1 lea rax->r10" },
    { 0x98BD8B, { 0x48, 0x0F, 0x45, 0xF0 }, { 0x49, 0x0F, 0x45, 0xF2 }, "sha1 cmovne rax->r10" },
};

static FILE *g_log = NULL;

static void logf_(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    if (g_log) { vfprintf(g_log, fmt, ap); fputc('\n', g_log); fflush(g_log); }
    vfprintf(stdout, fmt, ap);
    fputc('\n', stdout);
    fflush(stdout);
    va_end(ap);
}

/* Directory that contains this executable, with a trailing backslash. */
static void exe_dir(char *out, size_t cch)
{
    DWORD n = GetModuleFileNameA(NULL, out, (DWORD)cch);
    if (n == 0 || n >= cch) { out[0] = 0; return; }
    for (DWORD i = n; i > 0; i--) {
        if (out[i - 1] == '\\' || out[i - 1] == '/') { out[i] = 0; return; }
    }
    out[0] = 0;
}

/* Base address of the first module of a process (i.e. the image we launched). */
static ULONG_PTR main_module_base(DWORD pid)
{
    ULONG_PTR base = 0;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snap == INVALID_HANDLE_VALUE) return 0;

    MODULEENTRY32 me;
    me.dwSize = sizeof(me);
    if (Module32First(snap, &me)) base = (ULONG_PTR)me.modBaseAddr;
    CloseHandle(snap);
    return base;
}

/*
 * Write `len` bytes at `addr` in the target process.
 * Uses PAGE_EXECUTE_READWRITE for the duration of the write and restores the
 * previous protection afterwards; returns 1 on success.
 */
static int write_bytes(HANDLE proc, ULONG_PTR addr, const BYTE *data, DWORD len)
{
    DWORD oldProt = 0;
    if (!VirtualProtectEx(proc, (LPVOID)addr, len, PAGE_EXECUTE_READWRITE, &oldProt)) {
        logf_("VirtualProtectEx(RWX) failed: %lu", GetLastError());
        return 0;
    }
    SIZE_T written = 0;
    BOOL ok = WriteProcessMemory(proc, (LPVOID)addr, data, len, &written);
    DWORD werr = GetLastError();
    DWORD tmp = 0;
    VirtualProtectEx(proc, (LPVOID)addr, len, oldProt, &tmp);
    if (!ok || written != len) {
        logf_("WriteProcessMemory failed: ok=%d written=%llu err=%lu",
              (int)ok, (unsigned long long)written, werr);
        return 0;
    }
    return 1;
}

/* Read `len` bytes from the target process; returns 1 on success. */
static int read_bytes(HANDLE proc, ULONG_PTR addr, BYTE *out, DWORD len)
{
    SIZE_T got = 0;
    if (!ReadProcessMemory(proc, (LPVOID)addr, out, len, &got) || got != len) return 0;
    return 1;
}

int main(int argc, char **argv)
{
    char dir[MAX_PATH_LONG];
    char exe[MAX_PATH_LONG];
    char logpath[MAX_PATH_LONG];
    char cmdline[MAX_PATH_LONG];

    exe_dir(dir, sizeof(dir));
    _snprintf(exe, sizeof(exe), "%sVR_Kanojo.exe", dir);
    _snprintf(logpath, sizeof(logpath), "%sVR_Kanojo_fix.log", dir);
    g_log = fopen(logpath, "w");

    logf_("=== VRKanojo-Fix start ===");
    logf_("launcher dir: %s", dir);

    if (GetFileAttributesA(exe) == INVALID_FILE_ATTRIBUTES) {
        logf_("cannot find %s", exe);
        return 1;
    }

    /* Allow passing extra args straight through to the game. */
    if (argc > 1) {
        _snprintf(cmdline, sizeof(cmdline), "\"%s\"", exe);
        for (int i = 1; i < argc; i++) {
            size_t used = strlen(cmdline);
            _snprintf(cmdline + used, sizeof(cmdline) - used, " %s", argv[i]);
        }
    } else {
        _snprintf(cmdline, sizeof(cmdline), "\"%s\"", exe);
    }
    logf_("cmdline: %s", cmdline);

    STARTUPINFOA si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));

    /* CREATE_SUSPENDED so we can patch before UnityPlayer runs a single byte. */
    if (!CreateProcessA(NULL, cmdline, NULL, NULL, FALSE,
                        CREATE_SUSPENDED, NULL, dir, &si, &pi)) {
        logf_("CreateProcess failed: %lu", GetLastError());
        return 1;
    }
    logf_("pid=%lu", pi.dwProcessId);

    ULONG_PTR base = main_module_base(pi.dwProcessId);
    if (!base) {
        logf_("cannot resolve module base: %lu", GetLastError());
        TerminateProcess(pi.hProcess, 1);
        return 1;
    }
    logf_("imageBase=%p", (void *)base);

    int applied = 0;
    for (size_t i = 0; i < sizeof(g_sites) / sizeof(g_sites[0]); i++) {
        PatchSite *s = &g_sites[i];
        ULONG_PTR addr = base + s->rva;
        BYTE cur[4];
        if (!read_bytes(pi.hProcess, addr, cur, 4)) {
            logf_("cannot read %p: %lu", (void *)addr, GetLastError());
            continue;
        }
        if (memcmp(cur, s->expect, 4) == 0) {
            if (write_bytes(pi.hProcess, addr, s->replace, 4)) {
                logf_("patched %s at %p", s->label, (void *)addr);
                applied++;
            }
        } else if (memcmp(cur, s->replace, 4) == 0) {
            logf_("already patched %s at %p", s->label, (void *)addr);
            applied++;
        } else {
            logf_("bytes mismatch at %p: %02X %02X %02X %02X (expected %02X %02X %02X %02X)",
                  (void *)addr, cur[0], cur[1], cur[2], cur[3],
                  s->expect[0], s->expect[1], s->expect[2], s->expect[3]);
        }
    }
    logf_("applied %d/%d patches", applied, (int)(sizeof(g_sites) / sizeof(g_sites[0])));

    ResumeThread(pi.hThread);
    CloseHandle(pi.hThread);

    DWORD wait = WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 0;
    GetExitCodeProcess(pi.hProcess, &code);
    logf_("exit: wait=0x%lx code=0x%lx (%ld)", wait, code, (long)code);
    CloseHandle(pi.hProcess);
    if (g_log) fclose(g_log);
    return 0;
}
