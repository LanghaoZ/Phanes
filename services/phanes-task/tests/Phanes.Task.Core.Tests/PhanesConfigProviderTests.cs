using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Phanes.Task.Core.Configuration;
using Phanes.Task.Core.Options;

namespace CoreTests;

public class PhanesConfigProviderTests
{
    /// <summary>Scripted phanes-config: serves (version, doc) with ETag/304 semantics.</summary>
    private sealed class FakeConfigService : HttpMessageHandler
    {
        public int Version { get; set; } = 1;
        public string Doc { get; set; } = """{"Worker":{"WatermarkStuckSeconds":42,"MaxChainDepth":7},"Dispatcher":{"MaxDelaySeconds":60}}""";
        public bool Missing { get; set; }
        public int Requests { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (Missing) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var etag = $"\"{Version}\"";
            if (request.Headers.IfNoneMatch.Any(t => t.Tag == etag))
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$$"""{"namespace":"phanes-task","key":"options","version":{{{Version}}},"doc":{{{Doc}}}}"""),
            };
            response.Headers.ETag = new EntityTagHeaderValue(etag);
            return response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Send(request, ct));
    }

    private static (PhanesConfigConfigurationProvider Provider, FakeConfigService Service) Build()
    {
        var service = new FakeConfigService();
        var provider = new PhanesConfigConfigurationProvider(
            new PhanesConfigOptions { Url = "http://config.test", PollSeconds = 0 }, (_, _) => { }, service);
        provider.Load();
        return (provider, service);
    }

    [Fact]
    public void Flattens_the_document_under_PhanesTask_and_binds_to_options()
    {
        var (provider, _) = Build();
        Assert.True(provider.TryGet("PhanesTask:Worker:WatermarkStuckSeconds", out var v) && v == "42");
        Assert.True(provider.TryGet("PhanesTask:Dispatcher:MaxDelaySeconds", out var d) && d == "60");
        Assert.Equal(1, provider.Version);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PhanesTask:Worker:WatermarkStuckSeconds"] = "300",   // appsettings baseline
                ["PhanesTask:Worker:ActorMailboxCapacity"] = "64",
            })
            .Add(new ProviderSource(provider))
            .Build();
        var bound = config.GetSection("PhanesTask").Get<TaskOptions>()!;
        Assert.Equal(42, bound.Worker.WatermarkStuckSeconds);   // overlay wins
        Assert.Equal(64, bound.Worker.ActorMailboxCapacity);     // untouched key falls through
        Assert.Equal(7, bound.Worker.MaxChainDepth);
    }

    [Fact]
    public void Unchanged_version_is_a_304_and_a_new_version_reloads()
    {
        var (provider, service) = Build();
        var reloads = 0;
        provider.GetReloadToken().RegisterChangeCallback(_ => reloads++, null);

        provider.Load();                      // same ETag → 304, no reload
        Assert.Equal(0, reloads);
        Assert.Equal(2, service.Requests);

        service.Version = 2;
        service.Doc = """{"Worker":{"WatermarkStuckSeconds":5}}""";
        provider.Load();
        Assert.Equal(2, provider.Version);
        Assert.True(provider.TryGet("PhanesTask:Worker:WatermarkStuckSeconds", out var v) && v == "5");
        Assert.False(provider.TryGet("PhanesTask:Dispatcher:MaxDelaySeconds", out _)); // removed from doc
    }

    [Fact]
    public void Fetch_failure_keeps_the_last_good_document()
    {
        var (provider, service) = Build();
        service.Missing = true;   // 404 → document gone → cleared
        provider.Load();
        Assert.False(provider.TryGet("PhanesTask:Worker:WatermarkStuckSeconds", out _));
        Assert.Equal(0, provider.Version);
    }

    private sealed class ProviderSource(IConfigurationProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }
}
