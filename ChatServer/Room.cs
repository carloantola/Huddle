namespace ChatServer;
public class Room
{
    public string RoomId { get; set; }
    public List<Guid> clients { get; set; }

    public Room(string id, List<Guid> Clients)
    {
        RoomId = id;
        clients = Clients;
    }
}
