# Design notes

Working notes for Jellyfin Ingest. Descriptive of intent; will be updated as the implementation lands.

## Pipeline

```text
 watch folder ──► Watcher ──► Settler ──► Classifier ──► Planner ──► Executor ──► Library scan
   (FileSystemWatcher      (size stable   (parse name +   (target      (move / quarantine,
    + periodic sweep)       for N s)       provider        paths per     activity log,
                                           lookup)         naming        dry-run)
                                                           rules)
                                               │
                                               └─► low confidence / collision ─► left in place, reported
```

| Component | Jellyfin integration |
|---|---|
| **Watcher** | `IHostedService` sweeping each watch folder every 30 s. File-system notifications are deliberately not used: they are unreliable on network and virtualised shares, and a sweep is needed as a safety net anyway. Ignores hidden entries, the quarantine folder and partial downloads (`.part`, `.!qb`, `.crdownload`, `~$*`). |
| **Settler** | A release is processed only after every file in it has had a stable size for the settle time. |
| **Classifier** | `Emby.Naming` (`EpisodeResolver`, `VideoResolver`) for title / year / season / episode / edition parsing, then `IProviderManager` remote search against the libraries' configured providers. Scores candidates (title similarity, year, episode existence); below the threshold → review. |
| **Planner** | Pure function: `(classification, library root, options) → planned operations`. No I/O, fully unit-testable. |
| **Executor** | Same-filesystem `File.Move`, cross-filesystem copy-verify-delete. Never overwrites. Writes an activity log (`IActivityManager`) and a JSON action log for undo. |
| **Quarantine purge** | `IScheduledTask`, daily; deletes quarantined releases older than the retention period. |
| **Scan** | `ILibraryManager` refresh of the affected library only. |

## Naming rules

Follow the Jellyfin documentation exactly:

- Movies — <https://jellyfin.org/docs/general/server/media/movies/>
- Shows — <https://jellyfin.org/docs/general/server/media/shows/>
- External files — subtitle flags `default`, `forced`/`foreign`, `sdh`/`cc`/`hi`

Key points that are easy to get wrong:

1. **Movie files must start with the exact folder name, including provider IDs**, or multiple versions won't group:
   `Movie (2021) [tmdbid-123]/Movie (2021) [tmdbid-123] - Director's Cut.mkv`.
2. **Unknown tokens in subtitle names become the track title.** Use `.Alternate 2.en.srt`, never `.en.2.srt`.
3. Season folders are `Season 01`, never `S01`; specials are `Season 00`.
4. Multi-episode files: `S01E01-E02`.
5. Series folders may carry several IDs: `[tvdbid-N] [tmdbid-N]` — helps whichever provider is primary.
6. Strip `< > : " / \ | ? *`; replace `:` with ` -`; no trailing dots.

## Identification

`MediaIdentifier` scores every candidate from the configured providers (plus titles already in the destination
library) and only files automatically when the best is **≥ 0.80** and **clearly ahead** of the next distinct title
(0.08, or 0.15 when the release name has no year). Everything else is *needs review*.

Score = title similarity (max of an edit-distance ratio and a word-overlap score, on normalised titles)
+ year agreement (+0.10 exact, +0.05 off by one, −0.25 otherwise) + 0.20 if already in the library
+ a small nudge for the provider's own top results − a penalty when sequel numbers disagree
− 0.20 for a title known only by an IMDb id (no TMDb or TheTVDB id; typically an OMDb hit).

The thresholds were tuned against a private set of real release names with known ids (kept out of the repository):
a clear majority are identified automatically, higher when the destination library already has the title, and the
rest go to review rather than being guessed.

## Subtitle pairing

For each video in a release, in order:

1. Same stem: `Video.srt`, `Video.en.srt`, `Video.English.srt`.
2. Per-video subfolder: `Subs/<video stem>/N_English.srt` (common release layout); match on `SxxEyy` if the folder name
   differs slightly.
3. If the release contains exactly one video, any subtitle in the release belongs to it.

Language from the filename token (`en`, `eng`, `English`) or, failing that, from the text itself; SDH/forced from tokens.

## Clutter

Everything that isn't a video, a paired subtitle or recognised artwork goes to quarantine:
`<quarantine>/<yyyy-MM-dd>/<release name>/…` — dated folders make the retention purge trivial and safe.
Samples (`sample` in the name and under ~300 MB) are clutter too.

## Safety principles

- Dry run is the default for a new watch folder.
- Never overwrite; on collision, leave the release in place and report.
- Never delete except the retention purge of the quarantine folder.
- Every operation is logged with source and destination so it can be reversed.
