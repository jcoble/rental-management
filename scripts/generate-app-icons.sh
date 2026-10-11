#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dark="$repo_root/assets/brand/rental-command-icon-dark.png"
source_light="$repo_root/assets/brand/rental-command-icon-light.png"

if ! command -v sips >/dev/null 2>&1; then
  echo "sips is required to generate app icons." >&2
  exit 1
fi

render() {
  local source="$1"
  local size="$2"
  local out="$3"
  mkdir -p "$(dirname "$out")"
  sips -s format png -z "$size" "$size" "$source" --out "$out" >/dev/null
}

render_dark() {
  render "$source_dark" "$1" "$2"
}

render "$source_dark" 512 "$repo_root/web/static/brand/rental-command-icon-dark.png"
render "$source_light" 512 "$repo_root/web/static/brand/rental-command-icon-light.png"

render_dark 32 "$repo_root/web/static/favicon-32.png"
render_dark 180 "$repo_root/web/static/apple-touch-icon.png"
render_dark 192 "$repo_root/web/static/icon-192.png"
render_dark 512 "$repo_root/web/static/icon-512.png"
render_dark 192 "$repo_root/web/static/icon-maskable-192.png"
render_dark 512 "$repo_root/web/static/icon-maskable-512.png"

render_dark 32 "$repo_root/mobile/web/favicon.png"
render_dark 192 "$repo_root/mobile/web/icons/Icon-192.png"
render_dark 512 "$repo_root/mobile/web/icons/Icon-512.png"
render_dark 192 "$repo_root/mobile/web/icons/Icon-maskable-192.png"
render_dark 512 "$repo_root/mobile/web/icons/Icon-maskable-512.png"

render_dark 48 "$repo_root/mobile/android/app/src/main/res/mipmap-mdpi/ic_launcher.png"
render_dark 72 "$repo_root/mobile/android/app/src/main/res/mipmap-hdpi/ic_launcher.png"
render_dark 96 "$repo_root/mobile/android/app/src/main/res/mipmap-xhdpi/ic_launcher.png"
render_dark 144 "$repo_root/mobile/android/app/src/main/res/mipmap-xxhdpi/ic_launcher.png"
render_dark 192 "$repo_root/mobile/android/app/src/main/res/mipmap-xxxhdpi/ic_launcher.png"

ios_icons="$repo_root/mobile/ios/Runner/Assets.xcassets/AppIcon.appiconset"
render_dark 20 "$ios_icons/Icon-App-20x20@1x.png"
render_dark 40 "$ios_icons/Icon-App-20x20@2x.png"
render_dark 60 "$ios_icons/Icon-App-20x20@3x.png"
render_dark 29 "$ios_icons/Icon-App-29x29@1x.png"
render_dark 58 "$ios_icons/Icon-App-29x29@2x.png"
render_dark 87 "$ios_icons/Icon-App-29x29@3x.png"
render_dark 40 "$ios_icons/Icon-App-40x40@1x.png"
render_dark 80 "$ios_icons/Icon-App-40x40@2x.png"
render_dark 120 "$ios_icons/Icon-App-40x40@3x.png"
render_dark 120 "$ios_icons/Icon-App-60x60@2x.png"
render_dark 180 "$ios_icons/Icon-App-60x60@3x.png"
render_dark 76 "$ios_icons/Icon-App-76x76@1x.png"
render_dark 152 "$ios_icons/Icon-App-76x76@2x.png"
render_dark 167 "$ios_icons/Icon-App-83.5x83.5@2x.png"
render_dark 1024 "$ios_icons/Icon-App-1024x1024@1x.png"

echo "Generated Rental Command app icons from $source_dark"
