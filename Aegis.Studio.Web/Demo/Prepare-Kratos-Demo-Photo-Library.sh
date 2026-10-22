#!/usr/bin/env bash
set -euo pipefail

photo_root="${AEGIS_STUDIO_DEMO_PHOTOS:-/var/lib/aegis-studio/demo-photos}"
unit_name="${AEGIS_STUDIO_SYSTEMD_UNIT:-aegis-studio}"
photo_zip="${1:-}"
categories=(people wedding graduation family sports product)

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

temporary="$(mktemp -d)"
trap 'rm -rf "$temporary"' EXIT
unzip -q "$photo_zip" -d "$temporary/unpacked"
install -d -o "$service_user" -g "$service_group" -m 0750 "$photo_root"
counts=(0 0 0 0 0 0)
total=0
while IFS= read -r -d '' source; do
  folder="${source%/*}"
  folder="${folder##*/}"
  case "$folder" in
    "Corporate & Groups"|"ID & Passport"|"Portraits"|"Studio") category=people; index=0 ;;
    "Weddings") category=wedding; index=1 ;;
    "Graduation & School") category=graduation; index=2 ;;
    "Family & Newborn") category=family; index=3 ;;
    "Sports & Teams") category=sports; index=4 ;;
    "Product & Commercial") category=product; index=5 ;;
    *) continue ;;
  esac
  mime="$(file --brief --mime-type "$source")"
  [[ "$mime" == image/* ]] || { echo "Recognized demo photo is not a usable image: $source" >&2; exit 1; }
  name="${source##*/}"
  install -D -o "$service_user" -g "$service_group" -m 0640 "$source" "$photo_root/$category/BryansStudio-$name"
  counts[$index]=$((counts[$index] + 1))
  total=$((total + 1))
done < <(find "$temporary/unpacked" -type f \( -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.png' -o -iname '*.webp' \) -print0)

for index in "${!categories[@]}"; do
  if [[ "${counts[$index]}" -eq 0 ]]; then
    echo "The supplied photo ZIP has no usable image for required category '${categories[$index]}'." >&2
    exit 1
  fi
done

echo "Studio demo photo library ready: $photo_root ($total local image files across ${#categories[@]} required categories). Demo generation does not download images."
