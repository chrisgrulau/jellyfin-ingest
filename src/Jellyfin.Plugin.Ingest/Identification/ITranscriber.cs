using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// What was heard in a short stretch of a video.
/// </summary>
/// <param name="Text">The words heard, or <c>null</c> if there is no usable transcript.</param>
/// <param name="Note">Why not, in words for the activity panel (empty when there is nothing worth saying).</param>
public sealed record HeardText(string? Text, string Note);

/// <summary>
/// Transcribes a short stretch of a video (for example through the Subtitles plugin), to tell which episode it is.
/// </summary>
public interface ITranscriber
{
    /// <summary>
    /// Transcribes a couple of minutes of the video.
    /// </summary>
    /// <param name="videoPath">The video's full path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was heard; never throws for an unavailable or failed service.</returns>
    Task<HeardText> TranscribeAsync(string videoPath, CancellationToken cancellationToken);
}
