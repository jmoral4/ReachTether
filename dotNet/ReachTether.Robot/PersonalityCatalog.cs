using System.Text.Json;
using Microsoft.Extensions.Logging;

internal sealed record PersonalityDefinition(
    string Id,
    string DisplayName,
    string Instructions,
    IReadOnlyList<string> SwitchPhrases);

internal interface IPersonalityCatalog
{
    PersonalityDefinition DefaultPersonality { get; }
    IReadOnlyList<PersonalityDefinition> All { get; }
    bool TryResolveSwitchCommand(string input, out PersonalityDefinition personality);
}

internal sealed class PersonalityCatalog : IPersonalityCatalog
{
    private readonly Dictionary<string, PersonalityDefinition> byId;
    private readonly Dictionary<string, PersonalityDefinition> byNormalizedName;

    private PersonalityCatalog(
        PersonalityDefinition defaultPersonality,
        List<PersonalityDefinition> all,
        Dictionary<string, PersonalityDefinition> byId,
        Dictionary<string, PersonalityDefinition> byNormalizedName)
    {
        DefaultPersonality = defaultPersonality;
        All = all;
        this.byId = byId;
        this.byNormalizedName = byNormalizedName;
    }

    public PersonalityDefinition DefaultPersonality { get; }
    public IReadOnlyList<PersonalityDefinition> All { get; }

    public static PersonalityCatalog Load(string configuredPath, string defaultId, ILogger<PersonalityCatalog> logger)
    {
        var resolvedPath = ResolveCatalogPath(configuredPath);
        if (resolvedPath is null)
        {
            throw new FileNotFoundException(
                $"Could not find personality catalog '{configuredPath}'. Checked current directory and app base directory.");
        }

        var json = File.ReadAllText(resolvedPath);
        var root = JsonSerializer.Deserialize<PersonalityCatalogFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (root?.Personalities is null || root.Personalities.Count == 0)
        {
            throw new InvalidOperationException($"Personality catalog '{resolvedPath}' does not contain any personalities.");
        }

        var all = new List<PersonalityDefinition>(root.Personalities.Count);
        var byId = new Dictionary<string, PersonalityDefinition>(StringComparer.OrdinalIgnoreCase);
        var byNormalizedName = new Dictionary<string, PersonalityDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in root.Personalities)
        {
            var id = candidate.Id?.Trim() ?? string.Empty;
            var displayName = candidate.DisplayName?.Trim() ?? string.Empty;
            var instructions = candidate.Instructions?.Trim() ?? string.Empty;

            if (id.Length == 0)
            {
                throw new InvalidOperationException($"A personality in '{resolvedPath}' is missing 'id'.");
            }

            if (displayName.Length == 0)
            {
                throw new InvalidOperationException($"Personality '{id}' in '{resolvedPath}' is missing 'displayName'.");
            }

            if (instructions.Length == 0)
            {
                throw new InvalidOperationException($"Personality '{id}' in '{resolvedPath}' has empty 'instructions'.");
            }

            if (byId.ContainsKey(id))
            {
                throw new InvalidOperationException($"Duplicate personality id '{id}' in '{resolvedPath}'.");
            }

            var switchPhrases = (candidate.SwitchPhrases ?? [])
                .Select(s => s?.Trim() ?? string.Empty)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var personality = new PersonalityDefinition(id, displayName, instructions, switchPhrases);
            all.Add(personality);
            byId[id] = personality;

            AddLookup(byNormalizedName, id, personality);
            AddLookup(byNormalizedName, displayName, personality);
            foreach (var phrase in switchPhrases)
            {
                AddLookup(byNormalizedName, phrase, personality);
            }
        }

        var normalizedDefaultId = defaultId.Trim();
        if (!TryResolveConfiguredDefault(normalizedDefaultId, byId, byNormalizedName, out var defaultPersonality))
        {
            defaultPersonality = all[0];
            logger.LogWarning(
                "Configured default personality '{ConfiguredDefault}' not found in '{CatalogPath}'. Falling back to '{FallbackId}'. Available ids: {AvailableIds}.",
                normalizedDefaultId,
                resolvedPath,
                defaultPersonality.Id,
                string.Join(", ", all.Select(p => p.Id)));
        }

