#!/bin/zsh
# Baut die eigenständige FreeGuenther.app (Apple Silicon) und packt sie als ZIP.
# Das .NET SDK von Homebrew kennt den macOS-Workload nicht, deshalb das offizielle SDK aus ~/.dotnet.
set -e
cd "$(dirname "$0")"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

dotnet build -c Release
app=bin/Release/net10.0-macos/osx-arm64/FreeGuenther.app

# ZIP mit festem Ordner: App und Anleitung liegen nach dem Entpacken zusammen in „FreeGuenther“
stage=bin/Release/zip/FreeGuenther
rm -rf bin/Release/zip
mkdir -p "$stage"
ditto "$app" "$stage/FreeGuenther.app"
cp Installation.txt "$stage/"
ditto -c -k --keepParent "$stage" bin/Release/FreeGuenther-mac-arm64.zip
echo "Fertig: $PWD/$app"
echo "ZIP:    $PWD/bin/Release/FreeGuenther-mac-arm64.zip"
