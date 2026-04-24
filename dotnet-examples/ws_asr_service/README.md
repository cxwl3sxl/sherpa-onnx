# WebSocket ASR Service

基于 Sherpa-ONNX 的实时语音识别服务，提供 WebSocket 接口进行音频流识别。

## 功能特性

- **实时语音识别**：支持流式音频输入，实时返回识别结果
- **连接池**：预创建识别引擎实例，支持高并发
- **VAD 语音检测**：内置 Silero VAD，自动检测语音段落
- **HTTP 监控 API**：提供服务器状态监控端点
- **IP 白名单**：可选的 IP 访问控制

## 快速开始

### 1. 下载模型文件

从 [sherpa-onnx releases](https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models) 下载：

```
paraformer.zip    # 识别模型
tokens.txt      # 词表
silero_vad.onnx # VAD 模型
```

### 2. 配置 config.json

```json
{
  "server": {
    "host": "0.0.0.0",
    "port": 8080,
    "maxConcurrency": 4
  },
  "auth": {
    "token": "your-secret-token"
  },
  "model": {
    "paraformer": "paraformer.onnx",
    "tokens": "tokens.txt",
    "vad": "silero_vad.onnx"
  },
  "audio": {
    "sampleRate": 16000
  },
  "logging": {
    "level": "information",
    "logDirectory": "logs",
    "retainedDayCount": 7
  }
}
```

### 3. 运行服务

```bash
# 控制台模式
./WsAsrService

# 或安装为系统服务
./WsAsrService install
./WsAsrService start
```

---

## WebSocket 通信协议

### 连接建立

```
ws://host:port/
```

认证通过 `Authorization` 请求头传递。

#### 认证方式

客户端需要在 WebSocket 连接时设置 `Authorization` 请求头：

| 语言 | 设置方式 |
|------|---------|
| Python | `websocket.create_connection(url, header={"Authorization": token})` |
| C# | `client.Options.SetRequestHeader("Authorization", token)` |
| JavaScript | 浏览器 WebSocket 不支持，可通过代理或后端转发 |

**认证头格式**：
```
Authorization: Bearer your-secret-token
```

**注意**：认证头值需要 `Bearer` 前缀 + 空格 + token。

### 音频格式要求

| 属性 | 要求 |
|------|------|
| 编码 | PCM 16-bit 有符号整数 |
| 采样率 | 16000 Hz |
| 声道 | 单声道 (mono) |
| 字节序 | 小端 (little-endian) |

### 客户端发送

客户端需发送**二进制 PCM 数据**，并在音频结束时发送结束标记。

**结束标记**（16进制）：
```
10 49 71 2a 2b 0c 4b e5 8c 36 57 3e 8a 40 f6 d5
```

或者直接关闭 WebSocket 连接。

### 服务器响应

服务器通过 JSON 格式返回识别结果：

#### 1. 认证响应

**成功**：
```json
{
  "type": "auth",
  "success": true
}
```

**失败**：
```json
{
  "type": "auth",
  "success": false,
  "error": "Invalid token"
}
```

#### 2. 识别结果

```json
{
  "type": "result",
  "success": true,
  "content": "识别文本内容",
  "startMs": 0,
  "endMs": 1500
}
```

- `content`：识别出的文本
- `startMs`：音频片段起始时间（毫秒）
- `endMs`：音频片段结束时间（毫秒）

#### 3. 识别完成

```json
{
  "type": "done",
  "success": true
}
```

#### 4. 错误

```json
{
  "type": "error",
  "success": false,
  "error": "错误描述"
}
```

### 通信流程图

```
Client                                          Server
  |                                                |
  |-------- WebSocket Connect ---------------------->| |
  |       (with Authorization header)               |
  |                                                |
  |<------- auth response ---------------------------|
  |   {"type":"auth","success":true}              |
  |                                                |
  |-------- Audio Binary Data ------------------->| |
  |-------- Audio Binary Data ------------------->| |
  |-------- ... ------------------------------>| |
  |                                                |
  |<------- result ---------------------------|
  |   {"type":"result","success":true,          |
  |    "content":"第一段","startMs":0,"endMs":500} |
  |                                                |
  |<------- result ---------------------------|
  |   {"type":"result","success":true,          |
  |    "content":"第二段","startMs":500,"endMs":1500}|
  |                                                |
  |-------- End Marker / Close ----------->| |
  |                                                |
  |<------- done ----------------------------|
  |   {"type":"done","success":true}            |
```

### 客户端示例

#### JavaScript

浏览器 WebSocket 不支持自定义请求头，需要通过后端代理或子协议方式传递 token。示例使用 nginx 代理：

```javascript
// 方式1: 通过 nginx 代理设置固定 token
// nginx.conf:
// proxy_set_header Authorization "your-secret-token";

// 方式2: 通过后端转发
const ws = new WebSocket('ws://backend-server:8080/');
```

