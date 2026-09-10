# WebSocket语音识别服务协议

## 概述

本服务提供基于WebSocket的远程语音识别功能，采用VAD（语音活动检测）+ Paraformer离线识别模型。

## 连接信息

- **地址**: `ws://<host>:8080/?sample_rate=16000`
- **认证方式**: HTTP 请求头 `Authorization: Bearer <token>`（**不支持** URL 参数传 token）
- **采样率**: 通过 `sample_rate` 参数指定（默认 16000 Hz）。服务器内部会将音频重采样至 16000 Hz 后送 VAD 和 ASR 模型处理，支持范围 8000-48000 Hz。

## 认证方式

客户端连接时需要设置 `Authorization` 请求头：

```
Authorization: Bearer your-secret-token-here

连接地址: ws://localhost:8080/?sample_rate=16000
```

注意：认证头值需要 `Bearer` 前缀 + 空格 + token。浏览器端 WebSocket 无法自定义请求头，需通过反向代理（如 nginx `proxy_set_header`）或后端转发携带。

## 通信协议

### 消息格式

认证响应和识别结果采用JSON格式，编码UTF-8。

#### 1. 认证响应 (服务器 → 客户端)

连接成功后立即返回：

```json
{
  "type": "auth",
  "success": true,
  "content": "Authenticated"
}
```

认证失败时返回错误并关闭连接：
```json
{
  "type": "auth",
  "success": false,
  "error": "Invalid token"
}
```

#### 2. 音频数据 (客户端 → 服务器)

认证成功后，客户端循环发送音频数据。

**音频格式**:
- 采样率: 通过连接参数 `sample_rate` 指定（默认 16000 Hz），服务器内部自动重采样至 16000 Hz
- 位深: 16 bit
- 声道: 单声道 (mono)
- 帧大小: 1280 字节 (= 640个采样点 = 40ms @ sample_rate Hz)

音频数据以二进制方式发送，**不是JSON**。每次发送1280字节的原始PCM数据。

#### 3. 结束标记 (客户端 → 服务器)

客户端发送完所有音频后，发送结束标记（一个 16 字节的二进制帧）。

**结束标记**: `1049712a-2b0c-4be5-8c36-573e8a40f6d5` (16字节)

格式支持（作为原始字节发送，不是字符串）：
- Hex格式: `10 49 71 2a 2b 0c 4b e5 8c 36 57 3e 8a 40 f6 d5`
- 逗号分隔（十进制）: `16,73,113,42,43,12,75,229,140,54,87,62,138,64,246,213`

注意：结束标记必须位于**单个 WebSocket 消息的末尾**，且不能与其他音频数据合并在同一条消息中发送（否则标记字节会被当作音频识别）。

#### 4. 识别结果 (服务器 → 客户端)

服务器检测到语音片段后，实时返回识别结果：

```json
{
  "type": "result",
  "success": true,
  "content": "识别文本内容",
  "startMs": 0,
  "endMs": 1250
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| content | string | 识别文本 |
| startMs | long | 语音段开始时间 (毫秒) |
| endMs | long | 语音段结束时间 (毫秒) |

#### 5. 完成消息 (服务器 → 客户端)

识别完成后，服务器发送完成消息并可主动断开连接：

```json
{
  "type": "done",
  "success": true,
  "content": "Recognition completed"
}
```

## 完整交互流程

```
客户端                                          服务器
  |                                                |
  |---- WS连接 (Authorization: Bearer <token>) --->|
  |                                                |
  |<-- {"type":"auth","success":true} ----------|
  |                                                |
  |---- 1280 bytes音频数据 (N次) ---------------→|
  |<-- {"type":"result","success":true,...} ----|
  |<-- {"type":"result","success":true,...} ----|
  |                                                |
  |---- 结束标记 (16字节, 10 49 71 2a ...) ---->|
  |<-- {"type":"done","success":true} ----------|
  |--------------- 连接关闭 -------------------->|
