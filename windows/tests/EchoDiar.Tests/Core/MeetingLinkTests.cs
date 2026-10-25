using EchoDiar.Core.Calendar;

namespace EchoDiar.Tests.Core;

// Port of macOS MeetingLinkTests: guards which calendar links the app treats as meetings. Event content is untrusted,
// so only https links on allowlisted hosts count.
public sealed class MeetingLinkTests
{
    [Theory]
    // A link inside the notes text is found.
    [InlineData(null, null, null, "Join: https://us02web.zoom.us/j/123?pwd=x thanks", "Zoom https://us02web.zoom.us/j/123?pwd=x")]
    // Plain http and a look-alike host are refused.
    [InlineData("http://zoom.us/j/1", null, null, null, null)]
    [InlineData(null, null, null, "https://zoom.us.evil.com/j/1", null)]
    [InlineData(null, null, null, "https://evilzoom.us/j/1", null)]
    // An unrelated https link is not a meeting.
    [InlineData(null, null, "https://example.com/room", null, null)]
    // The event URL wins over the notes.
    [InlineData("https://meet.google.com/abc-defg-hij", null, null, "https://zoom.us/j/1",
        "Google Meet https://meet.google.com/abc-defg-hij")]
    // Windows only: the online meeting link field counts as a URL field, ahead of the location.
    [InlineData(null, "https://teams.microsoft.com/l/meetup-join/1", "https://zoom.us/j/1", null,
        "Microsoft Teams https://teams.microsoft.com/l/meetup-join/1")]
    // The location wins over the notes; trailing punctuation is not part of the link.
    [InlineData(null, null, "Room 4 (https://acme.webex.com/meet/bob).", "https://zoom.us/j/1",
        "Webex https://acme.webex.com/meet/bob")]
    // Credentials in the link and a non-default port are refused.
    [InlineData("https://user@zoom.us/j/1", null, null, null, null)]
    [InlineData(null, null, null, "https://zoom.us:8443/j/1", null)]
    public void Find(string? uri, string? onlineMeetingLink, string? location, string? notes, string? expected)
    {
        var link = MeetingLink.Find(uri is null ? null : new Uri(uri), onlineMeetingLink, location, notes);

        Assert.Equal(expected, link is null ? null : $"{link.ServiceName} {link.Url.AbsoluteUri}");
    }
}
