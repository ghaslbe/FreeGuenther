#!/bin/zsh
# Baut die eigenständige FreeGuenther.app (Apple Silicon) und packt sie als ZIP.
# Das .NET SDK von Homebrew kennt den macOS-Workload nicht, deshalb das offizielle SDK aus ~/.dotnet.
set -e
cd "$(dirname "$0")"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

dotnet build -c Release
app=bin/Release/net10.0-macos/osx-arm64/FreeGuenther.app
ditto -c -k --keepParent "$app" bin/Release/FreeGuenther-mac-arm64.zip
echo "Fertig: $PWD/$app"
echo "ZIP:    $PWD/bin/Release/FreeGuenther-mac-arm64.zip"
