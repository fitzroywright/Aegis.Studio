#!/usr/bin/env bash
set -euo pipefail

photo_root="${AEGIS_STUDIO_DEMO_PHOTOS:-/var/lib/aegis-studio/demo-photos}"
unit_name="${AEGIS_STUDIO_SYSTEMD_UNIT:-aegis-studio}"
photo_zip="${1:-}"

if [[ "$EUID" -ne 0 ]]; then
  echo "Run this script with sudo so it can create the persistent photo folder and set its owner." >&2
  exit 1
fi
if [[ -z "$photo_zip" || ! -f "$photo_zip" ]]; then
  echo "Usage: sudo bash $0 /path/to/Bryans-Studio-Demo-Photo-Library.zip" >&2
  exit 2
fi
command -v systemctl >/dev/null
command -v file >/dev/null
command -v unzip >/dev/null

service_user="$(systemctl show "$unit_name" -p User --value 2>/dev/null || true)"
service_group="$(systemctl show "$unit_name" -p Group --value 2>/dev/null || true)"
main_pid="$(systemctl show "$unit_name" -p MainPID --value 2>/dev/null || true)"
if [[ -z "$service_user" && "${main_pid:-0}" =~ ^[1-9][0-9]*$ ]]; then
  service_user="$(ps -o user= -p "$main_pid" | xargs)"
fi
if [[ -z "$service_user" ]]; then service_user=root; fi
if [[ -z "$service_group" ]]; then service_group="$(id -gn "$service_user")"; fi

install -d -o "$service_user" -g "$service_group" -m 0750 "$photo_root"
temporary="$(mktemp -d)"
trap 'rm -rf "$temporary"' EXIT
unzip -q "$photo_zip" -d "$temporary/unpacked"
installed=0
while IFS= read -r -d '' source; do
  name="${source##*/}"
  lower="$(printf '%s' "$source" | tr '[:upper:]' '[:lower:]')"
  case "$lower" in
    *wedding*|*engagement*) category=wedding ;;
    *graduation*|*school*) category=graduation ;;
    *newborn*) category=newborn ;;
    *family*) category=family ;;
    *corporate*) category=people ;;
    *sport*|*athlete*|*team*) category=sports ;;
    *product*|*commercial*) category=product ;;
    *portrait*|*headshot*|*consultation*|*passport*|*/id-*) category=people ;;
    *) continue ;;
  esac
  mime="$(file --brief --mime-type "$source")"
  [[ "$mime" == image/* ]] || { echo "Skipping non-image entry: $source" >&2; continue; }
  destination="$photo_root/$category/BryansStudio-$name"
  install -D -o "$service_user" -g "$service_group" -m 0640 "$source" "$destination"
  installed=$((installed + 1))
done < <(find "$temporary/unpacked" -type f \( -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.png' -o -iname '*.webp' \) -print0)
if [[ "$installed" -eq 0 ]]; then
  echo "No recognized demo photos were found in $photo_zip." >&2
  exit 1
fi


count="$(find "$photo_root" -type f \( -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.png' -o -iname '*.webp' \) | wc -l | xargs)"
echo "Studio demo photo library ready: $photo_root ($count local image files). Demo generation uses only these installed photos."
