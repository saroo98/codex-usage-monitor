using System.Reflection;
using System.Runtime.InteropServices;
using CodexUsageMonitor.App.Runtime;
using CodexUsageMonitor.Codex.Monitoring;
using CodexUsageMonitor.Codex.Transport;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Diagnostics;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Notifications.Native;
using CodexUsageMonitor.Persistence.Database;
using CodexUsageMonitor.Persistence.Outbox;
using CodexUsageMonitor.Persistence.Paths;
using CodexUsageMonitor.Updater.Security;
using CodexUsageMonitor.Windows.Startup;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Services;

public sealed class RuntimeDiagnosticsService
{
    private readonly UsageApplicationState _state;
    private readonly ApplicationSettingsService _settings;
    private readonly AppDataPaths _paths;
    private readonly UsageDatabase _database;
    private readonly IStartupRegistration _startup;
    private readonly CodexExecutableResolver _codexResolver;
    private readonly MultiProfileMonitorCoordinator _monitors;
    private readonly IClock _clock;
    private readonly IExecutableSignatureVerifier _signatureVerifier;
    private readonly IApplicationPackageContext _packageContext;
    private readonly ApplicationStartupState _applicationStartup;
    private readonly EmailCredentialService _emailCredentials;
    private readonly OAuthConnectionService _oauthConnections;
    private readonly EmailOutboxRepository _emailOutbox;
    private readonly UpdateRuntimeState _updates;
    private readonly INativeNotificationService _nativeNotifications;
    private readonly ILogger<RuntimeDiagnosticsService> _logger;

