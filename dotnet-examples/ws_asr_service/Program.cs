using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace WsAsrService;

static class Win32Process
{
  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}

class Program
{
  private static AppConfig? _config;
  private const string ServiceName = "WsAsrService";

  static async Task<int> Main(string[] args)
  {
    // 解析命令行参数
    var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
      ? OSPlatform.Windows
      : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        ? OSPlatform.Linux
        : OSPlatform.FreeBSD;

    // 检测是否以服务模式运行 (无控制台)
    var isService = IsRunningAsService(os);

    if (args.Length > 0 && !isService)
    {
      var command = args[0].ToLowerInvariant();
      return command switch
      {
        "install" => await InstallServiceAsync(os),
        "uninstall" => await UninstallServiceAsync(os),
        "start" => await StartServiceAsync(os),
        "stop" => await StopServiceAsync(os),
        "status" => await StatusServiceAsync(os),
        "-h" or "--help" or "/?" => ShowHelp(),
        _ => ShowHelp()
      };
    }

    // 无参数: 以控制台模式运行 (原有逻辑)
    return await RunAsConsoleAsync(isService);
  }

  private static bool IsRunningAsService(OSPlatform os)
  {
    if (os != OSPlatform.Windows)
    {
      return false;
    }

    // 检查进程是否作为服务运行
    // 通过检查是否是 Session 0 隔离进程来判断
    try
    {
      var processId = (uint)Process.GetCurrentProcess().Id;
      if (Win32Process.ProcessIdToSessionId(processId, out var sessionId))
      {
        return sessionId == 0; // Windows 服务运行在 Session 0
      }
    }
    catch
    {
      // ignored
    }

    return false;
  }

  private static int ShowHelp()
  {
    Console.WriteLine($"""
      Usage: WsAsrService [command]
      
      Commands:
        install   Install service (requires admin/root)
        uninstall Remove service (requires admin/root)
        start    Start service
        stop     Stop service
        status   Show service status
        -h, --help, /?  Show this help
      
      Examples:
        WsAsrService          Run as console application
        WsAsrService install Install as Windows Service or Linux systemd
        WsAsrService status  Check service status
      """);
    return 0;
  }

