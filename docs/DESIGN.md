# Design notes

Working notes for Jellyfin Ingest. Descriptive of intent; will be updated as the implementation lands.

## Pipeline

```text
 watch folder ──► Watcher ──► Settler ──► Classifier ──► Planner ──► Executor ──► Folder refresh
   (periodic sweep)       (unchanged     (parse name +   (target       (move / quarantine,
                           for N s)       provider        paths per      action log,
                                          lookup)         naming         dry-run)
                                                          rules)
                                               │
                                               └─► low confidence / collision ─► left in place, reported
```

| Component | Jellyfin integration |
|---|---|
| **Watcher** | `IHostedService` sweeping each watch folder every 30 s. File-system notifications are deliberately not used: they are unreliable on network and virtualised shares, and a sweep is needed as a safety net anyway. Ignores hidden entries, the quarantine folder and partial downloads (`.part`, `.!qb`, `.crdownload`, `~$*`). |
| **Settler** | A release is processed only after its files, sizes and last-write times have stayed unchanged for the settle time. |
| **Classifier** | The plugin's own release-name parser (`ReleaseNameParser`: title, year, season, episode, edition, extras, release tags), then `IProviderManager` remote search against the server's configured providers (cached; see *Identification*). Scores candidates (title similarity, year, titles already on the server); below the threshold → review. |
| **Planner** | `(release files, identification, library targets) → planned operations`, all-or-nothing per release. Disk access is only through callbacks (does a path exist, read a subtitle's text), so it is fully unit-testable. |
| **Executor** | Every file moves first to a hidden `.ingest-<id>.partial` name in its destination folder (a rename, or a copy across file systems), is size-verified, then renamed into place, so a half-copied file never appears under a real name (Jellyfin ignores hidden files). Each move is logged before (`intent`) and after (`done`) in `actions.jsonl`; a failure undoes the moves already made (and removes folders it created), and moves interrupted by a crash are finished or discarded at the next start. Never overwrites; every destination is re-checked against the plan's allowed folders first; free space is checked before a cross-file-system copy. |
| **Quarantine purge** | `IScheduledTask`, daily; deletes dated quarantine folders older than the retention period, but only ones Ingest created and marked (`.ingest-created`), never through links. |
| **Refresh** | `ILibraryMonitor.ReportFileSystemChanged` for each film or show folder filed into, the same path Jellyfin's real-time monitoring uses; never a server-wide scan. |

## Naming rules

Follow the Jellyfin documentation exactly:

- Movies — <https://jellyfin.org/docs/general/server/media/movies/>
- Shows — <https://jellyfin.org/docs/general/server/media/shows/>
- External files — subtitle flags `default`, `forced`/`foreign`, `sdh`/`cc`/`hi`

Key points that are easy to get wrong:

1. **Movie files must start with the exact folder name, including provider IDs**, or multiple versions won't group:
   `Movie (2021) [tmdbid-123]/Movie (2021) [tmdbid-123] - Director's Cut.mkv`.
2. **Unknown tokens in subtitle names become the track title.** Use `.Alternate 2.en.srt`, never `.en.2.srt`.
3. New season folders are `Season 01`, never `S01`; specials are `Season 00`. A show that already has its own season
   folder naming (`Season 1`, `S01`, `Specials`) keeps it.
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

### AI tie-breaker

When the result would be *needs review* but the best score is **≥ 0.60** and there are at least two candidates (for
episodes, only when the season and episode are known), `MediaIdentifier` hands the top five to an `ITiebreaker`. The
real one, `AiTiebreaker`, calls the Shoal AI plugin in process through the shared `AiBridgeClient` (caller `ingest`,
purpose `ingest.match`) with a JSON schema that allows only `{choice, reason}`.

- Sent: the file name, the parsed title, year and episode code, and per candidate its title, year, kind and whether
  it is already on the server. No paths, provider ids or library names.
- Accepted: an integer that is the position of an offered candidate. `-1` (none fits), anything else, an error or a
  missing AI plugin leaves the release in review; the AI's note is added to the reason unless the plugin simply
  isn't there or doesn't allow Ingest.
- A pick is identified exactly as a manual choice would be (`IdentifyAsChosenAsync`), so episode lookup and the
  never-a-second-copy rule still apply, and the result records who decided (`DecidedBy`), which the activity panel
  shows.
- Answers are memoised per sweep, so a release that waits for several sweeps isn't asked again each time.

## Subtitle pairing

For each video in a release, in order:

1. Same stem: `Video.srt`, `Video.en.srt`, `Video.English.srt`.
2. Per-video subfolder: `Subs/<video stem>/N_English.srt` (common release layout); match on `SxxEyy` if the folder name
   differs slightly.
3. If the release contains exactly one video, any subtitle in the release belongs to it.

Language from the filename token (`en`, `eng`, `English`) or, failing that, from the text itself; SDH/forced from tokens.

## Clutter

Everything that isn't a main video, an extra or a paired subtitle goes to quarantine (artwork such as `poster.jpg`
included: Jellyfin fetches its own):
`<quarantine>/<yyyy-MM-dd>/<release name>/…` — dated folders make the retention purge trivial and safe.
Samples (`sample` in the name and under ~300 MB) are clutter too.

## Safety principles

- Dry run is the default for a new watch folder.
- Never overwrite; on collision, leave the release in place and report.
- Never delete except the retention purge of the quarantine folder.
- Every operation is logged with source and destination so it can be reversed.
