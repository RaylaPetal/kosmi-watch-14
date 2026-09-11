using WatchAlong.Shared.Kosmi;
using Xunit;

namespace WatchAlong.Tests.Kosmi;

public class KosmiErrorMessagesTests
{
    [Theory]
    [InlineData("CefInitFailed")]
    [InlineData("NavigationBlocked")]
    [InlineData("CodecUnsupported")]
    [InlineData("AudioInitFailed")]
    [InlineData("RendererStartFailed")]
    [InlineData("RoomNotFound")]
    [InlineData("LoginRequired")]
    [InlineData("Kicked")]
    [InlineData("Full")]
    public void Every_documented_code_has_its_own_message(string code)
    {
        var message = KosmiErrorMessages.Describe(code);

        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void Every_documented_code_maps_to_a_distinct_message()
    {
        string[] codes =
        [
            "CefInitFailed", "NavigationBlocked", "CodecUnsupported", "AudioInitFailed",
            "RendererStartFailed", "RoomNotFound", "LoginRequired", "Kicked", "Full",
        ];

        var messages = codes.Select(KosmiErrorMessages.Describe).ToList();

        Assert.Equal(messages.Count, messages.Distinct().Count());
    }

    [Fact]
    public void Unknown_code_falls_back_to_a_generic_message_instead_of_throwing()
    {
        var message = KosmiErrorMessages.Describe("SomethingNeverDocumented");

        Assert.False(string.IsNullOrWhiteSpace(message));
    }
}
