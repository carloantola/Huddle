using System.Text.Json;

namespace ChatProtocols;

public static class ProtocolMessageValidator
{
    public static bool TryDeserialize(
        ReadOnlySpan<byte> payload,
        out ProtocolMessage? message,
        out ProtocolMessage? error)
    {
        message = null;
        error = null;

        try
        {
            message = JsonSerializer.Deserialize<ProtocolMessage>(payload);
        }
        catch (JsonException)
        {
            error = CreateError("invalid_json", "The payload is not a valid protocol message.");
            return false;
        }

        if (message is null)
        {
            error = CreateError("invalid_message", "The payload did not contain a message.");
            return false;
        }

        if (message.Version != ProtocolMessage.CurrentVersion)
        {
            error = CreateError("unsupported_version", $"Protocol version {message.Version} is not supported.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(message.Type))
        {
            error = CreateError("missing_type", "A message type is required.");
            return false;
        }

        switch (message.Type)
        {
            case MessageTypes.Chat when message.Text is null:
                error = CreateError("missing_text", "A chat message requires text.");
                return false;
            case MessageTypes.Join:
                if (string.IsNullOrWhiteSpace(message.Room))
                {
                    error = CreateError("missing_room", "A room name is required for this message.");
                    return false;
                }
                break;
            case MessageTypes.Leave:
                if (string.IsNullOrWhiteSpace(message.Room))
                {
                    error = CreateError("missing_room", "A room name is required for this message.");
                    return false;
                }
                break;
            case MessageTypes.Error:
                if (string.IsNullOrWhiteSpace(message.ErrorCode) ||
                    string.IsNullOrWhiteSpace(message.ErrorMessage))
                {
                    error = CreateError("invalid_error", "An error message requires a code and description.");
                    return false;
                }
                break;
            case MessageTypes.Chat:
            case MessageTypes.Heartbeat:
                break;
            case MessageTypes.ListRooms:
            case MessageTypes.ListUsers:
                break;
            case MessageTypes.BoardSnapshotRequest:
                if (!HasRoom(message))
                {
                    error = CreateError("missing_room", "A room name is required for this message.");
                    return false;
                }
                break;
            case MessageTypes.BoardStrokeAdd:
                if (!HasRoom(message) || !TryGetPayload(message, out BoardStroke? stroke) ||
                    !IsValidStroke(stroke))
                {
                    error = CreateError(
                        "invalid_board_payload",
                        $"A board stroke requires a room, ID, 1..{ProtocolLimits.MaximumStrokePointCount} finite points, positive width, and color.");
                    return false;
                }
                break;
            case MessageTypes.BoardTextAdd:
                if (!HasRoom(message) || !TryGetPayload(message, out BoardText? newText) ||
                    !IsValidText(newText))
                {
                    error = CreateError("invalid_board_payload", "A text object requires a room, ID, position, text, positive font size, and color.");
                    return false;
                }
                break;
            case MessageTypes.BoardTextEdit:
                if (!HasRoom(message) || !TryGetPayload(message, out EditBoardTextPayload? editText) ||
                    editText is null || string.IsNullOrWhiteSpace(editText.Id) || editText.Text is null)
                {
                    error = CreateError("invalid_board_payload", "Editing text requires a room, object ID, and text.");
                    return false;
                }
                break;
            case MessageTypes.BoardObjectMove:
                if (!HasRoom(message) || !TryGetPayload(message, out MoveBoardObjectPayload? move) ||
                    move is null || string.IsNullOrWhiteSpace(move.Id) || !IsValidPoint(move.Offset))
                {
                    error = CreateError("invalid_board_payload", "Moving an object requires a room, object ID, and finite offset.");
                    return false;
                }
                break;
            case MessageTypes.BoardObjectDelete:
                if (!HasRoom(message) || !TryGetPayload(message, out DeleteBoardObjectPayload? delete) ||
                    delete is null || string.IsNullOrWhiteSpace(delete.Id))
                {
                    error = CreateError("invalid_board_payload", "Deleting an object requires a room and object ID.");
                    return false;
                }
                break;
            case MessageTypes.BoardSnapshot:
                if (!HasRoom(message) || !TryGetPayload(message, out BoardSnapshotPayload? snapshot) ||
                    !IsValidSnapshot(snapshot))
                {
                    error = CreateError("invalid_board_payload", "A board snapshot requires a room and valid stroke and text collections.");
                    return false;
                }
                break;
            default:
                error = CreateError("unknown_type", $"Message type '{message.Type}' is not recognized.");
                return false;
        }

        return true;
    }

    public static ProtocolMessage CreateError(string code, string description)
    {
        return new ProtocolMessage
        {
            Version = ProtocolMessage.CurrentVersion,
            Type = MessageTypes.Error,
            ErrorCode = code,
            ErrorMessage = description
        };
    }

    private static bool HasRoom(ProtocolMessage message) =>
        !string.IsNullOrWhiteSpace(message.Room);

    public static bool TryGetPayload<T>(ProtocolMessage message, out T? value)
    {
        value = default;
        if (message.Payload is not { ValueKind: JsonValueKind.Object } payload)
        {
            return false;
        }

        try
        {
            value = payload.Deserialize<T>();
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidStroke(BoardStroke? stroke) =>
        stroke is not null &&
        !string.IsNullOrWhiteSpace(stroke.Id) &&
        stroke.Points is { Count: > 0 and <= ProtocolLimits.MaximumStrokePointCount } &&
        stroke.Points.All(IsValidPoint) &&
        float.IsFinite(stroke.Width) &&
        stroke.Width > 0 &&
        !string.IsNullOrWhiteSpace(stroke.Color);

    private static bool IsValidText(BoardText? text) =>
        text is not null &&
        !string.IsNullOrWhiteSpace(text.Id) &&
        text.Text is not null &&
        IsValidPoint(text.Position) &&
        float.IsFinite(text.FontSize) &&
        text.FontSize > 0 &&
        !string.IsNullOrWhiteSpace(text.Color);

    private static bool IsValidSnapshot(BoardSnapshotPayload? snapshot) =>
        snapshot is not null &&
        snapshot.Strokes is not null &&
        snapshot.Texts is not null &&
        snapshot.Strokes.All(IsValidStroke) &&
        snapshot.Texts.All(IsValidText);

    private static bool IsValidPoint(BoardPoint? point) =>
        point is not null &&
        float.IsFinite(point.X) &&
        float.IsFinite(point.Y);
}
