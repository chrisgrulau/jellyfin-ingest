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

### Replacing copies already on the server

A duplicate review item carries the copies it found (`ReviewItem.Existing`, kept as `PendingReviewItem.Existing`).
**Replace existing copies** (`POST Reviews/{id}/Replace`) needs every item to be such a duplicate, and the body must
repeat the copies the page showed (otherwise 409). The next sweep then plans with `IngestPlanner.ReplaceExisting`:

- each duplicate inside a library (all of a multi-episode file's) and its subtitle sidecars (same stem, subtitle
  extensions) are listed in `IngestPlan.Replacing`, and their paths count as free when naming the new files;
- they become quarantine moves into `<dated>/Replaced/`, placed first, so the new files can take their names;
- the executor accepts a source outside the release only if it is in `Replacing` and the move is a quarantine move.
  The moves are logged as quarantine, so the purge and a rollback treat them like any other.
- the new copy is filed where the copy it replaces lives: the same library folder, reusing the film or show folder
  (and an episode's season folder), when that folder belongs to one of the server's libraries of the right kind
  (`IngestPlanner.Libraries`). A library chosen in review (`ChosenMatch.Target`) wins. Each video follows its own
  replaced copy; videos with none are routed as usual. When the copy's folder isn't in such a library, the video is
  routed as usual and `IngestPlan.ReplacementNotes` says why. Whenever the new copy goes elsewhere, the replaced
  copy's folders are listed in `IngestPlan.TidyIfEmpty` and removed after filing if truly empty (never the library
  folder itself). `IngestPlan.ReplacementSummary` ("Replacing 1 copy in <library>.") is added to the activity entry.

### Decisions file by file

`PendingReview.FileDecisions` (by file, relative to the watch folder) holds `Replace` or `Quarantine` for single files
(`POST Reviews/{id}/Files`: files must be in the review; a replace repeats the copies the page showed, else 409;
"later" removes a decision). The decisions survive planning again (`PutReview`), and "Clear choice and retry" clears
them. The planner:

