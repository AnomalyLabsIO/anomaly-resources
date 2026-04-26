using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManifestAnalyzer;

internal sealed class ManifestAnalyzerApp
{
    private const string ManifestIdColumnName = "manifest_id";
    private const string GameVersionColumnName = "game_version";
    private const string VersionResolutionPathColumnName = "version_resolution_path";
    private const string ManifestSeenColumnName = "manifest_seen_utc";
    private const string PatchDateColumnName = "patch_date_utc";
    private const string PatchTimeColumnName = "patch_time_utc";
    private const string BuildIdColumnName = "build_id";

    public async Task<int> RunAsync(ManifestAnalyzerOptions options, CancellationToken cancellationToken)
    {
        var discord = DiscordNotifier.FromEnv(options.DiscordWebhookEnv);

        if (options.TrackerMode)
            return await RunTrackerAsync(options, discord, cancellationToken).ConfigureAwait(false);

        return await RunCsvAsync(options, cancellationToken).ConfigureAwait(false);
    }

    // ===================== Tracker (JSON) mode =====================

    private async Task<int> RunTrackerAsync(
        ManifestAnalyzerOptions options,
        DiscordNotifier? discord,
        CancellationToken cancellationToken)
    {
        var trackerPath = options.TrackerPath!;
        Console.WriteLine($"Tracker: {trackerPath}");

        var tracker = JsonTrackerDocument.LoadOrEmpty(trackerPath);
        Console.WriteLine($"Tracker entries loaded: {tracker.Entries.Count}");

        var initialSnapshot = SnapshotTracker(tracker);

        if (options.BackfillMode)
        {
            BackfillFromCsv(tracker, options.BackfillCsvPath!);
        }

        var newDiscoveries = new List<TrackerEntry>();
        var resolvedEntries = new List<TrackerEntry>();
        var failedEntries = new List<(TrackerEntry entry, string reason)>();

        var needsSteam = options.Discover || options.Resolve;
        if (needsSteam && !options.DryRun)
        {
            using var steamClient = new SteamManifestClient();
            try
            {
                await steamClient.LoginAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SteamSessionExpiredException ex)
            {
                Console.Error.WriteLine($"Steam authentication failed: {ex.Message}");
                if (discord != null)
                    await discord.NotifyAuthFailureAsync(ex.Message, cancellationToken).ConfigureAwait(false);
                return 78;
            }

            await steamClient.PrepareDownloadsAsync(cancellationToken).ConfigureAwait(false);

            if (options.Discover)
            {
                await DiscoverViaPicsAsync(steamClient, options, tracker, newDiscoveries, discord, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (options.Resolve)
            {
                await ResolveTrackerEntriesAsync(
                        steamClient,
                        options,
                        tracker,
                        trackerPath,
                        resolvedEntries,
                        failedEntries,
                        discord,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else if (needsSteam && options.DryRun)
        {
            Console.WriteLine("Dry run: skipping Steam login and resolution.");
        }

        var changed = !options.DryRun && tracker.Save(trackerPath, DateTimeOffset.UtcNow);
        if (changed)
            Console.WriteLine($"Wrote {trackerPath}");
        else
            Console.WriteLine("Tracker file unchanged.");

        if (changed && options.BumpManifestPath != null)
            BumpManifest(options.BumpManifestPath, trackerPath);

        EmitGitHubSummary(newDiscoveries, resolvedEntries, failedEntries);

        if (failedEntries.Count > 0)
        {
            Console.WriteLine($"{failedEntries.Count} manifest(s) failed to resolve. JSON entries kept with gameVersion=null.");
        }

        return 0;
    }

    private static void BackfillFromCsv(JsonTrackerDocument tracker, string csvPath)
    {
        Console.WriteLine($"Backfilling tracker from CSV: {csvPath}");
        var csv = CsvDocument.Load(csvPath);
        if (csv.Rows.Count <= 1)
        {
            Console.WriteLine("CSV has no data rows; nothing to backfill.");
            return;
        }

        var manifestIdIndex = csv.GetColumnIndex(ManifestIdColumnName);
        var gameVersionIndex = csv.TryGetColumnIndex(GameVersionColumnName, out var gvi) ? gvi : -1;
        var pathIndex = csv.TryGetColumnIndex(VersionResolutionPathColumnName, out var vrpi) ? vrpi : -1;
        var seenIndex = csv.TryGetColumnIndex(ManifestSeenColumnName, out var msi) ? msi : -1;
        var patchDateIndex = csv.TryGetColumnIndex(PatchDateColumnName, out var pdi) ? pdi : -1;
        var patchTimeIndex = csv.TryGetColumnIndex(PatchTimeColumnName, out var pti) ? pti : -1;
        var buildIdIndex = csv.TryGetColumnIndex(BuildIdColumnName, out var bii) ? bii : -1;

        var added = 0;
        var updated = 0;

        for (var rowIndex = 1; rowIndex < csv.Rows.Count; rowIndex++)
        {
            var row = csv.Rows[rowIndex];
            if (row.Fields.Count <= manifestIdIndex)
                continue;

            var manifestText = row.Fields[manifestIdIndex].Value.Trim();
            if (!ulong.TryParse(manifestText, out var manifestId) || manifestId == 0)
                continue;

            var existing = tracker.FindEntry(manifestId, "public");
            string? gameVersion = gameVersionIndex >= 0 && row.Fields.Count > gameVersionIndex
                ? Nullify(row.Fields[gameVersionIndex].Value)
                : null;
            string? resolutionPath = pathIndex >= 0 && row.Fields.Count > pathIndex
                ? Nullify(row.Fields[pathIndex].Value)
                : null;
            string? buildId = buildIdIndex >= 0 && row.Fields.Count > buildIdIndex
                ? Nullify(row.Fields[buildIdIndex].Value)
                : null;

            DateTimeOffset? date = null;
            if (seenIndex >= 0 && row.Fields.Count > seenIndex)
                date = TryParseSeenDate(row.Fields[seenIndex].Value);
            if (date == null && patchDateIndex >= 0 && patchTimeIndex >= 0
                && row.Fields.Count > patchDateIndex && row.Fields.Count > patchTimeIndex)
            {
                date = TryParsePatchDate(row.Fields[patchDateIndex].Value, row.Fields[patchTimeIndex].Value);
            }

            if (existing == null)
            {
                tracker.UpsertEntry(new TrackerEntry
                {
                    ManifestId = manifestId.ToString(CultureInfo.InvariantCulture),
                    BuildId = buildId,
                    Branch = "public",
                    Date = date,
                    GameVersion = gameVersion,
                    ResolutionPath = resolutionPath,
                    DiscoverySource = "csv-backfill",
                    Notes = null,
                });
                added++;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(existing.GameVersion) && gameVersion != null)
                {
                    existing.GameVersion = gameVersion;
                    existing.ResolutionPath = resolutionPath;
                    if (string.IsNullOrWhiteSpace(existing.DiscoverySource))
                        existing.DiscoverySource = "csv-backfill";
                    updated++;
                }
                if (existing.Date == null && date != null)
                {
                    existing.Date = date;
                }
                if (string.IsNullOrWhiteSpace(existing.BuildId) && buildId != null)
                {
                    existing.BuildId = buildId;
                }
            }
        }

        Console.WriteLine($"Backfill: {added} added, {updated} updated.");
    }

    private static string? Nullify(string value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static DateTimeOffset? TryParseSeenDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = value.Replace(" ", " ").Replace(" – ", " ").Replace("–", "-");
        if (DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto;

        var formats = new[]
        {
            "d MMMM yyyy - HH:mm:ss 'UTC'",
            "d MMMM yyyy HH:mm:ss 'UTC'",
            "d MMM yyyy - HH:mm:ss 'UTC'",
            "d MMM yyyy HH:mm:ss 'UTC'",
        };
        foreach (var fmt in formats)
        {
            if (DateTimeOffset.TryParseExact(cleaned, fmt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed;
        }

        return null;
    }

    private static DateTimeOffset? TryParsePatchDate(string datePart, string timePart)
    {
        if (string.IsNullOrWhiteSpace(datePart) || string.IsNullOrWhiteSpace(timePart))
            return null;

        var combined = $"{datePart.Trim()} {timePart.Trim()}";
        var formats = new[]
        {
            "dd-MMM-yy HH:mm",
            "d-MMM-yy HH:mm",
            "dd-MMM-yy H:mm",
            "d-MMM-yy H:mm",
        };
        foreach (var fmt in formats)
        {
            if (DateTimeOffset.TryParseExact(combined, fmt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed;
        }

        return null;
    }

    private static async Task DiscoverViaPicsAsync(
        SteamManifestClient steamClient,
        ManifestAnalyzerOptions options,
        JsonTrackerDocument tracker,
        List<TrackerEntry> newDiscoveries,
        DiscordNotifier? discord,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Discovering manifests via PICS for branches: {string.Join(", ", options.Branches)}");
        var picsEntries = await PicsManifestDiscovery
            .FetchAsync(steamClient, options.Branches, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"PICS returned {picsEntries.Count} branch entr{(picsEntries.Count == 1 ? "y" : "ies")}.");

        foreach (var pics in picsEntries)
        {
            if (tracker.ContainsManifest(pics.ManifestId, pics.Branch))
                continue;

            var entry = new TrackerEntry
            {
                ManifestId = pics.ManifestId.ToString(CultureInfo.InvariantCulture),
                BuildId = pics.BuildId,
                Branch = pics.Branch,
                Date = pics.ReleasedAt ?? DateTimeOffset.UtcNow,
                GameVersion = null,
                ResolutionPath = null,
                DiscoverySource = "pics",
                Notes = null,
            };

            tracker.UpsertEntry(entry);
            newDiscoveries.Add(entry);

            Console.WriteLine($"Discovered new manifest {entry.ManifestId} on {entry.Branch} (build {entry.BuildId ?? "?"}).");

            if (discord != null)
                await discord.NotifyDiscoveredAsync(entry.ManifestId, entry.Branch, entry.BuildId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ResolveTrackerEntriesAsync(
        SteamManifestClient steamClient,
        ManifestAnalyzerOptions options,
        JsonTrackerDocument tracker,
        string trackerPath,
        List<TrackerEntry> resolvedEntries,
        List<(TrackerEntry entry, string reason)> failedEntries,
        DiscordNotifier? discord,
        CancellationToken cancellationToken)
    {
        IEnumerable<TrackerEntry> source;
        if (options.ForceResolveManifest is ulong forceId)
        {
            var match = tracker.Entries
                .Where(e => ulong.TryParse(e.ManifestId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id == forceId)
                .ToList();
            if (match.Count == 0)
                throw new InvalidOperationException($"Manifest {forceId} not present in tracker; nothing to force-resolve.");
            source = match;
        }
        else if (options.Force)
        {
            source = tracker.Entries.ToList();
        }
        else
        {
            source = tracker.EntriesNeedingResolution().ToList();
        }

        var work = source
            .Where(e => ulong.TryParse(e.ManifestId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id != 0)
            .GroupBy(e => ulong.Parse(e.ManifestId, CultureInfo.InvariantCulture))
            .Select(g => new TrackerWorkItem(g.Key, g.ToList()))
            .ToList();

        if (options.Limit is int limit && work.Count > limit)
            work = work.Take(limit).ToList();

        if (work.Count == 0)
        {
            Console.WriteLine("No tracker entries need resolution.");
            return;
        }

        var workerCount = Math.Min(options.GetEffectiveParallelism(), work.Count);
        Console.WriteLine($"Resolving {work.Count} unique manifest{(work.Count == 1 ? "" : "s")} (parallelism: {workerCount}).");

        var tempRoot = Path.Combine(Path.GetTempPath(), "ManifestAnalyzer");
        var runRoot = Path.Combine(tempRoot, $"run-{DateTime.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        var saveLock = new SemaphoreSlim(1, 1);
        var startedCount = 0;

        try
        {
            await Parallel.ForEachAsync(
                work,
                new ParallelOptions { MaxDegreeOfParallelism = workerCount, CancellationToken = cancellationToken },
                async (item, ct) =>
                {
                    var startNumber = Interlocked.Increment(ref startedCount);
                    var prefix = $"[{startNumber}/{work.Count}] [manifest {item.ManifestId}]";
                    var workDir = Path.Combine(runRoot, item.ManifestId.ToString(CultureInfo.InvariantCulture));

                    Console.WriteLine($"{prefix} Starting");
                    DeleteDirectory(workDir);
                    Directory.CreateDirectory(workDir);

                    try
                    {
                        VersionResolutionResult? versionResolution = null;

                        if (options.GameManagerFirst)
                        {
                            var gameManagerPath = await steamClient
                                .DownloadGameManagerFileAsync(item.ManifestId, workDir, ct)
                                .ConfigureAwait(false);

                            if (gameManagerPath != null)
                            {
                                versionResolution = ManifestVersionReader.TryReadGameManagerVersion(gameManagerPath, prefix);
                            }
                        }

                        if (versionResolution == null)
                        {
                            var downloadedFiles = await steamClient
                                .DownloadRequiredFilesAsync(item.ManifestId, workDir, ct)
                                .ConfigureAwait(false);

                            versionResolution = ManifestVersionReader.ReadVersion(
                                downloadedFiles,
                                workDir,
                                prefix,
                                options.SignatureFirst,
                                options.UsesFullMetadataScan(),
                                options.GetEffectiveMinMetadataVersion(),
                                options.GetEffectiveMaxMetadataVersion());
                        }

                        await saveLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            foreach (var entry in item.Entries)
                            {
                                entry.GameVersion = versionResolution.Version;
                                entry.ResolutionPath = versionResolution.ResolutionPath;
                                resolvedEntries.Add(entry);
                            }
                            tracker.Save(trackerPath, DateTimeOffset.UtcNow);
                        }
                        finally
                        {
                            saveLock.Release();
                        }

                        Console.WriteLine($"{prefix} Resolved {versionResolution.Version} via {versionResolution.ResolutionPath}");

                        if (discord != null)
                        {
                            var sample = item.Entries.First();
                            await discord
                                .NotifySuccessAsync(versionResolution.Version, item.ManifestId.ToString(CultureInfo.InvariantCulture), sample.Branch, sample.BuildId, ct)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        var reason = ex.Message;
                        await saveLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            foreach (var entry in item.Entries)
                            {
                                entry.Notes = $"unresolved: {reason}";
                                if (entry.ResolutionPath == null)
                                    entry.ResolutionPath = "unresolved";
                                failedEntries.Add((entry, reason));
                            }
                            tracker.Save(trackerPath, DateTimeOffset.UtcNow);
                        }
                        finally
                        {
                            saveLock.Release();
                        }

                        Console.WriteLine($"{prefix} Failed: {reason}");

                        if (discord != null)
                        {
                            var sample = item.Entries.First();
                            await discord
                                .NotifyUnresolvedAsync(item.ManifestId.ToString(CultureInfo.InvariantCulture), sample.Branch, reason, ct)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        DeleteDirectory(workDir);
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            DeleteDirectory(runRoot);
            saveLock.Dispose();
        }
    }

    private static void BumpManifest(string manifestPath, string trackerPath)
    {
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine($"--bump-manifest target not found: {manifestPath}");
            return;
        }

        if (!File.Exists(trackerPath))
        {
            Console.WriteLine($"Cannot bump manifest: tracker file missing at {trackerPath}");
            return;
        }

        var manifestRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
                           ?? Path.GetFullPath(manifestPath);
        var relativeTrackerPath = Path.GetRelativePath(manifestRoot, trackerPath).Replace('\\', '/');

        var trackerBytes = File.ReadAllBytes(trackerPath);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(trackerBytes));
        var size = trackerBytes.LongLength;

        var existing = JsonNode.Parse(File.ReadAllText(manifestPath))
                       ?? throw new InvalidOperationException($"Failed to parse {manifestPath}.");
        var resources = existing["resources"] as JsonObject;
        if (resources == null)
        {
            resources = [];
            existing["resources"] = resources;
        }

        var entry = resources[relativeTrackerPath] as JsonObject ?? [];
        entry["sha256"] = sha256.ToUpperInvariant();
        entry["size"] = size;
        resources[relativeTrackerPath] = entry;

        var serialized = existing.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        if (!serialized.EndsWith('\n'))
            serialized += '\n';

        var current = File.ReadAllText(manifestPath);
        if (string.Equals(current, serialized, StringComparison.Ordinal))
            return;

        File.WriteAllText(manifestPath, serialized);
        Console.WriteLine($"Bumped {manifestPath} entry for {relativeTrackerPath}: sha256={sha256.ToUpperInvariant()} size={size}");
    }

    private static string SnapshotTracker(JsonTrackerDocument tracker)
    {
        var copy = tracker.Entries
            .Select(e => $"{e.Branch}|{e.ManifestId}|{e.GameVersion ?? string.Empty}")
            .OrderBy(s => s, StringComparer.Ordinal);
        return string.Join("\n", copy);
    }

    private static void EmitGitHubSummary(
        IReadOnlyList<TrackerEntry> newDiscoveries,
        IReadOnlyList<TrackerEntry> resolved,
        IReadOnlyList<(TrackerEntry entry, string reason)> failed)
    {
        var summary = BuildSummaryString(newDiscoveries, resolved, failed);
        Console.WriteLine($"Summary: {summary}");

        var outputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (string.IsNullOrEmpty(outputPath))
            return;

        try
        {
            File.AppendAllText(outputPath, $"summary={summary}\n");
            File.AppendAllText(outputPath, $"discovered={newDiscoveries.Count}\n");
            File.AppendAllText(outputPath, $"resolved={resolved.Count}\n");
            File.AppendAllText(outputPath, $"failed={failed.Count}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to write to GITHUB_OUTPUT: {ex.Message}");
        }
    }

    private static string BuildSummaryString(
        IReadOnlyList<TrackerEntry> newDiscoveries,
        IReadOnlyList<TrackerEntry> resolved,
        IReadOnlyList<(TrackerEntry entry, string reason)> failed)
    {
        var versionPart = resolved.Count > 0
            ? string.Join(", ", resolved.Select(e => $"{e.ManifestId.AsSpan(0, Math.Min(8, e.ManifestId.Length)).ToString()}.. -> {e.GameVersion} ({e.Branch})").Take(3))
            : "(no new versions)";

        return $"discovered={newDiscoveries.Count} resolved={resolved.Count} failed={failed.Count} {versionPart}";
    }

    // ===================== Existing CSV mode (unchanged behavior) =====================

    private async Task<int> RunCsvAsync(ManifestAnalyzerOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine($"CSV: {options.CsvPath}");

        var csv = CsvDocument.Load(options.CsvPath);
        if (csv.Rows.Count == 0)
            throw new InvalidOperationException("CSV is empty.");

        var manifestIdIndex = csv.GetColumnIndex(ManifestIdColumnName);
        var gameVersionIndex = csv.GetColumnIndex(GameVersionColumnName);
        var hadVersionResolutionPathColumn = csv.TryGetColumnIndex(VersionResolutionPathColumnName, out var versionResolutionPathIndex);
        versionResolutionPathIndex = csv.GetOrAddColumn(VersionResolutionPathColumnName);

        var headerColumnCount = csv.Rows[0].Fields.Count;
        ValidateColumnCounts(csv, headerColumnCount);

        var plan = BuildPlan(csv, manifestIdIndex, gameVersionIndex, options);

        Console.WriteLine($"Rows: {csv.Rows.Count - 1}");
        Console.WriteLine($"Pending rows: {plan.PendingRowCount}");
        Console.WriteLine($"Unique manifest ids to resolve: {plan.WorkItems.Count}");

        var workerCount = Math.Min(options.GetEffectiveParallelism(), Math.Max(plan.WorkItems.Count, 1));
        Console.WriteLine($"Parallelism: {workerCount}");
        if (options.UsesFullMetadataScan())
        {
            Console.WriteLine($"Metadata strategy: full scan {options.GetEffectiveMaxMetadataVersion()} down to {options.GetEffectiveMinMetadataVersion()}");
        }
        else
        {
            Console.WriteLine("Metadata strategy: known versions only (31, 29, 24)");
        }

        Console.WriteLine($"Game manager first: {(options.GameManagerFirst ? "enabled" : "disabled")}");

        if (plan.InvalidRows.Count > 0)
        {
            Console.WriteLine($"Rows skipped due to invalid manifest ids: {plan.InvalidRows.Count} ({string.Join(", ", plan.InvalidRows.Take(10))}{(plan.InvalidRows.Count > 10 ? ", ..." : string.Empty)})");
        }

        if (options.Limit is int limit)
            Console.WriteLine($"Applying limit: first {Math.Min(limit, plan.WorkItems.Count)} manifest groups.");

        if (plan.WorkItems.Count == 0)
        {
            Console.WriteLine("Nothing to do.");
            return 0;
        }

        if (options.DryRun)
        {
            Console.WriteLine("Dry run complete. No Steam login or CSV updates were performed.");
            return 0;
        }

        if (!hadVersionResolutionPathColumn)
            csv.Save(options.CsvPath);

        var tempRoot = Path.Combine(Path.GetTempPath(), "ManifestAnalyzer");
        var runRoot = Path.Combine(tempRoot, $"run-{DateTime.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        var resolvedCount = 0;
        var failedCount = 0;
        var startedCount = 0;
        var csvSaveLock = new SemaphoreSlim(1, 1);

        using var steamClient = new SteamManifestClient();
        await steamClient.LoginAsync(cancellationToken).ConfigureAwait(false);

        await steamClient.PrepareDownloadsAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Parallel.ForEachAsync(
                plan.WorkItems,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = workerCount,
                    CancellationToken = cancellationToken,
                },
                async (workItem, itemCancellationToken) =>
                {
                    var startNumber = Interlocked.Increment(ref startedCount);
                    var prefix = $"[{startNumber}/{plan.WorkItems.Count}] [manifest {workItem.ManifestId}]";
                    var workDir = Path.Combine(runRoot, workItem.ManifestId.ToString());

                    Console.WriteLine();
                    Console.WriteLine($"{prefix} Starting");

                    DeleteDirectory(workDir);
                    Directory.CreateDirectory(workDir);

                    try
                    {
                        VersionResolutionResult? versionResolution = null;

                        if (options.GameManagerFirst)
                        {
                            var gameManagerPath = await steamClient
                                .DownloadGameManagerFileAsync(workItem.ManifestId, workDir, itemCancellationToken)
                                .ConfigureAwait(false);

                            if (gameManagerPath != null)
                            {
                                versionResolution = ManifestVersionReader.TryReadGameManagerVersion(gameManagerPath, prefix);
                            }
                            else
                            {
                                Console.WriteLine($"{prefix} globalgamemanagers not present in this manifest. Falling back to IL2CPP probing.");
                            }
                        }

                        if (versionResolution == null)
                        {
                        var downloadedFiles = await steamClient
                            .DownloadRequiredFilesAsync(workItem.ManifestId, workDir, itemCancellationToken)
                            .ConfigureAwait(false);

                            versionResolution = ManifestVersionReader.ReadVersion(
                                downloadedFiles,
                                workDir,
                                prefix,
                                options.SignatureFirst,
                                options.UsesFullMetadataScan(),
                                options.GetEffectiveMinMetadataVersion(),
                                options.GetEffectiveMaxMetadataVersion());
                        }

                        await csvSaveLock.WaitAsync(itemCancellationToken).ConfigureAwait(false);
                        try
                        {
                            foreach (var rowIndex in workItem.RowIndices)
                            {
                                csv.Rows[rowIndex].Fields[gameVersionIndex].Value = versionResolution.Version;
                                csv.Rows[rowIndex].Fields[versionResolutionPathIndex].Value = versionResolution.ResolutionPath;
                            }

                            csv.Save(options.CsvPath);
                        }
                        finally
                        {
                            csvSaveLock.Release();
                        }

                        Interlocked.Increment(ref resolvedCount);
                        Console.WriteLine($"{prefix} Resolved game version: {versionResolution.Version} via {versionResolution.ResolutionPath}");
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failedCount);
                        Console.WriteLine($"{prefix} Failed: {ex.Message}");
                    }
                    finally
                    {
                        DeleteDirectory(workDir);
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            DeleteDirectory(runRoot);
            csvSaveLock.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine($"Finished. Resolved {resolvedCount} manifest groups; {failedCount} failed.");
        Console.WriteLine("CSV changes are written after each successful manifest resolution.");

        return failedCount == 0 ? 0 : 1;
    }

    private static ManifestPlan BuildPlan(
        CsvDocument csv,
        int manifestIdIndex,
        int gameVersionIndex,
        ManifestAnalyzerOptions options)
    {
        var allWorkItems = new List<ManifestWorkItem>();
        var workItemsByManifest = new Dictionary<ulong, ManifestWorkItem>();
        var invalidRows = new List<int>();
        var pendingRowCount = 0;

        for (var rowIndex = 1; rowIndex < csv.Rows.Count; rowIndex++)
        {
            var row = csv.Rows[rowIndex];
            var gameVersion = row.Fields[gameVersionIndex].Value;
            if (!options.Force && !string.IsNullOrWhiteSpace(gameVersion))
                continue;

            pendingRowCount++;

            var manifestText = row.Fields[manifestIdIndex].Value.Trim();
            if (!ulong.TryParse(manifestText, out var manifestId) || manifestId == 0)
            {
                invalidRows.Add(rowIndex + 1);
                continue;
            }

            if (!workItemsByManifest.TryGetValue(manifestId, out var workItem))
            {
                workItem = new ManifestWorkItem(manifestId);
                workItemsByManifest.Add(manifestId, workItem);
                allWorkItems.Add(workItem);
            }

            workItem.RowIndices.Add(rowIndex);
        }

        if (options.Limit is int limit && allWorkItems.Count > limit)
            allWorkItems = allWorkItems.Take(limit).ToList();

        return new ManifestPlan(allWorkItems, invalidRows, pendingRowCount);
    }

    private static void ValidateColumnCounts(CsvDocument csv, int expectedColumnCount)
    {
        for (var rowIndex = 1; rowIndex < csv.Rows.Count; rowIndex++)
        {
            var row = csv.Rows[rowIndex];
            if (row.Fields.Count != expectedColumnCount)
            {
                throw new InvalidOperationException(
                    $"Row {rowIndex + 1} has {row.Fields.Count} columns; expected {expectedColumnCount}.");
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}

internal sealed record TrackerWorkItem(ulong ManifestId, IReadOnlyList<TrackerEntry> Entries);

internal sealed class ManifestWorkItem(ulong manifestId)
{
    public ulong ManifestId { get; } = manifestId;

    public List<int> RowIndices { get; } = [];
}

internal sealed record ManifestPlan(
    IReadOnlyList<ManifestWorkItem> WorkItems,
    IReadOnlyList<int> InvalidRows,
    int PendingRowCount);
