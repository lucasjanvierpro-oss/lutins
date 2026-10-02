#!/bin/bash
# Compile Lutins.app. Avec --install : la copie dans ~/Applications et la (re)lance.
set -euo pipefail
cd "$(dirname "$0")"

APP=build/Lutins.app
rm -rf build
mkdir -p "$APP/Contents/MacOS"
swiftc -O -swift-version 5 -target "$(uname -m)-apple-macos13.0" \
  -framework Cocoa -framework Network -framework ServiceManagement \
  *.swift -o "$APP/Contents/MacOS/Lutins"
cp Info.plist "$APP/Contents/Info.plist"
codesign --force --sign - "$APP" >/dev/null
echo "Compilé : $APP"

if [[ "${1:-}" == "--install" ]]; then
  pkill -x Lutins 2>/dev/null || true
  mkdir -p "$HOME/Applications"
  rm -rf "$HOME/Applications/Lutins.app"
  cp -R "$APP" "$HOME/Applications/"
  open "$HOME/Applications/Lutins.app"
  echo "Installé et lancé : ~/Applications/Lutins.app"
fi