- sets aside videos marked `Quarantine` before identifying them; their subtitles (paired against every video, so they
  don't drift to another episode) and the videos go to the dated quarantine with the leftovers;
- replaces a duplicate marked `Replace` as if "Replace existing copies" had been asked for that file only;
- makes the plan a whole-release quarantine when every video is set aside.

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

### Episodes named by title

A name like `Show S13SP2 A Special Title` gives season 0 and an episode title but no number. `MediaIdentifier` then asks
`IMetadataLookup.ListSeasonAsync` for that season. Jellyfin's providers answer one episode at a time, so the lookup
asks for 1, 2, 3 … until three in a row are missing (at most 300). The providers cache whole seasons themselves, so this
is cheap after the first answer. `CachingMetadataLookup` keeps each list in memory for 24 hours (not on disk, because
the lists carry synopses).

- A title with similarity **≥ 0.85**, and at least 0.10 ahead of the next episode, is taken.
- Otherwise, if the tie-breaker is also an `IEpisodePicker` (the AI plugin, purpose `ingest.episode`), it gets up to
  40 episodes, likeliest first (title similarity, plus the year from the name). It sees each episode's code, title,
  year and the first 200 characters of its synopsis, and must answer with one of their positions or -1.
- Anything else leaves the release in review, with the reason.

The same path runs when a person chooses the series in review.

### Episodes from a transcript

When the series is known but the episode number isn't, and the title (if any) didn't settle it, `MediaIdentifier` can
compare a short transcript with the episode synopses. This needs an `ITranscriber` (`SpeechTranscriber`, through the
Subtitles plugin's `SpeechBridge`) and a tie-breaker that is also an `IEpisodePicker`.

1. **Episodes:** the season in the name, or seasons 1, 2 … until one is empty. Specials aren't guessed this way. More
   than 150 episodes gives up with a note, because a season number in the name would be needed.
2. **Transcript:** two minutes from 5:00 in, past most title sequences and recaps, or from 1:00 in if that gives too
   little speech (fewer than 15 words).
   - The Subtitles plugin decides whether Ingest may ask and which service it uses: its "Context for AI decisions"
     tier, built-in whisper by default. A paid service is metered against its own limits.
   - Transcripts are remembered per file (path, size, modified time) for the service's life, so a release is
     transcribed once.
3. **Pick:** the AI plugin (`ingest.episode`, medium effort) gets the file name, the series, the transcript (as data,
   at most 4,000 characters), and each episode's code, title and first 200 characters of synopsis. It must answer
   with a listed position or -1.

Anything else leaves the release in review, with the reason.

## Undo

Every real execution gets a run id, written on each of its action-log lines together with the destination's modified
time after the move (`modified`) and whether the source stayed (`kept`, copy and hard-link folders). A filing's
activity entry carries its run (`ActivityEntry.Run`), and **Undo** (`POST Ingest/Activity/{run}/Undo`) works from
exactly those lines (`ActionLogLine.CompletedMoves`: done or recovered, not rolled back).

`ReleaseUndo.Plan` checks everything before anything moves, and refuses the whole undo with a plain reason if:

- the entry isn't a filing with a run, was already undone, or is older than the action log keeps (90 days);
- the watch folder it came from is no longer configured (or fails the folder rules), or isn't there;
- a source is outside that watch folder, other than a replaced copy inside one of the server's library folders;
- a filed file (or quarantined clutter or replaced copy) is missing, or its size or modified time differ from the log;
- a place a file goes back to is taken, unless an earlier step of the same undo frees it (a replaced copy goes back
  where its replacement was, after the replacement has left);
- for a kept source (copy or hard link), the original in the watch folder has gone or changed size.

The undo is a plan for the ordinary executor: its moves are the filing's in reverse order, destination back to source,
and `IngestPlan.Returning` lists the exact files it may take (so the "source must be inside the release" guard becomes
"source must be one of these"). A kept source's library copy is instead moved to a hidden `.ingest-<id>.undone` name
beside it, and deleted (logged `deleted`) only once every move has succeeded, so a failure still rolls everything back.
A set-aside copy left by a crash is deleted at the next start (`PlanExecutor.FinishDeletes`, after `Recover`).
Afterwards the folders the filing's files were in are removed if truly empty, up to but never including a library,
quarantine or watch folder (a marked dated quarantine folder is never empty).

The page asks; the sweep does it (`IngestProgress.Queue`, carried out at the start of the next sweep, which is woken),
so an undo never races a filing or the watch-folder scan. Moves take `IngestProgress.FileGate`, as filing does. Before
the first move the release is put into review, **held** (`PendingReview.Held`, "Undone by an administrator — choose
what to do"), and a copy folder's `CopiedRelease` record is cleared; if the undo then fails and is rolled back, both
are put back. A held review with no request is never planned by the sweep, even after a restart; any decision (choose,
retry, file by file, quarantine) plans it as usual, and planning replaces the review without the hold. On success the
filing's entry gets `UndoneAt` (the page shows **Undone**), and an `Undone` entry is recorded (and copied to Jellyfin's
Activity log). A crash part-way leaves each file whole (the executor's recovery finishes or discards the interrupted
move) and the release held for review, possibly partly returned; nothing is filed again by itself.

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

- Dry run is set per watch folder, and is on for a new one. A folder saved before it was per folder takes the old
  global setting when the plugin loads, so upgrading changes nothing.
- Never overwrite; on collision, leave the release in place and report.
- Never delete except the retention purge of the quarantine folder, and a library copy whose original is still in a
  copy or hard-link watch folder when a filing is undone.
- Every operation is logged with source and destination so it can be reversed.

## Stored files

`state.json` and `search-cache.json` are read and written through common's `JsonFile` (a temporary file in the same
folder, flushed, then renamed over the old one). Each has its own policy for what a read finds:

| Found | `state.json` (dashboard) | `search-cache.json` |
|---|---|---|
| Missing | Start empty. | Start empty. |
| Damaged | Set aside as `state.json.corrupt-<time>` and logged; start empty. If it can't be moved, run from memory. | Start empty; the next save replaces it. |
| Unreadable | Logged; run from memory until restart, never replacing the file. | Start empty. |

A failed save keeps the dashboard in memory (logged once until the error changes); a failed cache save only loses the
cache. `actions.jsonl` is JSON Lines, appended one line per move, and trimmed in place at start-up.
