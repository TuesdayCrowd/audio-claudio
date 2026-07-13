#!/usr/bin/env bash
# Smoke-test a packaged (published + fixtures-staged) claudio build: --version, a
# fixture transcribe, and a render, run against the PACKAGED output -- never the dev
# tree (S5.9). Shared by scripts/package-macos.sh (osx-arm64, run on a Mac) and CI's
# linux-x64 "packaging mechanics" job.
#
# Usage: scripts/smoke-test-packaged.sh <staged-dir> <fixture.wav>

set -euo pipefail

STAGE_DIR="$1"
FIXTURE_WAV="$2"
EXE="$STAGE_DIR/claudio"

if [ ! -x "$EXE" ]; then
    echo "error: $EXE not found or not executable" >&2
    exit 1
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "==> $EXE --version"
VERSION_OUTPUT="$("$EXE" --version)"
echo "$VERSION_OUTPUT"
if ! echo "$VERSION_OUTPUT" | grep -qi "claudio"; then
    echo "error: --version output did not mention 'claudio': $VERSION_OUTPUT" >&2
    exit 1
fi

echo "==> $EXE transcribe (default/polyphonic engine -- exercises ModelLocator)"
"$EXE" transcribe "$FIXTURE_WAV" --tempo 120 --out-dir "$WORK_DIR"
for f in raw.mid score.mid score.musicxml; do
    if [ ! -s "$WORK_DIR/$f" ]; then
        echo "error: expected non-empty $WORK_DIR/$f after transcribe" >&2
        exit 1
    fi
done

echo "==> $EXE render (exercises SoundFontLocator)"
"$EXE" render "$WORK_DIR/score.mid" "$WORK_DIR/recreation.wav"
if [ ! -s "$WORK_DIR/recreation.wav" ]; then
    echo "error: expected non-empty $WORK_DIR/recreation.wav after render" >&2
    exit 1
fi

echo "==> Smoke test passed."
