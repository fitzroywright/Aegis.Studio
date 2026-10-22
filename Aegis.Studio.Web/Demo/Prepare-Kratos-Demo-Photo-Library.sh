#!/usr/bin/env bash
set -euo pipefail

photo_root="${AEGIS_STUDIO_DEMO_PHOTOS:-/var/lib/aegis-studio/demo-photos}"
unit_name="${AEGIS_STUDIO_SYSTEMD_UNIT:-aegis-studio}"
photo_source="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/Photos"
categories=(people wedding graduation family sports product)

if [[ "$EUID" -ne 0 ]]; then
  echo "Run this script with sudo so it can create the persistent photo folder and set its owner." >&2
  exit 1
fi
if [[ ! -d "$photo_source" ]]; then
  echo "Bundled Studio demo photographs are missing from '$photo_source'." >&2
  exit 1
fi
command -v systemctl >/dev/null
command -v file >/dev/null

service_user="$(systemctl show "$unit_name" -p User --value 2>/dev/null || true)"
service_group="$(systemctl show "$unit_name" -p Group --value 2>/dev/null || true)"
main_pid="$(systemctl show "$unit_name" -p MainPID --value 2>/dev/null || true)"
if [[ -z "$service_user" && "${main_pid:-0}" =~ ^[1-9][0-9]*$ ]]; then
  service_user="$(ps -o user= -p "$main_pid" | xargs)"
fi
if [[ -z "$service_user" ]]; then service_user=root; fi
if [[ -z "$service_group" ]]; then service_group="$(id -gn "$service_user")"; fi

install -d -o "$service_user" -g "$service_group" -m 0750 "$photo_root"
total=0
for category in "${categories[@]}"; do
  source_directory="$photo_source/$category"
  if [[ ! -d "$source_directory" ]]; then
    echo "Bundled demo photo category is missing: '$source_directory'." >&2
    exit 1
  fi
  count=0
  while IFS= read -r -d '' source; do
    mime="$(file --brief --mime-type "$source")"
    [[ "$mime" == image/* ]] || { echo "Bundled file is not a usable image: $source" >&2; exit 1; }
    name="${source##*/}"
    install -D -o "$service_user" -g "$service_group" -m 0640 "$source" "$photo_root/$category/BryansStudio-$name"
    count=$((count + 1))
    total=$((total + 1))
  done < <(find "$source_directory" -maxdepth 1 -type f \( -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.png' -o -iname '*.webp' \) -print0 | sort -z)
  if [[ "$count" -eq 0 ]]; then
    echo "Bundled demo photo category '$category' has no supported images." >&2
    exit 1
  fi
done

echo "Studio demo photo library ready: $photo_root ($total bundled local image files across ${#categories[@]} required categories). Demo generation does not download images."
