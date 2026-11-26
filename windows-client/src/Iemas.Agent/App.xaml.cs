using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Iemas.Agent.Services;
using Iemas.Agent.Views;
using Microsoft.Toolkit.Uwp.Notifications;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Iemas.Agent;

/// <summary>
/// §10 "System tray," §9 tray showing connection state, §8.1 toast display. Owns the single
/// long-lived AgentSessionManager and the NotifyIcon (System.Windows.Forms — WPF has no built-in
/// tray icon type; this is the standard, reliable approach for a WPF app that needs one).
/// </summary>
public partial class App : Application
{
    private NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private static Mutex? _singleInstance;
    public static AgentSessionManager Session { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Started by an Agent that is updating itself: swap the files, start the new version, and finish. No window.
        if (e.Args.Contains(AgentUpdater.ApplyArgument, StringComparer.OrdinalIgnoreCase))
        {
            await Task.Run(() => AgentUpdater.RunApply(e.Args));
            Shutdown();
            return;
        }

        // The one-file installer: an exe with the CMS's setup attached, run from anywhere but the install folder. Install
        // over any earlier Agent and start the installed copy. This comes before the single-instance check because an
        // earlier Agent may be running, and installing is what stops it.
        if (await TryInstallAsync())
        {
            Shutdown();
            return;
        }

        // One Agent per Windows user. A second copy (for example the user double-clicks the shortcut
        // while the tray one is running) would double every pop-up, so it just quits.
        _singleInstance = new Mutex(true, @"Local\IemasAgent.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }

        // Start with Windows is on by default; keep the Run entry pointing at this exe every launch.
        var settings = AgentSettings.Load();
        if (!settings.StartupDefaultApplied)
        {
            settings.StartWithWindows = true;
            settings.StartupDefaultApplied = true;
            settings.Save();
        }
        try { StartupRegistration.Apply(settings.StartWithWindows); } catch { /* a locked registry must not stop the Agent */ }
        var startedByWindows = e.Args.Contains(StartupRegistration.BackgroundArgument, StringComparer.OrdinalIgnoreCase);

        // §8.1 "Report errors" — a single unhandled exception anywhere (a WPF async-void event
        // handler, a background timer tick) must never silently kill the whole tray app; confirmed
        // live during E2E verification that it otherwise does. Best-effort report to the server,
        // then keep running rather than crash — losing the tray icon/Agent entirely would be worse
        // than one failed operation.
        DispatcherUnhandledException += (_, args) =>
        {
            _ = Session?.ReportErrorAsync($"Unhandled UI exception: {args.Exception}");
            System.Windows.MessageBox.Show(
                $"IEMAS Agent encountered an error and will continue running:\n\n{args.Exception.Message}",
                "IEMAS Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        Session = new AgentSessionManager();

        SetupTrayIcon();
        SetupToastActivation();

        _mainWindow = new MainWindow();
        if (startedByWindows)
        {
            // Quietly in the tray; pop-ups still appear on their own.
            _notifyIcon!.Text = "IEMAS Agent - starting...";
        }
        else
        {
            _mainWindow.Show();
        }

        // After an update the previous version is kept for a few minutes in case the new one fails; only then is it removed.
        _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ => AgentUpdater.CleanLeftovers());
        Session.UpdateReady += downloaded => Dispatcher.Invoke(() =>
        {
            // Hand over to the downloaded exe, which swaps the files and starts the new version.
            if (AgentUpdater.StartApply(downloaded)) ExitApplication();
        });
        Session.StateUpdated += UpdateTrayState;
        Session.PushReceived += ShowPopup;
        Session.WelcomeTourDue += () => Dispatcher.Invoke(ShowWelcomeTour);

        await Session.StartAsync();
    }

    /// <summary>True when this run was the installer and has finished (the installed Agent is started); the caller then quits.</summary>
    private static async Task<bool> TryInstallAsync()
    {
        var self = Environment.ProcessPath;
        if (self is null || !string.Equals(System.IO.Path.GetFileName(self), "IemasAgent.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (AgentInstaller.IsInstalledCopy(self)) return false;

        var config = SetupPackage.ReadFrom(self, out var exeLength);
        if (config is null) return false; // a plain exe (manual setup): runs where it is, as before

        try
        {
            var installed = await Task.Run(() => AgentInstaller.Install(self, exeLength, config));
            var psi = new System.Diagnostics.ProcessStartInfo(installed) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(installed)! };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AgentInstaller.Log($"Install failed: {ex}");
            var logFile = System.IO.Path.Combine(AgentSettings.SettingsDirectory, "install.log");
            MessageBox.Show($"The IEMAS Agent could not be installed:\n\n{ex.Message}\n\nSend a photo of this message to your IEMAS administrator.\nDetails: {logFile}",
                "IEMAS Agent setup", MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }
    }

    private void SetupTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "IEMAS Agent - Disconnected",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open IEMAS", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => { ShowMainWindow(); _mainWindow?.ShowSettings(); });
        menu.Items.Add("Help", null, (_, _) => { ShowMainWindow(); _mainWindow?.ShowHelp(); });
        menu.Items.Add("Welcome tour", null, (_, _) => ShowWelcomeTour());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    /// <summary>The IEMAS logo (blue envelope with an orange dot) so the tray icon is easy to spot; falls back to the default icon.</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/IemasAgent;component/Assets/iemas.ico"))?.Stream;
            if (stream is not null) return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch
        {
            // fall through to the default icon
        }
        return SystemIcons.Application;
    }

    /// <summary>Every alert from the server is shown in the Agent's own always-on-top pop-up.</summary>
    private void ShowPopup(Models.AgentPushMessage message)
    {
        if (message.Type is not ("SHOW_CASE" or "SHOW_REMINDER" or "SHOW_NOTIFICATION")) return;
        Dispatcher.Invoke(() => Views.PopupWindow.Show(
            message,
            openCase: caseId => { ShowMainWindow(); _mainWindow?.FocusCase(caseId); },
            acknowledge: caseId => _ = Session.SubmitActionAsync(caseId, Models.CaseActionType.Acknowledged, null)));
    }

    private void SetupToastActivation()
    {
        // §8.1 "Display Action Required Cases" reachable directly from a toast button.
        ToastNotificationManagerCompat.OnActivated += toastArgs =>
        {
            Dispatcher.Invoke(() =>
            {
                ShowMainWindow();
                var args = ToastArguments.Parse(toastArgs.Argument);
                if (args.TryGetValue(ToastService.CaseIdArgumentKey, out var caseIdStr) && Guid.TryParse(caseIdStr, out var caseId))
                {
                    _mainWindow?.FocusCase(caseId);
                    if (args.TryGetValue(ToastService.ActionArgumentKey, out var action) && action == "ack")
                    {
                        _ = Session.SubmitActionAsync(caseId, Models.CaseActionType.Acknowledged, null);
                    }
                }
            });
        };
    }

    private void UpdateTrayState()
    {
        if (_notifyIcon is null) return;
        Dispatcher.Invoke(() =>
        {
            _notifyIcon.Text = Session.State switch
            {
                SessionState.Connected => $"IEMAS Agent - Connected ({Session.ActionRequired.Count} Action Required)",
                SessionState.Authenticating => "IEMAS Agent - Connecting...",
                SessionState.PendingApproval => "IEMAS Agent - Awaiting Admin Approval",
                SessionState.Disconnected => "IEMAS Agent - Disconnected",
                SessionState.Rejected => "IEMAS Agent - Registration Rejected",
                SessionState.Error => "IEMAS Agent - Error",
                _ => "IEMAS Agent - Not Registered",
            };
        });
    }

    private Views.WelcomeWindow? _welcome;

    /// <summary>The first-run tour (also from the tray menu). One at a time; marked as shown when it opens.</summary>
    public void ShowWelcomeTour()
    {
        if (_welcome is not null) { _welcome.Activate(); return; }
        if (Session.State != SessionState.Connected && Session.State != SessionState.Authenticating && string.IsNullOrEmpty(Session.EmployeeName))
        {
            MessageBox.Show("The welcome tour is available once this PC is connected to IEMAS.", "IEMAS Agent", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Session.MarkWelcomeTourShown();
        _welcome = new Views.WelcomeWindow(Session.EmployeeName, Session.GetWorkingRulesAsync, Session.SendPracticeInquiryAsync);
        _welcome.Closed += (_, _) => _welcome = null;
        _welcome.Show();
        _welcome.Activate();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void ExitApplication()
    {
        _notifyIcon!.Visible = false;
        _notifyIcon.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ = Session.DisposeAsync();
        base.OnExit(e);
    }
}
