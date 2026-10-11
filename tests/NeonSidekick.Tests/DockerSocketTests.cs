using System.Security.Cryptography;
using System.Text;
using NeonSidekick.Docker;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// The Docker engine's socket on a Mac (2026-10-11): the setting's forms (<see cref="DockerSocket.Normalize"/>) and the look's order
/// (<see cref="DockerSocket.Resolve"/>) — the setting, <c>DOCKER_HOST</c>, the CLI's current context, the usual places — over a fake
/// file system and environment, so they run on every system.
/// </summary>
public sealed class DockerSocketTests
{
    private const string Home = "/Users/neon";

    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sockets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    private DockerSocketLookup Look(string? setting = null) =>
        DockerSocket.Resolve(setting, name => _variables.TryGetValue(name, out var value) ? value : null, Home, _sockets.Contains, path => _files.TryGetValue(path, out var text) ? text : null);

    /// <summary>A CLI context named <paramref name="name"/> whose endpoint is <paramref name="host"/>, written where the CLI keeps it.</summary>
    private void Context(string name, string host, string folder = Home + "/.docker")
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        _files[folder + "/contexts/meta/" + hash + "/meta.json"] = "{\"Name\":\"" + name + "\",\"Metadata\":{},\"Endpoints\":{\"docker\":{\"Host\":\"" + host + "\",\"SkipTLSVerify\":false}}}";
    }

    [Theory]
    [InlineData("unix:///var/run/docker.sock", "/var/run/docker.sock")]
    [InlineData("UNIX:///Users/neon/.orbstack/run/docker.sock", "/Users/neon/.orbstack/run/docker.sock")]
    [InlineData("/var/run/docker.sock", "/var/run/docker.sock")]
    [InlineData("  ~/.colima/default/docker.sock  ", "~/.colima/default/docker.sock")]
    [InlineData("unix://~/.docker/run/docker.sock", "~/.docker/run/docker.sock")]
    [InlineData("~", "~")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    [InlineData("docker_engine", "")]                                  // the compiled default, Windows' pipe: find it
    [InlineData(@"\\.\pipe\docker_engine", "")]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine", "")]
    [InlineData("//./pipe/custom", "")]
    [InlineData("run/docker.sock", "")]                                // relative: not a path the app would guess at
    [InlineData("unix://", "")]
    public void TheSetting_IsAPath_OrFindIt(string? setting, string normalized) =>
        Assert.Equal(normalized, DockerSocket.Normalize(setting));

    [Fact]
    public void Paths_AreExpanded_AndShown_WithTheHomeAsTilde()
    {
        Assert.Equal(Home + "/.docker/run/docker.sock", DockerSocket.Expand("~/.docker/run/docker.sock", Home));
        Assert.Equal(Home, DockerSocket.Expand("~", Home));
        Assert.Equal("/var/run/docker.sock", DockerSocket.Expand("/var/run/docker.sock", Home));
        Assert.Equal("~/.docker/run/docker.sock", DockerSocket.Display(Home + "/.docker/run/docker.sock", Home));
        Assert.Equal("~/x.sock", DockerSocket.Display(Home + "/x.sock", Home + "/"));
        Assert.Equal("/Users/neonx/d.sock", DockerSocket.Display("/Users/neonx/d.sock", Home));   // a neighbour's folder is not the home
        Assert.Equal("/var/run/docker.sock", DockerSocket.Display("/var/run/docker.sock", Home));
    }

    [Fact]
    public void TheSetting_IsUsedAlone_ThereOrNot()
    {
        _variables[DockerSocket.HostVariable] = "unix:///elsewhere.sock";
        _sockets.Add(Home + "/.docker/run/docker.sock");
        var lookup = Look("~/custom/engine.sock");
        Assert.Equal(new DockerSocketFound(Home + "/custom/engine.sock", DockerSocket.FromSetting), lookup.Socket);
        Assert.Equal(["~/custom/engine.sock"], lookup.Looked);
        Assert.Null(lookup.IgnoredHost);
    }

    [Fact]
    public void DockerHost_AsAUnixSocket_ComesNext_AheadOfTheContext()
    {
        _variables[DockerSocket.HostVariable] = "unix:///Users/neon/.colima/default/docker.sock";
        _files[Home + "/.docker/config.json"] = """{"auths":{},"currentContext":"desktop-linux"}""";
        Context("desktop-linux", "unix:///Users/neon/.docker/run/docker.sock");
        _sockets.Add(Home + "/.docker/run/docker.sock");
        var lookup = Look("docker_engine");   // the Windows default on a Mac: find it
        Assert.Equal(new DockerSocketFound(Home + "/.colima/default/docker.sock", DockerSocket.HostVariable), lookup.Socket);   // used though not there: the user named it
        Assert.Equal(["~/.colima/default/docker.sock"], lookup.Looked);
    }

    [Theory]
    [InlineData("tcp://192.168.1.20:2375")]
    [InlineData("ssh://neon@builder")]
    [InlineData("npipe:////./pipe/docker_engine")]
    public void DockerHost_NotAUnixSocket_IsIgnored_ForTheOneWarning_AndTheLookGoesOn(string host)
    {
        _variables[DockerSocket.HostVariable] = host;
        _sockets.Add("/var/run/docker.sock");
        var lookup = Look();
        Assert.Equal(new DockerSocketFound("/var/run/docker.sock", DockerSocket.FromUsualPlace), lookup.Socket);
        Assert.Equal(host, lookup.IgnoredHost);
        Assert.Contains(host, DockerText.HostIgnored(host));
    }

    [Fact]
    public void TheCurrentContext_FromConfigJson_NamesTheSocket_WhenItIsThere()
    {
        // The real files' shape on this Mac (Docker Desktop 4.42): config.json's currentContext, the meta folder named by its SHA-256.
        _files[Home + "/.docker/config.json"] = """{"auths":{"registry.example":{}},"credsStore":"desktop","currentContext":"orbstack"}""";
        Context("orbstack", "unix:///Users/neon/.orbstack/run/docker.sock");
        _sockets.Add(Home + "/.orbstack/run/docker.sock");
        _sockets.Add(Home + "/.docker/run/docker.sock");   // Desktop's is there too: the context wins, as for the CLI
        var lookup = Look("");
        Assert.Equal(new DockerSocketFound(Home + "/.orbstack/run/docker.sock", DockerSocket.FromContext("orbstack")), lookup.Socket);
        Assert.Equal("the Docker context orbstack", lookup.Socket!.Source);
    }

    [Fact]
    public void DockerContext_AndDockerConfig_OutrankConfigJsonsChoice()
    {
        _variables[DockerSocket.ContextVariable] = "colima";
        _variables[DockerSocket.ConfigVariable] = "~/alt-docker";
        _files[Home + "/alt-docker/config.json"] = """{"currentContext":"desktop-linux"}""";
        Context("desktop-linux", "unix:///Users/neon/.docker/run/docker.sock", Home + "/alt-docker");
        Context("colima", "unix:///Users/neon/.colima/default/docker.sock", Home + "/alt-docker");
        _sockets.Add(Home + "/.colima/default/docker.sock");
        _sockets.Add(Home + "/.docker/run/docker.sock");
        Assert.Equal(new DockerSocketFound(Home + "/.colima/default/docker.sock", DockerSocket.FromContext("colima")), Look().Socket);
    }

    [Fact]
    public void AContextWhoseSocketIsGone_IsLookedAt_ThenTheUsualPlaces()
    {
        // Docker Desktop quit: its context still names its socket, but OrbStack's is up.
        _files[Home + "/.docker/config.json"] = """{"currentContext":"desktop-linux"}""";
        Context("desktop-linux", "unix:///Users/neon/.docker/run/docker.sock");
        _sockets.Add(Home + "/.orbstack/run/docker.sock");
        var lookup = Look();
        Assert.Equal(new DockerSocketFound(Home + "/.orbstack/run/docker.sock", DockerSocket.FromUsualPlace), lookup.Socket);
    }

    [Theory]
    [InlineData("""{"currentContext":"default"}""")]                   // the CLI's built-in context names no file
    [InlineData("""{"auths":{}}""")]
    [InlineData("not json")]
    [InlineData("""["currentContext"]""")]
    public void NoUsableContext_GoesStraightToTheUsualPlaces(string config)
    {
        _files[Home + "/.docker/config.json"] = config;
        Context("default", "unix:///nowhere.sock");
        _sockets.Add(Home + "/.rd/docker.sock");
        var lookup = Look();
        Assert.Equal(new DockerSocketFound(Home + "/.rd/docker.sock", DockerSocket.FromUsualPlace), lookup.Socket);
    }

    [Theory]
    [InlineData("tcp://192.168.1.20:2376")]
    [InlineData("not a url")]
    public void AContextOnAnotherScheme_IsSkipped(string host)
    {
        _files[Home + "/.docker/config.json"] = """{"currentContext":"remote"}""";
        Context("remote", host);
        _sockets.Add("/var/run/docker.sock");
        Assert.Equal(new DockerSocketFound("/var/run/docker.sock", DockerSocket.FromUsualPlace), Look().Socket);
    }

    [Fact]
    public void AMetaFileThatIsNotJson_IsSkipped()
    {
        _files[Home + "/.docker/config.json"] = """{"currentContext":"broken"}""";
        _files[Home + "/.docker/contexts/meta/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("broken"))) + "/meta.json"] = "{";
        _sockets.Add(Home + "/.docker/run/docker.sock");
        Assert.Equal(DockerSocket.FromUsualPlace, Look().Socket!.Source);
    }

    [Fact]
    public void TheUsualPlaces_AreLookedAt_InOrder()
    {
        Assert.Equal(["~/.docker/run/docker.sock", "/var/run/docker.sock", "~/.orbstack/run/docker.sock", "~/.colima/default/docker.sock", "~/.rd/docker.sock"], DockerSocket.UsualPlaces);
        _sockets.Add(Home + "/.colima/default/docker.sock");
        _sockets.Add(Home + "/.rd/docker.sock");
        Assert.Equal(Home + "/.colima/default/docker.sock", Look().Socket!.Path);
        _sockets.Add("/var/run/docker.sock");
        Assert.Equal("/var/run/docker.sock", Look().Socket!.Path);
        _sockets.Add(Home + "/.docker/run/docker.sock");
        Assert.Equal(Home + "/.docker/run/docker.sock", Look().Socket!.Path);
    }

    [Fact]
    public void NothingFound_ListsEveryPlaceLookedAt_InOrder_OnceEach()
    {
        _files[Home + "/.docker/config.json"] = """{"currentContext":"desktop-linux"}""";
        Context("desktop-linux", "unix:///Users/neon/.docker/run/docker.sock");
        var lookup = Look();
        Assert.Null(lookup.Socket);
        Assert.Equal(["~/.docker/run/docker.sock", "/var/run/docker.sock", "~/.orbstack/run/docker.sock", "~/.colima/default/docker.sock", "~/.rd/docker.sock"], lookup.Looked);
        Assert.Equal(
            "Error: no Docker engine socket found (looked at ~/.docker/run/docker.sock, /var/run/docker.sock, ~/.orbstack/run/docker.sock, ~/.colima/default/docker.sock, ~/.rd/docker.sock); start Docker Desktop, OrbStack or Colima, or set Docker engine socket on the Docker tab of /tools",
            DockerText.NoSocket(lookup.Looked));
    }

    [UnixFact]
    public void Exists_FollowsALink_ToItsEnd_ADanglingOneIsNothing()
    {
        var folder = Directory.CreateTempSubdirectory("nss-");
        try
        {
            string target = Path.Join(folder.FullName, "target.sock");
            string link = Path.Join(folder.FullName, "link.sock");
            File.CreateSymbolicLink(link, target);
            Assert.True(File.Exists(link));   // .NET's own answer passes the dangling link: why the look has its own
            Assert.False(DockerSocket.Exists(link));
            File.WriteAllText(target, "");
            Assert.True(DockerSocket.Exists(link));
            Assert.True(DockerSocket.Exists(target));
            Assert.False(DockerSocket.Exists(Path.Join(folder.FullName, "none.sock")));
            Assert.Null(DockerSocket.ReadFile(Path.Join(folder.FullName, "none.json")));
            Assert.Equal("", DockerSocket.ReadFile(target));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheEngineAddress_FollowsTheSystem_TheWindowsPipeUntouched()
    {
        var data = new AppSettingsData { DockerEnginePipe = "docker_engine" };
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(DockerTransport.Socket, DockerSocket.Here);
            Assert.Equal("", DockerSession.PipeOf(data));
            Assert.Equal("/var/run/docker.sock", DockerSession.PipeOf(new AppSettingsData { DockerEnginePipe = "unix:///var/run/docker.sock" }));
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.Equal(DockerTransport.Pipe, DockerSocket.Here);
            Assert.Equal("docker_engine", DockerSession.PipeOf(data));
            Assert.Equal("dockerDesktopLinuxEngine", DockerSession.PipeOf(new AppSettingsData { DockerEnginePipe = "npipe:////./pipe/dockerDesktopLinuxEngine" }));
        }
        else
        {
            Assert.Equal(DockerTransport.None, DockerSocket.Here);
        }
    }
}
