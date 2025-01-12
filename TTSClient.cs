using SocketIOClient;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using NAudio.Wave;
using System.Threading;

public class TTSClient : IDisposable
{
    private readonly SocketIO socket;
    private readonly ConcurrentQueue<byte[]> audioQueue;
    private readonly WaveOutEvent waveOut;
    private bool isPlaying;
    private CancellationTokenSource cancellationSource;

    public event EventHandler<string> OnConnected;
    public event EventHandler<string> OnDisconnected;
    public event EventHandler<string> OnError;
    public event EventHandler<string> OnVoiceSet;

    public TTSClient(string serverUrl = "http://localhost:8000")
    {
        socket = new SocketIO(serverUrl);
        audioQueue = new ConcurrentQueue<byte[]>();
        waveOut = new WaveOutEvent();
        cancellationSource = new CancellationTokenSource();

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

        socket.On("error", response =>
        {
            var message = response.GetValue<ErrorResponse>().Message;
            OnError?.Invoke(this, message);
        });

        socket.On("voice_set", response =>
        {
            var voice = response.GetValue<VoiceResponse>().Voice;
            OnVoiceSet?.Invoke(this, $"Voice set to: {voice}");
        });

        socket.On("audio_chunk", response =>
        {
            var audioData = Convert.FromBase64String(response.GetValue<AudioChunkResponse>().Audio);
            audioQueue.Enqueue(audioData);
            ProcessAudioQueue();
        });
    }

    public async Task ConnectAsync()
    {
        try
        {
            await socket.ConnectAsync();
            isPlaying = true;
            _ = StartAudioProcessing();
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Connection error: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        isPlaying = false;
        cancellationSource.Cancel();
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

    private async Task StartAudioProcessing()
    {
        while (isPlaying && !cancellationSource.Token.IsCancellationRequested)
        {
            await ProcessAudioQueue();
            await Task.Delay(100);
        }
    }

    private async Task ProcessAudioQueue()
    {
        if (audioQueue.TryDequeue(out byte[] audioData))
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

    public void Dispose()
    {
        waveOut?.Dispose();
        cancellationSource?.Dispose();
        socket?.Dispose();
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