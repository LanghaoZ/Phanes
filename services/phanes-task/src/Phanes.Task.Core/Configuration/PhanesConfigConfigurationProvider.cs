namespace Phanes.Task.Core.Configuration;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Pulls one JSON document from the phanes-config service
/// (GET /configs/{namespace}/{key}, ETag polling) and exposes it as
/// configuration keys under "PhanesTask:". Registered after appsettings, so
/// its values win; anything the document does not mention falls through.
///
/// A change in the document's version triggers OnReload → every
/// IOptionsMonitor&lt;TaskOptions&gt; observer sees the new values. Failures
/// keep the last-good document; the service must never depend on
/// phanes-config being up.
/// </summary>
public sealed class PhanesConfigConfigurationProvider : ConfigurationProvider, IDisposable
{
    public const string RootKey = "PhanesTask";

    private readonly PhanesConfigOptions _options;
    private readonly HttpClient _http;
    private readonly Action<string, Exception?> _log;
    private readonly Timer? _timer;
    private string? _etag;
    private int _polling;

    public PhanesConfigConfigurationProvider(PhanesConfigOptions options, Action<string, Exception?> log, HttpMessageHandler? handler = null)
    {
        _options = options;
        _log = log;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri(options.Url.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        if (options.PollSeconds > 0)
        {
            var period = TimeSpan.FromSeconds(options.PollSeconds);
            _timer = new Timer(_ => Poll(), null, period, period);
        }
    }

    public int Version { get; private set; }

    public override void Load() => Poll();

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"configs/{_options.Namespace}/{_options.Key}");
            if (_etag is not null)
            {
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_etag));
            }
            using var response = _http.Send(request);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (Data.Count > 0)
                {
                    Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    _etag = null;
                    Version = 0;
                    OnReload();
                }
                return;
            }
            response.EnsureSuccessStatusCode();

            using var body = JsonDocument.Parse(response.Content.ReadAsStream());
            var root = body.RootElement;
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            Flatten(root.GetProperty("doc"), RootKey, data);
            Data = data;
            Version = root.GetProperty("version").GetInt32();
            _etag = response.Headers.ETag?.Tag;
            _log($"phanes-config: loaded {_options.Namespace}/{_options.Key} v{Version} ({data.Count} keys)", null);
            OnReload();
        }
        catch (Exception ex)
        {
            _log($"phanes-config: fetch failed for {_options.Namespace}/{_options.Key}; keeping last-good (v{Version})", ex);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string?> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, $"{prefix}:{property.Name}", into);
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, $"{prefix}:{i++}", into);
                }
                break;
            case JsonValueKind.Null:
                into[prefix] = null;
                break;
            default:
                into[prefix] = element.ToString();
                break;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _http.Dispose();
    }
}

public sealed class PhanesConfigConfigurationSource(PhanesConfigOptions options, Action<string, Exception?> log) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new PhanesConfigConfigurationProvider(options, log);
}

public static class PhanesConfigConfigurationExtensions
{
    /// <summary>
    /// Layer the phanes-config document over the already-added sources. No-op
    /// when "PhanesTask:ConfigService:Url" is empty.
    /// </summary>
    public static IConfigurationBuilder AddPhanesConfig(this IConfigurationBuilder builder, Action<string, Exception?> log)
    {
        var options = new PhanesConfigOptions();
        builder.Build().GetSection($"{PhanesConfigConfigurationProvider.RootKey}:ConfigService").Bind(options);
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            log("phanes-config: ConfigService:Url not set — dynamic configuration disabled.", null);
            return builder;
        }
        return builder.Add(new PhanesConfigConfigurationSource(options, log));
    }
}
