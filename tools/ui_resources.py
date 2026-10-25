"""Transform three UI XML resources read from a locally supplied, exact vendor file.

This file contains our transformation logic, not Xiaomi's XML or image resources.
"""
from collections import Counter
import hashlib, io, re, struct, xml.etree.ElementTree as ET, zipfile, zlib
from pathlib import Path

EXPECTED_SHA256 = "46EFA8AA4D56400A82523CE74808428B89A49C31430269CB536064C4E508514E"
OUTPUT_SHA256 = "1B67D413C39DC25A887DAB5362FDB383CA55BFAF25EE200B085900A1217BDA80"
MEMBERS = ("xml/main/main_window.xml", "xml/setting_window.xml", "xml/main/qrcode_window.xml")

def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest().upper()


class Region(io.RawIOBase):
    """Seekable bounded file view, so zipfile does not load a 330 MB blob."""
    def __init__(self, path, start, size):
        self.stream = path.open("rb")
        self.start, self.size, self.position = start, size, 0

    def readable(self):
        return True

    def seekable(self):
        return True

    def tell(self):
        return self.position

    def seek(self, offset, whence=0):
        point = offset if whence == 0 else (self.position if whence == 1 else self.size) + offset
        require(point >= 0, "Negative seek")
        self.position = point
        return point

    def read(self, count=-1):
        remaining = max(0, self.size - self.position)
        count = remaining if count < 0 else min(count, remaining)
        self.stream.seek(self.start + self.position)
        data = self.stream.read(count)
        self.position += len(data)
        return data

    def close(self):
        self.stream.close()
        super().close()


def resource_location(path):
    with path.open("rb") as stream:
        header = stream.read(4096)
        require(header[:2] == b"MZ", "Not a PE file")
        pe = struct.unpack_from("<I", header, 0x3C)[0]
        require(header[pe:pe + 4] == b"PE\0\0", "Bad PE signature")
        machine, sections = struct.unpack_from("<HH", header, pe + 4)
        optional_size = struct.unpack_from("<H", header, pe + 20)[0]
        optional = pe + 24
        require(machine == 0x8664 and struct.unpack_from("<H", header, optional)[0] == 0x20B,
                "Only the verified x64 build is supported")
        resource_rva, resource_size = struct.unpack_from("<II", header, optional + 112 + 16)
        section_table = optional + optional_size
        section_rows = []
        for i in range(sections):
            off = section_table + 40 * i
            virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from("<IIII", header, off + 8)
            section_rows.append((virtual_address, max(virtual_size, raw_size), raw_pointer))

        def file_offset(rva):
            for address, size, pointer in section_rows:
                if address <= rva < address + size:
                    return pointer + rva - address
            raise RuntimeError("RVA outside sections")

        root = file_offset(resource_rva)

        def read_at(offset, size):
            stream.seek(offset)
            result = stream.read(size)
            require(len(result) == size, "Truncated PE resource")
            return result

        def find_entry(relative, wanted):
            directory = read_at(root + relative, 16)
            named, ids = struct.unpack_from("<HH", directory, 12)
            for i in range(named + ids):
                name, target = struct.unpack("<II", read_at(root + relative + 16 + i * 8, 8))
                if name & 0x80000000:
                    address = root + (name & 0x7FFFFFFF)
                    length = struct.unpack("<H", read_at(address, 2))[0]
                    value = read_at(address + 2, length * 2).decode("utf-16le")
                else:
                    value = name
                if value == wanted:
                    return target
            raise RuntimeError(f"Missing resource {wanted!r}")

        target = find_entry(0, "ZIPRES")
        require(target & 0x80000000, "ZIPRES must be a directory")
        target = find_entry(target & 0x7FFFFFFF, 110)
        require(target & 0x80000000, "ZIPRES/110 must be a directory")
        target = find_entry(target & 0x7FFFFFFF, 2052)
        require(not target & 0x80000000, "Language entry must be resource data")
        data_rva, length = struct.unpack_from("<II", read_at(root + target, 16))
        start = file_offset(data_rva)
        require(root <= start and start + length <= root + resource_size, "ZIP outside resource section")
        require(read_at(start, 4) == b"PK\x03\x04", "ZIPRES payload is not ZIP")
        return start, length


def update_tag(text, name, attributes):
    pattern = re.compile(r'<[A-Za-z][^<>]*\bname="' + re.escape(name) + r'"[^<>]*>')
    matches = list(pattern.finditer(text))
    require(len(matches) == 1, f"Expected exactly one control {name}, found {len(matches)}")
    match = matches[0]
    tag = match.group(0)
    for key, value in attributes.items():
        attr = re.compile(r'\s' + re.escape(key) + r'="[^"]*"')
        replacement = f' {key}="{value}"'
        if attr.search(tag):
            tag = attr.sub(replacement, tag, count=1)
        else:
            position = len(tag) - (2 if tag.endswith("/>") else 1)
            tag = tag[:position].rstrip() + replacement + (" />" if tag.endswith("/>") else ">")
    return text[:match.start()] + tag + text[match.end():]


