# Changelog

## 0.4.0

- New `GET /Holix/StreamAuth`: a Traefik `forwardAuth` / nginx `auth_request` endpoint that closes Jellyfin's
  unauthenticated stream URLs at the reverse proxy. Stream paths (`/Videos/{id}/…`, `/Audio/{id}/…`) are
  allowed only for a valid token whose user may see the item (library access, parental rating, blocked tags —
  Jellyfin's own item check): 204 / 401 / 403 / 404. Decisions are cached for 60 s (max. 10 000 entries).
  Callable only from `StreamAuthAllowedProxies` (new setting; default loopback and private networks).
  Traefik and nginx examples in the README.
- Turkish rating labels that Jellyfin does not know — "Genel İzleyici" (and "Genel", "GENEL", …), "7 yaş ve
  üzeri", "13 yaş ve üzeri", "18 yaş ve üzeri" and RTÜK content signs such as "Olumsuz Öğeler İçerir" — are now
  scored by the plugin instead of being treated as unrated, so child profiles no longer hide general-audience
  Turkish content. Item metadata is not changed; existing items are rescored once at start-up.
- holix-cloud proof clock-skew allowance lowered from 30 s to 10 s (`exp` and future `iat`/`nbf`); a proof
  captured before a server restart can now be replayed for at most 70 s instead of 90 s.

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
