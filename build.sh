#!/usr/bin/env bash
# Build PortalCompass.dll and pack the Thunderstore zip.
#   ./build.sh dev     also copies the DLL to the profile's BepInEx/scripts: ScriptEngine reloads it in the running game (live iteration).
#   ./build.sh deploy  copies it into the profile's plugins folder (normal install, without Gale).
set -euo pipefail
cd "$(dirname "$0")"
v=$(grep -o 'const string Version = "[^"]*"' PortalCompass.cs | cut -d'"' -f2)
dotnet build -c Release -nologo -v q
cp bin/Release/netstandard2.1/PortalCompass.dll package/plugins/PortalCompass/
cp CHANGELOG.md README.md LICENSE package/
sed -i "s/\"version_number\":\"[^\"]*\"/\"version_number\":\"$v\"/" package/manifest.json
[ -f package/icon.png ] || echo "note: package/icon.png missing (Thunderstore needs a 256x256 icon)"
# forward slashes in the zip (PowerShell's Compress-Archive writes backslashes, which some mod managers can't unpack)
rm -f "Finn-PortalCompass-$v.zip"
python -I -c "import os,sys,zipfile
z=zipfile.ZipFile(sys.argv[2],'w',zipfile.ZIP_DEFLATED)
for d,_,fs in os.walk(sys.argv[1]):
    for f in sorted(fs):
        full=os.path.join(d,f); z.write(full, os.path.relpath(full, sys.argv[1]).replace(os.sep,'/'))
z.close()" package "Finn-PortalCompass-$v.zip"
profile="${PROFILE_DIR:-$APPDATA/com.kesomannen.gale/valheim/profiles/ValheimKK}/BepInEx"   # PROFILE_DIR: your mod manager profile folder
if [ "${1:-}" = dev ]; then
  # ScriptEngine reads the symbols too and refuses the DLL without its .pdb next to it; copy the pdb first, the dll last (that triggers the reload)
  mkdir -p "$profile/scripts" && cp bin/Release/netstandard2.1/PortalCompass.pdb "$profile/scripts/PortalCompass.pdb" && cp bin/Release/netstandard2.1/PortalCompass.dll "$profile/scripts/PortalCompass.dll" && echo "live: copied to $profile/scripts (ScriptEngine reloads in ~3 s)"
fi
if [ "${1:-}" = deploy ]; then
  dest="$profile/plugins/Finn-PortalCompass/PortalCompass"
  mkdir -p "$dest" && cp bin/Release/netstandard2.1/PortalCompass.dll "$dest/" && echo "deployed to $dest"
fi
echo "built $v"
