#!/usr/bin/env bash
#
# Strip debug information from the Breez Spark native payload in a build output
# directory, in place. Run against a plugin TargetDir *before* PluginPacker zips
# it; package.yml runs it there, and the same command is what a local pre-release
# check uses.
#
# Why: Breez.Sdk.Spark ships its Rust libraries unstripped. Each linux .so carries
# ~28 MB of .debug_* DWARF (~48% of the file) plus the .symtab that goes with it;
# the osx dylibs carry ~9-11 MB of symbol/linkedit. None of it is loaded at run
# time — stripping removes only never-mapped sections, .text/.rodata/.data/
# .eh_frame/.dynsym are untouched — yet it is what makes the packaged runtimes
# ~200 MB instead of ~100 MB. Measured on Breez.Sdk.Spark 0.23.0:
#
#   linux-x64   60 MB -> 21 MB     linux-arm64  59 MB -> 18 MB
#   osx-x64     25 MB -> 16 MB     osx-arm64    24 MB -> 13 MB
#
# What stripping costs, stated plainly:
#   - Native crash triage: Rust panic *messages* (with file:line) survive, they
#     live in .rodata. Symbolised RUST_BACKTRACE/gdb/coredumps on linux do not:
#     frames show bare addresses instead of function names.
#   - Shipped hashes no longer match Breez's upstream artifacts. Provenance is
#     this plugin's build attestation (package.yml), and the sha256 before each
#     strip is printed so an upstream mapping always exists. MIT permits the
#     modification; NOTICE still travels (enforced in package.yml).
#
# What it deliberately does not touch:
#   - Windows DLLs. Checked on 0.23.0: the PE debug directory is a CodeView
#     pointer of tens of bytes on win-x64; the payload is all real code.
#   - Mach-O on a non-macOS host: GNU/LLVM strip cannot rewrite Mach-O. Warn and
#     skip rather than fail — CI packaging runs on linux and the osx payload is
#     dev-only there; a mac host strips it for free.
#   - A Mach-O carrying a real (non-ad-hoc, non-linker-signed) signature: strip
#     would invalidate the signing identity. Today the dylibs are ad-hoc and
#     macOS strip re-establishes an ad-hoc signature (verified with codesign -v
#     after each strip); if Breez ever ships Developer ID-signed binaries,
#     skipping them is the correct behaviour, not a size loss.
#
# Toolchain resolution per ELF file (first that can handle the file's arch):
# llvm-strip (any ELF arch) -> <arch>-linux-gnu-strip -> host strip (probed) ->
# docker (ubuntu:24.04 + cross binutils). Idempotent: an already-stripped file
# is a no-op, so re-running after an incremental build is always safe.
#
# Usage: scripts/strip-native-payloads.sh <target-dir>   (expects <target-dir>/runtimes/)

set -euo pipefail

TARGET_DIR="${1:?usage: $0 <plugin TargetDir containing runtimes/>}"
RUNTIMES="$TARGET_DIR/runtimes"
[ -d "$RUNTIMES" ] || { echo "error: $RUNTIMES does not exist; pass the plugin build output directory" >&2; exit 1; }

# Pinned by manifest digest, not the mutable tag: the binary this container
# produces rewrites libraries that ship inside the attested artifact, so a
# repointed ubuntu:24.04 would put unverified bytes on the release path while
# the Sigstore attestation still vouched for the result. The apt-installed
# binutils are themselves GPG-verified against the Ubuntu archive key by apt.
# This digest is what `ubuntu:24.04` resolved to when the first stripped,
# server-verified artifacts were produced; refresh deliberately (and re-verify
# the output) when the base image is next needed for anything new.
DOCKER_IMAGE="ubuntu:24.04@sha256:33ceb71981b602c1a7443a53469e4dba065f7503eab3078a2d7a57a2ab987517"

sha() { sha256sum "$1" 2>/dev/null | cut -d' ' -f1 || shasum -a 256 "$1" | cut -d' ' -f1; }
fsize() { stat -c%s "$1" 2>/dev/null || stat -f%z "$1"; }
mb() { awk -v n="$1" 'BEGIN{printf "%.0f", n/1048576}'; }

# ELF e_machine (bytes 18-19, little-endian) decides which cross-tool to reach for.
elf_arch() {
  case "$(od -An -j18 -N2 -t x1 "$1" | tr -d ' \n')" in
    3e00) echo x86_64 ;;
    b700) echo aarch64 ;;
    *) echo unknown ;;
  esac
}

docker_strip() { # $1 = directory, $2 = file name — strips in place via bind mount
  local dir; dir="$(cd "$1" && pwd)"   # docker -v needs an absolute host path
  docker run --rm -v "$dir:/work" "$DOCKER_IMAGE" bash -c '
    set -e
    apt-get update -qq >/dev/null && apt-get install -y -qq binutils-aarch64-linux-gnu binutils-x86-64-linux-gnu >/dev/null
    aarch64-linux-gnu-strip -s "/work/$1" || x86_64-linux-gnu-strip -s "/work/$1"
  ' strip "$2"
}

