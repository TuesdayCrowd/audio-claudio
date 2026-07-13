using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AudioClaudio.Infrastructure.LiveView;
using Xunit;

namespace AudioClaudio.Tests.LiveView;

/// <summary>
/// GET /files/&lt;name&gt; -- the whitelisted take-file route (S5.11): serves the current take's
/// raw.mid / score.mid / score.musicxml / recreation.wav / input.wav from the out-dir.
/// </summary>
public class LiveNotationServerTakeFilesTests
{
    private static readonly string WebRoot = CreateFixtureWebRoot();

    private static string CreateFixtureWebRoot()
    {
        string dir = Directory.CreateTempSubdirectory("claudio_liveview_files_webroot_").FullName;
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html><body>osmd host</body></html>");
        return dir;
    }

    private static string CreateOutDir(params (string Name, byte[] Bytes)[] files)
    {
        string dir = Directory.CreateTempSubdirectory("claudio_liveview_files_outdir_").FullName;
        foreach ((string name, byte[] bytes) in files)
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
        return dir;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task ServesAWhitelistedTakeFileFromTheOutDir()
    {
        byte[] content = Encoding.UTF8.GetBytes("fake musicxml");
        string outDir = CreateOutDir(("score.musicxml", content));
        using var server = new LiveNotationServer(WebRoot, outDirPath: outDir);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl + "files/score.musicxml");
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(content, body);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task NonWhitelistedFileNameIsRejectedEvenIfItExistsInTheOutDir()
    {
        string outDir = CreateOutDir(("log.txt", Encoding.UTF8.GetBytes("run log, not a take file")));
        using var server = new LiveNotationServer(WebRoot, outDirPath: outDir);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl + "files/log.txt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task MissingTakeFileReturns404()
    {
        string outDir = CreateOutDir();
        using var server = new LiveNotationServer(WebRoot, outDirPath: outDir);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl + "files/recreation.wav");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task WhenNoOutDirIsConfiguredTakeFileRoutesAlwaysReturn404()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl + "files/score.mid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
