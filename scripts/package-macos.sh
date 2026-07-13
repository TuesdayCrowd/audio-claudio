#!/usr/bin/env bash
# Package the self-contained macOS (osx-arm64) claudio build into a distributable zip.
#
# S5.7/S5.8: a self-contained (NOT trimmed, NOT AOT -- ONNX Runtime and PortAudioSharp2
# ship native libraries and P/Invoke patterns AOT does not support) publish for
# osx-arm64, with fixtures/models + fixtures/soundfont shipped VERBATIM beside the
# `claudio` executable, zipped for distribution.
#
# Usage: scripts/package-macos.sh [output-dir]
#   output-dir defaults to artifacts/ (already gitignored).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-"$REPO_ROOT/artifacts"}"
RID="osx-arm64"
CONFIGURATION="Release"
STAGE_NAME="claudio-macos-arm64"
STAGE_DIR="$OUT_DIR/$STAGE_NAME"
ZIP_PATH="$OUT_DIR/$STAGE_NAME.zip"

echo "==> Cleaning $STAGE_DIR"
rm -rf "$STAGE_DIR" "$ZIP_PATH"
mkdir -p "$STAGE_DIR"

echo "==> Publishing self-contained $RID (no AOT: ONNX Runtime + PortAudioSharp2 ship native libs)"
dotnet publish "$REPO_ROOT/src/AudioClaudio.Cli/AudioClaudio.Cli.csproj" \
    -r osx-arm64 \
    -c "$CONFIGURATION" \
    --self-contained true \
    -o "$STAGE_DIR"

echo "==> Staging fixtures/models + fixtures/soundfont beside the binary (verbatim, S5.8)"
mkdir -p "$STAGE_DIR/fixtures"
cp -R "$REPO_ROOT/fixtures/models" "$STAGE_DIR/fixtures/models"
cp -R "$REPO_ROOT/fixtures/soundfont" "$STAGE_DIR/fixtures/soundfont"

if [ ! -x "$STAGE_DIR/claudio" ]; then
    echo "error: expected $STAGE_DIR/claudio to exist and be executable after publish" >&2
    exit 1
fi

echo "==> Smoke-testing the staged output before zipping (S5.9)"
"$REPO_ROOT/scripts/smoke-test-packaged.sh" "$STAGE_DIR" "$REPO_ROOT/fixtures/golden/two-bar.wav"

echo "==> Zipping to $ZIP_PATH"
( cd "$OUT_DIR" && zip -r -q "$(basename "$ZIP_PATH")" "$STAGE_NAME" )

echo "==> Done: $ZIP_PATH ($(du -sh "$ZIP_PATH" | cut -f1))"
