using System.Text;
using Iemas.Api.Services;
using Iemas.Application.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace Iemas.Tests.Agents;

public class AgentReleaseTests
{
    [Fact]
    public void Trailer_RoundTrips_AndLeavesTheExeBytesUntouched()
    {
        var exe = Encoding.ASCII.GetBytes("MZ-fake-exe-bytes");
        using var file = new MemoryStream();
        file.Write(exe);
        AgentSetupTrailer.Append(file, "{\"serverUrl\":\"https://x\",\"installKey\":\"k\"}");

        Assert.Equal("{\"serverUrl\":\"https://x\",\"installKey\":\"k\"}", AgentSetupTrailer.TryRead(file));
        Assert.Equal(exe, file.ToArray().Take(exe.Length).ToArray());
    }

    [Fact]
    public void CleanExe_HasNoTrailer()
    {
        using var file = new MemoryStream(Encoding.ASCII.GetBytes("MZ-just-an-exe-with-no-setup-attached"));
        Assert.Null(AgentSetupTrailer.TryRead(file));
        using var tiny = new MemoryStream(new byte[3]);
        Assert.Null(AgentSetupTrailer.TryRead(tiny));
    }

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "t";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static AgentReleaseLocator Locator(string folder) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AgentInstaller:ReleasePath"] = folder }).Build(), new Env());

    [Fact]
    public void Locator_ReportsVersionAndHash_OnlyWhenVersionFileExists_AndPicksUpANewBuild()
    {
        var dir = Path.Combine(Path.GetTempPath(), "iemas-rel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "installer"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "IemasAgent.exe"), "build one");
            var locator = Locator(dir);

            Assert.NotNull(locator.Folder());
            Assert.Null(locator.Current()); // no version.txt: cannot say which version this is

            File.WriteAllText(Path.Combine(dir, "version.txt"), "1.5.0\n");
            var first = locator.Current()!;
            Assert.Equal("1.5.0", first.Version);
            Assert.Equal(64, first.Sha256.Length);

            File.WriteAllText(Path.Combine(dir, "IemasAgent.exe"), "build two, longer");
            File.WriteAllText(Path.Combine(dir, "version.txt"), "1.6.0");
            var second = locator.Current()!;
            Assert.Equal("1.6.0", second.Version);
            Assert.NotEqual(first.Sha256, second.Sha256);

            File.WriteAllText(Path.Combine(dir, "version.txt"), "not a version");
            Assert.Null(locator.Current());
        }
        finally { Directory.Delete(dir, true); }
    }
}
