using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppDumper;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json;

namespace ManifestAnalyzer;

internal static class ManifestVersionReader
{
    private const string VersionMethodName = "GameCore.Version$$.cctor";
    private static readonly int[] KnownMetadataVersionCandidates = [31, 29, 24];

    private static readonly object ExtractionLock = new();
    private static readonly Dictionary<MetadataRegistryKey, int> LearnedMetadataVersions = [];
    private static readonly Dictionary<long, int> LearnedMetadataVersionsByMetadataSize = [];
    private static readonly Dictionary<int, int> LearnedMetadataVersionCounts = [];

    public static VersionResolutionResult ReadVersion(
        DownloadedManifestFiles downloadedFiles,
        string workingDirectory,
        string logPrefix,
        bool signatureFirst,
        bool fullMetadataScan,
        int minMetadataVersion,
        int maxMetadataVersion)
    {
        lock (ExtractionLock)
        {
            var failures = new List<string>();

            if (downloadedFiles.HasIl2CppInputs)
            {
                try
                {
                    var version = ReadIl2CppVersion(
                        downloadedFiles.GameAssemblyPath!,
                        downloadedFiles.MetadataPath!,
                        workingDirectory,
                        logPrefix,
                        signatureFirst,
                        fullMetadataScan,
                        minMetadataVersion,
                        maxMetadataVersion);

                    return version;
                }
                catch (Exception ex)
                {
                    failures.Add($"il2cpp: {ex.Message}");
                }
            }

            if (downloadedFiles.HasMonoInputs)
            {
                Console.WriteLine($"{logPrefix} Trying managed-assembly fallback.");

                try
                {
                    var version = ReadMonoVersion(downloadedFiles.MonoAssemblyPaths, logPrefix);
                    return new VersionResolutionResult(version, "mono");
                }
                catch (Exception ex)
                {
                    failures.Add($"mono: {ex.Message}");
                }
            }
            else if (failures.Count > 0)
            {
                failures.Add("mono: not attempted because no known managed assemblies were present in the manifest");
            }

            var failureSummary = failures.Count == 0
                ? "No IL2CPP or Mono inputs were available."
                : string.Join(" | ", failures);

            throw new InvalidOperationException($"Could not determine the game version. {failureSummary}");
        }
    }

    public static VersionResolutionResult? TryReadGameManagerVersion(string gameManagerPath, string logPrefix)
    {
        if (!File.Exists(gameManagerPath))
            return null;

        try
        {
            var data = File.ReadAllBytes(gameManagerPath);
            var offset = FindBundleVersionOffset(data);
            if (offset < 0)
            {
                Console.WriteLine($"{logPrefix} globalgamemanagers did not contain a readable dotted version string.");
                return null;
            }

            var length = BitConverter.ToInt32(data, offset);
            if (length <= 0 || offset + 4 + length > data.Length)
            {
                Console.WriteLine($"{logPrefix} globalgamemanagers version slot was malformed.");
                return null;
            }

            var versionString = Encoding.UTF8.GetString(data, offset + 4, length);
            if (!IsVersionString(versionString))
            {
                Console.WriteLine($"{logPrefix} globalgamemanagers produced a non-version string: {versionString}");
                return null;
            }

            Console.WriteLine($"{logPrefix} globalgamemanagers fast check succeeded: {versionString}");
            return new VersionResolutionResult(versionString, "gamemanager");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{logPrefix} globalgamemanagers fast check failed: {ex.Message}");
            return null;
        }
    }

