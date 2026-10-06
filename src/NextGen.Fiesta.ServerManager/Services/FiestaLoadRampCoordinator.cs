using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Controlled load ramp for the certified 2000-player Zone profile.
/// A stage is PASS only when the headless clients reached the real Zone protocol path and
/// the running Zone's ShinePlayer l_ListNum equals baseline + simulated ready clients.
/// </summary>
public sealed class FiestaLoadRampCoordinator
{
    private readonly ZonePoolRuntimeTestObserver _runtimeObserver = new();

    public async Task<FiestaLoadRampResult> RunAsync(
        FiestaLoadRampOptions options,
        Action<FiestaLoadRampProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var credentials = FiestaLoadCredentialManifest.Load(options.CredentialManifestPath);
        var targets = options.StageTargets
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var finalTarget = targets[^1];
        if (finalTarget > ZonePoolOfflineWriterSelfTest.PlayerTarget)
            return FiestaLoadRampResult.CreateBlocked(
                $"Ziel {finalTarget:N0} überschreitet den zertifizierten ShinePlayer-Pool {ZonePoolOfflineWriterSelfTest.PlayerTarget:N0}.");

        if (credentials.Clients.Count < finalTarget)
            return FiestaLoadRampResult.CreateBlocked(
                $"Credential-Manifest enthält nur {credentials.Clients.Count:N0} eindeutige Clients; benötigt werden {finalTarget:N0}.");

        if (credentials.Clients.Take(finalTarget).Any(x => x.CreateCharacterIfMissing)
            && string.IsNullOrWhiteSpace(options.ClientOptions.CharacterCreateTemplatePath))
        {
            return FiestaLoadRampResult.CreateBlocked(
                "Mindestens ein Testaccount benötigt Auto-Create, aber CharacterCreateTemplatePath fehlt.");
        }

        var baseline = _runtimeObserver.Observe(options.TargetZoneExePath, minimumUptimeSeconds: 0);
        if (!baseline.Passed || baseline.Pools is null)
            return FiestaLoadRampResult.CreateBlocked(
                "Runtime-Baseline ist nicht zertifiziert bereit: " + baseline.Detail);

        if (baseline.Pools.PlayerLimit != ZonePoolOfflineWriterSelfTest.PlayerTarget)
            return FiestaLoadRampResult.CreateBlocked(
                $"Runtime ShinePlayer-Limit ist {baseline.Pools.PlayerLimit:N0} statt {ZonePoolOfflineWriterSelfTest.PlayerTarget:N0}.");

        var baselinePlayers = baseline.Pools.PlayerCount;
        if (options.RequireEmptyBaseline && baselinePlayers != 0)
            return FiestaLoadRampResult.CreateBlocked(
                $"Für den isolierten Lasttest muss die Zone leer sein; aktuell {baselinePlayers:N0} Player.");

        var ready = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var failed = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<Task<FiestaHeadlessProbeResult>>(finalTarget);
        var stageResults = new List<FiestaLoadRampStageResult>(targets.Length);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        progress?.Invoke(new FiestaLoadRampProgress(
            "BASELINE",
            0,
            0,
            baselinePlayers,
            $"Baseline Player={baselinePlayers:N0}, Limit={baseline.Pools.PlayerLimit:N0}."));

        try
        {
            var started = 0;
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (failed.Count > 0)
                    return BuildFailure(
                        baselinePlayers,
                        stageResults,
                        ready.Count,
                        failed,
                        $"Vor Stufe {target:N0} ist mindestens ein Client ausgefallen.");

                var toStart = target - started;
                if (toStart <= 0)
                    continue;

                progress?.Invoke(new FiestaLoadRampProgress(
                    "STARTING",
                    target,
                    ready.Count,
                    baselinePlayers + ready.Count,
                    $"Starte {toStart:N0} weitere Clients für Zielstufe {target:N0}."));

                for (var index = started; index < target; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var credential = credentials.Clients[index];
                    var client = new FiestaHeadlessLoadClient();
                    var clientOptions = CloneClientOptions(options.ClientOptions, options.SessionHoldDuration);

                    var task = client.ProbeAndHoldAsync(
                        clientOptions,
                        credential,
                        sessionCts.Token,
                        p =>
                        {
                            if (p.Failed)
                            {
                                failed[p.Username] = $"{p.Stage}: {p.Detail}";
                                progress?.Invoke(new FiestaLoadRampProgress(
                                    "CLIENT-FAIL",
                                    target,
                                    ready.Count,
                                    baselinePlayers + ready.Count,
                                    $"{p.Username}/{p.CharacterName}: {p.Stage} · {p.Detail}"));
                                return;
                            }

                            if (p.Stage is FiestaLoadClientStage.ClientReady or FiestaLoadClientStage.Holding)
                            {
                                ready.TryAdd(p.Username, 0);
                            }

                            progress?.Invoke(new FiestaLoadRampProgress(
                                p.Stage.ToString().ToUpperInvariant(),
                                target,
                                ready.Count,
                                baselinePlayers + ready.Count,
                                $"{p.Username}/{p.CharacterName}: {p.Stage}"));
                        });

                    tasks.Add(task);

                    if (options.ClientStartInterval > TimeSpan.Zero)
                        await Task.Delay(options.ClientStartInterval, cancellationToken);
                }

                started = target;

                var readyDeadline = DateTime.UtcNow + options.StageReadyTimeout;
                while (ready.Count < target)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (failed.Count > 0)
                    {
                        return BuildFailure(
                            baselinePlayers,
                            stageResults,
                            ready.Count,
                            failed,
                            $"Clientfehler während Aufbau der Stufe {target:N0}.");
                    }

                    if (DateTime.UtcNow >= readyDeadline)
                    {
                        return BuildFailure(
                            baselinePlayers,
                            stageResults,
                            ready.Count,
                            failed,
                            $"Timeout: Nur {ready.Count:N0}/{target:N0} Clients erreichten ClientReady/Holding.");
                    }

                    progress?.Invoke(new FiestaLoadRampProgress(
                        "WAIT-READY",
                        target,
                        ready.Count,
                        baselinePlayers + ready.Count,
                        $"Warte auf ClientReady: {ready.Count:N0}/{target:N0}."));

                    await Task.Delay(options.ReadyPollInterval, cancellationToken);
                }

                if (options.StageSettleTime > TimeSpan.Zero)
                    await Task.Delay(options.StageSettleTime, cancellationToken);

                if (failed.Count > 0)
                {
                    return BuildFailure(
                        baselinePlayers,
                        stageResults,
                        ready.Count,
                        failed,
                        $"Clientfehler unmittelbar vor Serververifikation der Stufe {target:N0}.");
                }

                var observation = _runtimeObserver.Observe(options.TargetZoneExePath, minimumUptimeSeconds: 0);
                var expectedPlayers = baselinePlayers + target;
                if (!observation.Passed || observation.Pools is null)
                {
                    return BuildFailure(
                        baselinePlayers,
                        stageResults,
                        ready.Count,
                        failed,
                        $"Runtime-Observer blockierte Stufe {target:N0}: {observation.Detail}");
                }

                var actualPlayers = observation.Pools.PlayerCount;
                var stagePassed = actualPlayers == expectedPlayers;
                stageResults.Add(new FiestaLoadRampStageResult
                {
                    TargetClients = target,
                    ReadyClients = ready.Count,
                    ExpectedServerPlayers = expectedPlayers,
                    ActualServerPlayers = actualPlayers,
                    PlayerLimit = observation.Pools.PlayerLimit,
                    Passed = stagePassed,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Detail = stagePassed
                        ? $"PASS: {target:N0} Simulatoren ready, ShinePlayer {actualPlayers:N0}/{observation.Pools.PlayerLimit:N0}."
                        : $"FAIL: Simulatoren ready={ready.Count:N0}; ShinePlayer erwartet {expectedPlayers:N0}, gemessen {actualPlayers:N0}."
                });

                progress?.Invoke(new FiestaLoadRampProgress(
                    stagePassed ? "STAGE-PASS" : "STAGE-FAIL",
                    target,
                    ready.Count,
                    actualPlayers,
                    stageResults[^1].Detail));

                if (!stagePassed)
                {
                    return BuildFailure(
                        baselinePlayers,
                        stageResults,
                        ready.Count,
                        failed,
                        stageResults[^1].Detail);
                }
            }

