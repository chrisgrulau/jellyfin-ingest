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

1. **Wait until the copy has finished** (the file size has stopped changing).
2. **Identify it** using Jellyfin's own filename parser and the metadata providers you already have configured
   (TMDb, TheTVDB, …) — here: *Lantern*, season 1, episode 4.
3. **Rename and move it** into the right library using
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
6. **Trigger a library scan** so the new item appears straight away.

Anything it cannot identify confidently is left where it is and reported, never guessed.

## Features (planned)

| Area | Behaviour |
|---|---|
| Watch folders | Any number, each mapped to a target library (Movies, Shows, …). Picked in the plugin's settings page. |
| Identification | Jellyfin's `Emby.Naming` parser + your configured metadata providers; confidence threshold before anything moves. |
| Naming | Movies: `Title (Year) [tmdbid-N]/Title (Year) [tmdbid-N].ext` (editions as ` - Label`). Shows: `Series (Year) [tvdbid-N] [tmdbid-N]/Season NN/Series SNNEMM - Title.ext`, multi-episode `S01E01-E02`, specials in `Season 00`. Reserved characters (`< > : " / \ \| ? *`) removed. |
| Subtitles | Matched by name, by folder (`Subs/`), or by being the only video in the release; renamed `<video>.<lang>[.sdh][.forced].srt`. |
| Extras | Trailers, featurettes, deleted scenes … filed into Jellyfin's extras folders. |
| Clutter | Moved to a quarantine folder (default or user-chosen), purged after N days (default 30). |
| Safety | Dry-run mode, never overwrites, every action written to an activity log, collisions and low-confidence matches left for review on the settings page, where you pick the right title with one click. |
| Library refresh | Scans only the affected library after an ingest. |

## Requirements

- Jellyfin **12.1** or newer (the plugin targets .NET 10).
- The Jellyfin service account must be able to **write** to the watch folders, the library folders and the quarantine
  folder. If your media lives on a NAS share, check the share's permissions/ACLs for the `jellyfin` user.
- Metadata providers configured in Jellyfin (TMDb is built in; TheTVDB is optional via its plugin).

## Installation

Not yet published. Once releases exist you will be able to either:

- add this repository's plugin manifest URL under **Dashboard → Plugins → Repositories**, or
- download the release `.zip` and extract it into `<jellyfin data>/plugins/Ingest_<version>/`, then restart Jellyfin.

## Configuration

**Dashboard → Plugins → Ingest**

| Setting | Default | Notes |
|---|---|---|
| Watch folders | — | One or more folders, each with a target library. |
| Quarantine folder | `<watch folder>/.ingest-quarantine` | Keep it on the same filesystem as the watch folder so moves are instant. |
| Quarantine retention | 30 days | Enforced by the *Purge Ingest quarantine* scheduled task. |
| Dry run | On | Logs what would happen without moving anything. Turn off once you are happy with the results. |
| Settle time | 60 s | How long a file's size must stay unchanged before it is processed. |
| Scan library after ingest | On | |

## Building

```bash
dotnet build src/Jellyfin.Plugin.Ingest/Jellyfin.Plugin.Ingest.csproj -c Release
```

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
- [x] Library scan after ingest
- [x] Unit tests for parsing and naming; CI
- [ ] Release packaging and plugin repository manifest

Design notes live in [`docs/DESIGN.md`](docs/DESIGN.md).

## Security

This repository is public. **No secrets are committed or needed**: the plugin uses the metadata providers and API keys
already configured inside Jellyfin. Please report security issues as described in [SECURITY.md](SECURITY.md).

## Licence

[GPL-3.0](LICENSE), in line with Jellyfin and its official plugins.
