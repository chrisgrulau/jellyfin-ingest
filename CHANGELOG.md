# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow Jellyfin's plugin versioning.

## [Unreleased]

### Added
- Release-name parser: episode codes (`S01E04`, `1x04`, `Lantern1E4`, `Season 1 Episode 4`, multi-episode, `S13SP2`-style
  specials), title/year separation (incl. titles containing years), editions, extras and samples, release-tag stripping
  that leaves ordinary words in titles alone, title inference from parent folders.
- Naming engine: Jellyfin-standard movie/series/season/episode/extras names with provider ids; subtitle sidecar
  names with language, `default`/`sdh`/`forced` flags and readable disambiguation; reserved-character sanitising.
- Unit tests (xunit v3) for the parser and naming engine.
- Repository scaffolding: licence (GPL-3.0), README, design notes, contribution and security policies,
  `.gitignore` / `.editorconfig`, CODEOWNERS, Dependabot, plugin project skeleton, CI workflow.
