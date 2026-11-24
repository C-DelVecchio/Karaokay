#!/usr/bin/env bash
# ADR-004 compliance guard (C2): CI must fail if any binary media file larger than 100 KB is
# committed outside public/demo-tracks. The demo tracks are the only media the repository is
# allowed to ship; commercial charts/downloads must never enter Git.
#
# Uses `git ls-files` so only tracked content is inspected (build output is ignored) and
# `git cat-file -s` so the committed blob size is measured, not the working-tree copy.
set -euo pipefail

limit_bytes=$((100 * 1024))
status=0

media_extensions=(
  # audio
  mp3 ogg oga opus wav flac m4a aac wma aiff aif ape
  # video
  mp4 m4v mkv webm mov avi mpg mpeg wmv flv
  # images
  png jpg jpeg gif webp bmp tif tiff avif heic
)

is_media_file() {
  local path="${1,,}"
  local extension="${path##*.}"
  [ "$extension" != "$path" ] || return 1
  local candidate
  for candidate in "${media_extensions[@]}"; do
    if [ "$extension" = "$candidate" ]; then
      return 0
    fi
  done
  return 1
}

is_allowed_path() {
  local path="${1,,}"
  case "$path" in
    public/demo-tracks/* | */public/demo-tracks/*) return 0 ;;
    *) return 1 ;;
  esac
}

while IFS= read -r -d '' file; do
  is_media_file "$file" || continue
  is_allowed_path "$file" && continue

  size=$(git cat-file -s "HEAD:$file" 2>/dev/null || git cat-file -s ":$file")
  if [ "$size" -gt "$limit_bytes" ]; then
    echo "::error file=$file::committed binary media is ${size} bytes (> 100 KiB) outside public/demo-tracks"
    status=1
  fi
done < <(git ls-files -z)

if [ "$status" -ne 0 ]; then
  echo
  echo "ADR-004 forbids committing binary media >100 KB outside public/demo-tracks." >&2
  echo "Move the file out of the repository or replace it with a synthetic generator artifact." >&2
fi

exit "$status"
