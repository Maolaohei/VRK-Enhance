import struct, glob, os, sys

IMAGE_BASE = 0x140000000

def parse(path):
    with open(path, 'rb') as f:
        data = f.read()
    if data[:4] != b'MDMP':
        return None
    (sig, ver, nstreams, dirrva, checksum, ts, flags) = struct.unpack_from('<IIIIIIQ', data, 0)
    streams = []
    for i in range(nstreams):
        stype, size, rva = struct.unpack_from('<III', data, dirrva + i * 12)
        streams.append((stype, size, rva))

    exc = None
    modules = []
    for stype, size, rva in streams:
        if stype == 6:  # ExceptionStream
            tid, align = struct.unpack_from('<II', data, rva)
            code, eflags, erec, eaddr, nparam, unused = struct.unpack_from('<IIQQII', data, rva + 8)
            params = struct.unpack_from('<15Q', data, rva + 8 + 32)
            exc = dict(tid=tid, code=code, addr=eaddr, nparam=nparam,
                       params=[p for p in params[:nparam]])
        elif stype == 4:  # ModuleListStream
            n = struct.unpack_from('<I', data, rva)[0]
            off = rva + 4
            for _ in range(n):
                base, size_, chk, tds, namerva = struct.unpack_from('<QIIII', data, off)
                off += 108
                # MINIDUMP_STRING: 4-byte length + UTF-16
                ln = struct.unpack_from('<I', data, namerva)[0]
                name = data[namerva + 4: namerva + 4 + ln].decode('utf-16-le', 'replace')
                modules.append((base, size_, name))
    return exc, modules

def classify(addr, modules):
    best = None
    for base, size_, name in modules:
        if base <= addr < base + size_:
            if best is None or base > best[0]:
                best = (base, name)
    if best:
        return os.path.basename(best[1]), addr - best[0]
    return None, None

files = sorted(glob.glob(r"C:\Users\Admin\AppData\Local\CrashDumps\VR_Kanojo.exe.*.dmp"),
               key=lambda p: os.path.getmtime(p))
for p in files:
    try:
        r = parse(p)
    except Exception as e:
        print(p, "PARSE ERROR", e)
        continue
    if not r:
        print(p, "not a minidump")
        continue
    exc, modules = r
    if not exc:
        print(os.path.basename(p), "no exception stream")
        continue
    mod, off = classify(exc['addr'], modules)
    print("%-28s code=0x%08X addr=0x%016X  module=%s off=0x%X  nparam=%d params=%s  dlls=%d" % (
        os.path.basename(p), exc['code'], exc['addr'], mod,
        off if off is not None else -1, exc['nparam'],
        ",".join("0x%X" % v for v in exc['params']), len(modules)))
