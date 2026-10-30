# Camera RAW previews

Job media keeps the uploaded RAW bytes and generates separate 384-pixel thumbnails
and 1600-pixel JPEG previews through the durable leased derivative queue. RAWs are
recognized by extension even when browsers upload them as application/octet-stream.
Job Details, Media Review and production image views use the generated JPEGs.

The Python helper uses rawpy/LibRaw to extract the largest embedded JPEG or bitmap.
When missing, unreadable or smaller than 1600 pixels on its longest edge, it renders
the sensor data at half size with camera white balance and sRGB output. Embedded
EXIF orientation takes precedence over LibRaw orientation; rendered sensor data is
already oriented by LibRaw. The original is never modified or replaced.

RAW processing runs in a separate process with one OpenMP thread, a configurable
90-second timeout (clamped to 5–300 seconds), and a 120-million sensor pixel limit.
Output JPEGs are limited to 1600 pixels and verified before Common.Storage writes.
Temporary files are deleted after success, failure, timeout or cancellation.
Existing storage permissions, original-download permissions, versioning and job
workflow rules remain in effect. Browser fallback requests queue RAW work rather
than decoding RAWs synchronously. Original uploads now support up to 512 MiB using disk-backed ingestion. WorkDay uses resumable four MiB transfers; see [WorkDay RAW ingestion](WORKDAY-RAW-INGESTION.md).

Unsupported/corrupt RAWs are marked Unsupported; missing Python/dependencies,
crashes and timeouts are Failed and use the existing bounded retry mechanism.
Supported camera models depend on the bundled LibRaw version. Recognizing a RAW
extension is not a guarantee that every camera/compression mode is supported.
These are review previews and may differ from the final editor's rendering.

## Kratos setup (one time)

Install the runtime on Kratos, with the Studio checkout present:

```bash
sudo apt-get install python3-venv
sudo bash scripts/Install-RawPreviewRuntime.sh /opt/aegis/studio-raw-preview
```

Set this environment variable in Studio's systemd service override:

```ini
[Service]
Environment=Media__RawPreview__PythonExecutable=/opt/aegis/studio-raw-preview/bin/python3
```

Reload systemd and restart the actual Studio service after the normal build/deploy.
The project stages Media/raw_preview.py and its pinned requirements into build
and publish output. The external virtual environment survives deploy-directory
replacement. On macOS/Windows, install the same requirements in a local Python
virtual environment and set Media:RawPreview:PythonExecutable to its interpreter.

Existing RAW versions previously marked Unsupported can be queued with the
Selected Image card's **Regenerate preview** action after runtime installation.
New RAW uploads queue automatically. During processing, refresh the media listing
to retrieve the Ready status and generated thumbnail.

## Validation

```bash
python3 -m pip install -r Aegis.Studio.Web/Media/raw-preview-requirements.txt
python3 -m unittest discover -s tests/raw-preview -v
```

Nine regression tests cover embedded JPEG/bitmap previews, small/missing/corrupt
preview fallback, EXIF/camera orientation, oversized RAW rejection, unchanged
source bytes, and actual LibRaw rejection of invalid files.

Implementation verification also rendered real Nikon NEF and Kodak KDC samples
from rawpy's upstream test data and checked that source SHA-256 hashes were
unchanged. Those samples are not redistributed in Studio. Run the normal local
.NET build/test and a Kratos upload test with Brian's actual camera files before
declaring camera-specific acceptance.

References: https://github.com/letmaik/rawpy and
https://letmaik.github.io/rawpy/api/rawpy.RawPy.html
