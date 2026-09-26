# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow Jellyfin's plugin versioning.

## [Unreleased]

### Fixed

- **ING-19:** a library whose folder is a whole drive or file-system root (`E:\`, `/`) can be filed into, and a watch
  folder inside one is caught by the folder rules. Before, the containment check never matched a root.
- **ING-24:**
  - Jellyfin's metadata folder and transcode folder are protected like its other folders, even when they have been
    moved out of the data folder.
  - The folder rules also compare paths after following folder links, so a watch or quarantine folder that reaches a
    library through a symbolic link is caught.
  - A top-level release Jellyfin's account can't read now waits on the plugin page with the reason, instead of being
    skipped without a word.

## [0.1.7-alpha] - 2026-09-25

### Added

- **Quarantine** section on the plugin page: the dated quarantine folders, newest first, with each release's files and
  sizes and the day each folder is deleted (folders Ingest didn't create are shown as never deleted). Read-only, from the
  new `GET /Ingest/Quarantine` (administrators only); links are never followed and at most 2,000 files per folder are listed.

## [0.1.6-alpha] - 2026-09-25

### Changed
- The plugin family is now called **Shoal**: this plugin shows as "Shoal Ingest", and its quarantine task as "Purge
  Shoal Ingest quarantine" under "Shoal". Settings, data and the plugin id are unchanged.
- Builds (BLD-01, BLD-03): Jellyfin packages pinned to 12.1.0 with lock files and locked-mode restores; releases carry
  a build-provenance attestation (`gh attestation verify jellyfin-plugin-ingest.zip --repo chrisgrulau/jellyfin-ingest`).
  DESIGN.md and README corrected to match the code (DOC-01).
- `global.json` accepts any .NET 10 SDK (10.0.100 and later), so the SDKs shipped by Linux distributions (10.0.1xx)
  build it; CI uses the newest .NET 10 SDK, and Dependabot no longer raises the minimum. Package versions stay locked.

## [0.1.5-alpha] - 2026-09-25

### Fixed
- An offline library (an unmounted share) is never recreated on the local disk (ING-10): the library folder, or the
  existing show's folder, must exist when planning and again before moving. The release waits and is tried again
  automatically (after 5 minutes, doubling to hourly) until the share is back.
- A provider outage no longer strands releases in review (ING-07). "Nothing found" is tried again after 10 minutes,
  1 hour and 6 hours before being left for a person. After 12 empty searches in a row, identification pauses (10
  minutes, doubling to 2 hours) and the activity panel says why. The review card shows when the next try is due.
- Titles in non-Latin scripts (Cyrillic, Greek, CJK, Hebrew, Arabic, Thai …) now match, and two different ones from the
  same year are no longer merged into one candidate (ING-09).
- A Retry, Choose or Quarantine made while a release is being planned is no longer lost (ING-11).
- File names (ING-12): leading dots are removed (no hidden folders Jellyfin skips); a title with nothing printable
  becomes "Untitled"; titles and episode titles are shortened, between whole characters, to fit 255-byte names with
  room for subtitle flags; Windows device names (`CON`, `NUL` …) get an underscore.
- Paths are compared one way everywhere (ING-13): `/in/` and `/in` are the same watch folder, and folders are stored
  without a trailing separator. Reviews of a watch folder removed from the settings are dropped.
- The state file (ING-15): read and write errors no longer break the sweep or the settings page (the page runs from
  memory and the problem is logged once), an unreadable file is never overwritten, a damaged one is kept as
  `state.json.corrupt-<time>`, and null values in it are tolerated.

### Changed
- Provider searches are remembered (ING-07): results for 24 hours, kept across restarts in `search-cache.json`, so a
  season pack searches for its show once and restarts, retries or settings changes don't search again.
- The server's film or series names are loaded once per sweep instead of once per video (ING-08).
- After filing, only the film or show folders filed into are refreshed, instead of queueing Jellyfin's server-wide
  library scan (ING-14). The setting is now labelled "Refresh filed folders in Jellyfin after ingesting".

### Privacy
- Per-file moves and review reasons (full paths) are logged at Debug; Information gets one line per release (ING-17).
- The user who made a review decision is no longer stored.
- `actions.jsonl` keeps 90 days, at most 5 MB. The README lists what Ingest stores and for how long.

## [0.1.4-alpha] - 2026-09-25

### Security
- Folder guardrails (ING-03): a watch folder may not be a filesystem root, overlap any library folder, Jellyfin's own
  folders or another watch folder; a custom quarantine may not overlap a library or Jellyfin's folders, or contain a
  watch folder. The settings page asks the server to check before saving, and the service never sweeps (and the purge
  never cleans) an unsafe folder, reporting why once in the activity panel.
- The quarantine purge only deletes dated folders Ingest created: each is marked (`.ingest-created`) before anything is
  moved in, so an existing date-named folder (photo imports, backups) is never touched. Dated folders created before
  this release are marked once from Ingest's action log.
- Retention (at least 1 day) and settle time (at least 5 s) are clamped on save and when used.
- Provider ids are validated wherever they enter (ING-02): TMDb/TheTVDB must be digits and IMDb `tt` + digits, and the
  naming engine refuses any other id in a folder name, so an id crafted by a metadata plugin, an NFO file or a library
  edit can't steer a file outside the library.
- Choosing a title on the review screen only accepts a candidate the server itself produced for that review (by list
  and position); search results are stored with the review. A candidate sent by the browser is never trusted.
- Every destination is checked to stay inside its film or show folder (inside its library) or the quarantine folder,
  both when planning and again before anything is moved; an existing show is only joined if it is inside one of the
  server's libraries. A plan that fails the check moves nothing.
- Symbolic links and junctions are never followed (ING-01): nothing outside a watch folder can become part of a
  release, be moved, quarantined or purged, and link loops can't hang the sweep. A link at the top of a watch folder is
  held for review with an explanation.

### Fixed
- Moves are crash-safe and all-or-nothing (ING-06): each file moves to a hidden temporary name in its destination
  folder (a rename, or a copy across file systems), is size-verified, then renamed into place, so a crash or power cut
  mid-copy can never leave a truncated file under a real name in the library. Every move is logged before and after;
  a failure undoes the moves already made (and removes folders it created); moves interrupted by a crash are finished
  or discarded at the next start. Free space is checked before a cross-file-system copy, and a shutdown stops between
  files.
- A release is only processed once nothing in it has been written for the settle time (ING-04): file write times are
  part of the settle check, so downloads into pre-allocated, full-size files are no longer mistaken for finished ones.
  Write times are compared only with their own earlier values, so clock differences on network shares don't matter.
  New installs default to a 5-minute settle time (existing settings are kept), and the settings page and README explain
  how to set up download clients (an incomplete folder outside the watch folder, or an in-progress suffix).
- One unreadable watch folder or sub-folder no longer stops every other watch folder (ING-05); failures are reported
  once per distinct error. A file that vanishes mid-scan (a download renamed from `.part`) only defers its release.
- Quarantining a whole release on request can no longer fail the entire sweep; a failure is reported against it.
- A failure tidying up empty folders after every move succeeded is logged as a warning, not reported as a failed ingest.
- The quarantine folder is recognised even when configured with a trailing slash (part of ING-13).

## [0.1.3-alpha] - 2026-09-25

### Added
- Duplicate guard (PROC-01): an episode or film that's already on the server (any library, any file name or
  container, or not yet scanned) goes to review with the existing file's path instead of being filed as a second
  copy. A multi-episode file overlapping an existing episode, and the same episode twice in one release, are caught
  too. A different edition of a film you have (e.g. a Director's Cut) is still filed as another version. Editions are
  read by comparing a film's file name with its folder, so titles with a colon (sanitised to " - ") aren't mistaken
  for editions, and episodes are found in any season folder (`Season 1`, `S01`, `Specials` …).

### Fixed
- New episodes of an existing show go into the season folder the show already uses (`Season 1`, `S01`, `Specials` …)
  instead of a second `Season NN` folder (ING-16).
- File-system, NAS and OS housekeeping entries in a watch folder (`lost+found`, `$RECYCLE.BIN`,
  `System Volume Information`, `@eaDir`, `#recycle`, `Thumbs.db`, `desktop.ini` …) are ignored rather than treated
  as releases. Hidden entries such as `.Trash-1000` already were.

## [0.1.0-alpha.3] - 2026-09-24

> **Correction:** this release's notes also listed a duplicate guard, but that change missed the release (PROC-01). It
> ships in 0.1.3-alpha.

### Added
- New episodes follow their show: an episode of a series that's already on the server is filed into that series'
  folder, whichever library it's in (a library chosen in review still wins). Works even when the watch folder has no
  TV destination.
- Changing a watch folder's outcome-deciding settings (turning dry run off, changing destinations) plans every release
  still waiting in it again; no restart needed.

### Changed
- Identification counts titles in any library as "already in the library", not only the watch folder's destinations.

## [0.1.0-alpha.2] - 2026-09-24

### Added
- **Destinations per watch folder:** each watch folder files into one or more libraries, at most one per kind: a Shows
  library, a Movies library, or a single Mixed Movies and Shows library (which takes both). Each release goes to the
  destination for its kind, so one drop folder can take both films and shows. Overlapping destinations are refused
  by the settings page and ignored (with a log warning) by the service. Other library types (music, books, home
  videos …) are never offered.
- **Setup required:** until at least one enabled watch folder with at least one destination is saved, the settings
  page shows a setup prompt and Save refuses an incomplete setup; nothing is watched until then.
- Settings page **Needs review** section: each waiting release with its reasons and the candidate titles (score,
  top-match / IMDb-only / in-library tags, TMDb / TheTVDB / IMDb links) and a *File into* library picker offering
  only libraries of the right kind (e.g. either of two Movies libraries for a film).
  - *Use this* files the release as that title into that library on the next sweep.
  - *Search for a different title* (title, year, film or show) when the suggestions are wrong or empty.
  - *Retry* plans it again (e.g. after renaming files or clearing a destination); *Quarantine release* moves the
    whole release to quarantine.
  - A film found in a folder with no film destination (or vice versa) keeps its match for review instead of losing it.
  - A choice made while dry run is on is kept, so the release files the same way once dry run is turned off.
- Settings page **Recent activity** panel (filterable): filed, dry run, needs review, decisions (who chose what),
  failed, quarantined and purged, each with what went where or why not. Kept across restarts (last 300 entries);
  identical repeats (e.g. a dry run re-planned after a restart) aren't recorded twice.
- Admin-only API used by the page: `GET /Ingest/Status`, `GET /Ingest/Search`, `POST /Ingest/Reviews/{id}/Choose`,
  `…/Retry`, `…/Quarantine`.

- When a video has several subtitles in one language (main, SDH, commentary …), the main one is filed as
  `.default` so Jellyfin doesn't pick a titled extra that happens to sort first.

### Changed
- CI actions are pinned to full commit SHAs (with the version in a comment; Dependabot keeps them current).
- Reviews and activity are stored in `state.json` in the plugin's data folder (replaces `review.jsonl`).
- A release whose planning throws (e.g. a provider outage) is reported as failed and left for review instead of
  stopping the sweep and being retried every 30 s.
- A 0.1.0-alpha.1 configuration (single target library) keeps working and is converted to a destination on the next
  save.

## [0.1.0-alpha.1] - 2026-09-24

### Added
- Background service: sweeps watch folders every 30 s (file-system notifications are unreliable on network
  shares), waits for each release to settle, plans, executes (or dry-runs) and queues a library scan; releases needing
  review are logged and recorded in `review.jsonl` and not retried until they change.
- "Purge Ingest quarantine" scheduled task (daily) removing dated quarantine folders past the retention period.
- Settings page: watch folders with a picker for the target library and library folder, quarantine folder,
  retention, settle time, dry run, scan after ingest.
- Planning: release classification (video / subtitle / sample / clutter), subtitle pairing across common release
  layouts with language, SDH, forced and commentary detection, extras filed under their film or series, dated
  quarantine for clutter; a release is filed completely or not at all.
- Execution: never overwrites, verifies sizes after every move, JSON-lines action log for undo, stops at the first
  failure, tidies empty folders; dry-run reports without touching anything.
- Identification: searches Jellyfin's configured metadata providers (no plugin credentials), fuzzy title matching
  (typos, roman numerals, `&`/and, leading "The", accents, `³`), year and sequel-number checks, merging of the same
  title across providers, preference for titles already in the destination library, and a strict accept rule that
  sends ambiguous cases to review instead of guessing.
- Release-name parser: episode codes (`S01E04`, `1x04`, `Lantern1E4`, `Season 1 Episode 4`, multi-episode, `S13SP2`-style
  specials), title/year separation (incl. titles containing years), editions, extras and samples, release-tag stripping
  that leaves ordinary words in titles alone, title inference from parent folders.
- Naming engine: Jellyfin-standard movie/series/season/episode/extras names with provider ids; subtitle sidecar
  names with language, `default`/`sdh`/`forced` flags and readable disambiguation; reserved-character sanitising.
- Unit tests (xunit v3) for the parser and naming engine.
- Repository scaffolding: licence (GPL-3.0), README, design notes, contribution and security policies,
  `.gitignore` / `.editorconfig`, CODEOWNERS, Dependabot, plugin project skeleton, CI workflow.
