using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TwitchAudioPlayer.WPF.Services.MusicOrder;

namespace TwitchAudioPlayer.WPF.Services.Obs;

public sealed class ObsSceneSetupService(ILogger<ObsSceneSetupService> logger)
{
    private const string VideoSourceName = "Video TwitchAudioPlayer";
    private const string GeneralAudioSourceName = "GeneralAudio TwitchAudioPlayer";
    private const string YouTubeAudioSourceName = "YouTubeAudio TwitchAudioPlayer";
    private static readonly string[] LegacySourceNames =
    [
        "TwitchAudioPlayer Video",
        "TwitchAudioPlayer App Audio",
        "TwitchAudioPlayer YouTube Audio"
    ];
    private const string WindowCaptureKind = "window_capture";
    private const string AppAudioCaptureKind = "wasapi_process_output_capture";
    private const string BrowserSourceKind = "browser_source";

    public async Task<ObsSceneSetupResult> CreateOrUpdateAsync(
        ObsSceneSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        await using var client = await ObsWebSocketClient.ConnectAsync(
            request.Connection,
            logger,
            cancellationToken);

        await DeleteInternalAsync(client, request.GroupName, cancellationToken);

        await client.RequestAsync("CreateScene", new JsonObject
        {
            ["sceneName"] = request.GroupName
        }, cancellationToken);

        await CreateWindowCaptureAsync(client, request.GroupName, request, warnings, cancellationToken);
        await CreateAppAudioCaptureAsync(client, request.GroupName, request, warnings, cancellationToken);
        await CreateBrowserAudioSourceAsync(client, request.GroupName, request, warnings, cancellationToken);

        return new ObsSceneSetupResult(
            $"OBS scene '{request.GroupName}' was created with TwitchAudioPlayer sources.",
            warnings);
    }

    public async Task<ObsSceneSetupResult> DeleteAsync(
        ObsConnectionOptions connection,
        string groupName,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        await using var client = await ObsWebSocketClient.ConnectAsync(connection, logger, cancellationToken);
        await DeleteInternalAsync(client, groupName, cancellationToken);
        return new ObsSceneSetupResult($"OBS scene '{groupName}' was removed.", warnings);
    }

    private static async Task DeleteInternalAsync(
        ObsWebSocketClient client,
        string groupName,
        CancellationToken cancellationToken)
    {
        await IgnoreFailureAsync(
            () => client.RequestAsync("RemoveInput", new JsonObject
            {
                ["inputName"] = groupName
            }, cancellationToken),
            null,
            null);

        await IgnoreFailureAsync(
            () => client.RequestAsync("RemoveScene", new JsonObject
            {
                ["sceneName"] = groupName
            }, cancellationToken),
            null,
            null);

        foreach (var inputName in new[] { VideoSourceName, GeneralAudioSourceName, YouTubeAudioSourceName }
                     .Concat(LegacySourceNames))
        {
            await IgnoreFailureAsync(
                () => client.RequestAsync("RemoveInput", new JsonObject
                {
                    ["inputName"] = inputName
                }, cancellationToken),
                null,
                null);
        }
    }

    private static async Task CreateWindowCaptureAsync(
        ObsWebSocketClient client,
        string sceneName,
        ObsSceneSetupRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        await client.RequestAsync("CreateInput", new JsonObject
        {
            ["sceneName"] = sceneName,
            ["inputName"] = VideoSourceName,
            ["inputKind"] = WindowCaptureKind,
            ["sceneItemEnabled"] = true,
            ["inputSettings"] = new JsonObject
            {
                ["window"] = request.WindowTarget.ToObsWindowString(),
                ["priority"] = 2,
                ["capture_cursor"] = false,
                ["client_area"] = false
            }
        }, cancellationToken);

        var itemId = await GetSceneItemIdAsync(client, sceneName, VideoSourceName, cancellationToken);
        await client.RequestAsync("SetSceneItemTransform", new JsonObject
        {
            ["sceneName"] = sceneName,
            ["sceneItemId"] = itemId,
            ["sceneItemTransform"] = new JsonObject
            {
                ["cropTop"] = request.Crop.Top,
                ["cropBottom"] = request.Crop.Bottom,
                ["cropLeft"] = request.Crop.Left,
                ["cropRight"] = request.Crop.Right
            }
        }, cancellationToken);
    }

    private static async Task CreateAppAudioCaptureAsync(
        ObsWebSocketClient client,
        string sceneName,
        ObsSceneSetupRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        await IgnoreFailureAsync(
            () => client.RequestAsync("CreateInput", new JsonObject
            {
                ["sceneName"] = sceneName,
                ["inputName"] = GeneralAudioSourceName,
                ["inputKind"] = AppAudioCaptureKind,
                ["sceneItemEnabled"] = true,
                ["inputSettings"] = new JsonObject
                {
                    ["window"] = request.WindowTarget.ToObsWindowString(),
                    ["priority"] = 2
                }
            }, cancellationToken),
            warnings,
            "Application Audio Capture (BETA) was not created");
    }

    private static async Task CreateBrowserAudioSourceAsync(
        ObsWebSocketClient client,
        string sceneName,
        ObsSceneSetupRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        await IgnoreFailureAsync(
            () => client.RequestAsync("CreateInput", new JsonObject
            {
                ["sceneName"] = sceneName,
                ["inputName"] = YouTubeAudioSourceName,
                ["inputKind"] = BrowserSourceKind,
                ["sceneItemEnabled"] = true,
                ["inputSettings"] = new JsonObject
                {
                    ["url"] = request.BrowserAudioUrl,
                    ["width"] = 8,
                    ["height"] = 8,
                    ["fps"] = 30,
                    ["shutdown"] = false,
                    ["reroute_audio"] = true,
                    ["css"] = "body { background-color: rgba(0, 0, 0, 0); margin: 0; overflow: hidden; }"
                }
            }, cancellationToken),
            warnings,
            "Browser Source for YouTube iframe audio was not created");
    }

