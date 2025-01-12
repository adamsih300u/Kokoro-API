import socketio
import time
import wave
import pyaudio
import base64
import io
import threading
import queue

class TTSClient:
    def __init__(self, server_url='http://localhost:8000'):
        self.sio = socketio.Client()
        self.server_url = server_url
        self.audio_queue = queue.Queue()
        self.is_playing = False
        
        # Set up audio playback
        self.p = pyaudio.PyAudio()
        
        # Set up socketio event handlers
        self.sio.on('connect', self.on_connect)
        self.sio.on('disconnect', self.on_disconnect)
        self.sio.on('audio_chunk', self.on_audio_chunk)
        self.sio.on('error', self.on_error)
        self.sio.on('voice_set', self.on_voice_set)

    def connect(self):
        """Connect to the TTS server"""
        try:
            self.sio.connect(self.server_url)
            # Start audio playback thread
            threading.Thread(target=self.audio_player_thread, daemon=True).start()
        except Exception as e:
            print(f"Connection error: {e}")

    def disconnect(self):
        """Disconnect from the TTS server"""
        self.is_playing = False
        self.sio.disconnect()
        self.p.terminate()

    def set_voice(self, voice):
        """Set the voice to use"""
        self.sio.emit('set_voice', {'voice': voice})

    def speak(self, text):
        """Send text to be converted to speech"""
        self.sio.emit('tts', {'text': text})

    def on_connect(self):
        print("Connected to TTS server")

    def on_disconnect(self):
        print("Disconnected from TTS server")

    def on_audio_chunk(self, data):
        """Handle incoming audio chunks"""
        try:
            # Convert base64 audio data to WAV
            audio_data = base64.b64decode(data['audio'])
            self.audio_queue.put(audio_data)
        except Exception as e:
            print(f"Error processing audio chunk: {e}")

    def on_error(self, data):
        print(f"Server error: {data['message']}")

    def on_voice_set(self, data):
        print(f"Voice set to: {data['voice']}")

    def audio_player_thread(self):
        """Thread to handle audio playback"""
        self.is_playing = True
        while self.is_playing:
            try:
                # Get audio data from queue
                audio_data = self.audio_queue.get(timeout=1)
                
                # Create WAV file in memory
                with io.BytesIO(audio_data) as wav_buffer:
                    with wave.open(wav_buffer, 'rb') as wf:
                        # Open stream
                        stream = self.p.open(
                            format=self.p.get_format_from_width(wf.getsampwidth()),
                            channels=wf.getnchannels(),
                            rate=wf.getframerate(),
                            output=True
                        )

                        # Read and play audio data
                        chunk_size = 1024
                        data = wf.readframes(chunk_size)
                        while data and self.is_playing:
                            stream.write(data)
                            data = wf.readframes(chunk_size)

                        stream.stop_stream()
                        stream.close()

            except queue.Empty:
                continue
            except Exception as e:
                print(f"Audio playback error: {e}")

# Example usage
if __name__ == "__main__":
    client = TTSClient()
    client.connect()

    try:
        # Set voice (optional)
        client.set_voice('af')

        # Send some text
        client.speak("Hello! This is a test of the streaming TTS system.")
        time.sleep(2)
        client.speak("Here's another sentence that will be queued up.")
        
        # Keep the main thread running
        while True:
            time.sleep(1)

    except KeyboardInterrupt:
        client.disconnect() 