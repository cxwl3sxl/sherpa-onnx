using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace WsAsrService;

class Program
{
  private static AppConfig? _config;

  static async Task<int> Main()
  {
    var configuration = new ConfigurationBuilder()
      .AddJsonFile("config.json", optional: false, reloadOnChange: true)
      .Build();

    _config = LoadConfiguration(configuration);
    if (_config == null)
    {
      await Console.Error.WriteLineAsync("Failed to load configuration");
      return 1;
    }

    // 配置 Serilog
    var logDirectory = Path.GetFullPath(_config.Logging.LogDirectory);
    Directory.CreateDirectory(logDirectory);

    var minLevel = _config.Logging.Level.ToLowerInvariant() switch
    {
      "debug" => Serilog.Events.LogEventLevel.Debug,
      "warning" => Serilog.Events.LogEventLevel.Warning,
      "error" => Serilog.Events.LogEventLevel.Error,
      _ => Serilog.Events.LogEventLevel.Information
    };

    Log.Logger = new LoggerConfiguration()
      .MinimumLevel.Is(minLevel)
      .WriteTo.Console()
      .WriteTo.File(Path.Combine(logDirectory),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: _config.Logging.RetainedDayCount,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
      .CreateLogger();

    try
    {
      Log.Information("=== Starting WS-ASR Service ===");
      Log.Information("Server: {Host}:{Port}", _config.Server.Host, _config.Server.Port);
      Log.Information("Log directory: {LogDirectory}", logDirectory);

      // 验证模型文件
      if (!ValidateModels(_config))
      {
        return 1;
      }

      var builder = Host.CreateApplicationBuilder();
      builder.Services.AddSingleton(_config);
      builder.Services.AddHostedService<WebSocketAsrHostedService>();

      var host = builder.Build();

      // 处理优雅关闭
      Console.CancelKeyPress += (_, e) =>
      {
        e.Cancel = true;
        Log.Information("Received Ctrl+C, shutting down gracefully...");
      };

      await host.RunAsync();
      return 0;
    }
    catch (Exception ex)
    {
      Log.Fatal(ex, "Service terminated unexpectedly");
      return 1;
    }
    finally
    {
      await Log.CloseAndFlushAsync();
    }
  }

  private static AppConfig? LoadConfiguration(IConfigurationRoot _)
  {
    var json = File.ReadAllText("config.json");
    var jsonOptions = new JsonSerializerOptions
    {
      PropertyNameCaseInsensitive = true
    };
    return JsonSerializer.Deserialize<AppConfig>(json, jsonOptions);
  }

  private static bool ValidateModels(AppConfig config)
  {
    if (!File.Exists(config.Model.Paraformer))
    {
      Log.Error(
        "Model not found: {Path}\nPlease download from https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models",
        config.Model.Paraformer);
      return false;
    }

    if (!File.Exists(config.Model.Tokens))
    {
      Log.Error("Tokens not found: {Path}", config.Model.Tokens);
      return false;
    }

    if (!File.Exists(config.Model.Vad))
    {
      Log.Error("VAD not found: {Path}\nPlease download silero_vad.onnx or ten-vad.onnx", config.Model.Vad);
      return false;
    }

    return true;
  }
}
