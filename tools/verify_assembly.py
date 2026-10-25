"""Verify our GAS/COFF helper against the pure-Python PE transform constants."""
from pathlib import Path
import argparse
import importlib.util
import json
import struct

HERE = Path(__file__).resolve().parent


def require(condition, message):
    if not condition:
        raise ValueError(message)


def coff(path):
    data = Path(path).read_bytes()
    machine, count, _, sp, sn, optional, _ = struct.unpack_from("<HHIIIHH", data)
    if machine != 0x8664 or optional:
        raise ValueError("Expected relocatable AMD64 COFF")
    strings = sp + sn * 18
    def name(raw):
        if raw[:4] == b"\0"*4:
            start = strings + struct.unpack_from("<I", raw, 4)[0]
            return data[start:data.index(b"\0", start)].decode("ascii")
        return raw.rstrip(b"\0").decode("ascii")
    symbols = {}
    i = 0
    while i < sn:
        q = sp+i*18
        value, sec, typ, storage, aux = struct.unpack_from("<IhHBB", data, q+8)
        symbols[i] = (name(data[q:q+8]), value, sec)
        i += 1+aux
    sections = {}
    for i in range(count):
        q = 20+i*40
        n = name(data[q:q+8])
        size, raw, reloc = struct.unpack_from("<III", data, q+16)
        nr = struct.unpack_from("<H", data, q+32)[0]
        relocs = []
        for j in range(nr):
            site, sym, kind = struct.unpack_from("<IIH", data, reloc+j*10)
            relocs.append((site, symbols[sym][0], kind))
        sections[n] = (data[raw:raw+size] if size else b"", relocs)
    return sections, symbols


def verify(path):
    spec = importlib.util.spec_from_file_location("background_transform", HERE/"background_video_transform.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    sections, symbols = coff(path)
    require(sections['.text'][0] == module.CODE, 'assembler CODE mismatch')
    require(sections['.xdata'][0] == module.XDATA, 'assembler unwind mismatch')
    expected_rel = [(site,name,4) for site,(name,_) in module.REL32.items()]
    require(sections['.text'][1] == expected_rel, 'Offline validation failed at source line 52')
    expected_pdata = b"".join(struct.pack("<III",*row) for row in module.FUNCTIONS)
    require(sections['.pdata'][0] == expected_pdata, 'Offline validation failed at source line 54')
    expected_prel = [(row*12+part*4, ".xdata" if part==2 else ".text",3)
                    for row in range(len(module.FUNCTIONS)) for part in range(3)]
    require(sections['.pdata'][1] == expected_prel, 'Offline validation failed at source line 57')
    require(not sections['.data'][0] and (not sections['.bss'][0]), 'Offline validation failed at source line 58')
    return dict(assembler_matches_embedded_code=True, assembler_matches_unwind=True,
                relative_relocations_only=True, code_bytes=len(module.CODE),
                unwind_bytes=len(module.XDATA), functions=len(module.FUNCTIONS),
                no_vendor_image_loaded=True)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--object", type=Path, required=True, help="Our locally assembled AMD64 COFF object")
    args=parser.parse_args()
    result=verify(args.object)
    print(json.dumps(result,indent=2))


if __name__=="__main__":
    main()