  private static async Task<int> InstallServiceAsync(OSPlatform os)
  {
    try
    {
      var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
      if (string.IsNullOrEmpty(exePath))
      {
        await Console.Error.WriteLineAsync("Cannot determine executable path");
        return 1;
      }

      if (os == OSPlatform.Windows)
      {
        // Windows: 使用 sc.exe 创建服务
        // 确保以管理员权限运行
        var result = RunCommand("sc.exe", $"create {ServiceName} binPath= \"{exePath}\" start= auto", requireAdmin: true);
        if (result != 0) return result;

        // 设置描述
        RunCommand("sc.exe", $"description {ServiceName} \"WebSocket ASR Service\"", requireAdmin: false);

        // 设置为自动启动
        RunCommand("sc.exe", $"config {ServiceName} start= auto", requireAdmin: true);

        Console.WriteLine($"Service '{ServiceName}' installed successfully");
      }
      else
      {
        // Linux: 创建 systemd service 文件
        var serviceContent = $"""
          [Unit]
          Description=WebSocket ASR Service
          After=network.target
          
          [Service]
          Type=simple
          ExecStart={exePath}
          Restart=on-failure
          RestartSec=10
          User=root
          
          [Install]
          WantedBy=multi-user.target
          """;

        var servicePath = $"/etc/systemd/system/{ServiceName}.service";
        await File.WriteAllTextAsync(servicePath, serviceContent);

        // 重载 systemd 并启用服务
        RunCommand("systemctl", "daemon-reload", requireRoot: true);
        RunCommand("systemctl", $"enable {ServiceName}", requireRoot: true);

        Console.WriteLine($"Service '{ServiceName}' installed and enabled");
      }

      return 0;
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"Failed to install service: {ex.Message}");
      return 1;
    }
  }

  private static async Task<int> UninstallServiceAsync(OSPlatform os)
  {
    try
    {
      if (os == OSPlatform.Windows)
      {
        // 先停止服务 (如果正在运行)
        RunCommand("sc.exe", $"stop {ServiceName}", requireAdmin: false);

        // 删除服务
        var result = RunCommand("sc.exe", $"delete {ServiceName}", requireAdmin: true);
        if (result == 0)
        {
          Console.WriteLine($"Service '{ServiceName}' uninstalled successfully");
        }
        return result;
      }
      else
      {
        // Linux: 停止并禁用服务
        RunCommand("systemctl", $"stop {ServiceName}", requireRoot: false);
        RunCommand("systemctl", $"disable {ServiceName}", requireRoot: true);

        // 删除 service 文件
        var servicePath = $"/etc/systemd/system/{ServiceName}.service";
        if (File.Exists(servicePath))
        {
          File.Delete(servicePath);
        }

        RunCommand("systemctl", "daemon-reload", requireRoot: true);

        Console.WriteLine($"Service '{ServiceName}' uninstalled successfully");
      }

      return 0;
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"Failed to uninstall service: {ex.Message}");
      return 1;
    }
  }

  private static Task<int> StartServiceAsync(OSPlatform os)
  {
    try
    {
      if (os == OSPlatform.Windows)
      {
        RunCommand("sc.exe", $"start {ServiceName}", requireAdmin: false);
        Console.WriteLine($"Service '{ServiceName}' started");
      }
      else
      {
        RunCommand("systemctl", $"start {ServiceName}", requireRoot: false);
        Console.WriteLine($"Service '{ServiceName}' started");
      }
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"Failed to start service: {ex.Message}");
      return Task.FromResult(1);
    }

    return Task.FromResult(0);
  }

  private static Task<int> StopServiceAsync(OSPlatform os)
  {
    try
    {
      if (os == OSPlatform.Windows)
      {
        RunCommand("sc.exe", $"stop {ServiceName}", requireAdmin: false);
        Console.WriteLine($"Service '{ServiceName}' stopped");
      }
      else
      {
        RunCommand("systemctl", $"stop {ServiceName}", requireRoot: false);
        Console.WriteLine($"Service '{ServiceName}' stopped");
      }
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"Failed to stop service: {ex.Message}");
      return Task.FromResult(1);
    }

    return Task.FromResult(0);
  }

  private static async Task<int> StatusServiceAsync(OSPlatform os)
  {
    try
    {
      if (os == OSPlatform.Windows)
      {
        var (output, exitCode) = await RunCommandAsync("sc.exe", $"query {ServiceName}");
        
        if (output.Contains("STATE"))
        {
          // 解析状态输出
          var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
          foreach (var line in lines)
          {
            if (line.Contains("STATE"))
            {
              Console.WriteLine(line.Trim());
            }
          }
          
          // 检查是否在运行
          if (output.Contains("RUNNING"))
          {
            Console.WriteLine("Status: Running");
            return 0;
          }
          else if (output.Contains("STOPPED"))
          {
            Console.WriteLine("Status: Stopped");
            return 3;
          }
        }
        
        if (exitCode != 0 && !output.Contains("STATE"))
        {
          Console.WriteLine($"Service '{ServiceName}' is not installed");
          return 1;
        }
      }
      else
      {
        var (output, _) = await RunCommandAsync("systemctl", $"status {ServiceName}");
        Console.Write(output);
      }

      return 0;
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"Failed to get service status: {ex.Message}");
      return 1;
    }
  }

  // 执行外部命令的辅助方法
  private static int RunCommand(string command, string arguments, bool requireAdmin = false, bool requireRoot = false)
  {
    // 检查权限
    if (requireAdmin && !IsRunningAsAdmin())
    {
      Console.Error.WriteLine("This command requires administrator privileges.");
      Console.Error.WriteLine("Please run the command prompt as Administrator and try again.");
      return 1;
    }

    if (requireRoot && !IsRunningAsRoot())
    {
      Console.Error.WriteLine("This command requires root privileges.");
      Console.Error.WriteLine("Please run with sudo or as root user and try again.");
      return 1;
    }

    var (output, exitCode) = RunCommandAsync(command, arguments).GetAwaiter().GetResult();
    if (exitCode != 0)
    {
      Console.WriteLine(output);
    }
    return exitCode;
  }

  private static bool IsRunningAsAdmin()
  {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      return false;
    }

    try
    {
      using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
      var principal = new System.Security.Principal.WindowsPrincipal(identity);
      return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
    catch
    {
      return false;
    }
  }

  private static bool IsRunningAsRoot()
  {
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      return false;
    }

    // Linux/macOS: check if running as root via id command
    try
    {
      var psi = new ProcessStartInfo
      {
        FileName = "id",
        Arguments = "-u",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };
      using var process = Process.Start(psi);
      if (process != null)
      {
        process.WaitForExit();
        return process.ExitCode == 0 && process.StandardOutput.ReadToEnd().Trim() == "0";
      }
    }
    catch
    {
      // ignored
    }

    return false;
  }

  private static async Task<(string output, int exitCode)> RunCommandAsync(string command, string arguments)
  {
    var psi = new ProcessStartInfo
    {
      FileName = command,
      Arguments = arguments,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = Process.Start(psi);
    if (process == null)
    {
      return ("Failed to start process", 1);
    }

    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    var fullOutput = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";
    return (fullOutput, process.ExitCode);
  }

// ==================== 原有的控制台/服务运行逻辑 ====================
  private static async Task<int> RunAsConsoleAsync(bool isService = false)
  {
    // Windows 服务模式下, 使用可执行文件所在目录作为基础路径
    var baseDir = AppContext.BaseDirectory;
    var configPath = Path.Combine(baseDir, "config.json");

    var configuration = new ConfigurationBuilder()
      .SetBasePath(baseDir)
      .AddJsonFile("config.json", optional: false, reloadOnChange: true)
      .Build();

    _config = LoadConfiguration(configuration);
    if (_config == null)
    {
      await Console.Error.WriteLineAsync($"Failed to load configuration from: {configPath}");
      return 1;
    }

    // 配置 Serilog - Windows 服务模式下不输出到控制台
    var logDirectory = Path.GetFullPath(Path.Combine(baseDir, _config.Logging.LogDirectory));
    Directory.CreateDirectory(logDirectory);

    var minLevel = _config.Logging.Level.ToLowerInvariant() switch
    {
      "debug" => Serilog.Events.LogEventLevel.Debug,
      "warning" => Serilog.Events.LogEventLevel.Warning,
      "error" => Serilog.Events.LogEventLevel.Error,
      _ => Serilog.Events.LogEventLevel.Information
    };

    var loggerConfig = new LoggerConfiguration()
      .MinimumLevel.Is(minLevel)
      .WriteTo.File(Path.Combine(logDirectory,".log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: _config.Logging.RetainedDayCount,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}");

    // 控制台输出仅在非服务模式下启用
    if (!isService)
    {
      loggerConfig.WriteTo.Console();
    }

    Log.Logger = loggerConfig.CreateLogger();

    try
    {
      Log.Information("=== Starting WS-ASR Service ===");
      Log.Information("Base directory: {BaseDir}", baseDir);
      Log.Information("Server: {Host}:{Port}", _config.Server.Host, _config.Server.Port);
      Log.Information("Log directory: {LogDirectory}", logDirectory);
      Log.Information("Running as {Mode}", isService ? "Windows Service" : "Console");

      // 验证模型文件
      if (!ValidateModels(_config))
      {
        return 1;
      }

      var builder = Host.CreateApplicationBuilder();
      builder.Services.AddSingleton(_config);
      builder.Services.AddHostedService<WebSocketAsrHostedService>();

      var host = builder.Build();

      // 处理优雅关闭 (仅在控制台模式下有效)
      if (!isService)
      {
        Console.CancelKeyPress += (_, e) =>
        {
          e.Cancel = true;
          Log.Information("Received Ctrl+C, shutting down gracefully...");
        };
      }

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
    // 从 configuration 获取路径 (已通过 SetBasePath 设置)
    var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
    if (!File.Exists(configPath))
    {
      return null;
    }
    var json = File.ReadAllText(configPath);
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
