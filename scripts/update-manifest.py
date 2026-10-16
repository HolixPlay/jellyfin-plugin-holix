#!/usr/bin/env python3
"""Adds (or replaces) a release in the Jellyfin plugin repository manifest (manifest.json)."""

import argparse
import json
import pathlib

GUID = "06f19487-387d-4e2f-be1d-71f44efff069"
PLUGIN = {
    "guid": GUID,
    "name": "Holix",
    "description": (
        "Server plugin for the Holix Play client. Every Holix profile is a hidden Jellyfin user, so watch "
        "state, Next Up and parental controls work natively on the server. Age ratings (all, 7+, 13+, 16+, "
        "18+) become Jellyfin parental limits (TR/US/DE rating systems), profile sessions can require a "
        "PIN verified by holix-cloud (Ed25519-signed, single-use proof), and guest profiles keep no watch "
        "history."
    ),
    "overview": "Holix Play profiles: household profiles, parental controls, guest mode",
    "owner": "Holix Play",
    "category": "General",
    "imageUrl": "https://raw.githubusercontent.com/HolixPlay/jellyfin-plugin-holix/main/assets/logo.png",
}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", default="manifest.json")
    ap.add_argument("--version", required=True)
    ap.add_argument("--target-abi", required=True)
    ap.add_argument("--source-url", required=True)
    ap.add_argument("--checksum", required=True)
    ap.add_argument("--timestamp", required=True)
    ap.add_argument("--changelog", default="")
    a = ap.parse_args()

    path = pathlib.Path(a.manifest)
    data = json.loads(path.read_text(encoding="utf-8")) if path.exists() else []
    entry = next((p for p in data if p.get("guid") == GUID), None)
    if entry is None:
        entry = {**PLUGIN, "versions": []}
        data.append(entry)
    versions = entry.get("versions", [])
    entry.update(PLUGIN)

    release = {
        "version": a.version,
        "changelog": a.changelog,
        "targetAbi": a.target_abi,
        "sourceUrl": a.source_url,
        "checksum": a.checksum,
        "timestamp": a.timestamp,
    }
    versions = [v for v in versions if v.get("version") != a.version]
    versions.append(release)
    versions.sort(key=lambda v: tuple(int(x) for x in v["version"].split(".")), reverse=True)
    entry["versions"] = versions

    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
