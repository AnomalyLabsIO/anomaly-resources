using System.Text;

namespace ManifestAnalyzer;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        ManifestAnalyzerOptions options;
        try
        {
            options = ManifestAnalyzerOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintHelp();
            return 1;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler? cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (!cts.IsCancellationRequested)
            {
                Console.WriteLine("Cancellation requested...");
                cts.Cancel();
            }
        };

        Console.CancelKeyPress += cancelHandler;

        try
        {
            if (options.LocalDirectory != null)
            {
                return LocalAnalyzer.Run(options);
            }

            var app = new ManifestAnalyzerApp();
            return await app.RunAsync(options, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
        catch (SteamSessionExpiredException ex)
        {
            Console.Error.WriteLine($"Steam authentication failed: {ex.Message}");
            return 78;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("ManifestAnalyzer");
        Console.WriteLine();
        Console.WriteLine("CSV mode (default): updates a SteamDB manifest map CSV with resolved game versions.");
        Console.WriteLine("  dotnet run -- [--csv <path>] [--limit <n>] [--parallelism <n>] [--force] [--dry-run]");
        Console.WriteLine();
        Console.WriteLine("Tracker (JSON) mode: maintains anomaly-resources/builds/scpsl_builds.json via PICS.");
        Console.WriteLine("  dotnet run -- --tracker <path> [--discover] [--resolve] [--branches public,beta]");
        Console.WriteLine("                [--backfill-from-csv <csv>] [--bump-manifest <manifest.json>]");
        Console.WriteLine("                [--discord-webhook-env DISCORD_WEBHOOK_URL] [--unlicense <path>]");
        Console.WriteLine("                [--force-resolve-manifest <id>] [--dry-run]");
        Console.WriteLine();
        Console.WriteLine("Common options:");
        Console.WriteLine("  --csv <path>           CSV to update (CSV mode). Defaults to ./steamdb_patch_manifest_map.csv");
        Console.WriteLine("  --tracker <path>       JSON tracker to update (tracker mode).");
        Console.WriteLine("  --discover             Run PICS to find new manifests and append them to the tracker.");
        Console.WriteLine("  --resolve              Resolve game versions for tracker entries that lack one.");
        Console.WriteLine("                         Implied when --tracker is set without other modifiers.");
        Console.WriteLine("  --branches <list>      Comma-separated branches to track (default: public).");
        Console.WriteLine("  --backfill-from-csv    One-time import of an existing CSV into the tracker JSON.");
        Console.WriteLine("  --bump-manifest <p>    After a successful save, recompute sha256+size in <p> for the tracker file.");
        Console.WriteLine("  --discord-webhook-env  Name of an env var holding a Discord webhook URL for notifications.");
        Console.WriteLine("  --unlicense <path>     Override the unlicense.exe location (also via ANOMALY_UNLICENSE_PATH).");
        Console.WriteLine("  --force-resolve-manifest <id>  Re-resolve a single manifest end-to-end (debug aid).");
        Console.WriteLine("  --limit <n>            Process at most N unique manifest ids per run.");
        Console.WriteLine("  --parallelism <n>      Max manifest workers. Defaults to min(Environment.ProcessorCount, 4).");
        Console.WriteLine("  --signature-first      Run pdata cctor scanner before IL2CPP metadata probing.");
        Console.WriteLine("  --gamemanager-first    Try globalgamemanagers before downloading IL2CPP files.");
        Console.WriteLine("  --min-metadata-version Lowest metadata version for the full descending scan.");
        Console.WriteLine("  --max-metadata-version Highest metadata version for the full descending scan.");
        Console.WriteLine("  --force                Re-resolve entries/rows that already have a version.");
        Console.WriteLine("  --dry-run              Show the work plan without logging into Steam or writing.");
        Console.WriteLine("  --local <dir>          Run the IL2CPP version reader against a local SCP:SL install directory.");
        Console.WriteLine("  --help                 Show this help text.");
        Console.WriteLine();
        Console.WriteLine("Authentication (in order of preference):");
        Console.WriteLine("  STEAM_REFRESH_TOKEN_JSON  {\"AccountName\":\"...\",\"RefreshToken\":\"...\"} (silent reuse)");
        Console.WriteLine("  STEAM_USERNAME + STEAM_PASSWORD  (+ optional STEAM_SHARED_SECRET base64 TOTP seed)");
        Console.WriteLine("  Stored DPAPI session under %APPDATA%\\Anomaly\\steam (dev machines)");
        Console.WriteLine("  QR login fallback (interactive only).");
        Console.WriteLine();
        Console.WriteLine("Auth failures exit with code 78 so callers can distinguish them from data errors.");
    }
}
