using Microsoft.Extensions.Hosting;
using Serilog;

namespace WsAsrService;

/// <summary>
/// 后台服务实现 - 支持 Windows 和 Linux
/// </summary>
public class WebSocketAsrHostedService(AppConfig config) : IHostedService
{
  private WebSocketServer? _server;

  public Task StartAsync(CancellationToken cancellationToken)
  {
    Log.Information("Starting WebSocket ASR service: {Host}:{Port}", config.Server.Host, config.Server.Port);
    _server = new WebSocketServer(config);
    return _server.StartAsync(cancellationToken);
  }

  public async Task StopAsync(CancellationToken cancellationToken)
  {
    Log.Information("Stopping WebSocket ASR service...");
    if (_server != null)
    {
      await _server.StopAsync(cancellationToken);
    }
    Log.Information("WebSocket ASR service stopped");
  }
}
