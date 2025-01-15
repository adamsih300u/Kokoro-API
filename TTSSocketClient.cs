using SocketIOClient;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Threading;

public class TTSSocketClient : IDisposable
{
    private readonly SocketIO socket;
    private readonly ConcurrentQueue<byte[]> audioQueue;
    private readonly WaveOutEvent waveOut;
    private bool isPlaying;
    private readonly SemaphoreSlim audioSemaphore;

    public event EventHandler<string> OnConnected;
    public event EventHandler<string> OnDisconnected;
    public event EventHandler<string> OnError;
    public event EventHandler<string> OnVoiceSet;
    public event EventHandler<string> OnTextProcessed;

    public TTSSocketClient(string serverUrl = "http://localhost:8000")
    {
        socket = new SocketIO(serverUrl, new SocketIOOptions
        {
            EIO = 4,
            Transport = SocketIOClient.Transport.TransportProtocol.WebSocket,
            Path = "/socket.io/",
            Reconnection = true,
            ReconnectionAttempts = 3,
            ReconnectionDelay = 2000
        });

        audioQueue = new ConcurrentQueue<byte[]>();
        waveOut = new WaveOutEvent();
        audioSemaphore = new SemaphoreSlim(1, 1);
        isPlaying = true;

        SetupSocketHandlers();
    }

    private void SetupSocketHandlers()
    {
        socket.OnConnected += (sender, args) =>
        {
            OnConnected?.Invoke(this, "Connected to TTS server");
        };

        socket.OnDisconnected += (sender, args) =>
        {
            OnDisconnected?.Invoke(this, "Disconnected from TTS server");
        };

        socket.On("connection_test", (response) =>
        {
            var status = response.GetValue<ConnectionStatus>();
            OnConnected?.Invoke(this, $"Connection test: {status.Status}");
        });

        socket.On("error", response =>
        {
            var error = response.GetValue<ErrorResponse>();
            OnError?.Invoke(this, error.Message);
        });

        socket.On("voice_set", response =>
        {
            var voice = response.GetValue<VoiceResponse>();
            OnVoiceSet?.Invoke(this, $"Voice set to: {voice.Voice}");
        });

        socket.On("audio_chunk", async response =>
        {
            try
            {
                var data = response.GetValue<AudioChunkResponse>();
                OnTextProcessed?.Invoke(this, $"Processing: {data.Text}");

                var audioData = Convert.FromBase64String(data.Audio);
                
                // Verify WAV header
                if (audioData.Length < 44 || // WAV header is 44 bytes
                    audioData[0] != 'R' || audioData[1] != 'I' || 
                    audioData[2] != 'F' || audioData[3] != 'F')
                {
                    OnError?.Invoke(this, "Invalid WAV header");
                    return;
                }

                audioQueue.Enqueue(audioData);
                await ProcessAudioQueue();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Error processing audio: {ex.Message}");
            }
        });
    }

    private async Task ProcessAudioQueue()
    {
        if (!await audioSemaphore.WaitAsync(0)) // Don't wait if already processing
            return;

        try
        {
            while (audioQueue.TryDequeue(out byte[] audioData))
            {
                try
                {
                    using (var ms = new MemoryStream(audioData))
                    using (var reader = new WaveFileReader(ms))
                    {
                        var bufferedWaveProvider = new BufferedWaveProvider(reader.WaveFormat);
                        waveOut.Init(bufferedWaveProvider);

                        var buffer = new byte[1024];
                        int bytesRead;
                        while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            bufferedWaveProvider.AddSamples(buffer, 0, bytesRead);
                        }

                        waveOut.Play();
                        while (waveOut.PlaybackState == PlaybackState.Playing)
                        {
                            await Task.Delay(100);
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(this, $"Audio playback error: {ex.Message}");
                }
            }
        }
        finally
        {
            audioSemaphore.Release();
        }
    }

    public async Task ConnectAsync()
    {
        try
        {
            await socket.ConnectAsync();
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Connection error: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        isPlaying = false;
        await socket.DisconnectAsync();
        waveOut.Stop();
    }

    public async Task SetVoiceAsync(string voice)
    {
        await socket.EmitAsync("set_voice", new { voice });
    }

    public async Task SpeakAsync(string text)
    {
        await socket.EmitAsync("tts", new { text });
    }

    public void Dispose()
    {
        waveOut?.Dispose();
        audioSemaphore?.Dispose();
        socket?.Dispose();
    }

    private class ConnectionStatus
    {
        public string Status { get; set; }
    }

    private class ErrorResponse
    {
        public string Message { get; set; }
    }

    private class VoiceResponse
    {
        public string Voice { get; set; }
    }

    private class AudioChunkResponse
    {
        public string Audio { get; set; }
        public string Text { get; set; }
    }
} 