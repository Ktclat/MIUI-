"""Pure offline PE/assembler validation. Input files are only read as bytes."""
from pathlib import Path
import argparse
import json
import struct
import sys
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import background_video_transform as bg
from prepare import apply_bytes, sha256
from verify_assembly import verify

HERE=Path(__file__).resolve().parent
BASE = None
OBJECT = None


class TransformTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        profile = json.loads((HERE.parent/'profiles/miui-2.5.5.643.json').read_text(encoding='utf-8-sig'))
        row = next(row for row in profile['files'] if row['path'] == 'MiScreenShare.exe')
        if sha256(BASE) != row['sourceSha256']:
            raise ValueError('Original media file does not match the pinned vendor input')
        cls.source_bytes = BASE.read_bytes()
        cls.original = apply_bytes(cls.source_bytes, row['patches'])
        cls.candidate,cls.report=bg.transform(cls.original)

    def test_pinned_hashes(self):
        self.assertEqual(bg.sha(self.original),bg.BASELINE_SHA256)
        self.assertEqual(bg.sha(self.candidate),bg.CANDIDATE_SHA256)

    def test_deterministic_transform(self):
        again,_=bg.transform(self.original)
        self.assertEqual(again,self.candidate)

    def test_reject_other_build_and_double_patch(self):
        for bad in (b"",self.original[:-1],self.original+b"0",self.candidate):
            with self.assertRaises(ValueError):bg.transform(bad)

    def test_reject_truncated_pe(self):
        for n in (0,1,63,100,0x140,0x200,1024,7980000):
            with self.assertRaises((ValueError,struct.error)):bg.layout(self.original[:n])

    def test_rel32_bounds(self):
        self.assertEqual(bg.displacement(100,100),bytes(4))
        self.assertEqual(bg.displacement(1<<31,0),bytes.fromhex("00000080"))
        with self.assertRaises(ValueError):bg.displacement(0,1<<31)
        with self.assertRaises(ValueError):bg.displacement((1<<31)+1,0)

    def test_alignment_checks(self):
        self.assertEqual(bg.align(513,512),1024)
        for a in (0,3,24,-1):
            with self.assertRaises(ValueError):bg.align(513,a)

    def test_rollback_and_rebuild(self):
        restored=bytearray(self.candidate[:len(self.original)])
        for row in self.report["rollback_ranges"]:
            p=int(row["fileOffset"],16);before=bytes.fromhex(row["beforeHex"])
            self.assertEqual(restored[p:p+len(before)],bytes.fromhex(row["afterHex"]))
            restored[p:p+len(before)]=before
        self.assertEqual(bytes(restored),self.original)
        rebuilt,_=bg.transform(bytes(restored))
        self.assertEqual(rebuilt,self.candidate)

    def test_exception_entries_and_unwind(self):
        meta=bg.layout(self.candidate)
        directory,size=struct.unpack_from("<II",self.candidate,meta["opt"]+112+3*8)
        raw=bg.offset(meta,directory,size)
        entries=[struct.unpack_from("<III",self.candidate,raw+i) for i in range(0,size,12)]
        self.assertEqual(len(entries),8064)
        for a,b in zip(entries,entries[1:]):self.assertLessEqual(a[1],b[0])
        expected=[(40,[3],5),(40,[3],5),(88,[7,6,3],7)]
        for row,expect in zip(entries[-3:],expected):
            u=bg.offset(meta,row[2],4);header=self.candidate[u:u+4]
            self.assertEqual(header[0],1) # version1, no handler/chained flags
            self.assertEqual(header[1],expect[2]);self.assertEqual(header[3],0)
            amount=0;regs=[];last=256
            for i in range(header[2]):
                code,info=self.candidate[u+4+2*i:u+6+2*i]
                self.assertLessEqual(code,last);last=code
                op,reg=info&15,info>>4
                if op==0:amount+=8;regs.append(reg)
                elif op==2:amount+=reg*8+8
                else:self.fail("unexpected unwind opcode")
            self.assertEqual((amount,regs),expect[:2])
            self.assertEqual((8-amount)%16,0)
            self.assertGreaterEqual(amount-len(regs)*8,32) # Win64 shadow space

    def test_assembler_reproducibility(self):
        result=verify(OBJECT)
        self.assertTrue(result["assembler_matches_embedded_code"])

    def test_hidden_only_resume_hook(self):
        meta=bg.layout(self.candidate)
        raw=bg.offset(meta,0x6EE44,12)
        # cmp [frame+1c],0; jne +7 skips mov dl,1 AND the entire hooked call.
        self.assertEqual(self.candidate[raw:raw+7],bytes.fromhex("80791c007507b2"))
        source=0x6EE4A
        self.assertEqual(source+7,0x6EE51)

    def test_original_sections_and_overlay_retained(self):
        old=bg.layout(self.original);new=bg.layout(self.candidate)
        self.assertEqual(old["sections"],new["sections"][:-1])
        self.assertEqual(new["sections"][-1]["flags"],0x60000020)
        end=max(s["raw_offset"]+s["raw_size"] for s in old["sections"])
        self.assertEqual(self.original[end:],self.candidate[end:len(self.original)])
        self.assertEqual(BASE.read_bytes(),self.source_bytes)


if __name__=="__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--original-media', type=Path, required=True)
    parser.add_argument('--object', type=Path, required=True)
    args, remaining = parser.parse_known_args()
    BASE, OBJECT = args.original_media, args.object
    unittest.main(argv=[sys.argv[0], *remaining], verbosity=2)
