using System.Text.Json;

namespace AbyssSniff.LocalizationCompat;

/// <summary>
/// Loads AbyssMod's generated translation cache and exposes fail-open aliases for
/// matching. The cache is polled lazily so it may appear or change after startup.
/// </summary>
public sealed class TranslationCache
{
    private static readonly string[] CacheFileNames = { "static.json", "ui_texts.json" };
    private static readonly (string Original, string Translated)[] RequiredCategoryAliases =
    {
        ("ラッシュ", "冲锋"),
        ("インパクト", "冲击"),
        ("セーフ", "安全"),
        ("リスク", "风险")
    };

    private readonly string _cacheRoot;
    private readonly TimeSpan _pollInterval;
    private readonly Action<string>? _info;
    private readonly Action<string>? _warn;
    private readonly object _gate = new();
    private AliasSnapshot _snapshot = AliasSnapshot.Empty;
    private FileStamp[] _stamps = Array.Empty<FileStamp>();
    private DateTime _nextCheckUtc = DateTime.MinValue;

    public TranslationCache(
        string cacheRoot,
        TimeSpan? pollInterval = null,
        Action<string>? info = null,
        Action<string>? warn = null)
    {
        _cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _info = info;
        _warn = warn;
    }

    public string? Canonicalize(string? value)
    {
        EnsureFresh();
        return _snapshot.Canonicalize(value);
    }

    public bool ContainsEquivalent(string? value, string? substring)
    {
        EnsureFresh();
        return _snapshot.ContainsEquivalent(value, substring);
    }

    public string[] ExpandAliases(IEnumerable<string>? values)
    {
        EnsureFresh();
        return _snapshot.ExpandAliases(values);
    }

    public void ReloadNow()
    {
        lock (_gate)
        {
            _nextCheckUtc = DateTime.MinValue;
        }

        EnsureFresh();
    }

    private void EnsureFresh()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now < _nextCheckUtc)
            {
                return;
            }

            _nextCheckUtc = now + _pollInterval;
            FileStamp[] current;
            try
            {
                current = ScanFiles();
            }
            catch (Exception ex)
            {
                _warn?.Invoke("cache scan skipped: " + ex.Message);
                return;
            }

            if (StampsEqual(_stamps, current))
            {
                return;
            }

            var builder = new AliasSnapshotBuilder();
            foreach (var alias in RequiredCategoryAliases)
            {
                builder.AddPair(alias.Original, alias.Translated);
            }

            var invalidFiles = 0;
            foreach (var stamp in current)
            {
                try
                {
                    using var stream = new FileStream(
                        stamp.Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                    AddMappings(document.RootElement, builder);
                }
                catch (Exception ex)
                {
                    invalidFiles++;
                    _warn?.Invoke($"ignored invalid cache '{stamp.Path}': {ex.Message}");
                }
            }

            // A partially-written update must not erase the last known-good map.
            if (current.Length == 0 || invalidFiles < current.Length)
            {
                _snapshot = builder.Build();
                _info?.Invoke($"loaded {_snapshot.PairCount} translation pairs from {current.Length} cache file(s)");
            }

            _stamps = current;
        }
    }

    private FileStamp[] ScanFiles()
    {
        if (!Directory.Exists(_cacheRoot))
        {
            return Array.Empty<FileStamp>();
        }

        var paths = new List<string>();
        foreach (var localeDirectory in Directory.EnumerateDirectories(_cacheRoot))
        {
            foreach (var fileName in CacheFileNames)
            {
                var path = Path.Combine(localeDirectory, fileName);
                if (File.Exists(path))
                {
                    paths.Add(Path.GetFullPath(path));
                }
            }
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths.Select(path =>
        {
            var info = new FileInfo(path);
            return new FileStamp(path, info.Length, info.LastWriteTimeUtc.Ticks);
        }).ToArray();
    }

    private static void AddMappings(JsonElement element, AliasSnapshotBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        builder.AddPair(property.Name, property.Value.GetString());
                    }
                    else
                    {
                        AddMappings(property.Value, builder);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    AddMappings(child, builder);
                }

                break;
        }
    }

    private static bool StampsEqual(IReadOnlyList<FileStamp> left, IReadOnlyList<FileStamp> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct FileStamp(string Path, long Length, long LastWriteTicks);
}

internal sealed class AliasSnapshotBuilder
{
    private readonly Dictionary<string, AliasNode> _nodes = new(StringComparer.Ordinal);
    private int _pairCount;

