# Changelog

## 0.5.3

- **Account-first sign-in.** New `POST /Holix/Household/CloudSession` (no Jellyfin token): a device signed in to
  the household's Holix account sends a session proof from holix-cloud (`POST /v1/servers/{id}/session-proof`)
  and gets the household owner's Jellyfin session — no Jellyfin password on the household's other devices. The
  proof is an Ed25519 JWS (`typ: holix-jellyfin-session+jwt`, same holix-cloud keys as the profile proof), lives
  at most 60 s, is single use and is bound to the household, this server and the device ID. Only households the
  owner linked to holix-cloud with their own token accept it. With `ProfileId` (+ the PIN proof `ProfileProof`)
  a profile session is opened instead, with exactly the rules of `POST /Holix/Profiles/{id}/Session`. The
  response also carries the household and its profiles. The household screen limit is checked before the proof
  is consumed.
- New setting **Allow sign-in with the Holix account** (`AllowCloudSessions`, default on); new feature flag
  `cloud-session` in `/Holix/Info`.

## 0.5.2

- **Household-wide session limit.** Profiles now copy the owner's *Maximum number of simultaneous user
  sessions* (`MaxActiveSessions`; before, profiles were unlimited). On top of that the owner and all profiles
  together may use at most that many screens: `POST /Holix/Profiles/{id}/Session` returns
  409 `household_session_limit` when a new screen would exceed it. A screen is a device: the owner's session
  on a TV and the profile sessions opened on that TV count once, so switching profiles on the same device is
  always allowed. Sessions from every client count (Jellyfin web too) when they are playing or were used in
  the last 15 minutes. 0 (Jellyfin's default) keeps everything unlimited. Changing the owner's limit in the
  dashboard is propagated to the profiles; existing profiles are updated at start-up. New feature flag
  `household-session-limit` in `/Holix/Info`.
- Re-opening a profile on the same device ends that profile's previous (and idle) sessions first, so
  Jellyfin's per-user limit does not refuse it.
- **"Who can create a household"** setting with three choices: *Everyone* (default for new installs),
  *Administrators only*, *Selected users* — chosen from a list of the server's user names (hidden profile
  users and administrators are not listed). User IDs are no longer typed in. The 0.5.1 settings are migrated
  at start-up: `AllowNonAdminOwners` on → Everyone; off with users listed → Selected users (same users); off
  with no users → Administrators only (unchanged behaviour). The mode only affects creating new households;
  existing households keep working. Refusals now use the error code `household_not_allowed` (403).
- README: Holix Pro is a client licence; the plugin does not check it.

## 0.5.1

- Removed the ClipsUrl setting; preview clips now use Jellyfin's standard theme videos (backdrops folder).

## 0.5.0

- New `ClipsUrl` setting (Dashboard → Plugins → Holix): the address of the holix-clips service that makes
  short background preview clips for this server. It is announced to clients as `ClipsUrl` in `/Holix/Info`,
  so it no longer has to be typed into every client. Left out of the response when empty or not an absolute
  http(s) URL; a trailing `/` is removed. The plugin never contacts this URL. New feature flag `clips-url`.

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
