using ChatProtocols;

public interface IChatClient
{
    Task connect(string serverHost);

    Task sendChat(string message);

    Task joinRoom(string room);

    Task leaveRoom(string room);

    Task rooms();

    Task users();

    Task requestBoardSnapshot(string room, string? requestId = null);

    Task addStroke(string room, BoardStroke stroke, string? requestId = null);

    Task addText(string room, BoardText text, string? requestId = null);

    Task editText(string room, EditBoardTextPayload text, string? requestId = null);

    Task moveBoardObject(string room, MoveBoardObjectPayload move, string? requestId = null);

    Task deleteBoardObject(string room, DeleteBoardObjectPayload delete, string? requestId = null);

    event Action<ProtocolMessage>? MessageReceived;

    event Action<ProtocolMessage>? MessageProcessError;

    event Action<string>? NetworkError;

    Task DisconnectAsync();

}