            if (options.FinalStabilityDuration > TimeSpan.Zero)
            {
                var finalExpected = baselinePlayers + finalTarget;
                var deadline = DateTime.UtcNow + options.FinalStabilityDuration;
                var samples = 0;

                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (failed.Count > 0)
                    {
                        return BuildFailure(
                            baselinePlayers,
                            stageResults,
                            ready.Count,
                            failed,
                            "Clientausfall während finaler Stabilitätsphase.");
                    }

                    var observation = _runtimeObserver.Observe(options.TargetZoneExePath, minimumUptimeSeconds: 0);
                    if (!observation.Passed || observation.Pools is null)
                    {
                        return BuildFailure(
                            baselinePlayers,
                            stageResults,
                            ready.Count,
                            failed,
                            "Runtime-Observer blockierte die finale Stabilitätsphase: " + observation.Detail);
                    }

                    samples++;
                    if (observation.Pools.PlayerCount != finalExpected || ready.Count != finalTarget)
                    {
                        return BuildFailure(
                            baselinePlayers,
                            stageResults,
                            ready.Count,
                            failed,
                            $"Stabilität fehlgeschlagen: Ready {ready.Count:N0}/{finalTarget:N0}, ShinePlayer {observation.Pools.PlayerCount:N0}/{finalExpected:N0}.");
                    }

                    progress?.Invoke(new FiestaLoadRampProgress(
                        "STABILITY",
                        finalTarget,
                        ready.Count,
                        observation.Pools.PlayerCount,
                        $"Stabilität Sample {samples}: ShinePlayer {observation.Pools.PlayerCount:N0}/{observation.Pools.PlayerLimit:N0}."));

                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        break;
                    await Task.Delay(
                        remaining < options.StabilityPollInterval ? remaining : options.StabilityPollInterval,
                        cancellationToken);
                }
            }

            return new FiestaLoadRampResult
            {
                Passed = true,
                Status = "PASS",
                BaselinePlayers = baselinePlayers,
                FinalReadyClients = ready.Count,
                FinalExpectedPlayers = baselinePlayers + finalTarget,
                StageResults = stageResults,
                Detail =
                    $"LOAD RAMP PASS · {finalTarget:N0} echte Headless-Sessions · " +
                    $"ShinePlayer {baselinePlayers + finalTarget:N0}/{ZonePoolOfflineWriterSelfTest.PlayerTarget:N0} · " +
                    $"{stageResults.Count} Laststufen serverseitig verifiziert."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new FiestaLoadRampResult
            {
                Cancelled = true,
                Status = "CANCELLED",
                BaselinePlayers = baselinePlayers,
                FinalReadyClients = ready.Count,
                StageResults = stageResults,
                Detail = $"Load-Ramp abgebrochen; Ready={ready.Count:N0}."
            };
        }
        finally
        {
            sessionCts.Cancel();
            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15)); }
                catch { }
            }
        }
    }

    private static FiestaLoadRampResult BuildFailure(
        int baselinePlayers,
        IReadOnlyList<FiestaLoadRampStageResult> stages,
        int readyCount,
        ConcurrentDictionary<string, string> failed,
        string detail)
    {
        var failures = failed
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(x => $"{x.Key}: {x.Value}")
            .ToArray();

        return new FiestaLoadRampResult
        {
            Blocked = true,
            Status = "BLOCKED",
            BaselinePlayers = baselinePlayers,
            FinalReadyClients = readyCount,
            StageResults = stages.ToArray(),
            FailedClientCount = failed.Count,
            FailedClientSamples = failures,
            Detail = failed.Count == 0
                ? "LOAD RAMP BLOCKED · " + detail
                : "LOAD RAMP BLOCKED · " + detail + " · " + string.Join(" | ", failures)
        };
    }

    private static FiestaHeadlessProbeOptions CloneClientOptions(
        FiestaHeadlessProbeOptions source,
        TimeSpan holdDuration)
        => new()
        {
            LoginHost = source.LoginHost,
            LoginPort = source.LoginPort,
            WorldId = source.WorldId,
            ClientYear = source.ClientYear,
            ClientVersion = source.ClientVersion,
            ClientTag = source.ClientTag,
            FileHash = source.FileHash,
            ZoneTransferTemplatePath = source.ZoneTransferTemplatePath,
            CharacterCreateTemplatePath = source.CharacterCreateTemplatePath,
            AllowEmulatorSizedZoneTransfer = false,
            StepTimeout = source.StepTimeout,
            ZoneLoginTimeout = source.ZoneLoginTimeout,
            HoldDuration = holdDuration
        };
}

