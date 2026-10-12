#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <core-bin.zip>" >&2
  exit 1
fi

zipfile=$(readlink -f "$1")
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

unzip -q "$zipfile" -d "$tmp"

while IFS= read -r dir; do
  rm -rf "$dir"
done < <(find "$tmp" -type d \( -name sing_box -o -name mihomo -o -name srss \))

find "$tmp" -type f \( \
    -name 'geoip.metadb' -o \
    -name 'Country.mmdb' -o \
    -name 'libcronet*' -o \
    -name 'sing-box*' -o \
    -name 'mihomo*' -o \
    -name '*.srs' \
  \) -delete

leftover=$(find "$tmp" \( \
    -iname '*sing-box*' -o \
    -iname '*sing_box*' -o \
    -iname '*mihomo*' -o \
    -name '*.srs' \
  \) -print || true)

if [[ -n "$leftover" ]]; then
  echo "extra cores remain after slim:" >&2
  echo "$leftover" >&2
  exit 1
fi

if ! find "$tmp" -type f \( -name xray -o -name xray.exe \) | grep -q .; then
  echo "xray binary missing after slim" >&2
  exit 1
fi

rm -f "$zipfile"
( cd "$tmp" && zip -r -9 "$zipfile" . >/dev/null )
echo "slimmed $zipfile ($(du -h "$zipfile" | awk '{print $1}'))"
