namespace NeonSidekick.Shopping;

/// <summary>
/// The loaded profile's <see cref="ShoppingStore"/> (2026-10-11): the one opened for the profile's folder, or a new one once a profile
/// switch moved it, the old one closed. <see cref="Current"/> is called from the tools' pool threads, hence the lock. The screen and
/// headless each hold one.
/// </summary>
public sealed class ShoppingStoreHolder(Func<string> profileDirectory, TimeProvider? time = null) : IDisposable
{
    private readonly Func<string> _profileDirectory = profileDirectory ?? throw new ArgumentNullException(nameof(profileDirectory));
    private readonly object _gate = new();
    private ShoppingStore? _store;
    private bool _disposed;

    /// <summary>The store of the profile loaded now.</summary>
    public ShoppingStore Current()
    {
        lock (_gate)
        {
            string directory = _profileDirectory();
            string wanted = Path.Combine(Path.GetFullPath(directory), ShoppingStore.FolderName);
            if (_store is not { } store || !string.Equals(store.Folder, wanted, StringComparison.OrdinalIgnoreCase))
            {
                _store?.Dispose();
                store = new ShoppingStore(directory, time);
                if (_disposed)
                {
                    store.Dispose();
                }

                _store = store;
            }

            return store;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _store?.Dispose();
        }
    }
}
