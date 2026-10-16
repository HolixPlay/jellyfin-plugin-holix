# Changelog

## 0.3.0

- Age thresholds now use the band's age, identical across rating systems (TR/US/DE); the rating system only
  selects the label. A 7+ profile now sees US TV-Y7 in every system.
- `POST /Holix/Household/Link` returns the updated household (same body as `GET /Holix/Household`).
- New `PUT` / `DELETE /Holix/Profiles/{id}/Image`: profile pictures are stored as the Jellyfin user image.
- The profile image directory is validated to stay inside Jellyfin's user configuration directory.
- holix-cloud URL defaults to `https://api.holixplay.com`.

## 0.2.0

- Profile sessions in a cloud-linked household require a short-lived proof signed by holix-cloud
  (Ed25519) after PIN verification, so PINs are enforced server-side.
- Household ↔ holix-cloud linking (`POST /Holix/Household/Link`, admin link/unlink on the settings page).
- Cloud profile mapping lock.

## 0.1.0

- Initial release: `/Holix/Info`, `/Holix/Household`, `/Holix/Profiles` CRUD, profile sessions, guest
  playstate protection.
