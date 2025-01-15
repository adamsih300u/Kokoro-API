using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

public class AudioMessage
{
    public string MessageId { get; set; }
    public Dictionary<int, string> Chunks { get; } = new Dictionary<int, string>();
    public int TotalChunks { get; set; }
    public string Text { get; set; }
    
    public bool IsComplete => Chunks.Count == TotalChunks;
    
    public string GetCompleteAudio()
    {
        if (!IsComplete) return null;
        
        var orderedChunks = Chunks.OrderBy(x => x.Key)
                                 .Select(x => x.Value);
        return string.Concat(orderedChunks);
    }
}

public class TTSClient : IDisposable
{
    private readonly Uri serverUri;
    private ClientWebSocket webSocket;
    private readonly CancellationTokenSource cts;
    private const int MaxTextLength = 500;
    private const int OptimalChunkSize = 200;
    private readonly Dictionary<string, AudioMessage> _pendingMessages = new Dictionary<string, AudioMessage>();

    public event EventHandler<string> OnConnected;
    public event EventHandler<string> OnDisconnected;
    public event EventHandler<string> OnError;
    public event EventHandler<string> OnVoiceSet;
    public event EventHandler<string> OnStatus;
    public event EventHandler<(byte[] audio, string text)> OnAudioReceived;

    public TTSClient(string serverUrl = "ws://localhost:8000/ws")
    {
        serverUri = new Uri(serverUrl);
        webSocket = new ClientWebSocket();
        cts = new CancellationTokenSource();
    }

