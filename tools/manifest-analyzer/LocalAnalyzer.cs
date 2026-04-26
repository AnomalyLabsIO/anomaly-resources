namespace ManifestAnalyzer;

internal static class LocalAnalyzer
{
    public static int Run(ManifestAnalyzerOptions options)
    {
        var directory = options.LocalDirectory!;
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"Local directory not found: {directory}");
            return 1;
        }

        var gameAssemblyPath = Path.Combine(directory, "GameAssembly.dll");
        var metadataPath = Path.Combine(directory, "SCPSL_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        var gameManagerPath = Path.Combine(directory, "SCPSL_Data", "globalgamemanagers");
        var monoAssemblyPaths = new[]
            {
                Path.Combine(directory, "SCPSL_Data", "Managed", "Assembly-CSharp.dll"),
                Path.Combine(directory, "SCPSL_Data", "Managed", "GameCore.dll"),
            }
            .Where(File.Exists)
            .ToArray();

        Console.WriteLine($"Local directory: {directory}");
        Console.WriteLine($"GameAssembly.dll: {(File.Exists(gameAssemblyPath) ? $"found ({new FileInfo(gameAssemblyPath).Length:N0} bytes)" : "MISSING")}");
        Console.WriteLine($"global-metadata.dat: {(File.Exists(metadataPath) ? $"found ({new FileInfo(metadataPath).Length:N0} bytes)" : "MISSING")}");
        Console.WriteLine($"globalgamemanagers: {(File.Exists(gameManagerPath) ? $"found ({new FileInfo(gameManagerPath).Length:N0} bytes)" : "MISSING")}");
        Console.WriteLine($"Mono assemblies: {(monoAssemblyPaths.Length == 0 ? "none" : string.Join(", ", monoAssemblyPaths.Select(Path.GetFileName)))}");

        var workDir = Path.Combine(Path.GetTempPath(), "ManifestAnalyzer", $"local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var downloaded = new DownloadedManifestFiles(
                File.Exists(gameAssemblyPath) ? gameAssemblyPath : null,
                File.Exists(metadataPath) ? metadataPath : null,
                monoAssemblyPaths);

            if (options.GameManagerFirst && File.Exists(gameManagerPath))
            {
                var gmResult = ManifestVersionReader.TryReadGameManagerVersion(gameManagerPath, "[local]");
                if (gmResult != null)
                {
                    Console.WriteLine($"[local] globalgamemanagers version: {gmResult.Version} via {gmResult.ResolutionPath}");
                    return 0;
                }
            }

            var version = ManifestVersionReader.ReadVersion(
                downloaded,
                workDir,
                "[local]",
                options.SignatureFirst,
                options.UsesFullMetadataScan(),
                options.GetEffectiveMinMetadataVersion(),
                options.GetEffectiveMaxMetadataVersion());

            Console.WriteLine($"[local] Resolved game version: {version.Version} via {version.ResolutionPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[local] Failed: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

}
