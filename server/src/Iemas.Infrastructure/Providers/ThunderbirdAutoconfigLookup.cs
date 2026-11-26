using System.Xml.Linq;
using Iemas.Application.Common.Providers;

namespace Iemas.Infrastructure.Providers;

/// <summary>Reads Thunderbird's public mail-client configuration database (a fixed host, so a domain can never redirect us elsewhere).</summary>
public class ThunderbirdAutoconfigLookup : IMailAutoconfigLookup
{
    private readonly HttpClient _http;

    public ThunderbirdAutoconfigLookup(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(8);
    }

    public async Task<IReadOnlyList<MailServerCandidate>> LookupAsync(string domain, CancellationToken cancellationToken)
    {
        if (domain.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))) return Array.Empty<MailServerCandidate>();
        try
        {
            var xml = await _http.GetStringAsync($"https://autoconfig.thunderbird.net/v1.1/{domain}", cancellationToken);
            return ParseXml(xml);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return Array.Empty<MailServerCandidate>();
        }
    }

    public static IReadOnlyList<MailServerCandidate> ParseXml(string xml)
    {
        var list = new List<MailServerCandidate>();
        foreach (var server in XDocument.Parse(xml).Descendants("incomingServer").Where(s => (string?)s.Attribute("type") == "imap"))
        {
            var host = (string?)server.Element("hostname");
            if (string.IsNullOrWhiteSpace(host) || !int.TryParse((string?)server.Element("port"), out var port)) continue;
            var socket = ((string?)server.Element("socketType") ?? "SSL").ToUpperInvariant();
            list.Add(new MailServerCandidate(host.Trim(), port, socket == "STARTTLS" ? "STARTTLS" : "SSL/TLS"));
        }
        return list;
    }
}
