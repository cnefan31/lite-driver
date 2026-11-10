using System.Buffers;
using System.Text;
using LiteDriver.Configuration;
using LiteDriver.Protocol;
using Xunit;

namespace LiteDriver.Tests;

/// <summary>起始/结束标记分帧器的边界行为。</summary>
public sealed class DelimitedFrameTests
{
    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    [Fact]
    public void HexMarker_ShouldParseCommonForms()
    {
        Assert.True(HexMarker.TryParse("02", out var a, out _));
        Assert.Equal("02", Hex(a));

        Assert.True(HexMarker.TryParse("03ff", out var b, out _));
        Assert.Equal("03FF", Hex(b));

        Assert.True(HexMarker.TryParse("0x02,0x03", out var c, out _));
        Assert.Equal("0203", Hex(c));

        Assert.True(HexMarker.TryParse("FF FF FF FF", out var d, out _));
        Assert.Equal("FFFFFFFF", Hex(d));

        // 空表示未配置
        Assert.True(HexMarker.TryParse("", out var e, out _));
        Assert.Empty(e);
        Assert.True(HexMarker.TryParse(null, out var f, out _));
        Assert.Empty(f);
    }

    [Theory]
    [InlineData("0")]      // 奇数位
    [InlineData("0G")]     // 非法字符
    [InlineData("02-0")]   // 奇数位
    public void HexMarker_ShouldRejectInvalid(string text)
    {
        Assert.False(HexMarker.TryParse(text, out _, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void HexMarker_ShouldRejectTooLong()
    {
        var tooLong = new string('A', (HexMarker.MaxLength + 1) * 2);
        Assert.False(HexMarker.TryParse(tooLong, out _, out var err));
        Assert.Contains("超过上限", err);
    }

    [Fact]
    public void Constructor_ShouldRejectBothEmpty()
    {
        Assert.Throws<ArgumentException>(() =>
            new DelimitedFrameDecoder([], [], 1024));
    }

    [Fact]
    public void StartAndEnd_ShouldExtractCompleteFrame()
    {
        var decoder = new DelimitedFrameDecoder([0x02], [0x03], 1024);
        var stream = new byte[] { 0x02, 0x41, 0x42, 0x03 };

        var result = decoder.Decode(stream, out var consumed, out var frame);

        Assert.Equal(FrameDecodeResult.Frame, result);
        Assert.Equal(4, consumed);
        // 交付的帧包含起始与结束标记
        Assert.Equal("02414203", Hex(frame));
    }

    [Fact]
    public void StartAndEnd_ShouldWaitForHalfFrame()
    {
        var decoder = new DelimitedFrameDecoder([0x02], [0x03], 1024);
        var result = decoder.Decode(new byte[] { 0x02, 0x41 }, out var consumed, out _);

        Assert.Equal(FrameDecodeResult.NeedMore, result);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void MultiByteMarkers_ShouldWork()
    {
        // 起始 02，结束 03FF（两字节）
        var decoder = new DelimitedFrameDecoder([0x02], [0x03, 0xFF], 1024);
        var stream = new byte[] { 0x02, 0x41, 0x03, 0xFF };

        Assert.Equal(FrameDecodeResult.Frame, decoder.Decode(stream, out var consumed, out var frame));
        Assert.Equal(4, consumed);
        Assert.Equal("024103FF", Hex(frame));
    }

    [Fact]
    public void LeadingGarbage_ShouldBeDiscardedAndReported()
    {
        var decoder = new DelimitedFrameDecoder([0x02], [0x03], 1024);
        var stream = new byte[] { 0xAA, 0xBB, 0x02, 0x41, 0x03 };

        // 第一次解析：报告丢弃 2 字节垃圾
        var r1 = decoder.Decode(stream, out var consumed1, out _);
        Assert.Equal(FrameDecodeResult.NeedMore, r1);
        Assert.Equal(2, consumed1);

        // 对齐后再解析出完整帧
        var r2 = decoder.Decode(stream.AsSpan(consumed1), out var consumed2, out var frame);
        Assert.Equal(FrameDecodeResult.Frame, r2);
        Assert.Equal(3, consumed2);
        Assert.Equal("024103", Hex(frame));
    }

    [Fact]
    public void EndOnly_ShouldTreatEverythingBeforeTerminatorAsOneFrame()
    {
        // 只配结束标记 ffffffff
        var decoder = new DelimitedFrameDecoder([], [0xFF, 0xFF, 0xFF, 0xFF], 1024);
        var stream = new byte[] { 0x01, 0x02, 0x03, 0xFF, 0xFF, 0xFF, 0xFF };

        Assert.Equal(FrameDecodeResult.Frame, decoder.Decode(stream, out var consumed, out var frame));
        // 3 字节载荷 + 4 字节终止符 = 7
        Assert.Equal(7, consumed);
        Assert.Equal("010203FFFFFFFF", Hex(frame));
    }

    [Fact]
    public void EndOnly_ShouldWaitWhenNoTerminator()
    {
        var decoder = new DelimitedFrameDecoder([], [0xFF, 0xFF, 0xFF, 0xFF], 1024);
        var result = decoder.Decode(new byte[] { 0x01, 0x02, 0xFF }, out var consumed, out _);

        Assert.Equal(FrameDecodeResult.NeedMore, result);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void EndOnly_ShouldIncludeLeadingBytesByDesign()
    {
        // 已知约束：仅配结束标记时无法区分「前导垃圾」与「帧内容」，
        // 结束标记之前的全部字节都会被当作载荷。此处锁定该语义，避免被误当成 bug 修掉。
        var decoder = new DelimitedFrameDecoder([], [0xFF, 0xFF, 0xFF, 0xFF], 1024);
        var stream = new byte[] { 0x11, 0x22, 0xDE, 0xAD, 0xFF, 0xFF, 0xFF, 0xFF };

        Assert.Equal(FrameDecodeResult.Frame, decoder.Decode(stream, out var consumed, out var frame));
        Assert.Equal(8, consumed);
        Assert.Equal("1122DEADFFFFFFFF", Hex(frame));
    }

    [Fact]
    public void StartOnly_ShouldSplitOnNextStart()
    {
        var decoder = new DelimitedFrameDecoder([0x02], [], 1024);
        // 两个起始标记：第一帧到第二个 start 之前
        var stream = new byte[] { 0x02, 0x41, 0x02, 0x42 };

        Assert.Equal(FrameDecodeResult.Frame, decoder.Decode(stream, out var consumed, out var frame));
        Assert.Equal(2, consumed);
        Assert.Equal("0241", Hex(frame));

        // 剩余 [02 42] 已到流末尾，没有下一个 start 可作边界 -> 需要更多数据。
        Assert.Equal(FrameDecodeResult.NeedMore, decoder.Decode(stream.AsSpan(2), out var c2, out _));
        Assert.Equal(0, c2);
    }

    [Fact]
    public void StartOnly_ShouldWaitForNextStart()
    {
        var decoder = new DelimitedFrameDecoder([0x02], [], 1024);
        var result = decoder.Decode(new byte[] { 0x02, 0x41, 0x42 }, out var consumed, out _);

        Assert.Equal(FrameDecodeResult.NeedMore, result);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void ShouldBeFatal_WhenExceedingMaxFrameSize()
    {
        var decoder = new DelimitedFrameDecoder([0x02], [0x03], 8);
        var huge = new byte[64];
        huge[0] = 0x02;

        Assert.Equal(FrameDecodeResult.Fatal, decoder.Decode(huge, out _, out _));
    }

    [Fact]
    public void Encoder_ShouldWrapPayloadWithMarkers()
    {
        var encoder = new DelimitedFrameEncoder([0x02], [0x03, 0xFF]);
        var writer = new ArrayBufferWriter<byte>();
        encoder.Encode("AB"u8, writer);

        Assert.Equal("02414203FF", Convert.ToHexString(writer.WrittenSpan));
    }

    [Fact]
    public void Encoder_ShouldRejectBothEmpty()
    {
        Assert.Throws<ArgumentException>(() => new DelimitedFrameEncoder([], []));
    }

    [Fact]
    public void RoundTrip_ShouldBeSymmetric()
    {
        byte[] start = [0x7E];
        byte[] end = [0x7F];
        var encoder = new DelimitedFrameEncoder(start, end);
        var decoder = new DelimitedFrameDecoder(start, end, 1024);

        var writer = new ArrayBufferWriter<byte>();
        encoder.Encode("hello"u8, writer);

        Assert.Equal(FrameDecodeResult.Frame, decoder.Decode(writer.WrittenSpan, out var consumed, out var frame));
        Assert.Equal(writer.WrittenCount, consumed);
        // 剥掉标记后应还原原始载荷
        var payload = frame[(start.Length)..^end.Length];
        Assert.Equal("hello", Encoding.ASCII.GetString(payload));
    }
}

/// <summary>DelimitedKind 的注册期与配置期行为。</summary>
public sealed class DelimitedKindTests
{
    [Fact]
    public void CreateFactory_ShouldFixMarkersAtRegistration()
    {
        var factory = DelimitedKind.CreateFactory("02", "03");
        var ctx = new ProtocolContext("d", ConnectionDirection.Outbound, new DriverLimits(), null);
        var kind = factory(ctx);
        var protocol = kind.CreateProtocol(ctx);

        var writer = new ArrayBufferWriter<byte>();
        protocol.Encoder.Encode("A"u8, writer);
        Assert.Equal("024103", Convert.ToHexString(writer.WrittenSpan));
    }

    [Fact]
    public void CreateFactory_ShouldRejectBothEmpty()
    {
        Assert.Throws<ArgumentException>(() => DelimitedKind.CreateFactory(null, ""));
    }

    [Fact]
    public void CreateFactory_ShouldRejectInvalidHex()
    {
        Assert.Throws<ArgumentException>(() => DelimitedKind.CreateFactory("0", null));
    }

    [Fact]
    public void ValidateSettings_ShouldAcceptValidConfig()
    {
        var settings = Encoding.UTF8.GetBytes("""{"start":"02","end":"03"}""");
        Assert.Null(DelimitedKind.ValidateSettings(settings));
    }

    [Fact]
    public void ValidateSettings_ShouldAcceptEndOnly()
    {
        var settings = Encoding.UTF8.GetBytes("""{"end":"ffffffff"}""");
        Assert.Null(DelimitedKind.ValidateSettings(settings));
    }

    [Fact]
    public void ValidateSettings_ShouldRejectEmptySettings()
    {
        var error = DelimitedKind.ValidateSettings([]);
        Assert.NotNull(error);
        Assert.Contains("至少一个", error);
    }

    [Fact]
    public void ValidateSettings_ShouldRejectBothEmptyStrings()
    {
        var settings = Encoding.UTF8.GetBytes("""{"start":"","end":""}""");
        var error = DelimitedKind.ValidateSettings(settings);
        Assert.NotNull(error);
        Assert.Contains("至少要配置一个非空值", error);
    }

    [Fact]
    public void ValidateSettings_ShouldRejectBadHex()
    {
        var settings = Encoding.UTF8.GetBytes("""{"end":"0G"}""");
        var error = DelimitedKind.ValidateSettings(settings);
        Assert.NotNull(error);
        Assert.Contains("非法", error);
    }

    [Fact]
    public void Kind_ShouldReadMarkersFromConfig()
    {
        var json = System.Text.Json.JsonSerializer.Deserialize(
            """{"start":"AA","end":"BBCC"}""", LiteDriverJsonContext.Default.JsonElement);
        var ctx = new ProtocolContext("d", ConnectionDirection.Outbound, new DriverLimits(), json);

        var kind = (DelimitedKind)new DelimitedKind(ctx);
        Assert.Equal("AA", Convert.ToHexString(kind.MarkerStart));
        Assert.Equal("BBCC", Convert.ToHexString(kind.MarkerEnd));
    }
}
