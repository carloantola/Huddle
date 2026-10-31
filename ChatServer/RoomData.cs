namespace ChatServer;
using ChatProtocols;

public class RoomData
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public string roomId { get; set; }
    public Dictionary<string, BoardStroke> _strokes { get; set; } = new Dictionary<string, BoardStroke>();
    public Dictionary<string, BoardText> _texts { get; set; } = new Dictionary<string, BoardText>();
    public int BoardUpdateSequence { get; set; }

    public RoomData(Dictionary<string, BoardStroke> strokes,Dictionary<string, BoardText> texts,string room)
    {
        _strokes = strokes;
        _texts = texts;
        BoardUpdateSequence = 0;
        roomId = room;
    }
}