using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LiteDriver.Orchestration;
using NanoServer;
using NanoServer.Http;

namespace LiteDriver.Orchestration.Server;

/// <summary>编排服务选项。</summary>
public sealed class OrchestrationServerOptions
{
    /// <summary>监听地址。默认 127.0.0.1（仅本机可访问）。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>监听端口。0 表示由系统分配。</summary>
    public int Port { get; set; } = 8099;

    /// <summary>页面标题。</summary>
    public string Title { get; set; } = "LiteDriver 功能编排";
}

/// <summary>
/// 编排服务：用 NanoServer 提供拖拽编排页面与 REST API。
/// 页面与 API 都跑在同一进程内，调用 <see cref="OrchestrationEngine"/> 完成热应用。
/// </summary>
public sealed class OrchestrationServer : IAsyncDisposable
{
    private readonly OrchestrationEngine _engine;
    private readonly OrchestrationServerOptions _options;
    private NanoWebServer? _server;
    private string _pageHtml = "";

    /// <summary>创建编排服务。</summary>
    public OrchestrationServer(
        OrchestrationEngine engine,
        OrchestrationServerOptions? options = null,
        IEnumerable<ProtocolRegistration>? protocols = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _options = options ?? new OrchestrationServerOptions();
        _ = protocols;
    }

    /// <summary>实际监听端口（<c>Port=0</c> 时由系统分配）。</summary>
    public int Port { get; private set; }

    /// <summary>启动服务。返回后即可在浏览器访问。</summary>
    public void Start()
    {
        var server = new NanoWebServer();

        foreach (var path in new[]
                 {
                     "/", "/index.html", "/api/health", "/api/state", "/api/protocols",
                     "/api/validate", "/api/apply", "/api/graph"
                 })
        {
            server.AllowAnonymous(path);
        }

        server.Map("/", (req, res) => WritePageAsync(res));
        server.Map("/index.html", (req, res) => WritePageAsync(res));

        server.Map("/api/health", (req, res) => JsonAsync(res,
            ApiEnvelope<HealthEnvelope>.Success(new HealthEnvelope
            {
                Status = "ok",
                Revision = _engine.CurrentGraph.Revision
            }),
            OrchestrationJsonContext.Relaxed.ApiEnvelopeHealthEnvelope));

        server.Map("/api/protocols", (req, res) => JsonAsync(res,
            ApiEnvelope<ProtocolOption[]>.Success(
                _engine.Protocols.Select(p => new ProtocolOption(p.KindId, p.DisplayName, p.Description, p.Fields))
                    .ToArray()),
            OrchestrationJsonContext.Relaxed.ApiEnvelopeProtocolOptionArray));

        server.Map("/api/state", (req, res) => JsonAsync(res,
            ApiEnvelope<OrchestrationStatus>.Success(_engine.Status()),
            OrchestrationJsonContext.Relaxed.ApiEnvelopeOrchestrationStatus));

        server.Map("/api/graph", (req, res) => JsonAsync(res,
            ApiEnvelope<OrchestrationGraph>.Success(_engine.CurrentGraph),
            OrchestrationJsonContext.Relaxed.ApiEnvelopeOrchestrationGraph));

        server.Map("/api/validate", async (req, res) =>
        {
            try
            {
                var graph = await ReadGraphAsync(req).ConfigureAwait(false);
                if (graph is null)
                {
                    await FailAsync(res, "请求体不是合法编排图").ConfigureAwait(false);
                    return;
                }

                var result = _engine.Validate(graph);
                await JsonAsync(res,
                    ApiEnvelope<ValidateEnvelope>.Success(new ValidateEnvelope
                    {
                        Ok = result.Ok,
                        Issues = result.Issues
                    }),
                    OrchestrationJsonContext.Relaxed.ApiEnvelopeValidateEnvelope).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await FailAsync(res, ex.Message).ConfigureAwait(false);
            }
        });

        server.Map("/api/apply", async (req, res) =>
        {
            try
            {
                var graph = await ReadGraphAsync(req).ConfigureAwait(false);
                if (graph is null)
                {
                    await FailAsync(res, "请求体不是合法编排图").ConfigureAwait(false);
                    return;
                }

                var report = await _engine.ApplyAsync(graph).ConfigureAwait(false);
                await JsonAsync(res,
                    ApiEnvelope<ApplyReport>.Success(report),
                    OrchestrationJsonContext.Relaxed.ApiEnvelopeApplyReport).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await FailAsync(res, ex.Message).ConfigureAwait(false);
            }
        });

        server.Start(_options.Host, _options.Port == 0 ? 0 : _options.Port);
        Port = _options.Port;
        _server = server;
        _pageHtml = LoadPage();
    }

    /// <summary>停止服务。</summary>
    public async Task StopAsync()
    {
        if (_server is not null)
        {
            await _server.StopAsync().ConfigureAwait(false);
            _server = null;
        }
    }

    private async Task WritePageAsync(HttpListenerResponse res)
    {
        if (_pageHtml.Length == 0)
        {
            _pageHtml = LoadPage(_options.Title);
        }

        var bytes = Encoding.UTF8.GetBytes(_pageHtml);
        res.ContentType = "text/html; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        res.Close();
    }

    private static string LoadPage(string title = "LiteDriver 功能编排")
    {
        var asm = typeof(OrchestrationServer).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return $"<h1>{title}</h1><p>index.html 未嵌入程序集。</p>";
        }

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("{{TITLE}}", title, StringComparison.Ordinal);
    }

    private static async Task<OrchestrationGraph?> ReadGraphAsync(HttpListenerRequest req)
    {
        var body = await req.ReadBodyAsStringAsync(CancellationToken.None).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(body, OrchestrationJsonContext.Default.OrchestrationGraph);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>用源生成的 JsonTypeInfo 序列化，AOT 下零反射。</summary>
    private static async Task JsonAsync<T>(HttpListenerResponse res, T payload, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(payload, typeInfo);
        await res.WriteJsonAsync(json).ConfigureAwait(false);
    }

    /// <summary>失败响应。</summary>
    private static Task FailAsync(HttpListenerResponse res, string error) =>
        JsonAsync(res, ApiEnvelope.Failure(error), OrchestrationJsonContext.Relaxed.ApiEnvelope);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
