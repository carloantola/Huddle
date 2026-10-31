using System.Net.Sockets;
namespace ChatServer;
public class ClientConnection
{
    private const string Base36Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private static long nextDisplayId;

    public Guid Id { get; set; }
    public string DisplayId { get; }

    public TcpClient Client { get; set; }

    public string RoomId { get; set; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public ClientConnection(TcpClient client, string roomid)
    {
        Id = Guid.NewGuid();
        var displayId = Interlocked.Increment(ref nextDisplayId);
        DisplayId = $"User-{FormatBase36(displayId).PadLeft(4, '0')}";
        Client = client;
        RoomId = roomid;
    }

    private static string FormatBase36(long value)
    {
        Span<char> buffer = stackalloc char[13];
        var position = buffer.Length;

        do
        {
            buffer[--position] = Base36Digits[(int)(value % 36)];
            value /= 36;
        }
        while (value > 0);

        return new string(buffer[position..]);
    }
}