def parse_ui_xml(data):
    """Parse a validation view; never rewrite the payload's legacy bare '&'."""
    text = data.decode("utf-8-sig")
    bare_ampersands = re.compile(r'&(?!#\d+;|#x[0-9a-fA-F]+;|[A-Za-z][A-Za-z0-9_.:-]*;)')
    normalized, count = bare_ampersands.subn("&amp;", text)
    return ET.fromstring(normalized), count


def ui_changes(member):
    offscreen = {"visible": "false", "enabled": "false", "mouse": "false", "float": "true",
                 "pos": "-1000,-1000,-999,-999", "width": "1", "height": "1", "padding": "0,0,0,0"}
    if member == "xml/setting_window.xml":
        changes = {name: dict(offscreen) for name in ("cl_mishare", "btn_mishare", "cl_tws", "btn_tws")}
        changes.update({
            "cl_priavcy": {"pos": "0,113,2,135"},
            "cl_about": {"pos": "0,150,2,172"},
            "cl_setting_update": {"pos": "53,153,59,159"},
        })
        return changes
    if member == "xml/main/qrcode_window.xml":
        return {"text_confirm_in_phone": {"font": "1", "height": "22", "textcolor": "#FFB64B00"}}
    require(member == "xml/main/main_window.xml", "Unexpected XML member")
    hidden = (
        "avatar_unlogin_status", "avatar_login_status", "btn_mishare", "btn_miplay_wlan",
        "cl_mipods_split", "btn_mipods", "cl_mipods_update", "cl_mipods_twsoff", "btn_link_more",
        "login_menu_tips", "midrop_menu_tips", "miplay_menu_tips", "midrop_menu_tws", "more_menu_tips",
    )
    changes = {name: dict(offscreen) for name in hidden}
    changes.update({
        "btn_miscreen": {"float": "true", "pos": "15,55,45,85", "padding": "0,0,0,0"},
        "screen_share_menu_tips": {"pos": "54,55,130,85"},
        # Leave all TabLayout defaults untouched. The native startup selects
        # the phone page and performs initialization in that transition.
        # Keep the original total footprint: 24+40 becomes 40+24.
        "link_help_layout": {"height": "40", "padding": "0,0,0,24"},
        "btn_icon_scan_to_link": {"padding": "0,8,8,0"},
        "btn_scan_to_link": {"align": "center", "font": "2", "hotfont": "2", "pushedfont": "2",
                             "height": "40", "width": "160", "padding": "0,0,12,0",
                             "textcolor": "#FFFFFFFF", "bkcolor": "#FFB64B00"},
        "btn_icon_screen_share_help": {"padding": "0,8,4,0"},
        "btn_screen_share_help": {"padding": "0,10,36,0"},
        "text_confirm_in_phone": {"font": "8", "height": "24", "textcolor": "#FFB64B00"},
        "btn_show_panel": {"font": "2", "pos": "198,401,342,441"},
        "btn_close_link": {"font": "2", "pos": "378,401,522,441"},
    })
    return changes


def transform(original, member):
    text = original.decode("utf-8-sig")
    changes = ui_changes(member)
    for name, attributes in changes.items():
        text = update_tag(text, name, attributes)
    ui_only_text = text
    # Non-runtime comments are removed to fit the original compressed allocation.
    text = re.sub(r'<!--[\s\S]*?-->', "", text)
    patched = text.encode("utf-8")
    before, original_bare_ampersands = parse_ui_xml(original)
    after, patched_bare_ampersands = parse_ui_xml(patched)
    require(original_bare_ampersands == patched_bare_ampersands, "Legacy XML escaping changed")
    before_names = Counter(e.get("name") for e in before.iter() if e.get("name"))
    after_names = Counter(e.get("name") for e in after.iter() if e.get("name"))
    require(before_names == after_names, "Named controls changed")
    require([e.tag for e in before.iter()] == [e.tag for e in after.iter()], "Control class/order changed")
    exact_changes = []
    for index, (old, new) in enumerate(zip(before.iter(), after.iter())):
        require(old.tag == new.tag and old.get("name") == new.get("name"), "Control identity/order changed")
        if old.tag == "TabLayout":
            require(old.attrib == new.attrib, "Native TabLayout initialization attributes changed")
        require((old.text or "").strip() == (new.text or "").strip(), "Element text changed")
        require((old.tail or "").strip() == (new.tail or "").strip(), "Element tail changed")
        allowed = changes.get(old.get("name"), {})
        for key in sorted(set(old.attrib) | set(new.attrib)):
            previous, current = old.get(key), new.get(key)
            if previous != current:
                require(key in allowed and current == allowed[key], f"Unplanned attribute {old.get('name')}.{key}")
                exact_changes.append({"element_index": index, "control": old.get("name"), "attribute": key,
                                      "before": previous, "after": current})
    # Text is assigned by the native localization layer. Keep every product/error/consent string as-is.
    require(Counter((e.get("name"), e.get("text")) for e in before.iter() if e.get("text")) ==
            Counter((e.get("name"), e.get("text")) for e in after.iter() if e.get("text")), "Product text changed")
    return patched, ui_only_text, exact_changes, original_bare_ampersands


