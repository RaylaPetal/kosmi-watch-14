using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using WatchAlong.Shared.Kosmi;
using WatchAlong.Shared.Screens;

namespace WatchAlong.Shared.Invites;

/// <summary>A decoded, validated <c>WA1:</c> invite (group-invites spec "An invite is self-contained...").</summary>
public sealed record DecodedInvite(string RoomCode, string Name, ScreenAnchor? Anchor);

/// <summary>A decoded, validated <c>WA1P:</c> position-share message (group-invites spec "A user can share their placed screen's current position on demand").</summary>
public sealed record DecodedPositionShare(string Name, ScreenAnchor Anchor);

/// <summary>
/// Encodes/decodes the two self-contained, serverless chat tokens from design.md §9.3: a room
/// invite (<c>WA1:</c>, room optional-anchor) and a standalone position-share (<c>WA1P:</c>,
/// required anchor, no room). Both are <c>base64url(deflate(json))</c> — no network, no I/O — so
/// decoding is pure and safe to run directly on untrusted chat text (group-invites spec
/// "Malformed or invalid invite/position share is ignored").
/// </summary>
public static class InviteCodec
{
    public const string InvitePrefix = "WA1:";
    public const string PositionSharePrefix = "WA1P:";

    private const int CurrentVersion = 1;

    // design.md §9.4's abuse-limit bound, reused here for the invite/share name (tasks.md 1.2).
    private const int MaxNameLength = 40;

    // Matches PlacementWindow's own width drag-field clamp (0.2-20 yalms); height is always
    // derived at 16:9, so its bound follows from the same width bound (tasks.md 1.3).
    private const float MinWidth = 0.2f;
    private const float MaxWidth = 20f;
    private const float MinHeight = MinWidth * 9f / 16f;
    private const float MaxHeight = MaxWidth * 9f / 16f;

    // No UI ever produces a screen far from the player; this is only here to reject corrupted or
    // tampered coordinates, not to model real zone bounds.
    private const float MaxPositionMagnitude = 5000f;

    /// <summary>Encodes an invite for <paramref name="roomCode"/>, optionally carrying <paramref name="anchor"/>. Throws <see cref="ArgumentException"/> for an invalid room code or empty name.</summary>
    public static string EncodeInvite(string roomCode, string name, ScreenAnchor? anchor)
    {
        if (!TryNormalizeRoomCode(roomCode, out var normalizedRoomCode))
            throw new ArgumentException("Not a valid Kosmi room code.", nameof(roomCode));

        var dto = new InviteDto(CurrentVersion, normalizedRoomCode, ClampName(name), anchor is null ? null : ToAnchorDto(anchor));
        return InvitePrefix + EncodePayload(dto);
    }

    /// <summary>Decodes and validates an invite token (with its <see cref="InvitePrefix"/>). Never throws — a malformed or out-of-bounds token simply fails to decode.</summary>
    public static bool TryDecodeInvite(string token, out DecodedInvite invite)
    {
        invite = null!;
        if (!token.StartsWith(InvitePrefix, StringComparison.Ordinal))
            return false;

        if (!TryDecodePayload<InviteDto>(token[InvitePrefix.Length..], out var dto))
            return false;

        if (dto.V != CurrentVersion || string.IsNullOrEmpty(dto.N) || !TryNormalizeRoomCode(dto.R, out var roomCode))
            return false;

        ScreenAnchor? anchor = null;
        if (dto.A is not null && !TryFromAnchorDto(dto.A, out anchor))
            return false;

        invite = new DecodedInvite(roomCode, dto.N, anchor);
        return true;
    }

    /// <summary>Encodes a standalone position-share message for <paramref name="anchor"/>. Throws <see cref="ArgumentException"/> for an empty name.</summary>
    public static string EncodePositionShare(string name, ScreenAnchor anchor)
    {
        var dto = new ShareDto(CurrentVersion, ClampName(name), ToAnchorDto(anchor));
        return PositionSharePrefix + EncodePayload(dto);
    }

