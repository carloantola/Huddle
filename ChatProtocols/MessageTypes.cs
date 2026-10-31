namespace ChatProtocols;

public static class MessageTypes
{
    public const string Chat = "chat";
    public const string Join = "join";
    public const string Leave = "leave";
    public const string Error = "error";
    public const string Heartbeat = "heartbeat";
    public const string ListRooms = "list_rooms";
    public const string ListUsers = "list_users";

    public const string BoardStrokeAdd = "board.stroke.add";
    public const string BoardTextAdd = "board.text.add";
    public const string BoardTextEdit = "board.text.edit";
    public const string BoardObjectMove = "board.object.move";
    public const string BoardObjectDelete = "board.object.delete";
    public const string BoardSnapshotRequest = "board.snapshot.request";
    public const string BoardSnapshot = "board.snapshot";
}