    private static VersionResolutionResult ReadIl2CppVersion(
        string gameAssemblyPath,
        string metadataPath,
        string workingDirectory,
        string logPrefix,
        bool signatureFirst,
        bool fullMetadataScan,
        int minMetadataVersion,
        int maxMetadataVersion)
    {
        if (!File.Exists(gameAssemblyPath))
            throw new FileNotFoundException($"GameAssembly.dll not found: {gameAssemblyPath}", gameAssemblyPath);

        if (!File.Exists(metadataPath))
            throw new FileNotFoundException($"global-metadata.dat not found: {metadataPath}", metadataPath);

        var originalMetadataBytes = File.ReadAllBytes(metadataPath);
        var registryKey = BuildMetadataRegistryKey(gameAssemblyPath, metadataPath);
        if (signatureFirst)
        {
            Console.WriteLine($"{logPrefix} Scanner-first mode enabled. Running the pdata cctor scanner before metadata probing.");

            var scannerFirst = PdataVersionCctorScanner.TryFindVersion(File.ReadAllBytes(gameAssemblyPath), logPrefix);
            if (scannerFirst != null)
            {
                Console.WriteLine($"{logPrefix} Pdata cctor scanner resolved the version before metadata probing.");
                return BuildCctorScanResult(scannerFirst);
            }

            Console.WriteLine($"{logPrefix} Pdata cctor scanner did not find a confident match. Continuing to metadata probing.");
        }

        var metadataVersionCandidates = GetMetadataVersionCandidates(
            registryKey,
            fullMetadataScan,
            minMetadataVersion,
            maxMetadataVersion);
        if (LearnedMetadataVersions.TryGetValue(registryKey, out var learnedMetadataVersion))
        {
            Console.WriteLine(
                $"{logPrefix} Reusing learned metadata version {learnedMetadataVersion} for GameAssembly {registryKey.GameAssemblySize:N0} bytes and metadata {registryKey.MetadataSize:N0} bytes.");
        }
        else if (LearnedMetadataVersionsByMetadataSize.TryGetValue(registryKey.MetadataSize, out var learnedMetadataVersionByMetadataSize))
        {
            Console.WriteLine(
                $"{logPrefix} Reusing learned metadata version {learnedMetadataVersionByMetadataSize} for metadata size {registryKey.MetadataSize:N0} bytes.");
        }
        else if (LearnedMetadataVersionCounts.Count > 0)
        {
            var globallyPreferredMetadataVersions = string.Join(", ",
                LearnedMetadataVersionCounts
                    .OrderByDescending(entry => entry.Value)
                    .ThenByDescending(entry => entry.Key)
                    .Select(entry => $"{entry.Key} ({entry.Value} hits)"));

            Console.WriteLine($"{logPrefix} Trying globally learned metadata versions first: {globallyPreferredMetadataVersions}.");
        }

        var failures = new List<string>();
        var unpacked = false;

        while (true)
        {
            failures.Clear();
            var protectedCandidateSeen = false;

            for (var candidateIndex = 0; candidateIndex < metadataVersionCandidates.Count; candidateIndex++)
            {
                var metadataVersion = metadataVersionCandidates[candidateIndex];
                if (candidateIndex == 1)
                {
                    var remainingCandidates = string.Join(", ", metadataVersionCandidates.Skip(candidateIndex));
                    if (metadataVersionCandidates[0] == maxMetadataVersion)
                    {
                        Console.WriteLine(
                            $"{logPrefix} Primary metadata probe failed. Trying remaining metadata versions: {remainingCandidates}.");
                    }
                    else
                    {
                        Console.WriteLine(
                            $"{logPrefix} Preferred metadata probe {metadataVersionCandidates[0]} failed. Trying remaining metadata versions: {remainingCandidates}.");
                    }
                }

                var attemptDir = Path.Combine(workingDirectory, $"il2cpp-{metadataVersion}");
                DeleteDirectory(attemptDir);
                Directory.CreateDirectory(attemptDir);

                try
                {
                    var candidateMetadataPath = Path.Combine(attemptDir, "global-metadata.dat");
                    File.WriteAllBytes(candidateMetadataPath, PatchMetadataVersion(originalMetadataBytes, metadataVersion));

                    var version = TryReadVersion(
                        File.ReadAllBytes(gameAssemblyPath),
                        candidateMetadataPath,
                        metadataVersion,
                        attemptDir);

                    if (version.HasValue)
                    {
                        RegisterWorkingMetadataVersion(registryKey, metadataVersion, logPrefix);
                        Console.WriteLine($"{logPrefix} IL2CPP resolved with metadata version {metadataVersion}.");
                        return new VersionResolutionResult(
                            FormatVersion(version.Value),
                            "il2cpp");
                    }

                    failures.Add($"metadata {metadataVersion}: version byte pattern was not found");
                }
                catch (ManifestAssemblyProtectedException)
                {
                    protectedCandidateSeen = true;
                    failures.Add($"metadata {metadataVersion}: IL2CPP auto-search could not resolve registrations and the PE looked protected");
                }
                catch (Exception ex)
                {
                    failures.Add($"metadata {metadataVersion}: {ex.Message}");
                }
                finally
                {
                    DeleteDirectory(attemptDir);
                }
            }

            if (protectedCandidateSeen && !unpacked)
            {
                if (!LooksLikeThemidaOrWinLicense(File.ReadAllBytes(gameAssemblyPath)))
                {
                    failures.Add("unpacker skipped: GameAssembly.dll does not positively match known Themida/WinLicense markers");
                    break;
                }

                Console.WriteLine($"{logPrefix} GameAssembly.dll is protected. Running the unpacker once before retrying.");

                try
                {
                    UnpackGameAssembly(gameAssemblyPath, workingDirectory);
                }
                catch (Exception ex)
                {
                    var unpackFailureSummary = string.Join(" | ", failures);
                    throw new InvalidOperationException(
                        $"Could not determine the game version. {unpackFailureSummary} | unpacker failed: {ex.Message}");
                }

                unpacked = true;
                continue;
            }

            break;
        }

        var failureSummary = failures.Count == 0
            ? "No IL2CPP metadata version candidate succeeded."
            : string.Join(" | ", failures);

        Console.WriteLine($"{logPrefix} IL2CPP metadata probing exhausted. Falling back to pdata cctor scanner.");
        var scannerResult = PdataVersionCctorScanner.TryFindVersion(File.ReadAllBytes(gameAssemblyPath), logPrefix);
        if (scannerResult != null)
            return BuildCctorScanResult(scannerResult);

        throw new InvalidOperationException($"Could not determine the game version. {failureSummary}");
    }

