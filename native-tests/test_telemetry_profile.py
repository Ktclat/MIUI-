"""Check our public patch bytes against the synthetic Win64 test instructions.

Only reads the JSON profile and our assembler source. No vendor files are needed
or executed. This prevents passing stub tests for bytes absent from the profile;
it does not replace validation of the pinned input files during preparation.
"""
import argparse
import json
from pathlib import Path
import re
import struct

HERE = Path(__file__).resolve().parent


def require(condition, message):
    if not condition:
        raise ValueError(message)


def payload(source, label):
    matches = re.findall(
        rf"^{re.escape(label)}:\n[ \t]*\.byte ([^\n]+)", source, re.MULTILINE
    )
    require(len(matches) == 1, f"Expected exactly one byte block for {label}")
    return bytes(int(part.strip(), 0) for part in matches[0].split(","))


def profile_bytes(profile, filename, offset):
    rows = [row for row in profile["files"] if row["path"] == filename]
    require(len(rows) == 1, f"Expected exactly one profile row for {filename}")
    patches = [
        patch for patch in rows[0]["patches"]
        if int(str(patch["offset"]), 0) == offset
    ]
    require(len(patches) == 1, f"Missing or duplicate patch: {filename} at {offset:#x}")
    return bytes.fromhex(patches[0]["after"])


def verify(profile, source):
    # Match both cleanup branches, the false return and every void-return site.
    sites = (
        ("MiDrop.dll", 0x645E9, ".Ldiscard"),
        ("MiDrop.dll", 0x6479B, ".Ltyped"),
        ("PrivacyAuthorization.dll", 0x2A350, "check_false_patch"),
        ("MiDrop.dll", 0xC7BF0, "check_void_patch"),
        ("idmruntime.dll", 0x158DD0, "check_void_patch"),
        ("idmruntime.dll", 0x15D3B0, "check_void_patch"),
        ("idmruntime.dll", 0x15DCA0, "check_void_patch"),
        ("PrivacyAuthorization.dll", 0x2AA80, "check_void_patch"),
        ("PrivacyAuthorization.dll", 0x40CC0, "check_void_patch"),
        ("PrivacyAuthorization.dll", 0x41A90, "check_void_patch"),
        ("PrivacyAuthorization.dll", 0x422D0, "check_void_patch"),
    )
    for filename, offset, label in sites:
        require(
            profile_bytes(profile, filename, offset) == payload(source, label),
            f"Profile and synthetic instructions differ: {filename} at {offset:#x}",
        )

    # The rel32 jumps must land at the corresponding synthetic cleanup label.
    for label, jump_at, distance in ((".Ldiscard", 7, 0x6E), (".Ltyped", 3, 0x3D9)):
        code = payload(source, label)
        require(code[jump_at] == 0xE9, f"Missing near jump at {label}")
        target = jump_at + 5 + struct.unpack_from("<i", code, jump_at + 1)[0]
        require(target == distance, f"Unexpected cleanup branch target at {label}")
        fill = (
            rf"^{re.escape(label)}:\n[^\n]+\n[ \t]*\.fill "
            rf"(0x[0-9a-fA-F]+) - \(\. - {re.escape(label)}\), 1, 0xcc\n"
            rf"{re.escape(label)}_cleanup:"
        )
        matches = re.findall(fill, source, re.MULTILINE)
        require(
            len(matches) == 1 and int(matches[0], 0) == distance,
            f"Synthetic cleanup placement differs at {label}",
        )
    require(payload(source, "check_false_patch") == bytes.fromhex("33c0c3"),
            "The upload start must return false")
    require(payload(source, "check_void_patch") == bytes.fromhex("c3"),
            "The void-return probe must contain only RET")
    return len(sites)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--profile", type=Path,
        default=HERE.parent / "profiles" / "miui-2.5.5.643.json",
    )
    args = parser.parse_args()
    profile = json.loads(args.profile.read_text(encoding="utf-8-sig"))
    source = (HERE / "telemetry_probe.S").read_text(encoding="ascii")
    count = verify(profile, source)
    print(json.dumps({"telemetryProfilePatches": count, "vendorCodeExecuted": False}))


if __name__ == "__main__":
    main()