public sealed class FiestaLoadRampOptions
{
    public string TargetZoneExePath { get; init; } = string.Empty;
    public string CredentialManifestPath { get; init; } = string.Empty;
    public FiestaHeadlessProbeOptions ClientOptions { get; init; } = new();
    public IReadOnlyList<int> StageTargets { get; init; } =
        new[] { 1, 10, 100, 500, 1000, 1450, 1510, 1600 };
    public bool RequireEmptyBaseline { get; init; } = true;
    public TimeSpan ClientStartInterval { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan StageReadyTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ReadyPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan StageSettleTime { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SessionHoldDuration { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan FinalStabilityDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StabilityPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TargetZoneExePath) || !File.Exists(TargetZoneExePath))
            throw new FileNotFoundException("TargetZoneExePath wurde nicht gefunden.", TargetZoneExePath);
        if (string.IsNullOrWhiteSpace(CredentialManifestPath) || !File.Exists(CredentialManifestPath))
            throw new FileNotFoundException("CredentialManifestPath wurde nicht gefunden.", CredentialManifestPath);
        ClientOptions.Validate();

        if (string.IsNullOrWhiteSpace(ClientOptions.ZoneTransferTemplatePath))
            throw new ArgumentException("Für Originalserver-Lasttests ist ZoneTransferTemplatePath zwingend.");
        if (StageTargets.Count == 0 || StageTargets.Any(x => x <= 0))
            throw new ArgumentException("StageTargets muss positive Werte enthalten.");
        if (ClientStartInterval < TimeSpan.Zero
            || StageReadyTimeout <= TimeSpan.Zero
            || ReadyPollInterval <= TimeSpan.Zero
            || StageSettleTime < TimeSpan.Zero
            || SessionHoldDuration <= TimeSpan.Zero
            || FinalStabilityDuration < TimeSpan.Zero
            || StabilityPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("Rampenzeiten enthalten ungültige Werte.");
        }

        if (SessionHoldDuration <= StageReadyTimeout + FinalStabilityDuration)
            throw new ArgumentException("SessionHoldDuration muss länger als StageReadyTimeout + FinalStabilityDuration sein.");
    }
}

