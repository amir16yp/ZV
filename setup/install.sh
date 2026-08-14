#!/bin/sh
# ZV per-user installer for Linux/macOS
# Copies the compiler binary into ~/.local/bin and the lib/ folder into
# ~/.local/share/zv/lib, then ensures ~/.local/bin is on PATH.

set -e

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
SOURCE_DIR="${1:-$(dirname "$SCRIPT_DIR")}"
BIN_DIR="$HOME/.local/bin"
LIB_DIR="$HOME/.local/share/zv/lib"

if [ ! -f "$SOURCE_DIR/zv" ] && [ ! -f "$SOURCE_DIR/ZV" ]; then
    echo "Error: zv binary not found in '$SOURCE_DIR'." >&2
    echo "Run this script from the same folder as the published binary, or pass the source directory as the first argument." >&2
    exit 1
fi

mkdir -p "$BIN_DIR"
mkdir -p "$LIB_DIR"

echo "Installing ZV to $BIN_DIR ..."

# Prefer lowercase binary name on Unix.
if [ -f "$SOURCE_DIR/zv" ]; then
    cp "$SOURCE_DIR/zv" "$BIN_DIR/zv"
    chmod +x "$BIN_DIR/zv"
elif [ -f "$SOURCE_DIR/ZV" ]; then
    cp "$SOURCE_DIR/ZV" "$BIN_DIR/zv"
    chmod +x "$BIN_DIR/zv"
fi

if [ -d "$SOURCE_DIR/lib" ]; then
    echo "Copying lib/ to $LIB_DIR ..."
    cp -R "$SOURCE_DIR/lib/"* "$LIB_DIR/"
else
    echo "Warning: no lib/ folder found in '$SOURCE_DIR'."
fi

# Make sure ~/.local/bin is on PATH.
if ! echo "$PATH" | grep -q "$BIN_DIR"; then
    case "${SHELL##*/}" in
        zsh)
            PROFILE="$HOME/.zshrc"
            ;;
        bash)
            PROFILE="$HOME/.bashrc"
            ;;
        *)
            PROFILE="$HOME/.profile"
            ;;
    esac
    echo "export PATH=\"$BIN_DIR:\$PATH\"" >> "$PROFILE"
    echo "Added $BIN_DIR to PATH in $PROFILE. Restart your terminal or run:"
    echo "  source $PROFILE"
else
    echo "$BIN_DIR is already on PATH."
fi

echo "ZV installed successfully. You can now run 'zv' from a new terminal."
