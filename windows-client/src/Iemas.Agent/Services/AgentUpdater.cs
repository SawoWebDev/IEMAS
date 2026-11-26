using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Iemas.Agent.Services;

/// <summary>
/// Updates the Agent from the server, so PCs need no new installer. The running Agent asks the server which version is
/// published; when it is newer, downloads it, checks its SHA-256 against the value the server sent over the same signed-in,
/// certificate-pinned connection, then starts the downloaded exe in "apply" mode and quits. The downloaded exe waits for
/// the old Agent to exit, swaps itself into the installed place, starts it, and checks that it stays up; if it does not,
/// the previous version is put back.
/// </summary>
public static class AgentUpdater
{
    public const string ApplyArgument = "--apply-update";
    public static string UpdateDir => Path.Combine(AgentSettings.SettingsDirectory, "update");

    /// <summary>Remembered so a bad download is not retried every minute.</summary>
    private static string? _failedVersion;
    private static DateTime _failedAt;

    /// <summary>Whether this process is an installed Agent that can replace itself in place.</summary>
    public static bool CanUpdateInPlace =>
        Environment.ProcessPath is { } p && string.Equals(Path.GetFileName(p), "IemasAgent.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsNewer(string? published, string current) =>
        Version.TryParse(published, out var latest) && Version.TryParse(current, out var mine) && latest > mine;

    /// <summary>The path of a downloaded, verified newer Agent, or null when there is nothing to update to.</summary>
    public static async Task<string?> DownloadIfNewerAsync(IemasApiClient api, string currentVersion, CancellationToken ct)
    {
        if (!CanUpdateInPlace) return null;
        var info = await api.GetUpdateInfoAsync(ct);
        if (!info.Succeeded || info.Value is not { Available: true } available || !IsNewer(available.Version, currentVersion)) return null;
        if (_failedVersion == available.Version && DateTime.UtcNow - _failedAt < TimeSpan.FromHours(6)) return null;

        try
        {
            Directory.CreateDirectory(UpdateDir);
            var final = Path.Combine(UpdateDir, $"IemasAgent-{available.Version}.exe");
            var part = final + ".part";
            AgentInstaller.Log($"Update {currentVersion} -> {available.Version} available; downloading");
            var download = await api.DownloadUpdateAsync(part, ct);
            if (!download.Succeeded) throw new IOException(download.Error ?? "download failed");

            string hash;
            await using (var stream = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (!string.Equals(hash, available.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("the downloaded file does not match the server's checksum");

            File.Move(part, final, overwrite: true);
            return final;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failedVersion = available.Version;
            _failedAt = DateTime.UtcNow;
            AgentInstaller.Log($"Update to {available.Version} failed: {ex.Message}");
            TryDelete(Path.Combine(UpdateDir, $"IemasAgent-{available.Version}.exe.part"));
            return null;
        }
    }

    /// <summary>Starts the downloaded exe in apply mode. The caller must exit straight afterwards.</summary>
    public static bool StartApply(string downloadedExe)
    {
        try
        {
            var psi = new ProcessStartInfo(downloadedExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(downloadedExe)! };
            psi.ArgumentList.Add(ApplyArgument);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(Environment.ProcessPath!);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AgentInstaller.Log($"Could not start the update: {ex.Message}");
            return false;
        }
    }

    /// <summary>Runs in the downloaded exe: <c>--apply-update &lt;old process id&gt; &lt;installed exe&gt;</c>. Returns when done.</summary>
    public static void RunApply(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, ApplyArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || args.Length < index + 3 || !int.TryParse(args[index + 1], out var oldPid)) return;
        var target = args[index + 2];
        var self = Environment.ProcessPath!;
        var backup = target + ".old";

        try
        {
            // 1. Let the old Agent finish; stop it if it does not.
            try
            {
                using var old = Process.GetProcessById(oldPid);
                if (!old.WaitForExit(30000)) { old.Kill(entireProcessTree: true); old.WaitForExit(5000); }
            }
            catch (ArgumentException) { /* already gone */ }

            // 2. Tidy anything else left behind, then swap the files, keeping the old one to go back to.
            foreach (var line in AgentInstaller.CleanEarlierVersions(Path.GetDirectoryName(target)!)) AgentInstaller.Log("  " + line);
            if (File.Exists(target)) File.Move(target, backup, overwrite: true);
            try
            {
                AgentInstaller.CopyExe(self, target, SetupPackage.ReadFrom(self, out var length) is null ? 0 : length);
            }
            catch
            {
                if (File.Exists(backup)) File.Move(backup, target, overwrite: true);
                throw;
            }
            AgentInstaller.EnsureStartupAndShortcut(target);
            AgentInstaller.Log($"Updated {target} from {self}");

            // 3. Start the new one and make sure it stays up; otherwise put the old one back.
            Start(target);
            if (!StaysUp(target, TimeSpan.FromSeconds(25)))
            {
                AgentInstaller.Log("The new version did not stay running; going back to the previous one");
                KillRunning(target);
                if (File.Exists(backup))
                {
                    File.Move(backup, target, overwrite: true);
                    Start(target);
                }
            }
        }
        catch (Exception ex)
        {
            AgentInstaller.Log($"Update failed: {ex.Message}");
            if (!Process.GetProcessesByName("IemasAgent").Any(p => p.Id != Environment.ProcessId))
            {
                if (!File.Exists(target) && File.Exists(backup)) { try { File.Move(backup, target); } catch { } }
                if (File.Exists(target)) Start(target);
            }
        }
    }

    /// <summary>Removes downloads and the previous version left by an update that has worked.</summary>
    public static void CleanLeftovers()
    {
        try { if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, recursive: true); } catch { /* still in use by the applier */ }
        if (Environment.ProcessPath is { } p) TryDelete(p + ".old");
    }

    private static void Start(string exe)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.ArgumentList.Add(StartupRegistration.BackgroundArgument);
        Process.Start(psi);
    }

    private static bool StaysUp(string exe, TimeSpan time)
    {
        // Up and still up at the end counts. Never seen, or seen and then gone, means it crashed on start.
        var until = DateTime.UtcNow + time;
        var seen = false;
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(1000);
            if (RunningFrom(exe).Any()) seen = true;
            else if (seen) return false;
        }
        return seen;
    }

    private static IEnumerable<Process> RunningFrom(string exe)
    {
        foreach (var p in Process.GetProcessesByName("IemasAgent"))
        {
            if (p.Id == Environment.ProcessId) continue;
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) yield return p;
        }
    }

    private static void KillRunning(string exe)
    {
        foreach (var p in RunningFrom(exe).ToList())
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
