#!/usr/bin/env bash
set -euo pipefail
# Run on the Studio host. Keep this venv outside the replaceable publish directory.
studio_raw_runtime="${1:-/opt/aegis/studio-raw-preview}"
studio_repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
python3 -m venv "$studio_raw_runtime"
"$studio_raw_runtime/bin/python3" -m pip install -r "$studio_repo_root/Aegis.Studio.Web/Media/raw-preview-requirements.txt"
"$studio_raw_runtime/bin/python3" -c 'import rawpy; from PIL import Image; print("RAW decoder ready; LibRaw:", rawpy.libraw_version)'
printf 'Configure Studio: Media__RawPreview__PythonExecutable=%s/bin/python3\n' "$studio_raw_runtime"
