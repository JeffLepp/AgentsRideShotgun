namespace Deskweave.Product;

/// <summary>
/// Selects this standalone product's data folder before an engine is initialized. The safe
/// default is Deskweave, including helper and diagnostic processes that omit configuration.
/// Provider-owned files and user-selected export folders never pass through this class.
/// </summary>
public static class ProductContext
{
    public const string DefaultFolderName = "Deskweave";

    static readonly Lock Gate = new();
    static string _folderName = DefaultFolderName;
    static bool _pathsObserved;

    public static string FolderName
    {
        get
        {
            lock (Gate)
            {
                _pathsObserved = true;
                return _folderName;
            }
        }
    }

    public static string RoamingRoot => Path.Combine(
        RequiredSpecialFolder(Environment.SpecialFolder.ApplicationData), FolderName);

    public static string LocalRoot => Path.Combine(
        RequiredSpecialFolder(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>
    /// Configures this process before a product-owned path is first read. Repeating the same
    /// selection is harmless; changing products after an app has observed a path is refused so
    /// one process can never split its data across two owners.
    /// </summary>
    public static void Configure(string folderName)
    {
        string normalized = ValidateFolderName(folderName);
        lock (Gate)
        {
            if (_pathsObserved && !string.Equals(_folderName, normalized, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Product data paths are already owned by '{_folderName}'.");
            _folderName = normalized;
        }
    }

    public static string Roaming(params string[] segments) => CombineUnder(RoamingRoot, segments);

    public static string Local(params string[] segments) => CombineUnder(LocalRoot, segments);

    internal static string ValidateFolderName(string folderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);
        string value = folderName.Trim();
        if (value is "." or ".."
            || Path.IsPathFullyQualified(value)
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("The product folder must be one safe folder name.", nameof(folderName));
        return value;
    }

    static string RequiredSpecialFolder(Environment.SpecialFolder folder)
    {
        string value = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"Windows could not resolve {folder}.");
        return value;
    }

    static string CombineUnder(string root, IReadOnlyList<string> segments)
    {
        string current = root;
        foreach (string segment in segments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            if (segment is "." or ".."
                || Path.IsPathFullyQualified(segment)
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.Contains(Path.DirectorySeparatorChar)
                || segment.Contains(Path.AltDirectorySeparatorChar))
                throw new ArgumentException(
                    "Data path segments must be safe folder or file names.", nameof(segments));
            current = Path.Combine(current, segment);
        }
        return current;
    }
}
