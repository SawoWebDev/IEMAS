using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using Iemas.Agent.Models;

namespace Iemas.Agent.Services;

public enum SessionState { NotRegistered, PendingApproval, Rejected, Authenticating, Connected, Disconnected, Error }

/// <summary>
/// Requirements §68 (full registration → CONNECTED state machine), §8.1 (heartbeat, sync after
/// reconnect, report version/errors), §75/§77 (server is authoritative; UI is rebuilt from SYNC).
/// This is the single owner of the Agent's identity/connection lifecycle — Views only observe its
/// events and call its action methods; no view talks to IemasApiClient/SignalRAgentConnection
/// directly, keeping exactly one place responsible for the state machine's correctness.
/// </summary>
public class AgentSessionManager : IAsyncDisposable
{
    private readonly CredentialStore _credentialStore = new();
    private readonly AgentSettings _settings;
    private IemasApiClient _api;
    private readonly SignalRAgentConnection _signalR = new();
    private readonly DispatcherTimer _heartbeatTimer;
    private readonly DispatcherTimer _reauthWatchTimer;

    private Guid _agentId;
    private string? _registrationKey;
    private DateTimeOffset _tokenExpiresAt;
    private string _registrationRequestToken = string.Empty;

    public static string AgentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public SessionState State { get; private set; } = SessionState.NotRegistered;
    public string EmployeeName { get; private set; } = string.Empty;
    public List<CaseDto> ActionRequired { get; private set; } = new();
    public List<CaseDto> Waiting { get; private set; } = new();

    public event Action? StateUpdated;

    /// <summary>True when an installer package left a key: registering then connects without waiting for approval.</summary>
    public bool HasInstallKey => !string.IsNullOrWhiteSpace(_settings.InstallKey);
    public string? InstallEmail => _settings.InstallEmail;

    /// <summary>Why the automatic registration from the installer did not work, shown on the register screen.</summary>
    public string? AutoRegistrationError { get; private set; }
    public event Action<AgentPushMessage>? PushReceived;

    public AgentSessionManager()
    {
        _settings = AgentSettings.Load();
        _api = new IemasApiClient(_settings.ServerUrl, _settings.AllowInsecureTls, _settings.ServerCertificateThumbprint);

        _signalR.StateChanged += OnSignalRStateChanged;
        _signalR.CommandReceived += OnPushReceived;

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _heartbeatTimer.Tick += async (_, _) => await SafeHeartbeatAsync();

        // §68 registration is a polling flow ("Administrator Reviews" happens on the CMS side, on
        // the admin's own schedule) — poll every few seconds while PendingApproval so the Agent
        // reaches CONNECTED automatically the moment an admin approves, without a manual retry.
        _reauthWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _reauthWatchTimer.Tick += async (_, _) => await PollRegistrationIfPendingAsync();

        _sentItemsTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _sentItemsTimer.Tick += async (_, _) => await ReportOutlookRepliesAsync();
    }

    private readonly DispatcherTimer _sentItemsTimer;
    private readonly HashSet<string> _reportedEntryIds = new(StringComparer.Ordinal);
    private bool _scanningSentItems;

    /// <summary>
    /// Sends the employee's replies to their open inquiries from Outlook's Sent Items to the server. Needed for
    /// POP mailboxes, whose sent mail stays on this PC; harmless for IMAP ones (the server ignores duplicates).
    /// </summary>
    private async Task ReportOutlookRepliesAsync()
    {
        if (_scanningSentItems || State != SessionState.Connected) return;
        var openCases = ActionRequired.Concat(Waiting).ToList();
        if (openCases.Count == 0 || !OutlookSentItems.IsOutlookRunning()) return;

        _scanningSentItems = true;
        try
        {
            var customers = openCases.Select(c => c.CustomerEmailAddress.Trim().ToLowerInvariant()).ToHashSet();
            // From the oldest open inquiry, but never more than 30 days back.
            var since = new[] { openCases.Min(c => c.FirstEmailReceivedAt), DateTimeOffset.Now.AddDays(-30) }.Max();

            var (messages, problem) = await OutlookSentItems.ReadAsync(since, customers);
            var fresh = messages.Where(m => !_reportedEntryIds.Contains(m.EntryId)).ToList();
            if (fresh.Count == 0)
            {
                if (problem is not null && problem != "Outlook is not running.")
                    await _api.ReportErrorAsync($"Reading Outlook Sent Items failed: {problem}", CancellationToken.None);
                return;
            }

            var result = await _api.ReportSentMessagesAsync(new AgentSentMessagesRequest(fresh.Take(100).ToList()), CancellationToken.None);
            if (!result.Succeeded) return; // tried again on the next tick
            foreach (var m in fresh.Take(100)) _reportedEntryIds.Add(m.EntryId);
            if (result.Value is { RepliesStored: > 0 } or { InternalStored: > 0 }) await RefreshSyncAsync();
        }
        catch (Exception ex)
        {
            await _api.ReportErrorAsync($"Reading Outlook Sent Items failed: {ex.Message}", CancellationToken.None);
        }
        finally
        {
            _scanningSentItems = false;
        }
    }

