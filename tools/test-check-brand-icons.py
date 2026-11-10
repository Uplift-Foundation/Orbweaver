#!/usr/bin/env python3
"""Tests for tools/make-icons.py's mobile set and tools/check-brand-icons.py (CB-323).

    python3 tools/test-check-brand-icons.py

Every refusal is paired with a control that passes, so a checker that rejects everything (or a
fixture that is wrong in some other way) cannot look like it works. The negative control for the
decision in the plan, that the mobile 1024s use the 48-px cut and not the 512 one, is the 512 cut
drawn at 1024: it must fail the 29-px ring check where the shipped cut passes it.
"""

import importlib.util
import io
import os
import shutil
import struct
import sys
import tempfile
import unittest
import zlib
from contextlib import redirect_stdout

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("check_brand_icons", os.path.join(_HERE, "check-brand-icons.py"))
cbi = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cbi)
mk = cbi.mk

SHIPPED = cbi.DEFAULT_DIR


def img_of(pixels, size=1024, opaque=False):
    return cbi.read_png(mk.png_bytes(size, pixels, opaque))


def scaled_watch(scale):
    """The watch tile with the web enlarged: the shape of mistake the 0.47 rule exists for."""
    cv = mk._full_bleed_tile(1024)
    mk.draw_web(cv, 512, 512, scale * 1024 / 512, mk.mobile_cut(), (*mk.IDLE, 1.0), (*mk.GENERATING, 1.0))
    return cv.pixels()


class GeneratorTests(unittest.TestCase):
    def test_same_bytes_twice(self):
        for draw, opaque in ((mk.draw_ios_light, True), (mk.draw_ios_dark, False),
                             (mk.draw_ios_tinted, False), (mk.draw_watch, True)):
            self.assertEqual(mk.png_bytes(1024, draw(), opaque), mk.png_bytes(1024, draw(), opaque))
        self.assertEqual(mk.png_bytes(256, mk.draw_mark_template()), mk.png_bytes(256, mk.draw_mark_template()))

    def test_opaque_png_has_no_alpha_channel_and_rgba_does(self):
        rgb = img_of(mk.draw_ios_light(64), 64, True)
        rgba = img_of(mk.draw_ios_light(64), 64, False)
        self.assertEqual((rgb[2], rgba[2]), (2, 6))
        self.assertEqual(cbi.pixel(rgb, 3, 3), cbi.pixel(rgba, 3, 3))

    def test_write_png_opaque_flag(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "x.png")
            mk.write_png(p, 8, mk.draw_ios_light(8), True)
            self.assertEqual(cbi.read_png(p)[2], 2)
            mk.write_png(p, 8, mk.draw_ios_light(8))
            self.assertEqual(cbi.read_png(p)[2], 6)

    def test_mobile_uses_the_48_cut(self):
        self.assertEqual(mk.mobile_cut(), mk.cut_for(48))
        self.assertNotEqual(mk.mobile_cut(), mk.cut_for(512))

    def test_main_writes_the_mobile_set(self):
        with tempfile.TemporaryDirectory() as d:
            old = (mk.ASSETS, mk.MOBILE)
            mk.ASSETS, mk.MOBILE = os.path.join(d, "a"), os.path.join(d, "m")
            try:
                with redirect_stdout(io.StringIO()) as out:
                    mk.main()
            finally:
                mk.ASSETS, mk.MOBILE = old
            self.assertEqual(sorted(os.listdir(os.path.join(d, "m"))), sorted(n for n, *_ in cbi.CHECKS))
            self.assertIn("ios-tinted-1024.png", out.getvalue())
            self.assertEqual(cbi.check_dir(os.path.join(d, "m")), [])


class CrossDrivePathTests(unittest.TestCase):
    """relpath raises ValueError between Windows drives; CI's Windows leg hit it (a checkout on D:,
    a temp directory on C:). Forced here on every platform so macOS can see it too."""

    def setUp(self):
        self._real = os.path.relpath

        def other_drive(path, start=os.curdir):
            raise ValueError("path is on mount 'C:', start on mount 'D:'")
        os.path.relpath = other_drive

    def tearDown(self):
        os.path.relpath = self._real

    def test_shown_falls_back_to_the_path_as_given(self):
        self.assertEqual(mk._shown("/some/where/x.png"), "/some/where/x.png")

    def test_control_shown_is_relative_when_it_can_be(self):
        os.path.relpath = self._real
        self.assertEqual(mk._shown(os.path.join(mk.HERE, "docs", "x.png")), os.path.join("docs", "x.png"))

    def test_main_of_both_tools_survives_it(self):
        with tempfile.TemporaryDirectory() as d:
            old = (mk.ASSETS, mk.MOBILE)
            mk.ASSETS, mk.MOBILE = os.path.join(d, "a"), os.path.join(d, "m")
            try:
                with redirect_stdout(io.StringIO()) as out:
                    mk.main()
                self.assertIn(os.path.join(d, "m", "watchos-1024.png"), out.getvalue())
            finally:
                mk.ASSETS, mk.MOBILE = old
            with redirect_stdout(io.StringIO()) as out:
                self.assertEqual(cbi.main(["--dir", os.path.join(d, "m"), "--no-regen"]), 0)
            self.assertIn(os.path.join(d, "m"), out.getvalue())


