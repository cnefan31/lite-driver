using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiteDriver.TestBench.Protocols;
using NanoServer;
using NanoServer.Http;

namespace LiteDriver.TestBench;

/// <summary>测试台 API 使用的 JSON 契约（源生成，禁用反射式序列化）。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProtocolInfo[]))]
[JsonSerializable(typeof(StartRequest))]
[JsonSerializable(typeof(SendRequest))]
[JsonSerializable(typeof(ApiResult))]
[JsonSerializable(typeof(BenchStatus))]
[JsonSerializable(typeof(ConnectivityReport))]
[JsonSerializable(typeof(CheckStep))]
[JsonSerializable(typeof(CheckStep[]))]
[JsonSerializable(typeof(BenchEvent))]
[JsonSerializable(typeof(BenchEvent[]))]
public partial class BenchJsonContext : JsonSerializerContext
{
}

/// <summary>协议目录项（前端用）。</summary>
public sealed record ProtocolInfo(
    string Id,
    string DisplayName,
    string Description,
    string Encoding,
    bool SupportsHeartbeat,
    bool SupportsHandshake);

/// <summary>启动请求。</summary>
public sealed record StartRequest(string ClientProtocolId, string ServerProtocolId, int Port);

/// <summary>发送请求。</summary>
public sealed record SendRequest(string Message);

/// <summary>统一 API 响应。</summary>
public sealed record ApiResult(bool Ok, string? Error, JsonElement? Data);

/// <summary>
/// 用 NanoServer 承载测试台：静态页 + JSON API。
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(BenchJsonContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<int> Main(string[] args)
    {
        var port = 8099;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var p))
            {
                port = p;
            }
        }

        await using var runtime = new BenchRuntime();
        var server = new NanoWebServer();
        // 测试台无鉴权需求；显式声明匿名路径，避免启用鉴权时被拦。
        foreach (var path in new[] { "/", "/index.html", "/api/protocols", "/api/status", "/api/start", "/api/stop", "/api/connectivity" })
        {
            server.AllowAnonymous(path);
        }

        // ---- 页面 ----
        server.Map("/", static (req, res) => WriteIndexAsync(res));
        server.Map("/index.html", static (req, res) => WriteIndexAsync(res));

        // ---- API ----
        server.Map("/api/protocols", (req, res) => JsonAsync(res, new ApiResult(
            true,
            null,
            ToElement(ProtocolCatalog.List.Select(p => new ProtocolInfo(
                p.Id, p.DisplayName, p.Description, p.Encoding, p.SupportsHeartbeat, p.SupportsHandshake)).ToArray()))));

        server.Map("/api/status", (req, res) => JsonAsync(res, new ApiResult(true, null, ToElement(runtime.Status()))));

        server.Map("/api/start", async (req, res) =>
        {
            try
            {
                var body = await req.ReadBodyAsStringAsync().ConfigureAwait(false);
                var dto = JsonSerializer.Deserialize(body, BenchJsonContext.Default.StartRequest);
                if (dto is null)
                {
                    await JsonAsync(res, new ApiResult(false, "请求体无法解析", null)).ConfigureAwait(false);
                    return;
                }

                var status = await runtime.StartAsync(
                    dto.ClientProtocolId, dto.ServerProtocolId, dto.Port, CancellationToken.None).ConfigureAwait(false);
                await JsonAsync(res, new ApiResult(true, null, ToElement(status))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await JsonAsync(res, new ApiResult(false, ex.Message, null)).ConfigureAwait(false);
            }
        });

        server.Map("/api/stop", async (req, res) =>
        {
            var status = await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await JsonAsync(res, new ApiResult(true, null, ToElement(status))).ConfigureAwait(false);
        });

        server.Map("/api/connectivity", async (req, res) =>
        {
            try
            {
                var body = await req.ReadBodyAsStringAsync().ConfigureAwait(false);
                var dto = string.IsNullOrWhiteSpace(body)
                    ? new SendRequest("hello-from-bench")
                    : JsonSerializer.Deserialize(body, BenchJsonContext.Default.SendRequest) ?? new SendRequest("hello");
                var report = await runtime.RunConnectivityAsync(dto.Message, CancellationToken.None).ConfigureAwait(false);
                await JsonAsync(res, new ApiResult(true, null, ToElement(report))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await JsonAsync(res, new ApiResult(false, ex.Message, null)).ConfigureAwait(false);
            }
        });

        Console.WriteLine($"LiteDriver 测试台已启动： http://127.0.0.1:{port}/");
        Console.WriteLine("按 Ctrl+C 退出。");

        // NanoServer 的真实入口是同步 Start(port) 与异步 StopAsync()。
        server.Start(port);

        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopped.TrySetResult(true);
        };

        await stopped.Task.ConfigureAwait(false);
        await server.StopAsync().ConfigureAwait(false);
        await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    /// <summary>把嵌入的 index.html 写出。</summary>
    private static async Task WriteIndexAsync(System.Net.HttpListenerResponse res)
    {
        var html = LoadIndexHtml();
        var bytes = Encoding.UTF8.GetBytes(html);
        res.ContentType = "text/html; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        res.Close();
    }

    /// <summary>从嵌入资源读取前端页面。</summary>
    private static string LoadIndexHtml()
    {
        var asm = typeof(Program).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return "<h1>index.html 未嵌入程序集</h1>";
        }

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task JsonAsync(System.Net.HttpListenerResponse res, ApiResult payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await res.WriteJsonAsync(json).ConfigureAwait(false);
    }

    /// <summary>把任意对象转成 JsonElement，走已注册的源生成上下文。</summary>
    private static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);
}
