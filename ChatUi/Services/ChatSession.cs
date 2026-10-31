
using ChatProtocols;
using ChatServer;

public class ChatSession : IChatSession
{
    public IChatClient? _client;
    public IChatServer? _server;
    public IChatClientFactory chatClientFactory;
    public IChatServerFactory chatServerFactory;
    public event Action<ProtocolMessage>? MessageReceived;
    public event Action<ProtocolMessage>? MessageProcessError;
    public event Action<string>? NetworkError;

    public ChatSession(IChatClientFactory Client, IChatServerFactory Server)
    {
        chatClientFactory = Client;
        chatServerFactory = Server;
    }
    public async Task ConnectAsync(string host)
    {
        if (_client is not null)
        {
            throw new InvalidOperationException("A client is already connected.");
        }
        var client = chatClientFactory.Create();
        client.MessageReceived += OnClientMessage;
        client.MessageProcessError += OnMessageProcessError;
        client.NetworkError += OnNetworkError;
        try
        {
            await client.connect(host);
           _client = client;
        }
        catch (Exception connectError)
        {
            client.MessageReceived -= OnClientMessage;
            client.MessageProcessError -= OnMessageProcessError;
            client.NetworkError -= OnNetworkError;

            try
            {
                await client.DisconnectAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Connecting failed, and client cleanup also failed.",connectError,cleanupError);
            }

            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        
        var Client = _client;
        var Server = _server;
        _client = null;
        _server = null;

        try
        {
            if (Client is not null)
            {
                Client.MessageReceived -= OnClientMessage;
                Client.MessageProcessError -= OnMessageProcessError;
                Client.NetworkError -= OnNetworkError;
                await Client.DisconnectAsync();
            }
        }
        finally
        {
            if (Server is not null)
            {
                await Server.StopAsync();
            } 
        }
    }

    public async Task HostAsync()
    {
        if (_server is not null || _client is not null)
        {
            throw new InvalidOperationException("The chat session is already active.");
        }
        var server = chatServerFactory.Create();
        await server.StartAsync();
        _server = server;
        try
        {
            await ConnectAsync("localhost");
        }
        catch
        {
            await _server.StopAsync();
            _server = null;
            throw;
        }
       
    }

    private void OnClientMessage(ProtocolMessage msg)
    {
        MessageReceived?.Invoke(msg);
    }

    private void OnMessageProcessError(ProtocolMessage msg)
    {
        MessageProcessError?.Invoke(msg);
    }

    private void OnNetworkError(string error)
    {
        NetworkError?.Invoke(error);
    }

    public Task sendChat(string message)
    {
        return ConnectedClient.sendChat(message);
    }

    public Task joinRoom(string room)
    {
        return ConnectedClient.joinRoom(room);
    }

    public Task leaveRoom(string room)
    {
        return ConnectedClient.leaveRoom(room);
    }

    public Task rooms()
    {
        return ConnectedClient.rooms();
    }

    public Task users()
    {
        return ConnectedClient.users();
    }

    public Task requestBoardSnapshot(string room, string? requestId = null)
    {
        return ConnectedClient.requestBoardSnapshot(room, requestId);
    }

    public Task addStroke(string room, BoardStroke stroke, string? requestId = null)
    {
        return ConnectedClient.addStroke(room, stroke, requestId);
    }

    public Task addText(string room, BoardText text, string? requestId = null)
    {
        return ConnectedClient.addText(room, text, requestId);
    }

    public Task editText(string room, EditBoardTextPayload text, string? requestId = null)
    {
        return ConnectedClient.editText(room, text, requestId);
    }

    public Task moveBoardObject(string room, MoveBoardObjectPayload move, string? requestId = null)
    {
        return ConnectedClient.moveBoardObject(room, move, requestId);
    }

    public Task deleteBoardObject(string room, DeleteBoardObjectPayload delete, string? requestId = null)
    {
        return ConnectedClient.deleteBoardObject(room, delete, requestId);
    }

    private IChatClient ConnectedClient =>
        _client ?? throw new InvalidOperationException("The chat session is not connected.");
}