    private static VersionResolutionResult BuildCctorScanResult(PdataCctorResult result)
    {
        return new VersionResolutionResult(
            $"{result.Major}.{result.Minor}.{result.Revision}",
            "il2cpp-cctor-scan");
    }

    private static MetadataRegistryKey BuildMetadataRegistryKey(string gameAssemblyPath, string metadataPath)
    {
        return new MetadataRegistryKey(
            new FileInfo(gameAssemblyPath).Length,
            new FileInfo(metadataPath).Length);
    }

    private static List<int> GetMetadataVersionCandidates(
        MetadataRegistryKey registryKey,
        bool fullMetadataScan,
        int minMetadataVersion,
        int maxMetadataVersion)
    {
        var candidates = new List<int>();
        var seen = new HashSet<int>();

        if (LearnedMetadataVersions.TryGetValue(registryKey, out var learnedMetadataVersion) && seen.Add(learnedMetadataVersion))
            candidates.Add(learnedMetadataVersion);

        if (LearnedMetadataVersionsByMetadataSize.TryGetValue(registryKey.MetadataSize, out var learnedMetadataVersionByMetadataSize) &&
            seen.Add(learnedMetadataVersionByMetadataSize))
        {
            candidates.Add(learnedMetadataVersionByMetadataSize);
        }

        foreach (var globallyPreferredMetadataVersion in LearnedMetadataVersionCounts
                     .OrderByDescending(entry => entry.Value)
                     .ThenByDescending(entry => entry.Key)
                     .Select(entry => entry.Key))
        {
            if (globallyPreferredMetadataVersion >= minMetadataVersion &&
                globallyPreferredMetadataVersion <= maxMetadataVersion &&
                seen.Add(globallyPreferredMetadataVersion))
            {
                candidates.Add(globallyPreferredMetadataVersion);
            }
        }

        foreach (var knownMetadataVersion in KnownMetadataVersionCandidates)
        {
            if (knownMetadataVersion >= minMetadataVersion &&
                knownMetadataVersion <= maxMetadataVersion &&
                seen.Add(knownMetadataVersion))
            {
                candidates.Add(knownMetadataVersion);
            }
        }

        if (!fullMetadataScan)
            return candidates;

        for (var metadataVersion = maxMetadataVersion; metadataVersion >= minMetadataVersion; metadataVersion--)
        {
            if (seen.Add(metadataVersion))
                candidates.Add(metadataVersion);
        }

        return candidates;
    }

