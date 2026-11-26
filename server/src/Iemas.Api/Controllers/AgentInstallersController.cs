using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using Iemas.Api.Services;
using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Api.Controllers;

/// <summary>
/// One-click Agent installer. An administrator downloads ONE file, IemasAgent.exe with the setup (server address and
/// a new installer key) attached to its end. Run on a PC it installs itself, removes any earlier IEMAS Agent, starts,
/// and registers with the key so the PC is approved straight away. The older zip (exe + Install-IEMAS-Agent.bat) is
/// still offered for PCs that cannot run a downloaded exe directly.
/// </summary>
[ApiController]
[Route("api/v1/agent-installers")]
[Authorize(Policy = "RequireAdministrator")]
public class AgentInstallersController : ControllerBase
{
    private const string SetupFileName = "iemas-agent-setup.json";

    private readonly AgentInstallKeyService _keys;
    private readonly IConfiguration _configuration;
    private readonly AgentReleaseLocator _release;
    private readonly IAppDbContext _db;

    public AgentInstallersController(AgentInstallKeyService keys, IConfiguration configuration, AgentReleaseLocator release, IAppDbContext db)
    {
        _keys = keys;
        _configuration = configuration;
        _release = release;
        _db = db;
    }

    /// <summary>Whether packages can be built here (the Agent files are mounted into the API container).</summary>
    [HttpGet("availability")]
    public ActionResult<object> Availability() => _release.Folder() is null
        ? Ok(new { available = false, message = "The Agent files are not on the server. Mount windows-client/agent-release at /app/agent-release (see docker-compose.yml)." })
        : Ok(new { available = true, message = (string?)null });

    /// <summary>The published Agent version, whether PCs update themselves, and how many PCs run which version.</summary>
    [HttpGet("release")]
    public async Task<ActionResult<AgentReleaseStatusDto>> Release(CancellationToken cancellationToken)
    {
        var current = _release.Current();
        var versions = await _db.Agents.AsNoTracking()
            .Where(a => a.RegistrationStatus == Iemas.Domain.Agents.AgentRegistrationStatus.Approved)
            .GroupBy(a => a.AgentVersion ?? "unknown")
            .Select(g => new AgentVersionCountDto(g.Key, g.Count()))
            .ToListAsync(cancellationToken);
        var autoUpdate = await SystemSettingsService.GetBoolAsync(_db, SystemSettingKeys.AgentAutoUpdate, cancellationToken);
        var outdated = current is null ? 0 : versions.Where(v => v.Version != current.Version).Sum(v => v.Count);
        return Ok(new AgentReleaseStatusDto(current?.Version, current?.SizeBytes, autoUpdate, outdated,
            versions.OrderByDescending(v => v.Count).ToList(),
            current is null && _release.Folder() is not null
                ? "The release has no version.txt, so PCs cannot update themselves. Publish with windows-client/Publish-Agent.ps1."
                : null));
    }

    [HttpGet("keys")]
    public async Task<ActionResult<List<AgentInstallKeyDto>>> GetKeys(CancellationToken cancellationToken) =>
        Ok(await _keys.ListAsync(cancellationToken));

    [HttpPost("keys/{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        var result = await _keys.RevokeAsync(id, cancellationToken);
        return result.Succeeded ? NoContent() : BadRequest(new { message = result.Error });
    }

    /// <summary>Creates a new installer key and returns the one-file installer: the Agent exe with its setup attached.</summary>
    [HttpPost("package-exe")]
    public async Task<IActionResult> CreateExePackage([FromBody] CreateInstallPackageRequest request, CancellationToken cancellationToken)
    {
        var folder = _release.Folder();
        if (folder is null) return BadRequest(new { message = NoFilesMessage });

        var created = await CreateKeyAsync(request, cancellationToken);
        if (created.Error is not null) return BadRequest(new { message = created.Error });

        using var buffer = new MemoryStream();
        await using (var exe = new FileStream(Path.Combine(folder, "IemasAgent.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await exe.CopyToAsync(buffer, cancellationToken);
        AgentSetupTrailer.Append(buffer, JsonSerializer.Serialize(created.Setup));

        var who = created.WhoName is { } w ? $"-{w}" : "";
        buffer.Position = 0;
        return File(buffer.ToArray(), "application/octet-stream", $"IEMAS-Agent-Setup{who}.exe");
    }

    /// <summary>Creates a new installer key and returns the zip that carries it (exe, install scripts and setup file).</summary>
    [HttpPost("package")]
    public async Task<IActionResult> CreatePackage([FromBody] CreateInstallPackageRequest request, CancellationToken cancellationToken)
    {
        var folder = _release.Folder();
        if (folder is null) return BadRequest(new { message = NoFilesMessage });

        var created = await CreateKeyAsync(request, cancellationToken);
        if (created.Error is not null) return BadRequest(new { message = created.Error });

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntryFromFile(Path.Combine(folder, "IemasAgent.exe"), "IemasAgent.exe", CompressionLevel.NoCompression);
            foreach (var file in Directory.GetFiles(Path.Combine(folder, "installer")))
            {
                zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
            }

            var entry = zip.CreateEntry(SetupFileName, CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, created.Setup, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        }

        var who = created.WhoName ?? "company";
        return File(buffer.ToArray(), "application/zip", $"IEMAS-Agent-Installer-{who}-{DateTime.UtcNow:yyyyMMdd}.zip");
    }

    private const string NoFilesMessage = "The Agent files are not on the server, so no installer can be built. Mount windows-client/agent-release at /app/agent-release (see docker-compose.yml).";

    private sealed record CreatedKey(object? Setup, string? WhoName, string? Error);

    private async Task<CreatedKey> CreateKeyAsync(CreateInstallPackageRequest request, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var created = await _keys.CreateAsync(request, userId, cancellationToken);
        if (!created.Succeeded) return new CreatedKey(null, null, created.Error);

        var setup = new
        {
            serverUrl = request.ServerUrl.Trim().TrimEnd('/'),
            allowSelfSignedCertificate = request.AllowSelfSignedCertificate,
            selfEnrollment = request.SelfEnrollment,
            serverCertificateThumbprint = _configuration["AgentInstaller:CertificateThumbprint"],
            emailAddress = created.Value!.Key.EmailAddress,
            employeeName = created.Value.OwnerName,
            installKey = created.Value.RawKey,
            expiresAt = created.Value.Key.ExpiresAt,
        };
        var who = created.Value.Key.EmailAddress is { } e ? e.Split('@')[0] : null;
        return new CreatedKey(setup, who, null);
    }
}

public record AgentVersionCountDto(string Version, int Count);

public record AgentReleaseStatusDto(string? Version, long? SizeBytes, bool AutoUpdate, int PcsOnOlderVersion, List<AgentVersionCountDto> Versions, string? Problem);
