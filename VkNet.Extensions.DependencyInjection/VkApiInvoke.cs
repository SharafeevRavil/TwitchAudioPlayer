using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using VkNet.Abstractions;
using VkNet.Abstractions.Core;
using VkNet.Abstractions.Utils;
using VkNet.Utils;
using VkNet.Utils.JsonConverter;

namespace VkNet.Extensions.DependencyInjection;

public class VkApiInvoke : IVkApiInvoke
{
    private readonly Uri _apiBaseUri = new("https://api.vk.com/method/");

    private readonly IRestClient _client;

    private readonly IEnumerable<JsonConverter> _defaultConverters = new JsonConverter[]
    {
        new VkCollectionJsonConverter(),
        new UnixDateTimeConverter(),
        new AttachmentJsonConverter(),
        new StringEnumConverter()
    };

    private readonly ICaptchaHandler _handler;
    private readonly ILanguageService _languageService;
    private readonly IAsyncRateLimiter _rateLimiter;
    private readonly IVkTokenStore _tokenStore;
    private readonly IVkApiVersionManager _versionManager;

    public VkApiInvoke(IRestClient client, ICaptchaHandler handler, IVkApiVersionManager versionManager,
        IVkTokenStore tokenStore, ILanguageService languageService, IAsyncRateLimiter rateLimiter)
    {
        _client = client;
        _handler = handler;
        _versionManager = versionManager;
        _tokenStore = tokenStore;
        _languageService = languageService;
        _rateLimiter = rateLimiter;
    }

    public VkResponse Call(string methodName, VkParameters parameters, bool skipAuthorization = false)
    {
        return CallAsync(methodName, parameters, skipAuthorization).GetAwaiter().GetResult();
    }

    public T? Call<T>(string methodName, VkParameters parameters, bool skipAuthorization = false,
        params JsonConverter[] jsonConverters)
    {
        return CallAsync<T>(methodName, parameters, skipAuthorization, jsonConverters).GetAwaiter().GetResult();
    }

    public async Task<VkResponse> CallAsync(string methodName, VkParameters parameters, bool skipAuthorization = false)
    {
        var json = await InvokeInternalAsync(methodName, parameters, skipAuthorization);

        return new VkResponse(json)
        {
            RawJson = json.ToString()
        };
    }

    public Task<T?> CallAsync<T>(string methodName, VkParameters parameters, bool skipAuthorization = false)
    {
        return CallAsync<T>(methodName, parameters, skipAuthorization, Array.Empty<JsonConverter>());
    }

    public string Invoke(string methodName, IDictionary<string, string> parameters, bool skipAuthorization = false)
    {
        return InvokeAsync(methodName, parameters, skipAuthorization).GetAwaiter().GetResult();
    }

    public async Task<string> InvokeAsync(string methodName, IDictionary<string, string> parameters,
        bool skipAuthorization = false)
    {
        var json = await InvokeInternalAsync(methodName, parameters, skipAuthorization);
        return json.ToString();
    }

    public DateTimeOffset? LastInvokeTime { get; private set; }
    public TimeSpan? LastInvokeTimeSpan => DateTimeOffset.Now - LastInvokeTime;

    private void TryAddRequiredParameters(IDictionary<string, string> parameters, bool skipAuthorization)
    {
        parameters.TryAdd("v", _versionManager.Version);
        parameters.TryAdd("lang", _languageService.GetLanguage()?.ToString() ?? "ru");
        if (!skipAuthorization)
            parameters.TryAdd("access_token", _tokenStore.Token);
    }

    private JsonSerializerSettings CreateSettings(IEnumerable<JsonConverter> userConverters)
    {
        var converters = _defaultConverters.ToList(); // i actually wanna clone the array here
        converters.AddRange(userConverters);

        return new JsonSerializerSettings
        {
            Converters = converters,
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy()
            },
            MaxDepth = null,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };
    }

    private async Task<T?> CallAsync<T>(string methodName, VkParameters parameters, bool skipAuthorization,
        JsonConverter[] jsonConverters)
    {
        var json = await InvokeInternalAsync(methodName, parameters, skipAuthorization);

        return json.ToObject<T>(JsonSerializer.Create(CreateSettings(jsonConverters)));
    }

    private Task<JToken> InvokeInternalAsync(string methodName, IDictionary<string, string> parameters,
        bool skipAuthorization)
    {
        TryAddRequiredParameters(parameters, skipAuthorization);

        return _handler.Perform(async (sid, key) =>
        {
            if (sid is { } captchaSid)
            {
                parameters.Add("captcha_sid", captchaSid.ToString());
                parameters.Add("captcha_key", key);
            }

            await _rateLimiter.WaitNextAsync();

            var response = await _client.PostAsync(new Uri(_apiBaseUri, methodName), parameters, Encoding.UTF8);
            LastInvokeTime = DateTimeOffset.Now;

            return VkErrors.IfErrorThrowException(response.Value)["response"]!;
        });
    }
}