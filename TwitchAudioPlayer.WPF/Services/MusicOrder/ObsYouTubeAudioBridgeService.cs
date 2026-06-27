using System.ComponentModel;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;
using TwitchAudioPlayer.WPF.Services;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

public sealed class ObsYouTubeAudioBridgeService : IDisposable
{
    private const string ListenPrefix = "http://127.0.0.1:38174/";
    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    private static readonly WaveFormat CaptureFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private readonly BrowserPlayerService _browserPlayer;
    private readonly IUserSettingsManager _settingsManager;
    private readonly ILogger<ObsYouTubeAudioBridgeService> _logger;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _serverCancellation = new();
    private readonly object _gate = new();
    private readonly object _meterGate = new();
    private readonly List<ObsAudioClient> _clients = [];

    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;
    private uint _runningProcessId;
    private int _captureGeneration;
    private float _lastInputRms;
    private float _lastInputPeak;
    private float _lastOutputPeak;
    private float _outputGain;
    private bool _disposed;

    public ObsYouTubeAudioBridgeService(
        BrowserPlayerService browserPlayer,
        IUserSettingsManager settingsManager,
        ILogger<ObsYouTubeAudioBridgeService> logger)
    {
        _browserPlayer = browserPlayer;
        _settingsManager = settingsManager;
        _logger = logger;
        _outputGain = NormalizeGain(_settingsManager.Settings.ObsYouTubeAudioGain);

        _browserPlayer.PropertyChanged += BrowserPlayerOnPropertyChanged;
        _browserPlayer.LoadRequested += BrowserPlayerOnLoadRequested;
        _browserPlayer.PlayRequested += BrowserPlayerOnPlaybackControlChanged;
        _browserPlayer.PauseRequested += BrowserPlayerOnPlaybackControlChanged;
        _browserPlayer.StopRequested += BrowserPlayerOnStopRequested;
        _browserPlayer.VolumeRequested += BrowserPlayerOnVolumeRequested;
        _browserPlayer.MuteRequested += BrowserPlayerOnMuteRequested;
        _settingsManager.SettingsChanged += SettingsManagerOnSettingsChanged;

        StartServer();
    }

    public static string BrowserSourceUrl => $"{ListenPrefix}obs-youtube-audio.html";

