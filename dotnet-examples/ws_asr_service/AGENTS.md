# AGENTS.md - ws_asr_service

## Project Context

- **Location**: `dotnet-examples/ws_asr_service/`
- **Language**: C# (.NET 8)
- **Framework**: ASP.NET Core (Kestrel) + Sherpa-ONNX
- **Build**: `dotnet build` or `dotnet publish -c Release -o dist/`

## Build & Run

```bash
# Build
dotnet build

# Run locally
dotnet run --project ws_asr_service.csproj

# Console commands
./WsAsrService              # Run as console
./WsAsrService install      # Install Windows Service (requires admin)
./WsAsrService start       # Start service
./WsAsrService stop        # Stop service
./WsAsrService status      # Check status
./WsAsrService uninstall   # Remove service
```

## Required Setup

1. **Model files** must exist in `./models/` (configured in `config.json`):
   - `model.paraformer` - ASR model (.onnx)
   - `model.tokens` - vocabulary file
   - `model.vad` - voice activity detector (.onnx)
   
2. **config.json** - required at application base directory. See `config.json` for structure.

3. **Windows**: May need `netsh http add urlacl url=http://+:8080/ user=<username>` for port binding.

## Key Implementation Details

### WebSocket Protocol

- **End marker**: `1049712a-2b0c-4be5-8c36-573e8a40f6d5` (bytes: `10 49 71 2a 2b 0c 4b e5 8c 36 57 3e 8a 40 f6 d5`)
  - Note: PROTOCOL.md shows `CAFEBABE-FADE-BABE-DEAD-BEEF-FADEBAABE` which is DIFFERENT from actual implementation
- **Audio format**: PCM 16-bit, 16000 Hz, mono, little-endian
- **Frame size**: 1280 bytes (= 640 samples = 40ms @ 16kHz)

### Authentication

Two methods supported:
1. **Header**: `Authorization: Bearer <token>`
2. **Query**: `ws://host:port/?token=<token>`

### HTTP Endpoints

- `GET /stats` - server statistics
- `GET /health` - health check
- WebSocket at `/` - recognition endpoint

### Concurrency

- Pool size controlled by `server.maxConcurrency` (default: 4)
- Each instance uses ~60-80 MB memory
- Semaphore-based connection limiting

## Files

| File | Purpose |
|------|---------|
| `Program.cs` | Entry point, service lifecycle, CLI commands |
| `WebSocketServer.cs` | Kestrel server, WebSocket handling, ASR logic |
| `config.json` | Runtime configuration |
| `AppConfig.cs` | Configuration classes |
| `WebSocketAsrHostedService.cs` | Background service wrapper |

## Common Issues

- **Connection refused**: Check port binding, run `netsh http add urlacl` on Windows
- **Empty recognition**: Verify audio format (PCM 16-bit, 16000 Hz, mono)
- **Pool exhaustion**: Increase `server.maxConcurrency` in config.json