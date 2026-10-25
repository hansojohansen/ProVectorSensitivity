#!/usr/bin/env bash
# OpenVector RESEARCH build - host-side tests of the angle x speed gain table math.
# Compiles the real rawaccel/common headers with g++ and/or clang++ on Linux (or WSL).
#
#   research/rawaccel-angular/test/run.sh          # every compiler found
#   CXX=g++ research/rawaccel-angular/test/run.sh  # one compiler
#
# Per compiler:
#   1. unit tests, -O1 with AddressSanitizer + UBSan (if the toolchain has them)
#   2. the same tests with -O2 -ffast-math (the driver is built with /fp:fast)
#   3. equivalence: with the table off, research headers == pristine upstream 1.7.1 headers,
#      bit for bit, on random settings and packet streams. Upstream is rebuilt by
#      reverse-applying patches/0001-openvector-angular-gain.patch (no network needed).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
common="$root/rawaccel/common"
patch_file="$root/patches/0001-openvector-angular-gain.patch"
build="$(mktemp -d)"
trap 'rm -rf "$build"' EXIT

compilers=()
if [[ -n "${CXX:-}" ]]; then compilers=("$CXX"); else
  for c in g++ clang++; do command -v "$c" >/dev/null && compilers+=("$c"); done
fi
[[ ${#compilers[@]} -gt 0 ]] || { echo "need g++ or clang++" >&2; exit 2; }

# pristine upstream common/ for the equivalence check
have_up=0
if [[ -f "$patch_file" ]]; then
  mkdir -p "$build/up"
  cp -r "$root/rawaccel/." "$build/up/"
  (cd "$build/up" && patch -s -R -p1 < "$patch_file")
  if grep -q angular "$build/up/common/rawaccel.hpp"; then echo "reverse patch did not restore upstream" >&2; exit 1; fi
  have_up=1
fi

for CXX in "${compilers[@]}"; do
  echo "################ $("$CXX" --version | head -1)"
  flags=(-std=c++17 -Wall -Wextra -Wno-unused-parameter -Wno-missing-field-initializers -Wno-sign-compare
         -ffp-contract=off -include "$here/host_shim.h")
  if ! "$CXX" --version | grep -qi clang; then
    flags+=(-Wno-changes-meaning)   # upstream "cap_mode cap_mode" member; MSVC and clang accept it
  fi
  tag="$(basename "$CXX")"

  san=(-fsanitize=address,undefined -fno-sanitize-recover=undefined -fno-omit-frame-pointer)
  if ! echo 'int main(){}' | "$CXX" -x c++ "${san[@]}" - -o "$build/probe_$tag" 2>/dev/null; then
    echo "note: sanitizers unavailable for $CXX, building without them"; san=()
  fi

  echo "== 1. unit tests (-O1 ${san[*]:+with sanitizers}) =="
  "$CXX" "${flags[@]}" -O1 -g "${san[@]}" -I"$common" "$here/test_angular_gain.cpp" -o "$build/t_san_$tag"
  "$build/t_san_$tag"

  echo "== 2. unit tests (-O2 -ffast-math) =="
  fast=(-O2 -ffast-math)
  "$CXX" --version | grep -qi clang && fast+=(-Wno-nan-infinity-disabled -Wno-unknown-warning-option)
  "$CXX" "${flags[@]}" "${fast[@]}" -I"$common" "$here/test_angular_gain.cpp" -o "$build/t_fast_$tag"
  "$build/t_fast_$tag" > "$build/fast_$tag.log" || { cat "$build/fast_$tag.log"; echo "FAST-MATH BUILD FAILED"; exit 1; }
  tail -n 1 "$build/fast_$tag.log"

  echo "== 3. table off == upstream 1.7.1, bit for bit =="
  if [[ $have_up == 1 ]]; then
    for side in up research; do
      inc="$common"; [[ "$side" == up ]] && inc="$build/up/common"
      "$CXX" "${flags[@]}" -O2 -I"$inc" "$here/equiv_main.cpp" -o "$build/eq_${side}_$tag"
      "$build/eq_${side}_$tag" > "$build/eq_${side}_$tag.txt"
    done
    if cmp -s "$build/eq_up_$tag.txt" "$build/eq_research_$tag.txt"; then
      echo "identical: $(wc -l < "$build/eq_up_$tag.txt") outputs"
    else
      echo "DIFFERENT outputs between upstream and research with the table off:" >&2
      diff "$build/eq_up_$tag.txt" "$build/eq_research_$tag.txt" | head -20 >&2
      exit 1
    fi
  else
    echo "SKIPPED: $patch_file not found (run scripts/make-patch.sh)"
  fi
  echo
done

echo "== tools/make_angular_table.py (Python mirror of the lookup) =="
if command -v python3 >/dev/null; then python3 -I "$root/tools/make_angular_table.py" --selftest; else echo "SKIPPED: no python3"; fi
echo

echo "ALL HOST TESTS PASSED (${compilers[*]})"
