using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Speech;
using Jellyfin.Plugin.Ingest.Identification;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Gets a short transcript from the family's Subtitles plugin, if it is installed and allows Ingest to ask (its own
/// settings page decides that, which speech-to-text service is used, and its spending limits).
/// <list type="bullet">
/// <item>Two minutes are transcribed, from five minutes in (past most title sequences and recaps), or from one minute
/// in when the video is too short for that.</item>
/// <item>The video stays on this server: the Subtitles plugin runs in the same process, and a cloud service is used
/// only if it was chosen there.</item>
/// <item>Each file is transcribed once while it is unchanged (remembered until the server restarts).</item>
/// </list>
/// </summary>
public sealed class SpeechTranscriber : ITranscriber
{
    /// <summary>The purpose the Subtitles plugin sees (and budgets under).</summary>
    public const string Purpose = "ingest.episode";

    /// <summary>The fewest words that count as a usable transcript.</summary>
    public const int MinWords = 15;

    /// <summary>The most transcripts remembered.</summary>
    public const int MaxRemembered = 200;

    private static readonly TimeSpan Length = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan[] Starts = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)];

    private readonly Func<string, string, string, TimeSpan, TimeSpan, string?, CancellationToken, Task<SpeechReply>> _transcribe;
    private readonly Dictionary<string, HeardText> _heard = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechTranscriber"/> class.
    /// </summary>
    public SpeechTranscriber()
        : this(SpeechBridgeClient.TranscribeAsync)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechTranscriber"/> class.
    /// </summary>
    /// <param name="transcribe">Asks the Subtitles plugin (for tests).</param>
    internal SpeechTranscriber(Func<string, string, string, TimeSpan, TimeSpan, string?, CancellationToken, Task<SpeechReply>> transcribe)
    {
        _transcribe = transcribe ?? throw new ArgumentNullException(nameof(transcribe));
    }

    /// <inheritdoc />
    public async Task<HeardText> TranscribeAsync(string videoPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(videoPath);
        var info = new FileInfo(videoPath);
        var key = videoPath + "|" + (info.Exists ? info.Length + "|" + info.LastWriteTimeUtc.Ticks : "missing");
        lock (_lock)
        {
            if (_heard.TryGetValue(key, out var earlier))
            {
                return earlier;
            }
        }

        var heard = await AskAsync(videoPath, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            if (_heard.Count >= MaxRemembered)
            {
                _heard.Clear();
            }

            _heard[key] = heard;
        }

        return heard;
    }

    /// <summary>
    /// Reads the Subtitles plugin's reply.
    /// </summary>
    /// <param name="reply">The reply.</param>
    /// <returns>What was heard, or why nothing usable was.</returns>
    internal static HeardText Read(SpeechReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!reply.Ok)
        {
            // Not installed or not allowed: say nothing; anything else: say why there's no transcript
            return new HeardText(null, reply.Failure is "not-installed" or "not-allowed" ? string.Empty : "No transcript from the Subtitles plugin: " + reply.Error);
        }

        var text = (reply.Text ?? string.Empty).Trim();
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= MinWords
            ? new HeardText(text.Length > 4000 ? text[..4000] : text, string.Empty)
            : new HeardText(null, "Too little speech was heard to tell which episode this is.");
    }

    private async Task<HeardText> AskAsync(string videoPath, CancellationToken ct)
    {
        HeardText heard = new(null, string.Empty);
        foreach (var start in Starts)
        {
            heard = Read(await _transcribe("ingest", Purpose, videoPath, start, Length, null, ct).ConfigureAwait(false));
            if (heard.Text is not null || !heard.Note.StartsWith("Too little", StringComparison.Ordinal))
            {
                return heard;
            }
        }

        return heard;
    }
}
