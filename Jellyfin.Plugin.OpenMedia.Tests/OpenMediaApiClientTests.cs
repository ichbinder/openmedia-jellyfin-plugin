using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class OpenMediaApiClientTests
{
    [Fact]
    public async Task GetLibraryAsync_RequestsEpisodesAndParsesKind()
    {
        Uri? requested = null;
        var handler = new StubHandler(req =>
        {
            requested = req.RequestUri;
            var json = "{\"items\":[{\"kind\":\"episode\",\"hash\":\"h\",\"tmdbId\":1,\"title\":\"S\",\"season\":2,\"episode\":5}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        var client = new OpenMediaApiClient(new HttpClient(handler), "https://api.test.com/", "om_t");

        var items = await client.GetLibraryAsync(CancellationToken.None);

        Assert.Equal("https://api.test.com/jellyfin/library?include=episodes", requested!.ToString());
        var item = Assert.Single(items);
        Assert.Equal("episode", item.Kind);
        Assert.Equal(2, item.Season);
        Assert.Equal(5, item.Episode);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_respond(request));
    }
}
