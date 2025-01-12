# Kokoro TTS API Server

A Flask-based API server that provides Text-to-Speech capabilities using the Kokoro TTS model. Supports both REST API and WebSocket connections for real-time streaming audio generation.

## Features

- Multiple voice support
- Real-time audio streaming via WebSocket
- REST API endpoints for simple TTS conversion
- CPU-optimized inference
- Docker containerization
- Sentence batching for optimal performance

## Quick Start

### Using Docker

```bash
# Build the container
docker build -t kokoro-tts .

# Run the container
docker run -p 8000:8000 kokoro-tts
```

### Manual Installation

```bash
# Install dependencies
pip install -r requirements.txt

# Run the server
python app.py
```

## API Reference

### REST API Endpoints

1. **List Available Voices**
```http
GET /voices
```
Response:
```json
{
    "voices": ["af", "bella", "sarah"],
    "default_voice": "af"
}
```

2. **Text to Speech**
```http
POST /tts
Content-Type: application/json

{
    "text": "Your text here",
    "voice": "af"  // Optional, defaults to "af"
}
```
Response: WAV audio file

### WebSocket API

The WebSocket API provides real-time streaming capabilities for continuous text-to-speech conversion.

#### Events

1. **Connection**
   - Client connects to `ws://localhost:8000`
   - Server emits 'connect' event on successful connection

2. **Set Voice**
   ```javascript
   // Emit 'set_voice' event
   {
       "voice": "af"  // or any available voice
   }
   ```

3. **Send Text**
   ```javascript
   // Emit 'tts' event
   {
       "text": "Your text to convert to speech"
   }
   ```

4. **Receive Audio**
   ```javascript
   // Listen for 'audio_chunk' events
   {
       "audio": "base64_encoded_wav_data",
       "text": "The text that was processed"
   }
   ```

## Code Examples

### Python REST Client

```python
import requests

def get_voices():
    response = requests.get('http://localhost:8000/voices')
    return response.json()

def text_to_speech(text, voice='af'):
    response = requests.post(
        'http://localhost:8000/tts',
        json={'text': text, 'voice': voice}
    )
    if response.status_code == 200:
        with open('output.wav', 'wb') as f:
            f.write(response.content)
    else:
        print(f"Error: {response.json()}")

# Example usage
voices = get_voices()
print(f"Available voices: {voices['voices']}")
text_to_speech("Hello, this is a test.", "af")
```

### Python WebSocket Client

```python
import socketio
import base64
import time

class TTSClient:
    def __init__(self, server_url='http://localhost:8000'):
        self.sio = socketio.Client()
        self.setup_handlers()
        self.sio.connect(server_url)

    def setup_handlers(self):
        @self.sio.on('connect')
        def on_connect():
            print("Connected to TTS server")

        @self.sio.on('audio_chunk')
        def on_audio_chunk(data):
            audio_data = base64.b64decode(data['audio'])
            filename = f"output_{int(time.time())}.wav"
            with open(filename, 'wb') as f:
                f.write(audio_data)
            print(f"Saved audio chunk: {filename}")

    def set_voice(self, voice):
        self.sio.emit('set_voice', {'voice': voice})

    def speak(self, text):
        self.sio.emit('tts', {'text': text})

    def disconnect(self):
        self.sio.disconnect()

# Example usage
client = TTSClient()
client.set_voice('af')
client.speak("This is a test of the streaming TTS system.")
time.sleep(5)  # Wait for audio processing
client.disconnect()
```

## Configuration

### Environment Variables

```bash
CUDA_VISIBLE_DEVICES=""  # Force CPU usage
FORCE_CPU=1             # Ensure CPU inference
TORCH_DEVICE="cpu"      # Set PyTorch device
```

### Docker Configuration

The included Dockerfile sets up a CPU-optimized environment for running the TTS server.

```bash
# Build the image
docker build -t kokoro-tts .

# Run with specific port
docker run -p 8000:8000 kokoro-tts

# Run with resource limits
docker run -p 8000:8000 --memory=2g kokoro-tts
```

## Performance Optimization

- Optimal text length: 100-200 characters per request
- Maximum text length: 500 characters
- Long text is automatically chunked into smaller segments
- Server timeout is set to 5 minutes for long processing jobs
- Sentences are automatically batched for optimal processing
- Uses CPU optimization and threading for better performance
- Includes caching for frequently used phrases

## Dependencies

Core dependencies (see requirements.txt for full list):
- Flask
- Flask-SocketIO
- PyTorch (CPU version)
- NumPy
- SciPy
- Transformers

## Troubleshooting

Common issues and solutions:

1. **WAV Header Issues**
   - Ensure client properly handles base64 decoded audio data
   - Check WAV format compatibility in client audio player

2. **Performance Issues**
   - Reduce batch size if memory usage is high
   - Increase number of workers if CPU usage is low
   - Monitor system resources during operation

3. **Connection Issues**
   - Verify WebSocket connection URL
   - Check firewall settings
   - Ensure proper CORS configuration

## License

This project uses the Kokoro TTS model. Please check the model's license for usage terms.
```
