using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Ai;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Parsing;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Settles close matches with the family's AI plugin, if it is installed and allowed to help Ingest.
/// <list type="bullet">
/// <item>Only the file name and the candidates' titles, years and kinds are sent (no paths, no user names).</item>
/// <item>The answer must be one of the offered positions, or -1 for none; anything else is ignored.</item>
/// <item>Each question is asked once per sweep (a season pack asks once, not once per episode).</item>
/// <item>Without an answer the release waits for review, as it always has.</item>
/// </list>
/// </summary>
public sealed class AiTiebreaker : ITiebreaker
{
    /// <summary>The purpose the AI plugin sees (and budgets under).</summary>
    public const string Purpose = "ingest.match";

    private const string Instructions =
        "A downloaded video must be filed under the right film or TV series. Choose the candidate this file most likely "
        + "is, using the file name, the title and year read from it, and whether it is an episode. A candidate already in "
        + "the user's library is a strong sign when it fits. If no candidate clearly fits, answer -1. Give a one-sentence "
        + "reason.";

    private static readonly object Schema = new
    {
        type = "object",
        properties = new
        {
            choice = new { type = "integer", description = "The index of the chosen candidate, or -1 if none clearly fits." },
            reason = new { type = "string", description = "One sentence." },
        },
        required = new[] { "choice", "reason" },
        additionalProperties = false,
    };

    private readonly Func<string, string, string, object, object, int, string, CancellationToken, Task<AiReply>> _ask;
    private readonly Dictionary<string, TiebreakPick> _asked = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="AiTiebreaker"/> class.
    /// </summary>
    public AiTiebreaker()
        : this(AiBridgeClient.AskAsync)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AiTiebreaker"/> class.
    /// </summary>
    /// <param name="ask">Asks the AI plugin (for tests).</param>
    internal AiTiebreaker(Func<string, string, string, object, object, int, string, CancellationToken, Task<AiReply>> ask)
    {
        _ask = ask ?? throw new ArgumentNullException(nameof(ask));
    }

    /// <inheritdoc />
    public async Task<TiebreakPick> PickAsync(ParsedRelease release, string fileName, IReadOnlyList<ScoredCandidate> options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(options);
        var key = string.Join('|', release.Kind, release.Title, release.Year, string.Join(';', options.Select(o => o.Candidate.Name + "/" + o.Candidate.Year + "/" + o.Candidate.IsSeries)));
        if (_asked.TryGetValue(key, out var earlier))
        {
            return earlier;
        }

        var data = new
        {
            file = fileName,
            title = release.Title,
            year = release.Year,
            episode = release.Season is { } s && release.Episode is { } e ? string.Create(CultureInfo.InvariantCulture, $"S{s:00}E{e:00}") : null,
            candidates = options.Select((o, i) => new
            {
                index = i,
                title = o.Candidate.Name,
                year = o.Candidate.Year,
                kind = o.Candidate.IsSeries ? "series" : "film",
                inLibrary = string.Equals(o.Candidate.Source, MediaIdentifier.LibrarySource, StringComparison.Ordinal),
            }),
        };
        var reply = await _ask("ingest", Purpose, Instructions, data, Schema, 2048, "low", cancellationToken).ConfigureAwait(false);
        var pick = Read(reply, options.Count);
        _asked[key] = pick;
        return pick;
    }

    /// <summary>
    /// Reads the AI plugin's reply into a pick, accepting only an offered position.
    /// </summary>
    /// <param name="reply">The reply.</param>
    /// <param name="count">How many options were offered.</param>
    /// <returns>The pick.</returns>
    internal static TiebreakPick Read(AiReply reply, int count)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!reply.Ok || reply.Answer is not { ValueKind: JsonValueKind.Object } answer)
        {
            // Not installed or not allowed: say nothing; anything else: say why the AI didn't settle it
            return new TiebreakPick(null, reply.Failure is "not-installed" or "not-allowed" ? string.Empty : "The AI plugin couldn't help: " + reply.Error, null);
        }

        var reason = answer.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? Trim(r.GetString()) : string.Empty;
        if (!answer.TryGetProperty("choice", out var c) || c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out var choice) || choice < -1 || choice >= count)
        {
            return new TiebreakPick(null, "The AI plugin's answer wasn't one of the candidates, so it was ignored.", null);
        }

        var by = "AI (" + (reply.Model ?? "model") + ")";
        return choice == -1
            ? new TiebreakPick(null, "The AI didn't think any candidate clearly fits: " + reason, null)
            : new TiebreakPick(choice, reason, by);
    }

    // Model text goes into the activity panel only: kept short and on one line
    private static string Trim(string? text)
    {
        var t = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return t.Length > 300 ? t[..300] + "…" : t;
    }
}