public sealed class FiestaLoadCredentialManifest
{
    public const string FormatV1 = "NextGen.NA2016.LoadCredentials.v1";

    public string Format { get; init; } = FormatV1;
    public List<FiestaLoadClientCredential> Clients { get; init; } = new();

    public static FiestaLoadCredentialManifest Load(string path)
    {
        var manifest = JsonSerializer.Deserialize<FiestaLoadCredentialManifest>(
                           File.ReadAllText(path, Encoding.UTF8))
                       ?? throw new InvalidDataException("Credential-Manifest ist leer oder ungültig.");
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (!string.Equals(Format, FormatV1, StringComparison.Ordinal))
            throw new InvalidDataException($"Unbekanntes Credential-Manifestformat '{Format}'.");
        if (Clients.Count == 0)
            throw new InvalidDataException("Credential-Manifest enthält keine Clients.");

        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in Clients)
        {
            client.Validate();
            if (!usernames.Add(client.Username))
                throw new InvalidDataException($"Doppelter Username im Lasttest: {client.Username}");
            if (!characters.Add(client.CharacterName))
                throw new InvalidDataException($"Doppelter Charaktername im Lasttest: {client.CharacterName}");
        }
    }
}

public sealed class FiestaLoadRampResult
{
    public bool Passed { get; init; }
    public bool Blocked { get; init; }
    public bool Cancelled { get; init; }
    public string Status { get; init; } = "BLOCKED";
    public int BaselinePlayers { get; init; }
    public int FinalReadyClients { get; init; }
    public int FinalExpectedPlayers { get; init; }
    public int FailedClientCount { get; init; }
    public IReadOnlyList<string> FailedClientSamples { get; init; } = Array.Empty<string>();
    public IReadOnlyList<FiestaLoadRampStageResult> StageResults { get; init; } = Array.Empty<FiestaLoadRampStageResult>();
    public string Detail { get; init; } = string.Empty;

    public static FiestaLoadRampResult CreateBlocked(string detail)
        => new() { Blocked = true, Status = "BLOCKED", Detail = "LOAD RAMP BLOCKED · " + detail };
}

public sealed class FiestaLoadRampStageResult
{
    public int TargetClients { get; init; }
    public int ReadyClients { get; init; }
    public int ExpectedServerPlayers { get; init; }
    public int ActualServerPlayers { get; init; }
    public int PlayerLimit { get; init; }
    public bool Passed { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public readonly record struct FiestaLoadRampProgress(
    string Phase,
    int TargetClients,
    int ReadyClients,
    int ServerPlayers,
    string Detail);
