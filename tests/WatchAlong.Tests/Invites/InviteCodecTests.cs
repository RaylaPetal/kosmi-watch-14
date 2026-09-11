using System.Numerics;
using WatchAlong.Shared.Invites;
using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Screens;
using Xunit;

namespace WatchAlong.Tests.Invites;

public class InviteCodecTests
{
    private static ScreenAnchor MakeAnchor() => new(
        "house_1_2_3_4_5_6",
        new ScreenTransform
        {
            Position = new Vector3(1, 2, 3),
            RotationDegrees = new Vector3(4, 5, 6),
            Scale = new Vector2(3f, 1.6875f),
        });

    [Fact]
    public void Invite_round_trips_with_an_anchor()
    {
        var anchor = MakeAnchor();
        var token = InviteCodec.EncodeInvite("sulync", "Movie night", anchor);

        Assert.StartsWith(InviteCodec.InvitePrefix, token);
        Assert.True(InviteCodec.TryDecodeInvite(token, out var invite));
        Assert.Equal("sulync", invite.RoomCode);
        Assert.Equal("Movie night", invite.Name);
        Assert.Equal(anchor, invite.Anchor);
    }

    [Fact]
    public void Invite_round_trips_without_an_anchor()
    {
        var token = InviteCodec.EncodeInvite("sulync", "Movie night", anchor: null);

        Assert.True(InviteCodec.TryDecodeInvite(token, out var invite));
        Assert.Equal("sulync", invite.RoomCode);
        Assert.Null(invite.Anchor);
    }

    [Fact]
    public void Position_share_round_trips()
    {
        var anchor = MakeAnchor();
        var token = InviteCodec.EncodePositionShare("Ray", anchor, ScreenRenderMode.DepthTested);

        Assert.StartsWith(InviteCodec.PositionSharePrefix, token);
        Assert.True(InviteCodec.TryDecodePositionShare(token, out var share));
        Assert.Equal("Ray", share.Name);
        Assert.Equal(anchor, share.Anchor);
        Assert.Equal(ScreenRenderMode.DepthTested, share.RenderMode);
    }

    [Fact]
    public void Position_share_without_a_render_mode_field_defaults_to_quad()
    {
        // Pre-existing shares (encoded before the "m" field existed) carry no render mode at all.
        var anchorJson = "{\"k\":\"house_1\",\"p\":[1,2,3],\"q\":[4,5,6],\"s\":[3,1.6875]}";
        var token = InviteCodec.PositionSharePrefix + InviteEncodeForTest(v: 1, n: "Ray", anchorJson: anchorJson);

        Assert.True(InviteCodec.TryDecodePositionShare(token, out var share));
        Assert.Equal(ScreenRenderMode.Quad, share.RenderMode);
    }

    [Fact]
    public void Position_share_missing_anchor_field_is_rejected()
    {
        // A position share with no anchor can't be produced by the encoder (its parameter is
        // non-nullable) — the decode-side rejection matters for a hand-tampered/truncated token.
        var withoutAnchor = InviteCodec.PositionSharePrefix + InviteEncodeForTest(v: 1, n: "Ray", anchorJson: null);
        Assert.False(InviteCodec.TryDecodePositionShare(withoutAnchor, out _));
    }

    [Fact]
    public void Over_length_name_is_truncated_before_encoding()
    {
        var longName = new string('x', 100);
        var token = InviteCodec.EncodeInvite("sulync", longName, anchor: null);

        Assert.True(InviteCodec.TryDecodeInvite(token, out var invite));
        Assert.Equal(40, invite.Name.Length);
        Assert.Equal(new string('x', 40), invite.Name);
    }

    [Fact]
    public void Malformed_room_code_fails_to_decode()
    {
        var token = InviteCodec.PositionSharePrefix; // wrong prefix entirely
        Assert.False(InviteCodec.TryDecodeInvite(token, out _));

        var badRoomToken = InviteEncodeForTest(v: 1, r: "has space", n: "x", anchorJson: null);
        Assert.False(InviteCodec.TryDecodeInvite(InviteCodec.InvitePrefix + badRoomToken, out _));
    }

    [Fact]
    public void Out_of_bounds_anchor_fails_to_decode()
    {
        var hugeScaleAnchorJson = "{\"k\":\"house_1\",\"p\":[0,0,0],\"q\":[0,0,0],\"s\":[999,999]}";
        var token = InviteCodec.InvitePrefix + InviteEncodeForTest(v: 1, r: "sulync", n: "x", anchorJson: hugeScaleAnchorJson);
        Assert.False(InviteCodec.TryDecodeInvite(token, out _));
    }

    [Theory]
    [InlineData("not-valid-base64url!!!")]
    [InlineData("")]
    [InlineData("QQ")] // valid base64url but not deflate data
    public void Corrupted_or_truncated_payload_fails_cleanly(string garbage)
    {
        Assert.False(InviteCodec.TryDecodeInvite(InviteCodec.InvitePrefix + garbage, out _));
        Assert.False(InviteCodec.TryDecodePositionShare(InviteCodec.PositionSharePrefix + garbage, out _));
    }

    /// <summary>Builds a raw (not-through-the-encoder) deflate+base64url payload so decode-side validation can be tested independently of the encoder's own guards.</summary>
    private static string InviteEncodeForTest(int v, string? r = null, string? n = null, string? anchorJson = null)
    {
        var json = $"{{\"v\":{v}{(r is not null ? $",\"r\":\"{r}\"" : "")}{(n is not null ? $",\"n\":\"{n}\"" : "")}{(anchorJson is not null ? $",\"a\":{anchorJson}" : "")}}}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(bytes);
        return Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