    public async Task StartAsync()
    {
        var stored = _credentialStore.Load();
        if (stored is null)
        {
            State = SessionState.NotRegistered;
            StateUpdated?.Invoke();

            // Installed from a CMS package made for one mailbox: register straight away, nothing to type.
            if (HasInstallKey && !_settings.SelfEnrollment && !string.IsNullOrWhiteSpace(_settings.InstallEmail))
            {
                var (ok, error) = await RegisterAsync(_settings.InstallEmail!, Environment.MachineName);
                if (!ok)
                {
                    AutoRegistrationError = error;
                    StateUpdated?.Invoke();
                }
            }
            return;
        }

        _agentId = stored.AgentId;
        _registrationKey = stored.RegistrationKey;
        RebuildApiClientIfServerChanged(stored.ServerUrl);
        await AuthenticateAndConnectAsync();
    }

    /// <summary>§68 "Agent Sends Registration Request → Server Creates PENDING."</summary>
    public async Task<(bool Succeeded, string? Error)> RegisterAsync(string emailAddress, string clientName)
    {
        var request = new RegisterAgentRequest(emailAddress, clientName, _settings.ServerUrl, AgentVersion, Environment.MachineName,
            HasInstallKey ? _settings.InstallKey : null);
        var result = await _api.RegisterAsync(request, CancellationToken.None);

        // An expired or used-up installer must not block the PC: forget the key and fall back to the normal
        // request that an administrator approves in the CMS.
        if (!result.Succeeded && HasInstallKey && (result.Error ?? "").Contains("can no longer be used", StringComparison.OrdinalIgnoreCase))
        {
            ForgetInstallKey();
            result = await _api.RegisterAsync(request with { InstallKey = null }, CancellationToken.None);
        }

        if (!result.Succeeded || result.Value is null)
        {
            return (false, result.Error);
        }

        await AfterRegisteredAsync(result.Value.AgentId, result.Value.RegistrationRequestToken, result.Value.Status);
        return (true, null);
    }

    /// <summary>True when the installer is a company installer: the employee types an email and a password, nothing else.</summary>
    public bool UsesSelfEnrollment => _settings.SelfEnrollment && HasInstallKey;

    /// <summary>
    /// One-step setup. The password goes to the server once, over TLS, and is never stored on this PC. The server
    /// proves it with a real mailbox login, finds or creates the employee and the mailbox, and approves this PC.
    /// </summary>
    public async Task<(bool Succeeded, string? Error)> SelfEnrollAsync(string emailAddress, string password)
    {
        var request = new SelfEnrollRequest(_settings.InstallKey ?? string.Empty, emailAddress, password, Environment.MachineName,
            _settings.ServerUrl, AgentVersion, Environment.MachineName);
        var result = await _api.SelfEnrollAsync(request, CancellationToken.None);
        if (!result.Succeeded || result.Value is null)
        {
            return (false, result.Error);
        }

        await AfterRegisteredAsync(result.Value.AgentId, result.Value.RegistrationRequestToken, result.Value.Status);
        return (true, result.Value.Message);
    }

    private async Task AfterRegisteredAsync(Guid agentId, string registrationRequestToken, int status)
    {
        AutoRegistrationError = null;

        _agentId = agentId;
        _registrationRequestToken = registrationRequestToken;
        State = SessionState.PendingApproval;
        StateUpdated?.Invoke();
        _reauthWatchTimer.Start();
        if (status == 1)
        {
            // Approved on the spot by the installer key: collect the credential now instead of on the next poll.
            await PollRegistrationIfPendingAsync();
        }
    }

    private async Task PollRegistrationIfPendingAsync()
    {
        if (State != SessionState.PendingApproval || string.IsNullOrEmpty(_registrationRequestToken))
        {
            return;
        }

        var result = await _api.GetRegistrationStatusAsync(_registrationRequestToken, CancellationToken.None);
        if (!result.Succeeded || result.Value is null)
        {
            return;
        }

        // §68 status: 0=Pending, 1=Approved, 2=Rejected, 3=Revoked (AgentRegistrationStatus order).
        switch (result.Value.Status)
        {
            case 1 when result.Value.RegistrationKey is not null:
                _reauthWatchTimer.Stop();
                _registrationKey = result.Value.RegistrationKey;
                _credentialStore.Save(_agentId, _registrationKey, _settings.ServerUrl);
                ForgetInstallKey();
                await AuthenticateAndConnectAsync();
                break;
            case 2:
                _reauthWatchTimer.Stop();
                State = SessionState.Rejected;
                StateUpdated?.Invoke();
                break;
        }
    }

