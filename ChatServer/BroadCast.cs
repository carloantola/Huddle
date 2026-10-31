using ChatProtocols;
namespace ChatServer;
public class BroadCast
{
    public string RoomId { get; }
    public ProtocolMessage Message { get; }

    public BroadCast(string roomId, ProtocolMessage message)
    {
        RoomId = roomId;
        Message = message;
    }

}