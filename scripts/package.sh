#!/usr/bin/env bash
# Builds the plugin and packs a Jellyfin plugin zip (jprm layout: DLLs + meta.json at the zip root).
# Output: artifacts/holix_<version>.zip and artifacts/holix_<version>.zip.md5
# Usage:  scripts/package.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GUID="06f19487-387d-4e2f-be1d-71f44efff069"
TARGET_ABI="$(sed -n 's/^targetAbi: *"\(.*\)"/\1/p' "$ROOT/build.yaml")"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
OUT="$ROOT/artifacts"
PUB="$OUT/publish"
STAGE="$OUT/stage"
ZIP_NAME="holix_${VERSION}.zip"
TIMESTAMP="${SOURCE_DATE:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

rm -rf "$OUT"
mkdir -p "$PUB" "$STAGE"
dotnet publish "$ROOT/Jellyfin.Plugin.Holix/Jellyfin.Plugin.Holix.csproj" -c Release -o "$PUB" --nologo -v quiet

# The zip carries the plugin DLL and BouncyCastle (Ed25519 verification); Jellyfin's own assemblies
# are provided by the server (ExcludeAssets=runtime).
cp "$PUB/Jellyfin.Plugin.Holix.dll" "$PUB/BouncyCastle.Cryptography.dll" "$STAGE/"
cat > "$STAGE/meta.json" <<JSON
{
  "category": "General",
  "changelog": "See https://github.com/HolixPlay/jellyfin-plugin-holix/blob/main/CHANGELOG.md",
  "description": "Server plugin for the Holix Play client: every Holix profile is a hidden Jellyfin user, parental limits from age ratings (TR/US/DE), PIN-protected profile sessions verified with holix-cloud, and no watch history for guest profiles.",
  "guid": "$GUID",
  "name": "Holix",
  "overview": "Holix Play profiles: household profiles, parental controls, guest mode",
  "owner": "Holix Play",
  "targetAbi": "$TARGET_ABI",
  "timestamp": "$TIMESTAMP",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": true,
  "imagePath": "",
  "assemblies": ["BouncyCastle.Cryptography.dll", "Jellyfin.Plugin.Holix.dll"]
}
JSON

(cd "$STAGE" && TZ=UTC zip -X -q "$OUT/$ZIP_NAME" Jellyfin.Plugin.Holix.dll BouncyCastle.Cryptography.dll meta.json)

# Jellyfin verifies downloaded packages with MD5 (InstallationManager).
MD5="$(md5sum "$OUT/$ZIP_NAME" 2>/dev/null | cut -d' ' -f1 || md5 -q "$OUT/$ZIP_NAME")"
echo "$MD5  $ZIP_NAME" > "$OUT/$ZIP_NAME.md5"
rm -rf "$STAGE"

echo "version=$VERSION"
echo "target_abi=$TARGET_ABI"
echo "zip=$OUT/$ZIP_NAME"
echo "md5=$MD5"
