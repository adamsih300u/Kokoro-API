from flask import Flask, request, send_file, jsonify
from flask_socketio import SocketIO, emit
from models import build_model
import torch
from kokoro import generate
import io
import scipy.io.wavfile
import os
import logging
import time
import queue
import threading
import numpy as np
import base64
import struct
from concurrent.futures import ThreadPoolExecutor
from functools import lru_cache

# Set up logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = Flask(__name__)
socketio = SocketIO(app, cors_allowed_origins="*")

# Initialize model on startup
device = 'cpu'
MODEL = build_model('kokoro-v0_19.pth', device)
# Disable gradient computation for inference
torch.set_grad_enabled(False)

# Load available voices
VOICES = {}
VOICES_DIR = 'voices'
# Pre-process voices for faster loading
voice_cache = {}
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

# Keep track of active sessions
active_sessions = {}

# Add to app initialization
MAX_WORKERS = min(os.cpu_count(), 4)  # Limit based on CPU cores
thread_pool = ThreadPoolExecutor(max_workers=MAX_WORKERS)

# Add at the top with other constants
MAX_TEXT_LENGTH = 500
OPTIMAL_TEXT_LENGTH = 200
CHUNK_SIZE = 150  # Characters per chunk
MAX_PROCESSING_TIME = 240  # Maximum processing time in seconds

@socketio.on('connect')
def handle_connect():
    """Handle new WebSocket connections"""
    session_id = request.sid
    active_sessions[session_id] = {
        'voice': DEFAULT_VOICE,
        'queue': queue.Queue(),
        'processing': False
    }
    logger.info(f"New connection: {session_id}")
    # Send test message to verify connection
    socketio.emit('connection_test', {'status': 'connected'}, room=session_id)

@socketio.on('disconnect')
def handle_disconnect():
    """Clean up when client disconnects"""
    session_id = request.sid
    if session_id in active_sessions:
        del active_sessions[session_id]
    logger.info(f"Client disconnected: {session_id}")

@socketio.on('set_voice')
def handle_set_voice(data):
    """Set voice for the session"""
    voice = data.get('voice', DEFAULT_VOICE)
    if voice not in VOICES:
        emit('error', {'message': f'Voice not found. Available voices: {list(VOICES.keys())}'})
        return
    
    active_sessions[request.sid]['voice'] = voice
    emit('voice_set', {'voice': voice})

@socketio.on('tts')
def handle_tts(data):
    """Handle incoming text for TTS conversion"""
    session_id = request.sid
    session = active_sessions.get(session_id)
    if not session:
        emit('error', {'message': 'Invalid session'})
        return

    text = data.get('text', '').strip()
    if not text:
        return

    # Add text to session queue
    session['queue'].put(text)
    
    # Start processing if not already running
    if not session['processing']:
        session['processing'] = True
        threading.Thread(target=process_queue, args=(session_id,)).start()

def process_queue(session_id):
    """Process queued text for a session"""
    session = active_sessions.get(session_id)
    if not session:
        return

    start_time = time.time()
    while True:
        try:
            # Get next text from queue
            try:
                text = session['queue'].get_nowait()
            except queue.Empty:
                session['processing'] = False
                break

            # Process text
            # Combine shorter sentences to reduce overhead
            sentences = [s.strip() for s in text.split('.') if s.strip()]
            batched_sentences = []
            current_batch = ""
            for sentence in sentences:
                if len(current_batch) + len(sentence) < 150:  # Optimal batch size
                    current_batch += sentence + ". "
                else:
                    if current_batch:
                        batched_sentences.append(current_batch)
                    current_batch = sentence + ". "
            if current_batch:
                batched_sentences.append(current_batch)

            for batch in batched_sentences:
                if session_id not in active_sessions:
                    return  # Session ended

                batch = batch.strip() + '.'
                logger.info(f"Processing sentence for {session_id}: {batch}")

                try:
                    # Generate audio
                    audio, _ = generate(
                        MODEL,
                        batch,
                        VOICES[session['voice']],
                        lang=session['voice'][0]
                    )

                    # Convert to WAV
                    buffer = io.BytesIO()
                    # Vectorized operation for faster conversion
                    audio_array = (audio * 32767).astype(np.int16)
                    scipy.io.wavfile.write(buffer, 22050, audio_array)
                    buffer.seek(0)

                    # Verify WAV header before sending
                    wav_data = buffer.getvalue()
                    if len(wav_data) < 44:  # WAV header is 44 bytes
                        raise Exception("WAV data too short")
                        
                    # Check RIFF header
                    riff_header = wav_data[:4]
                    if riff_header != b'RIFF':
                        logger.error(f"Invalid RIFF header: {riff_header}")
                        raise Exception("Invalid WAV format")
                        
                    # Check WAVE format
                    wave_format = wav_data[8:12]
                    if wave_format != b'WAVE':
                        logger.error(f"Invalid WAVE format: {wave_format}")
                        raise Exception("Invalid WAV format")
                        
                    logger.info(f"WAV header validation passed. File size: {len(wav_data)} bytes")

                    # Send audio chunk to client
                    # Convert binary data to base64 string for safe transmission
                    audio_base64 = base64.b64encode(wav_data).decode('utf-8')
                    logger.info(f"Sending audio chunk to client {session_id}, base64 length: {len(audio_base64)}")
                    socketio.emit('audio_chunk', {
                        'audio': audio_base64,
                        'text': batch
                    }, room=session_id, callback=lambda: logger.info(f"Client {session_id} acknowledged audio chunk"))
                    logger.info(f"Audio chunk sent to client {session_id}")

                except Exception as e:
                    logger.error(f"Error generating audio: {str(e)}")
                    socketio.emit('error', {
                        'message': f'Error generating audio: {str(e)}'
                    }, room=session_id)

        except Exception as e:
            logger.error(f"Queue processing error: {str(e)}")
            session['processing'] = False
            break

    logger.info(f"Processing completed in {time.time() - start_time:.2f} seconds")

