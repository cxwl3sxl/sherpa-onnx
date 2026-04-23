# WebSocket语音识别服务协议

## 概述

本服务提供基于WebSocket的远程语音识别功能，采用VAD（语音活动检测）+ Paraformer离线识别模型。

## 连接信息

- **地址**: `ws://<host>:8080/?token=<your-token>`
- **认证方式**: URL Query Token

## 认证方式

客户端连接时将Token放在URL查询参数中：

```
ws://localhost:8080/?token=your-secret-token-here
```

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
- 采样率: 16000 Hz
- 位深: 16 bit
- 声道: 单声道 (mono)
- 帧大小: 1280 字节 (= 640个采样点 = 40ms @ 16kHz)

音频数据以二进制方式发送，**不是JSON**。每次发送1280字节的原始PCM数据。

#### 3. 结束标记 (客户端 → 服务器)

客户端发送完所有音频后，发送结束标记。

**结束标记**: `CAFEBABE-FADE-BABE-DEAD-BEEF-FADEBAABE` (16字节)

格式支持：
- Hex格式: `CAFEBABE-FADE-BABE-DEAD-BEEF-FADEBAABE`
- 逗号分隔: `202,254,186,190,250,222,186,190,222,173,190,239,250,222,186,190`

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
  |---- WS连接 (带token) --------------------------->|
  |                                                |
  |<-- {"type":"auth","success":true} ----------|
  |                                                |
  |---- 1280 bytes音频数据 (N次) ---------------→|
  |<-- {"type":"result","success":true,...} ----|
  |<-- {"type":"result","success":true,...} ----|
  |                                                |
  |---- [0xFF,0xFF,0xFF,0xFF] (结束标记) ------>|
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
  },
"audio": {
    "sampleRate": 16000
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
    uri = "ws://localhost:8080/?token=your-secret-token-here"
    async with websockets.connect(uri) as ws:
        # 接收认证响应
        resp = json.loads(await ws.recv())
        print(f"Auth: {resp}")

        # 发送音频
        with open("audio.wav", "rb") as f:
            f.seek(44)  # 跳过WAV头
            while chunk := f.read(1280):
                await ws.send(chunk)

# 发送结束标记 (Java magic number)
await ws.send(bytes([0xCA, 0xFE, 0xBA, 0xBE, 0xFA, 0xDE, 0xBA, 0xBE,
                   0xDE, 0xAD, 0xBE, 0xEF, 0xFA, 0xDE, 0xBA, 0xBE]))

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
await client.ConnectAsync(new Uri("ws://localhost:8080/?token=your-secret-token"));

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

// 发送结束标记 (Java magic number)
await client.SendAsync(
    new ArraySegment<byte>(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0xFA, 0xDE, 0xBA, 0xBE,
                                0xDE, 0xAD, 0xBE, 0xEF, 0xFA, 0xDE, 0xBA, 0xBE }),
    WebSocketMessageType.Binary, true, CancellationToken.None);
```

### WebSocket JS客户端示例

```javascript
const ws = new WebSocket('ws://localhost:8080/?token=your-secret-token-here');

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
// 发送结束标记 (Java magic number)
ws.send(new Uint8Array([0xCA, 0xFE, 0xBA, 0xBE, 0xFA, 0xDE, 0xBA, 0xBE,
                        0xDE, 0xAD, 0xBE, 0xEF, 0xFA, 0xDE, 0xBA, 0xBE]));
```