    public RuntimeDiagnosticsService(
        UsageApplicationState state,
        ApplicationSettingsService settings,
        AppDataPaths paths,
        UsageDatabase database,
        IStartupRegistration startup,
        CodexExecutableResolver codexResolver,
        MultiProfileMonitorCoordinator monitors,
        IClock clock,
        IExecutableSignatureVerifier signatureVerifier,
        IApplicationPackageContext packageContext,
        ApplicationStartupState applicationStartup,
        EmailCredentialService emailCredentials,
        OAuthConnectionService oauthConnections,
        EmailOutboxRepository emailOutbox,
        UpdateRuntimeState updates,
        INativeNotificationService nativeNotifications,
        ILogger<RuntimeDiagnosticsService> logger)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _codexResolver = codexResolver ?? throw new ArgumentNullException(nameof(codexResolver));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _packageContext = packageContext ?? throw new ArgumentNullException(nameof(packageContext));
        _applicationStartup = applicationStartup ?? throw new ArgumentNullException(nameof(applicationStartup));
        _emailCredentials = emailCredentials ?? throw new ArgumentNullException(nameof(emailCredentials));
        _oauthConnections = oauthConnections ?? throw new ArgumentNullException(nameof(oauthConnections));
        _emailOutbox = emailOutbox ?? throw new ArgumentNullException(nameof(emailOutbox));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _nativeNotifications = nativeNotifications ?? throw new ArgumentNullException(nameof(nativeNotifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DiagnosticSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var monitor = _state.ActiveMonitorState;
        var snapshot = _state.ActiveSnapshot;
        var settings = _settings.Current;
        var checks = new List<DiagnosticCheck>();
        var details = new Dictionary<string, string>(StringComparer.Ordinal);

        var startup = await ReadStartupAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(new DiagnosticCheck(
            "startup.registration",
            startup.State is not StartupRegistrationState.Unavailable,
            startup.SafeReasonCode ?? startup.State.ToString()));
        details["startup.state"] = startup.State.ToString();

        var databaseStatus = await ReadDatabaseStatusAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(new DiagnosticCheck("database.integrity", databaseStatus.IsHealthy, databaseStatus.SafeDetail));
        details["database.status"] = databaseStatus.SafeDetail;
        details["database.schema"] = UsageDatabase.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var emailStatus = await ReadEmailStatusAsync(settings.Email, cancellationToken).ConfigureAwait(false);
        checks.Add(new DiagnosticCheck("email.configuration", emailStatus.IsHealthy, emailStatus.SafeDetail));
        details["email.credential"] = emailStatus.CredentialState;
        details["email.oauth"] = emailStatus.OAuthState;

        var outbox = await ReadOutboxStatusAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(new DiagnosticCheck("email.outbox", outbox.IsHealthy, outbox.SafeDetail));
        details["email.outbox.pending"] = outbox.Statistics.PendingCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        details["email.outbox.terminal"] = outbox.Statistics.TerminalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        details["email.outbox.next"] = outbox.Statistics.NextAvailableAtUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        details["email.outbox.last_error"] = outbox.Statistics.LastSafeErrorCode ?? "none";

        var resolvedCodex = _codexResolver.Resolve();
        string? codexVersion = null;
        if (resolvedCodex is null)
        {
            checks.Add(new DiagnosticCheck("codex.executable", false, "codex.not_found"));
            details["codex.path"] = "Unavailable";
        }
        else
        {
            checks.Add(new DiagnosticCheck("codex.executable", true, resolvedCodex.DiscoverySource));
            details["codex.path"] = SafeDiagnosticRedactor.Redact(resolvedCodex.ExecutablePath);
            codexVersion = await ProbeCodexVersionAsync(resolvedCodex, cancellationToken).ConfigureAwait(false);
        }

        var assembly = typeof(RuntimeDiagnosticsService).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        details["build.commit"] = ReadMetadata(assembly, "BuildCommit") ?? "unknown";
        details["build.channel"] = ReadMetadata(assembly, "BuildChannel") ?? "development";
        details["package.type"] = PackageType(_paths, _packageContext.IsPackaged);
        details["signature.status"] = await InspectSignatureAsync(Environment.ProcessPath, cancellationToken).ConfigureAwait(false);
        details["app.data"] = _paths.IsPortable ? "portable-local" : "per-user-local";
        details["startup.stage"] = _applicationStartup.Stage.ToString();
        details["startup.degraded"] = _applicationStartup.DegradedComponents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        details["profiles.configured"] = settings.Profiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        details["profiles.running"] = _monitors.RunningProfileIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        details["notifications.enabled"] = settings.Notifications.Enabled.ToString(System.Globalization.CultureInfo.InvariantCulture);
        details["notifications.registration"] = NativeNotificationState();
        details["notifications.implementation"] = _nativeNotifications.GetType().Name;
        details["email.provider"] = settings.Email.Provider.ToString();
        details["email.configured"] = IsEmailConfigured(settings.Email).ToString(System.Globalization.CultureInfo.InvariantCulture);
        details["updates.enabled"] = settings.Updates.AutomaticChecks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        details["updates.channel"] = settings.Updates.Channel.ToString();
        details["updates.manifest"] = settings.Updates.ManifestUri is null ? "not-configured" : "configured";
        details["updates.state"] = _updates.Current.Status.ToString();
        details["updates.available"] = _updates.Current.AvailableVersion ?? "none";
        details["logs.directory"] = settings.General.PrivacyMode
            ? Path.GetFileName(_paths.LogsDirectory)
            : SafeDiagnosticRedactor.Redact(_paths.LogsDirectory);

        if (snapshot is not null)
        {
            details["active.profile"] = snapshot.ProfileId.ToString("D");
            details["active.account"] = snapshot.Account.StorageKey;
            details["active.workspace"] = snapshot.Workspace is null ? "not-reported" : "reported";
            details["limits.count"] = snapshot.Limits.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            details["limits.identities"] = string.Join(",", snapshot.Limits.Select(static limit => SafeIdentifier(limit.Identity)));
            details["snapshot.observed"] = snapshot.ObservedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var profile = settings.Profiles.FirstOrDefault(item => item.Id == snapshot.ProfileId);
            details["codex.home"] = string.IsNullOrWhiteSpace(profile?.CodexHome)
                ? "default"
                : settings.General.PrivacyMode ? "configured" : SafeDiagnosticRedactor.Redact(profile.CodexHome);
        }

        checks.Add(new DiagnosticCheck(
            "monitor.active",
            monitor.Connection is MonitorConnectionState.Live or MonitorConnectionState.Delayed,
            monitor.Connection.ToString()));
        foreach (var component in _applicationStartup.DegradedComponents.OrderBy(static item => item.Component, StringComparer.Ordinal))
        {
            checks.Add(new DiagnosticCheck($"startup.{component.Component}", false, component.SafeErrorCode));
        }

        return new DiagnosticSnapshot(
            _clock.UtcNow,
            version,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            _packageContext.IsPackaged,
            _paths.IsPortable,
            monitor.Connection.ToString(),
            monitor.LastSuccessAtUtc,
            codexVersion,
            monitor.SafeErrorCode,
            checks,
            details);
    }

    private async Task<(bool IsHealthy, string SafeDetail, string CredentialState, string OAuthState)> ReadEmailStatusAsync(
        EmailSettings email,
        CancellationToken cancellationToken)
    {
        if (!email.Enabled)
        {
            return (true, "email.not_enabled", "NotApplicable", "NotApplicable");
        }

        switch (email.Provider)
        {
            case EmailProviderMode.Off:
                return (true, "email.off", "NotApplicable", "NotApplicable");

            case EmailProviderMode.OtherSmtp:
            case EmailProviderMode.ProtonMailBridge:
                {
                    var credential = await _emailCredentials.GetSmtpPasswordStatusAsync(
                        email.SenderAddress,
                        cancellationToken).ConfigureAwait(false);
                    var ready = !string.IsNullOrWhiteSpace(email.SenderAddress) &&
                        credential.IsStored &&
                        !string.IsNullOrWhiteSpace(email.SmtpHost) &&
                        email.SmtpSecurity is not SmtpSecurityMode.None;
                    return (ready, ready ? "email.ready" : "email.incomplete", credential.State.ToString(), "NotApplicable");
                }

            case EmailProviderMode.Microsoft365:
            case EmailProviderMode.Gmail:
                {
                    var oauth = await _oauthConnections.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    var ready = !string.IsNullOrWhiteSpace(email.ConnectedAddress) && oauth.IsConnected;
                    return (ready, ready ? "email.ready" : "email.incomplete", "NotApplicable", oauth.State.ToString());
                }

            default:
                return (false, "email.unsupported_provider", "NotApplicable", "NotApplicable");
        }
    }

    private async Task<(bool IsHealthy, string SafeDetail, EmailOutboxStatistics Statistics)> ReadOutboxStatusAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var statistics = await _emailOutbox.GetStatisticsAsync(_clock.UtcNow, cancellationToken).ConfigureAwait(false);
            return (statistics.TerminalCount == 0, statistics.TerminalCount == 0 ? "email.outbox_ready" : "email.outbox_has_terminal_items", statistics);
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
        {
            _logger.LogWarning(exception, "Email outbox diagnostics failed.");
            return (false, "email.outbox_unavailable", new EmailOutboxStatistics(0, 0, null, null));
        }
    }