    public async Task ConnectAsync()
    {
        try
        {
            if (webSocket.State != WebSocketState.None)
            {
                await DisconnectAsync();
                webSocket = new ClientWebSocket();
            }

            await webSocket.ConnectAsync(serverUri, cts.Token);
            OnConnected?.Invoke(this, "Connected to TTS server");
            _ = ReceiveLoop();
            _ = MaintainConnection();
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Connection error: {ex.Message}");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await SendMessageAsync(JsonSerializer.Serialize(new { command = "close" }));
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cts.Token);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Shutdown error: {ex.Message}");
        }
        finally
        {
            cts.Cancel();
        }
    }

    public async Task SetVoiceAsync(string voice)
    {
        await SendMessageAsync(JsonSerializer.Serialize(new { command = "set_voice", voice }));
    }

    public async Task SpeakAsync(string text)
    {
        // Clean up text
        text = text.Replace("\r\n", " ").Trim();

        // Split text into chunks if needed
        var chunks = SplitTextIntoChunks(text);

        foreach (var chunk in chunks)
        {
            try
            {
                var message = JsonSerializer.Serialize(new { command = "tts", text = chunk });
                await SendMessageAsync(message);

                // Wait for processing to complete before sending next chunk
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);
                
                // Wait for response before continuing
                await WaitForAudioResponse(linkedCts.Token);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Error processing chunk: {ex.Message}");
                throw;
            }
        }
    }

    private List<string> SplitTextIntoChunks(string text)
    {
        var chunks = new List<string>();
        if (text.Length <= MaxTextLength)
        {
            chunks.Add(text);
            return chunks;
        }

        // Split on sentence boundaries
        var sentences = text.Split(new[] { ". ", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries);
        var currentChunk = new StringBuilder();

        foreach (var sentence in sentences)
        {
            var potentialChunk = currentChunk.Length == 0 ? 
                sentence : 
                currentChunk + ". " + sentence;

            if (potentialChunk.Length > MaxTextLength)
            {
                if (currentChunk.Length > 0)
                {
                    chunks.Add(currentChunk.ToString());
                    currentChunk.Clear();
                }
                // If a single sentence is too long, split it
                if (sentence.Length > MaxTextLength)
                {
                    var words = sentence.Split(' ');
                    currentChunk.Clear();
                    foreach (var word in words)
                    {
                        if (currentChunk.Length + word.Length + 1 > MaxTextLength)
                        {
                            chunks.Add(currentChunk.ToString());
                            currentChunk.Clear();
                        }
                        if (currentChunk.Length > 0) currentChunk.Append(" ");
                        currentChunk.Append(word);
                    }
                }
                else
                {
                    currentChunk.Append(sentence);
                }
            }
            else
            {
                if (currentChunk.Length > 0) currentChunk.Append(". ");
                currentChunk.Append(sentence);
            }

            if (currentChunk.Length >= OptimalChunkSize)
            {
                chunks.Add(currentChunk.ToString());
                currentChunk.Clear();
            }
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString());
        }

        return chunks;
    }

    private TaskCompletionSource<bool> audioResponseReceived;

    private async Task WaitForAudioResponse(CancellationToken token)
    {
        audioResponseReceived = new TaskCompletionSource<bool>();
        using var registration = token.Register(() => audioResponseReceived.TrySetCanceled());
        await audioResponseReceived.Task;
    }

    private async Task SendMessageAsync(string message)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        var buffer = Encoding.UTF8.GetBytes(message);
        await webSocket.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None  // Don't use cancellation token for send
        );
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[16384]; // Larger buffer for audio data
        
        try
        {
            while (webSocket.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
            {
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult result;
                
                do
                {
                    result = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cts.Token
                    );
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            cts.Token
                        );
                        OnDisconnected?.Invoke(this, "Connection closed");
                        return;
                    }
                    
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                ms.Seek(0, System.IO.SeekOrigin.Begin);
                var message = Encoding.UTF8.GetString(ms.ToArray());
                OnStatus?.Invoke(this, $"Received message size: {message.Length} bytes");
                ProcessMessage(message);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Receive error: {ex.Message}");
            audioResponseReceived?.TrySetException(ex);
        }
    }

    private void ProcessMessage(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                var errorMsg = error.GetString();
                OnError?.Invoke(this, errorMsg);
                audioResponseReceived?.TrySetException(new Exception(errorMsg));
                return;
            }

            if (root.TryGetProperty("status", out var status))
            {
                var statusStr = status.GetString();
                if (statusStr == "voice_set")
                {
                    var voice = root.GetProperty("voice").GetString();
                    OnVoiceSet?.Invoke(this, $"Voice set to: {voice}");
                }
                else if (statusStr == "processing")
                {
                    var text = root.GetProperty("text").GetString();
                    OnStatus?.Invoke(this, $"Processing: {text}");
                }
                else if (statusStr == "pong")
                {
                    // Heartbeat response received
                }
                return;
            }

            if (root.TryGetProperty("audio", out var audio))
            {
                var base64Data = audio.GetString();
                OnStatus?.Invoke(this, $"Received audio data (base64): {base64Data.Length} bytes");
                var audioData = Convert.FromBase64String(base64Data);
                OnStatus?.Invoke(this, $"Decoded audio size: {audioData.Length} bytes");
                var text = root.GetProperty("text").GetString();
                OnAudioReceived?.Invoke(this, (audioData, text));
                audioResponseReceived?.TrySetResult(true);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Error processing message: {ex.Message}");
            audioResponseReceived?.TrySetException(ex);
        }
    }

    private DateTime lastPing = DateTime.MinValue;
    private readonly TimeSpan pingInterval = TimeSpan.FromSeconds(30);

    private async Task MaintainConnection()
    {
        while (!cts.Token.IsCancellationRequested)
        {
            if (webSocket.State == WebSocketState.Open &&
                DateTime.UtcNow - lastPing > pingInterval)
            {
                try
                {
                    await SendMessageAsync(JsonSerializer.Serialize(new { command = "ping" }));
                    lastPing = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(this, $"Ping error: {ex.Message}");
                }
            }
            await Task.Delay(5000, cts.Token);
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        webSocket.Dispose();
        cts.Dispose();
    }

    public bool IsConnected => webSocket.State == WebSocketState.Open;

    private async Task HandleWebSocketMessage(string message)
    {
        var data = JsonDocument.Parse(message);
        var root = data.RootElement;
        
        string messageType = root.GetProperty("type").GetString();
        
        switch (messageType)
        {
            case "audio_chunk":
                await HandleAudioChunk(root);
                break;
            case "processing":
                // Handle processing status if needed
                break;
            case "error":
                // Handle error
                break;
        }
    }
    
    private async Task HandleAudioChunk(JsonElement data)
    {
        string messageId = data.GetProperty("message_id").GetString();
        string chunk = data.GetProperty("audio_chunk").GetString();
        int chunkIndex = data.GetProperty("chunk_index").GetInt32();
        int totalChunks = data.GetProperty("total_chunks").GetInt32();
        bool isFinal = data.GetProperty("is_final").GetBoolean();
        string text = data.GetProperty("text").GetString();
        
        // Get or create message buffer
        if (!_pendingMessages.TryGetValue(messageId, out var audioMessage))
        {
            audioMessage = new AudioMessage 
            { 
                MessageId = messageId,
                TotalChunks = totalChunks,
                Text = text
            };
            _pendingMessages[messageId] = audioMessage;
        }
        
        // Add chunk to buffer
        audioMessage.Chunks[chunkIndex] = chunk;
        
        // If message is complete, process it
        if (audioMessage.IsComplete)
        {
            string completeAudio = audioMessage.GetCompleteAudio();
            byte[] audioData = Convert.FromBase64String(completeAudio);
            
            // Process the complete audio data
            await ProcessAudioData(audioData, text);
            
            // Clean up
            _pendingMessages.Remove(messageId);
        }
    }
    
    private async Task ProcessAudioData(byte[] audioData, string text)
    {
        // Your existing audio processing code here
        // This is where you'd play the audio or handle it as needed
    }
} 