class ShippedFilesTests(unittest.TestCase):
    def test_committed_files_pass_including_the_regeneration_compare(self):
        self.assertEqual(cbi.check_dir(SHIPPED), [])

    def test_legibility_numbers_the_thresholds_were_set_from(self):
        orb, ring = cbi.legibility(cbi.read_png(os.path.join(SHIPPED, "ios-light-1024.png")))
        self.assertGreater(orb, cbi.LIGHT_ORB_CONTRAST)
        self.assertGreater(ring, cbi.RING_COVERAGE)
        # and not by a hair: the heavier cut is the whole point of the decision
        self.assertGreater(ring, 0.38)


class ControlTests(unittest.TestCase):
    def test_512_cut_fails_the_small_size_ring_check_the_48_cut_passes(self):
        good = cbi.check_light(img_of(mk.draw_ios_light(1024), opaque=True))
        bad = cbi.check_light(img_of(mk.draw_ios_light(1024, mk.cut_for(512)), opaque=True))
        self.assertEqual(good, [])
        self.assertEqual(len(bad), 1)
        self.assertIn("outer-ring", bad[0])

    def test_dim_orb_fails_the_orb_contrast_check(self):
        cv = mk._full_bleed_tile(1024)
        mk.draw_web(cv, 512, 512, 2.0, mk.mobile_cut(), (*mk.IDLE, 1.0), (0x14, 0x1A, 0x20, 1.0))
        bad = cbi.check_light(img_of(cv.pixels(), opaque=True))
        self.assertTrue(any("orb's contrast" in f for f in bad), bad)

    def test_light_with_alpha_channel_is_refused(self):
        bad = cbi.check_light(img_of(mk.draw_ios_light(1024), opaque=False))
        self.assertEqual(len(bad), 1)
        self.assertIn("alpha channel", bad[0])

    def test_light_with_rounded_corners_is_refused(self):
        cv = mk.Canvas(1024)
        cv.rounded_rect(512, 512, 512, 180, (*mk.TILE, 1.0))
        mk.draw_web(cv, 512, 512, 2.0, mk.mobile_cut(), (*mk.IDLE, 1.0), (*mk.GENERATING, 1.0))
        bad = cbi.check_light(img_of(cv.pixels()))
        self.assertTrue(any("edge to edge" in f for f in bad), bad)

    def test_dark_control_and_refusals(self):
        self.assertEqual(cbi.check_dark(img_of(mk.draw_ios_dark())), [])
        self.assertIn("alpha channel", cbi.check_dark(img_of(mk.draw_ios_light(1024), opaque=True))[0])
        self.assertIn("corners are not transparent", cbi.check_dark(img_of(mk.draw_ios_light(1024)))[0])
        empty = img_of(mk.Canvas(1024).pixels())
        self.assertIn("orb at the centre", cbi.check_dark(empty)[0])

    def test_tinted_control_and_refusals(self):
        self.assertEqual(cbi.check_tinted(img_of(mk.draw_ios_tinted())), [])
        self.assertIn("alpha channel", cbi.check_tinted(img_of(mk.draw_ios_light(1024), opaque=True))[0])
        # the coloured dark variant is not a greyscale, and its corners are fine, so only one failure
        coloured = cbi.check_tinted(img_of(mk.draw_ios_dark()))
        self.assertEqual(len(coloured), 1)
        self.assertIn("not greyscale", coloured[0])
        self.assertIn("corners are not transparent", cbi.check_tinted(img_of(mk.draw_ios_light(1024)))[0])

    def test_watch_control_and_radius_rule(self):
        self.assertEqual(cbi.check_watch(img_of(mk.draw_watch(), opaque=True)), [])
        self.assertEqual(cbi.check_watch(img_of(scaled_watch(1.1), opaque=True)), [])
        # 1.3 puts the ring at 0.377 * 1.3 = 0.49 of the size, outside the 0.47 circle
        bad = cbi.check_watch(img_of(scaled_watch(1.3), opaque=True))
        self.assertTrue(any("outside 0.47" in f for f in bad), bad)
        self.assertIn("alpha channel", cbi.check_watch(img_of(mk.draw_watch(), opaque=False))[0])

    def test_the_48_cut_sits_inside_the_watch_circle_with_margin(self):
        extent = (mk.WEB_REACH + mk.mobile_cut()["w"] / 2) / 512
        self.assertLess(extent, mk.WATCH_SAFE - 0.05)

    def test_template_control_and_refusals(self):
        self.assertEqual(cbi.check_template(img_of(mk.draw_mark_template(), 256)), [])
        self.assertIn("alpha channel", cbi.check_template(img_of(mk.draw_ios_light(256), 256, True))[0])
        coloured = cbi.check_template(img_of(mk.draw_ios_dark(256), 256))
        self.assertTrue(any("distinct colours" in f for f in coloured), coloured)
        solid = cbi.check_template(img_of([(0, 0, 0, 255)] * 64, 8))
        self.assertTrue(any("clear and solid" in f for f in solid), solid)


