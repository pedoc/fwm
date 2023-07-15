using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Handlers;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Humanizer;
using Serilog;
using ShellProgressBar;

// ReSharper disable InconsistentNaming

namespace fwm;

internal class Program
{
    static Task Main(string[] args)
    {
        var app = ConsoleApp.Create(args);
        app.AddCommands<Handler>();
        return app.RunAsync();
    }
}

class Handler : ConsoleAppBase
{
    private static readonly ILogger _logger;

    static Handler()
    {
        _logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
    }

    [RootCommand]
    public Task DefaultAsync(
        [Option("l", "视频链接")] string url,
        [Option("s")] string downloadDir)
    {
        var type = "";
        if (Regex.Match(url, @"https?://h5.pipix.com/\S*").Success)
        {
            type = nameof(皮皮虾Async);
        }

        return ParseAsync(type, url, downloadDir);
    }

    [Command("parse")]
    public async Task ParseAsync(
        [Option("t", "视频来源,如 皮皮虾")] string type,
        [Option("l", "视频链接")] string url,
        [Option("s")] string downloadDir)
    {
        if (!IsSupported(type, out var m))
        {
            _logger.Error("不支持的视频来源:{Type}", type);
            return;
        }

        var task = (ValueTask<Result<VideoInfo>>)m!.Invoke(null, new object[] { type, url })!;
        var result = await task.ConfigureAwait(false);
        if (result.Success)
        {
            _logger.Information("{Type} 的链接 {Url} 解析成功", type, url);
            await DownloadIfSetAsync(type, result.Value, downloadDir).ConfigureAwait(false);
        }
        else
        {
            _logger.Error("解析 {Type} 的链接 {Url} 失败,原因:{Reason}", type, url, result.Message);
        }
    }

    static bool IsSupported(string type, out MethodInfo m)
    {
        var t = typeof(Handler);
        m = t.GetMethod(type.EndsWith("Async") ? type : type + "Async", BindingFlags.NonPublic | BindingFlags.Static);
        return m != null;
    }

    static async ValueTask DownloadIfSetAsync(string type, VideoInfo videoInfo, string downloadDir)
    {
        Debug.Assert(videoInfo != null);
        if (string.IsNullOrEmpty(downloadDir))
        {
            _logger.Information("未指定下载目录,跳过 {Type} {Url} 下载", type, videoInfo.Url);
        }
        else
        {
            _logger.Information("正在下载 {Type} {Url} 到 {Dir}", type, videoInfo.Url, downloadDir);
            if (!Directory.Exists(downloadDir))
            {
                Directory.CreateDirectory(downloadDir);
            }

            await DownloadV2Async(videoInfo.Url, downloadDir, videoInfo.Title + ".mp4").ConfigureAwait(false);
        }
    }

    static async ValueTask<Result<VideoInfo>> 皮皮虾Async(string type, string url)
    {
        var location = await GetHeaderAsync(url, "Location", handler =>
        {
            handler.AllowAutoRedirect = false;
        }).ConfigureAwait(false);
        var m = Regex.Match(location, @"/item/(?<id>\d+)\?", RegexOptions.Compiled);
        if (!m.Success)
        {
            _logger.Error("无法解析 {Type}->{Url},获取Location失败", type, url);
            return Result.Error<VideoInfo>("无法解析视频链接");
        }

        var outputString = await GetAsync(
            $"https://is.snssdk.com/bds/cell/detail/?cell_type=1&aid=1319&app_name=super&cell_id={m.Groups["id"]}")
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(outputString))
        {
            _logger.Error("无法解析 {Type}->{Url},获取视频信息失败", type, url);
            return Result.Error<VideoInfo>("无法解析视频链接");
        }

