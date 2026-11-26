using System.Security.Cryptography;

namespace Iemas.Api.Services;

public sealed record AgentReleaseInfo(string Version, string Sha256, long SizeBytes, string ExePath);

/// <summary>
/// Finds the published Agent (windows-client/agent-release: IemasAgent.exe, version.txt and installer/) and describes it.
/// The version comes from version.txt, which Publish-Agent.ps1 writes next to the exe; the hash is computed here.
/// </summary>
public sealed class AgentReleaseLocator
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly object _gate = new();
    private (DateTime Stamp, long Size, string Version, AgentReleaseInfo Info)? _cached;

    public AgentReleaseLocator(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    /// <summary>The release folder, or null when the Agent files are not on this server.</summary>
    public string? Folder()
    {
        var candidates = new[]
        {
            _configuration["AgentInstaller:ReleasePath"],
            "/app/agent-release",
            Path.Combine(_environment.ContentRootPath, "..", "..", "..", "windows-client", "agent-release"),
        };
        return candidates.Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Path.GetFullPath(c!))
            .FirstOrDefault(c => File.Exists(Path.Combine(c, "IemasAgent.exe")) && Directory.Exists(Path.Combine(c, "installer")));
    }

    /// <summary>The published Agent's version and hash. Null when there is no release or no version.txt to say which version it is.</summary>
    public AgentReleaseInfo? Current()
    {
        var folder = Folder();
        if (folder is null) return null;
        var exe = Path.Combine(folder, "IemasAgent.exe");
        var versionFile = Path.Combine(folder, "version.txt");
        if (!File.Exists(versionFile)) return null;
        var version = File.ReadLines(versionFile).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(version) || !Version.TryParse(version, out _)) return null;

        var info = new FileInfo(exe);
        lock (_gate)
        {
            if (_cached is { } c && c.Stamp == info.LastWriteTimeUtc && c.Size == info.Length && c.Version == version) return c.Info;

            using var stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var release = new AgentReleaseInfo(version, hash, info.Length, exe);
            _cached = (info.LastWriteTimeUtc, info.Length, version, release);
            return release;
        }
    }
}