    private string NativeNotificationState()
    {
        if (_applicationStartup.DegradedComponents.Any(static item =>
                string.Equals(item.Component, "native-notifications", StringComparison.Ordinal)))
        {
            return "degraded";
        }

        return _applicationStartup.Stage < ApplicationStartupStage.NativeNotificationRegistration
            ? "pending"
            : "registered";
    }

    private static string SafeIdentifier(string value)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(digest.AsSpan(0, 8));
    }

    private async Task<StartupRegistrationResult> ReadStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _startup.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(exception, "Startup registration diagnostics failed.");
            return new StartupRegistrationResult(StartupRegistrationState.Unavailable, "startup.read_failed");
        }
    }

    private async Task<(bool IsHealthy, string SafeDetail)> ReadDatabaseStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _database.IntegrityCheckAsync(cancellationToken).ConfigureAwait(false);
            return (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase), SafeDiagnosticRedactor.Redact(result));
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
        {
            _logger.LogWarning(exception, "Database diagnostics failed.");
            return (false, "database.check_failed");
        }
    }

    private async Task<string?> ProbeCodexVersionAsync(ResolvedCodexCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var version = await _codexResolver.ProbeVersionAsync(
                command,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(version) ? null : SafeDiagnosticRedactor.Redact(version);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning(exception, "Codex version diagnostics failed.");
            return null;
        }
    }

    private static string? ReadMetadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value;

    private static string PackageType(AppDataPaths paths, bool isPackaged) => isPackaged
        ? "MSIX"
        : paths.IsPortable ? "Portable" : "Unpackaged";

    private static bool IsEmailConfigured(EmailSettings settings) =>
        settings.Enabled &&
        settings.Provider is not EmailProviderMode.Off &&
        !string.IsNullOrWhiteSpace(settings.ConnectedAddress ?? settings.SenderAddress);

    private async Task<string> InspectSignatureAsync(string? processPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return "unavailable";
        }

        try
        {
            var result = await _signatureVerifier.VerifyAsync(
                processPath,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);
            if (result.IsTrusted)
            {
                return "trusted";
            }

            return string.IsNullOrWhiteSpace(result.CertificateThumbprint)
                ? result.SafeErrorCode ?? "unsigned"
                : "signed.publisher_unpinned";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(exception, "Executable signature diagnostics failed.");
            return "signature.check_failed";
        }
    }

}
