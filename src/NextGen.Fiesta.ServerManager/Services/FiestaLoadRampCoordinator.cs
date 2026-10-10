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
        if (!credentials.IsLoginAutoRegistrationCompatible(out var credentialCompatibility))
        {
            return FiestaLoadRampResult.CreateBlocked(
                "Credential-Preflight: " + credentialCompatibility);
        }

        var targets = options.StageTargets
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var finalTarget = targets[^1];
        if (finalTarget > ZonePoolOfflineWriterSelfTest.PlayerTarget)
            return FiestaLoadRampResult.CreateBlocked(
                $"Ziel {finalTarget:N0} überschreitet den zertifizierten ShinePlayer-Pool {ZonePoolOfflineWriterSelfTest.PlayerTarget:N0}.");

        if (options.PriorityCredentialNumber > credentials.Clients.Count)
            return FiestaLoadRampResult.CreateBlocked(
                $"B-Diagnoseidentität #{options.PriorityCredentialNumber} existiert nicht; " +
                $"Credential-Manifest enthält {credentials.Clients.Count:N0} Identitäten.");

        if (credentials.Clients.Count - options.CredentialStartIndex < finalTarget)
            return FiestaLoadRampResult.CreateBlocked(
                $"Credential-Manifest enthält {credentials.Clients.Count:N0} eindeutige Clients; " +
                $"Startindex {options.CredentialStartIndex + 1:N0} und Ziel {finalTarget:N0} passen nicht in das Manifest.");

        if (Enumerable.Range(0, finalTarget)
            .Any(index => credentials.Clients[options.ResolveCredentialIndex(index)].CreateCharacterIfMissing)
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

        var listener = new ZoneClientListenerTestConfiguration().VerifyApplied(options.TargetZoneExePath);
        if (!listener.Verified || listener.MaxAccept != ZoneClientListenerTestConfiguration.CertifiedMaxAccept)
            return FiestaLoadRampResult.CreateBlocked(
                "Zertifizierter Zone-Listener 2000 ist nicht aktiv/verifiziert: " + listener.Detail);

        var baselinePlayers = baseline.Pools.PlayerCount;
        if (options.RequireEmptyBaseline && baselinePlayers != 0)
            return FiestaLoadRampResult.CreateBlocked(
                $"Für den isolierten Lasttest muss die Zone leer sein; aktuell {baselinePlayers:N0} Player.");

        // A local, credential-free trace is useful when the stock NA2016 services log no failure.
        // A bounded progress filter avoids writing per-client heartbeat traffic during long holds.
        if (!string.IsNullOrWhiteSpace(options.DiagnosticsPath))
        {
            var traceFolder = Path.GetDirectoryName(Path.GetFullPath(options.DiagnosticsPath));
            if (!string.IsNullOrWhiteSpace(traceFolder))
                Directory.CreateDirectory(traceFolder);
        }
        using var diagnostics = string.IsNullOrWhiteSpace(options.DiagnosticsPath)
            ? null
            : new StreamWriter(options.DiagnosticsPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        var diagnosticLock = new object();
        var diagnosticsClosed = false;
        void Trace(string phase, string detail)
        {
            if (diagnostics is null)
                return;
            lock (diagnosticLock)
            {
                if (diagnosticsClosed)
                    return;
                try
                {
                    var safeDetail = detail.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
                    diagnostics.WriteLine($"{DateTimeOffset.UtcNow:O}\t{phase}\t{safeDetail}");
                }
                catch (IOException)
                {
                    // Diagnostic output must not alter the live admission/capacity verdict.
                }
            }
        }

        var ready = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var failed = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lastZoneHeartbeatRoundTrip = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var lastClientProgress = new ConcurrentDictionary<string, FiestaHeadlessClientProgress>(StringComparer.OrdinalIgnoreCase);
        var deferredZoneAdmissions = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<Task<FiestaHeadlessProbeResult>>(finalTarget);
        var stageResults = new List<FiestaLoadRampStageResult>(targets.Length);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Trace("BASELINE",
            $"CredentialStartIndex={options.CredentialStartIndex}; PriorityCredentialNumber={options.PriorityCredentialNumber}; " +
            $"ZonePlayer={baselinePlayers}; PoolLimit={baseline.Pools.PlayerLimit}; " +
            $"Listener={listener.MaxAccept}; DelayedZoneReentryAttempts={options.ClientOptions.DelayedZoneReentryAttempts}; " +
            $"StageTargets={string.Join(",", targets)}; " +
            $"StartIntervalMs={options.ClientStartInterval.TotalMilliseconds:N0}; HoldSec={options.SessionHoldDuration.TotalSeconds:N0}");
        progress?.Invoke(new FiestaLoadRampProgress(
            "BASELINE",
            0,
            0,
            baselinePlayers,
            $"Baseline Player={baselinePlayers:N0}, Limit={baseline.Pools.PlayerLimit:N0} · Listener {listener.MaxAccept:N0} verifiziert."));

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

                Trace("STAGE-START", $"Target={target}; Ready={ready.Count}; AdditionalClients={toStart}");
                progress?.Invoke(new FiestaLoadRampProgress(
                    "STARTING",
                    target,
                    ready.Count,
                    baselinePlayers + ready.Count,
                    $"Starte {toStart:N0} weitere Clients für Zielstufe {target:N0}."));

                for (var index = started; index < target; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var credential = credentials.Clients[options.ResolveCredentialIndex(index)];
                    var client = new FiestaHeadlessLoadClient();
                    var clientOptions = CloneClientOptions(options.ClientOptions, options.SessionHoldDuration);

                    var task = client.ProbeAndHoldAsync(
                        clientOptions,
                        credential,
                        sessionCts.Token,
                        p =>
                        {
                            // Cancellation of intentionally held sessions is cleanup,
                            // not evidence of a server-side admission failure.
                            if (p.Failed
                                && sessionCts.IsCancellationRequested
                                && string.Equals(p.Detail, "Abgebrochen", StringComparison.Ordinal))
                                return;

                            lastClientProgress[p.Username] = p;
                            if (p.Detail.StartsWith("RAMP_ZONE_REENTRY_RETRY", StringComparison.Ordinal))
                                deferredZoneAdmissions.TryAdd(p.Username, 0);

                            if (p.Failed
                                || p.Detail.StartsWith("RAMP_LOGIN_RETRY", StringComparison.Ordinal)
                                || p.Stage is FiestaLoadClientStage.LoginConnected
                                    or FiestaLoadClientStage.VersionAccepted
                                    or FiestaLoadClientStage.LoginAuthenticated
                                    or FiestaLoadClientStage.WorldRedirectReceived
                                    or FiestaLoadClientStage.WorldConnected
                                    or FiestaLoadClientStage.ZoneRedirectReceived
                                    or FiestaLoadClientStage.ZoneConnected
                                    or FiestaLoadClientStage.ZoneAuthenticated
                                    or FiestaLoadClientStage.ClientReady)
                            {
                                var phase = p.Failed
                                    ? "CLIENT-FAIL"
                                    : p.Detail.StartsWith("RAMP_LOGIN_RETRY", StringComparison.Ordinal)
                                        ? "LOGIN-RETRY"
                                        : p.Detail.StartsWith("RAMP_ZONE_REENTRY_RETRY", StringComparison.Ordinal)
                                            ? "ZONE-DEFERRED-REENTRY"
                                            : p.Stage.ToString().ToUpperInvariant();
                                Trace(phase, $"{p.Username}/{p.CharacterName}: " +
                                    $"Stage={p.Stage}; {p.Detail}");
                            }

                            if (p.Failed)
                            {
                                ready.TryRemove(p.Username, out _);
                                failed[p.Username] = $"{p.Stage}: {p.Detail}";
                                progress?.Invoke(new FiestaLoadRampProgress(
                                    "CLIENT-FAIL",
                                    target,
                                    ready.Count,
                                    baselinePlayers + ready.Count,
                                    $"{p.Username}/{p.CharacterName}: {p.Stage} · {p.Detail}"));
                                return;
                            }

                            var isHeartbeatRoundTrip =
                                p.Stage == FiestaLoadClientStage.Holding
                                && p.Detail.StartsWith("ZONE_HEARTBEAT_ROUNDTRIP", StringComparison.Ordinal);
                            if (isHeartbeatRoundTrip)
                            {
                                lastZoneHeartbeatRoundTrip[p.Username] = p.TimestampUtc;
                                // Keep high-frequency heartbeat telemetry internal. It is surfaced
                                // only when a stage fails, so hundreds of sessions do not flood WPF.
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

                var expectedPlayers = baselinePlayers + target;
                ZonePoolRuntimeTestObservation? observation = null;
                var exactSamples = 0;
                var verificationSamples = 0;

                // Do not fail a stage from one instantaneous PDB/runtime sample. Require two
                // consecutive exact ShinePlayer samples, while still failing hard if a client
                // actually drops or the exact count never converges within five seconds.
                for (var sample = 0; sample < 5; sample++)
                {
                    if (failed.Count > 0 || ready.Count != target)
                        break;

                    observation = _runtimeObserver.Observe(options.TargetZoneExePath, minimumUptimeSeconds: 0);
                    if (!observation.Passed || observation.Pools is null)
                    {
                        return BuildFailure(
                            baselinePlayers,
                            stageResults,
                            ready.Count,
                            failed,
                            $"Runtime-Observer blockierte Stufe {target:N0}: {observation.Detail}");
                    }

                    verificationSamples++;
                    if (observation.Pools.PlayerCount == expectedPlayers)
                    {
                        exactSamples++;
                        if (exactSamples >= 2)
                            break;
                    }
                    else
                    {
                        exactSamples = 0;
                    }

                    if (sample < 4)
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }

                if (observation is null || observation.Pools is null)
                {
                    return BuildFailure(
                        baselinePlayers,
                        stageResults,
                        ready.Count,
                        failed,
                        $"Keine gültige Serverbeobachtung für Stufe {target:N0}.");
                }

                var actualPlayers = observation.Pools.PlayerCount;
                var stagePassed =
                    failed.Count == 0
                    && ready.Count == target
                    && actualPlayers == expectedPlayers
                    && exactSamples >= 2;

                Trace(stagePassed ? "STAGE-PASS" : "STAGE-FAIL",
                    $"Target={target}; Ready={ready.Count}; ExpectedShinePlayer={expectedPlayers}; " +
                    $"ActualShinePlayer={actualPlayers}; ExactSamples={exactSamples}; Samples={verificationSamples}; " +
                    $"DeferredZoneReentries={deferredZoneAdmissions.Count}");
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
                        ? $"PASS: {target:N0} Simulatoren ready, ShinePlayer {actualPlayers:N0}/{observation.Pools.PlayerLimit:N0} · {exactSamples} exakte Samples." +
                          (deferredZoneAdmissions.IsEmpty ? "" :
                              $" DIAGNOSE: {deferredZoneAdmissions.Count:N0} Identität(en) benötigten eine verzögerte vollständige Zone-Nachaufnahme.")
                        : $"FAIL: Simulatoren ready={ready.Count:N0}; ShinePlayer erwartet {expectedPlayers:N0}, gemessen {actualPlayers:N0} nach {verificationSamples} Verifikations-Samples. " +
                          BuildHeartbeatMismatchDetail(
                              ready,
                              lastZoneHeartbeatRoundTrip,
                              lastClientProgress,
                              credentials.Clients,
                              DateTimeOffset.UtcNow)
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

            Trace("RAMP-PASS",
                $"FinalReady={ready.Count}; Target={finalTarget}; Stages={stageResults.Count}; " +
                $"DeferredZoneReentries={deferredZoneAdmissions.Count}; " +
                $"DiagnosticMode={options.ClientOptions.DelayedZoneReentryAttempts > 0}");
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
                    $"{stageResults.Count} Laststufen serverseitig verifiziert." +
                    (deferredZoneAdmissions.IsEmpty ? "" :
                        $" DIAGNOSE: {deferredZoneAdmissions.Count:N0} Zone-Nachaufnahmen; " +
                        "dieser Lauf war kein sauberer Erstaufnahme-Baseline-Test.")
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
            // Sample the live Zone BEFORE cancelling held clients. Otherwise a failed
            // ramp only reports client Ready, not the actual ShinePlayer occupancy at failure.
            if (failed.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var onFailure = _runtimeObserver.Observe(options.TargetZoneExePath, minimumUptimeSeconds: 0);
                    // A Zone can already allocate ShinePlayer for an in-flight CH6/1
                    // while our client still waits for the authoritative SH6/2/CH6/3
                    // pair. Report this explicitly; it is NOT a PASS without ClientReady.
                    var notYetReady = Math.Max(0, tasks.Count - ready.Count - failed.Count);
                    var serverMinusReady = onFailure.Pools is null
                        ? "<unavailable>"
                        : (onFailure.Pools.PlayerCount - baselinePlayers - ready.Count).ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                    Trace("FAIL-LIVE-ZONE",
                        $"Ready={ready.Count}; Failed={failed.Count}; " +
                        $"Started={tasks.Count}; StartedNeitherReadyNorFailed={notYetReady}; " +
                        $"ServerMinusReady={serverMinusReady}; " +
                        $"ShinePlayer={(onFailure.Pools is null ? "<unavailable>" : onFailure.Pools.PlayerCount.ToString())}; " +
                        $"PoolLimit={(onFailure.Pools is null ? "<unavailable>" : onFailure.Pools.PlayerLimit.ToString())}; " +
                        $"ObserverPassed={onFailure.Passed}; Detail={onFailure.Detail}");
                }
                catch (Exception ex)
                {
                    Trace("FAIL-LIVE-ZONE", $"Observer failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            Trace("RAMP-SHUTDOWN-SNAPSHOT",
                $"ReadyBeforeCleanup={ready.Count}; RealFailuresBeforeCleanup={failed.Count}; " +
                $"VerifiedStages={stageResults.Count(x => x.Passed)}");
            sessionCts.Cancel();
            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15)); }
                catch { }
            }
            Trace("RAMP-END",
                $"ReadyAtShutdown={ready.Count}; RealFailures={failed.Count}; " +
                $"PassedStages={stageResults.Count(x => x.Passed)}; CleanupDisconnectsExcluded=true");
            lock (diagnosticLock)
                diagnosticsClosed = true;
        }
    }

    private static string BuildHeartbeatMismatchDetail(
        ConcurrentDictionary<string, byte> ready,
        ConcurrentDictionary<string, DateTimeOffset> lastZoneHeartbeatRoundTrip,
        ConcurrentDictionary<string, FiestaHeadlessClientProgress> lastClientProgress,
        IReadOnlyList<FiestaLoadClientCredential> credentials,
        DateTimeOffset now)
    {
        // Real NA2016 capture: first Zone SH2/4 arrived ~30.1 s after CH6/3 and the client
        // answered CH2/5. A 25-second freshness window therefore classified healthy original
        // sessions as stale before their first heartbeat was even due.
        var freshnessWindow = TimeSpan.FromSeconds(45);
        var freshnessCutoff = now - freshnessWindow;
        var readyUsers = ready.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var fresh = readyUsers
            .Where(user =>
                lastZoneHeartbeatRoundTrip.TryGetValue(user, out var at)
                && at >= freshnessCutoff)
            .ToArray();

        var notYetDue = readyUsers.Count(user =>
        {
            if (lastZoneHeartbeatRoundTrip.ContainsKey(user))
                return false;
            return lastClientProgress.TryGetValue(user, out var state)
                   && now - state.TimestampUtc <= freshnessWindow;
        });

        var overdueUsers = readyUsers
            .Where(user =>
            {
                if (lastZoneHeartbeatRoundTrip.TryGetValue(user, out var at))
                    return at < freshnessCutoff;

                return !lastClientProgress.TryGetValue(user, out var state)
                       || now - state.TimestampUtc > freshnessWindow;
            })
            .ToArray();

        var neverServerHeartbeat = overdueUsers.Count(user =>
            !lastZoneHeartbeatRoundTrip.ContainsKey(user));

        var sample = overdueUsers
            .Take(12)
            .Select(user =>
            {
                var credential = credentials.FirstOrDefault(x =>
                    x.Username.Equals(user, StringComparison.OrdinalIgnoreCase));
                var character = credential?.CharacterName ?? "?";

                string ageText;
                if (lastZoneHeartbeatRoundTrip.TryGetValue(user, out var heartbeatAt))
                {
                    ageText = $"letzter SH2/4→CH2/5 vor {Math.Max(0, (now - heartbeatAt).TotalSeconds):N1}s";
                }
                else if (lastClientProgress.TryGetValue(user, out var state))
                {
                    ageText = $"noch nie SH2/4; Holding seit {Math.Max(0, (now - state.TimestampUtc).TotalSeconds):N1}s";
                }
                else
                {
                    ageText = "keine Zeitbasis";
                }

                return $"{user}/{character} ({ageText})";
            })
            .ToArray();

        return
            $"Zone-Heartbeat capture-basiert SH2/4→CH2/5: frisch <=45s {fresh.Length:N0}/{readyUsers.Length:N0}; " +
            $"noch nicht fällig {notYetDue:N0}; überfällig {overdueUsers.Length:N0} " +
            $"(davon nie SH2/4: {neverServerHeartbeat:N0}). " +
            $"Beispiele: {(sample.Length == 0 ? "keine" : string.Join(", ", sample))}.";
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

        var lastPassed = stages.LastOrDefault(x => x.Passed)?.TargetClients ?? 0;
        var capacityContext = lastPassed > 0
            ? $" · Letzte sauber serverseitig verifizierte Stufe: {lastPassed:N0} · Ready bei Abbruch: {readyCount:N0}"
            : $" · Noch keine Laststufe serverseitig verifiziert · Ready bei Abbruch: {readyCount:N0}";

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
                ? "LOAD RAMP BLOCKED · " + detail + capacityContext
                : "LOAD RAMP BLOCKED · " + detail + capacityContext + " · " + string.Join(" | ", failures)
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
            ClientCaptureProfilePath = source.ClientCaptureProfilePath,
            ZoneTransferTemplatePath = source.ZoneTransferTemplatePath,
            CharacterCreateTemplatePath = source.CharacterCreateTemplatePath,
            AllowEmulatorWorldClientKeyFallback = false,
            AllowEmulatorSizedZoneTransfer = false,
            StepTimeout = source.StepTimeout,
            ZoneLoginTimeout = source.ZoneLoginTimeout,
            DelayedZoneReentryAttempts = source.DelayedZoneReentryAttempts,
            HoldDuration = holdDuration
        };
}

