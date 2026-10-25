#!/usr/bin/env bash
# OpenVector RESEARCH build: regenerate (or --check) patches/0001-openvector-angular-gain.patch,
# the full diff between pristine upstream Raw Accel v1.7.1 and the vendored tree in ../rawaccel.
#
#   scripts/make-patch.sh            fetch upstream at the pinned commit and rewrite the patch
#   scripts/make-patch.sh --check    fail if the committed patch is out of date
#   UPSTREAM_DIR=/path/to/rawaccel scripts/make-patch.sh   use an existing checkout (must be at the pinned commit)
#
# Needs git and diff. Network access to github.com unless UPSTREAM_DIR is given.
set -euo pipefail

UPSTREAM_URL="https://github.com/RawAccelOfficial/rawaccel.git"
UPSTREAM_COMMIT="53a721345617a1e29f3a16750cbdf807040cf44e"   # v1.7.1
# vendored subset (everything else upstream, e.g. grapher/, doc/, signed/ binaries, is not vendored)
PATHS=(LICENSE ReadMe.md rawaccel.sln .gitattributes common driver installer uninstaller wrapper wrapper-deps writer)

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
patch_file="$here/patches/0001-openvector-angular-gain.patch"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

if [[ -n "${UPSTREAM_DIR:-}" ]]; then
  up="$UPSTREAM_DIR"
  actual="$(git -C "$up" rev-parse HEAD)"
  [[ "$actual" == "$UPSTREAM_COMMIT" ]] || { echo "UPSTREAM_DIR is at $actual, expected $UPSTREAM_COMMIT" >&2; exit 2; }
else
  up="$tmp/upstream"
  git init -q "$up"
  git -C "$up" fetch -q --depth 1 "$UPSTREAM_URL" "$UPSTREAM_COMMIT"
  git -C "$up" checkout -q FETCH_HEAD
fi

mkdir -p "$tmp/a" "$tmp/b"
for p in "${PATHS[@]}"; do cp -r "$up/$p" "$tmp/a/"; done
# the vendored tree, minus build outputs
(cd "$here/rawaccel" && tar --exclude=./packages --exclude=./x64 --exclude=./ARM64 --exclude='./*/obj' --exclude='./*/bin' -cf - .) | tar -xf - -C "$tmp/b"

out="$tmp/new.patch"
{
  echo "OpenVector RESEARCH build: angle x speed gain table for Raw Accel v1.7.1 ($UPSTREAM_COMMIT)."
  echo "RESEARCH ONLY - test-signed, never shipped, never for anticheat-protected games. MIT (see LICENSE)."
  echo "Apply from an upstream checkout at that commit with: patch -p1 < this.patch"
  echo
  (cd "$tmp" && diff -ruN a b) || true
} > "$out"

if [[ "${1:-}" == "--check" ]]; then
  if diff -q "$out" "$patch_file" >/dev/null; then echo "patch is up to date"; else echo "patch is OUT OF DATE, run scripts/make-patch.sh" >&2; exit 1; fi
else
  mkdir -p "$(dirname "$patch_file")"
  cp "$out" "$patch_file"
  echo "wrote $patch_file ($(grep -c '^diff ' "$patch_file") files changed)"
fi