    private static async Task<int> GetSceneItemIdAsync(
        ObsWebSocketClient client,
        string sceneName,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var data = await client.RequestAsync("GetSceneItemId", new JsonObject
        {
            ["sceneName"] = sceneName,
            ["sourceName"] = sourceName
        }, cancellationToken);

        return data?["sceneItemId"]?.GetValue<int>()
               ?? throw new InvalidOperationException($"OBS did not return scene item id for '{sourceName}'.");
    }

    private static async Task IgnoreFailureAsync(
        Func<Task> action,
        List<string>? warnings,
        string? warningPrefix)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            if (warnings is not null && warningPrefix is not null)
                warnings.Add($"{warningPrefix}: {ex.Message}");
        }
    }

    private sealed class ObsWebSocketClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly ILogger _logger;
        private int _requestId;

        private ObsWebSocketClient(ILogger logger)
        {
            _logger = logger;
        }

        public static async Task<ObsWebSocketClient> ConnectAsync(
            ObsConnectionOptions options,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var client = new ObsWebSocketClient(logger);
            await client._socket.ConnectAsync(options.ToUri(), cancellationToken);
            await client.IdentifyAsync(options.Password, cancellationToken);
            return client;
        }

        public async Task<JsonObject?> RequestAsync(
            string requestType,
            JsonObject? requestData,
            CancellationToken cancellationToken)
        {
            var requestId = Interlocked.Increment(ref _requestId).ToString(CultureInfo.InvariantCulture);
            var request = new JsonObject
            {
                ["op"] = 6,
                ["d"] = new JsonObject
                {
                    ["requestType"] = requestType,
                    ["requestId"] = requestId
                }
            };

            if (requestData is not null)
                request["d"]!.AsObject()["requestData"] = requestData;

            await SendAsync(request, cancellationToken);

            while (true)
            {
                using var document = await ReceiveJsonAsync(cancellationToken);
                var root = document.RootElement;
                var op = root.GetProperty("op").GetInt32();
                if (op != 7)
                    continue;

                var data = root.GetProperty("d");
                if (!string.Equals(data.GetProperty("requestId").GetString(), requestId, StringComparison.Ordinal))
                    continue;

                var status = data.GetProperty("requestStatus");
                var result = status.GetProperty("result").GetBoolean();
                if (!result)
                {
                    var code = status.TryGetProperty("code", out var codeElement)
                        ? codeElement.GetInt32().ToString(CultureInfo.InvariantCulture)
                        : "unknown";
                    var comment = status.TryGetProperty("comment", out var commentElement)
                        ? commentElement.GetString()
                        : "no details";
                    throw new InvalidOperationException($"{requestType} failed ({code}): {comment}");
                }

                return data.TryGetProperty("responseData", out var responseData)
                    ? JsonNode.Parse(responseData.GetRawText())?.AsObject()
                    : null;
            }
        }

        private async Task IdentifyAsync(string? password, CancellationToken cancellationToken)
        {
            using var hello = await ReceiveJsonAsync(cancellationToken);
            var root = hello.RootElement;
            if (root.GetProperty("op").GetInt32() != 0)
                throw new InvalidOperationException("OBS WebSocket did not send Hello.");

            var data = root.GetProperty("d");
            var identify = new JsonObject
            {
                ["op"] = 1,
                ["d"] = new JsonObject
                {
                    ["rpcVersion"] = 1
                }
            };

            if (data.TryGetProperty("authentication", out var auth))
            {
                if (string.IsNullOrEmpty(password))
                    throw new InvalidOperationException("OBS WebSocket requires a password.");

                var salt = auth.GetProperty("salt").GetString() ?? "";
                var challenge = auth.GetProperty("challenge").GetString() ?? "";
                identify["d"]!.AsObject()["authentication"] = CreateAuthentication(password, salt, challenge);
            }

            await SendAsync(identify, cancellationToken);

            while (true)
            {
                using var response = await ReceiveJsonAsync(cancellationToken);
                var op = response.RootElement.GetProperty("op").GetInt32();
                if (op == 2)
                    return;

                if (op == 5)
                    continue;

                _logger.LogDebug("Unexpected OBS WebSocket op while identifying: {Op}", op);
            }
        }

        private static string CreateAuthentication(string password, string salt, string challenge)
        {
            var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
            return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
        }

        private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
        {
            var json = message.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }

        private async Task<JsonDocument> ReceiveJsonAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var stream = new MemoryStream();

            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("OBS WebSocket closed the connection.");

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    break;
            }

            stream.Position = 0;
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "TwitchAudioPlayer done",
                    CancellationToken.None);
            }

            _socket.Dispose();
        }
    }
}

public sealed record ObsConnectionOptions(string Host, int Port, string? Password)
{
    public Uri ToUri() => new($"ws://{Host}:{Port}");
}

public sealed record ObsSceneSetupRequest(
    ObsConnectionOptions Connection,
    string GroupName,
    ObsWindowCaptureTarget WindowTarget,
    ObsCrop Crop,
    string BrowserAudioUrl);

public sealed record ObsWindowCaptureTarget(string Title, string ClassName, string ExecutableName)
{
    public string ToObsWindowString() => $"{Title}:{ClassName}:{ExecutableName}";
}

public sealed record ObsCrop(int Top, int Bottom, int Left, int Right);

public sealed record ObsSceneSetupResult(string Message, IReadOnlyList<string> Warnings);