def central_entry(region, name):
    # The verified payload is a non-ZIP64, single-volume archive.
    region.seek(max(0, region.size - 65557))
    tail = region.read()
    relative = tail.rfind(b"PK\x05\x06")
    require(relative >= 0, "Missing ZIP end record")
    disk, cd_disk, entries_disk, entries, cd_size, cd_offset, comment_size = struct.unpack_from("<4H2IH", tail, relative + 4)
    require(disk == cd_disk == 0 and entries_disk == entries and entries < 65535, "Unsupported ZIP directory")
    position = cd_offset
    for _ in range(entries):
        region.seek(position)
        header = region.read(46)
        require(header[:4] == b"PK\x01\x02", "Invalid ZIP central entry")
        filename_size, extra_size, comment_size = struct.unpack_from("<HHH", header, 28)
        filename = region.read(filename_size).decode("utf-8")
        if filename == name:
            return position
        position += 46 + filename_size + extra_size + comment_size
    raise RuntimeError(f"Missing central entry {name}")


def patch_file(path):
    """Patch only a caller-created private build copy; require the exact input hash."""
    path = Path(path)
    require(digest(path) == EXPECTED_SHA256, "UI input is not the reviewed original")
    start, size = resource_location(path)
    blocks, expected_xml = [], {}
    with Region(path, start, size) as region, zipfile.ZipFile(region) as archive:
        old_metadata = {i.filename: (i.header_offset, i.compress_type, i.flag_bits, i.CRC,
                                     i.compress_size, i.file_size) for i in archive.infolist()}
        for member in MEMBERS:
            info = archive.getinfo(member)
            require(info.compress_type == zipfile.ZIP_DEFLATED and info.flag_bits & 9 == 0,
                    "Unsupported ZIP compression, encryption or data descriptor")
            original = archive.read(info)
            central = central_entry(region, member)
            region.seek(info.header_offset)
            local_header = region.read(30)
            require(local_header[:4] == b"PK\x03\x04", "Bad ZIP header")
            require(struct.unpack_from("<III", local_header, 14) ==
                    (info.CRC, info.compress_size, info.file_size), "ZIP metadata mismatch")
            filename_size, extra_size = struct.unpack_from("<HH", local_header, 26)
            offset = info.header_offset + 30 + filename_size + extra_size
            patched, _, _, _ = transform(original, member)
            compressor = zlib.compressobj(level=9, wbits=-15)
            compressed = compressor.compress(patched) + compressor.flush()
            require(len(compressed) <= info.compress_size, "Modified XML exceeds its allocation")
            metadata = struct.pack("<III", zlib.crc32(patched) & 0xFFFFFFFF,
                                   len(compressed), len(patched))
            blocks.extend([(start + offset, compressed + b"\0" * (info.compress_size - len(compressed))),
                           (start + info.header_offset + 14, metadata), (start + central + 16, metadata)])
            expected_xml[member] = hashlib.sha256(patched).hexdigest()
    cursor = start
    for offset, data in sorted(blocks):
        require(cursor <= offset and offset + len(data) <= start + size, "Overlapping/out-of-range resource write")
        cursor = offset + len(data)
    with path.open('r+b') as stream:
        for offset, data in blocks:
            stream.seek(offset)
            stream.write(data)
    require(digest(path) == OUTPUT_SHA256,
            "UI output hash differs; checked reference uses Python 3.14.6 / zlib 1.3.2. "
            "Do not refresh the allowlist to suppress this error.")
    with Region(path, start, size) as region, zipfile.ZipFile(region) as archive:
        require(archive.testzip() is None, "ZIP CRC verification failed")
        require(set(archive.namelist()) == set(old_metadata), "ZIP members changed")
        for info in archive.infolist():
            previous = old_metadata[info.filename]
            current = (info.header_offset, info.compress_type, info.flag_bits, info.CRC,
                       info.compress_size, info.file_size)
            if info.filename in expected_xml:
                require(current[:3] == previous[:3], "ZIP layout changed")
                require(hashlib.sha256(archive.read(info)).hexdigest() == expected_xml[info.filename],
                        "Modified XML readback differs")
            else:
                require(current == previous, "Unrelated ZIP member changed")