public sealed class FiestaLoadRampOptions
{
    // The diagnostic ramp deliberately closes the huge 100 -> 500 gap. The current
    // original-server evidence shows that failures can first appear somewhere inside
    // that interval, so the certification run must preserve the last known clean level.
    public static IReadOnlyList<int> DiagnosticStageTargets { get; } =
        new[] { 1, 10, 50, 100, 150, 200, 250, 300, 350, 400, 450, 500,
                600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400,
                1450, 1500, 1510, 1550, 1600 };

    public string TargetZoneExePath { get; init; } = string.Empty;
    public string CredentialManifestPath { get; init; } = string.Empty;
    // Zero-based identity offset used by the isolated A test. Ramp B always starts at zero.
    public int CredentialStartIndex { get; init; }
    // 0: canonical order. >0: move one existing identity to the front WITHOUT
    // creating, skipping, duplicating, or changing the underlying credential manifest.
    // Used only for a diagnostic Ramp B; must be marked as non-baseline in trace/UI.
    public int PriorityCredentialNumber { get; init; }

    internal int ResolveCredentialIndex(int launchIndex)
    {
        if (launchIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(launchIndex));

        if (PriorityCredentialNumber == 0)
            return checked(CredentialStartIndex + launchIndex);

        var priorityIndex = PriorityCredentialNumber - 1;
        return launchIndex == 0
            ? priorityIndex
            : launchIndex <= priorityIndex
                ? launchIndex - 1
                : launchIndex;
    }