    /// <summary>Decodes and validates a position-share token (with its <see cref="PositionSharePrefix"/>). Never throws.</summary>
    public static bool TryDecodePositionShare(string token, out DecodedPositionShare share)
    {
        share = null!;
        if (!token.StartsWith(PositionSharePrefix, StringComparison.Ordinal))
            return false;

        if (!TryDecodePayload<ShareDto>(token[PositionSharePrefix.Length..], out var dto))
            return false;

        if (dto.V != CurrentVersion || string.IsNullOrEmpty(dto.N) || dto.A is null || !TryFromAnchorDto(dto.A, out var anchor))
            return false;

        share = new DecodedPositionShare(dto.N, anchor!);
        return true;
    }

    private static string ClampName(string name)
    {
        var stripped = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (stripped.Length == 0)
            throw new ArgumentException("Name must not be empty.", nameof(name));

        return stripped.Length > MaxNameLength ? stripped[..MaxNameLength] : stripped;
    }

    private static bool TryNormalizeRoomCode(string roomCode, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(roomCode))
            return false;

        // Reuses KosmiRoomUrl's own validated room-code shape by round-tripping through
        // ToUrl/TryParse, rather than re-implementing that regex here (tasks.md 1.3) — this is
        // the only code path anywhere that decides "is this a valid room code".
        return KosmiRoomUrl.TryParse(KosmiRoomUrl.ToUrl(roomCode.Trim()), out normalized);
    }

    private static AnchorDto ToAnchorDto(ScreenAnchor anchor) => new(
        anchor.LocationKey,
        [anchor.Transform.Position.X, anchor.Transform.Position.Y, anchor.Transform.Position.Z],
        [anchor.Transform.RotationDegrees.X, anchor.Transform.RotationDegrees.Y, anchor.Transform.RotationDegrees.Z],
        [anchor.Transform.Scale.X, anchor.Transform.Scale.Y]);

    private static bool TryFromAnchorDto(AnchorDto dto, out ScreenAnchor? anchor)
    {
        anchor = null;
        if (string.IsNullOrWhiteSpace(dto.K))
            return false;
        if (dto.P is not { Length: 3 } || dto.Q is not { Length: 3 } || dto.S is not { Length: 2 })
            return false;

        var position = new Vector3((float)dto.P[0], (float)dto.P[1], (float)dto.P[2]);
        var rotation = new Vector3((float)dto.Q[0], (float)dto.Q[1], (float)dto.Q[2]);
        var scale = new Vector2((float)dto.S[0], (float)dto.S[1]);

        if (!IsFinite(position) || !IsFinite(rotation) || !IsFinite(scale))
            return false;
        if (position.Length() > MaxPositionMagnitude)
            return false;
        if (scale.X < MinWidth || scale.X > MaxWidth || scale.Y < MinHeight || scale.Y > MaxHeight)
            return false;

        anchor = new ScreenAnchor(dto.K, new ScreenTransform { Position = position, RotationDegrees = rotation, Scale = scale });
        return true;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool IsFinite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    private static string EncodePayload<T>(T dto)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(dto);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json);
        return Base64UrlEncode(output.ToArray());
    }

    /// <summary>
    /// Decoding untrusted chat text is a trust boundary (group-invites spec "Malformed or
    /// invalid invite is ignored") — deliberately catches every exception rather than
    /// enumerating base64/deflate/JSON failure modes one at a time.
    /// </summary>
    private static bool TryDecodePayload<T>(string encoded, out T dto)
    {
        dto = default!;
        try
        {
            var compressed = Base64UrlDecode(encoded);
            using var input = new MemoryStream(compressed);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);

            var decoded = JsonSerializer.Deserialize<T>(output.ToArray());
            if (decoded is null)
                return false;

            dto = decoded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }

    private sealed record AnchorDto(
        [property: JsonPropertyName("k")] string K,
        [property: JsonPropertyName("p")] double[] P,
        [property: JsonPropertyName("q")] double[] Q,
        [property: JsonPropertyName("s")] double[] S);

    private sealed record InviteDto(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("r")] string R,
        [property: JsonPropertyName("n")] string N,
        [property: JsonPropertyName("a")] AnchorDto? A);

    private sealed record ShareDto(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("n")] string N,
        [property: JsonPropertyName("a")] AnchorDto? A);
}
