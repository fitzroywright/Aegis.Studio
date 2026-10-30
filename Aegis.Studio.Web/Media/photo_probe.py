"""Bounded, dependency-free container inspection for private photo ingestion.
No decoder availability decision is made here: unsupported RAW previews retain originals.
"""
import json
import os
import struct
import sys

RAW = {'.cr2', '.cr3', '.nef', '.nrw', '.arw', '.raf', '.orf', '.rw2', '.dng'}
TYPES = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8, 11: 4, 12: 8, 13: 4, 16: 8, 17: 8, 18: 8}
MAX_PIXELS = 120_000_000


def inspect(path, name):
    extension = os.path.splitext(name)[1].lower()
    size = os.path.getsize(path)
    with open(path, 'rb') as source:
        budget = [1_048_576]

        def read(offset, length):
            if offset < 0 or length < 0 or offset + length > size or length > budget[0]:
                raise ValueError('Invalid photo container bounds')
            budget[0] -= length
            source.seek(offset)
            result = source.read(length)
            if len(result) != length:
                raise ValueError('Truncated photograph')
            return result

        header = read(0, min(size, 32))
        result = {'format': extension[1:].upper(), 'raw': extension in RAW,
                  'contentType': 'application/octet-stream', 'sizeBytes': size}
        if extension in ('.jpg', '.jpeg'):
            if header[:3] != b'\xff\xd8\xff' or size < 4 or read(size - 2, 2) != b'\xff\xd9':
                raise ValueError('Invalid JPEG')
            result['contentType'] = 'image/jpeg'
        elif extension == '.png':
            if header[:8] != b'\x89PNG\r\n\x1a\n' or header[12:16] != b'IHDR' or read(size - 8, 4) != b'IEND':
                raise ValueError('Invalid PNG')
            result.update(width=int.from_bytes(header[16:20], 'big'), height=int.from_bytes(header[20:24], 'big'), contentType='image/png')
        elif extension == '.webp':
            if header[:4] != b'RIFF' or header[8:12] != b'WEBP' or int.from_bytes(header[4:8], 'little') + 8 != size:
                raise ValueError('Invalid WebP')
            result['contentType'] = 'image/webp'
        elif extension in ('.cr3', '.heic', '.heif'):
            if header[4:8] != b'ftyp':
                raise ValueError('Invalid camera media container')
            offset, boxes, brands, media_bytes = 0, set(), [], 0
            for _ in range(4096):
                if offset == size:
                    break
                box = read(offset, 8)
                length, kind = int.from_bytes(box[:4], 'big'), box[4:8]
                base = 8
                if length == 1:
                    length, base = int.from_bytes(read(offset + 8, 8), 'big'), 16
                elif length == 0:
                    length = size - offset
                if length < base or offset + length > size:
                    raise ValueError('Invalid media box')
                boxes.add(kind)
                if kind == b'mdat':
                    media_bytes += length - base
                if kind == b'ftyp':
                    payload = read(offset + base, length - base)
                    if len(payload) < 8 or len(payload) > 1024:
                        raise ValueError('Invalid media brands')
                    brands = [payload[:4]] + [payload[i:i + 4] for i in range(8, len(payload), 4)]
                offset += length
            if offset != size or media_bytes < 1:
                raise ValueError('Missing camera media data')
            if extension == '.cr3':
                if b'crx ' not in brands or b'moov' not in boxes:
                    raise ValueError('Not a Canon CR3 container')
            elif not set(brands).intersection({b'heic', b'heix', b'hevc', b'hevx', b'mif1', b'msf1'}) or b'meta' not in boxes:
                raise ValueError('Not a HEIF photograph')
            else:
                result['contentType'] = 'image/heic'
        elif extension == '.raf':
            if header[:16] != b'FUJIFILMCCD-RAW ':
                raise ValueError('Not a Fujifilm RAF')
            offsets = read(84, 24)
            jpeg_offset, jpeg_size, _, _, raw_offset, raw_size = struct.unpack('>6I', offsets)
            if not raw_size or raw_offset < 108 or raw_offset + raw_size > size:
                raise ValueError('Invalid RAF sensor data')
            if jpeg_size and (jpeg_offset < 108 or jpeg_offset + jpeg_size > size or read(jpeg_offset, 2) != b'\xff\xd8'):
                raise ValueError('Invalid RAF embedded preview')
        elif extension in RAW or extension in ('.tif', '.tiff'):
            if header[:2] not in (b'II', b'MM'):
                raise ValueError('Not a TIFF camera container')
            order = '<' if header[:2] == b'II' else '>'
            u16 = lambda data: struct.unpack(order + 'H', data)[0]
            u32 = lambda data: struct.unpack(order + 'I', data)[0]
            magic = u16(header[2:4])
            expected = {'.orf': (0x4f52, 0x5352), '.rw2': (85,)}.get(extension, (42,))
            if magic not in expected:
                raise ValueError('Invalid TIFF camera signature')
            pending, seen, tags, pixel_data = [u32(header[4:8])], set(), {}, False
            if extension == '.cr2':
                if header[8:12] != b'CR\x02\x00':
                    raise ValueError('Not a Canon CR2')
                pending.append(u32(header[12:16]))
            while pending:
                offset = pending.pop()
                if not offset or offset in seen:
                    continue
                if len(seen) >= 32:
                    raise ValueError('Excessive camera directories')
                seen.add(offset)
                count = u16(read(offset, 2))
                if not 1 <= count <= 512:
                    raise ValueError('Invalid camera directory')
                entries = read(offset + 2, count * 12 + 4)
                local = {}
                for i in range(count):
                    tag, kind, length = struct.unpack(order + 'HHI', entries[i * 12:i * 12 + 8])
                    if kind not in TYPES or length > size:
                        raise ValueError('Invalid camera field')
                    n = TYPES[kind] * length
                    if n > size or (tag in (2, 3, 256, 257, 271, 272, 273, 279, 280, 324, 325, 330, 50706, 262) and n > 262144):
                        raise ValueError('Excessive camera metadata')
                    raw = entries[i * 12 + 8:i * 12 + 12]
                    if n > 4:
                        field_offset = u32(raw)
                        if field_offset + n > size:
                            raise ValueError('Camera metadata outside original')
                        # Only read interpreted fields; opaque maker data is never executed.
                        data = read(field_offset, n) if tag in (2, 3, 256, 257, 271, 272, 273, 279, 280, 324, 325, 330, 50706, 262) else b''
                    else:
                        data = raw[:n]
                    if kind == 2 and tag in (271, 272):
                        value = data.rstrip(b'\0').decode('ascii', errors='replace')[:200]
                    elif kind in (3, 4, 13) and data:
                        unit, decode = (2, u16) if kind == 3 else (4, u32)
                        value = [decode(data[j:j + unit]) for j in range(0, len(data), unit)]
                    else:
                        value = list(data)
                    local[tag] = value
                    if tag in (271, 272, 50706, 262, 33422):
                        tags[tag] = value
                    if tag == 330:
                        pending.extend(value)
                for offsets_tag, sizes_tag in ((273, 279), (324, 325)):
                    offsets, lengths = local.get(offsets_tag, []), local.get(sizes_tag, [])
                    if offsets and len(offsets) == len(lengths):
                        if any(o < 8 or n <= 0 or o + n > size for o, n in zip(offsets, lengths)):
                            raise ValueError('Camera image data outside original')
                        pixel_data = True
                if extension == '.rw2' and local.get(280):
                    raw_offset = local[280][0]
                    if raw_offset < 8 or raw_offset >= size:
                        raise ValueError('Panasonic sensor data outside original')
                    pixel_data = True
                dimensions = ((2, 'width'), (3, 'height')) if extension == '.rw2' else ((256, 'width'), (257, 'height'))
                for tag, label in dimensions:
                    if local.get(tag):
                        result[label] = max(result.get(label, 0), local[tag][0])
                pending.append(u32(entries[-4:]))
            maker = str(tags.get(271, '')).upper()
            # Maker names follow LibRaw/ExifTool camera families; OM SYSTEM uses ORF too.
            expected_maker = {'.cr2': ('CANON',), '.nef': ('NIKON',), '.nrw': ('NIKON',), '.arw': ('SONY',), '.orf': ('OLYMPUS', 'OM DIGITAL', 'OM SYSTEM'), '.rw2': ('PANASONIC', 'LEICA')}.get(extension)
            if not pixel_data or (expected_maker and not any(m in maker for m in expected_maker)) or (extension == '.dng' and 50706 not in tags):
                raise ValueError('Camera format does not match its contents')
            if extension in ('.nef', '.nrw', '.arw', '.dng') and not (33422 in tags or any(x in tags.get(262, []) for x in (32803, 34892))):
                raise ValueError('Missing camera sensor description')
            if extension in ('.tif', '.tiff'):
                result['contentType'] = 'image/tiff'
            result.update(make=tags.get(271), model=tags.get(272))
        else:
            raise ValueError('Unsupported photograph format')
        if ('width' in result and result['width'] <= 0) or ('height' in result and result['height'] <= 0) or result.get('width', 1) * result.get('height', 1) > MAX_PIXELS:
            raise ValueError('Photograph exceeds dimension limits')
        return result


if __name__ == '__main__':
    try:
        if len(sys.argv) != 3:
            sys.exit(2)
        print(json.dumps(inspect(sys.argv[1], sys.argv[2])))
    except (ValueError, OSError, struct.error, OverflowError):
        sys.exit(2)