    private void StartServer()
    {
        try
        {
            _listener.Prefixes.Add(ListenPrefix);
            _listener.Start();
            _ = Task.Run(() => RunServerAsync(_serverCancellation.Token));
            _logger.LogInformation("OBS YouTube audio bridge listening at {Url}", BrowserSourceUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start OBS YouTube audio bridge at {Url}", BrowserSourceUrl);
        }
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/ws", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWebSocketAsync(context, cancellationToken);
                return;
            }

            if (path.Equals("/status", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(context, GetStatusMessage(), "application/json; charset=utf-8", cancellationToken);
                return;
            }

            await WriteTextAsync(context, GetBrowserSourceHtml(), "text/html; charset=utf-8", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OBS YouTube audio bridge request failed");
            TryClose(context.Response, 500);
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            TryClose(context.Response, 400);
            return;
        }

        var webSocketContext = await context.AcceptWebSocketAsync(null);
        var client = new ObsAudioClient(webSocketContext.WebSocket);
        lock (_gate)
        {
            _clients.Add(client);
        }

        client.TryQueueText(GetFormatMessage());
        client.TryQueueText(GetControlMessage());
        _logger.LogInformation("OBS YouTube audio bridge client connected");

        try
        {
            await client.RunAsync(cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _clients.Remove(client);
            }

            client.Dispose();
            _logger.LogInformation("OBS YouTube audio bridge client disconnected");
        }
    }

    private static async Task WriteTextAsync(
        HttpListenerContext context,
        string text,
        string contentType,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();
    }

    private static void TryClose(HttpListenerResponse response, int statusCode)
    {
        try
        {
            response.StatusCode = statusCode;
            response.Close();
        }
        catch
        {
            // Best-effort close for aborted local requests.
        }
    }

    private void BrowserPlayerOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserPlayerService.WebViewBrowserProcessId))
            StartOrRestartCapture();
        else if (e.PropertyName == nameof(BrowserPlayerService.IsPlaying))
            BroadcastControl();
    }

    private void BrowserPlayerOnLoadRequested(object? sender, YouTubeBrowserPlaybackRequest e)
    {
        BroadcastControl();
        StartOrRestartCapture();
    }

    private void BrowserPlayerOnPlaybackControlChanged(object? sender, EventArgs e) =>
        BroadcastControl();

    private void BrowserPlayerOnStopRequested(object? sender, EventArgs e)
    {
        BroadcastControl();
        StopCapture();
    }

    private void BrowserPlayerOnVolumeRequested(object? sender, double e) =>
        BroadcastControl();

    private void BrowserPlayerOnMuteRequested(object? sender, bool e) =>
        BroadcastControl();

    private void SettingsManagerOnSettingsChanged(object? sender, UserSettings settings)
    {
        lock (_meterGate)
        {
            _outputGain = NormalizeGain(settings.ObsYouTubeAudioGain);
        }
    }

    private void StartOrRestartCapture()
    {
        if (_disposed || !_browserPlayer.IsYouTubeActive || _browserPlayer.WebViewBrowserProcessId == 0)
            return;

        lock (_gate)
        {
            if (_runningProcessId == _browserPlayer.WebViewBrowserProcessId && _captureTask is { IsCompleted: false })
                return;
        }

        StopCapture();

        var processId = _browserPlayer.WebViewBrowserProcessId;
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_disposed)
            {
                cts.Dispose();
                return;
            }

            _runningProcessId = processId;
            _captureCancellation = cts;
            var generation = ++_captureGeneration;
            ResetMeter();
            _captureTask = Task.Run(() => RunCaptureAsync(processId, generation, cts.Token), cts.Token);
        }
    }

    private async Task RunCaptureAsync(uint targetProcessId, int generation, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting OBS YouTube audio bridge capture for process tree {ProcessId}",
                targetProcessId);

            using var audioClient = await ActivateProcessLoopbackAsync(targetProcessId);
            audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback,
                100 * 10_000,
                0,
                CaptureFormat,
                Guid.Empty);

            var captureClient = audioClient.AudioCaptureClient;
            BroadcastFormat();
            BroadcastControl();
            audioClient.Start();

            while (!cancellationToken.IsCancellationRequested)
            {
                var packetSize = captureClient.GetNextPacketSize();
                while (packetSize > 0)
                {
                    var data = captureClient.GetBuffer(out var frames, out var flags);
                    var byteCount = frames * CaptureFormat.BlockAlign;
                    var packet = new byte[byteCount];
                    if (!flags.HasFlag(AudioClientBufferFlags.Silent))
                    {
                        Marshal.Copy(data, packet, 0, byteCount);
                        ApplyFixedGain(packet);
                    }

                    captureClient.ReleaseBuffer(frames);
                    BroadcastAudio(packet);
                    packetSize = captureClient.GetNextPacketSize();
                }

                await Task.Delay(5, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop/restart path.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OBS YouTube audio bridge capture failed");
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _captureGeneration)
                    _runningProcessId = 0;
            }
        }
    }

    private static async Task<AudioClient> ActivateProcessLoopbackAsync(uint targetProcessId)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            var operation = dispatcher.InvokeAsync(() => ProcessLoopbackAudioClientActivator.ActivateAsync(targetProcessId));
            return await await operation.Task;
        }

        return await ProcessLoopbackAudioClientActivator.ActivateAsync(targetProcessId);
    }

    private void BroadcastFormat() =>
        BroadcastText(GetFormatMessage());

    private void BroadcastControl() =>
        BroadcastText(GetControlMessage());

    private static string GetFormatMessage() => JsonSerializer.Serialize(new
    {
        type = "format",
        sampleRate = CaptureFormat.SampleRate,
        channels = CaptureFormat.Channels
    });

    private string GetControlMessage() => JsonSerializer.Serialize(new
    {
        type = "control",
        volume = Math.Clamp(_browserPlayer.Volume, 0, 1),
        muted = _browserPlayer.IsMuted,
        playing = _browserPlayer.IsYouTubeActive && _browserPlayer.IsPlaying
    });

    private string GetStatusMessage()
    {
        int clientsCount;
        uint processId;
        lock (_gate)
        {
            clientsCount = _clients.Count;
            processId = _runningProcessId;
        }

        lock (_meterGate)
        {
            return JsonSerializer.Serialize(new
            {
                status = "ok",
                url = BrowserSourceUrl,
                processId,
                clients = clientsCount,
                inputRms = _lastInputRms,
                inputRmsDb = ToDb(_lastInputRms),
                inputPeak = _lastInputPeak,
                inputPeakDb = ToDb(_lastInputPeak),
                outputPeak = _lastOutputPeak,
                outputPeakDb = ToDb(_lastOutputPeak),
                outputGain = _outputGain,
                outputGainDb = ToDb(_outputGain)
            });
        }
    }

    private void ApplyFixedGain(byte[] packet)
    {
        var samples = MemoryMarshal.Cast<byte, float>(packet);
        if (samples.Length == 0)
            return;

        double energy = 0;
        var peak = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            energy += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }

        var rms = (float)Math.Sqrt(energy / samples.Length);
        float gain;
        lock (_meterGate)
        {
            gain = _outputGain;
        }

        var outputPeak = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            var output = samples[i] * gain;
            samples[i] = output;
            outputPeak = Math.Max(outputPeak, Math.Abs(output));
        }

        lock (_meterGate)
        {
            _lastInputRms = rms;
            _lastInputPeak = peak;
            _lastOutputPeak = outputPeak;
        }
    }

    private static double ToDb(float value) =>
        value <= 0 ? double.NegativeInfinity : 20 * Math.Log10(value);

    private static float NormalizeGain(double gain) =>
        (float)Math.Clamp(double.IsFinite(gain) ? gain : 1, 0, 64);

    private void ResetMeter()
    {
        lock (_meterGate)
        {
            _lastInputRms = 0;
            _lastInputPeak = 0;
            _lastOutputPeak = 0;
        }
    }

    private void BroadcastAudio(byte[] packet)
    {
        List<ObsAudioClient> clients;
        lock (_gate)
        {
            clients = [.._clients];
        }

        foreach (var client in clients)
            client.TryQueueBinary(packet);
    }

    private void BroadcastText(string text)
    {
        List<ObsAudioClient> clients;
        lock (_gate)
        {
            clients = [.._clients];
        }

        foreach (var client in clients)
            client.TryQueueText(text);
    }

    private void StopCapture()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _captureCancellation;
            task = _captureTask;
            _captureCancellation = null;
            _captureTask = null;
            _runningProcessId = 0;
            _captureGeneration++;
        }

        cts?.Cancel();
        if (task is null)
        {
            cts?.Dispose();
        }
        else
        {
            _ = task.ContinueWith(_ => cts?.Dispose(), TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _browserPlayer.PropertyChanged -= BrowserPlayerOnPropertyChanged;
        _browserPlayer.LoadRequested -= BrowserPlayerOnLoadRequested;
        _browserPlayer.PlayRequested -= BrowserPlayerOnPlaybackControlChanged;
        _browserPlayer.PauseRequested -= BrowserPlayerOnPlaybackControlChanged;
        _browserPlayer.StopRequested -= BrowserPlayerOnStopRequested;
        _browserPlayer.VolumeRequested -= BrowserPlayerOnVolumeRequested;
        _browserPlayer.MuteRequested -= BrowserPlayerOnMuteRequested;
        _settingsManager.SettingsChanged -= SettingsManagerOnSettingsChanged;

        StopCapture();
        _serverCancellation.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // Best-effort shutdown.
        }

        lock (_gate)
        {
            foreach (var client in _clients)
                client.Dispose();
            _clients.Clear();
        }

        _serverCancellation.Dispose();
    }

    private sealed class ObsAudioClient : IDisposable
    {
        private readonly WebSocket _webSocket;
        private readonly Channel<OutboundMessage> _channel = Channel.CreateBounded<OutboundMessage>(
            new BoundedChannelOptions(96)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        public ObsAudioClient(WebSocket webSocket)
        {
            _webSocket = webSocket;
        }

        public bool TryQueueBinary(byte[] packet) =>
            _channel.Writer.TryWrite(new OutboundMessage(packet, null));

        public bool TryQueueText(string text) =>
            _channel.Writer.TryWrite(new OutboundMessage(null, text));

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var receiveTask = Task.Run(() => DrainReceiveAsync(cancellationToken), CancellationToken.None);
            try
            {
                await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken))
                {
                    if (_webSocket.State != WebSocketState.Open)
                        break;

                    if (message.Text is { } text)
                    {
                        var bytes = Encoding.UTF8.GetBytes(text);
                        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                    }
                    else if (message.Binary is { } binary)
                    {
                        await _webSocket.SendAsync(binary, WebSocketMessageType.Binary, true, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (WebSocketException)
            {
                // Browser source disconnected.
            }
            finally
            {
                _channel.Writer.TryComplete();
                try
                {
                    await receiveTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch
                {
                    // The receive loop is only there to notice browser-source disconnects.
                }
            }
        }

        private async Task DrainReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[256];
            try
            {
                while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
                {
                    var result = await _webSocket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            catch
            {
                // Closing/disconnected browser sources are expected.
            }
            finally
            {
                _channel.Writer.TryComplete();
            }
        }

        public void Dispose()
        {
            _channel.Writer.TryComplete();
            _webSocket.Dispose();
        }

        private readonly record struct OutboundMessage(byte[]? Binary, string? Text);
    }

    private static class ProcessLoopbackAudioClientActivator
    {
        private const ushort VtBlob = 65;

        private static readonly Guid IAudioClientGuid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

        public static async Task<AudioClient> ActivateAsync(uint targetProcessId)
        {
            var activationParams = new AudioClientActivationParams
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = targetProcessId,
                    ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree
                }
            };

            var activationParamsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
            var propVariantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
            IActivateAudioInterfaceAsyncOperation? asyncOperation = null;

            try
            {
                Marshal.StructureToPtr(activationParams, activationParamsPointer, false);
                var propVariant = new PropVariant
                {
                    vt = VtBlob,
                    blob = new Blob
                    {
                        cbSize = Marshal.SizeOf<AudioClientActivationParams>(),
                        pBlobData = activationParamsPointer
                    }
                };
                Marshal.StructureToPtr(propVariant, propVariantPointer, false);

                var completionHandler = new ActivateAudioInterfaceCompletionHandler();
                var result = ActivateAudioInterfaceAsync(
                    ProcessLoopbackDevice,
                    IAudioClientGuid,
                    propVariantPointer,
                    completionHandler,
                    out asyncOperation);
                Marshal.ThrowExceptionForHR(result);

                var audioClient = await completionHandler.Task.WaitAsync(TimeSpan.FromSeconds(8));
                return new AudioClient(audioClient);
            }
            finally
            {
                if (asyncOperation is not null)
                    Marshal.ReleaseComObject(asyncOperation);

                Marshal.FreeHGlobal(propVariantPointer);
                Marshal.FreeHGlobal(activationParamsPointer);
            }
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientActivationParams
        {
            public AudioClientActivationType ActivationType;
            public AudioClientProcessLoopbackParams ProcessLoopbackParams;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientProcessLoopbackParams
        {
            public uint TargetProcessId;
            public ProcessLoopbackMode ProcessLoopbackMode;
        }

        private enum AudioClientActivationType
        {
            Default,
            ProcessLoopback
        }

        private enum ProcessLoopbackMode
        {
            IncludeTargetProcessTree,
            ExcludeTargetProcessTree
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public Blob blob;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int cbSize;
            public IntPtr pBlobData;
        }

        private sealed class ActivateAudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler
        {
            private readonly TaskCompletionSource<IAudioClient> _source =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<IAudioClient> Task => _source.Task;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
            {
                try
                {
                    activateOperation.GetActivateResult(out var activateResult, out var activateInterface);
                    Marshal.ThrowExceptionForHR(activateResult);
                    _source.TrySetResult((IAudioClient)activateInterface);
                }
                catch (Exception ex)
                {
                    _source.TrySetException(ex);
                }
            }
        }
    }

    private static string GetBrowserSourceHtml() =>
        """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>TwitchAudioPlayer OBS YouTube Audio</title>
            <style>
                html, body {
                    width: 100%;
                    height: 100%;
                    margin: 0;
                    overflow: hidden;
                    background: transparent;
                    color: transparent;
                }
            </style>
        </head>
        <body>
        <script>
        (() => {
            const workletCode = `
                class TapPcmPlayer extends AudioWorkletProcessor {
                    constructor() {
                        super();
                        this.queue = [];
                        this.current = null;
                        this.index = 0;
                        this.port.onmessage = event => {
                            if (event.data && event.data.type === "samples") {
                                this.queue.push(event.data.samples);
                            }
                        };
                    }

                    nextSample() {
                        if (!this.current || this.index >= this.current.length) {
                            this.current = this.queue.shift() || null;
                            this.index = 0;
                        }

                        if (!this.current) {
                            return [0, 0];
                        }

                        const left = this.current[this.index++] || 0;
                        const right = this.current[this.index++] || left;
                        return [left, right];
                    }

                    process(inputs, outputs) {
                        const output = outputs[0];
                        const left = output[0];
                        const right = output[1] || output[0];
                        for (let i = 0; i < left.length; i++) {
                            const sample = this.nextSample();
                            left[i] = sample[0];
                            right[i] = sample[1];
                        }

                        return true;
                    }
                }

                registerProcessor("tap-pcm-player", TapPcmPlayer);
            `;

            let context;
            let node;
            let gain;
            let socket;
            let reconnectTimer;

            async function ensureAudio() {
                if (context) {
                    if (context.state !== "running") {
                        await context.resume().catch(() => {});
                    }

                    return;
                }

                context = new AudioContext({ sampleRate: 48000, latencyHint: "interactive" });
                const workletUrl = URL.createObjectURL(new Blob([workletCode], { type: "application/javascript" }));
                await context.audioWorklet.addModule(workletUrl);
                URL.revokeObjectURL(workletUrl);
                node = new AudioWorkletNode(context, "tap-pcm-player", {
                    numberOfInputs: 0,
                    numberOfOutputs: 1,
                    outputChannelCount: [2]
                });
                gain = context.createGain();
                gain.gain.value = 0;
                node.connect(gain).connect(context.destination);
                await context.resume().catch(() => {});
            }

            function applyControl(message) {
                if (!gain) {
                    return;
                }

                const volume = Math.max(0, Math.min(1, Number(message.volume) || 0));
                gain.gain.value = message.muted || !message.playing ? 0 : volume;
            }

            async function connect() {
                clearTimeout(reconnectTimer);
                await ensureAudio();
                const protocol = location.protocol === "https:" ? "wss:" : "ws:";
                socket = new WebSocket(`${protocol}//${location.host}/ws`);
                socket.binaryType = "arraybuffer";
                socket.onopen = () => ensureAudio();
                socket.onmessage = event => {
                    if (typeof event.data === "string") {
                        const message = JSON.parse(event.data);
                        if (message.type === "control") {
                            applyControl(message);
                        }

                        return;
                    }

                    if (node) {
                        node.port.postMessage({
                            type: "samples",
                            samples: new Float32Array(event.data)
                        }, [event.data]);
                    }
                };
                socket.onclose = () => {
                    reconnectTimer = setTimeout(connect, 1000);
                };
                socket.onerror = () => {
                    try {
                        socket.close();
                    } catch {
                    }
                };
            }

            setInterval(() => {
                if (context && context.state !== "running") {
                    context.resume().catch(() => {});
                }
            }, 1000);

            connect();
        })();
        </script>
        </body>
        </html>
        """;
}
