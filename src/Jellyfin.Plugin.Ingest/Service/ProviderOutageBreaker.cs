using System;
using System.Globalization;
using Jellyfin.Plugin.Ingest.Identification;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Pauses identification when many title searches in a row found nothing: most likely a provider outage (which looks
/// like "nothing found" to plugins), so quota isn't spent and releases aren't sent to review for it. Each pause is
/// longer while the searches still find nothing.
/// </summary>
public sealed partial class ProviderOutageBreaker
{
    /// <summary>
    /// Provider searches in a row that may come back empty before identification pauses (a release can make up to five,
    /// so this is about three unknown releases running). An outage looks like "nothing found" to plugins.
    /// </summary>
    public const int EmptySearchesBeforePause = 12;

    private readonly IngestStateStore _state;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private DateTimeOffset _pausedUntil;
    private int _pauses;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderOutageBreaker"/> class.
    /// </summary>
    /// <param name="state">Activity shown on the dashboard (the first pause is reported there).</param>
    /// <param name="clock">Clock.</param>
    /// <param name="logger">The service's logger.</param>
    public ProviderOutageBreaker(IngestStateStore state, TimeProvider clock, ILogger logger)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets a value indicating whether identification is paused now.</summary>
    public bool IsPaused => _clock.GetUtcNow() < _pausedUntil;

    /// <summary>
    /// Pauses identification if the searches in a row that found nothing reached <see cref="EmptySearchesBeforePause"/>;
    /// a search that found something ends the run of pauses.
    /// </summary>
    /// <param name="lookup">The service's search cache, which counts empty searches.</param>
    public void Check(CachingMetadataLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (lookup.ConsecutiveEmpty == 0)
        {
            _pauses = 0;
            return;
        }

        if (lookup.ConsecutiveEmpty < EmptySearchesBeforePause)
        {
            return;
        }

        var pause = TimeSpan.FromMinutes(Math.Min(120, 10 * Math.Pow(2, Math.Min(_pauses, 10))));
        _pausedUntil = _clock.GetUtcNow() + pause;
        LogProvidersPaused(_logger, lookup.ConsecutiveEmpty, pause.TotalMinutes);
        if (_pauses == 0)
        {
            _state.Record(new ActivityEntry
            {
                Time = _clock.GetUtcNow(),
                Status = ActivityStatus.Failed,
                Release = "Metadata providers",
                Summary = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The last {lookup.ConsecutiveEmpty} title searches found nothing, so the metadata providers may be down. Identifying new releases is paused for {pause.TotalMinutes:0} minutes, then tried again (pausing for longer, up to 2 hours, while they still find nothing)."),
            });
        }

        _pauses++;
        lookup.ResetEmptyCount();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest: the last {Count} title searches found nothing; pausing identification for {Minutes} minutes")]
    private static partial void LogProvidersPaused(ILogger logger, int count, double minutes);
}
