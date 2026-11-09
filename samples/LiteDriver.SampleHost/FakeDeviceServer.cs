using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LiteDriver.SampleHost;

/// <summary>
/// 示例设备模拟器：在本机起一个 TCP 服务，按 <see cref="LineDeviceKind"/> 的帧格式应答，
/// 用于在没有真实 PLC 的情况下演示与验证驱动的连接、重连与命令语义。
/// <para>支持通过命令字控制故障：<c>#drop</c> 立即断开当前连接。</para>
/// </summary>
public sealed class FakeDeviceServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _connections;
    private int _commands;

    public FakeDeviceServer(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port { get; private set; }

    public int TotalConnections => Volatile.Read(ref _connections);

    public int TotalCommands => Volatile.Read(ref _commands);

    public void Start()
    {
        _listener.Start(32);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var header = new byte[2];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(stream, header, _cts.Token).ConfigureAwait(false))
                    {
                        return;
                    }

                    var length = BinaryPrimitives.ReadUInt16BigEndian(header);
                    if (length == 0)
                    {
                        return;
                    }

                    var payload = new byte[length];
                    if (!await ReadExactAsync(stream, payload, _cts.Token).ConfigureAwait(false))
                    {
                        return;
                    }

                    Interlocked.Increment(ref _commands);
                    var text = Encoding.ASCII.GetString(payload);

                    if (text == "#drop")
                    {
                        // 模拟设备侧异常断开：不发 FIN 之外的任何信息，直接关闭。
                        return;
                    }

                    var reply = text switch
                    {
                        "PING" => "PONG",
                        "FAIL" => "ERR unsupported",
                        _ => "OK " + text
                    };

                    await WriteFrameAsync(stream, reply, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // 客户端断开或服务停止，均属预期。
            }
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private static async Task WriteFrameAsync(NetworkStream stream, string text, CancellationToken ct)
    {
        var payload = Encoding.ASCII.GetBytes(text);
        var frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(2));
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 停止超时属预期。
            }
        }

        _cts.Dispose();
    }
}