    public string DiagnosticsPath { get; init; } = string.Empty;
    public FiestaHeadlessProbeOptions ClientOptions { get; init; } = new();
    public IReadOnlyList<int> StageTargets { get; init; } = DiagnosticStageTargets;
    public bool RequireEmptyBaseline { get; init; } = true;
    public TimeSpan ClientStartInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan StageReadyTimeout { get; init; } = TimeSpan.FromMinutes(8);
    public TimeSpan ReadyPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan StageSettleTime { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SessionHoldDuration { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan FinalStabilityDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StabilityPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TargetZoneExePath) || !File.Exists(TargetZoneExePath))
            throw new FileNotFoundException("TargetZoneExePath wurde nicht gefunden.", TargetZoneExePath);
        if (string.IsNullOrWhiteSpace(CredentialManifestPath) || !File.Exists(CredentialManifestPath))
            throw new FileNotFoundException("CredentialManifestPath wurde nicht gefunden.", CredentialManifestPath);
        ClientOptions.Validate();

        if (string.IsNullOrWhiteSpace(ClientOptions.ClientCaptureProfilePath))
            throw new ArgumentException("Für Originalserver-Lasttests ist ClientCaptureProfilePath mit capture-basiertem CH3/15 zwingend.");
        var capturedProfile = FiestaCapturedClientProfile.Load(ClientOptions.ClientCaptureProfilePath);
        if (!capturedProfile.HasCapturedWorldClientKey)
            throw new ArgumentException("ClientCaptureProfilePath enthält keinen vollständigen capture-basierten CH3/15 WorldClientKey-Body.");
        if (string.IsNullOrWhiteSpace(ClientOptions.ZoneTransferTemplatePath))
            throw new ArgumentException("Für Originalserver-Lasttests ist ZoneTransferTemplatePath zwingend.");
        if (StageTargets.Count == 0 || StageTargets.Any(x => x <= 0))
            throw new ArgumentException("StageTargets muss positive Werte enthalten.");
        if (CredentialStartIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(CredentialStartIndex));
        if (PriorityCredentialNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(PriorityCredentialNumber));
        if (PriorityCredentialNumber > 0 && CredentialStartIndex != 0)
            throw new ArgumentException(
                "Die priorisierte Ramp-B-Identität darf nicht mit einem A-Einzeltest-Startindex kombiniert werden.");
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