    private static void RegisterWorkingMetadataVersion(
        MetadataRegistryKey registryKey,
        int metadataVersion,
        string logPrefix)
    {
        if (LearnedMetadataVersions.TryGetValue(registryKey, out var existingMetadataVersion) && existingMetadataVersion == metadataVersion)
        {
            LearnedMetadataVersionCounts[metadataVersion] = LearnedMetadataVersionCounts.GetValueOrDefault(metadataVersion) + 1;
            return;
        }

        LearnedMetadataVersions[registryKey] = metadataVersion;
        LearnedMetadataVersionsByMetadataSize[registryKey.MetadataSize] = metadataVersion;
        LearnedMetadataVersionCounts[metadataVersion] = LearnedMetadataVersionCounts.GetValueOrDefault(metadataVersion) + 1;
        Console.WriteLine(
            $"{logPrefix} Learned metadata version {metadataVersion} for GameAssembly {registryKey.GameAssemblySize:N0} bytes and metadata {registryKey.MetadataSize:N0} bytes.");
    }

    private static string ReadMonoVersion(IReadOnlyList<string> monoAssemblyPaths, string logPrefix)
    {
        var failures = new List<string>();

        foreach (var assemblyPath in monoAssemblyPaths)
        {
            if (!File.Exists(assemblyPath))
            {
                failures.Add($"{Path.GetFileName(assemblyPath)}: file not found");
                continue;
            }

            try
            {
                var version = TryReadMonoVersionFromAssembly(assemblyPath);
                if (version != null)
                {
                    Console.WriteLine($"{logPrefix} Managed-assembly fallback succeeded via {Path.GetFileName(assemblyPath)}.");
                    return version;
                }

                failures.Add($"{Path.GetFileName(assemblyPath)}: GameCore.Version static constructor did not yield constant version fields");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(assemblyPath)}: {ex.Message}");
            }
        }

        var failureSummary = failures.Count == 0
            ? "No managed assemblies were available to inspect."
            : string.Join(" | ", failures);

