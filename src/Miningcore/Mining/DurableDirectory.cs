namespace Miningcore.Mining;

/// <summary>
/// Creates every missing directory component and durably publishes each new child entry in its
/// parent. This matters for safety state: syncing a file and its child directory does not make the
/// child directory's first appearance durable in the parent after a power loss.
/// </summary>
internal static class DurableDirectory
{
    public static void EnsureCreated(string directory,
        Action<string> syncDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        syncDirectory ??= ShareRecoveryFatalState.SyncDirectoryWhereSupported;

        var fullPath = Path.GetFullPath(directory);
        var missing = new Stack<string>();
        var current = fullPath;

        while(!Directory.Exists(current))
        {
            missing.Push(current);
            var parent = Path.GetDirectoryName(current);

            if(string.IsNullOrEmpty(parent) ||
               string.Equals(parent, current, StringComparison.Ordinal))
                throw new DirectoryNotFoundException(
                    $"No existing parent could be found while creating {fullPath}");

            current = parent;
        }

        while(missing.TryPop(out var child))
        {
            var parent = Path.GetDirectoryName(child)!;
            Directory.CreateDirectory(child);
            syncDirectory(parent);
        }
    }
}
