# -*- coding: utf-8 -*-
"""Minimal minidump stack triage: modules, threads (x64 CONTEXT RIP/RSP), stack scan.

Usage: python dmpstack.py <file.dmp> [--threads N] [--frames M]
Reads only the streams needed for "which code is this thread in":
  Directory(MDMP) / ModuleList(4) / ThreadList(3) / MemoryList(5) / Memory64List(9)
No symbols: numbers are reported as module+offset.
"""
import struct
import sys
import os

ST_MODULE_LIST = 4
ST_THREAD_LIST = 3
ST_MEMORY_LIST = 5
ST_MEMORY64_LIST = 9
ST_EXCEPTION = 6

# x64 CONTEXT offsets
CTX_RSP = 0x98
CTX_RBP = 0xA0
CTX_RIP = 0xF8


def u16(b, o):
    return struct.unpack_from("<H", b, o)[0]


def u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


def u64(b, o):
    return struct.unpack_from("<Q", b, o)[0]


def read_mdstring(b, rva):
    if rva <= 0 or rva + 4 > len(b):
        return ""
    n = u32(b, rva)
    raw = b[rva + 4: rva + 4 + n]
    try:
        return raw.decode("utf-16-le", "replace")
    except Exception:
        return ""


def parse(path):
    b = open(path, "rb").read()
    if b[:4] != b"MDMP":
        raise SystemExit("not a minidump: %s" % path)
    n_streams = u32(b, 8)
    dir_rva = u32(b, 12)
    ts = u32(b, 20)
    streams = {}
    for i in range(n_streams):
        o = dir_rva + i * 12
        stype = u32(b, o)
        size = u32(b, o + 4)
        rva = u32(b, o + 8)
        streams[stype] = (size, rva)

    modules = []
    if ST_MODULE_LIST in streams:
        size, rva = streams[ST_MODULE_LIST]
        n = u32(b, rva)
        for i in range(n):
            o = rva + 4 + i * 108
            base = u64(b, o)
            isize = u32(b, o + 8)
            name_rva = u32(b, o + 20)
            modules.append((base, isize, read_mdstring(b, name_rva)))

    threads = []
    if ST_THREAD_LIST in streams:
        size, rva = streams[ST_THREAD_LIST]
        n = u32(b, rva)
        for i in range(n):
            o = rva + 4 + i * 48
            tid = u32(b, o)
            teb = u64(b, o + 16)
            stk_start = u64(b, o + 24)
            stk_size = u32(b, o + 32)
            stk_rva = u32(b, o + 36)
            ctx_size = u32(b, o + 40)
            ctx_rva = u32(b, o + 44)
            rip = rsp = rbp = 0
            if ctx_size >= CTX_RIP + 8 and ctx_rva + CTX_RIP + 8 <= len(b):
                rip = u64(b, ctx_rva + CTX_RIP)
                rsp = u64(b, ctx_rva + CTX_RSP)
                rbp = u64(b, ctx_rva + CTX_RBP)
            threads.append(dict(tid=tid, teb=teb, stack=(stk_start, stk_size, stk_rva),
                                rip=rip, rsp=rsp, rbp=rbp, ctx_size=ctx_size))

    # memory ranges: va -> file offset
    mem = []
    if ST_MEMORY64_LIST in streams:
        size, rva = streams[ST_MEMORY64_LIST]
        n = u64(b, rva)
        base_rva = u64(b, rva + 8)
        cur = base_rva
        for i in range(n):
            o = rva + 16 + i * 16
            va = u64(b, o)
            dsize = u64(b, o + 8)
            mem.append((va, dsize, cur))
            cur += dsize
    elif ST_MEMORY_LIST in streams:
        size, rva = streams[ST_MEMORY_LIST]
        n = u32(b, rva)
        for i in range(n):
            o = rva + 4 + i * 16
            va = u64(b, o)
            dsize = u32(b, o + 8)
            frva = u32(b, o + 12)
            mem.append((va, dsize, frva))

    exc = None
    if ST_EXCEPTION in streams:
        size, rva = streams[ST_EXCEPTION]
        tid = u32(b, rva)
        code = u32(b, rva + 8)
        flags = u32(b, rva + 12)
        addr = u64(b, rva + 24)
        nparam = u32(b, rva + 32)
        params = [u64(b, rva + 40 + 8 * i) for i in range(min(nparam, 15))]
        exc = dict(tid=tid, code=code, flags=flags, addr=addr, params=params)

    return dict(size=len(b), ts=ts, modules=modules, threads=threads, mem=mem, exc=exc)


def mod_of(modules, addr):
    for base, size, name in modules:
        if base <= addr < base + size:
            return name, addr - base
    return None, 0


def mem_read(b, mem, va, size):
    for mva, msize, mrva in mem:
        if mva <= va and va + size <= mva + msize:
            off = mrva + (va - mva)
            if off + size <= len(b):
                return b[off: off + size]
    return None


def fmt(modules, addr):
    name, off = mod_of(modules, addr)
    if name:
        return "%s+0x%X" % (os.path.basename(name), off)
    return "0x%X" % addr


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    path = sys.argv[1]
    max_threads = 8
    frames = 24
    args = sys.argv[2:]
    if "--threads" in args:
        max_threads = int(args[args.index("--threads") + 1])
    if "--frames" in args:
        frames = int(args[args.index("--frames") + 1])

    d = parse(path)
    b = open(path, "rb").read()
    print("== %s ==" % path)
    print("bytes=%d  threads=%d  modules=%d  mem_ranges=%d  timestamp=0x%08X"
          % (d["size"], len(d["threads"]), len(d["modules"]), len(d["mem"]), d["ts"]))
    if d["exc"]:
        e = d["exc"]
        print("EXCEPTION tid=%d code=0x%08X at %s params=[%s]"
              % (e["tid"], e["code"], fmt(d["modules"], e["addr"]),
                 ", ".join("0x%X" % p for p in e["params"])))

    # rank threads: those whose RIP is inside an image first, then by stack size
    def rank(t):
        name, _ = mod_of(d["modules"], t["rip"])
        return (0 if name else 1, -t["stack"][1])

    threads = sorted(d["threads"], key=rank)[:max_threads]
    for t in threads:
        print("\n-- tid=%d  rip=%s  rsp=0x%X  rbp=0x%X  stack=0x%X+0x%X"
              % (t["tid"], fmt(d["modules"], t["rip"]), t["rsp"], t["rbp"],
                 t["stack"][0], t["stack"][1]))
        start, ssize, _ = t["stack"]
        raw = mem_read(b, d["mem"], start, min(ssize, 0x40000))
        if raw is None:
            print("   (stack memory not present in dump)")
            continue
        hits = []
        for i in range(0, len(raw) - 8 + 1, 8):
            v = struct.unpack_from("<Q", raw, i)[0]
            name, off = mod_of(d["modules"], v)
            if name:
                hits.append((start + i, v, os.path.basename(name), off))
        below = [h for h in hits if h[0] >= t["rsp"]]
        sel = below[:frames] if below else hits[:frames]
        for va, v, name, off in sel:
            mark = " <-rsp" if va == t["rsp"] else ""
            print("   0x%X  %s+0x%X%s" % (va, name, off, mark))


if __name__ == "__main__":
    main()
