"""Offline MIUI+ native background-video transform; never loads the input PE.

The enclosing build must FIRST apply the existing private-pipe and hide/resume
profile. Only the pinned resulting c15ac3... image is accepted. patch_file() is
called only on the enclosing builder's newly copied staging file.
The embedded assembler output is independently reproducible from the adjacent
background_video.S. It adds no import, DLL, writable code, thread, or global.
"""
from pathlib import Path
import hashlib
import struct

BASELINE_SHA256 = "c15ac36971bcb47edc9bea5827071099e7cb8b5985b89db5bf0fc817f97884a5"
CANDIDATE_SHA256 = "f8121a68ef63de9ffccf05ef5d9b4077b5cba2a970513b535ef654019f23607c"
SECTION_NAME = b".bgvideo"

CODE = bytes.fromhex(
    "534883ec204889cb837918007532b901000000ba12000000e87300000084c07429b901000000ba0c"
    "000000e86000000084c074164889d931d2e800000000eb0a4889d931d2e8000000004883c4205bc3"
    "534883ec204889cb83791800751031c9ba0c000000e82600000084c0740d4889d9ba01000000e800"
    "0000004883c4205bc30f1f400066662e0f1f8400000000005356574883ec4089ce89d7b920000000"
    "e8000000004889c3ff150000000031d241b81027000049f7f040883b4c8d4b0541c6010849ffc189"
    "c283e27f48c1e807740e81ca8000000041881149ffc1ebe741881149ffc183ff0c75106641c70118"
    "0b4983c10241c60158eb0e6641c70110204983c10241c6013085f6410f9541014983c1024929d945"
    "89c84183e8054489430148897c242048895c24284c894c2430e8000000004889c1488d542420e800"
    "00000084c0750c4889d9e80000000031c0eb05b8010000004883c4405f5e5bc3"
)
XDATA = bytes.fromhex("01050200053201300105020005320130010704000772037002600130")
FUNCTIONS = ((0, 80, 0), (80, 129, 8), (144, 352, 16))
REL32 = {
    0x3a: ("native_show", 0x697d0),
    0x46: ("native_show", 0x697d0),
    0x77: ("native_show", 0x697d0),
    0xa1: ("native_allocate", 0xdce28),
    0xaa: ("native_time_iat", 0x19d790),
    0x132: ("native_pipe_singleton", 0x47be0),
    0x13f: ("native_post_business", 0x47ff0),
    0x14b: ("native_free", 0xdcd24),
}
HOOKS = (
    (0x623D7, bytes.fromhex("e8f4730000"), 0, "main: audio32=true then mode11=true then hide; child: preserve hide-only behavior"),
    (0x6EE4C, bytes.fromhex("e87fa9ffff"), 0x50, "existing hidden-only ActiveSelf: mode=false then original Show(true)"),
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def align(value, unit):
    if unit <= 0 or unit & (unit - 1):
        raise ValueError("Invalid PE alignment")
    return (value + unit - 1) & -unit


def layout(data):
    if len(data) < 0x100 or data[:2] != b"MZ":
        raise ValueError("Truncated or invalid MZ")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if pe + 24 > len(data) or data[pe:pe+4] != b"PE\0\0":
        raise ValueError("Invalid PE signature")
    machine, count = struct.unpack_from("<HH", data, pe + 4)
    osize = struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    table = opt + osize
    if machine != 0x8664 or osize < 240 or table + count * 40 > len(data):
        raise ValueError("Invalid x64 optional/section header")
    if struct.unpack_from("<H", data, opt)[0] != 0x20B:
        raise ValueError("Expected PE32+")
    certificate_offset, certificate_size = struct.unpack_from("<II", data, opt+112+4*8)
    if certificate_offset+certificate_size > len(data):
        raise ValueError("Truncated certificate/overlay region")
    sections = []
    for i in range(count):
        q = table + i * 40
        vs, rva, size, raw = struct.unpack_from("<IIII", data, q + 8)
        if raw + size > len(data):
            raise ValueError("Truncated section")
        sections.append(dict(name=data[q:q+8].rstrip(b"\0").decode("ascii"),
                             virtual_size=vs, rva=rva, raw_size=size, raw_offset=raw,
                             flags=struct.unpack_from("<I", data, q+36)[0]))
    return dict(pe=pe, opt=opt, table=table, section_count=count, sections=sections,
                section_alignment=struct.unpack_from("<I", data, opt+32)[0],
                file_alignment=struct.unpack_from("<I", data, opt+36)[0],
                header_size=struct.unpack_from("<I", data, opt+60)[0],
                image_base=struct.unpack_from("<Q", data, opt+24)[0])


def offset(meta, rva, length=1):
    for sec in meta["sections"]:
        d = rva - sec["rva"]
        if d >= 0 and d + length <= sec["raw_size"]:
            return sec["raw_offset"] + d
    raise ValueError("RVA not backed by file bytes: " + hex(rva))


def displacement(source_after, target):
    value = target - source_after
    if not -(1 << 31) <= value < (1 << 31):
        raise ValueError("REL32 overflow")
    return struct.pack("<i", value)


def transform(data):
    if sha(data) != BASELINE_SHA256:
        raise ValueError("Unreviewed input SHA256; old profile must be applied first")
    meta = layout(data)
    opt = meta["opt"]
    new_header = meta["table"] + meta["section_count"] * 40
    if new_header + 40 > meta["header_size"] or any(data[new_header:new_header+40]):
        raise ValueError("No empty section-header slot")
    new_rva = align(max(s["rva"] + max(s["virtual_size"], s["raw_size"])
                        for s in meta["sections"]), meta["section_alignment"])
    # Append AFTER the complete original file, including signature/overlay.
    new_raw = align(len(data), meta["file_alignment"])
    code = bytearray(CODE)
    for site, (name, target) in REL32.items():
        if code[site:site+4] != b"\0" * 4:
            raise ValueError("Unexpected assembler relocation placeholder")
        code[site:site+4] = displacement(new_rva + site + 4, target)
    xoff = align(len(code), 4)
    poff = align(xoff + len(XDATA), 4)
    old_pd_rva, old_pd_size = struct.unpack_from("<II", data, opt + 112 + 3*8)
    if old_pd_size % 12:
        raise ValueError("Invalid exception table length")
    old_pd_off = offset(meta, old_pd_rva, old_pd_size)
    old_pd = data[old_pd_off:old_pd_off+old_pd_size]
    old_functions = [struct.unpack_from("<III", old_pd, x) for x in range(0, old_pd_size, 12)]
    if not all(a[0] < a[1] <= b[0] for a, b in zip(old_functions, old_functions[1:])):
        raise ValueError("Original exception table is unordered/overlapping")
    added_functions = [(new_rva+s, new_rva+e, new_rva+xoff+u) for s, e, u in FUNCTIONS]
    if old_functions[-1][1] > added_functions[0][0]:
        raise ValueError("Exception ranges overlap")
    new_pd = old_pd + b"".join(struct.pack("<III", *row) for row in added_functions)
    blob = bytes(code) + b"\0" * (xoff-len(code)) + XDATA
    blob += b"\0" * (poff-len(blob)) + new_pd
    raw_size = align(len(blob), meta["file_alignment"])
    result = bytearray(data)
    allowed = []
    hook_rows = []
    for rva, before, relative, reason in HOOKS:
        off = offset(meta, rva, len(before))
        if result[off:off+len(before)] != before:
            raise ValueError("Hook preimage mismatch at " + hex(rva))
        after = b"\xe8" + displacement(rva+5, new_rva+relative)
        result[off:off+5] = after
        allowed.append((off, off+5))
        hook_rows.append(dict(file="MiScreenShare.exe", RVA=hex(rva), fileOffset=hex(off),
                              beforeHex=before.hex(), afterHex=after.hex(),
                              baselineSha256=BASELINE_SHA256, reason=reason))
    def put(off, fmt, *values):
        patch = struct.pack(fmt, *values)
        result[off:off+len(patch)] = patch
        allowed.append((off, off+len(patch)))
    put(meta["pe"]+6, "<H", meta["section_count"]+1)
    old_code_size = struct.unpack_from("<I", data, opt+4)[0]
    put(opt+4, "<I", old_code_size+raw_size)
    put(opt+56, "<I", align(new_rva+len(blob), meta["section_alignment"]))
    put(opt+64, "<I", 0)  # stale checksum is not retained
    put(opt+112+3*8, "<II", new_rva+poff, len(new_pd))
    header = SECTION_NAME.ljust(8, b"\0") + struct.pack("<IIIIIIHHI",
        len(blob), new_rva, raw_size, new_raw, 0, 0, 0, 0, 0x60000020)
    result[new_header:new_header+40] = header
    allowed.append((new_header, new_header+40))
    result.extend(b"\0" * (new_raw-len(result)))
    result.extend(blob)
    result.extend(b"\0" * (raw_size-len(blob)))
    result = bytes(result)
    # Verify all mutations in the old file and preserve prior patches/overlay.
    for i, (old, new) in enumerate(zip(data, result)):
        if old != new and not any(a <= i < b for a, b in allowed):
            raise AssertionError("Unexpected mutation at " + hex(i))
    out = layout(result)
    require(out['sections'][:-1] == meta['sections'], 'Offline validation failed at source line 175')
    require(out['image_base'] == meta['image_base'], 'Offline validation failed at source line 176')
    require(out['sections'][-1]['flags'] == 1610612768, 'Offline validation failed at source line 177')  # RX; never W
    require(len(result) == new_raw + raw_size, 'Offline validation failed at source line 178')
    require(result[new_raw + poff:new_raw + poff + len(old_pd)] == old_pd, 'Offline validation failed at source line 179')
    # No new absolute pointers: native calls/IAT references are RIP-relative.
    for site, (_, target) in REL32.items():
        require(new_rva + site + 4 + struct.unpack_from('<i', result, new_raw + site)[0] == target, 'Offline validation failed at source line 182')
    for relocated_base in (0x140000000, 0x7FF600000000, 0x180000000):
        for site, (_, target) in REL32.items():
            require(relocated_base + new_rva + site + 4 + struct.unpack_from('<i', result, new_raw + site)[0] == relocated_base + target, 'Offline validation failed at source line 185')
    # All directories except the exception table, and CFG flags, are identical.
    for index in range(16):
        if index != 3:
            q = opt+112+index*8
            require(data[q:q + 8] == result[q:q + 8], 'Offline validation failed at source line 190')
    require(data[opt + 70:opt + 72] == result[opt + 70:opt + 72], 'Offline validation failed at source line 191')
    lc_rva, lc_size = struct.unpack_from("<II", data, opt+112+10*8)
    lco = offset(meta, lc_rva, lc_size)
    require(data[lco:lco + lc_size] == result[lco:lco + lc_size], 'Offline validation failed at source line 194')
    # Existing prologues/lambda remain untouched; replacement sites are calls.
    for a, b in ((0x623B0,0x623D1),(0x624D1,0x624E2),(0x6EE30,0x6EE4C),
                 (0x6EE51,0x6EE7A),(0x6EE80,0x6F0B0)):
        q = offset(meta, a, b-a)
        require(data[q:q + b - a] == result[q:q + b - a], 'Offline validation failed at source line 199')
    if sha(result) != CANDIDATE_SHA256:
        raise AssertionError("Unexpected deterministic output SHA256")
    # A byte-exact rollback uses only the old header ranges/two hook sites;
    # removes the added bytes; never depends on a separately saved vendor file.
    rollback = bytearray(result[:len(data)])
    rollback_rows = []
    for a,b in allowed:
        rollback[a:b] = data[a:b]
        rollback_rows.append(dict(fileOffset=hex(a), beforeHex=data[a:b].hex(), afterHex=result[a:b].hex()))
    require(bytes(rollback) == data, 'Offline validation failed at source line 209')
    report = dict(baseline_sha256=sha(data), candidate_sha256=sha(result),
        original_size=len(data), candidate_size=len(result), section_rva=hex(new_rva),
        section_raw=hex(new_raw), section_size=len(blob), section_flags="RX, 0x60000020",
        helper_functions=[dict(begin=hex(a),end=hex(b),unwind=hex(c)) for a,b,c in added_functions],
        new_exception_directory=[hex(new_rva+poff),len(new_pd)],
        original_exception_entries=len(old_functions), new_exception_entries=len(added_functions),
        code_source="background_video.S", code_sha256=sha(CODE), xdata_sha256=sha(XDATA),
        hook_patches=hook_rows, rollback_ranges=rollback_rows,
        all_original_sections_preserved=True, original_overlay_preserved=True,
        original_unwind_prologues_epilogues_lambdas_preserved=True,
        exception_table_sorted_nonoverlapping=True, exception_entries_copied_byte_exactly=True,
        existing_imports_relocations_load_config_cfg_unchanged=True,
        added_absolute_relocations=0, direct_calls_only_to_helpers=True,
        aslr_arithmetic_checked_at_three_image_bases=True,
        no_writable_executable_section=True, no_new_imports_threads_or_globals=True,
        rollback_sha256=sha(rollback), target_loaded_or_executed=False)
    return result, report


def patch_file(path):
    """Root build API: mutate only its already copied, hash-pinned staging file."""
    path = Path(path)
    data, report = transform(path.read_bytes())
    path.write_bytes(data)
    if sha(path.read_bytes()) != report["candidate_sha256"]:
        raise IOError("Transformed file readback mismatch")
    return report


if __name__ == "__main__":
    raise SystemExit("Use the project build.ps1; this module only edits its new staging copy.")
