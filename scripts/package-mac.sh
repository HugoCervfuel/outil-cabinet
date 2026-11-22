#!/bin/bash
# Construit « Outil de cabinet.app » pour macOS, signé localement (signature ad hoc), puis le compresse.
# Usage : scripts/package-mac.sh osx-arm64|osx-x64
set -euo pipefail

RID="$1"
VERSION="0.1.0"
OUT="publish/mac-$RID"
APP="$OUT/Outil de cabinet.app"

rm -rf "$OUT"
dotnet publish src/Cabinet.App -c Release -r "$RID" --self-contained true -p:UseAppHost=true -o "$OUT/bin"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/bin/." "$APP/Contents/MacOS/"
cp src/Cabinet.App/Assets/app.icns "$APP/Contents/Resources/OutilCabinet.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Outil de cabinet</string>
  <key>CFBundleDisplayName</key><string>Outil de cabinet</string>
  <key>CFBundleIdentifier</key><string>fr.outilcabinet.app</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>OutilCabinet</string>
  <key>CFBundleIconFile</key><string>OutilCabinet</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSUIElement</key><true/>
  <key>CFBundleDevelopmentRegion</key><string>fr</string>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"

# Image disque classique : l'application et un raccourci vers Applications, à glisser l'une sur l'autre.
STAGE="$OUT/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
DMG="$OUT/OutilCabinet-$RID.dmg"
hdiutil create -volname "Outil de cabinet" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
hdiutil verify "$DMG"

# Vérification : l'image s'ouvre et l'application qu'elle contient est intacte.
MOUNT="$(mktemp -d)"
hdiutil attach "$DMG" -mountpoint "$MOUNT" -nobrowse -readonly
codesign --verify --deep --strict "$MOUNT/Outil de cabinet.app"
test -x "$MOUNT/Outil de cabinet.app/Contents/MacOS/OutilCabinet"
hdiutil detach "$MOUNT"
echo "Paquet prêt : $DMG"