        logger.LogInformation(
            "Loaded {Count} personalities from '{CatalogPath}'. Default='{DefaultId}'.",
            all.Count,
            resolvedPath,
            defaultPersonality.Id);

        return new PersonalityCatalog(defaultPersonality, all, byId, byNormalizedName);
    }

    public bool TryResolveSwitchCommand(string input, out PersonalityDefinition personality)
    {
        personality = DefaultPersonality;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var normalized = NormalizeLookupKey(input);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (byNormalizedName.TryGetValue(normalized, out var matchedByName) && matchedByName is not null)
        {
            personality = matchedByName;
            return true;
        }

        const string personalityPrefix = "personality ";
        const string switchPrefix = "switch to ";
        if (normalized.StartsWith(personalityPrefix, StringComparison.Ordinal))
        {
            return TryResolveByToken(normalized[personalityPrefix.Length..], out personality);
        }

        if (normalized.StartsWith(switchPrefix, StringComparison.Ordinal))
        {
            return TryResolveByToken(normalized[switchPrefix.Length..], out personality);
        }

        return false;
    }

    private bool TryResolveByToken(string rawToken, out PersonalityDefinition personality)
    {
        personality = DefaultPersonality;
        var token = NormalizeLookupKey(rawToken);
        if (token.Length == 0)
        {
            return false;
        }

        if (byNormalizedName.TryGetValue(token, out var matchedByToken) && matchedByToken is not null)
        {
            personality = matchedByToken;
            return true;
        }

        if (byId.TryGetValue(token, out var matchedById) && matchedById is not null)
        {
            personality = matchedById;
            return true;
        }

        return false;
    }

    private static string? ResolveCatalogPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        var currentDirectoryPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
        if (File.Exists(currentDirectoryPath))
        {
            return currentDirectoryPath;
        }

        var appBasePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
        if (File.Exists(appBasePath))
        {
            return appBasePath;
        }

        return null;
    }

    private static void AddLookup(
        IDictionary<string, PersonalityDefinition> lookup,
        string rawKey,
        PersonalityDefinition personality)
    {
        var key = NormalizeLookupKey(rawKey);
        if (key.Length == 0 || lookup.ContainsKey(key))
        {
            return;
        }

        lookup[key] = personality;
    }

    private static bool TryResolveConfiguredDefault(
        string configuredValue,
        IReadOnlyDictionary<string, PersonalityDefinition> byId,
        IReadOnlyDictionary<string, PersonalityDefinition> byNormalizedName,
        out PersonalityDefinition personality)
    {
        if (byId.TryGetValue(configuredValue, out var byExactId) && byExactId is not null)
        {
            personality = byExactId;
            return true;
        }

        var normalized = NormalizeLookupKey(configuredValue);
        if (normalized.Length > 0
            && byNormalizedName.TryGetValue(normalized, out var byAlias)
            && byAlias is not null)
        {
            personality = byAlias;
            return true;
        }

        if (normalized.Length > 0)
        {
            var prefixMatches = byId
                .Select(kvp => new { Personality = kvp.Value, NormalizedId = NormalizeLookupKey(kvp.Key) })
                .Where(x => x.NormalizedId.StartsWith(normalized, StringComparison.Ordinal))
                .Select(x => x.Personality)
                .Distinct()
                .ToList();

            if (prefixMatches.Count == 1)
            {
                personality = prefixMatches[0];
                return true;
            }
        }

        personality = default!;
        return false;
    }

    private static string NormalizeLookupKey(string value)
    {
        var lowered = value.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        return string.Join(' ', lowered.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class PersonalityCatalogFile
    {
        public List<PersonalityDefinitionFile>? Personalities { get; init; }
    }

    private sealed class PersonalityDefinitionFile
    {
        public string? Id { get; init; }
        public string? DisplayName { get; init; }
        public string? Instructions { get; init; }
        public string[]? SwitchPhrases { get; init; }
    }
}
