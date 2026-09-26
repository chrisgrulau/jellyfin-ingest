# Shoal Ingest

<p align="center"><img src="assets/shoal-ingest.png" alt="Shoal Ingest icon" width="480"></p>

Part of **Shoal**, a family of Jellyfin plugins that work together: [Shoal
Ingest](https://github.com/chrisgrulau/jellyfin-ingest) files new media into your libraries, [Shoal
Subtitles](https://github.com/chrisgrulau/jellyfin-subtitles) finds, checks and synchronises subtitles, and [Shoal
AI](https://github.com/chrisgrulau/jellyfin-ai) gives both optional AI help. Each works on its own; installed together,
they help each other.

A [Jellyfin](https://jellyfin.org) plugin that watches one or more **drop folders** and files new media into your
libraries automatically — identified, renamed to Jellyfin's naming standard, with its subtitles, and with the release
clutter set aside.

> **Status: alpha.** It works and is used daily, but settings and behaviour may still change between versions. Dry
> run is on until you turn it off. See the [roadmap](#roadmap).

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
| Clutter | Moved to a dated quarantine folder (default or user-chosen), purged after N days (default 30). *Quarantine* on the plugin page shows what is there and when each day's folder is deleted. |
| AI tie-breaker | Optional, with the Shoal AI plugin: a close call between candidates Ingest already found is settled by the AI choosing one of them (or none). Only file names, titles and years are sent for this (see [What Ingest sends](#what-ingest-sends)); every choice is shown in *Recent activity*. |
| Episodes from a transcript | Optional, with the Shoal Subtitles and AI plugins: when a name doesn't say which episode it is, two minutes of it are transcribed (built-in speech-to-text by default, on this server) and the AI picks the listed episode whose synopsis fits, or none. |
| Episodes named by title | A file that gives the episode title but no number (typically a special) is matched against that season's episode list from your providers; if no title clearly matches, the AI plugin (when installed) may choose one of the listed episodes. |
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

### Living with other tools

- **Sonarr and Radarr** import finished downloads themselves: don't point Ingest at the folders they import from, or
  both will fight over the same files. Ingest is for downloads nothing else files.
- **Seeding torrents:** set the watch folder to **hard-linked** (same drive; no extra space) or **copied**. The release
  then stays where the client seeds it, and Ingest remembers what it filed so it isn't filed again. **Moved** (the
  default) suits Usenet and anything nothing else needs afterwards.
- **Docker:** mount the downloads and the media under one parent folder (for example `/data/downloads` and
  `/data/media`), so moves are instant renames rather than copies across file systems.
- **Windows service:** use network paths (`\\nas\share\incoming`) rather than mapped drive letters, which services
  can't see.

## Requirements

- Jellyfin **12.1** or newer (the plugin targets .NET 10).
- The Jellyfin service account must be able to **write** to the watch folders, the library folders and the quarantine
  folder. If your media lives on a NAS share, check the share's permissions/ACLs for the `jellyfin` user.
- Metadata providers configured in Jellyfin (TMDb is built in; TheTVDB is optional via its plugin).

## Installation

**From the Shoal plugin repository (recommended):** in **Dashboard → Plugins → Repositories**, add

```
https://raw.githubusercontent.com/chrisgrulau/jellyfin-shoal/main/manifest.json
```

then install **Shoal Ingest** from the catalogue and restart Jellyfin. Jellyfin installs updates from a repository
automatically (daily, and at start-up) unless you switch that off for the plugin on its page under **My Plugins**.

**By hand:** download the `.zip` from the [Releases](../../releases) page, check it against `SHA256SUMS` (and, if you
like, `gh attestation verify <zip> --repo chrisgrulau/jellyfin-ingest`), extract it into
`<jellyfin data>/plugins/Ingest_<version>/`, and restart Jellyfin.

After installing, open **Dashboard → Plugins → Ingest** and add at least one watch folder with a destination library;
nothing is watched until you do. Dry run is on until you turn it off.

## Configuration

**Dashboard → Plugins → Ingest**

| Setting | Default | Notes |
|---|---|---|
| Watch folders | — | Required. One or more folders, each with one or more destination libraries (one per kind). Use the path as the Jellyfin server sees it: the container path in Docker, and on a Windows service a network path (`\\nas\share\incoming`) rather than a mapped drive letter, which services can't see. |
| Files are (per watch folder) | moved | **moved**, **hard-linked** (the release stays for seeding; no extra space; copied instead if the library is on another drive) or **copied** (the release stays; uses the space twice). With hard link or copy, clutter stays in the release too. |
| Quarantine folder | `<watch folder>/.ingest-quarantine` | Keep it on the same filesystem as the watch folder so moves are instant. |
| Quarantine retention | 30 days | Enforced by the *Purge Ingest quarantine* scheduled task. |
| Dry run | On | Records what would happen (see *Recent activity*) without moving anything. Turn off once you are happy with the results. |
| Settle time | 300 s | How long nothing in a release (files, sizes, write times) may change before it is processed. |
| Refresh filed folders after ingest | On | Only the film or show folders filed into are refreshed, never whole libraries. |
| Let the AI plugin settle close matches | On | Does nothing unless the Shoal AI plugin is installed and allows Ingest. |
| Identify unnamed episodes from a short transcript | On | Also needs the Shoal Subtitles plugin, with **Let Ingest ask for short transcripts** on there. |

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

## What Ingest sends

Nothing leaves the server unless you install and allow the other Shoal plugins, and then only for releases it can't
settle on its own:

| To | When | What |
|---|---|---|
| Your metadata providers (through Jellyfin) | Every release | The title and year read from the name (as Jellyfin's own identify does) |
| Shoal AI → your AI provider | A close match | The file name; candidate titles, years and kinds |
| Shoal AI → your AI provider | An episode named by title, or with no usable name | Also the season's episode titles, years and the start of each synopsis; for no usable name, up to 4,000 characters of transcribed dialogue |
| Shoal Subtitles → speech-to-text | An episode with no usable name, if Subtitles allows it | Two minutes of audio: it stays on the server with the built-in or a local service, and goes to a cloud service only if you chose one there |

No paths, user names or library names are sent.

## Upgrading and uninstalling

Upgrades keep your settings and state. When uninstalling, remove releases waiting for review first. Left behind in the
data folder: `state.json`, `actions.jsonl` and `search-cache.json`; and in each watch folder, the hidden
`.ingest-quarantine` folder, which is no longer purged once the plugin is gone (delete it by hand).

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
- [x] Plugin repository manifest (the Shoal repository)
- [x] New episodes of a show you already have follow it to the library it's in
- [x] Optional AI tie-breaker for close calls (Shoal AI plugin)
- [x] Episodes named by title only (specials), with the AI plugin as a fallback
- [x] Episodes with no usable name: a short transcript compared with episode synopses

Design notes live in [`docs/DESIGN.md`](docs/DESIGN.md).

## Security

This repository is public. **No secrets are committed or needed**: the plugin uses the metadata providers and API keys
already configured inside Jellyfin. Please report security issues as described in [SECURITY.md](SECURITY.md).

## Licence

[GPL-3.0](LICENSE), in line with Jellyfin's official plugins (the server itself is GPL-2.0).
