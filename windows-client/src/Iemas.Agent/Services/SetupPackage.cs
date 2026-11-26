using System.IO;
using System.Text;
using System.Text.Json;

namespace Iemas.Agent.Services;

/// <summary>What the CMS attaches to the installer exe: where the server is and the key that lets this PC set itself up.</summary>
public class SetupConfig
{
    public string? ServerUrl { get; set; }
    public bool AllowSelfSignedCertificate { get; set; }
    public bool SelfEnrollment { get; set; }
    public string? ServerCertificateThumbprint { get; set; }
    public string? EmailAddress { get; set; }
    public string? EmployeeName { get; set; }
    public string? InstallKey { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// The one-file installer is IemasAgent.exe with its setup written after the last byte of the exe (Windows ignores
/// bytes past the end of an exe). Layout: [exe][setup JSON, UTF-8][JSON length, 4 bytes little-endian][8 bytes "IEMASCFG"].
/// The server writes it in Iemas.Application/Agents/AgentSetupTrailer.cs; keep the two in step.
/// </summary>
public static class SetupPackage
{
    private const string Magic = "IEMASCFG";
    private const int FooterLength = 4 + 8;

    /// <summary>The setup attached to this exe file, or null for a clean exe (an installed or updated copy).</summary>
    public static SetupConfig? ReadFrom(string exePath, out long exeLength)
    {
        exeLength = 0;
        try
        {
            using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            exeLength = stream.Length;
            if (stream.Length < FooterLength) return null;

            var footer = new byte[FooterLength];
            stream.Seek(-FooterLength, SeekOrigin.End);
            stream.ReadExactly(footer, 0, FooterLength);
            if (Encoding.ASCII.GetString(footer, 4, 8) != Magic) return null;

            var length = BitConverter.ToInt32(footer, 0);
            if (length <= 0 || length > 64 * 1024 || length > stream.Length - FooterLength) return null;

            var body = new byte[length];
            stream.Seek(-(FooterLength + length), SeekOrigin.End);
            stream.ReadExactly(body, 0, length);
            var config = JsonSerializer.Deserialize<SetupConfig>(Encoding.UTF8.GetString(body), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config is null || string.IsNullOrWhiteSpace(config.ServerUrl)) return null;

            exeLength = stream.Length - FooterLength - length; // the exe proper, without the setup
            return config;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
