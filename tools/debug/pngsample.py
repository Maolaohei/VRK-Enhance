# -*- coding: utf-8 -*-
"""Sample a few pixels of the captured frame to see whether anything is actually drawn."""
import struct, zlib, sys, collections

def load_png(path):
    d = open(path, 'rb').read()
    assert d[:8] == b'\x89PNG\r\n\x1a\n'
    pos = 8
    w = h = bit = ctype = None
    idat = bytearray()
    while pos < len(d):
        ln = struct.unpack('>I', d[pos:pos+4])[0]
        typ = d[pos+4:pos+8]
        body = d[pos+8:pos+8+ln]
        if typ == b'IHDR':
            w, h, bit, ctype = struct.unpack('>IIBB', body[:10])
        elif typ == b'IDAT':
            idat += body
        elif typ == b'IEND':
            break
        pos += 12 + ln
    raw = zlib.decompress(bytes(idat))
    ch = {0:1, 2:3, 4:2, 6:4}[ctype]
    stride = w * ch
    out = bytearray(h * stride)
    prev = bytearray(stride)
    p = 0
    for y in range(h):
        f = raw[p]; p += 1
        line = bytearray(raw[p:p+stride]); p += stride
        if f == 1:
            for i in range(ch, stride):
                line[i] = (line[i] + line[i-ch]) & 0xFF
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif f == 3:
            for i in range(stride):
                a = line[i-ch] if i >= ch else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif f == 4:
            for i in range(stride):
                a = line[i-ch] if i >= ch else 0
                b = prev[i]
                c = prev[i-ch] if i >= ch else 0
                pp = a + b - c
                pa, pb, pc = abs(pp-a), abs(pp-b), abs(pp-c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        out[y*stride:(y+1)*stride] = line
        prev = line
    return w, h, ch, out

w, h, ch, px = load_png(sys.argv[1] if len(sys.argv) > 1 else 'vrk_frame_Title_1.png')
print("size", w, h, "channels", ch)

def at(x, y):
    o = (y * w + x) * ch
    return tuple(px[o:o+3])

pts = [(0,0), (w-1,0), (0,h-1), (w-1,h-1), (w//2,h//2), (w//4,h//2), (3*w//4,h//2), (w//2,h//4), (w//2,3*h//4)]
for (x,y) in pts:
    print("(%4d,%4d) = %s" % (x, y, at(x,y)))

c = collections.Counter()
step = max(1, w//64)
for y in range(0, h, max(1, h//36)):
    for x in range(0, w, step):
        c[at(x,y)] += 1
print("distinct colours sampled:", len(c))
print("most common:", c.most_common(6))
flat = len(c) <= 3
print("VERDICT:", "flat / uniform colour" if flat else "image contains gradients or content")