#### Python

```python
import websocket
import json

def on_message(ws, message):
    msg = json.loads(message)
    msg_type = msg.get('type')
    
    if msg_type == 'auth':
        if not msg.get('success'):
            print(f"Auth failed: {msg.get('error')}")
            ws.close()
    elif msg_type == 'result':
        if msg.get('success'):
            print(f"Recognized: {msg.get('content')}")
    elif msg_type == 'done':
        print("Recognition completed")
        ws.close()

# 设置认证头
header = {"Authorization": "your-secret-token"}
ws = websocket.create_connection('ws://localhost:8080/', header=header)
ws.settimeout(30)
ws.bind(on_message)

# 发送音频文件
with open('audio.wav', 'rb') as f:
    while chunk := f.read(4096):
        ws.send(chunk)

# 发送结束标记
end_marker = bytes.fromhex('1049712a2b0c4be58c36573e8a40f6d5')
ws.send(end_marker)

ws.close()
```

#### C#

```csharp
using System.Net.WebSockets;
using System.Text;

var client = new ClientWebSocket();

// 添加 Authorization 请求头
client.Options.SetRequestHeader("Authorization", "your-secret-token");

await client.ConnectAsync(new Uri("ws://localhost:8080/"));

// 发送音频
var buffer = new byte[4096];
var bytesRead = await audioStream.ReadAsync(buffer);
while (bytesRead > 0)
{
    await client.SendAsync(buffer.AsMemory(0, bytesRead), WebSocketMessageType.Binary, true, CancellationToken.None);
    bytesRead = await audioStream.ReadAsync(buffer);
}

// 发送结束标记
var endMarker = new byte[] { 0x10, 0x49, 0x71, 0x2a, 0x2b, 0x0c, 0x4b, 0xe5,
                          0x8c, 0x36, 0x57, 0x3e, 0x8a, 0x40, 0xf6, 0xd5 };
await client.SendAsync(endMarker.AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);

// 接收响应
var responseBuffer = new byte[8192];
while (client.State == WebSocketState.Open)
{
    var result = await client.ReceiveAsync(responseBuffer, CancellationToken.None);
    var json = Encoding.UTF8.GetString(responseBuffer.AsSpan(0, result.Count));
    var msg = JsonSerializer.Deserialize<WsMessage>(json);
    // 处理消息
}

// 发送音频
var buffer = new byte[4096];
var bytesRead = await audioStream.ReadAsync(buffer);
while (bytesRead > 0)
{
    await client.SendAsync(buffer.AsMemory(0, bytesRead), WebSocketMessageType.Binary, true, CancellationToken.None);
    bytesRead = await audioStream.ReadAsync(buffer);
}

// 发送结束标记
var endMarker = new byte[] { 0x10, 0x49, 0x71, 0x2a, 0x2b, 0x0c, 0x4b, 0xe5,
                          0x8c, 0x36, 0x57, 0x3e, 0x8a, 0x40, 0xf6, 0xd5 };
await client.SendAsync(endMarker.AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);

// 接收响应
var responseBuffer = new byte[8192];
while (client.State == WebSocketState.Open)
{
    var result = await client.ReceiveAsync(responseBuffer, CancellationToken.None);
    var json = Encoding.UTF8.GetString(responseBuffer.AsSpan(0, result.Count));
    var msg = JsonSerializer.Deserialize<WsMessage>(json);
    // 处理消息
}
```

---

## HTTP API

### GET /stats

服务器状态监控端点。

**请求**：
```
GET http://localhost:8080/stats
```

**响应**：
```json
{
  "timestamp": "2026-04-24T10:30:00.000Z",
  "server": {
    "host": "0.0.0.0",
    "port": 8080,
    "uptime": "00:15:30"
  },
  "connections": {
    "active": 2,
    "totalRequests": 150,
    "maxConcurrency": 4,
    "availableSlots": 2
  },
  "recognizer": {
    "poolSize": 4,
    "availableInPool": 2,
    "emergencyInstances": 0,
    "maxEmergency": 2
  },
  "performance": {
    "processMemoryMb": 256,
    "threadCount": 14,
    "gcHeapSizeMb": 32
  }
}
```

**字段说明**：

| 字段 | 类型 | 说明 |
|------|------|------|
| timestamp | string | UTC 时间戳 |
| server.host | string | 服务监听地址 |
| server.port | int | 服务端口 |
| server.uptime | string | 运行时间 (dd:hh:mm:ss) |
| connections.active | int | 当前活动连接数 |
| connections.totalRequests | long | 总识别请求数 |
| connections.maxConcurrency | int | 最大并发数 |
| connections.availableSlots | int | 可用并发槽位 |
| recognizer.poolSize | int | 识别引擎池大小 |
| recognizer.availableInPool | int | 池中可用实例数 |
| recognizer.emergencyInstances | int | 紧急创建的实例数 |
| recognizer.maxEmergency | int | 最大紧急实例数 |
| performance.processMemoryMb | int | 进程内存 (MB) |
| performance.threadCount | int | 线程数 |
| performance.gcHeapSizeMb | int | GC 堆大小 (MB) |

