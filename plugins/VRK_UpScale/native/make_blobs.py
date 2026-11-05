#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 fxc 产出的 .cso 转成可内嵌的 shader_blobs.h（与 vrkup.cpp 里的符号名一致）。

用法：  python make_blobs.py <native 目录>
默认读取同目录下的 fullscreen.cso / easu.cso / rcas.cso，写回 shader_blobs.h。
"""
import os
import sys

# 符号名必须与 vrkup.cpp 中的 extern 声明一致
BLOBS = [
    ("fullscreen.cso", "kVbytecode"),
    ("easu.cso", "kEasuBytecode"),
    ("rcas.cso", "kRcasBytecode"),
]


def emit(name, data):
    out = []
    out.append("static const unsigned char %s[] = {" % name)
    for i in range(0, len(data), 16):
        chunk = data[i:i + 16]
        out.append("  " + ",".join("0x%02x" % b for b in chunk) + ",")
    out.append("};")
    out.append("static const unsigned int %s_len = %du;" % (name, len(data)))
    return "\n".join(out)


def main():
    native = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    parts = ["// Auto-generated from fxc output. Do not edit.", "#pragma once", ""]
    for fname, symbol in BLOBS:
        path = os.path.join(native, fname)
        if not os.path.isfile(path):
            print("[ERR] missing " + path)
            return 1
        with open(path, "rb") as f:
            data = f.read()
        parts.append(emit(symbol, data))
        parts.append("")
        print("%-16s -> %-16s %6d bytes" % (fname, symbol, len(data)))
    dst = os.path.join(native, "shader_blobs.h")
    with open(dst, "w", encoding="ascii", newline="\n") as f:
        f.write("\n".join(parts))
    print("wrote " + dst)
    return 0


if __name__ == "__main__":
    sys.exit(main())