strip_elf() { # $1 = path to .so
  local f="$1" arch tool=""
  arch="$(elf_arch "$f")"
  [ "$arch" != unknown ] || { echo "error: $f has an unrecognised ELF machine id" >&2; return 1; }

  if command -v llvm-strip >/dev/null 2>&1; then
    tool="llvm-strip"
  elif command -v "${arch}-linux-gnu-strip" >/dev/null 2>&1; then
    tool="${arch}-linux-gnu-strip"
  elif command -v strip >/dev/null 2>&1; then
    # Host GNU strip is single-arch on Debian/Ubuntu images and macOS has no ELF
    # support at all; probe with a copy so a miss never touches the real file.
    if cp "$f" "$f.probe" && strip -s "$f.probe" 2>/dev/null; then
      tool="strip"
    fi
    rm -f "$f.probe"
  fi

  if [ -n "$tool" ]; then
    "$tool" -s "$f"
    return 0
  fi

  command -v docker >/dev/null 2>&1 || {
    echo "error: no ELF stripper for $f (install llvm or binutils-${arch}-linux-gnu, or run with docker)" >&2
    return 1
  }
  docker_strip "$(dirname "$f")" "$(basename "$f")"
}

strip_macho() { # $1 = path to .dylib
  local f="$1" sig
  if [ "$(uname -s)" != "Darwin" ]; then
    echo "warn: $f not stripped (Mach-O stripping needs a macOS host; linux CI ships the osx payload as-is)" >&2
    return 2
  fi
  # Safe to strip when unsigned (x86_64 payloads ship that way; x86_64 macOS
  # does not require signatures at load) or ad-hoc/linker-signed (strip
  # re-establishes an equivalent ad-hoc signature). A real signing identity
  # must survive untouched: invalidating it is worse than the bytes saved.
  sig="$(codesign -dv "$f" 2>&1 || true)"
  if ! printf '%s' "$sig" | grep -q 'adhoc\|linker-signed\|not signed at all'; then
    echo "warn: $f carries a real code signature; not stripping (identity would be invalidated)" >&2
    return 2
  fi
  # || rc=$? in the caller suspends errexit here, so a failed strip must be explicit
  strip -x "$f" || { echo "error: strip -x failed on $f" >&2; return 1; }
  # before: the pre-strip size, set by the caller
  # There is no cheap "already stripped" test for Mach-O (unlike `file` on
  # ELF), but strip -x is idempotent: a second pass on a stripped dylib frees
  # nothing. No shrink therefore means no-op, not failure.
  if [ "$(fsize "$f")" = "$before" ]; then
    printf 'no-op  %-12s %3s MB — already stripped\n' "$(basename "$(dirname "$(dirname "$f")")")" "$(mb "$before")"
    return 3
  fi
  if printf '%s' "$sig" | grep -q 'adhoc\|linker-signed'; then codesign -v "$f"; fi   # verify the re-sign
  return 0
}

total_before=0; total_after=0

while read -r f; do
  plat="$(basename "$(dirname "$(dirname "$f")")")"
  before="$(fsize "$f")"
  total_before=$((total_before + before))

  case "$f" in
    *.dll)
      printf 'skip   %-12s %3s MB — PE payload has no strippable debug data\n' "$plat" "$(mb "$before")"
      total_after=$((total_after + before))
      continue
      ;;
    *.so)
      if ! file "$f" | grep -q 'not stripped\|with debug_info'; then
        printf 'no-op  %-12s %3s MB — already stripped\n' "$plat" "$(mb "$before")"
        total_after=$((total_after + before))
        continue
      fi
      hash_before="$(sha "$f")"
      strip_elf "$f"
      ;;
    *.dylib)
      hash_before="$(sha "$f")"
      rc=0; strip_macho "$f" || rc=$?
      if [ "$rc" = 2 ] || [ "$rc" = 3 ]; then   # 2 = skipped (warn printed), 3 = no-op (printed)
        total_after=$((total_after + before))
        continue
      fi
      [ "$rc" = 0 ] || exit 1
      ;;
    *)
      echo "error: unexpected native file $f" >&2; exit 1
      ;;
  esac

  after="$(fsize "$f")"
  total_after=$((total_after + after))
  printf 'strip  %-12s %3s MB -> %2s MB   upstream sha256 %s\n' \
    "$plat" "$(mb "$before")" "$(mb "$after")" "$hash_before"
  [ "$after" -lt "$before" ] || { echo "error: $f did not shrink after strip" >&2; exit 1; }
done < <(find "$RUNTIMES" -path '*/native/*' \( -name '*.so' -o -name '*.dylib' -o -name '*.dll' \) | sort)

printf 'runtimes total: %s MB -> %s MB\n' "$(mb "$total_before")" "$(mb "$total_after")"