        throw new InvalidOperationException($"Managed-assembly fallback failed. {failureSummary}");
    }

    private static string? TryReadMonoVersionFromAssembly(string assemblyPath)
    {
        var readerParameters = new ReaderParameters
        {
            ReadSymbols = false,
            InMemory = true,
            ReadingMode = ReadingMode.Immediate,
        };

        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readerParameters);
        var versionType = FindVersionType(assembly.MainModule.Types);
        if (versionType == null)
            return null;

        if (TryExtractVersionFromFieldConstants(versionType, out var constantVersion))
            return FormatVersion(constantVersion);

        var cctor = versionType.Methods.FirstOrDefault(method => method.IsConstructor && method.IsStatic && method.HasBody);
        if (cctor == null)
            return null;

        var ctorVersion = TryExtractVersionFromMonoInstructions(cctor.Body.Instructions);
        return ctorVersion.HasValue ? FormatVersion(ctorVersion.Value) : null;
    }

    private static TypeDefinition? FindVersionType(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            if (string.Equals(type.Namespace, "GameCore", StringComparison.Ordinal) &&
                string.Equals(type.Name, "Version", StringComparison.Ordinal))
            {
                return type;
            }

            var nestedMatch = FindVersionType(type.NestedTypes);
            if (nestedMatch != null)
                return nestedMatch;
        }

        return null;
    }

    private static bool TryExtractVersionFromFieldConstants(
        TypeDefinition versionType,
        out (byte Major, byte Minor, byte Revision) version)
    {
        var values = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in versionType.Fields)
        {
            if (!field.HasConstant)
                continue;

            if (!TryNormalizeVersionFieldName(field.Name, out var normalizedFieldName))
                continue;

            if (!TryConvertToByte(field.Constant, out var fieldValue))
                continue;

            values[normalizedFieldName] = fieldValue;
        }

        var builtVersion = TryBuildVersion(values);
        if (!builtVersion.HasValue)
        {
            version = default;
            return false;
        }

        version = builtVersion.Value;
        return true;
    }

    private static (byte Major, byte Minor, byte Revision)? TryExtractVersionFromMonoInstructions(IList<Instruction> instructions)
    {
        var values = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        for (var instructionIndex = 0; instructionIndex < instructions.Count; instructionIndex++)
        {
            var instruction = instructions[instructionIndex];
            if (instruction.OpCode.Code is not (Code.Stfld or Code.Stsfld))
                continue;

            if (instruction.Operand is not FieldReference fieldReference)
                continue;

            if (!TryNormalizeVersionFieldName(fieldReference.Name, out var normalizedFieldName))
                continue;

            if (!TryGetInt32Constant(instructions, instructionIndex - 1, out var constantValue))
                continue;

            if (constantValue is < byte.MinValue or > byte.MaxValue)
                continue;

            values[normalizedFieldName] = (byte)constantValue;
        }

        return TryBuildVersion(values);
    }

    private static bool TryNormalizeVersionFieldName(string fieldName, out string normalizedFieldName)
    {
        normalizedFieldName = string.Empty;

        var simplified = new string(fieldName.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        if (simplified.Contains("backward", StringComparison.Ordinal))
            return false;

        if (simplified.Contains("major", StringComparison.Ordinal))
        {
            normalizedFieldName = "major";
            return true;
        }

        if (simplified.Contains("minor", StringComparison.Ordinal))
        {
            normalizedFieldName = "minor";
            return true;
        }

        if (simplified.Contains("revision", StringComparison.Ordinal))
        {
            normalizedFieldName = "revision";
            return true;
        }

        return false;
    }

    private static bool TryGetInt32Constant(IList<Instruction> instructions, int instructionIndex, out int value)
    {
        while (instructionIndex >= 0)
        {
            var instruction = instructions[instructionIndex];
            switch (instruction.OpCode.Code)
            {
                case Code.Conv_I1:
                case Code.Conv_I2:
                case Code.Conv_I4:
                case Code.Conv_U1:
                case Code.Conv_U2:
                case Code.Conv_U4:
                    instructionIndex--;
                    continue;

                case Code.Ldc_I4_M1:
                    value = -1;
                    return true;
                case Code.Ldc_I4_0:
                    value = 0;
                    return true;
                case Code.Ldc_I4_1:
                    value = 1;
                    return true;
                case Code.Ldc_I4_2:
                    value = 2;
                    return true;
                case Code.Ldc_I4_3:
                    value = 3;
                    return true;
                case Code.Ldc_I4_4:
                    value = 4;
                    return true;
                case Code.Ldc_I4_5:
                    value = 5;
                    return true;
                case Code.Ldc_I4_6:
                    value = 6;
                    return true;
                case Code.Ldc_I4_7:
                    value = 7;
                    return true;
                case Code.Ldc_I4_8:
                    value = 8;
                    return true;
                case Code.Ldc_I4_S:
                    value = instruction.Operand switch
                    {
                        sbyte signedByte => signedByte,
                        byte unsignedByte => unsignedByte,
                        _ => Convert.ToInt32(instruction.Operand),
                    };
                    return true;
                case Code.Ldc_I4:
                    value = instruction.Operand is int intValue
                        ? intValue
                        : Convert.ToInt32(instruction.Operand);
                    return true;
                default:
                    value = 0;
                    return false;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryConvertToByte(object? constant, out byte value)
    {
        switch (constant)
        {
            case byte byteValue:
                value = byteValue;
                return true;
            case sbyte signedByteValue when signedByteValue >= 0:
                value = (byte)signedByteValue;
                return true;
            case short shortValue when shortValue is >= byte.MinValue and <= byte.MaxValue:
                value = (byte)shortValue;
                return true;
            case int intValue when intValue is >= byte.MinValue and <= byte.MaxValue:
                value = (byte)intValue;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static (byte Major, byte Minor, byte Revision)? TryBuildVersion(IReadOnlyDictionary<string, byte> values)
    {
        if (!values.TryGetValue("major", out var major) ||
            !values.TryGetValue("minor", out var minor) ||
            !values.TryGetValue("revision", out var revision))
        {
            return null;
        }

        return (major, minor, revision);
    }

    private static string FormatVersion((byte Major, byte Minor, byte Revision) version)
    {
        return $"{version.Major}.{version.Minor}.{version.Revision}";
    }

    private static int FindBundleVersionOffset(byte[] data)
    {
        for (var i = 0; i < data.Length - 14; i++)
        {
            var length = BitConverter.ToInt32(data, i);
            if (length < 3 || length > 20 || i + 4 + length > data.Length)
                continue;

            var candidate = Encoding.UTF8.GetString(data, i + 4, length);
            if (!IsVersionString(candidate))
                continue;

            var parts = candidate.Split('.');
            if (byte.TryParse(parts[0], out var major) && major >= 10)
                return i;
        }

        return -1;
    }

    private static bool IsVersionString(string value)
    {
        return Regex.IsMatch(value, @"^\d{1,3}\.\d{1,3}\.\d{1,3}$");
    }

    private static byte[] PatchMetadataVersion(byte[] metadataBytes, int metadataVersion)
    {
        const int versionOffset = 4;

        var patchedBytes = (byte[])metadataBytes.Clone();
        var versionBytes = BitConverter.GetBytes(metadataVersion);
        if (patchedBytes.Length < versionOffset + versionBytes.Length)
        {
            throw new InvalidOperationException(
                $"global-metadata.dat is too small to patch a metadata version header ({patchedBytes.Length} bytes).");
        }

        Array.Copy(versionBytes, 0, patchedBytes, versionOffset, versionBytes.Length);
        return patchedBytes;
    }

    private static (byte Major, byte Minor, byte Revision)? TryReadVersion(
        byte[] gameAssemblyData,
        string metadataPath,
        int metadataVersion,
        string scratchDirectory)
    {
        var config = new Config
        {
            ForceIl2CppVersion = true,
            ForceVersion = metadataVersion,
        };

        Il2CppAutomationWorker.Init(gameAssemblyData, metadataPath, config, out var metadata, out var il2Cpp);
        Il2CppAutomationWorker.Dump(metadata, il2Cpp, scratchDirectory + Path.DirectorySeparatorChar);

        var scriptJsonPath = Path.Combine(scratchDirectory, "script.json");
        if (!File.Exists(scriptJsonPath))
            throw new InvalidOperationException("Il2CppDumper did not produce script.json.");

        var scriptJson = JsonConvert.DeserializeObject<ScriptJson>(File.ReadAllText(scriptJsonPath))
            ?? throw new InvalidOperationException("Failed to deserialize script.json.");

        var methods = BuildMethodDictionary(scriptJson);
        return TryExtractVersion(gameAssemblyData, il2Cpp, methods);
    }

    private static Dictionary<string, ScriptMethod> BuildMethodDictionary(ScriptJson scriptJson)
    {
        var methods = new Dictionary<string, ScriptMethod>(StringComparer.Ordinal);
        foreach (var method in scriptJson.ScriptMethod)
        {
            if (!methods.ContainsKey(method.Name))
                methods.Add(method.Name, method);
        }

        return methods;
    }

    private static (byte Major, byte Minor, byte Revision)? TryExtractVersion(
        byte[] gameAssemblyData,
        Il2Cpp il2Cpp,
        IReadOnlyDictionary<string, ScriptMethod> methodsDictionary)
    {
        var methodOffset = GetOffsetFromFunctionName(il2Cpp, VersionMethodName, methodsDictionary);
        if (methodOffset < 0)
            return null;

        byte[] pattern = [0x48, 0x8B, 0x88, 0xB8, 0x00, 0x00, 0x00];
        var patternOffset = IndexOf(gameAssemblyData, pattern, methodOffset);
        if (patternOffset < 0)
            return null;

        var searchFrom = (int)patternOffset;
        byte[] fieldOffsets = [0, 1, 2];
        var values = new byte[3];

        for (var i = 0; i < fieldOffsets.Length; i++)
        {
            byte[] instruction = fieldOffsets[i] == 0
                ? [0xC6, 0x01]
                : [0xC6, 0x41, fieldOffsets[i]];

            var instructionOffset = (int)IndexOf(gameAssemblyData, instruction, searchFrom);
            if (instructionOffset < 0)
                return null;

            var valueOffset = instructionOffset + instruction.Length;
            values[i] = gameAssemblyData[valueOffset];
            searchFrom = valueOffset + 1;
        }

        return (values[0], values[1], values[2]);
    }

    private static int GetOffsetFromFunctionName(
        Il2Cpp il2Cpp,
        string functionName,
        IReadOnlyDictionary<string, ScriptMethod> methodsDictionary)
    {
        if (!methodsDictionary.TryGetValue(functionName, out var method))
            return -1;

        var address = method.Address;
        var rvaOffset = il2Cpp.GetRVA(il2Cpp.MapRTVA(address)) - address;
        var offset = address - rvaOffset;
        return Convert.ToInt32(offset);
    }

    private static long IndexOf(byte[] data, byte[] pattern, int startIndex)
    {
        if (pattern.Length == 0)
            return startIndex;

        var lastStartIndex = data.Length - pattern.Length;
        for (var dataIndex = startIndex; dataIndex <= lastStartIndex; dataIndex++)
        {
            if (data[dataIndex] != pattern[0])
                continue;

            var match = true;
            for (var patternIndex = 1; patternIndex < pattern.Length; patternIndex++)
            {
                if (data[dataIndex + patternIndex] == pattern[patternIndex])
                    continue;

                match = false;
                break;
            }

            if (match)
                return dataIndex;
        }

        return -1;
    }

    private static void UnpackGameAssembly(string gameAssemblyPath, string workingDirectory)
    {
        var unpackerSourcePath = FindUnlicensePath();
        if (unpackerSourcePath == null)
        {
            throw new InvalidOperationException(
                "GameAssembly.dll is Themida-protected, but unlicense.exe could not be found in the sibling Anomaly repo.");
        }

        var unpackerPath = Path.Combine(workingDirectory, "unlicense.exe");
        var unpackedPath = Path.Combine(workingDirectory, "unpacked_GameAssembly.dll");

        File.Copy(unpackerSourcePath, unpackerPath, overwrite: true);

        try
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = unpackerPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            process.StartInfo.ArgumentList.Add(gameAssemblyPath);

            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data != null)
                    stdout.AppendLine(eventArgs.Data);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data != null)
                    stderr.AppendLine(eventArgs.Data);
            };

            if (!process.Start())
                throw new InvalidOperationException("Failed to start unlicense.exe.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(45_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort timeout cleanup.
                }

                throw new TimeoutException("unlicense.exe timed out while unpacking GameAssembly.dll.");
            }

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(BuildUnpackerFailureMessage(process.ExitCode, stdout.ToString(), stderr.ToString()));
            }

            if (!File.Exists(unpackedPath))
                throw new InvalidOperationException("unlicense.exe did not produce unpacked_GameAssembly.dll.");

            var unpackedSize = new FileInfo(unpackedPath).Length;
            if (unpackedSize < 1024 * 1024)
            {
                throw new InvalidOperationException(
                    $"unpacked_GameAssembly.dll is suspiciously small ({unpackedSize} bytes).");
            }

            File.Move(unpackedPath, gameAssemblyPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(unpackerPath);
            TryDeleteFile(unpackedPath);
        }
    }

    private static string? FindUnlicensePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("ANOMALY_UNLICENSE_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateSearchRoots())
        {
            foreach (var relative in UnlicenseRelativeCandidates)
            {
                var candidate = Path.GetFullPath(Path.Combine(root, relative));
                if (!seenPaths.Add(candidate))
                    continue;

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static readonly string[] UnlicenseRelativeCandidates =
    [
        Path.Combine("anomaly-resources", "patching", "unlicense.exe"),
        Path.Combine("patching", "unlicense.exe"),
        Path.Combine("Anomaly", "Anomaly.Installer", "Resources", "unlicense.exe"),
    ];

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in EnumerateAncestors(Environment.CurrentDirectory))
        {
            if (visited.Add(path))
                yield return path;
        }

        foreach (var path in EnumerateAncestors(AppContext.BaseDirectory))
        {
            if (visited.Add(path))
                yield return path;
        }
    }

    private static IEnumerable<string> EnumerateAncestors(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        while (directory != null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
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
            // Best-effort scratch cleanup.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private static string BuildUnpackerFailureMessage(int exitCode, string stdout, string stderr)
    {
        var message = exitCode switch
        {
            2 => "unlicense.exe could not detect a supported Themida/WinLicense version for this GameAssembly.dll",
            3 => "unlicense.exe detected an unsupported 32-bit vs 64-bit mismatch",
            4 => "unlicense.exe timed out or failed to locate the original entry point",
            _ => $"unlicense.exe exited with code {exitCode}",
        };

        var details = string.IsNullOrWhiteSpace(stderr)
            ? stdout.Trim()
            : stderr.Trim();

        if (string.IsNullOrWhiteSpace(details))
            return message + ".";

        return message + $": {details}";
    }

    private static bool LooksLikeThemidaOrWinLicense(byte[] gameAssemblyData)
    {
        try
        {
            using var peStream = new MemoryStream(gameAssemblyData, writable: false);
            using var peReader = new PEReader(peStream, PEStreamOptions.LeaveOpen);

            foreach (var section in peReader.PEHeaders.SectionHeaders)
            {
                if (string.Equals(section.Name, ".themida", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(section.Name, ".winlice", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (MatchesThemida2Pattern(gameAssemblyData, section.PointerToRawData, section.SizeOfRawData))
                    return true;
            }
        }
        catch
        {
            // If we cannot parse the PE cleanly, do not assume protection.
        }

        return false;
    }

    private static bool MatchesThemida2Pattern(byte[] data, int start, int length)
    {
        if (start < 0 || length <= 0 || start >= data.Length)
            return false;

        ReadOnlySpan<byte> themida2PatternA = [0x56, 0x50, 0x53, 0xE8, 0x01, 0x00, 0x00, 0x00, 0xCC, 0x58];
        ReadOnlySpan<byte> themida2PatternB = [0x83, 0xEC, 0x04, 0x50, 0x53, 0xE8, 0x01, 0x00, 0x00, 0x00, 0xCC, 0x58];

        var available = Math.Min(length, data.Length - start);
        if (available <= 0)
            return false;

        var sectionStart = data.AsSpan(start, available);
        return sectionStart.StartsWith(themida2PatternA) || sectionStart.StartsWith(themida2PatternB);
    }
}

internal sealed record VersionResolutionResult(string Version, string ResolutionPath);

internal sealed record MetadataRegistryKey(long GameAssemblySize, long MetadataSize);

internal sealed class ManifestAssemblyProtectedException : Exception;

internal static class Il2CppAutomationWorker
{
    public static void Init(byte[] il2CppBytes, string metadataPath, Config config, out Metadata metadata, out Il2Cpp il2Cpp)
    {
        Init(il2CppBytes, File.ReadAllBytes(metadataPath), config, out metadata, out il2Cpp);
    }

    public static void Init(byte[] il2CppBytes, byte[] metadataBytes, Config config, out Metadata metadata, out Il2Cpp il2Cpp)
    {
        metadata = new Metadata(new MemoryStream(metadataBytes));

        var il2CppMagic = BitConverter.ToUInt32(il2CppBytes, 0);
        var il2CppMemory = new MemoryStream(il2CppBytes);
        il2Cpp = il2CppMagic switch
        {
            0x6D736100 => new WebAssembly(il2CppMemory).CreateMemory(),
            0x304F534E => new NSO(il2CppMemory).UnCompress(),
            0x905A4D => new PE(il2CppMemory),
            0x464C457F => il2CppBytes[4] == 2 ? new Elf64(il2CppMemory) : new Elf(il2CppMemory),
            0xFEEDFACF => new Macho64(il2CppMemory),
            0xFEEDFACE => new Macho(il2CppMemory),
            0xCAFEBABE or 0xBEBAFECA => throw new NotSupportedException("FAT Mach-O IL2CPP binaries are not supported."),
            _ => throw new NotSupportedException("The IL2CPP binary format is not supported."),
        };

        var version = config.ForceIl2CppVersion ? config.ForceVersion : metadata.Version;
        il2Cpp.SetProperties(version, metadata.metadataUsagesCount);

        if (config.ForceDump || il2Cpp.CheckDump())
        {
            if (il2Cpp is ElfBase)
            {
                throw new InvalidOperationException(
                    "Dumped ELF IL2CPP inputs that require manual address entry are not supported.");
            }

            il2Cpp.IsDumped = true;
        }

        var success = il2Cpp.PlusSearch(
            metadata.methodDefs.Count(method => method.methodIndex >= 0),
            metadata.typeDefs.Length,
            metadata.imageDefs.Length);

        var looksProtectedWindowsPe = OperatingSystem.IsWindows() && !success && il2Cpp is PE;

        if (!success)
            success = il2Cpp.Search();

        if (!success)
            success = il2Cpp.SymbolSearch();

        if (!success && looksProtectedWindowsPe)
            throw new ManifestAssemblyProtectedException();

        if (!success)
        {
            throw new InvalidOperationException(
                "Il2CppDumper could not locate CodeRegistration/MetadataRegistration automatically.");
        }

        if (il2Cpp.Version >= 27 && il2Cpp.IsDumped)
        {
            var typeDef = metadata.typeDefs[0];
            var il2CppType = il2Cpp.types[typeDef.byvalTypeIndex];
            metadata.ImageBase = il2CppType.data.typeHandle - metadata.header.typeDefinitionsOffset;
        }
    }

    public static void Dump(Metadata metadata, Il2Cpp il2Cpp, string outputDirectory)
    {
        var executor = new Il2CppExecutor(metadata, il2Cpp);
        var scriptGenerator = new StructGenerator(executor);
        scriptGenerator.WriteScript(outputDirectory);
    }
}
