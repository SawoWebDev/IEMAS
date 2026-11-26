using System.Text;

namespace Iemas.Application.Agents;

/// <summary>
/// The one-file installer: IemasAgent.exe followed by its setup (server address, installer key ...) as a trailer.
/// Windows ignores bytes after the end of an exe, and the Agent reads its own last bytes on first run.
/// Layout, in order: [exe bytes][setup JSON, UTF-8][JSON length, 4 bytes little-endian][8 ASCII bytes "IEMASCFG"].
/// The Agent has the same format in Services/SetupPackage.cs.
/// </summary>
public static class AgentSetupTrailer
{
    public const string Magic = "IEMASCFG";
    private const int FooterLength = 4 + 8;

    public static void Append(Stream output, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        output.Write(body, 0, body.Length);
        output.Write(BitConverter.GetBytes(body.Length), 0, 4);
        output.Write(Encoding.ASCII.GetBytes(Magic), 0, 8);
    }

    /// <summary>Reads the trailer back. Null when the stream has none (a clean exe).</summary>
    public static string? TryRead(Stream input)
    {
        if (!input.CanSeek || input.Length < FooterLength) return null;
        var footer = new byte[FooterLength];
        input.Seek(-FooterLength, SeekOrigin.End);
        input.ReadExactly(footer, 0, FooterLength);
        if (Encoding.ASCII.GetString(footer, 4, 8) != Magic) return null;
        var length = BitConverter.ToInt32(footer, 0);
        if (length <= 0 || length > 64 * 1024 || length > input.Length - FooterLength) return null;
        var body = new byte[length];
        input.Seek(-(FooterLength + length), SeekOrigin.End);
        input.ReadExactly(body, 0, length);
        return Encoding.UTF8.GetString(body);
    }
}