        var output = JsonNode.Parse(outputString);
        Debug.Assert(output != null);
        try
        {
            var status_code= output["status_code"]!.GetValue<int>();
            if (status_code != 0)
            {
                return Result.Error<VideoInfo>(
                    $"解析失败,错误代码:{status_code},原因:{output["message"]!.GetValue<string>().ToString()}");
            }

            var videoUrl = output["data"]!["data"]!["item"]!["origin_video_download"]!["url_list"]![0]!["url"]!
                .GetValue<string>();
            var title = output["data"]["data"]["item"]["content"]!.GetValue<string>();
            var video_id = output["data"]["data"]["item"]["video"]!["video_id"]!.GetValue<string>();
            var author = output["data"]["data"]["item"]["author"]!["name"]!.GetValue<string>();
            var cover = output["data"]["data"]["item"]["cover"]!["url_list"]![0]!["url"]!.GetValue<string>();
            return Result.Ok(new VideoInfo(string.IsNullOrEmpty(title) ? video_id : title, videoUrl, cover, author));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "解析 {Type}->{Url} 失败,原因:{Message},原始响应:{Response}", type, url,ex.Message,outputString);
            throw;
        }
    }

    static async ValueTask<string> GetHeaderAsync(
        string url,
        string header,
        Action<HttpClientHandler> configAction = default)
    {
        using var hc = CreateHttpClient(configAction);
        var r = await hc.GetAsync(url).ConfigureAwait(false);
        return r.Headers.GetValues(header).FirstOrDefault();
    }

    static HttpClient CreateHttpClient(Action<HttpClientHandler> configAction)
    {
        var handler = new HttpClientHandler();
        handler.ClientCertificateOptions = ClientCertificateOption.Manual;
        handler.ServerCertificateCustomValidationCallback =
            (httpRequestMessage, cert, cetChain, policyErrors) => true;
        configAction?.Invoke(handler);
        var hc = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        return hc;
    }

    static async ValueTask<string> GetAsync(
        string url,
        Action<HttpClientHandler> configAction = default)
    {
        using var hc = CreateHttpClient(configAction);
        hc.DefaultRequestHeaders.Clear();
        hc.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 11_0 like Mac OS X) AppleWebKit/604.1.38 (KHTML, like Gecko) Version/11.0 Mobile/15A372 Safari/604.1");

        var r = await hc.GetAsync(url).ConfigureAwait(false);
        return await r.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    static async ValueTask DownloadAsync(
        string url,
        string directory,
        string fileName,
        Action<HttpClientHandler> configAction = default)
    {
        static void OnHttpReceiveProgress(object sender, HttpProgressEventArgs args)
        {
            _logger.Information("已下载 {ProgressPercentage}", args.ProgressPercentage);
        }
        ProgressMessageHandler ph = default;
        try
        {
            using var hc = CreateHttpClient(handler =>
            {
                configAction?.Invoke(handler);
                ph = new ProgressMessageHandler(handler);
            });
            ph.HttpReceiveProgress += OnHttpReceiveProgress;
            using var r = await hc.GetAsync(url).ConfigureAwait(false);
            fileName ??= r.Content?.Headers?.ContentDisposition?.FileName;
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = Guid.NewGuid().ToString("N") + ".mp4";
            }
            var path = Path.Combine(directory, fileName);
            await using var fs = File.Create(path);
            await r.Content.CopyToAsync(fs).ConfigureAwait(false);
        }
        finally
        {
            if (ph != null)
            {
                ph.HttpReceiveProgress -= OnHttpReceiveProgress;
                ph.Dispose();
            }
        }
    }

    static async ValueTask DownloadV2Async(
        string url,
        string directory,
        string fileName,
        Action<HttpClientHandler> configAction = default)
    {
        using var hc = CreateHttpClient(handler =>
            {
                configAction?.Invoke(handler);
            });

        using var r = await hc.GetAsync(url).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        fileName ??= r.Content?.Headers?.ContentDisposition?.FileName;
        if (string.IsNullOrEmpty(fileName))
        {
            fileName = Guid.NewGuid().ToString("N") + ".mp4";
        }
        var path = Path.Combine(directory, fileName);
        const int bufferSize = 8192;
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true);

        var totalLength = r.Content.Headers.ContentLength;
        await using Stream contentStream = await r.Content.ReadAsStreamAsync().ConfigureAwait(false);
        if (totalLength == null)
        {
            try
            {
                totalLength = contentStream.Length;
            }
            catch (NotSupportedException)
            {
                totalLength = -1;
            }
        }
        var totalRead = 0L;
        var buffer = new byte[bufferSize];
        var isMoreToRead = true;

        const int totalTicks = 10;
        var options = new ProgressBarOptions
        {
            ProgressCharacter = '─',
            ProgressBarOnBottom = true
        };
        using var pbar = new ProgressBar((int)totalLength.Value, "", options);
        
        do
        {
            var read = await contentStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0)
            {
                isMoreToRead = false;
            }
            else
            {
                //var percent = totalLength == -1 ? "-" : ((float)(totalRead * 1d / totalLength) * 100).ToString("f2");
                await fs.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                totalRead += read;
                //_logger.Information("已下载 {Percent}%,总大小={Size}MB", percent, totalLength.Value.Bytes().Kilobytes);
                pbar.Tick((int)totalRead);
                //Thread.Sleep(0);
            }
        }
        while (isMoreToRead);
    }
}

class Result
{
    public bool Success { get; set; }
    public string Message { get; set; }

    public static Result Ok() => new Result() { Success = true };
    public static Result Error(string message) => new Result() { Success = false, Message = message };
    public static Result<T> Ok<T>(T result) => new Result<T>() { Success = true, Value = result };
    public static Result<T> Error<T>(string message, T result = default) => new Result<T>() { Success = false, Message = message, Value = result };
}

class Result<T> : Result
{
    public T Value { get; set; }
}

record VideoInfo(string Title, string Url, string Cover, string Author);