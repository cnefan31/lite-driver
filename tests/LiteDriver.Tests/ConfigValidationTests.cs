using LiteDriver.Configuration;
using Xunit;

namespace LiteDriver.Tests;

/// <summary>配置校验必须在占用端口或拨号之前完成。</summary>
public sealed class ConfigValidationTests
{
    private static readonly string[] KnownKinds = ["line-lp"];

    private static FrozenConfig Validate(DriverConfig config, DriverLimits? limits = null) =>
        FrozenConfig.Create(
            config,
            1,
            KnownKinds,
            limits ?? new DriverLimits(),
            static (_, _) => null);

    [Fact]
    public void Should_RejectUnknownKind()
    {
        var result = Validate(new DriverConfig
        {
            Devices =
            [
                new DeviceDescriptor
                {
                    DeviceId = "d1",
                    KindId = "not-registered",
                    Endpoints = [new EndpointDescriptor { Host = "127.0.0.1", Port = 502 }]
                }
            ]
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "kind_unknown");
    }

    [Fact]
    public void Should_RejectDuplicateDeviceId()
    {
        var device = new DeviceDescriptor
        {
            DeviceId = "d1",
            KindId = "line-lp",
            Endpoints = [new EndpointDescriptor { Host = "127.0.0.1", Port = 502 }]
        };

        var result = Validate(new DriverConfig { Devices = [device, device] });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "device_duplicate");
    }

    [Fact]
    public void Should_RejectEmptyEndpoints()
    {
        var result = Validate(new DriverConfig
        {
            Devices = [new DeviceDescriptor { DeviceId = "d1", KindId = "line-lp" }]
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "endpoints_empty");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(-1)]
    public void Should_RejectOutOfRangeEndpointPort(int port)
    {
        var result = Validate(new DriverConfig
        {
            Devices =
            [
                new DeviceDescriptor
                {
                    DeviceId = "d1",
                    KindId = "line-lp",
                    Endpoints = [new EndpointDescriptor { Host = "127.0.0.1", Port = port }]
                }
            ]
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "endpoint_port_range");
    }

    [Fact]
    public void Should_RejectDuplicateListenerPort()
    {
        var result = Validate(new DriverConfig
        {
            Listeners =
            [
                new ListenerDescriptor { ListenerId = "l1", KindId = "line-lp", Host = "127.0.0.1", Port = 9000 },
                new ListenerDescriptor { ListenerId = "l2", KindId = "line-lp", Host = "127.0.0.1", Port = 9000 }
            ]
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "listener_port_conflict");
    }

    [Fact]
    public void Should_AllowEphemeralListenerPortsToCoexist()
    {
        var result = Validate(new DriverConfig
        {
            Listeners =
            [
                new ListenerDescriptor { ListenerId = "l1", KindId = "line-lp", Host = "127.0.0.1", Port = 0 },
                new ListenerDescriptor { ListenerId = "l2", KindId = "line-lp", Host = "127.0.0.1", Port = 0 }
            ]
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Should_RejectInvalidRetryAndHeartbeatRanges()
    {
        var result = Validate(new DriverConfig
        {
            Devices =
            [
                new DeviceDescriptor
                {
                    DeviceId = "d1",
                    KindId = "line-lp",
                    Endpoints = [new EndpointDescriptor { Host = "127.0.0.1", Port = 502 }],
                    Retry = new RetryOptions { InitialMs = 0, Jitter = 5 },
                    Heartbeat = new HeartbeatOptions { IntervalMs = 10, Misses = 0 }
                }
            ]
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "retry_initial_range");
        Assert.Contains(result.Issues, i => i.Code == "retry_jitter_range");
        Assert.Contains(result.Issues, i => i.Code == "heartbeat_interval_range");
        Assert.Contains(result.Issues, i => i.Code == "heartbeat_misses_range");
    }

    [Fact]
    public void Should_RejectMaxSessionsAboveLimit()
    {
        var limits = new DriverLimits { MaxSessionsPerListener = 4 };
        var result = Validate(new DriverConfig
        {
            Listeners =
            [
                new ListenerDescriptor
                {
                    ListenerId = "l1", KindId = "line-lp", Host = "127.0.0.1", Port = 0, MaxSessions = 16
                }
            ]
        }, limits);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "listener_max_sessions_range");
    }

    [Fact]
    public void Should_PropagateKindSettingsValidatorError()
    {
        var result = FrozenConfig.Create(
            new DriverConfig
            {
                Devices =
                [
                    new DeviceDescriptor
                    {
                        DeviceId = "d1",
                        KindId = "line-lp",
                        Endpoints = [new EndpointDescriptor { Host = "127.0.0.1", Port = 502 }]
                    }
                ]
            },
            1,
            KnownKinds,
            new DriverLimits(),
            static (_, _) => "bad settings");

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "kind_settings_invalid");
    }

    [Fact]
    public void Limits_ShouldRejectInconsistentConfiguration()
    {
        var limits = new DriverLimits { MaxSessionsPerListener = 64, MaxTotalSessions = 8 };
        Assert.Throws<ArgumentException>(() => limits.ValidateAndFreeze());
    }

    [Fact]
    public void JsonContext_ShouldRoundTripConfigWithoutReflection()
    {
        const string json = """
        {
          "devices": [
            {
              "deviceId": "press-01",
              "kindId": "line-lp",
              "endpoints": [{ "host": "127.0.0.1", "port": 502 }],
              "retry": { "initialMs": 300, "maxMs": 5000, "jitter": 0.1 },
              "heartbeat": { "enabled": false }
            }
          ],
          "listeners": [
            { "listenerId": "gw", "kindId": "line-lp", "host": "127.0.0.1", "port": 0 }
          ]
        }
        """;

        var config = System.Text.Json.JsonSerializer.Deserialize(json, LiteDriverJsonContext.Default.DriverConfig);

        Assert.NotNull(config);
        Assert.Single(config!.Devices);
        Assert.Equal("press-01", config.Devices[0].DeviceId);
        Assert.Equal(300, config.Devices[0].Retry.InitialMs);
        Assert.Single(config.Listeners);
    }
}