    public bool AddPair(string? original, string? translated)
    {
        original = original?.Trim();
        translated = translated?.Trim();
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(translated) || original == translated)
        {
            return false;
        }

        var originalNode = GetNode(original);
        var translatedNode = GetNode(translated);
        var root = Union(originalNode, translatedNode);
        root.Originals.Add(original);
        _pairCount++;
        return true;
    }

    public AliasSnapshot Build()
    {
        if (_nodes.Count == 0)
        {
            return AliasSnapshot.Empty;
        }

        var byRoot = new Dictionary<AliasNode, AliasGroup>();
        foreach (var entry in _nodes)
        {
            var root = Find(entry.Value);
            if (!byRoot.TryGetValue(root, out var group))
            {
                group = new AliasGroup(root.Members, root.Originals);
                byRoot.Add(root, group);
            }
        }

        var byValue = new Dictionary<string, AliasGroup>(StringComparer.Ordinal);
        foreach (var group in byRoot.Values)
        {
            foreach (var alias in group.Aliases)
            {
                byValue[alias] = group;
            }
        }

        return new AliasSnapshot(byValue, _pairCount);
    }

    private AliasNode GetNode(string value)
    {
        if (_nodes.TryGetValue(value, out var node))
        {
            return node;
        }

        node = new AliasNode(value);
        _nodes.Add(value, node);
        return node;
    }

    private static AliasNode Find(AliasNode node)
    {
        if (!ReferenceEquals(node.Parent, node))
        {
            node.Parent = Find(node.Parent);
        }

        return node.Parent;
    }

    private static AliasNode Union(AliasNode left, AliasNode right)
    {
        left = Find(left);
        right = Find(right);
        if (ReferenceEquals(left, right))
        {
            return left;
        }

        if (left.Rank < right.Rank)
        {
            (left, right) = (right, left);
        }

        right.Parent = left;
        if (left.Rank == right.Rank)
        {
            left.Rank++;
        }

        left.Members.UnionWith(right.Members);
        left.Originals.UnionWith(right.Originals);
        return left;
    }

    private sealed class AliasNode
    {
        public AliasNode(string value)
        {
            Parent = this;
            Members.Add(value);
        }

        public AliasNode Parent { get; set; }
        public int Rank { get; set; }
        public HashSet<string> Members { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Originals { get; } = new(StringComparer.Ordinal);
    }
}

internal sealed class AliasSnapshot
{
    public static readonly AliasSnapshot Empty = new(
        new Dictionary<string, AliasGroup>(StringComparer.Ordinal),
        0);

    private readonly IReadOnlyDictionary<string, AliasGroup> _byValue;

    public AliasSnapshot(IReadOnlyDictionary<string, AliasGroup> byValue, int pairCount)
    {
        _byValue = byValue;
        PairCount = pairCount;
    }

    public int PairCount { get; }

    public string? Canonicalize(string? value)
    {
        if (string.IsNullOrEmpty(value) || !_byValue.TryGetValue(value, out var group))
        {
            return value;
        }

        // If two original strings share one translation, keeping the input is safer
        // than selecting an arbitrary original. Alias-aware contains still checks all.
        if (group.Originals.Contains(value) || group.Originals.Length != 1)
        {
            return value;
        }

        return group.Originals[0];
    }

    public bool ContainsEquivalent(string? value, string? substring)
    {
        if (value is null || substring is null)
        {
            return false;
        }

        var values = GetAliases(value);
        var substrings = GetAliases(substring);
        foreach (var candidate in values)
        {
            foreach (var needle in substrings)
            {
                if (candidate.IndexOf(needle, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public string[] ExpandAliases(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            foreach (var alias in GetAliases(value))
            {
                if (seen.Add(alias))
                {
                    result.Add(alias);
                }
            }
        }

        return result.ToArray();
    }

    private IReadOnlyList<string> GetAliases(string value)
    {
        return _byValue.TryGetValue(value, out var group)
            ? group.Aliases
            : new[] { value };
    }
}

internal sealed class AliasGroup
{
    public AliasGroup(IEnumerable<string> aliases, IEnumerable<string> originals)
    {
        Originals = originals.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Aliases = aliases
            .OrderByDescending(value => Originals.Contains(value, StringComparer.Ordinal))
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    public string[] Aliases { get; }
    public string[] Originals { get; }
}
