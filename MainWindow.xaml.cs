using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using NAudio.Wave;
using System.Threading;
using SocketIOClient;

public partial class MainWindow : Window
{
    private readonly TTSClient ttsClient;

    public MainWindow()
    {
        InitializeComponent();
        ttsClient = new TTSClient();
        
        ttsClient.OnConnected += (s, msg) => UpdateStatus(msg);
        ttsClient.OnDisconnected += (s, msg) => UpdateStatus(msg);
        ttsClient.OnError += (s, msg) => UpdateStatus($"Error: {msg}");
        ttsClient.OnVoiceSet += (s, msg) => UpdateStatus(msg);

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ttsClient.ConnectAsync();
    }

    private async void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        await ttsClient.DisconnectAsync();
        ttsClient.Dispose();
    }

    private async void VoiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceComboBox.SelectedItem is string voice)
        {
            await ttsClient.SetVoiceAsync(voice);
        }
    }

    private async void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        var text = TextInput.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            await ttsClient.SpeakAsync(text);
        }
    }

    private void UpdateStatus(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
        });
    }
} 