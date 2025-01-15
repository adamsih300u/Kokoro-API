# Kokoro TTS API Server

A Flask-based API server that provides Text-to-Speech capabilities using the Kokoro TTS model. Supports both WebSocket connections for real-time streaming audio generation.

## Features

- Real-time text-to-speech conversion
- WebSocket streaming with chunked audio delivery
- Multiple voice support
- Rootless container deployment
- Multi-architecture support (AMD64/ARM64)
- CPU-optimized inference
- Automatic text chunking and batching

## Quick Start

### Using Docker

```bash
# Pull the container
docker pull ghcr.io/adamsih300u/kokoro-api:latest

# Run the container
docker run -d -p 8000:8000 ghcr.io/adamsih300u/kokoro-api:latest
```

### Using Docker Compose

Create a `docker-compose.yml`:
```yaml
version: '3.8'
services:
  tts:
    image: ghcr.io/adamsih300u/kokoro-api:latest
    ports:
      - "8000:8000"
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8000/"]
      interval: 30s
      timeout: 10s
      retries: 3
```

Then run:
```bash
docker-compose up -d
```

## API Reference

### REST Endpoints

#### List Available Voices
```http
GET /voices
```

Response:
```json
{
    "voices": ["af", "other_voices..."],
    "default_voice": "af"
}
```

### WebSocket API

Connect to: `ws://your-server:8000/ws`

#### Commands

1. Set Voice:
```json
{
    "command": "set_voice",
    "voice": "af"  // or any available voice
}
```

2. Generate Speech:
```json
{
    "command": "tts",
    "text": "Your text here"
}
```

3. Heartbeat:
```json
{
    "command": "ping"
}
```

#### Server Responses

1. Initial Connection:
```json
{
    "status": "connected",
    "voices": ["af", "other_voices..."],
    "current_voice": "af"
}
```

2. Audio Chunks:
```json
{
    "type": "audio_chunk",
    "message_id": 1234567890,
    "audio_chunk": "base64_encoded_audio_data",
    "is_final": false,
    "chunk_index": 0,
    "total_chunks": 10,
    "text": "Original text"
}
```

## Audio Format Specifications

- Sample Rate: 22.05 kHz
- Bit Depth: 16-bit
- Channels: Mono
- Format: PCM WAV

## System Limitations

- Maximum text length: 500 characters
- Optimal text length: 200 characters
- Processing timeout: 240 seconds
- Chunk delay: 20ms between audio chunks

## Performance Optimization

- Automatic text chunking for long inputs
- Server-side audio streaming
- CPU optimization and threading
- Efficient base64 encoding/decoding
- Rootless container for security

## Development

### Building from Source

```bash
git clone https://github.com/adamsih300u/Kokoro-API.git
cd Kokoro-API
docker build -t kokoro-api:local .
```

### Environment Variables

```bash
PYTHONUNBUFFERED=1
FLASK_APP=app.py
FLASK_ENV=production
```

## Troubleshooting

1. **Audio Playback Issues**
   - Ensure proper handling of base64 encoded audio chunks
   - Verify WAV format compatibility
   - Check audio chunk ordering using chunk_index

2. **Connection Issues**
   - Verify WebSocket URL and port
   - Check for firewall restrictions
   - Ensure proper network connectivity

3. **Performance Issues**
   - Monitor CPU usage
   - Check memory consumption
   - Verify text length is within optimal range

## License

This project uses the Kokoro TTS model. Please check the model's license for usage terms.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
```
