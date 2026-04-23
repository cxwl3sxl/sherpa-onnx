using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SherpaOnnx;

namespace WsAsrService;

class Program
{
    private static AppConfig? _config;
    private static OfflineRecognizer? _recognizer;
    private static VadModelConfig _vadConfig = new();
    private static readonly byte[] EndMarker = [255, 255, 255, 255];

    static async Task Main(string[] args)
    {
        var configPath = args.Length > 0 ? args[0] : "config.json";
        Console.WriteLine($"Loading config from: {configPath}");

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Config file not found: {configPath}");
            return;
        }

        var json = await File.ReadAllTextAsync(configPath);
        _config = JsonSerializer.Deserialize<AppConfig>(json);
        if (_config == null)
        {
            Console.WriteLine("Failed to parse config.json");
            return;
        }

        Console.WriteLine($"Server config: {_config.Server.Host}:{_config.Server.Port}");
        Console.WriteLine($"Auth token: {_config.Auth.Token}");

        // Initialize ASR
        InitAsr();

        // Start WebSocket server
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{_config.Server.Host}:{_config.Server.Port}/");
        listener.Start();
        Console.WriteLine($"WebSocket server listening on ws://{_config.Server.Host}:{_config.Server.Port}/");

        while (true)
        {
            var context = await listener.GetContextAsync();
            if (context.Request.IsWebSocketRequest)
            {
                _ = HandleWebSocketAsync(context);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }

    private static void InitAsr()
    {
        if (_config == null) return;

        // Check model files exist
        if (!File.Exists(_config.Model.Paraformer))
        {
            Console.WriteLine($"Model not found: {_config.Model.Paraformer}");
            Console.WriteLine("Please download from https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models");
            return;
        }

        if (!File.Exists(_config.Model.Tokens))
        {
            Console.WriteLine($"Tokens not found: {_config.Model.Tokens}");
            return;
        }

        if (!File.Exists(_config.Model.Vad))
        {
            Console.WriteLine($"VAD not found: {_config.Model.Vad}");
            Console.WriteLine("Please download silero_vad.onnx or ten-vad.onnx");
            return;
        }

        Console.WriteLine("Initializing ASR model...");
        var recognizerConfig = new OfflineRecognizerConfig();
        recognizerConfig.ModelConfig.Paraformer.Model = _config.Model.Paraformer;
        recognizerConfig.ModelConfig.Tokens = _config.Model.Tokens;
        recognizerConfig.ModelConfig.Debug = 0;
        _recognizer = new OfflineRecognizer(recognizerConfig);

        _vadConfig = new VadModelConfig();
        _vadConfig.SileroVad.Model = _config.Model.Vad;
        _vadConfig.SileroVad.Threshold = 0.3f;
        _vadConfig.SileroVad.MinSilenceDuration = 0.5f;
        _vadConfig.SileroVad.MinSpeechDuration = 0.25f;
        _vadConfig.SileroVad.MaxSpeechDuration = 5.0f;
        _vadConfig.SileroVad.WindowSize = 512;
        _vadConfig.Debug = 0;

        Console.WriteLine("ASR model initialized successfully");
    }

    private static async Task HandleWebSocketAsync(HttpListenerContext context)
    {
        WebSocket? ws = null;
        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            ws = wsContext.WebSocket;

            Console.WriteLine("New WebSocket connection");
            var (authenticated, error) = await AuthenticateAsync(ws);
            if (!authenticated)
            {
                await SendMessageAsync(ws, new WsMessage
                {
                    Type = "auth",
                    Success = false,
                    Error = error
                });
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, error ?? "Auth failed", CancellationToken.None);
                return;
            }

            await SendMessageAsync(ws, new WsMessage
            {
                Type = "auth",
                Success = true,
                Content = "Authenticated"
            });

            Console.WriteLine("Client authenticated");
            await ProcessAudioAsync(ws);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket error: {ex.Message}");
        }
        finally
        {
            if (ws != null)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                }
                catch { }
            }
            Console.WriteLine("WebSocket connection closed");
        }
    }

    private static async Task<(bool, string?)> AuthenticateAsync(WebSocket ws)
    {
        try
        {
            var buffer = new byte[4096];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                return (false, "Expected text message for auth");
            }

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var msg = JsonSerializer.Deserialize<WsMessage>(json);

            if (msg?.Type != "auth")
            {
                return (false, "First message must be auth");
            }

            if (msg.Content != _config?.Auth.Token)
            {
                return (false, "Invalid token");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task ProcessAudioAsync(WebSocket ws)
    {
        if (_config == null || _recognizer == null)
        {
            await SendMessageAsync(ws, new WsMessage
            {
                Type = "error",
                Success = false,
                Error = "Server not initialized"
            });
            return;
        }

        // Create VAD for this session
        var vad = new VoiceActivityDetector(_vadConfig, 60);
        var buffer = new byte[4096];
        var endMarker = _config.Audio.EndMarker.Split(',').Select(byte.Parse).ToArray();
        var sampleRate = _vadConfig.SampleRate;
        var windowSize = _vadConfig.SileroVad.WindowSize;

        while (ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var data = buffer.Take(result.Count).ToArray();

                // Check for end marker
                if (data.Length >= endMarker.Length &&
                    data.TakeLast(endMarker.Length).SequenceEqual(endMarker))
                {
                    Console.WriteLine("Received end marker, processing audio...");
                    break;
                }

                // Process through VAD
                var samples = ConvertToFloat(data);
                vad.AcceptWaveform(samples);

                if (vad.IsSpeechDetected())
                {
                    while (!vad.IsEmpty())
                    {
                        var segment = vad.Front();
                        var text = RecognizeSegment(segment.Samples);
                        if (!string.IsNullOrEmpty(text))
                        {
                            Console.WriteLine($"Result: {text}");
                            await SendMessageAsync(ws, new WsMessage
                            {
                                Type = "result",
                                Success = true,
                                Content = text
                            });
                        }
                        vad.Pop();
                    }
                }
            }
            catch (WebSocketException ex)
            {
                Console.WriteLine($"Receive error: {ex.Message}");
                break;
            }
        }

        // Flush VAD
        vad.Flush();

        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            var text = RecognizeSegment(segment.Samples);
            if (!string.IsNullOrEmpty(text))
            {
                Console.WriteLine($"Result: {text}");
                await SendMessageAsync(ws, new WsMessage
                {
                    Type = "result",
                    Success = true,
                    Content = text
                });
            }
            vad.Pop();
        }

        await SendMessageAsync(ws, new WsMessage
        {
            Type = "done",
            Success = true,
            Content = "Recognition completed"
        });
    }

    private static float[] ConvertToFloat(byte[] data)
    {
        var samples = new float[data.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        }
        return samples;
    }

    private static string RecognizeSegment(float[] samples)
    {
        if (_recognizer == null || samples.Length == 0) return "";

        var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(_config?.Audio.SampleRate ?? 16000, samples);
        _recognizer.Decode(stream);
        return stream.Result.Text ?? "";
    }

    private static async Task SendMessageAsync(WebSocket ws, WsMessage msg)
    {
        var json = JsonSerializer.Serialize(msg);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
