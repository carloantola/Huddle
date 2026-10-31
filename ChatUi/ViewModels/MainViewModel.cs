using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ChatProtocols;
using ChatSessionContract = global::IChatSession;

namespace ChatUi.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<string, ObservableCollection<string>> roomMessageHistory =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ChatSessionContract chatSession;
    private string status = "Disconnected";
    private string currentRoom = "lobby";
    private string serverHost = "localhost";
    private string draftMessage = "";
    private string? selectedRoom;
    private bool isConnected;
    private bool isStarting;
    private ObservableCollection<string> messages = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Messages
    {
        get => messages;
        private set => SetProperty(ref messages, value);
    }

    public ObservableCollection<string> Rooms { get; } = new();
    public ObservableCollection<string> Users { get; } = new();
    public BoardViewModel Board { get; } = new();

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public string CurrentRoom
    {
        get => currentRoom;
        private set => SetProperty(ref currentRoom, value);
    }

    public string ServerHost
    {
        get => serverHost;
        set => SetProperty(ref serverHost, value);
    }

    public string DraftMessage
    {
        get => draftMessage;
        set => SetProperty(ref draftMessage, value);
    }

    public string? SelectedRoom
    {
        get => selectedRoom;
        set => SetProperty(ref selectedRoom, value);
    }

    public bool IsConnected
    {
        get => isConnected;
        private set
        {
            if (SetProperty(ref isConnected, value))
            {
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanHost));
            }
        }
    }

    public bool CanConnect => !IsConnected && !isStarting;
    public bool CanHost => !IsConnected && !isStarting;

    public MainViewModel(Dispatcher dispatcher, ChatSessionContract chatSession)
    {
        this.dispatcher = dispatcher;
        this.chatSession = chatSession;
        chatSession.MessageReceived += OnMessageReceived;
        chatSession.MessageProcessError += OnMessageProcessError;
        chatSession.NetworkError += OnNetworkError;
        roomMessageHistory.Add(currentRoom, Messages);
    }

    public Task ConnectAsync() => StartSessionAsync(host: false);

    public Task HostAsync() => StartSessionAsync(host: true);

    private async Task StartSessionAsync(bool host)
    {
        if (IsConnected || isStarting)
        {
            return;
        }

        isStarting = true;
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanHost));
        Status = host ? "Starting local server..." : "Connecting to local server...";

        try
        {
            if (host)
            {
                await chatSession.HostAsync();
            }
            else
            {
                await chatSession.ConnectAsync(ServerHost);
            }

            IsConnected = true;
            SwitchCurrentRoom("lobby");
            Board.SetSession(chatSession);
            await Board.SwitchRoomAsync("lobby");
            Status = host ? "Hosting" : "Connected";
            Messages.Add($"{(host ? "Hosting server" : "Connected")}. You are in lobby.");
            await chatSession.rooms();
            await chatSession.users();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = host ? "Hosting failed" : "Connection failed";
            Messages.Add($"Connection error: {ex.Message}");
            Board.SetSession(null);
            try
            {
                await chatSession.DisconnectAsync();
            }
            catch (Exception cleanupError)
            {
                Messages.Add($"Disconnect error: {cleanupError.Message}");
            }
        }
        finally
        {
            isStarting = false;
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanHost));
        }
    }

    public async Task DisconnectAsync()
    {
        Board.SetSession(null);
        if (!IsConnected)
        {
            await chatSession.DisconnectAsync();
            Status = "Disconnected";
            return;
        }

        await chatSession.DisconnectAsync();
        IsConnected = false;
        Status = "Disconnected";
        Messages.Add("Disconnected.");
    }

    public async Task SendChatAsync()
    {
        var text = DraftMessage.Trim();
        if (string.IsNullOrWhiteSpace(text) || !IsConnected)
        {
            return;
        }

        DraftMessage = "";
        Messages.Add($"You: {text}");
        await chatSession.sendChat(text);
    }

    public async Task JoinSelectedRoomAsync()
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(SelectedRoom))
        {
            return;
        }

        if (string.Equals(SelectedRoom, CurrentRoom, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await chatSession.joinRoom(SelectedRoom);
        SwitchCurrentRoom(SelectedRoom);
        await Board.SwitchRoomAsync(CurrentRoom);
        Messages.Add($"Switched to {CurrentRoom}.");
        await RefreshUsersAsync();
    }

    public async Task LeaveCurrentRoomAsync()
    {
        if (!IsConnected)
        {
            return;
        }

        await chatSession.leaveRoom(CurrentRoom);
        SwitchCurrentRoom("lobby");
        await Board.SwitchRoomAsync(CurrentRoom);
        Messages.Add("Returned to lobby.");
        await RefreshUsersAsync();
    }

    public async Task RefreshRoomsAsync()
    {
        if (IsConnected)
        {
            await chatSession.rooms();
        }
    }

    public async Task RefreshUsersAsync()
    {
        if (IsConnected)
        {
            await chatSession.users();
        }
    }

    private void OnMessageReceived(ProtocolMessage message)
    {
        _ = dispatcher.InvokeAsync(() => HandleMessage(message));
    }

    private void HandleMessage(ProtocolMessage message)
    {
        switch (message.Type)
        {
            case MessageTypes.Chat:
                var sender = string.IsNullOrWhiteSpace(message.SenderId)
                    ? ""
                    : $"{message.SenderId}: ";
                Messages.Add($"{sender}{message.Text ?? ""}");
                break;
            case MessageTypes.Error:
                Board.HandleError(message);
                Messages.Add($"Server error [{message.ErrorCode}]: {message.ErrorMessage}");
                break;
            case MessageTypes.ListRooms:
                ReplaceItems(Rooms, message.Text);
                break;
            case MessageTypes.ListUsers:
                ReplaceItems(Users, message.Text);
                break;
            case MessageTypes.BoardSnapshot:
            case MessageTypes.BoardStrokeAdd:
            case MessageTypes.BoardTextAdd:
            case MessageTypes.BoardTextEdit:
            case MessageTypes.BoardObjectMove:
            case MessageTypes.BoardObjectDelete:
                Board.HandleMessage(message);
                break;
            default:
                Messages.Add($"Received message type '{message.Type}'.");
                break;
        }
    }

    private void SwitchCurrentRoom(string room)
    {
        if (!roomMessageHistory.TryGetValue(room, out var history))
        {
            history = new ObservableCollection<string>();
            roomMessageHistory.Add(room, history);
        }

        CurrentRoom = room;
        Messages = history;
    }

    private void OnMessageProcessError(ProtocolMessage error)
    {
        _ = dispatcher.InvokeAsync(() =>
        {
            Messages.Add($"Protocol error [{error.ErrorCode}]: {error.ErrorMessage}");
        });
    }

    private void OnNetworkError(string error)
    {
        _ = dispatcher.InvokeAsync(() => HandleNetworkErrorAsync(error)).Task.Unwrap();
    }

    private async Task HandleNetworkErrorAsync(string error)
    {
        Status = "Network error";
        Messages.Add($"Network error: {error}");
        Board.SetSession(null);
        try
        {
            await chatSession.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Messages.Add($"Disconnect error: {ex.Message}");
        }
        finally
        {
            IsConnected = false;
        }
    }

    private static void ReplaceItems(ObservableCollection<string> target, string? content)
    {
        target.Clear();
        foreach (var item in (content ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            target.Add(item.TrimEnd('\r'));
        }
    }

    private bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
