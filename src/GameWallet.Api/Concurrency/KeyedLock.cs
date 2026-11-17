using System.Collections.Concurrent;

namespace GameWallet.Api.Concurrency;

/// <summary>
/// One async lock per key. Every balance change for a player runs under that player's lock,
/// so a bet and a win arriving together cannot both read the same starting balance,
/// while different players are still served in parallel.
/// In a multi-instance deployment this would be a distributed lock or a database row lock.
/// </summary>
public sealed class KeyedLock
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        while (true)
        {
            entry = _entries.GetOrAdd(key, _ => new Entry());
            lock (entry)
            {
                if (!entry.Removed)
                {
                    entry.Users++;
                    break;
                }
            }
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Release(key, entry, semaphoreHeld: false);
            throw;
        }

        return new Releaser(() => Release(key, entry, semaphoreHeld: true));
    }

    private void Release(string key, Entry entry, bool semaphoreHeld)
    {
        if (semaphoreHeld)
            entry.Semaphore.Release();

        lock (entry)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                entry.Removed = true;
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users;
        public bool Removed;
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                release();
        }
    }
}
