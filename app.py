from flask import Flask, request, send_file, jsonify
from flask_sock import Sock
from models import build_model
import torch
from kokoro import generate
import io
import scipy.io.wavfile
import os
import logging
import time
import threading
import numpy as np
import base64
import json
from simple_websocket import ConnectionClosed

# Set up logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = Flask(__name__)
sock = Sock(app)

# Initialize model on startup
device = 'cpu'
MODEL = build_model('kokoro-v0_19.pth', device)
# Disable gradient computation for inference
torch.set_grad_enabled(False)

# Load available voices
VOICES = {}
VOICES_DIR = 'voices'
for voice_file in os.listdir(VOICES_DIR):
    if voice_file.endswith('.pt'):
        voice_name = voice_file[:-3]
        VOICES[voice_name] = torch.load(
            os.path.join(VOICES_DIR, voice_file), 
            weights_only=True
        ).to(device)
        logger.info(f"Loaded voice: {voice_name}")

# Default voice
DEFAULT_VOICE = 'af'

# Add at the top with other constants
MAX_TEXT_LENGTH = 500
OPTIMAL_TEXT_LENGTH = 200
CHUNK_SIZE = 8192  # Reduced from 16384 to 8192 bytes
MAX_PROCESSING_TIME = 240  # Maximum processing time in seconds
CHUNK_DELAY = 0.02  # Reduced from 0.05 to 0.02 seconds

# Add new constant
MESSAGE_TYPES = {
    'AUDIO_CHUNK': 'audio_chunk',
    'PROCESSING': 'processing',
    'ERROR': 'error'
}

# WebSocket endpoint
@sock.route('/ws')
def ws_endpoint(ws):
    """Handle WebSocket connections"""
    logger.info("New WebSocket connection established")
    current_voice = DEFAULT_VOICE
    
    try:
        # Send initial connection confirmation
        ws.send(json.dumps({
            "status": "connected",
            "voices": list(VOICES.keys()),
            "current_voice": current_voice
        }))
        
        while True:
            try:
                data = json.loads(ws.receive())
                if data is None:  # Connection closed
                    logger.info("Client disconnected (received None)")
                    break
                    
                logger.info(f"Received message: {json.dumps(data)}")
                command = data.get('command')
                if command == 'ping':  # Add heartbeat support
                    ws.send(json.dumps({'status': 'pong'}))
                    continue
                    
                if command == 'set_voice':
                    voice = data.get('voice', DEFAULT_VOICE)
                    if voice not in VOICES:
                        ws.send(json.dumps({
                            'error': f'Voice not found. Available voices: {list(VOICES.keys())}'
                        }))
                        continue
                    current_voice = voice
                    ws.send(json.dumps({
                        'status': 'voice_set',
                        'voice': voice
                    }))
                
                elif command == 'tts':
                    text = data.get('text', '').strip()
                    # Remove any special tags
                    text = text.replace('#fiction\r\n', '')
                    text = text.replace('#style\r\n', '')
                    text = text.replace('\r\n', ' ')
                    # Validate text length
                    if len(text) > MAX_TEXT_LENGTH:
                        ws.send(json.dumps({
                            'error': f'Text too long ({len(text)} chars). Maximum is {MAX_TEXT_LENGTH} characters.'
                        }))
                        continue
                    if not text:
                        continue
                        
                    logger.info(f"Processing TTS request: {text[:100]}...")
                    message_id = int(time.time() * 1000)  # Unique ID for this audio message
                    
                    # Send processing status
                    ws.send(json.dumps({
                        'type': MESSAGE_TYPES['PROCESSING'],
                        'message_id': message_id,
                        'text': text[:100] + '...' if len(text) > 100 else text
                    }))
                    
                    # Process TTS request
                    audio, _ = generate(MODEL, text, VOICES[current_voice], lang=current_voice[0])
                    
                    # Convert to WAV
                    buffer = io.BytesIO()
                    audio_array = (audio * 32767).astype(np.int16)
                    scipy.io.wavfile.write(buffer, 22050, audio_array)
                    buffer.seek(0)
                    
                    logger.info(f"Audio generated for message {message_id}, preparing to send...")
                    audio_base64 = base64.b64encode(buffer.getvalue()).decode('utf-8')
                    logger.info(f"Audio data size (base64): {len(audio_base64)} bytes")
                    
                    chunks = [audio_base64[i:i+CHUNK_SIZE] for i in range(0, len(audio_base64), CHUNK_SIZE)]
                    total_chunks = len(chunks)
                    logger.info(f"Splitting audio into {total_chunks} chunks")
                    
                    # Send all chunks except the last one
                    for i, chunk in enumerate(chunks[:-1]):
                        ws.send(json.dumps({
                            'type': MESSAGE_TYPES['AUDIO_CHUNK'],
                            'message_id': message_id,
                            'audio_chunk': chunk,
                            'is_final': False,
                            'chunk_index': i,
                            'total_chunks': total_chunks,
                            'text': text
                        }))
                        logger.debug(f"Sent chunk {i+1}/{total_chunks} for message {message_id}")
                        time.sleep(CHUNK_DELAY)
                    
                    # Send the final chunk
                    ws.send(json.dumps({
                        'type': MESSAGE_TYPES['AUDIO_CHUNK'],
                        'message_id': message_id,
                        'audio_chunk': chunks[-1],
                        'is_final': True,
                        'chunk_index': len(chunks) - 1,
                        'total_chunks': total_chunks,
                        'text': text
                    }))
                    
                    logger.info(f"Audio message {message_id} sent successfully ({total_chunks} chunks)")
                
                elif command == 'close':  # Handle graceful closure
                    logger.info("Client requested closure")
                    break
                
            except Exception as e:
                logger.error(f"WebSocket error: {str(e)}")
                if isinstance(e, (ConnectionClosed, ConnectionError)):
                    logger.info(f"WebSocket connection closed by client: {str(e)}")
                    break
                else:
                    try:
                        ws.send(json.dumps({'error': str(e)}))
                    except:
                        logger.info("Could not send error - connection closed")
                        break
            
    except (ConnectionClosed, ConnectionError):
        logger.info("WebSocket connection closed")
    except Exception as e:
        logger.error(f"Unexpected error: {str(e)}")
    finally:
        logger.info("WebSocket connection terminated")

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=8000)  # For development 