class FileLevelTests(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp()
        for name, *_ in cbi.CHECKS:
            shutil.copy(os.path.join(SHIPPED, name), self.d)

    def tearDown(self):
        shutil.rmtree(self.d)

    def test_control_passes(self):
        self.assertEqual(cbi.check_dir(self.d), [])

    def test_missing_file(self):
        os.remove(os.path.join(self.d, "watchos-1024.png"))
        self.assertEqual(cbi.check_dir(self.d), ["watchos-1024.png: missing; run python3 tools/make-icons.py"])

    def test_non_generator_png_is_refused(self):
        with open(os.path.join(self.d, "ios-dark-1024.png"), "wb") as fh:
            fh.write(b"not a png")
        self.assertEqual(cbi.check_dir(self.d), ["ios-dark-1024.png: not a PNG"])

    def test_edited_pixel_fails_the_regeneration_compare_only(self):
        p = os.path.join(self.d, "ios-dark-1024.png")
        img = cbi.read_png(p)
        px = cbi.decoded_pixels(img)
        px[512 * 1024 + 512] = (255, 0, 0, 255)  # the orb's centre, still an opaque orb
        with open(p, "wb") as fh:
            fh.write(mk.png_bytes(1024, px))
        fails = cbi.check_dir(self.d)
        self.assertEqual(len(fails), 1)
        self.assertIn("pixels differ", fails[0])
        self.assertEqual(cbi.check_dir(self.d, regen=False), [])

    def test_main_exit_codes(self):
        with redirect_stdout(io.StringIO()) as out:
            self.assertEqual(cbi.main(["--dir", self.d]), 0)
        self.assertIn("ok: 5 files", out.getvalue())
        os.remove(os.path.join(self.d, "ios-light-1024.png"))
        with redirect_stdout(io.StringIO()) as out:
            self.assertEqual(cbi.main(["--dir", self.d, "--no-regen"]), 1)
        self.assertIn("FAIL ios-light-1024.png: missing", out.getvalue())


class PngReaderTests(unittest.TestCase):
    def _png(self, ctype=6, depth=8, filt=0, interlace=0, drop_ihdr=False):
        w = h = 2
        bpp = 3 if ctype == 2 else 4
        raw = b"".join(bytes([filt]) + b"\x10" * (w * bpp) for _ in range(h))

        def chunk(tag, data):
            return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))
        out = b"\x89PNG\r\n\x1a\n"
        if not drop_ihdr:
            out += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, depth, ctype, 0, 0, interlace))
        return out + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")

    def test_reads_both_layouts(self):
        self.assertEqual(cbi.read_png(self._png(6))[:3], (2, 2, 6))
        self.assertEqual(cbi.read_png(self._png(2))[:3], (2, 2, 2))

    def test_refusals(self):
        for kwargs, text in (({"depth": 16}, "need 8-bit"), ({"ctype": 0}, "need 8-bit"),
                             ({"interlace": 1}, "need 8-bit"), ({"filt": 1}, "filter is not 0"),
                             ({"drop_ihdr": True}, "no IHDR")):
            with self.assertRaises(cbi.PngError) as cm:
                cbi.read_png(self._png(**kwargs))
            self.assertIn(text, str(cm.exception))


class AreaAverageTests(unittest.TestCase):
    def test_flat_colour_and_exact_halves(self):
        flat = img_of([(10, 20, 30, 255)] * 64, 8, True)
        self.assertEqual(cbi.area_average(flat, 2)[1][1][:3], (10.0, 20.0, 30.0))
        half = img_of([(0, 0, 0, 255)] * 4 + [(200, 200, 200, 255)] * 4, 2, True)
        # 2x2: top row black, bottom white; averaged to 1 px = mid grey
        row = img_of([(0, 0, 0, 255)] * 2 + [(200, 200, 200, 255)] * 2, 2, True)
        self.assertAlmostEqual(cbi.area_average(row, 1)[0][0][0], 100.0)
        self.assertIsNotNone(half)

    def test_transparent_does_not_darken_the_colour(self):
        px = [(200, 100, 50, 255), (0, 0, 0, 0)] * 2
        r, g, b, a = cbi.area_average(img_of(px, 2), 1)[0][0]
        self.assertEqual((round(r), round(g), round(b), round(a)), (200, 100, 50, 128))

    def test_fully_transparent_is_zero(self):
        self.assertEqual(cbi.area_average(img_of([(0, 0, 0, 0)] * 4, 2), 1)[0][0], (0.0, 0.0, 0.0, 0.0))

    def test_contrast_is_symmetric_and_black_white_is_21(self):
        self.assertAlmostEqual(cbi.contrast((0, 0, 0), (255, 255, 255)), 21.0)
        self.assertEqual(cbi.contrast((9, 9, 9), (200, 200, 200)), cbi.contrast((200, 200, 200), (9, 9, 9)))
        self.assertLess(cbi.luminance((5, 5, 5)), cbi.luminance((50, 50, 50)))


if __name__ == "__main__":
    unittest.main(verbosity=2)
