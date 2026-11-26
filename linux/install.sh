#!/bin/sh
# Installs Beam for the current user: ~/.local/bin/beam, an app-menu entry and the icon.
set -e
here=$(cd "$(dirname "$0")" && pwd)
bin="$HOME/.local/bin"
apps="$HOME/.local/share/applications"
icons="$HOME/.local/share/icons/hicolor/128x128/apps"
mkdir -p "$bin" "$apps" "$icons"
install -m 755 "$here/beam" "$bin/beam"
install -m 644 "$here/beam.png" "$icons/beam.png"
cat > "$apps/beam.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=Beam
Comment=Cast from your phone to this computer (DLNA)
Exec=$bin/beam
Icon=beam
Terminal=false
Categories=AudioVideo;Video;Network;
DESK
echo "Installed. Start it from your apps menu or run: beam"
if ! command -v mpv >/dev/null 2>&1; then
  echo "Beam plays through mpv, which is not installed yet. Install it, for example:  sudo apt install mpv"
fi
case ":$PATH:" in *":$bin:"*) ;; *) echo "Note: $bin is not in your PATH; use the apps-menu entry or run $bin/beam" ;; esac
