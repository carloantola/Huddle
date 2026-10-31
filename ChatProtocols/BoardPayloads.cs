namespace ChatProtocols;

public sealed class BoardPoint
{
    public float X { get; set; }
    public float Y { get; set; }
}

public sealed class BoardStroke
{
    public string Id { get; set; } = "";
    public List<BoardPoint> Points { get; set; } = [];
    public float Width { get; set; }
    public string Color { get; set; } = "";
}

public sealed class BoardText
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public BoardPoint Position { get; set; } = new();
    public float FontSize { get; set; }
    public string Color { get; set; } = "";
}

public sealed class EditBoardTextPayload
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class MoveBoardObjectPayload
{
    public string Id { get; set; } = "";
    public BoardPoint Offset { get; set; } = new();
}

public sealed class DeleteBoardObjectPayload
{
    public string Id { get; set; } = "";
}

public sealed class BoardSnapshotPayload
{
    public List<BoardStroke> Strokes { get; set; } = [];
    public List<BoardText> Texts { get; set; } = [];
}