    private void ForgetInstallKey()
    {
        if (_settings.InstallKey is null && _settings.InstallEmail is null && !_settings.SelfEnrollment) return;
        _settings.InstallKey = null;
        _settings.InstallEmail = null;
        _settings.SelfEnrollment = false;
        try { _settings.Save(); } catch (System.IO.IOException) { /* retried on the next registration */ }
    }

    private async Task AuthenticateAndConnectAsync()
    {
        if (_registrationKey is null) return;

        State = SessionState.Authenticating;
        StateUpdated?.Invoke();

        var authResult = await _api.AuthenticateAsync(_agentId, _registrationKey, CancellationToken.None);
        if (!authResult.Succeeded || authResult.Value is null)
        {
            // §69/§70 — an invalid/revoked credential must not be treated as "try again later";
            // clear it so the user re-registers rather than looping forever on a dead credential.
            _credentialStore.Clear();
            State = SessionState.Error;
            StateUpdated?.Invoke();
            return;
        }

        _api.AccessToken = authResult.Value.AccessToken;
        _tokenExpiresAt = authResult.Value.ExpiresAt;
        EmployeeName = authResult.Value.EmployeeName;

        try
        {
            await _signalR.ConnectAsync(_settings.ServerUrl, () => _api.AccessToken, _settings.AllowInsecureTls, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await _api.ReportErrorAsync($"SignalR connect failed: {ex.Message}", CancellationToken.None);
        }

        await RefreshSyncAsync();
        if (!_settings.WelcomeTourShown) WelcomeTourDue?.Invoke();
        _heartbeatTimer.Start();
        _sentItemsTimer.Start();
        _ = ReportOutlookRepliesAsync();
    }

    private void OnSignalRStateChanged(AgentConnectionState state)
    {
        State = state switch
        {
            AgentConnectionState.Connected => SessionState.Connected,
            AgentConnectionState.Connecting => SessionState.Authenticating,
            _ => SessionState.Disconnected,
        };
        StateUpdated?.Invoke();

        if (state == AgentConnectionState.Connected)
        {
            // §75 — SYNC is mandatory on every (re)connect; server state, not whatever the Agent
            // cached before the drop, is what the UI shows next.
            _ = RefreshSyncAsync();
        }
    }

    private void OnPushReceived(AgentPushMessage message)
    {
        PushReceived?.Invoke(message);

        // §72/§104 — a push is a hint to refresh, never itself the state; always re-pull the
        // authoritative list rather than mutating ActionRequired/Waiting from the push payload alone.
        if (message.Type is "SHOW_CASE" or "SHOW_REMINDER" or "SHOW_NOTIFICATION" or "SYNC_REQUIRED")
        {
            _ = RefreshSyncAsync();
        }

        if (message.Type is "SHOW_CASE" or "SHOW_REMINDER" or "SHOW_NOTIFICATION")
        {
            var toastError = ToastService.Show(message.Title ?? "IEMAS", message.Message ?? string.Empty, message.CaseId);
            if (toastError is not null)
            {
                _ = _api.ReportErrorAsync($"Toast display failed for {message.Type}: {toastError}", CancellationToken.None);
            }
        }
    }

    public async Task RefreshSyncAsync()
    {
        var result = await _api.SyncAsync(CancellationToken.None);
        if (result.Succeeded && result.Value is not null)
        {
            ActionRequired = result.Value.ActionRequired;
            Waiting = result.Value.Waiting;
            StateUpdated?.Invoke();
        }
    }

    /// <summary>A newer Agent was downloaded and checked; the app should start it and exit. The argument is the downloaded exe.</summary>
    public event Action<string>? UpdateReady;

    private DateTime _nextUpdateCheck = DateTime.UtcNow.AddSeconds(30);

    /// <summary>Asks the server for a newer Agent about every 30 minutes (first check shortly after connecting).</summary>
    private async Task CheckForUpdateAsync()
    {
        if (DateTime.UtcNow < _nextUpdateCheck) return;
        _nextUpdateCheck = DateTime.UtcNow.AddMinutes(30);
        var downloaded = await AgentUpdater.DownloadIfNewerAsync(_api, AgentVersion, CancellationToken.None);
        if (downloaded is not null) UpdateReady?.Invoke(downloaded);
    }

    private async Task SafeHeartbeatAsync()
    {
        try
        {
            // §69 15-minute Agent token lifetime, no refresh token — re-authenticate proactively
            // before expiry rather than waiting for a 401, so the SignalR connection (which used the
            // token at handshake time) doesn't silently start rejecting hub calls mid-session.
            if (DateTimeOffset.UtcNow >= _tokenExpiresAt.AddMinutes(-2) && _registrationKey is not null)
            {
                await AuthenticateAndConnectAsync();
                return;
            }

            // The live connection carries the pop-ups; if it has dropped for good, sign in again and reconnect
            // rather than carrying on with REST heartbeats only (which would look "connected" but get no alerts).
            if (_signalR.State == AgentConnectionState.Disconnected && _registrationKey is not null)
            {
                await AuthenticateAndConnectAsync();
                return;
            }

            await _api.HeartbeatAsync(AgentVersion, CancellationToken.None);
            await _signalR.SendHeartbeatAsync(AgentVersion, CancellationToken.None);
            await CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            await _api.ReportErrorAsync($"Heartbeat failed: {ex.Message}", CancellationToken.None);
        }
    }

    public async Task<ApiResult<CaseActionResultDto>> SubmitActionAsync(Guid caseId, CaseActionType actionType, string? comment, DateTimeOffset? requestedForUtc = null)
    {
        var request = new SubmitCaseActionRequest(Guid.NewGuid().ToString("N"), caseId, actionType, comment, DateTimeOffset.UtcNow, requestedForUtc);
        var result = await _api.SubmitCaseActionAsync(request, CancellationToken.None);
        if (result.Succeeded)
        {
            await RefreshSyncAsync();
        }
        return result;
    }

    public async Task<ApiResult<bool>> SubmitCommentAsync(Guid caseId, string comment)
    {
        var request = new SubmitCaseCommentRequest(Guid.NewGuid().ToString("N"), caseId, comment, DateTimeOffset.UtcNow);
        return await _api.SubmitCaseCommentAsync(request, CancellationToken.None);
    }

    public async Task<ApiResult<bool>> CompleteCaseAsync(Guid caseId, CaseCompletionReason reason, string? comment)
    {
        var result = await _api.CompleteCaseAsync(caseId, new CompleteCaseActionRequest(reason, comment), CancellationToken.None);
        if (result.Succeeded)
        {
            await RefreshSyncAsync();
        }
        return result;
    }

    /// <summary>§8.1 "Report errors" — exposed for the app-wide unhandled-exception handler (see App.xaml.cs), best-effort, never throws.</summary>
    public Task ReportErrorAsync(string detail) => _api.ReportErrorAsync(detail, CancellationToken.None);

    public Task<ApiResult<WorkingRulesDto>> GetWorkingRulesAsync() => _api.GetWorkingRulesAsync(CancellationToken.None);

    public Task<ApiResult<PracticeInquiryResult>> SendPracticeInquiryAsync() => _api.SendPracticeInquiryAsync(CancellationToken.None);

    /// <summary>Raised once, the first time this PC connects (the welcome tour has not been shown yet).</summary>
    public event Action? WelcomeTourDue;

    /// <summary>Records that the welcome tour was shown, so it does not open again on the next start.</summary>
    public void MarkWelcomeTourShown()
    {
        if (_settings.WelcomeTourShown) return;
        _settings.WelcomeTourShown = true;
        try { _settings.Save(); } catch (System.IO.IOException) { /* shown again next time; harmless */ }
    }

    public void ApplyServerUrlChange(string newServerUrl, bool allowInsecureTls)
    {
        _settings.ServerUrl = newServerUrl;
        _settings.AllowInsecureTls = allowInsecureTls;
        _settings.Save();
        RebuildApiClientIfServerChanged(newServerUrl);
    }

    private void RebuildApiClientIfServerChanged(string serverUrl)
    {
        _api.Dispose();
        _api = new IemasApiClient(serverUrl, _settings.AllowInsecureTls, _settings.ServerCertificateThumbprint);
    }

    public void SignOut()
    {
        _sentItemsTimer.Stop();
        _heartbeatTimer.Stop();
        _reauthWatchTimer.Stop();
        _credentialStore.Clear();
        State = SessionState.NotRegistered;
        StateUpdated?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _sentItemsTimer.Stop();
        _heartbeatTimer.Stop();
        _reauthWatchTimer.Stop();
        await _signalR.DisposeAsync();
        _api.Dispose();
    }
}
