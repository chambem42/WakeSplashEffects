"""Rewrite a shader bundle built with Unity 2019.4.40 so KSP's 2019.4.18 player can use its D3D11 programs.

2019.4.40 writes each D3D11 program's bytecode with a version-2 header (6 bytes, first byte 2, then 32 more bytes) before
the DXBC; 2019.4.18 knows only the version-1 header (the same 6 bytes, first byte 1) and reports such programs as
unsupported (nothing draws). The 2019.4.18 editor cannot be licensed any more, so: build with 2019.4.40, then run this.

    python fix_bundle.py <bundle> [--check]

Needs UnityPy (pip install UnityPy). --check only reports.
"""
import struct
import sys

import UnityPy
import lz4.block

D3D11 = 4  # shader compiler platform


def fix_program(p):
    """One program's bytes -> (bytes, changed)."""
    d = p.find(b"DXBC")
    if d < 42 or p[d - 38] != 2:
        return p, False
    code_len = struct.unpack_from("<i", p, d - 42)[0]
    extra = p[d - 32:d]
    if any(extra):
        print(f"    note: dropping non-zero version-2 header bytes {extra.hex()}")
    out = p[:d - 42] + struct.pack("<i", code_len - 32) + bytes([1]) + p[d - 37:d - 32] + p[d:]
    return out, True


def fix_platform_blob(data):
    """count, then (offset, length, segment) per program, then the programs."""
    n = struct.unpack_from("<i", data, 0)[0]
    entries = [struct.unpack_from("<iii", data, 4 + 12 * i) for i in range(n)]
    progs = [data[o:o + l] for o, l, _ in entries]
    fixed, changed = zip(*(fix_program(p) for p in progs)) if progs else ((), ())
    table, body, pos = [], b"", 4 + 12 * n
    for p, (_, _, seg) in zip(fixed, entries):
        table.append(struct.pack("<iii", pos, len(p), seg))
        body += p
        pos += len(p)
    return struct.pack("<i", n) + b"".join(table) + body, sum(changed), n


def main(path, check):
    env = UnityPy.load(path)
    total = 0
    for obj in env.objects:
        if obj.type.name != "Shader":
            continue
        t = obj.read_typetree()
        name = t["m_ParsedForm"]["m_Name"]
        blob = bytes(t["compressedBlob"])
        new_blob = b""
        offs, clens, dlens = [], [], []
        for i, plat in enumerate(t["platforms"]):
            off, cl, dl = t["offsets"][i], t["compressedLengths"][i], t["decompressedLengths"][i]
            nested = isinstance(off, list)
            if nested:
                off, cl, dl = off[0], cl[0], dl[0]
            data = lz4.block.decompress(blob[off:off + cl], uncompressed_size=dl)
            changed = 0
            if plat == D3D11:
                data, changed, n = fix_platform_blob(data)
                print(f"  {name}: {changed} of {n} D3D11 programs had the version-2 header")
                total += changed
            comp = lz4.block.compress(data, store_size=False)
            o = len(new_blob)
            new_blob += comp
            offs.append([o] if nested else o)
            clens.append([len(comp)] if nested else len(comp))
            dlens.append([len(data)] if nested else len(data))
        if not check:
            t["compressedBlob"] = list(new_blob)
            t["offsets"], t["compressedLengths"], t["decompressedLengths"] = offs, clens, dlens
            obj.save_typetree(t)
    if not check and total:
        with open(path, "wb") as f:
            f.write(env.file.save(packer="lz4"))
        print(f"wrote {path}")
    elif not total:
        print("nothing to change")


if __name__ == "__main__":
    main(sys.argv[1], "--check" in sys.argv[2:])