---

### GET /health

健康检查端点。

**请求**：
```
GET http://localhost:8080/health
```

**响应**：
```json
{
  "status": "healthy",
  "timestamp": "2026-04-24T10:30:00.000Z",
  "processUptime": "00:15:30"
}
```

---

## 配置文件说明

### config.json 完整结构

```json
{
  // 服务器配置
  "server": {
    // 监听地址 (0.0.0.0 监听所有接口)
    "host": "0.0.0.0",
    // 监听端口
    "port": 8080,
    // 最大并发识别数
    // 建议: 4-8核CPU设为4-8，16+核设为8-16
    "maxConcurrency": 4,
    // 获取识别器超时时间(秒)
    "acquireTimeoutSeconds": 30
  },

  // 认证配置
  "auth": {
    // 认证Token，用于WebSocket连接验证
    "token": "your-secret-token"
  },

  // 模型配置
  "model": {
    // Paraformer识别模型路径 (.onnx)
    "paraformer": "paraformer.onnx",
    // 词表文件路径
    "tokens": "tokens.txt",
    // VAD模型路径 (.onnx)
    "vad": "silero_vad.onnx"
  },

  // 音频配置
  "audio": {
    // 采样率
    "sampleRate": 16000
  },

  // 日志配置
  "logging": {
    // 日志级别: Debug, Information, Warning, Error
    "level": "information",
    // 日志目录
    "logDirectory": "logs",
    // 保留天数
    "retainedDayCount": 7
  },

  // 安全配置 (可选)
  "security": {
    // IP白名单，支持格式:
    // - 单个IP: "192.168.1.100"
    // - CIDR: "192.168.1.0/24"
    // - 通配符: "192.168.1.*"
    // 为空时允许所有IP
    "allowedIps": [
      "127.0.0.1",
      "192.168.1.0/24"
    ]
  }
}
```

### 配置项说明

| 配置项 | 必填 | 默认值 | 说明 |
|--------|------|--------|------|
| server.host | 否 | 0.0.0.0 | 监听地址 |
| server.port | 否 | 8080 | 监听端口 |
| server.maxConcurrency | 否 | 4 | 最大并发数 |
| server.acquireTimeoutSeconds | 否 | 30 | 获取引擎超时(秒) |
| auth.token | 是 | - | 认证Token |
| model.paraformer | 是 | - | 识别模型路径 |
| model.tokens | 是 | - | 词表路径 |
| model.vad | 是 | - | VAD模型路径 |
| audio.sampleRate | 是 | 16000 | 采样率 |
| logging.level | 否 | information | 日志级别 |
| logging.logDirectory | 否 | logs | 日志目录 |
| logging.retainedDayCount | 否 | 7 | 保留天数 |
| security.allowedIps | 否 | 允许所有 | IP白名单 |

---

## 系统服务

### Windows

```bash
# 安装服务
WsAsrService.exe install

# 启动服务
WsAsrService.exe start

# 停止服务
WsAsrService.exe stop

# 检查状态
WsAsrService.exe status

# 卸载服务
WsAsrService.exe uninstall
```

### Linux

```bash
# 安装 systemd 服务
sudo ./WsAsrService install

# 启动服务
sudo ./WsAsrService start

# 停止服务
sudo ./WsAsrService stop

# 检查状态
sudo ./WsAsrService status

# 卸载服务
sudo ./WsAsrService uninstall
```

---

## 性能优化

### 调整并发数

根据 CPU 核心数调整 `maxConcurrency`：

| CPU 核心数 | 建议并发数 |
|----------|-----------|
| 4 核 | 4 |
| 8 核 | 6-8 |
| 16+ 核 | 12-16 |

### 内存估算

每个识别实例约需 **60-80 MB** 内存。

总内存 ≈ `maxConcurrency × 70 MB` + 系统开销

---

## 常见问题

### Q: WebSocket 连接失败

A: 检查：
1. 端口是否被占用
2. Token 是否正确
3. Windows 下需运行 `netsh http add urlacl url=http://+:8080/ user=<你的用户名>`

### Q: 识别结果为空

A: 检查音频格式：
1. 必须是 PCM 16-bit
2. 采样率必须是 16000 Hz
3. 必须是单声道

### Q: 并发数不足

A: 增加 `server.maxConcurrency` 值，或升级 CPU

---

## 许可证

MIT License