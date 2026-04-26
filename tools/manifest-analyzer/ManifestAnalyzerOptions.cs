namespace ManifestAnalyzer;

internal sealed record ManifestAnalyzerOptions(
    string CsvPath,
    int? Limit,
    int? Parallelism,
    bool SignatureFirst,
    bool GameManagerFirst,
    int? MinMetadataVersion,
    int? MaxMetadataVersion,
    bool Force,
    bool DryRun,
    string? LocalDirectory,
    string? TrackerPath,
    bool Discover,
    bool Resolve,
    bool ResolveExplicit,
    IReadOnlyList<string> Branches,
    string? BackfillCsvPath,
    string? UnlicensePath,
    string? BumpManifestPath,
    string? DiscordWebhookEnv,
    ulong? ForceResolveManifest,
    bool ShowHelp)
{
    private const string DefaultCsvFileName = "steamdb_patch_manifest_map.csv";
    private const int DefaultMinMetadataVersion = 16;
    private const int DefaultMaxMetadataVersion = 31;

    public bool TrackerMode => !string.IsNullOrEmpty(TrackerPath);

    public bool BackfillMode => !string.IsNullOrEmpty(BackfillCsvPath);

    public static ManifestAnalyzerOptions Parse(string[] args)
    {
        var csvPath = Path.Combine(Environment.CurrentDirectory, DefaultCsvFileName);
        int? limit = null;
        int? parallelism = null;
        var signatureFirst = false;
        var gameManagerFirst = false;
        int? minMetadataVersion = null;
        int? maxMetadataVersion = null;
        var force = false;
        var dryRun = false;
        string? localDirectory = null;
        string? trackerPath = null;
        var discover = false;
        var resolve = false;
        var resolveExplicit = false;
        List<string> branches = ["public"];
        string? backfillCsvPath = null;
        string? unlicensePath = null;
        string? bumpManifestPath = null;
        string? discordWebhookEnv = null;
        ulong? forceResolveManifest = null;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--csv":
                    i = RequireValue(args, i, "--csv");
                    csvPath = args[i];
                    break;

                case "--limit":
                    i = RequireValue(args, i, "--limit");
                    if (!int.TryParse(args[i], out var parsedLimit) || parsedLimit <= 0)
                        throw new ArgumentException("--limit must be a positive integer.");
                    limit = parsedLimit;
                    break;

                case "--parallelism":
                    i = RequireValue(args, i, "--parallelism");
                    if (!int.TryParse(args[i], out var parsedParallelism) || parsedParallelism <= 0)
                        throw new ArgumentException("--parallelism must be a positive integer.");
                    parallelism = parsedParallelism;
                    break;

                case "--signature-first":
                    signatureFirst = true;
                    break;

                case "--gamemanager-first":
                    gameManagerFirst = true;
                    break;

                case "--min-metadata-version":
                    i = RequireValue(args, i, "--min-metadata-version");
                    if (!int.TryParse(args[i], out var parsedMinMetadataVersion) || parsedMinMetadataVersion <= 0)
                        throw new ArgumentException("--min-metadata-version must be a positive integer.");
                    minMetadataVersion = parsedMinMetadataVersion;
                    break;

                case "--max-metadata-version":
                    i = RequireValue(args, i, "--max-metadata-version");
                    if (!int.TryParse(args[i], out var parsedMaxMetadataVersion) || parsedMaxMetadataVersion <= 0)
                        throw new ArgumentException("--max-metadata-version must be a positive integer.");
                    maxMetadataVersion = parsedMaxMetadataVersion;
                    break;

                case "--force":
                    force = true;
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                case "--local":
                    i = RequireValue(args, i, "--local");
                    localDirectory = args[i];
                    break;

                case "--tracker":
                    i = RequireValue(args, i, "--tracker");
                    trackerPath = args[i];
                    break;

                case "--discover":
                    discover = true;
                    break;

                case "--resolve":
                    resolve = true;
                    resolveExplicit = true;
                    break;

                case "--branches":
                    i = RequireValue(args, i, "--branches");
                    branches = args[i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(b => b.ToLowerInvariant())
                        .Distinct()
                        .ToList();
                    if (branches.Count == 0)
                        branches = ["public"];
                    break;

                case "--backfill-from-csv":
                    i = RequireValue(args, i, "--backfill-from-csv");
                    backfillCsvPath = args[i];
                    break;

                case "--unlicense":
                    i = RequireValue(args, i, "--unlicense");
                    unlicensePath = args[i];
                    break;

                case "--bump-manifest":
                    i = RequireValue(args, i, "--bump-manifest");
                    bumpManifestPath = args[i];
                    break;

                case "--discord-webhook-env":
                    i = RequireValue(args, i, "--discord-webhook-env");
                    discordWebhookEnv = args[i];
                    break;

                case "--force-resolve-manifest":
                    i = RequireValue(args, i, "--force-resolve-manifest");
                    if (!ulong.TryParse(args[i], out var parsedForceManifest) || parsedForceManifest == 0)
                        throw new ArgumentException("--force-resolve-manifest must be a positive uint64 manifest id.");
                    forceResolveManifest = parsedForceManifest;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        var effectiveMinMetadataVersion = minMetadataVersion ?? DefaultMinMetadataVersion;
        var effectiveMaxMetadataVersion = maxMetadataVersion ?? DefaultMaxMetadataVersion;
        if (effectiveMinMetadataVersion > effectiveMaxMetadataVersion)
        {
            throw new ArgumentException(
                $"--min-metadata-version ({effectiveMinMetadataVersion}) cannot be greater than --max-metadata-version ({effectiveMaxMetadataVersion}).");
        }

        if (trackerPath != null && !resolveExplicit && !discover && backfillCsvPath == null && forceResolveManifest == null)
        {
            // Default: tracker mode without other modifiers means "discover + resolve".
            discover = true;
            resolve = true;
        }
        else if (trackerPath != null && !resolveExplicit && (discover || forceResolveManifest != null))
        {
            // Discover or force-resolve implies resolve unless explicitly suppressed.
            resolve = true;
        }

        if (unlicensePath != null)
        {
            Environment.SetEnvironmentVariable("ANOMALY_UNLICENSE_PATH", Path.GetFullPath(unlicensePath));
        }

        return new ManifestAnalyzerOptions(
            CsvPath: Path.GetFullPath(csvPath),
            Limit: limit,
            Parallelism: parallelism,
            SignatureFirst: signatureFirst,
            GameManagerFirst: gameManagerFirst,
            MinMetadataVersion: minMetadataVersion,
            MaxMetadataVersion: maxMetadataVersion,
            Force: force,
            DryRun: dryRun,
            LocalDirectory: localDirectory == null ? null : Path.GetFullPath(localDirectory),
            TrackerPath: trackerPath == null ? null : Path.GetFullPath(trackerPath),
            Discover: discover,
            Resolve: resolve,
            ResolveExplicit: resolveExplicit,
            Branches: branches,
            BackfillCsvPath: backfillCsvPath == null ? null : Path.GetFullPath(backfillCsvPath),
            UnlicensePath: unlicensePath == null ? null : Path.GetFullPath(unlicensePath),
            BumpManifestPath: bumpManifestPath == null ? null : Path.GetFullPath(bumpManifestPath),
            DiscordWebhookEnv: discordWebhookEnv,
            ForceResolveManifest: forceResolveManifest,
            ShowHelp: showHelp);
    }

    public int GetEffectiveParallelism()
    {
        return Parallelism ?? Math.Clamp(Environment.ProcessorCount, 1, 4);
    }

    public int GetEffectiveMinMetadataVersion()
    {
        return MinMetadataVersion ?? DefaultMinMetadataVersion;
    }

    public int GetEffectiveMaxMetadataVersion()
    {
        return MaxMetadataVersion ?? DefaultMaxMetadataVersion;
    }

    public bool UsesFullMetadataScan()
    {
        return MinMetadataVersion.HasValue || MaxMetadataVersion.HasValue;
    }

    private static int RequireValue(string[] args, int index, string optionName)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{optionName} requires a value.");

        return index + 1;
    }
}
