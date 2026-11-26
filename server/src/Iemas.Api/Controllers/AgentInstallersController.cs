using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using Iemas.Application.Agents;
using Iemas.Application.Agents.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// One-click Agent installer. An administrator downloads a zip with the Agent, an install script and a setup
/// file holding the server address and a new installer key. On the PC, Install-IEMAS-Agent.bat installs the
/// .NET runtime if needed, installs and starts the Agent, and the Agent registers with the key and is approved
/// straight away (no Pending step in the CMS).
/// </summary>
[ApiController]
[Route("api/v1/agent-installers")]
[Authorize(Policy = "RequireAdministrator")]
public class AgentInstallersController : ControllerBase
{
    private const string SetupFileName = "iemas-agent-setup.json";

    private readonly AgentInstallKeyService _keys;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public AgentInstallersController(AgentInstallKeyService keys, IConfiguration configuration, IWebHostEnvironment environment)
    {
        _keys = keys;
        _configuration = configuration;
        _environment = environment;
    }

    /// <summary>Where the Agent release files live: IemasAgent.exe plus the installer/ scripts.</summary>
    private string? ReleaseFolder()
    {
        var candidates = new[]
        {
            _configuration["AgentInstaller:ReleasePath"],
            "/app/agent-release",
            Path.Combine(_environment.ContentRootPath, "..", "..", "..", "windows-client", "agent-release"),
        };
        return candidates.Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Path.GetFullPath(c!))
            .FirstOrDefault(c => System.IO.File.Exists(Path.Combine(c, "IemasAgent.exe")) && Directory.Exists(Path.Combine(c, "installer")));
    }

    /// <summary>Whether packages can be built here (the Agent files are mounted into the API container).</summary>
    [HttpGet("availability")]
    public ActionResult<object> Availability() => ReleaseFolder() is null
        ? Ok(new { available = false, message = "The Agent files are not on the server. Mount windows-client/agent-release at /app/agent-release (see docker-compose.yml)." })
        : Ok(new { available = true, message = (string?)null });

    [HttpGet("keys")]
    public async Task<ActionResult<List<AgentInstallKeyDto>>> GetKeys(CancellationToken cancellationToken) =>
        Ok(await _keys.ListAsync(cancellationToken));

    [HttpPost("keys/{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        var result = await _keys.RevokeAsync(id, cancellationToken);
        return result.Succeeded ? NoContent() : BadRequest(new { message = result.Error });
    }

    /// <summary>Creates a new installer key and returns the installer zip that carries it.</summary>
    [HttpPost("package")]
    public async Task<IActionResult> CreatePackage([FromBody] CreateInstallPackageRequest request, CancellationToken cancellationToken)
    {
        var folder = ReleaseFolder();
        if (folder is null)
            return BadRequest(new { message = "The Agent files are not on the server, so no installer can be built. Mount windows-client/agent-release at /app/agent-release (see docker-compose.yml)." });

        var userId = Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var created = await _keys.CreateAsync(request, userId, cancellationToken);
        if (!created.Succeeded) return BadRequest(new { message = created.Error });

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

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntryFromFile(Path.Combine(folder, "IemasAgent.exe"), "IemasAgent.exe", CompressionLevel.Optimal);
            foreach (var file in Directory.GetFiles(Path.Combine(folder, "installer")))
            {
                zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
            }

            var entry = zip.CreateEntry(SetupFileName, CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, setup, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        }

        var who = created.Value.Key.EmailAddress is { } e ? e.Split('@')[0] : "company";
        var fileName = $"IEMAS-Agent-Installer-{who}-{DateTime.UtcNow:yyyyMMdd}.zip";
        return File(buffer.ToArray(), "application/zip", fileName);
    }
}