        var requiredHold = CalculateRequiredSessionHoldDuration();
        if (SessionHoldDuration <= requiredHold)
        {
            throw new ArgumentException(
                $"SessionHoldDuration {SessionHoldDuration:c} ist zu kurz; für diese Rampenkonfiguration werden " +
                $"mehr als {requiredHold:c} benötigt.");
        }
    }

    internal TimeSpan CalculateRequiredSessionHoldDuration()
    {
        var targets = StageTargets
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        if (targets.Length == 0)
            return TimeSpan.Zero;

        // HoldDuration starts only after a client has reached ClientReady.
        // For the first stage {1}, StageReadyTimeout therefore must not be charged against
        // that client's hold time. It does need to survive all later ramp stages, settle
        // windows and the final stability interval. For a first stage >1, one readiness
        // window is still required for the remaining clients of that first stage.
        var finalTarget = targets[^1];
        var remainingClientStarts = Math.Max(0, finalTarget - 1);
        var remainingReadyWindows =
            Math.Max(0, targets.Length - 1) +
            (targets[0] > 1 ? 1 : 0);

        var clientStartSpread = TimeSpan.FromTicks(
            checked(ClientStartInterval.Ticks * (long)remainingClientStarts));
        var readyBudget = TimeSpan.FromTicks(
            checked(StageReadyTimeout.Ticks * (long)remainingReadyWindows));
        var settleBudget = TimeSpan.FromTicks(
            checked(StageSettleTime.Ticks * (long)targets.Length));
        var verificationBudget = TimeSpan.FromTicks(
            checked(TimeSpan.FromSeconds(5).Ticks * (long)targets.Length));

        return clientStartSpread
               + readyBudget
               + settleBudget
               + verificationBudget
               + FinalStabilityDuration
               + ReadyPollInterval
               + StabilityPollInterval;
    }

    public static FiestaLoadRampTimingSelfTestResult RunTimingSelfTest()
    {
        try
        {
            var single = new FiestaLoadRampOptions
            {
                StageTargets = new[] { 1 },
                ClientStartInterval = TimeSpan.Zero,
                StageReadyTimeout = TimeSpan.FromMinutes(5),
                ReadyPollInterval = TimeSpan.FromMilliseconds(500),
                StageSettleTime = TimeSpan.FromSeconds(3),
                SessionHoldDuration = TimeSpan.FromMinutes(7),
                FinalStabilityDuration = TimeSpan.FromMinutes(5),
                StabilityPollInterval = TimeSpan.FromSeconds(5)
            };
            var autoManifest = new FiestaLoadCredentialManifest
            {
                Clients = new List<FiestaLoadClientCredential>
                {
                    new()
                    {
                        Username = "r_ngt000001",
                        PasswordMd5 = "21232f297a57a5a743894a0e4a801fc3",
                        CharacterName = "NGT000001",
                        Slot = 0,
                        CreateCharacterIfMissing = true
                    }
                }
            };
            if (!autoManifest.IsLoginAutoRegistrationCompatible(out _))
                throw new InvalidDataException("r_-Credential-Manifest wurde fälschlich als inkompatibel abgelehnt.");

            var priorityRamp = new FiestaLoadRampOptions { PriorityCredentialNumber = 515 };
            var defaultRamp = new FiestaLoadRampOptions();
            var reordered = Enumerable.Range(0, 1600)
                .Select(priorityRamp.ResolveCredentialIndex)
                .ToArray();
            if (reordered.Length != 1600
                || reordered.Distinct().Count() != 1600
                || reordered[0] != 514
                || reordered[1] != 0
                || reordered[514] != 513
                || reordered[515] != 515
                || defaultRamp.ResolveCredentialIndex(514) != 514)
            {
                throw new InvalidDataException(
                    "Diagnostische Ramp-B-Umordnung muss 515 einmalig zuerst wählen, " +
                    "alle 1600 Identitäten erhalten und die Standardreihenfolge unverändert lassen.");
            }

            // The probe must select an existing identity from the manifest, not create
            // new login credentials or mutate the 1600-client benchmark ordering.
            var source = Enumerable.Range(1, 1600).Select(n => $"r_ngl{n:000000}").ToArray();
            const int probeIdentityNumber = 515;
            if (source[probeIdentityNumber - 1] != "r_ngl000515"
                || source[0] != "r_ngl000001")
            {
                throw new InvalidDataException(
                    "Gezielte A-Probe verletzt die 1-basierte Identitätsauswahl oder verändert Ramp B.");
            }

            var staleManifest = new FiestaLoadCredentialManifest
            {
                Clients = new List<FiestaLoadClientCredential>
                {
                    new()
                    {
                        Username = "ngt000001",
                        PasswordMd5 = "21232f297a57a5a743894a0e4a801fc3",
                        CharacterName = "NGT000001",
                        Slot = 0,
                        CreateCharacterIfMissing = true
                    }
                }
            };
            if (staleManifest.IsLoginAutoRegistrationCompatible(out _))
                throw new InvalidDataException("Manifest ohne r_-Prefix wurde fälschlich für Auto-Registration akzeptiert.");

            var singleRequired = single.CalculateRequiredSessionHoldDuration();
            if (single.SessionHoldDuration <= singleRequired
                || singleRequired >= TimeSpan.FromMinutes(6))
            {
                throw new InvalidDataException(
                    $"1-Client-Timing falsch: required={singleRequired:c}, hold={single.SessionHoldDuration:c}.");
            }

            var ramp = new FiestaLoadRampOptions
            {
                StageTargets = DiagnosticStageTargets,
                ClientStartInterval = TimeSpan.FromSeconds(1),
                StageReadyTimeout = TimeSpan.FromMinutes(3),
                ReadyPollInterval = TimeSpan.FromMilliseconds(500),
                StageSettleTime = TimeSpan.FromSeconds(10),
                SessionHoldDuration = TimeSpan.FromHours(2),
                FinalStabilityDuration = TimeSpan.FromMinutes(5),
                StabilityPollInterval = TimeSpan.FromSeconds(5)
            };
            var rampRequired = ramp.CalculateRequiredSessionHoldDuration();
            if (ramp.SessionHoldDuration <= rampRequired
                || rampRequired <= TimeSpan.FromMinutes(100)
                || rampRequired >= TimeSpan.FromHours(2))
            {
                throw new InvalidDataException(
                    $"1600er-Diagnoseramp-Timing falsch: required={rampRequired:c}, hold={ramp.SessionHoldDuration:c}.");
            }

            if (DiagnosticStageTargets.Zip(DiagnosticStageTargets.Skip(1), (a, b) => b - a)
                .TakeWhile((_, index) => DiagnosticStageTargets[index] < 500)
                .Any(step => step > 100))
            {
                throw new InvalidDataException("Diagnoseramp enthält unterhalb 500 weiterhin eine zu große Laststufen-Lücke.");
            }

            return new FiestaLoadRampTimingSelfTestResult(
                true,
                $"LOAD RAMP TIMING SELFTEST: PASS · 1 Client benötigt {singleRequired:c} < 00:07:00 · " +
                $"1600er Diagnoseramp mit feinen Stufen/1-s Admission/3-min Ready-Budget + 5-s Serverkonvergenz benötigt {rampRequired:c} < 02:00:00.");
        }
        catch (Exception ex)
        {
            return new FiestaLoadRampTimingSelfTestResult(
                false,
                "LOAD RAMP TIMING SELFTEST: FAIL · " + ex.Message);
        }
    }
}

public readonly record struct FiestaLoadRampTimingSelfTestResult(bool Success, string Detail);

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

    public bool IsLoginAutoRegistrationCompatible(out string detail)
    {
        Validate();

        var invalid = Clients
            .Where(x => !x.Username.StartsWith("r_", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(x => x.Username)
            .ToArray();

        if (invalid.Length == 0)
        {
            detail =
                $"AUTO-REGISTER CREDENTIALS OK · {Clients.Count:N0} Accounts · erster User {Clients[0].Username}.";
            return true;
        }

        detail =
            "Ausgewähltes Credential-Manifest ist NICHT für Login-Auto-Registration geeignet. " +
            "Erwartet werden ausschließlich Usernamen mit Präfix r_. " +
            $"Gefunden: {string.Join(", ", invalid)}. " +
            "Bitte das Manifest des erfolgreichen r_-Laufs auswählen. Falls es nicht mehr vorhanden ist, " +
            "mit einem NEUEN Basis-Prefix frische Auto-Register-Credentials erzeugen, damit keine vorhandenen r_-Accounts ein anderes Passwort besitzen.";
        return false;
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
