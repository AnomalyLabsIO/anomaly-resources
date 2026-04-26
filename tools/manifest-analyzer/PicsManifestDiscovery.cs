using SteamKit2;

namespace ManifestAnalyzer;

internal sealed record PicsManifestEntry(
    string Branch,
    ulong ManifestId,
    string? BuildId,
    DateTimeOffset? ReleasedAt);

internal static class PicsManifestDiscovery
{
    public static async Task<IReadOnlyList<PicsManifestEntry>> FetchAsync(
        SteamManifestClient client,
        IReadOnlyCollection<string> branchFilter,
        CancellationToken cancellationToken)
    {
        if (!client.IsLoggedOn || client.RawClient == null)
            throw new InvalidOperationException("Not logged in to Steam.");

        var steamApps = client.RawClient.GetHandler<SteamApps>()
            ?? throw new InvalidOperationException("SteamApps handler unavailable.");

        var appRequest = new SteamApps.PICSRequest(SteamManifestClient.ScpSlAppId);
        var resultSet = await steamApps
            .PICSGetProductInfo(new[] { appRequest }, Array.Empty<SteamApps.PICSRequest>())
            .ToTask()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (resultSet?.Results == null)
            return Array.Empty<PicsManifestEntry>();

        var allowedBranches = branchFilter.Count == 0
            ? null
            : new HashSet<string>(branchFilter, StringComparer.OrdinalIgnoreCase);

        var entries = new List<PicsManifestEntry>();
        foreach (var callback in resultSet.Results)
        {
            if (!callback.Apps.TryGetValue(SteamManifestClient.ScpSlAppId, out var appInfo))
                continue;

            var depots = appInfo.KeyValues["depots"];
            var branches = depots["branches"];
            var manifestsKv = depots[SteamManifestClient.ScpSlDepotId.ToString()]["manifests"];

            foreach (var branch in manifestsKv.Children)
            {
                var branchName = branch.Name ?? "public";
                if (allowedBranches != null && !allowedBranches.Contains(branchName))
                    continue;

                var gidStr = branch["gid"].AsString();
                if (!ulong.TryParse(gidStr, out var manifestId) || manifestId == 0)
                    continue;

                DateTimeOffset? releasedAt = null;
                var timeStr = branch["timeupdated"].AsString();
                if (long.TryParse(timeStr, out var unixSec) && unixSec > 0)
                    releasedAt = DateTimeOffset.FromUnixTimeSeconds(unixSec);

                var buildId = branches[branchName]["buildid"].AsString();

                entries.Add(new PicsManifestEntry(
                    Branch: branchName,
                    ManifestId: manifestId,
                    BuildId: string.IsNullOrEmpty(buildId) ? null : buildId,
                    ReleasedAt: releasedAt));
            }
        }

        return entries;
    }
}
