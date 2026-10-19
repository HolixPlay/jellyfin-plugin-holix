# Holix Play for Jellyfin

[![Build](https://github.com/HolixPlay/jellyfin-plugin-holix/actions/workflows/build.yml/badge.svg)](https://github.com/HolixPlay/jellyfin-plugin-holix/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/HolixPlay/jellyfin-plugin-holix)](https://github.com/HolixPlay/jellyfin-plugin-holix/releases)
[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-blue.svg)](LICENSE)

Jellyfin server plugin (`Jellyfin.Plugin.Holix`) for the [Holix Play](https://holixplay.com) client. It maps
Holix Play's Netflix-style household profiles onto Jellyfin's user model: **every Holix profile is a hidden
Jellyfin user**. Watch state, Next Up / Continue Watching and parental controls therefore work natively on the
server, in every Jellyfin client.

Without the plugin, Holix Play still works in single-profile mode (the signed-in Jellyfin user is the only
profile).

## Features

- **Household profiles.** A household *owner* (a Jellyfin user) creates, edits and deletes profiles; each
  profile is a separate hidden Jellyfin user. Library, channel, device, remote-access and playback permissions
  are copied from the owner; administrative rights (admin, deletion, collection/subtitle management, remote
  control of other users) are always off. When the owner's policy changes in the dashboard it is propagated to
  the profiles automatically.
- **Age rating → parental limit.** Birth dates never reach the server (they stay in holix-cloud). The client
  sends the profile's age band (`all`, `7+`, `13+`, `16+`, `18+`); the plugin writes the matching Jellyfin
  `MaxParentalRating` / `MaxParentalSubRating`, blocked tags and (for children) hides unrated content.
  Rating systems: Turkey (RTÜK), US (MPA/TV), Germany (FSK). Turkish labels that Jellyfin does not know
  (e.g. "Genel İzleyici", "7 yaş ve üzeri") are scored by the plugin instead of being treated as unrated.
- **Profile sessions with server-side PIN.** `POST /Holix/Profiles/{id}/Session` issues a Jellyfin access
  token for a profile. Profiles have no usable password. In a household linked to holix-cloud the request must
  carry a short-lived (≤ 60 s), single-use proof that holix-cloud signs (Ed25519) only after the profile PIN was
  verified — so a PIN cannot be bypassed by calling the server directly.
- **Guest profiles keep no history.** Playback position, played state, play count and last-played date are
  discarded for guest profiles (optionally favourites/likes/ratings too).
- **Profile pictures** are stored as the Jellyfin user image, so other Jellyfin clients show them as well.
- **Stream authorization for reverse proxies.** `GET /Holix/StreamAuth` is a Traefik `forwardAuth` / nginx
  `auth_request` endpoint that closes Jellyfin's unauthenticated stream URLs at the proxy: a stream is only
  served to a valid token whose user may see that item (see
  [Closing the stream gap at the reverse proxy](#closing-the-stream-gap-at-the-reverse-proxy)).
- **Settings page** in the dashboard: holix-cloud URL, proof enforcement, guest behaviour, default rating
  system, who may own a household, profile limit, child defaults, and a list of households with cloud
  link/unlink.

## Requirements

| | |
|---|---|
| Jellyfin server | **12.2.0 or newer** (`targetAbi` `12.2.0.0`; older servers will not load the plugin) |
| Runtime | .NET 10 (bundled with Jellyfin 12.x) |
| Client | Holix Play (Apple TV, iPhone/iPad, Mac, Android / Android TV) |
| Cloud (optional, recommended) | holix-cloud for PIN-protected profiles — default `https://api.holixplay.com` |

## Installation

### From the Holix Play repository (recommended)

1. Jellyfin **Dashboard → Plugins → Repositories → `+`**.
2. Name: `Holix Play`, URL:

   ```text
   https://holixplay.com/jellyfin/manifest.json
   ```

3. **Catalog → Holix → Install**, then restart the Jellyfin server.

Updates appear in the catalog automatically. (The URL redirects to
[`manifest.json`](https://raw.githubusercontent.com/HolixPlay/jellyfin-plugin-holix/main/manifest.json) in
this repository; the zips are attached to the [GitHub releases](https://github.com/HolixPlay/jellyfin-plugin-holix/releases).)

### Manual

Download `holix_<version>.zip` from the [releases](https://github.com/HolixPlay/jellyfin-plugin-holix/releases)
page and unpack it into Jellyfin's plugin directory, then restart Jellyfin:

```sh
sudo mkdir -p /var/lib/jellyfin/plugins/Holix_0.4.0.0
sudo unzip -o holix_0.4.0.0.zip -d /var/lib/jellyfin/plugins/Holix_0.4.0.0
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Holix_0.4.0.0
sudo systemctl restart jellyfin
```

With Docker the directory is `<config volume>/plugins/Holix_<version>`; give ownership to the user the
container runs as and restart the container. Remove older `Holix_*` directories.

### Verify

```sh
curl -s https://jellyfin.example.com/Holix/Info
```

The response must contain `"ApiVersion": 1` and `cloud-proof` in `Features`.

## Configuration

**Dashboard → Plugins → Holix.** Settings are stored in `plugins/configurations/Jellyfin.Plugin.Holix.xml`;
the household mapping lives in a separate file,
`plugins/configurations/Jellyfin.Plugin.Holix/households.json`, so saving the settings page never overwrites
it and plugin updates (which replace the versioned plugin directory) keep it. **Back this file up** — if it is
lost, the profile users stay in Jellyfin but the mapping is gone.

| Setting | Default | Description |
|---|---|---|
| `CloudUrl` | `https://api.holixplay.com` | holix-cloud base URL (https; http only for localhost). Announced to clients in `/Holix/Info`; the plugin only ever contacts this URL (link code + public keys) |
| `RequireCloudProof` | **on** | In a cloud-linked household, profile sessions require a holix-cloud proof; cloud profile mappings are locked |
| `RequireCloudLink` | off | When on, households that are not linked to holix-cloud cannot open profile sessions at all |
| `GuestBehavior` | `DiscardPlaystate` | `DiscardAllUserData` (also favourites/ratings) or `KeepPlaystate` (protection off) |
| `DefaultRatingSystem` | `TR` | Rating system for new households (`TR`, `US`, `DE`) |
| `MaxProfilesPerHousehold` | 10 | Including the owner |
| `BlockUnratedForChildren` | on | Hide unrated movies/series/trailers/Live TV/channel content for child profiles (music and books excluded) |
| `StreamAuthAllowedProxies` | empty | Addresses (CIDR or single IPs) allowed to call `/Holix/StreamAuth`. Empty: loopback and private networks (10/8, 172.16/12, 192.168/16, fc00::/7). Other callers get 404 |
| `DefaultChildBlockedTags` | empty | Tags blocked for every child profile |
| `AllowNonAdminOwners` / `AllowedOwnerUserIds` | off / empty | Who may become a household owner (see below) |

### First-time setup

1. Create a **regular (non-administrator) Jellyfin user** for the household and give it the libraries the
   family should see. Add its user ID to *Users allowed to create a household* (or enable *Non-admin users can
   create households*).
2. Sign in with that user in Holix Play; the app creates the household (`POST /Holix/Household`) and links it
   to holix-cloud. Alternatively an administrator can enter a link code for a household on the settings page.
3. Create profiles in the app. Child profiles get their limits from the age band.

> **Important — the household owner should not be a Jellyfin administrator.** An administrator token can use
> Jellyfin's own admin APIs (for example change a profile user's password and sign in as that profile), which
> no plugin can prevent. PINs are only truly enforced on the server when the account used in the Holix Play
> client is a regular user. Profile users are never administrators.

## Security model

- **Root of trust: owner token + holix-cloud proof.** Profile sessions can only be opened with the household
  owner's Jellyfin token; the plugin checks (in `households.json`) that the caller owns the household and the
  target profile belongs to it. Profile tokens get 403 on all `/Holix/Profiles*` endpoints — a child profile
  cannot change its own rating or switch to another profile.
- **Profile users have no usable password.** A random 48-byte password is set at creation and never stored
  (otherwise a passwordless user could sign in via `AuthenticateByName` with an empty password). Profiles are
  hidden from the login screen. Tokens are issued with `ISessionManager.AuthenticateDirect` (the same mechanism
  Quick Connect uses), so Jellyfin's normal device/session records, device permissions and session limits
  apply.
- **Per-profile device IDs** (`holix-` + SHA-256(device | profile)[0..16]) so profiles on the same device do
  not revoke each other's sessions.
- **PIN enforced on the server.** In a linked household `…/Session` requires holix-cloud's proof, a compact
  JWS (`alg: EdDSA`, `typ: holix-jellyfin-proof+jwt`). The plugin only stores holix-cloud's **public** keys
  (JWKS), so neither the Jellyfin server, its backups nor its administrator can mint a proof. Checks:

  | Check | Error (all 403) |
  |---|---|
  | Format, `alg = EdDSA`, `typ`, no `crit`, Ed25519 signature (BouncyCastle) | `invalid_proof` |
  | `kid` in the cached JWKS; on a miss the JWKS is re-fetched at most every 5 min (key rotation) | `proof_unknown_key` |
  | `aud = holix-jellyfin-plugin` | `proof_wrong_audience` |
  | `iss` equals the value learnt when linking | `proof_wrong_issuer` |
  | Lifetime `exp − iat` 1–60 s; `iat`/`nbf` ≤ now + 10 s; now < `exp` + 10 s | `invalid_proof`, `proof_not_yet_valid`, `proof_expired` |
  | `hid` = linked household, `jf_sid` = this server's `SystemId` | `proof_wrong_household`, `proof_wrong_server` |
  | `jf_uid` = requested profile user **and** `pid` = the profile's `HolixProfileId` | `proof_wrong_profile` |
  | `kind` = profile kind (not checked for the owner) | `proof_kind_mismatch` |
  | Child: `max_age_rating` = rating stored by the plugin | `age_rating_mismatch` |
  | `jti` not used before (consumed only after every other check passed) | `proof_replayed` |
  | Profile has no `HolixProfileId` | `profile_not_mapped` |

- **Mapping lock.** Proofs rely on the `pid` ↔ `HolixProfileId` mapping. In a linked household (with proofs
  required) a non-empty mapping cannot be changed or cleared (409 `mapping_locked`), and a cloud profile can be
  mapped to only one profile (409 `mapping_in_use`); otherwise someone holding the owner token could map a
  PIN-protected profile to a PIN-less cloud profile.
- **Who controls the link.** The owner performs the first link, but the cloud URL comes only from the
  administrator's `CloudUrl` setting (never from the request). Re-linking or unlinking a linked household is
  administrator-only; otherwise an owner-token holder could link the household to their own cloud account and
  mint proofs.
- **Transition mode.** Unlinked households keep the old behaviour (owner token is enough) with a server log
  warning and `Warning: "cloud_not_linked"` in the response. Enable `RequireCloudLink` once all households are
  linked.
- **Limits.**
  1. If the household owner is a Jellyfin **administrator**, Jellyfin's admin APIs are outside the plugin's
     control (see the note above).
  2. The **owner token is the household's master key.** Whoever holds it can use Jellyfin as the owner and can
     change profile settings (for example raise a child profile's age band; the change applies to that
     profile's existing sessions immediately, and only new sessions are re-checked against holix-cloud). The
     client must store it securely (keychain); treat it like a password.
  3. Used `jti` values are kept in memory; after a server restart a proof captured in the preceding ≤ 70 s
     (60 s lifetime + 10 s clock-skew allowance) could be replayed once (and still only together with the
     owner token).
  4. Proofs are checked when a session is opened; issued Jellyfin tokens live on until revoked
     (`DELETE /Holix/Profiles/{id}/Session`, e.g. after a PIN change).
  5. Parental limits hide items from every list, search and item/playback-info endpoint (404 for a child
     profile), but Jellyfin's **static stream URL (`/Videos/{id}/stream`) and item images need no
     authentication at all**, and its HLS endpoints check the token but not the parental limit. Anyone who
     already knows an item ID can therefore stream it regardless of profile; IDs are not discoverable with a
     child profile's token. **Close this at the reverse proxy with `/Holix/StreamAuth`** (see
     [below](#closing-the-stream-gap-at-the-reverse-proxy)); its decisions are cached for 60 s, so a revoked
     token or a stricter limit applies to streams within 60 s. Item images stay public. Clients that do not
     send a token with stream requests (some DLNA renderers) stop working behind it.
- **Household ownership is a privilege**: an owner can create (hidden, non-admin) users on the server.
  That is why only administrators may own households by default. Profiles never get more library access than
  the owner, and a stricter parental limit on the owner also caps every profile.
- **Birth dates never reach the server**; only the derived threshold and the age band label are stored.

Please report security issues privately to **security@holixplay.com** (or via GitHub's private vulnerability
reporting) rather than opening a public issue.

## Closing the stream gap at the reverse proxy

`GET /Holix/StreamAuth` lets the reverse proxy ask the plugin, for every media request, whether the caller may
stream that item. Route only stream paths through it (`/Videos/{id}/…`, `/Audio/{id}/…`); everything else goes
to Jellyfin directly.

| Input | |
|---|---|
| Path | `X-Forwarded-Uri` (+ `X-Forwarded-Method`); without it the request's own path |
| Token | `Authorization: MediaBrowser … Token="…"` (also `Emby`, `X-Emby-Authorization`), `X-Emby-Token`, `X-MediaBrowser-Token`, then `api_key` / `ApiKey` in the forwarded URI's query (or the request's own query) |

Recognised paths (case-insensitive, GUID with or without dashes, an optional base-URL prefix):
`/Videos/{id}/stream[.ext]`, `/Videos/{id}/master.m3u8`, `/Videos/{id}/main.m3u8`, `/Videos/{id}/live.m3u8`,
`/Videos/{id}/hls1/…`, `/Videos/{id}/{mediaSourceId}/…` (any deeper path), `/Audio/{id}/stream[.ext]`,
`/Audio/{id}/universal`, `/Audio/{id}/master.m3u8`, `/Audio/{id}/main.m3u8`, `/Audio/{id}/hls1/…`.

| Result | When |
|---|---|
| `204` | Token valid and the user may see the item (or a server API key); also for paths that are not stream paths and for `OPTIONS` |
| `401` | No token or invalid token |
| `403` | User disabled, or the item is not visible to the user — the same check as Jellyfin's `GET /Users/{userId}/Items/{itemId}` (`BaseItem.IsVisibleStandalone`: library access, parental rating, unrated blocking, blocked/allowed tags, parent folders) |
| `404` | No such item; or the caller is not in `StreamAuthAllowedProxies` |

Decisions are cached per (token, item) for 60 s (at most 10 000 entries), so HLS segment requests do not hit
the database. The allow list is checked against the directly connected address, before Jellyfin applies
`X-Forwarded-For` from its known proxies. The proxy itself must still refuse `/Holix/StreamAuth` from the
internet (the examples below do), because a request relayed by the proxy comes from an allowed address.

### Traefik (file provider)

```yaml
http:
  middlewares:
    holix-stream-auth:
      forwardAuth:
        address: "http://jellyfin:8096/Holix/StreamAuth"
        trustForwardHeader: false
    holix-deny:
      ipAllowList:
        sourceRange: ["127.0.0.1/32"]

  routers:
    jellyfin:
      rule: "Host(`jellyfin.example.com`)"
      entryPoints: [websecure]
      service: jellyfin
      tls: {}
    jellyfin-streams:
      rule: "Host(`jellyfin.example.com`) && PathRegexp(`(?i)^(/[^/]+)?/(videos|audio)/[0-9a-f-]{32,36}/`)"
      priority: 1000
      entryPoints: [websecure]
      service: jellyfin
      middlewares: [holix-stream-auth]
      tls: {}
    jellyfin-streamauth-external:
      rule: "Host(`jellyfin.example.com`) && PathRegexp(`(?i)/holix/streamauth`)"
      priority: 1001
      entryPoints: [websecure]
      service: jellyfin
      middlewares: [holix-deny]
      tls: {}

  services:
    jellyfin:
      loadBalancer:
        servers:
          - url: "http://jellyfin:8096"
```

Traefik forwards the client's headers and sets `X-Forwarded-Uri` / `X-Forwarded-Method` itself
(`trustForwardHeader: false` ignores client-supplied values).

### nginx (`auth_request`)

```nginx
# Stream paths: ask the plugin first.
location ~* ^/(videos|audio)/[0-9a-f-]{32,36}/ {
    auth_request /_holix_stream_auth;
    # auth_request turns any status other than 2xx/401/403 into 500; map it back to 404 (unknown item).
    error_page 500 =404 /_holix_not_found;
    proxy_pass http://jellyfin:8096;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_buffering off;
}

location = /_holix_stream_auth {
    internal;
    proxy_pass http://jellyfin:8096/Holix/StreamAuth;
    proxy_pass_request_body off;
    proxy_set_header Content-Length "";
    proxy_set_header X-Forwarded-Uri $request_uri;
    proxy_set_header X-Forwarded-Method $request_method;
    proxy_set_header Host $host;
}

location = /_holix_not_found { internal; return 404; }

# Never expose the endpoint itself.
location ~* ^/holix/streamauth { return 404; }
```

## API

All responses are PascalCase JSON (Jellyfin's default; send `Accept: application/json; profile="CamelCase"`
for camelCase). Authentication is Jellyfin's: `Authorization: MediaBrowser Client="Holix Play", Device="…",
DeviceId="…", Version="…", Token="<token>"`. API keys are not accepted (every operation is bound to a user).
Errors: `{"Error": "<code>", "Message": "…"}`.

| Method and path | Auth | Description |
|---|---|---|
| `GET /Holix/Info` | anonymous | `Version`, `ApiVersion` (1), `Features` (`cloud-proof`, `profile-image`, …), `CloudUrl`, `GuestBehavior`, `RatingSystems`, `DefaultRatingSystem`, `AgeRatings`, `MaxProfiles`, `RequireCloudProof`, `RequireCloudLink`. 404 → plugin not installed → single-profile mode |
| `GET /Holix/Household` | user | Owner: household, all profiles (owner first, `IsOwner=true`) and `Cloud` link info (or `null`). Profile user: `Role="profile"` and only its own profile. 404 if none |
| `POST /Holix/Household` | admin\* | Makes the caller a household owner (idempotent). `{RatingSystem?: "TR"\|"US"\|"DE", OwnerHolixProfileId?}` |
| `DELETE /Holix/Household?deleteProfileUsers=true` | owner | Deletes the household (by default with its profile users) |
| `POST /Holix/Household/Link` | owner | `{Code}` from holix-cloud (`POST /v1/jellyfin/link-code`). Returns the updated household (same body as `GET /Holix/Household`). 409 `already_linked`, 409 `cloud_not_configured`, 400 `invalid_link_code` |
| `POST /Holix/Household/Sync` | owner | Re-derives profile policies from the owner (normally automatic) |
| `GET /Holix/Profiles`, `GET /Holix/Profiles/{id}` | owner | Profiles (`id` = Jellyfin user ID) |
| `POST /Holix/Profiles` | owner | 201. `{Name, Kind: "adult"\|"child"\|"guest", AgeRating?, RatingSystem?, BlockedTags?, HolixProfileId?}`. `AgeRating` is required for children; default `18+` otherwise |
| `PATCH /Holix/Profiles/{id}` | owner | Only the given fields. For the owner's own profile only `HolixProfileId` |
| `DELETE /Holix/Profiles/{id}` | owner | Deletes the profile and its Jellyfin user (not the owner) |
| `POST /Holix/Profiles/{id}/Session` | owner + proof | `{JellyfinProof, DeviceId?, DeviceName?, App?, AppVersion?}`. Returns `AccessToken`, `DeviceId`, `ServerId`, `Profile`, `Authentication` (same shape as `/Users/AuthenticateByName`), `ProofVerified`, `Warning` |
| `DELETE /Holix/Profiles/{id}/Session` | owner | Revokes all Jellyfin tokens of the profile |
| `PUT /Holix/Profiles/{id}/Image` | owner | Raw JPEG or PNG body (≤ 2 MB). Stored as the Jellyfin user image. 413 `image_too_large`, 415 `unsupported_image` |
| `DELETE /Holix/Profiles/{id}/Image` | owner | Removes the profile's Jellyfin user image |
| `GET /Holix/Admin/Households` | admin | All households (settings page) |
| `GET /Holix/StreamAuth` | proxy only | forwardAuth / `auth_request` decision for stream paths: 204 / 401 / 403 / 404, empty body (see above) |
| `POST` / `DELETE /Holix/Admin/Households/{ownerId}/Link` | admin | Link / re-link with `{Code}`; unlink |

\* By default only Jellyfin administrators may create a household; see `AllowNonAdminOwners` /
`AllowedOwnerUserIds`.

### Client flow

```text
GET  /Holix/Info                       404 → single-profile mode
POST /Users/AuthenticateByName         owner sign-in (or Quick Connect) → owner token
POST /Holix/Household                  once (idempotent)
GET  /Holix/Household                  if Cloud == null, link once:
     holix-cloud POST /v1/jellyfin/link-code → {code}
     POST /Holix/Household/Link {Code}
GET  /Holix/Profiles                   profile picker (matched to holix-cloud profiles via HolixProfileId)
     profile chosen →
     holix-cloud POST /v1/profiles/{HolixProfileId}/session
          {pin?, jellyfin: {server_id: <SystemId>, user_id: <Profile.Id>}} → jellyfin_proof (≤ 60 s)
POST /Holix/Profiles/{id}/Session {JellyfinProof, …}
                                       → profile token; all further Jellyfin calls use it
```

The proof is single-use; clients use it immediately and never store or log it.

## Guest profiles: no history

Every write of Jellyfin watch state goes through `IUserDataManager.SaveUserData` (`/Sessions/Playing*`,
`/UserPlayedItems`, `/UserItems/{id}/UserData`, WebSocket, SyncPlay, imports) and raises `UserDataSaved`.
The plugin subscribes to that event: if the record belongs to a guest profile and contains watch state, it
resets position, played flag, play count and last-played date and writes the clean record back in the same
request. Endpoint filters, rollback at session end or relying on the client were rejected because they miss
code paths or break "Now Playing" in the dashboard.

Limit: guest playback still shows up in the dashboard activity log and in the Playback Reporting plugin's
statistics (those are separate records, not user data). Do not connect Trakt for guest profiles.

## Age band → parental limit

Scores are the `ratingScore` values from Jellyfin 12.2.0's `Localization/Ratings/{tr,us,de}.json`. Jellyfin
allows an item if its score is below the limit, or equal with a sub-score ≤ the limit's sub-score. The
threshold is the band's age and covers all sub-scores at that age — identical in all three systems; the
rating system only chooses the label.

| Holix | Threshold (score/sub) | TR label | US label | DE label |
|---|---|---|---|---|
| `all` | 0/0 | Genel İzleyici Kitlesi | G | FSK 0 |
| `7+` | 7/1 | 6+ | TV-Y7-FV | FSK 6 |
| `13+` | 13/1 | 13+ | PG-13 | FSK 12 |
| `16+` | 16/0 | 16+ | TV-14 | FSK 16 |
| `18+` | unlimited | 18+ | Unrestricted | FSK 18 |

| Rating (score/sub) | Lowest band |
|---|---|
| G, TV-Y, TV-G, Genel İzleyici Kitlesi, FSK 0 (0) | `all` |
| TR 6A/6+ (6/0, 6/1), FSK 6, TV-Y7 (7/0), TV-Y7-FV (7/1) | `7+` |
| PG (10/0), TV-PG-* (10/1), TR 10A/10+, FSK 12, PG-13 (13/0), TR 13A/13+ (13/0, 13/1) | `13+` |
| TV-14, TV-14-* (14/0, 14/1), TR 16+ (16/0), FSK 16 | `16+` |
| R (17/0), NC-17/TV-MA (17/1), TR 18+, FSK 18, TV-X (18) | `18+` |

Turkish labels missing from Jellyfin's `tr.json` are added to Jellyfin's rating lookup by the plugin at start-up
(an extra rating system consulted after all built-in ones, so built-in scores never change; item metadata is
not modified). Existing items carrying such a label get their stored score recomputed once, which is what a
metadata refresh would do. Sources: RTÜK [Akıllı İşaretler Uygulama Esas ve İlkeleri](https://www.rtuk.gov.tr/Media/FM/Birimler/izleme/ai_rehber/1-_bolum_-_uygulama_esas_ve_ilkeleri.pdf)
(age signs Genel İzleyici, 7+, 13+, 18+) and [content signs](https://www.rtuk.gov.tr/haber-bultenleri-ve-haber-programlarina-akilli-isaret-kullanma-zorunlulugu-getirildi/4836)
(Şiddet/Korku, Cinsellik, Olumsuz Örnek Oluşturabilecek Davranışlar).

| Label (any case, Turkish or ASCII letters) | Score/sub | Lowest band |
|---|---|---|
| Genel İzleyici, Genel İzleyici Kitlesi, Genel, Tüm Yaşlar, Her Yaş | 0/0 | `all` |
| 7+, 7 yaş ve üzeri / üstü | 7/0 | `7+` |
| 6 yaş ve üzeri (= 6+), 10 yaş ve üzeri (= 10+) | 6/1, 10/1 | `7+`, `13+` |
| 13+, 13 yaş ve üzeri / üstü | 13/1 | `13+` |
| 16 yaş ve üzeri (= 16+) | 16/0 | `16+` |
| 18+, 18 yaş ve üzeri / üstü | 18/0 | `18+` |
| Content sign without an age: Olumsuz Öğeler İçerir, Olumsuz Örnek Oluşturabilecek Davranışlar, Şiddet/Korku, Şiddet ve Korku, Cinsellik | 18/0 (fails closed) | `18+` |

If the owner's own `MaxParentalRating` is stricter, profiles cannot exceed it. Threshold changes are applied
to existing profiles at plugin start-up, or on demand with `POST /Holix/Household/Sync`.

## Building

Requires the .NET SDK pinned in [`global.json`](global.json) (10.0.4xx).

```sh
dotnet build                     # warnings are errors
dotnet test                      # xUnit v3 (Microsoft.Testing.Platform)
dotnet format --verify-no-changes
scripts/package.sh               # → artifacts/holix_<version>.zip (+ .md5)
```

The zip contains `Jellyfin.Plugin.Holix.dll`, `BouncyCastle.Cryptography.dll` (Ed25519; .NET has no built-in
Ed25519) and `meta.json`. Jellyfin assemblies are provided by the server.

### Releasing

Bump `<Version>` in `Directory.Build.props` (and `build.yaml`), add a section to [`CHANGELOG.md`](CHANGELOG.md),
then push a tag `v<major>.<minor>.<patch>`. The [release workflow](.github/workflows/release.yml) tests and
packages the plugin, publishes a GitHub release with the zip and its MD5, and commits the new version to
[`manifest.json`](manifest.json) on `main`.

## Uninstalling

Dashboard → Plugins → Holix → Uninstall. Profile users remain in Jellyfin (hidden); delete them first with
`DELETE /Holix/Household` if you want them gone.

## License

Copyright © 2026 IORA Teknoloji Geliştirme Ltd. Şti.

This program is free software: you can redistribute it and/or modify it under the terms of the **GNU General
Public License, version 3 only** (GPL-3.0-only), as published by the Free Software Foundation. It is distributed
in the hope that it will be useful, but WITHOUT ANY WARRANTY; see [`LICENSE`](LICENSE) for details.

Jellyfin plugins link against the GPL-licensed `Jellyfin.Controller`. Third-party components and their
licenses are listed in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). The "Holix Play" name and logo are not
licensed under the GPL.
