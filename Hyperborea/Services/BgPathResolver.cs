using Dalamud.Game;
using ECommons.ExcelServices;
using Lumina.Excel.Sheets;
using System.Reflection;

namespace Hyperborea.Services;

public sealed class BgPathResolver
{
    static readonly ClientLanguage[] SearchLanguageCandidates =
    [
        ClientLanguage.ChineseSimplified,
        ClientLanguage.English,
        ClientLanguage.Japanese,
        ClientLanguage.German,
        ClientLanguage.French,
        ClientLanguage.ChineseTraditional,
        ClientLanguage.Korean,
    ];

    static readonly string[] RemovableExtensions =
    [
        ".lvb",
        ".lgb",
        ".sgb",
        ".pcb",
        ".uld",
        ".tex",
    ];

    static readonly string[] PathFieldHints =
    [
        "bg",
        "path",
        "layout",
        "lvb",
        "territory",
    ];

    readonly object cacheLock = new();
    readonly Dictionary<ClientLanguage, bool> languageSupport = [];
    readonly HashSet<string> loggedWarnings = [];
    List<BgPathTerritoryCandidate> candidates = [];
    Dictionary<string, List<BgPathTerritoryCandidate>> basicPathIndex = [];
    Dictionary<string, List<BgPathTerritoryCandidate>> normalizedPathIndex = [];
    Dictionary<string, List<BgPathTerritoryCandidate>> lastTwoSegmentIndex = [];
    bool built;

    public BgPathResolverStats Stats { get; private set; } = new();

    public void Invalidate()
    {
        lock (cacheLock)
        {
            built = false;
            candidates = [];
            basicPathIndex = [];
            normalizedPathIndex = [];
            lastTwoSegmentIndex = [];
            Stats = new();
        }
    }

    public IReadOnlyList<BgPathMatch> ResolveTerritoriesByBgPath(string bgPath)
    {
        EnsureBuilt();
        var basicPath = NormalizeBasicPath(bgPath);
        var normalizedPath = NormalizePath(bgPath);
        if (normalizedPath.IsNullOrEmpty()) return [];

        var matches = new Dictionary<uint, BgPathMatch>();
        AddMatches(matches, basicPathIndex.GetValueOrDefault(basicPath), bgPath, BgPathMatchKind.Exact, 100);
        AddMatches(matches, normalizedPathIndex.GetValueOrDefault(normalizedPath), bgPath, BgPathMatchKind.Normalized, 90);

        foreach (var candidate in candidates)
        {
            if (candidate.NormalizedPath.IsNullOrEmpty()) continue;
            if (candidate.NormalizedPath == normalizedPath) continue;
            if (candidate.NormalizedPath.Length < 8 || normalizedPath.Length < 8) continue;
            if (candidate.NormalizedPath.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.Contains(candidate.NormalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                AddMatch(matches, candidate, bgPath, BgPathMatchKind.Contains, 75);
            }
        }

        var lastTwoSegments = GetLastSegments(normalizedPath, 2);
        if (!lastTwoSegments.IsNullOrEmpty())
        {
            AddMatches(matches, lastTwoSegmentIndex.GetValueOrDefault(lastTwoSegments), bgPath, BgPathMatchKind.LastSegment, 60);
        }

        return matches.Values
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.TerritoryId)
            .ToArray();
    }

    public string CreateDiagnostic(string bgPath, IReadOnlyList<BgPathMatch>? knownMatches = null)
    {
        var matches = knownMatches ?? ResolveTerritoriesByBgPath(bgPath);
        var normalized = NormalizePath(bgPath);
        var matchText = matches.Count == 0
            ? "none"
            : string.Join(", ", matches.Select(x => $"{x.TerritoryId} ({x.MatchKind}, {x.TerritoryBgPath})"));
        return $"{bgPath} normalized = {normalized}; matched territories = {matchText}";
    }

