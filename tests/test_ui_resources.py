"""Offline tests use synthetic controls and our original title artwork only."""
import hashlib
import struct
import sys
import unittest
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import ui_resources as ui


def synthetic_xml(member):
    """This small fixture is authored here; no vendor XML is distributed."""
    root = ET.Element("Window", {"size": "800,600"})
    ET.SubElement(root, "Font", {"id": "0", "name": "Original Fixture Font", "size": "12"})
    tabs = ET.SubElement(root, "TabLayout", {"name": "native_selected_page", "selectedid": "2"})
    for name in ui.ui_changes(member):
        kind = "Control" if name.startswith("cl_") else "Text" if name.startswith("text_") else "Button"
        attributes = {"name": name, "font": "0"}
        if name == "btn_show_panel":
            attributes["enabled"] = "false"  # Only the native readiness path may change this.
        ET.SubElement(tabs, kind, attributes)
    ET.SubElement(tabs, "Text", {"name": "authorization_fixture", "text": "Confirm on your phone & keep error messages"})
    return ET.tostring(root, encoding="utf-8")


def controls(data):
    root, _ = ui.parse_ui_xml(data)
    return {e.get("name"): e for e in root.iter() if e.get("name") and e.tag != "Font"}


class UiResourcesTests(unittest.TestCase):
    def test_bindings_classes_and_native_tab_selection_are_preserved(self):
        for member in ui.MEMBERS:
            with self.subTest(member=member):
                original = synthetic_xml(member)
                patched = ui.transform(original, member)[0]
                before, after = controls(original), controls(patched)
                self.assertEqual(set(before), set(after))
                self.assertEqual({name: e.tag for name, e in before.items()}, {name: e.tag for name, e in after.items()})
                self.assertEqual(before["native_selected_page"].attrib, after["native_selected_page"].attrib)
                self.assertEqual(before["authorization_fixture"].attrib, after["authorization_fixture"].attrib)

    def test_privacy_controls_are_disabled_hidden_and_outside_layout(self):
        member = "xml/setting_window.xml"
        result = controls(ui.transform(synthetic_xml(member), member)[0])
        for name in ("cl_priavcy", "btn_priavcy", "text_priavcy", "btn_priavcy_set"):
            for attribute in ("visible", "enabled", "mouse"):
                self.assertEqual(result[name].get(attribute), "false")
            self.assertEqual(result[name].get("pos"), "-1000,-1000,-999,-999")
            self.assertEqual(result[name].get("float"), "true")
        self.assertEqual(result["cl_about"].get("pos"), "0,113,2,135")

    def test_commented_privacy_link_is_removed_without_creating_a_control(self):
        member = "xml/setting_window.xml"
        original = synthetic_xml(member).replace(
            b'</Window>', b'<!-- <Button name="btn_license" text="Privacy fixture"/> --></Window>')
        self.assertNotIn("btn_license", controls(original))
        patched = ui.transform(original, member)[0]
        self.assertNotIn("btn_license", controls(patched))
        self.assertNotIn(b'btn_license', patched)

    def test_active_privacy_link_is_disabled_without_changing_its_binding(self):
        member = "xml/setting_window.xml"
        original = synthetic_xml(member).replace(
            b'</Window>', b'<Button name="btn_license" text="Privacy fixture" enabled="true"/>'
            b'<!-- <Button name="btn_license"/> --></Window>')
        result = controls(ui.transform(original, member)[0])["btn_license"]
        self.assertEqual(result.tag, "Button")
        self.assertEqual(result.get("text"), "Privacy fixture")
        for attribute, value in ui.hidden_control_attributes().items():
            self.assertEqual(result.get(attribute), value)

    def test_panel_fix_does_not_override_native_enabled_state(self):
        member = "xml/main/main_window.xml"
        result = controls(ui.transform(synthetic_xml(member), member)[0])
        self.assertEqual(result["btn_show_panel"].get("enabled"), "false")
        self.assertNotIn("enabled", ui.ui_changes(member)["btn_show_panel"])

    def test_disconnect_background_has_no_zero_center_nine_slice(self):
        member = "xml/main/main_window.xml"
        result = controls(ui.transform(synthetic_xml(member), member)[0])["btn_close_link"]
        self.assertEqual(result.get("pos"), "378,401,522,441")
        for attribute, filename in (("normalimage", "normal"), ("hotimage", "hover"), ("pushedimage", "press")):
            self.assertEqual(result.get(attribute), "image/button_" + filename + ".png")
            self.assertNotIn("corner=", result.get(attribute))

    def test_private_fonts_are_added_without_replacing_original_fonts(self):
        for member in ui.MEMBERS:
            with self.subTest(member=member):
                original = synthetic_xml(member)
                patched = ui.transform(original, member)[0]
                before, _ = ui.parse_ui_xml(original)
                after, _ = ui.parse_ui_xml(patched)
                fonts = {e.get("id"): e.attrib for e in after.iter("Font")}
                self.assertEqual(fonts["0"], next(before.iter("Font")).attrib)
                for font in ui.private_fonts(member):
                    self.assertEqual(fonts[font["id"]], font)
                    self.assertEqual(font["name"], "Microsoft YaHei UI")
                for element in after.iter():
                    for attribute in ("font", "hotfont", "pushedfont"):
                        if element.get(attribute):
                            self.assertIn(element.get(attribute), fonts)

    def test_private_font_collision_is_rejected(self):
        member = "xml/main/main_window.xml"
        original = synthetic_xml(member).replace(b'id="0"', b'id="200"', 1)
        with self.assertRaisesRegex(RuntimeError, "Private font id collision"):
            ui.transform(original, member)

    def test_missing_binding_is_rejected(self):
        member = "xml/setting_window.xml"
        original = synthetic_xml(member).replace(b'name="btn_priavcy"', b'name="unexpected_name"')
        with self.assertRaisesRegex(RuntimeError, "Expected exactly one control btn_priavcy"):
            ui.transform(original, member)

    def test_duplicate_binding_is_rejected(self):
        member = "xml/setting_window.xml"
        original = synthetic_xml(member).replace(b'</Window>', b'<Button name="btn_priavcy"/></Window>')
        with self.assertRaisesRegex(RuntimeError, "Expected exactly one control btn_priavcy"):
            ui.transform(original, member)

    def test_original_title_png_is_deterministic_valid_and_fits_allocation(self):
        data = ui.title_png()
        self.assertEqual(data, ui.title_png())
        self.assertEqual(data[:8], b"\x89PNG\r\n\x1a\n")
        self.assertLessEqual(len(data), 2379)
        position, chunks = 8, {}
        while position < len(data):
            size = struct.unpack_from(">I", data, position)[0]
            kind = data[position + 4:position + 8]
            body = data[position + 8:position + 8 + size]
            crc = struct.unpack_from(">I", data, position + 8 + size)[0]
            self.assertEqual(crc, zlib.crc32(kind + body) & 0xFFFFFFFF)
            self.assertNotIn(kind, chunks)
            chunks[kind] = body
            position += 12 + size
        self.assertEqual(position, len(data))
        self.assertEqual(set(chunks), {b"IHDR", b"IDAT", b"IEND"})
        self.assertEqual(struct.unpack(">IIBBBBB", chunks[b"IHDR"]), (118, 24, 8, 6, 0, 0, 0))
        rows = zlib.decompress(chunks[b"IDAT"])
        # Deflate bytes can vary across zlib versions; the raster must not.
        self.assertEqual(hashlib.sha256(rows).hexdigest(), "5016751f8c89b4c96edce8092844cc6a1f7a97e5fd83f0c52353f95abdf2d5f7")
        self.assertEqual(len(rows), 24 * (1 + 118 * 4))
        self.assertTrue(all(rows[y * 473] == 0 for y in range(24)))
        # Artwork has transparent margins and colored pixels, no vendor input.
        self.assertEqual(rows[4], 0)
        self.assertTrue(any(rows[y * 473 + 1 + x * 4 + 3] for y in range(24) for x in range(118)))


if __name__ == "__main__":
    unittest.main()