```

## 错误响应

所有错误响应格式：

```json
{
  "type": "<消息类型>",
  "success": false,
  "error": "<错误描述>"
}
```

## 配置说明

编辑 `config.json` 修改服务配置：

```json
{
  "server": {
    "host": "0.0.0.0",
    "port": 8080
  },
  "auth": {
    "token": "your-secret-token-here"
  },
  "model": {
    "paraformer": "./models/sherpa-onnx-paraformer-zh-2023-09-14/model.int8.onnx",
    "tokens": "./models/sherpa-onnx-paraformer-zh-2023-09-14/tokens.txt",
    "vad": "./models/silero_vad.onnx"
  }
}
```

## 使用示例

### Python客户端示例

```python
import asyncio
import websockets
import json

async def recognize():
    uri = "ws://localhost:8080/?sample_rate=16000"
    headers = {"Authorization": "Bearer your-secret-token-here"}
    async with websockets.connect(uri, extra_headers=headers) as ws:
        # 接收认证响应
        resp = json.loads(await ws.recv())
        print(f"Auth: {resp}")

        # 发送音频
        with open("audio.wav", "rb") as f:
            f.seek(44)  # 跳过WAV头
            while chunk := f.read(1280):
                await ws.send(chunk)

        # 发送结束标记 (16字节)
        await ws.send(bytes.fromhex('1049712a2b0c4be58c36573e8a40f6d5'))

        # 接收结果
        while True:
            resp = json.loads(await ws.recv())
            if resp["type"] == "result":
                print(f"Result: {resp['content']}")
            elif resp["type"] == "done":
                break

asyncio.run(recognize())
```

### C#客户端示例

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using var client = new ClientWebSocket();
// 通过请求头携带 token
client.Options.SetRequestHeader("Authorization", "Bearer your-secret-token-here");
await client.ConnectAsync(new Uri("ws://localhost:8080/?sample_rate=16000"));

var buffer = new byte[4096];
var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, result.Count));

// 发送音频
var audio = File.ReadAllBytes("audio.wav").Skip(44).ToArray();
for (int i = 0; i < audio.Length; i += 1280)
{
    await client.SendAsync(
        new ArraySegment<byte>(audio, i, Math.Min(1280, audio.Length - i)),
        WebSocketMessageType.Binary, i + 1280 >= audio.Length, CancellationToken.None);
}

// 发送结束标记 (16字节)
await client.SendAsync(
    new ArraySegment<byte>(new byte[] { 0x10, 0x49, 0x71, 0x2a, 0x2b, 0x0c, 0x4b, 0xe5,
                                0x8c, 0x36, 0x57, 0x3e, 0x8a, 0x40, 0xf6, 0xd5 }),
    WebSocketMessageType.Binary, true, CancellationToken.None);
```

### WebSocket JS客户端示例

浏览器 WebSocket 无法自定义请求头，需通过反向代理（如 nginx `proxy_set_header Authorization "Bearer your-secret-token-here"`）注入认证头：

```javascript
const ws = new WebSocket('ws://localhost:8080/?sample_rate=16000');

ws.onmessage = (event) => {
    const msg = JSON.parse(event.data);
    if (msg.type === 'auth') {
        console.log('Auth:', msg);
    } else if (msg.type === 'result') {
        console.log('Result:', msg.content);
    } else if (msg.type === 'done') {
        console.log('Done');
        ws.close();
    }
};

// 发送音频文件
const audioBuffer = await fetch('audio.wav').then(r => r.arrayBuffer());
const audioData = new Uint8Array(audioBuffer);
// 发送音频数据（跳过44字节WAV头）
for (let i = 44; i < audioData.length; i += 1280) {
    ws.send(audioData.slice(i, i + 1280));
}
// 发送结束标记 (16字节)
ws.send(new Uint8Array([0x10, 0x49, 0x71, 0x2a, 0x2b, 0x0c, 0x4b, 0xe5,
                        0x8c, 0x36, 0x57, 0x3e, 0x8a, 0x40, 0xf6, 0xd5]));
```