# Jellyfin Ingest

A [Jellyfin](https://jellyfin.org) plugin that watches one or more **drop folders** and files new media into your
libraries automatically — identified, renamed to Jellyfin's naming standard, with its subtitles, and with the release
clutter set aside.

> **Status: early development.** The repository is being scaffolded; nothing here is ready to install yet.
> See the [roadmap](#roadmap).

## What it does

Drop a file (or a whole release folder) into a watched folder such as `incoming`:

```text
incoming/
└── n00b-Lantern1E4/
    ├── n00b-Lantern1E4.mp4
    ├── english.srt
    ├── README.txt
    └── sample.mkv
```

and Jellyfin Ingest will:

1. **Wait until the copy has finished**: nothing in the release (files, sizes, write times) has changed for the settle
   time (5 minutes by default). See [Download clients](#download-clients).
2. **Identify it** from the release name (title, year, season/episode, edition) and the metadata providers you
   already have configured in Jellyfin (TMDb, TheTVDB, …) — here: *Lantern*, season 1, episode 4.
3. **Rename and move it** into the watch folder's destination library for its kind (shows or films) using
   [Jellyfin's naming conventions](https://jellyfin.org/docs/general/server/media/shows/), including provider IDs so the
   match can never drift:

   ```text
   Shows/Lantern (2001) [tvdbid-100001] [tmdbid-200001]/Season 01/Lantern S01E04 - Glass Harbour.mp4
   Shows/Lantern (2001) [tvdbid-100001] [tmdbid-200001]/Season 01/Lantern S01E04 - Glass Harbour.en.srt
   ```

4. **Bring its subtitles along**, even when they are named differently from the video (`english.srt`, `Subs/2_English.srt`, …),
   tagging language, SDH and forced flags the way Jellyfin expects.
5. **Quarantine everything else** (READMEs, `.nfo`, samples, screenshots, `.txt` …) instead of deleting it. Quarantined
   items are removed automatically after a retention period (30 days by default) by a Jellyfin scheduled task.
6. **Refresh the filed folders** so the new item appears straight away (just those folders, not whole libraries).

Anything it cannot identify confidently is left where it is, never guessed, and listed under **Needs review** on the
plugin page, where you pick the right title (and library) with one click or search for it.

## Features

| Area | Behaviour |
|---|---|
| Watch folders | Any number. Each has one or more destinations, at most one per kind: a Shows library, a Movies library, or a single Mixed Movies and Shows library. Releases go to the destination for their kind; new episodes of a show already on the server join it in whichever library it's in. |
| Identification | Own release-name parser + your configured metadata providers; TMDb/TheTVDB matches preferred over IMDb-only ones; titles already in the library preferred; a confidence threshold and a clear lead over the runner-up before anything moves. |
| Naming | Movies: `Title (Year) [tmdbid-N]/Title (Year) [tmdbid-N].ext` (editions as ` - Label`). Shows: `Series (Year) [tvdbid-N] [tmdbid-N]/Season NN/Series SNNEMM - Title.ext`, multi-episode `S01E01-E02`, specials in `Season 00`. Reserved characters (`< > : " / \ \| ? *`) removed. |
| Subtitles | Matched by name, by folder (`Subs/`), or by being the only video in the release; renamed `<video>[.Title].<lang>[.default][.sdh][.forced].srt`. When a language has several tracks, the main one is marked default. |
| Extras | Trailers, featurettes, deleted scenes … filed into Jellyfin's extras folders. |
| Clutter | Moved to a dated quarantine folder (default or user-chosen), purged after N days (default 30). |
| Review | *Needs review* on the plugin page: reasons, candidate titles with provider links, a library picker, title search, retry, or quarantine the whole release. |
| Activity | *Recent activity* on the plugin page: filed, dry run, needs review, decisions, failures, quarantine and purges, with what went where. |
| Safety | Dry-run mode (on by default), never overwrites, never files a second copy of an episode or film already on the server, crash-safe moves (hidden temporary name, size check, then rename; interrupted moves are finished or discarded at the next start), all-or-nothing per release (a failure undoes the moves already made), a JSON-lines action log. |
| Library refresh | Asks Jellyfin to refresh just the film or show folders filed into after a real ingest. |

## Download clients

Ingest can only tell that a release has finished arriving when nothing in it changes for the settle time. Point your
download client at an **incomplete (temporary) folder outside the watch folder** and let it move finished downloads in,
or have it add an in-progress suffix (`.part`, `.!qb`, `.crdownload` …) until each file is complete. Some clients create
files at their full size before downloading them (pre-allocation), so file size alone proves nothing; Ingest also watches
write times, but a download that stalls for longer than the settle time can still look finished.

## Requirements

- Jellyfin **12.1** or newer (the plugin targets .NET 10).
- The Jellyfin service account must be able to **write** to the watch folders, the library folders and the quarantine
  folder. If your media lives on a NAS share, check the share's permissions/ACLs for the `jellyfin` user.
- Metadata providers configured in Jellyfin (TMDb is built in; TheTVDB is optional via its plugin).

## Installation

Pre-releases are published on the [Releases](../../releases) page. Download the `.zip`, extract it into
`<jellyfin data>/plugins/Ingest_<version>/`, and restart Jellyfin. A plugin repository manifest (for installing and
updating from **Dashboard → Plugins → Repositories**) is planned.

After installing, open **Dashboard → Plugins → Ingest** and add at least one watch folder with a destination library;
nothing is watched until you do. Dry run is on until you turn it off.

## Configuration

**Dashboard → Plugins → Ingest**

| Setting | Default | Notes |
|---|---|---|
| Watch folders | — | Required. One or more folders, each with one or more destination libraries (one per kind). |
| Quarantine folder | `<watch folder>/.ingest-quarantine` | Keep it on the same filesystem as the watch folder so moves are instant. |
| Quarantine retention | 30 days | Enforced by the *Purge Ingest quarantine* scheduled task. |
| Dry run | On | Records what would happen (see *Recent activity*) without moving anything. Turn off once you are happy with the results. |
| Settle time | 300 s | How long nothing in a release (files, sizes, write times) may change before it is processed. |
| Refresh filed folders after ingest | On | Only the film or show folders filed into are refreshed, never whole libraries. |

## What Ingest stores and logs

Everything stays on your server, in Jellyfin's plugin data folder (`<jellyfin data>/plugins/Jellyfin.Plugin.Ingest/`):

| File | What's in it | Kept |
|---|---|---|
| `state.json` | Releases waiting for review and the recent activity shown on the Ingest page (release names, paths, chosen titles). Who made a decision is not recorded. | The latest 300 activity entries |
| `actions.jsonl` | One line per file moved: time, release, source and destination. Used to finish or undo moves interrupted by a restart. | 90 days, at most 5 MB |
| `search-cache.json` | Recent title searches and their provider ids, so the same title isn't searched again. | 24 hours |

Jellyfin's log gets one line per release at Information level. Per-file moves and review reasons, which include full
paths, are logged only at Debug. Paths and release names can reveal account names, share names and where media came
from, so check a log before posting it publicly.

## Building

```bash
dotnet build src/Jellyfin.Plugin.Ingest/Jellyfin.Plugin.Ingest.csproj -c Release
```

Any .NET 10 SDK builds it (`global.json` sets the floor, so Linux distribution packages work), and package versions are locked in `packages.lock.json`. The Jellyfin
packages are pinned to the server version in `build.yaml`'s `targetAbi`; bump them together.

The output `Jellyfin.Plugin.Ingest.dll` goes in `<jellyfin data>/plugins/Ingest_<version>/`.
Continuous integration builds every push and pull request; tagged commits (`v*`) produce a release zip.

## Roadmap

- [x] Repository scaffolding
- [x] Plugin skeleton and settings page (watch folders with library picker, quarantine, retention, dry run, settle time)
- [x] Watch-folder service (periodic sweep, settle-time detection, partial-download and hidden-file handling)
- [x] Release-name parser (episode codes incl. `1x04`, `Lantern1E4`, `Season 1 Episode 4`, multi-episode, specials; title/year split; editions; extras; release-tag stripping)
- [x] Identification: provider lookup through Jellyfin, fuzzy title matching, confidence scoring, library-aware tie-breaking
- [x] Naming engine (movies, episodes, multi-episode, specials, editions, extras, subtitle sidecars)
- [x] Subtitle pairing and language/flag detection (incl. commentary tracks, content-based language fallback)
- [x] Planner (all-or-nothing per release) and executor with dry-run, never-overwrite, size verification and a JSON-lines action log
- [x] Quarantine + scheduled purge task
- [x] Refresh of the filed folders after ingest
- [x] Destinations per watch folder; review screen (library choice, title search); activity panel
- [x] Unit tests; CI; release packaging (pre-releases)
- [ ] Plugin repository manifest
- [x] New episodes of a show you already have follow it to the library it's in

Design notes live in [`docs/DESIGN.md`](docs/DESIGN.md).

## Security

This repository is public. **No secrets are committed or needed**: the plugin uses the metadata providers and API keys
already configured inside Jellyfin. Please report security issues as described in [SECURITY.md](SECURITY.md).

## Licence

[GPL-3.0](LICENSE), in line with Jellyfin's official plugins (the server itself is GPL-2.0).
