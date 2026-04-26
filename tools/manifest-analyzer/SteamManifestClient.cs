using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;

namespace ManifestAnalyzer;

internal sealed class SteamManifestClient : IDisposable
{
    public const uint ScpSlAppId = 700330;
    public const uint ScpSlDepotId = 700331;
    private const string DeviceFriendlyName = "Manifest Analyzer";
    private const string EnvRefreshTokenJson = "STEAM_REFRESH_TOKEN_JSON";
    private const string EnvSteamUsername = "STEAM_USERNAME";
    private const string EnvSteamPassword = "STEAM_PASSWORD";
    private const string EnvSteamSharedSecret = "STEAM_SHARED_SECRET";
    private const string GameAssemblyManifestPath = "GameAssembly.dll";
    private const string MetadataManifestPath = @"SCPSL_Data\il2cpp_data\Metadata\global-metadata.dat";
    private const string GameManagerManifestPath = @"SCPSL_Data\globalgamemanagers";

    private static readonly string[] MonoAssemblyManifestPaths =
    [
        @"SCPSL_Data\Managed\Assembly-CSharp.dll",
        @"SCPSL_Data\Managed\GameCore.dll",
    ];

    private static readonly HashSet<string> CandidateManifestPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        NormalizeManifestPath(GameAssemblyManifestPath),
        NormalizeManifestPath(MetadataManifestPath),
        NormalizeManifestPath(GameManagerManifestPath),
        NormalizeManifestPath(MonoAssemblyManifestPaths[0]),
        NormalizeManifestPath(MonoAssemblyManifestPaths[1]),
    };

    private readonly object _gate = new();

    private SteamClient? _client;
    private CallbackManager? _callbackManager;
    private CancellationTokenSource? _callbackPumpCts;
    private Task? _callbackPumpTask;

    private bool _isLoggedOn;
    private string? _loggedOnAccountName;
    private byte[]? _depotKey;
    private Server? _cdnServer;
    private bool _disableSessionPersistence;
    private StoredSession? _runtimeSession;

    public SteamClient? RawClient => _client;
    public bool IsLoggedOn => _isLoggedOn;

    private static string SessionDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Anomaly",
        "steam");

    private static string SessionFilePath => Path.Combine(SessionDirectory, "session.bin");

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        if (_isLoggedOn)
            return;

        var envSession = LoadEnvRefreshTokenSession();
        if (envSession != null)
        {
            Console.WriteLine($"Trying STEAM_REFRESH_TOKEN_JSON for {envSession.AccountName}...");
            _disableSessionPersistence = true;
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await CompleteLogOnAsync(envSession.AccountName, envSession.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
                _runtimeSession = envSession;
                Console.WriteLine($"Logged into Steam as {envSession.AccountName} using STEAM_REFRESH_TOKEN_JSON.");
                return;
            }
            catch (SteamSessionExpiredException ex)
            {
                Console.WriteLine($"STEAM_REFRESH_TOKEN_JSON rejected ({ex.Message}); falling back to credentials.");
                ResetSession();
            }
        }

        var credentials = LoadEnvCredentials();
        if (credentials != null)
        {
            Console.WriteLine($"Trying credential login for {credentials.Username}...");
            _disableSessionPersistence = true;
            try
            {
                await LoginWithCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SteamSessionExpiredException)
            {
                throw;
            }
            catch (AuthenticationException ex)
            {
                throw new SteamSessionExpiredException($"Steam credential login failed: {ex.Message}");
            }
        }

        var storedSession = LoadStoredSession();
        if (storedSession != null)
        {
            Console.WriteLine("Trying stored Steam session...");
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await CompleteLogOnAsync(storedSession.AccountName, storedSession.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
                _runtimeSession = storedSession;
                Console.WriteLine($"Logged into Steam as {storedSession.AccountName} using the stored session.");
                return;
            }
            catch (SteamSessionExpiredException)
            {
                Console.WriteLine("Stored Steam session expired. Falling back to QR login.");
                DeleteStoredSession();
                ResetSession();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Stored Steam session failed: {ex.Message}");
                ResetSession();
            }
        }

        Console.WriteLine("Starting Steam QR login...");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var authSession = await _client!.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
        {
            DeviceFriendlyName = DeviceFriendlyName,
            IsPersistentSession = true,
        }).ConfigureAwait(false);

        var openedBrowser = false;

        void PrintChallengeUrl(string url)
        {
            Console.WriteLine("Approve the sign-in from the Steam mobile app.");
            Console.WriteLine($"Challenge URL: {url}");

            if (openedBrowser)
                return;

            TryOpenBrowser(url);
            openedBrowser = true;
        }

        PrintChallengeUrl(authSession.ChallengeURL);
        authSession.ChallengeURLChanged = () => PrintChallengeUrl(authSession.ChallengeURL);

        AuthPollResult pollResult;
        try
        {
            pollResult = await authSession.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            throw new InvalidOperationException($"Steam QR login failed: {ex.Message}", ex);
        }

        await CompleteLogOnAsync(pollResult.AccountName, pollResult.RefreshToken, cancellationToken)
            .ConfigureAwait(false);
        _runtimeSession = new StoredSession(pollResult.AccountName, pollResult.RefreshToken);
        PersistStoredSession(pollResult.AccountName, pollResult.RefreshToken);

        Console.WriteLine($"Logged into Steam as {pollResult.AccountName}.");
    }

    private async Task LoginWithCredentialsAsync(SteamCredentials credentials, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var details = new AuthSessionDetails
        {
            Username = credentials.Username,
            Password = credentials.Password,
            DeviceFriendlyName = DeviceFriendlyName,
            IsPersistentSession = true,
            Authenticator = credentials.Authenticator,
        };

        var session = await _client!.Authentication
            .BeginAuthSessionViaCredentialsAsync(details)
            .ConfigureAwait(false);

        AuthPollResult pollResult;
        try
        {
            pollResult = await session.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            throw new SteamSessionExpiredException($"Steam credential login failed: {ex.Message}");
        }

        await CompleteLogOnAsync(pollResult.AccountName, pollResult.RefreshToken, cancellationToken)
            .ConfigureAwait(false);

        _runtimeSession = new StoredSession(pollResult.AccountName, pollResult.RefreshToken);
        Console.WriteLine($"Logged into Steam as {pollResult.AccountName} via credentials.");
    }

    public StoredSession? GetRuntimeSession() => _runtimeSession;

    public async Task PrepareDownloadsAsync(CancellationToken cancellationToken)
    {
        _ = await GetDepotKeyAsync(cancellationToken).ConfigureAwait(false);
        _ = await GetCdnServerAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DownloadedManifestFiles> DownloadRequiredFilesAsync(
        ulong manifestId,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var filesByPath = await DownloadSelectedFilesAsync(
            manifestId,
            destinationRoot,
            [GameAssemblyManifestPath, MetadataManifestPath, .. MonoAssemblyManifestPaths],
            cancellationToken).ConfigureAwait(false);

        var gameAssemblyPath = TryBuildDownloadedPath(destinationRoot, filesByPath, GameAssemblyManifestPath);
        var metadataPath = TryBuildDownloadedPath(destinationRoot, filesByPath, MetadataManifestPath);
        var monoAssemblyPaths = MonoAssemblyManifestPaths
            .Select(path => TryBuildDownloadedPath(destinationRoot, filesByPath, path))
            .Where(path => path != null)
            .Cast<string>()
            .ToArray();

        if ((gameAssemblyPath == null || metadataPath == null) && monoAssemblyPaths.Length == 0)
        {
            throw new InvalidOperationException(
                "Manifest did not contain a complete IL2CPP pair or a known managed assembly for Mono fallback.");
        }

        return new DownloadedManifestFiles(
            gameAssemblyPath,
            metadataPath,
            monoAssemblyPaths);
    }

    public async Task<string?> DownloadGameManagerFileAsync(
        ulong manifestId,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var filesByPath = await DownloadSelectedFilesAsync(
            manifestId,
            destinationRoot,
            [GameManagerManifestPath],
            cancellationToken).ConfigureAwait(false);

        return TryBuildDownloadedPath(destinationRoot, filesByPath, GameManagerManifestPath);
    }

    private async Task<Dictionary<string, DepotManifest.FileData>> DownloadSelectedFilesAsync(
        ulong manifestId,
        string destinationRoot,
        IReadOnlyCollection<string> requestedManifestPaths,
        CancellationToken cancellationToken)
    {
        if (!_isLoggedOn || _client == null)
            throw new InvalidOperationException("Not logged in to Steam.");

        var steamContent = _client.GetHandler<SteamContent>()
            ?? throw new InvalidOperationException("SteamContent handler unavailable.");

        var requestCode = await steamContent
            .GetManifestRequestCode(ScpSlDepotId, ScpSlAppId, manifestId, "public")
            .ConfigureAwait(false);

        if (requestCode == 0)
        {
            throw new InvalidOperationException(
                $"Steam did not issue a manifest request code for manifest {manifestId}.");
        }

        var depotKey = await GetDepotKeyAsync(cancellationToken).ConfigureAwait(false);
        var cdnServer = await GetCdnServerAsync(cancellationToken).ConfigureAwait(false);

        using var cdnClient = new Client(_client);
        var manifest = await cdnClient
            .DownloadManifestAsync(ScpSlDepotId, manifestId, requestCode, cdnServer, depotKey)
            .ConfigureAwait(false);

        var filesByPath = SelectCandidateFiles(manifest, requestedManifestPaths);
        Directory.CreateDirectory(destinationRoot);

        foreach (var file in filesByPath.Values)
        {
            await DownloadFileAsync(cdnClient, cdnServer, depotKey, file, destinationRoot, cancellationToken)
                .ConfigureAwait(false);
        }

        return filesByPath;
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        SteamClient client;
        CallbackManager callbackManager;

        lock (_gate)
        {
            if (_client != null && _client.IsConnected)
                return;

            try
            {
                _client?.Disconnect();
            }
            catch
            {
                // Best effort reset.
            }

            StopCallbackPump();
            _client = new SteamClient();
            _callbackManager = new CallbackManager(_client);
            _callbackPumpCts = new CancellationTokenSource();

            var pumpToken = _callbackPumpCts.Token;
            var managerForPump = _callbackManager;
            _callbackPumpTask = Task.Run(() =>
            {
                while (!pumpToken.IsCancellationRequested)
                {
                    try
                    {
                        managerForPump.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                    }
                    catch
                    {
                        // Keep the callback pump alive.
                    }
                }
            }, pumpToken);

            _isLoggedOn = false;
            _loggedOnAccountName = null;
            _depotKey = null;
            _cdnServer = null;

            client = _client;
            callbackManager = _callbackManager;
        }

        var connectedTask = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var connectedSub = callbackManager.Subscribe<SteamClient.ConnectedCallback>(_ =>
            connectedTask.TrySetResult(null));
        using var disconnectedSub = callbackManager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
            connectedTask.TrySetException(new IOException("Disconnected from Steam before the connection completed.")));

        client.Connect();

        using var registration = cancellationToken.Register(() => connectedTask.TrySetCanceled(cancellationToken));
        await connectedTask.Task.ConfigureAwait(false);
    }

    private async Task CompleteLogOnAsync(string accountName, string refreshToken, CancellationToken cancellationToken)
    {
        if (_isLoggedOn && string.Equals(_loggedOnAccountName, accountName, StringComparison.Ordinal))
            return;

        var steamUser = _client!.GetHandler<SteamUser>()
            ?? throw new InvalidOperationException("SteamUser handler unavailable.");

        var loggedOnTask = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var loggedOnSub = _callbackManager!.Subscribe<SteamUser.LoggedOnCallback>(callback =>
            loggedOnTask.TrySetResult(callback));
        using var loggedOffSub = _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(callback =>
            loggedOnTask.TrySetException(new IOException($"Steam logged off: {callback.Result}")));
        using var disconnectedSub = _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
            loggedOnTask.TrySetException(new IOException("Disconnected from Steam during logon.")));

        steamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username = accountName,
            AccessToken = refreshToken,
            ShouldRememberPassword = true,
        });

        using var registration = cancellationToken.Register(() => loggedOnTask.TrySetCanceled(cancellationToken));
        var result = await loggedOnTask.Task.ConfigureAwait(false);

        if (result.Result != EResult.OK)
            throw MapLogOnFailure(result.Result);

        _isLoggedOn = true;
        _loggedOnAccountName = accountName;
    }

    private async Task<byte[]> GetDepotKeyAsync(CancellationToken cancellationToken)
    {
        if (_depotKey != null)
            return _depotKey;

        var steamApps = _client!.GetHandler<SteamApps>()
            ?? throw new InvalidOperationException("SteamApps handler unavailable.");

        var depotKeyResult = await steamApps
            .GetDepotDecryptionKey(ScpSlDepotId, ScpSlAppId)
            .ToTask()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (depotKeyResult.Result == EResult.AccessDenied || depotKeyResult.Result == EResult.NoMatch)
            throw new InvalidOperationException("The logged-in Steam account does not own SCP:SL.");

        if (depotKeyResult.Result != EResult.OK)
            throw new InvalidOperationException($"GetDepotDecryptionKey failed: {depotKeyResult.Result}");

        _depotKey = depotKeyResult.DepotKey;
        return _depotKey;
    }

    private async Task<Server> GetCdnServerAsync(CancellationToken cancellationToken)
    {
        if (_cdnServer != null)
            return _cdnServer;

        var steamContent = _client!.GetHandler<SteamContent>()
            ?? throw new InvalidOperationException("SteamContent handler unavailable.");

        var servers = await steamContent.GetServersForSteamPipe().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        _cdnServer = servers?.FirstOrDefault()
            ?? throw new InvalidOperationException("No Steam CDN servers were returned.");

        return _cdnServer;
    }

    private static Dictionary<string, DepotManifest.FileData> SelectCandidateFiles(
        DepotManifest manifest,
        IReadOnlyCollection<string> requestedManifestPaths)
    {
        var filesByPath = new Dictionary<string, DepotManifest.FileData>(StringComparer.OrdinalIgnoreCase);
        var requestedNormalizedPaths = requestedManifestPaths
            .Select(NormalizeManifestPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.Files ?? [])
        {
            if ((file.Flags & EDepotFileFlag.Directory) != 0 || (file.Flags & EDepotFileFlag.Symlink) != 0)
                continue;

            var normalizedPath = NormalizeManifestPath(file.FileName);
            if (CandidateManifestPaths.Contains(normalizedPath) && requestedNormalizedPaths.Contains(normalizedPath))
                filesByPath[normalizedPath] = file;
        }

        return filesByPath;
    }

    private static string? TryBuildDownloadedPath(
        string destinationRoot,
        IReadOnlyDictionary<string, DepotManifest.FileData> filesByPath,
        string manifestPath)
    {
        var normalizedPath = NormalizeManifestPath(manifestPath);
        if (!filesByPath.ContainsKey(normalizedPath))
            return null;

        return Path.Combine(destinationRoot, normalizedPath.Replace('\\', Path.DirectorySeparatorChar));
    }

    private static async Task DownloadFileAsync(
        Client cdnClient,
        Server cdnServer,
        byte[] depotKey,
        DepotManifest.FileData file,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var relativePath = NormalizeManifestPath(file.FileName);
        Console.WriteLine($"  [{Path.GetFileName(destinationRoot)}] Downloading {relativePath} ({file.TotalSize:N0} bytes)");

        var outputPath = Path.Combine(destinationRoot, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        var parentDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(parentDirectory))
            Directory.CreateDirectory(parentDirectory);

        if (file.Chunks == null || file.Chunks.Count == 0)
        {
            await using var _ = File.Create(outputPath);
            return;
        }

        await using var outputStream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        outputStream.SetLength((long)file.TotalSize);

        foreach (var chunk in file.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);
            try
            {
                int written;
                try
                {
                    written = await cdnClient
                        .DownloadDepotChunkAsync(ScpSlDepotId, chunk, cdnServer, buffer, depotKey)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new IOException($"Chunk download failed for {relativePath}: {ex.Message}", ex);
                }

                outputStream.Position = (long)chunk.Offset;
                await outputStream.WriteAsync(buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static string NormalizeManifestPath(string path)
    {
        return path.Replace('/', '\\').TrimStart('\\');
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Printing the URL is enough if the browser launch fails.
        }
    }

    private static Exception MapLogOnFailure(EResult result)
    {
        return result switch
        {
            EResult.InvalidPassword => new SteamSessionExpiredException("Stored Steam token was rejected."),
            EResult.AccessDenied => new SteamSessionExpiredException("Stored Steam token was denied."),
            EResult.Revoked => new SteamSessionExpiredException("Stored Steam token was revoked."),
            EResult.Expired => new SteamSessionExpiredException("Stored Steam token expired."),
            EResult.InvalidSignature => new SteamSessionExpiredException("Stored Steam token signature was invalid."),
            EResult.AccountLogonDenied => new SteamSessionExpiredException("Stored Steam token was not accepted."),
            EResult.NoConnection => new IOException("No connection to Steam."),
            EResult.Timeout => new IOException("Steam login timed out."),
            _ => new InvalidOperationException($"Steam logon failed: {result}"),
        };
    }

    private static StoredSession? LoadEnvRefreshTokenSession()
    {
        var json = Environment.GetEnvironmentVariable(EnvRefreshTokenJson);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var session = JsonSerializer.Deserialize<StoredSession>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (session == null
                || string.IsNullOrWhiteSpace(session.AccountName)
                || string.IsNullOrWhiteSpace(session.RefreshToken))
            {
                Console.WriteLine($"{EnvRefreshTokenJson} did not contain accountName + refreshToken; ignoring.");
                return null;
            }

            return session;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to parse {EnvRefreshTokenJson}: {ex.Message}");
            return null;
        }
    }

    private static SteamCredentials? LoadEnvCredentials()
    {
        var username = Environment.GetEnvironmentVariable(EnvSteamUsername);
        var password = Environment.GetEnvironmentVariable(EnvSteamPassword);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        IAuthenticator? authenticator = null;
        var sharedSecret = Environment.GetEnvironmentVariable(EnvSteamSharedSecret);
        if (!string.IsNullOrWhiteSpace(sharedSecret))
        {
            try
            {
                authenticator = new SteamGuardAuthenticator(sharedSecret);
            }
            catch (Exception ex)
            {
                throw new SteamSessionExpiredException(
                    $"{EnvSteamSharedSecret} is set but could not be parsed as base64: {ex.Message}");
            }
        }

        return new SteamCredentials(username.Trim(), password, authenticator);
    }

    private static StoredSession? LoadStoredSession()
    {
        try
        {
            if (!File.Exists(SessionFilePath))
                return null;

            var bytes = File.ReadAllBytes(SessionFilePath);
            var plaintext = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)
                : bytes;

            return JsonSerializer.Deserialize<StoredSession>(plaintext);
        }
        catch
        {
            return null;
        }
    }

    private void PersistStoredSession(string accountName, string refreshToken)
    {
        if (_disableSessionPersistence)
            return;

        try
        {
            Directory.CreateDirectory(SessionDirectory);

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(new StoredSession(accountName, refreshToken));
            var bytesToStore = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser)
                : plaintext;

            File.WriteAllBytes(SessionFilePath, bytesToStore);
        }
        catch
        {
            // Best effort persistence.
        }
    }

    private static void DeleteStoredSession()
    {
        try
        {
            if (File.Exists(SessionFilePath))
                File.Delete(SessionFilePath);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private void ResetSession()
    {
        lock (_gate)
        {
            try
            {
                _client?.Disconnect();
            }
            catch
            {
                // Best effort cleanup.
            }

            StopCallbackPump();
            _client = null;
            _callbackManager = null;
            _isLoggedOn = false;
            _loggedOnAccountName = null;
            _depotKey = null;
            _cdnServer = null;
        }
    }

    private void StopCallbackPump()
    {
        try
        {
            _callbackPumpCts?.Cancel();
        }
        catch
        {
            // Best effort cleanup.
        }

        try
        {
            _callbackPumpTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown races.
        }

        _callbackPumpCts?.Dispose();
        _callbackPumpCts = null;
        _callbackPumpTask = null;
    }

    public void Dispose()
    {
        ResetSession();
    }

    public sealed record StoredSession(string AccountName, string RefreshToken);

    private sealed record SteamCredentials(string Username, string Password, IAuthenticator? Authenticator);
}

internal sealed record DownloadedManifestFiles(
    string? GameAssemblyPath,
    string? MetadataPath,
    IReadOnlyList<string> MonoAssemblyPaths)
{
    public bool HasIl2CppInputs =>
        !string.IsNullOrWhiteSpace(GameAssemblyPath) &&
        !string.IsNullOrWhiteSpace(MetadataPath);

    public bool HasMonoInputs => MonoAssemblyPaths.Count > 0;
}

internal sealed class SteamSessionExpiredException(string message) : Exception(message);
