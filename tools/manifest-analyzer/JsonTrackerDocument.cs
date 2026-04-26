using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManifestAnalyzer;

internal sealed class JsonTrackerDocument
{
    private const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public uint AppId { get; set; } = SteamManifestClient.ScpSlAppId;
    public uint DepotId { get; set; } = SteamManifestClient.ScpSlDepotId;
    public DateTimeOffset GeneratedAt { get; set; }
    public List<TrackerEntry> Entries { get; set; } = [];

    public static JsonTrackerDocument LoadOrEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonTrackerDocument
            {
                GeneratedAt = DateTimeOffset.UtcNow,
            };
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonTrackerDocument
            {
                GeneratedAt = DateTimeOffset.UtcNow,
            };
        }

        var document = JsonSerializer.Deserialize<JsonTrackerDocument>(json, ReadOptions)
            ?? throw new InvalidOperationException($"Tracker file {path} is not valid JSON.");

        document.Entries ??= [];
        return document;
    }

    public bool ContainsManifest(ulong manifestId, string branch)
    {
        var manifestStr = manifestId.ToString(CultureInfo.InvariantCulture);
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.ManifestId, manifestStr, StringComparison.Ordinal)
                && string.Equals(entry.Branch, branch, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public TrackerEntry? FindEntry(ulong manifestId, string? branch = null)
    {
        var manifestStr = manifestId.ToString(CultureInfo.InvariantCulture);
        foreach (var entry in Entries)
        {
            if (!string.Equals(entry.ManifestId, manifestStr, StringComparison.Ordinal))
                continue;

            if (branch == null || string.Equals(entry.Branch, branch, StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        return null;
    }

    public IEnumerable<TrackerEntry> EntriesNeedingResolution()
    {
        foreach (var entry in Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.GameVersion))
                yield return entry;
        }
    }

    public void UpsertEntry(TrackerEntry entry)
    {
        var existing = FindEntry(ulong.Parse(entry.ManifestId, CultureInfo.InvariantCulture), entry.Branch);
        if (existing != null)
        {
            existing.BuildId = entry.BuildId ?? existing.BuildId;
            existing.Branch = entry.Branch;
            existing.Date = entry.Date ?? existing.Date;
            if (!string.IsNullOrWhiteSpace(entry.GameVersion))
                existing.GameVersion = entry.GameVersion;
            if (!string.IsNullOrWhiteSpace(entry.ResolutionPath))
                existing.ResolutionPath = entry.ResolutionPath;
            if (!string.IsNullOrWhiteSpace(entry.DiscoverySource))
                existing.DiscoverySource = entry.DiscoverySource;
            if (entry.Notes != null)
                existing.Notes = entry.Notes;
            return;
        }

        Entries.Add(entry);
    }

    public void Sort()
    {
        Entries.Sort((a, b) =>
        {
            var dateA = a.Date ?? DateTimeOffset.MinValue;
            var dateB = b.Date ?? DateTimeOffset.MinValue;
            var dateCompare = dateB.CompareTo(dateA);
            if (dateCompare != 0)
                return dateCompare;

            var manifestA = ulong.TryParse(a.ManifestId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ma) ? ma : 0UL;
            var manifestB = ulong.TryParse(b.ManifestId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? mb : 0UL;
            return manifestB.CompareTo(manifestA);
        });
    }

    public bool Save(string path, DateTimeOffset generatedAt)
    {
        Sort();
        SchemaVersion = CurrentSchemaVersion;

        var stableGeneratedAt = generatedAt;
        GeneratedAt = stableGeneratedAt;

        // Render twice: once with the candidate timestamp, once with the existing file's timestamp.
        // If everything else matches, keep the existing timestamp so the file is byte-identical.
        var candidate = JsonSerializer.Serialize(this, WriteOptions);
        if (!candidate.EndsWith('\n'))
            candidate += '\n';

        if (File.Exists(path))
        {
            var current = File.ReadAllText(path);
            if (TryStripGeneratedAt(current, out var existingWithoutTs)
                && TryStripGeneratedAt(candidate, out var candidateWithoutTs)
                && string.Equals(existingWithoutTs, candidateWithoutTs, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, candidate, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return true;
    }

    private static bool TryStripGeneratedAt(string json, out string stripped)
    {
        const string Marker = "\"generatedAt\":";
        var index = json.IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0)
        {
            stripped = json;
            return true;
        }

        var lineEnd = json.IndexOf('\n', index);
        if (lineEnd < 0)
            lineEnd = json.Length;

        stripped = json.Remove(index, lineEnd - index);
        return true;
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new IsoUtcDateTimeOffsetConverter(),
            new IsoUtcNullableDateTimeOffsetConverter(),
        },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new IsoUtcDateTimeOffsetConverter(),
            new IsoUtcNullableDateTimeOffsetConverter(),
        },
    };
}

internal sealed class IsoUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return DateTimeOffset.Parse(raw!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        var utc = value.ToUniversalTime();
        writer.WriteStringValue(utc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
    }
}

internal sealed class IsoUtcNullableDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        var raw = reader.GetString();
        if (string.IsNullOrEmpty(raw))
            return null;

        return DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        var utc = value.Value.ToUniversalTime();
        writer.WriteStringValue(utc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
    }
}

internal sealed class TrackerEntry
{
    [JsonPropertyOrder(1)]
    public string ManifestId { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string? BuildId { get; set; }

    [JsonPropertyOrder(3)]
    public string Branch { get; set; } = "public";

    [JsonPropertyOrder(4)]
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyOrder(5)]
    public string? GameVersion { get; set; }

    [JsonPropertyOrder(6)]
    public string? ResolutionPath { get; set; }

    [JsonPropertyOrder(7)]
    public string? DiscoverySource { get; set; }

    [JsonPropertyOrder(8)]
    public string? Notes { get; set; }
}
