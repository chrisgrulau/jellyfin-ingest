using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// The gate held while Ingest moves or deletes files: filing, quarantining, undo, restore, recovery, "Delete now" and the
/// quarantine purge. Only one of them runs at a time, so a deletion never races a move into the same folder. Unlike a
/// <c>lock</c> it can be waited for asynchronously and held across <c>await</c> (a deletion waits between retries).
/// Not re-entrant.
/// </summary>
#pragma warning disable CA1001 // Lives as long as the plugin; SemaphoreSlim only needs disposing once its wait handle is used, which it never is
public sealed class FileOperationsGate
#pragma warning restore CA1001
{
    /// <summary>How long "Delete now" and the purge wait for a filing that is running to finish.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Gets a value indicating whether something holds the gate now.</summary>
    public bool IsHeld => _semaphore.CurrentCount == 0;

    /// <summary>
    /// Waits for the gate (as long as it takes, like a <c>lock</c>).
    /// </summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Releases the gate when disposed.</returns>
    public IDisposable Enter(CancellationToken cancellationToken = default)
    {
        _semaphore.Wait(cancellationToken);
        return new Holder(_semaphore);
    }

    /// <summary>
    /// Waits for the gate for up to <paramref name="timeout"/>.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Releases the gate when disposed, or <c>null</c> if it wasn't free in time.</returns>
    public async Task<IDisposable?> TryEnterAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => await _semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false) ? new Holder(_semaphore) : null;

    private sealed class Holder(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
