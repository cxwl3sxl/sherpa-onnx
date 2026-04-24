namespace WsAsrService;

public class SecurityConfig
{
  /// <summary>
  /// 允许访问 HTTP 端点 (如 /stats, /health) 的 IP 白名单
  /// 支持 CIDR 格式 (如 "192.168.1.0/24") 和单个 IP (如 "192.168.1.100")
  /// 为空时允许所有 IP
  /// </summary>
  public List<string> AllowedIps { get; set; } = new();
}