# Keep the REST endpoints for compatibility
@app.route('/voices', methods=['GET'])
def list_voices():
    """Get a list of available voices"""
    return jsonify({
        'voices': list(VOICES.keys()),
        'default_voice': DEFAULT_VOICE
    })

@app.route('/tts', methods=['POST'])
def text_to_speech():
    try:
        data = request.get_json()
        text = data.get('text')
        
        # Split long text into chunks
        if len(text) > CHUNK_SIZE:
            chunks = [text[i:i+CHUNK_SIZE] for i in range(0, len(text), CHUNK_SIZE)]
        else:
            chunks = [text]
        
        # Process each chunk
        audio_chunks = []
        for chunk in chunks:
            # Generate audio with selected voice
            audio, _ = generate(
                MODEL, 
                chunk,
                VOICES[voice_name], 
                lang=voice_name[0]
            )
            audio_chunks.append(audio)
        
        # Combine audio chunks
        audio = np.concatenate(audio_chunks)
        
        # Add length warning in response headers
        headers = {}
        if len(text) > OPTIMAL_TEXT_LENGTH:
            headers['X-Text-Length-Warning'] = 'Text exceeds optimal length of 200 characters'
        
        if len(text) > MAX_TEXT_LENGTH:
            return {'error': f'Text exceeds maximum length of {MAX_TEXT_LENGTH} characters'}, 400

        voice_name = data.get('voice', DEFAULT_VOICE)

        if not text:
            return {'error': 'No text provided'}, 400

        if voice_name not in VOICES:
            return {
                'error': f'Voice not found. Available voices: {list(VOICES.keys())}'
            }, 400

        logger.info(f"Generating audio for text: '{text}' using voice: {voice_name}")

        # Check if audio was generated successfully
        if audio is None or len(audio) == 0:
            logger.error("Audio generation failed - empty output")
            return {'error': 'Failed to generate audio'}, 500

        logger.info(f"Generated audio length: {len(audio)}")
        
        # Convert to WAV format
        buffer = io.BytesIO()
        scipy.io.wavfile.write(buffer, 22050, audio)
        buffer.seek(0)
        
        # Check file size
        file_size = buffer.getbuffer().nbytes
        logger.info(f"Generated WAV file size: {file_size} bytes")
        
        if file_size < 1024:  # If less than 1KB
            logger.error("Generated file too small")
            return {'error': 'Generated audio file too small'}, 500
        
        return send_file(
            buffer,
            mimetype='audio/wav',
            as_attachment=True,
            download_name='speech.wav'
        )

    except Exception as e:
        logger.error(f"Error generating audio: {str(e)}", exc_info=True)
        return {'error': str(e)}, 500

@lru_cache(maxsize=1000)
def generate_cached(text, voice_name):
    """Cache frequently used text-voice combinations"""
    voice = VOICES[voice_name]
    return generate(MODEL, text, voice, lang=voice_name[0])

if __name__ == '__main__':
    socketio.run(app, host='0.0.0.0', port=8000, allow_unsafe_werkzeug=True)  # For development 