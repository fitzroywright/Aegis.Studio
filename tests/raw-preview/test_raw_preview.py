import importlib.util
import io
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import numpy as np
from PIL import Image
import rawpy

HELPER = Path(__file__).resolve().parents[2] / "Aegis.Studio.Web/Media/raw_preview.py"
spec = importlib.util.spec_from_file_location("raw_preview", HELPER)
preview = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preview)


class FakeRaw:
    def __init__(self, thumb=None, flip=0):
        self.sizes = SimpleNamespace(raw_width=4000, raw_height=3000, flip=flip)
        self.thumb = thumb
        self.unpacked = False
        self.processed = False

    def __enter__(self): return self
    def __exit__(self, *args): pass
    def open_file(self, path): self.source = path
    def extract_thumb(self):
        if self.thumb is None:
            raise rawpy.LibRawNoThumbnailError()
        return self.thumb
    def unpack(self): self.unpacked = True
    def postprocess(self, **kwargs):
        self.processed = True
        self.options = kwargs
        return np.zeros((1200, 2000, 3), dtype=np.uint8)


def jpeg(width, height, orientation=None):
    output = io.BytesIO()
    with Image.new("RGB", (width, height), (30, 90, 150)) as image:
        exif = Image.Exif()
        if orientation is not None: exif[274] = orientation
        image.save(output, "JPEG", exif=exif)
    return SimpleNamespace(format=rawpy.ThumbFormat.JPEG, data=output.getvalue())


class RawPreviewTests(unittest.TestCase):
    def render(self, fake):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "source.dat"
            source.write_bytes(b"original RAW bytes")
            result = Path(folder) / "preview.jpg"
            with patch.object(rawpy, "RawPy", return_value=fake):
                preview.render(str(source), str(result))
            self.assertEqual(source.read_bytes(), b"original RAW bytes")
            with Image.open(result) as image:
                self.assertEqual(image.format, "JPEG")
                self.assertLessEqual(max(image.size), 1600)
                return image.size

    def test_large_embedded_jpeg_avoids_sensor_decode(self):
        fake = FakeRaw(jpeg(2000, 1200))
        self.assertEqual(self.render(fake), (1600, 960))
        self.assertFalse(fake.unpacked)
        self.assertFalse(fake.processed)

    def test_small_embedded_jpeg_falls_back_to_raw(self):
        fake = FakeRaw(jpeg(200, 120))
        self.assertEqual(self.render(fake), (1600, 960))
        self.assertTrue(fake.unpacked)
        self.assertTrue(fake.options["half_size"])
        self.assertTrue(fake.options["use_camera_wb"])
        self.assertEqual(fake.options["output_bps"], 8)

    def test_missing_embedded_preview_falls_back(self):
        fake = FakeRaw()
        self.render(fake)
        self.assertTrue(fake.processed)

    def test_corrupt_embedded_jpeg_falls_back(self):
        fake = FakeRaw(SimpleNamespace(format=rawpy.ThumbFormat.JPEG, data=b"corrupt"))
        self.render(fake)
        self.assertTrue(fake.processed)

    def test_bitmap_preview(self):
        fake = FakeRaw(SimpleNamespace(format=rawpy.ThumbFormat.BITMAP,
                                      data=np.zeros((1200, 2000, 3), dtype=np.uint8)))
        self.assertEqual(self.render(fake), (1600, 960))
        self.assertFalse(fake.processed)

    def test_camera_orientation_when_jpeg_has_no_exif(self):
        self.assertEqual(self.render(FakeRaw(jpeg(2000, 1200), flip=6)), (960, 1600))

    def test_exif_orientation_is_not_applied_twice(self):
        self.assertEqual(self.render(FakeRaw(jpeg(2000, 1200, orientation=6), flip=6)), (960, 1600))

    def test_oversize_raw_rejected_before_sensor_decode(self):
        fake = FakeRaw()
        fake.sizes.raw_width = 100000
        with self.assertRaises(ValueError): self.render(fake)
        self.assertFalse(fake.unpacked)

    def test_real_decoder_rejects_invalid_raw(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "corrupt.CR3"
            source.write_bytes(b"invalid RAW")
            with patch.object(preview.sys, "argv", [str(HELPER), str(source), str(Path(folder) / "out.jpg")]):
                self.assertEqual(preview.main(), 2)


if __name__ == "__main__": unittest.main()
