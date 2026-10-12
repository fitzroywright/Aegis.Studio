"""Render a camera RAW to a bounded JPEG; never write to the source file.

Exit codes: 0 ready, 2 unsupported/corrupt/oversize, 3 runtime failure.
Invoked only by Studio's background worker in a private temporary directory.
"""
import io
import sys

MAX_PIXELS = 120_000_000
PREVIEW_EDGE = 1600


def orient_embedded(image, flip):
    from PIL import Image, ImageOps
    # Honor explicit JPEG EXIF; otherwise use the camera orientation from LibRaw.
    if image.getexif().get(274):
        return ImageOps.exif_transpose(image)
    operation = {3: Image.Transpose.ROTATE_180,
                 5: Image.Transpose.ROTATE_90,
                 6: Image.Transpose.ROTATE_270}.get(flip)
    return image.transpose(operation) if operation is not None else image.copy()


def render(source, destination):
    import rawpy
    from PIL import Image
    Image.MAX_IMAGE_PIXELS = MAX_PIXELS
    # open_file reads metadata without unpacking the full sensor image.
    with rawpy.RawPy() as raw:
        raw.open_file(source)
        sizes = raw.sizes
        if sizes.raw_width <= 0 or sizes.raw_height <= 0 or sizes.raw_width * sizes.raw_height > MAX_PIXELS:
            raise ValueError("RAW dimensions exceed the supported limit")
        image = None
        try:
            thumb = raw.extract_thumb()
            if thumb.format == rawpy.ThumbFormat.JPEG:
                with Image.open(io.BytesIO(thumb.data)) as embedded:
                    if embedded.width * embedded.height > MAX_PIXELS:
                        raise ValueError("Embedded dimensions exceed the supported limit")
                    image = orient_embedded(embedded, sizes.flip).convert("RGB")
            elif thumb.format == rawpy.ThumbFormat.BITMAP:
                if thumb.data.shape[0] * thumb.data.shape[1] > MAX_PIXELS:
                    raise ValueError("Embedded dimensions exceed the supported limit")
                image = orient_embedded(Image.fromarray(thumb.data), sizes.flip).convert("RGB")
        except (rawpy.LibRawNoThumbnailError, rawpy.LibRawUnsupportedThumbnailError, OSError, ValueError):
            # Missing, malformed or unreadable embedded previews can still have valid RAW data.
            image = None
        if image is None or max(image.size) < PREVIEW_EDGE:
            if image is not None:
                image.close()
            raw.unpack()
            # LibRaw applies camera orientation on postprocess; do not rotate twice.
            rgb = raw.postprocess(half_size=True, use_camera_wb=True, output_bps=8,
                                  output_color=rawpy.ColorSpace.sRGB)
            image = Image.fromarray(rgb)
        try:
            image.thumbnail((PREVIEW_EDGE, PREVIEW_EDGE), Image.Resampling.LANCZOS)
            image.save(destination, format="JPEG", quality=88)
        finally:
            image.close()


def main():
    try:
        import rawpy
        if len(sys.argv) != 3:
            return 3
        try:
            render(sys.argv[1], sys.argv[2])
        except (rawpy.LibRawError, ValueError, OSError):
            return 2
        return 0
    except Exception:
        return 3


if __name__ == "__main__":
    sys.exit(main())