    public bool HasAnyTerritoryPathContaining(string value)
    {
        EnsureBuilt();
        var normalized = NormalizePath(value);
        if (normalized.IsNullOrEmpty()) return false;
        return candidates.Any(x => x.NormalizedPath.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(x.NormalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    void EnsureBuilt()
    {
        lock (cacheLock)
        {
            if (built) return;
            Build();
            built = true;
        }
    }

    void Build()
    {
        var nextCandidates = new List<BgPathTerritoryCandidate>();
        var stats = new BgPathResolverStats();
        foreach (var territory in EnumerateTerritories())
        {
            foreach (var pathInfo in ExtractTerritoryPaths(territory))
            {
                var basicPath = NormalizeBasicPath(pathInfo.Path);
                var normalizedPath = NormalizePath(pathInfo.Path);
                if (normalizedPath.IsNullOrEmpty()) continue;
                var candidate = new BgPathTerritoryCandidate(
                    territory.RowId,
                    pathInfo.FieldName,
                    pathInfo.Path,
                    basicPath,
                    normalizedPath,
                    GetLastSegments(normalizedPath, 2),
                    territory.Map.RowId != 0,
                    territory.PlaceName.RowId != 0,
                    IsOldSelectorVisible(territory));
                nextCandidates.Add(candidate);
            }
        }

        candidates = nextCandidates
            .GroupBy(x => $"{x.TerritoryId}:{x.FieldName}:{x.NormalizedPath}")
            .Select(x => x.First())
            .ToList();
        basicPathIndex = BuildIndex(candidates, x => x.BasicPath);
        normalizedPathIndex = BuildIndex(candidates, x => x.NormalizedPath);
        lastTwoSegmentIndex = BuildIndex(candidates.Where(x => !x.LastTwoSegments.IsNullOrEmpty()), x => x.LastTwoSegments);
        stats.TerritoryRowsScanned = candidates.Select(x => x.TerritoryId).Distinct().Count();
        stats.PathFieldsIndexed = candidates.Count;
        stats.UniqueNormalizedPaths = normalizedPathIndex.Count;
        Stats = stats;
    }

    static Dictionary<string, List<BgPathTerritoryCandidate>> BuildIndex(IEnumerable<BgPathTerritoryCandidate> source, Func<BgPathTerritoryCandidate, string> getKey)
    {
        var ret = new Dictionary<string, List<BgPathTerritoryCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in source)
        {
            var key = getKey(candidate);
            if (key.IsNullOrEmpty()) continue;
            if (!ret.TryGetValue(key, out var list))
            {
                list = [];
                ret[key] = list;
            }
            list.Add(candidate);
        }
        return ret;
    }

    IEnumerable<TerritoryType> EnumerateTerritories()
    {
        foreach (var language in GetSafeSearchLanguages())
        {
            var sheet = Svc.Data.GetExcelSheet<TerritoryType>(language);
            if (sheet == null) continue;
            foreach (var territory in sheet)
            {
                yield return territory;
            }
            yield break;
        }
    }

    IEnumerable<(string FieldName, string Path)> ExtractTerritoryPaths(TerritoryType territory)
    {
        foreach (var prop in typeof(TerritoryType).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.GetIndexParameters().Length == 0))
        {
            if (!LooksLikePathField(prop.Name)) continue;
            object? value;
            try
            {
                value = prop.GetValue(territory);
            }
            catch (Exception e)
            {
                LogWarningOnce($"Field:{prop.Name}", e, $"Failed to read TerritoryType.{prop.Name}.");
                continue;
            }

            var text = ValueToText(value);
            if (LooksLikeBgPath(text))
            {
                yield return (prop.Name, text);
            }
        }
    }

    IEnumerable<ClientLanguage> GetSafeSearchLanguages()
    {
        var seen = new HashSet<ClientLanguage>();
        foreach (var language in SearchLanguageCandidates.Prepend(Svc.ClientState.ClientLanguage))
        {
            if (!seen.Add(language)) continue;
            if (IsSupportedLanguage(language)) yield return language;
        }
    }

    bool IsSupportedLanguage(ClientLanguage language)
    {
        var name = language.ToString();
        if (name.Equals("None", StringComparison.OrdinalIgnoreCase) || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return false;
        if (languageSupport.TryGetValue(language, out var supported)) return supported;
        try
        {
            var sheet = Svc.Data.GetExcelSheet<TerritoryType>(language);
            supported = sheet != null;
            if (supported) _ = sheet!.Count;
        }
        catch (Exception e)
        {
            supported = false;
            LogWarningOnce($"UnsupportedLanguage:{language}", e, $"Skipping unsupported territory language {language}.");
        }
        languageSupport[language] = supported;
        return supported;
    }

    static bool LooksLikePathField(string fieldName)
    {
        return PathFieldHints.Any(x => fieldName.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    static bool LooksLikeBgPath(string text)
    {
        return !text.IsNullOrEmpty()
            && (text.Contains('/') || text.Contains('\\'))
            && !text.StartsWith("#", StringComparison.Ordinal)
            && !text.Contains("Lumina.", StringComparison.OrdinalIgnoreCase);
    }

    static string ValueToText(object? value)
    {
        if (value == null) return "";
        if (value is string text) return text;
        try
        {
            var method = value.GetType().GetMethod("GetText", Type.EmptyTypes);
            if (method != null) return method.Invoke(value, [])?.ToString() ?? "";
        }
        catch
        {
        }
        return "";
    }

    static bool IsOldSelectorVisible(TerritoryType territory)
    {
        return territory.PlaceName.RowId != 0
            || territory.ContentFinderCondition.RowId != 0
            || territory.QuestBattle.RowId != 0;
    }

    static void AddMatches(Dictionary<uint, BgPathMatch> matches, IEnumerable<BgPathTerritoryCandidate>? candidates, string sourcePath, BgPathMatchKind kind, int baseScore)
    {
        if (candidates == null) return;
        foreach (var candidate in candidates)
        {
            AddMatch(matches, candidate, sourcePath, kind, baseScore);
        }
    }

    static void AddMatch(Dictionary<uint, BgPathMatch> matches, BgPathTerritoryCandidate candidate, string sourcePath, BgPathMatchKind kind, int baseScore)
    {
        var score = baseScore
            + (candidate.WasVisibleInOldSelector ? 8 : 0)
            + (candidate.HasMap ? 6 : 0)
            + (candidate.HasPlaceName ? 4 : 0);
        var match = new BgPathMatch(candidate.TerritoryId, sourcePath, candidate.OriginalPath, candidate.FieldName, kind, score);
        if (!matches.TryGetValue(candidate.TerritoryId, out var existing) || existing.Score < match.Score)
        {
            matches[candidate.TerritoryId] = match;
        }
    }

    public static string NormalizeBasicPath(string path)
    {
        var ret = path.Trim().Replace('\\', '/').ToLowerInvariant();
        while (ret.Contains("//", StringComparison.Ordinal))
        {
            ret = ret.Replace("//", "/", StringComparison.Ordinal);
        }
        return ret.Trim('/');
    }

    public static string NormalizePath(string path)
    {
        var ret = NormalizeBasicPath(path);
        if (ret.StartsWith("bg/", StringComparison.Ordinal)) ret = ret[3..];
        foreach (var extension in RemovableExtensions)
        {
            if (ret.EndsWith(extension, StringComparison.Ordinal))
            {
                ret = ret[..^extension.Length];
                break;
            }
        }
        return ret.Trim('/');
    }

    public static string GetLastSegments(string normalizedPath, int count)
    {
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < count) return "";
        return string.Join("/", segments.Skip(segments.Length - count));
    }

    void LogWarningOnce(string key, Exception e, string message)
    {
        var warningKey = $"{key}:{e.GetType().FullName}:{e.Message}";
        if (!loggedWarnings.Add(warningKey)) return;
        PluginLog.Warning($"[BgPathResolver] {message} {e.GetType().Name}: {e.Message}");
    }
}

public sealed record BgPathTerritoryCandidate(
    uint TerritoryId,
    string FieldName,
    string OriginalPath,
    string BasicPath,
    string NormalizedPath,
    string LastTwoSegments,
    bool HasMap,
    bool HasPlaceName,
    bool WasVisibleInOldSelector);

public sealed record BgPathMatch(
    uint TerritoryId,
    string SourcePath,
    string TerritoryBgPath,
    string FieldName,
    BgPathMatchKind MatchKind,
    int Score)
{
    public IEnumerable<string> SearchParts
    {
        get
        {
            yield return SourcePath;
            yield return TerritoryBgPath;
            yield return BgPathResolver.NormalizePath(SourcePath);
            yield return BgPathResolver.NormalizePath(TerritoryBgPath);
            yield return BgPathResolver.GetLastSegments(BgPathResolver.NormalizePath(SourcePath), 2);
            yield return TerritoryId.ToString();
            yield return $"Territory {TerritoryId}";
            yield return MatchKind.ToString();
        }
    }
}

public enum BgPathMatchKind
{
    Exact,
    Normalized,
    Contains,
    LastSegment,
}

public sealed class BgPathResolverStats
{
    public int TerritoryRowsScanned;
    public int PathFieldsIndexed;
    public int UniqueNormalizedPaths;
}
