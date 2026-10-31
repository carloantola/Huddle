using System.Net.Sockets;

public class ClientConnection
{

    public Guid Id { get; set; }

    public TcpClient Client { get; set; }

    public string RoomId { get; set; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public ClientConnection(TcpClient client, string roomid)
    {
        Id = Guid.NewGuid();
        Client = client;
        RoomId = roomid;
    }
}