extern alias ApiHost;

using ApiRooms = ApiHost::Karaokay.Api.Endpoints.RoomsEndpoints;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Guards the QR payload contract: phone cameras require an absolute URL, while a configured
/// <c>PublicBaseUrl</c> must always take precedence.
/// </summary>
public sealed class QrJoinUrlTests
{
    [Theory]
    [InlineData(null, "http", "127.0.0.1:5255", "http://127.0.0.1:5255/remote?code=ABCD")]
    [InlineData("", "https", "stage.local:7120", "https://stage.local:7120/remote?code=ABCD")]
    [InlineData("   ", "http", "localhost", "http://localhost/remote?code=ABCD")]
    [InlineData("https://karaokay.example", "http", "localhost", "https://karaokay.example/remote?code=ABCD")]
    [InlineData("https://karaokay.example/", "http", "localhost", "https://karaokay.example/remote?code=ABCD")]
    public void BuildJoinUrlProducesAnAbsoluteScannableUrl(
        string? publicBaseUrl,
        string scheme,
        string host,
        string expected)
    {
        var actual = ApiRooms.BuildJoinUrl(publicBaseUrl, scheme, host, "ABCD");

        Assert.Equal(expected, actual);
    }
}
