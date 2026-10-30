"""Container-level acceptance; these fixtures do not claim sensor-decoder support."""
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest

HELPER = Path(__file__).resolve().parents[2] / 'Aegis.Studio.Web/Media/photo_probe.py'
spec = importlib.util.spec_from_file_location('photo_probe', HELPER)
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


def tiff(extension, maker=''):
    data = bytearray(1024)
    magic = {'.orf': 0x4f52, '.rw2': 85}.get(extension, 42)
    struct.pack_into('<2sHI', data, 0, b'II', magic, 16)
    if extension == '.cr2':
        data[8:12] = b'CR\x02\0'
        struct.pack_into('<I', data, 12, 16)
    tags = [(256, 4, 1, 2), (257, 4, 1, 2), (262, 3, 1, 32803), (273, 4, 1, 700), (279, 4, 1, 4)]
    if extension == '.rw2':
        tags = [(2, 4, 1, 2), (3, 4, 1, 2), (280, 4, 1, 700)]
    if extension == '.dng':
        tags.append((50706, 1, 4, 0x401))
    if maker:
        text = maker.encode() + b'\0'
        data[400:400 + len(text)] = text
        tags.append((271, 2, len(text), 400))
    struct.pack_into('<H', data, 16, len(tags))
    for i, entry in enumerate(tags):
        struct.pack_into('<HHII', data, 18 + i * 12, *entry)
    return data


def box(kind, payload):
    return struct.pack('>I4s', len(payload) + 8, kind) + payload


class PhotoProbeTests(unittest.TestCase):
    def inspect(self, contents, name):
        with tempfile.TemporaryDirectory() as directory:
            file = Path(directory) / 'original'
            file.write_bytes(contents)
            before = file.read_bytes()
            result = probe.inspect(file, name)
            self.assertEqual(file.read_bytes(), before)
            return result

    def test_tiff_camera_families_and_dng(self):
        for extension, maker in [('.cr2', 'Canon'), ('.nef', 'NIKON'), ('.nrw', 'NIKON'), ('.arw', 'SONY'), ('.orf', 'OLYMPUS'), ('.orf', 'OM Digital Solutions'), ('.rw2', 'Panasonic'), ('.dng', '')]:
            with self.subTest(extension=extension, maker=maker):
                self.assertTrue(self.inspect(tiff(extension, maker), 'capture' + extension)['raw'])

    def test_cr3_has_camera_brand_metadata_and_nonempty_media(self):
        data = box(b'ftyp', b'crx \0\0\0\0crx ') + box(b'moov', b'camera') + box(b'mdat', b'sensor')
        self.assertEqual(self.inspect(data, 'capture.cr3')['format'], 'CR3')
        for invalid in [data[:-1], data.replace(b'crx ', b'mp42'), box(b'ftyp', b'crx \0\0\0\0') + box(b'moov', b'') + box(b'mdat', b'')]:
            with self.assertRaises(ValueError): self.inspect(invalid, 'capture.cr3')

    def test_raf_checks_sensor_offset_and_preview_bounds(self):
        data = bytearray(256);data[:16] = b'FUJIFILMCCD-RAW '
        struct.pack_into('>6I', data, 84, 120, 4, 0, 0, 200, 56)
        data[120:124] = b'\xff\xd8\xff\xd9'
        self.assertTrue(self.inspect(data, 'capture.raf')['raw'])
        struct.pack_into('>I', data, 104, 1000)
        with self.assertRaises(ValueError): self.inspect(data, 'capture.raf')

    def test_wrong_maker_extension_malformed_offsets_and_directory_bomb(self):
        with self.assertRaises(ValueError): self.inspect(tiff('.nef', 'SONY'), 'capture.nef')
        data = tiff('.dng');struct.pack_into('<I', data, 4, 2**32 - 1)
        with self.assertRaises(ValueError): self.inspect(data, 'capture.dng')
        data = tiff('.dng');struct.pack_into('<H', data, 16, 65535)
        with self.assertRaises(ValueError): self.inspect(data, 'capture.dng')
        data = tiff('.dng');struct.pack_into('<I', data, 26, 120_000_001)
        with self.assertRaises(ValueError): self.inspect(data, 'capture.dng')

    def test_unsupported_payloads_and_jpeg_truncation(self):
        for extension in probe.RAW:
            with self.subTest(extension=extension), self.assertRaises(ValueError):
                self.inspect(b'<html>not a camera original</html>', 'capture' + extension)
        with self.assertRaises(ValueError): self.inspect(b'\xff\xd8\xffcorrupt', 'capture.jpg')
        self.assertEqual(self.inspect(b'\xff\xd8\xff\xe0\xff\xd9', 'capture.jpg')['contentType'], 'image/jpeg')
        self.assertEqual(self.inspect(tiff('.tiff'), 'capture.tiff')['contentType'], 'image/tiff')


if __name__ == '__main__': unittest.main()
