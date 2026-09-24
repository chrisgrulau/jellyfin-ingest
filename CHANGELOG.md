# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow Jellyfin's plugin versioning.

## [Unreleased]

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

### Added (0.1.0-alpha.1)
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
