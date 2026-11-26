using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Iemas.Agent.Services;

/// <summary>
/// Installs the Agent from the one-file installer, and finds and removes earlier IEMAS Agents. This is the C# form of
/// installer\Install-IEMAS-Agent.ps1 and Remove-Old-IEMAS-Agent.ps1, which the zip installer still uses.
///
/// Cleanup, for the signed-in Windows user: stops every other running IemasAgent.exe; finds old copies from running
/// programs, the Start-with-Windows entry, shortcuts (Start menu, Startup, Desktop), an old uninstall entry and the usual
/// install folders; deletes only the Agent's own files there (and a folder only when that leaves it empty); removes the
/// old shortcuts and Start-with-Windows entry. It KEEPS %LOCALAPPDATA%\IemasAgent (settings and this PC's registration),
/// so an update needs no new approval. No administrator rights are needed.
/// </summary>
public static class AgentInstaller
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "IemasAgent";
    private const string ExeName = "IemasAgent.exe";
    private static readonly string[] AgentFiles = { "IemasAgent.exe", "IemasAgent.pdb", "IemasAgent.dll", "IemasAgent.deps.json", "IemasAgent.runtimeconfig.json" };

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string InstallDir => Path.Combine(LocalAppData, "Programs", "IEMAS Agent");
    public static string InstalledExe => Path.Combine(InstallDir, ExeName);
    private static string LogPath => Path.Combine(AgentSettings.SettingsDirectory, "install.log");

    public static bool IsInstalledCopy(string exePath) =>
        string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(AgentSettings.SettingsDirectory);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
        }
        catch { /* a log must never stop an install */ }
    }

    // ------------------------------------------------------------------------------------------------ install

    /// <summary>
    /// Replaces any earlier Agent with this one: clean up, copy the exe into <see cref="InstallDir"/>, save the server
    /// address and installer key, turn on Start with Windows and add the Start menu shortcut. Returns the installed exe.
    /// </summary>
    public static string Install(string sourceExe, long exeLength, SetupConfig config)
    {
        Log($"Installing from {sourceExe} (server {config.ServerUrl}) over any earlier version");
        var report = CleanEarlierVersions(InstallDir);
        foreach (var line in report) Log("  " + line);

        CopyExe(sourceExe, InstalledExe, exeLength);
        Log($"Installed to {InstallDir}");

        var settings = AgentSettings.Load();
        settings.ServerUrl = config.ServerUrl!.Trim().TrimEnd('/');
        settings.AllowInsecureTls = config.AllowSelfSignedCertificate;
        settings.StartWithWindows = true;
        settings.StartupDefaultApplied = true;
        settings.SelfEnrollment = config.SelfEnrollment;
        settings.ServerCertificateThumbprint = string.IsNullOrWhiteSpace(config.ServerCertificateThumbprint) ? null : config.ServerCertificateThumbprint;
        settings.InstallEmail = string.IsNullOrWhiteSpace(config.EmailAddress) ? null : config.EmailAddress;
        // A PC that is already registered keeps that registration; the key is only needed by a PC that has none yet.
        settings.InstallKey = new CredentialStore().HasStoredCredential() ? null : config.InstallKey;
        settings.Save();

        EnsureStartupAndShortcut(InstalledExe);
        return InstalledExe;
    }

    /// <summary>Copies the exe proper (without any attached setup) to its place, replacing a copy that is still exiting.</summary>
    public static void CopyExe(string source, string target, long exeLength)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".new";
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var remaining = exeLength > 0 ? exeLength : input.Length;
            var buffer = new byte[1024 * 1024];
            while (remaining > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0) break;
                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        // The old copy may need a moment to let go after it was stopped.
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temp, target, overwrite: true); break; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(500); }
        }
        try { File.Delete(target + ":Zone.Identifier"); } catch { /* the "downloaded from the internet" mark; harmless if it stays */ }
    }

    /// <summary>Start with Windows points at this copy, and the Start menu has one shortcut to it.</summary>
    public static void EnsureStartupAndShortcut(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.SetValue(RunValueName, $"\"{exe}\" {StartupRegistration.BackgroundArgument}");
        }
        catch (Exception ex) { Log($"Could not set Start with Windows: {ex.Message}"); }

        try
        {
            var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "IEMAS Agent.lnk");
            if (!string.Equals(ShortcutTarget(link), exe, StringComparison.OrdinalIgnoreCase))
                CreateShortcut(link, exe, Path.GetDirectoryName(exe)!, "IEMAS Agent - customer inquiry alerts");
        }
        catch (Exception ex) { Log($"Could not create the Start menu shortcut: {ex.Message}"); }
    }

    // ------------------------------------------------------------------------------------------------ cleanup

    /// <summary>
    /// Finds earlier copies of the Agent and removes them (see the class summary). <paramref name="newInstallDir"/> is left
    /// alone, apart from the program in it being stopped. With <paramref name="whatIf"/> nothing is stopped or deleted.
    /// Returns what was found and done, one line each.
    /// </summary>
    public static List<string> CleanEarlierVersions(string newInstallDir, bool whatIf = false)
    {
        var report = new List<string>();
        var newDir = Path.GetFullPath(newInstallDir).TrimEnd('\\');
        var found = new List<string>();
        void AddFound(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); } catch { return; }
            if (!string.Equals(Path.GetFileName(full), ExeName, StringComparison.OrdinalIgnoreCase)) return;
            if (!found.Contains(full, StringComparer.OrdinalIgnoreCase)) found.Add(full);
        }

        // 1. Running copies (never this process).
        var self = Environment.ProcessId;
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)).Where(p => p.Id != self).ToList();
        foreach (var p in running)
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* another session's process; cannot read its path */ }
            AddFound(path);
            report.Add($"Running: IEMAS Agent (process {p.Id}) {path}");
        }
        if (running.Count > 0 && !whatIf)
        {
            foreach (var p in running)
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { /* already gone, or not ours to stop */ }
            }
            report.Add($"Stopped {running.Count} running IEMAS Agent program(s)");
        }

        // 2. Start with Windows entry.
        string? runValue = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            runValue = key?.GetValue(RunValueName) as string;
        }
        catch { }
        string? runExe = null;
        if (!string.IsNullOrWhiteSpace(runValue))
        {
            runExe = ExePathFromCommand(runValue);
            AddFound(runExe);
            report.Add($"Start-with-Windows entry points to: {runExe}");
        }

        // 3. Shortcuts. Those that already point into the new folder are fine and stay.
        var oldShortcuts = new List<string>();
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        }.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            foreach (var lnk in FindShortcuts(folder, 2))
            {
                var target = ShortcutTarget(lnk);
                if (target is null || !string.Equals(Path.GetFileName(target), ExeName, StringComparison.OrdinalIgnoreCase)) continue;
                AddFound(target);
                if (!InDirectory(target, newDir)) oldShortcuts.Add(lnk);
                report.Add($"Shortcut: {lnk}");
            }
        }

        // 4. An uninstall entry from an installer that registered itself.
        foreach (var (hive, path) in new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(name);
                    var display = entry?.GetValue("DisplayName") as string;
                    if (display is null || !display.StartsWith("IEMAS Agent", StringComparison.OrdinalIgnoreCase)) continue;
                    var quiet = entry!.GetValue("QuietUninstallString") as string;
                    report.Add($"Uninstall entry: {display} {entry.GetValue("DisplayVersion")}");
                    if (string.IsNullOrWhiteSpace(quiet)) { report.Add("It has no silent uninstaller; its files are removed below if found."); continue; }
                    if (whatIf) continue;
                    try
                    {
                        using var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + quiet) { CreateNoWindow = true, UseShellExecute = false });
                        if (proc is not null && proc.WaitForExit(60000)) report.Add($"Ran its uninstaller (exit code {proc.ExitCode})");
                    }
                    catch (Exception ex) { report.Add($"Its uninstaller could not run: {ex.Message}"); }
                }
            }
            catch { /* a hive this user cannot read */ }
        }

        // 5. The usual folders.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var dir in new[]
        {
            Path.Combine(LocalAppData, "Programs", "IEMAS Agent"), Path.Combine(LocalAppData, "Programs", "IemasAgent"),
            Path.Combine(LocalAppData, "IEMAS Agent"), Path.Combine(programFiles, "IEMAS Agent"),
            Path.Combine(programFiles, "IemasAgent"), Path.Combine(programFilesX86, "IEMAS Agent"),
        })
            AddFound(Path.Combine(dir, ExeName));

        // Remove.
        var hadEarlierFiles = found.Any(File.Exists);
        var removed = 0;
        foreach (var exe in found)
        {
            if (!File.Exists(exe)) continue;
            var dir = Path.GetDirectoryName(exe)!;
            string version;
            try { version = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "unknown version"; } catch { version = "unknown version"; }

            if (InDirectory(exe, newDir) && string.Equals(dir.TrimEnd('\\'), newDir, StringComparison.OrdinalIgnoreCase))
            {
                report.Add($"Earlier version {version} in {dir} will be replaced by the new one");
                continue;
            }
            if (whatIf) { report.Add($"Would remove version {version} from {dir}"); continue; }

            foreach (var name in AgentFiles)
            {
                var file = Path.Combine(dir, name);
                try { if (File.Exists(file)) File.Delete(file); } catch { /* reported below */ }
            }
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            if (File.Exists(exe)) report.Add($"Could not remove {exe} (in use or no permission); remove it by hand");
            else { report.Add($"Removed IEMAS Agent {version} from {dir}"); removed++; }
        }

        if (!whatIf)
        {
            foreach (var link in oldShortcuts) { try { File.Delete(link); } catch { } }
            if (oldShortcuts.Count > 0) report.Add($"Removed {oldShortcuts.Count} old shortcut(s)");
            // The Start-with-Windows entry is replaced by the caller; one that already points at the new copy is left as is.
            if (runValue is not null && !(runExe is not null && InDirectory(runExe, newDir)))
            {
                try { using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true); key?.DeleteValue(RunValueName, throwOnMissingValue: false); } catch { }
            }
        }

        // The usual folders are candidates whether or not anything is there, so only files that exist count as a find.
        if (!hadEarlierFiles && running.Count == 0) report.Add("No earlier IEMAS Agent found on this PC");
        else if (removed == 0 && running.Count > 0) report.Add("Earlier IEMAS Agent stopped; it will be updated in place");
        return report;
    }

    private static bool InDirectory(string path, string directory)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"C:\x\IemasAgent.exe" --background  ->  C:\x\IemasAgent.exe</summary>
    public static string ExePathFromCommand(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            if (close > 1) return trimmed[1..close];
        }
        var cut = trimmed.IndexOf(" --", StringComparison.Ordinal);
        return (cut > 0 ? trimmed[..cut] : trimmed).Trim();
    }

    private static IEnumerable<string> FindShortcuts(string folder, int depth)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "*.lnk").ToList(); } catch { yield break; }
        foreach (var f in files) yield return f;
        if (depth <= 0) yield break;
        List<string> subs;
        try { subs = Directory.EnumerateDirectories(folder).ToList(); } catch { yield break; }
        foreach (var sub in subs)
            foreach (var f in FindShortcuts(sub, depth - 1)) yield return f;
    }

    private static string? ShortcutTarget(string lnkPath)
    {
        try
        {
            if (!File.Exists(lnkPath)) return null;
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(lnkPath);
            string target = link.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }

    private static void CreateShortcut(string lnkPath, string target, string workDir, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(lnkPath);
        link.TargetPath = target;
        link.WorkingDirectory = workDir;
        link.Description = description;
        link.Save();
    }
}
