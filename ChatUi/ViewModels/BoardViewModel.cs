using System.ComponentModel;
using System.Runtime.CompilerServices;
using ChatProtocols;
using ChatSessionContract = global::IChatSession;

namespace ChatUi.ViewModels;

public sealed class BoardViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, BoardDocument> documents =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BoardDocument> pendingRequests = new(StringComparer.Ordinal);
    private ChatSessionContract? chatSession;
    private string currentRoom = "lobby";
    private string status = "Connect to load this room's board.";
    private bool isReady;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? BoardChanged;

    public string Status
    {
        get => status;
        private set
        {
            if (status == value)
            {
                return;
            }

            status = value;
            OnPropertyChanged();
        }
    }

    public bool IsReady
    {
        get => isReady;
        private set
        {
            if (isReady == value)
            {
                return;
            }

            isReady = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<BoardStroke> GetStrokes() => CreateProjection().Strokes;

    public IReadOnlyList<BoardText> GetTexts() => CreateProjection().Texts;

    public void SetSession(ChatSessionContract? session)
    {
        chatSession = session;
        if (session is null)
        {
            foreach (var document in documents.Values)
            {
                ClearPending(document);
                document.IsSyncing = false;
                document.SnapshotRequestId = null;
            }

            IsReady = false;
            Status = "Disconnected";
            BoardChanged?.Invoke();
        }
    }

    public async Task SwitchRoomAsync(string room)
    {
        if (documents.TryGetValue(currentRoom, out var previousDocument) &&
            previousDocument.SnapshotRequestId is not null)
        {
            pendingRequests.Remove(previousDocument.SnapshotRequestId);
            previousDocument.SnapshotRequestId = null;
            previousDocument.IsSyncing = false;
        }

        currentRoom = room;
        OnPropertyChanged(nameof(CurrentRoom));

        var document = GetDocument(room);
        document.BufferedUpdates.Clear();
        document.IsReady = false;
        document.IsSyncing = true;
        IsReady = false;
        Status = $"Loading {room} board...";
        BoardChanged?.Invoke();

        if (chatSession is null)
        {
            document.IsSyncing = false;
            Status = "Connect to load this room's board.";
            return;
        }

        await RequestSnapshotAsync(document, chatSession);
    }

    public string CurrentRoom => currentRoom;

    public void HandleMessage(ProtocolMessage message)
    {
        if (message.Type == MessageTypes.BoardSnapshot)
        {
            HandleSnapshot(message);
            return;
        }

        if (!IsBoardOperation(message.Type) ||
            !string.Equals(message.Room, currentRoom, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!message.BoardUpdateSequence.HasValue)
        {
            Status = "Received a board update without a sequence number.";
            return;
        }

        var document = GetDocument(currentRoom);
        var sequence = message.BoardUpdateSequence.Value;
        if (sequence <= document.LastAppliedSequence)
        {
            return;
        }

        document.BufferedUpdates[sequence] = message;
        if (!document.IsReady || document.IsSyncing)
        {
            return;
        }

        ApplyBufferedUpdates(document);
    }

    public void HandleError(ProtocolMessage message)
    {
        if (message.RequestId is null ||
            !pendingRequests.Remove(message.RequestId, out var document))
        {
            return;
        }

        document.PendingOperations.RemoveAll(
            operation => operation.RequestId == message.RequestId);
        if (document.SnapshotRequestId == message.RequestId)
        {
            document.SnapshotRequestId = null;
            document.IsSyncing = false;
            document.IsReady = false;
        }

        if (string.Equals(document.RoomId, currentRoom, StringComparison.OrdinalIgnoreCase))
        {
            IsReady = document.IsReady;
            Status = $"Board action rejected [{message.ErrorCode}]: {message.ErrorMessage}";
            BoardChanged?.Invoke();
        }
    }

    public Task AddStrokeAsync(BoardStroke stroke)
    {
        var room = currentRoom;
        var client = chatSession;
        var message = CreateOperation(MessageTypes.BoardStrokeAdd, room, stroke);
        return SubmitOperationAsync(
            message,
            client,
            room,
            requestId => client!.addStroke(room, stroke, requestId));
    }

    public Task AddTextAsync(BoardText text)
    {
        var room = currentRoom;
        var client = chatSession;
        var message = CreateOperation(MessageTypes.BoardTextAdd, room, text);
        return SubmitOperationAsync(
            message,
            client,
            room,
            requestId => client!.addText(room, text, requestId));
    }

    public Task EditTextAsync(EditBoardTextPayload text)
    {
        var room = currentRoom;
        var client = chatSession;
        var message = CreateOperation(MessageTypes.BoardTextEdit, room, text);
        return SubmitOperationAsync(
            message,
            client,
            room,
            requestId => client!.editText(room, text, requestId));
    }

    public Task MoveObjectAsync(MoveBoardObjectPayload move)
    {
        var room = currentRoom;
        var client = chatSession;
        var message = CreateOperation(MessageTypes.BoardObjectMove, room, move);
        return SubmitOperationAsync(
            message,
            client,
            room,
            requestId => client!.moveBoardObject(room, move, requestId));
    }

    public Task DeleteObjectAsync(DeleteBoardObjectPayload delete)
    {
        var room = currentRoom;
        var client = chatSession;
        var message = CreateOperation(MessageTypes.BoardObjectDelete, room, delete);
        return SubmitOperationAsync(
            message,
            client,
            room,
            requestId => client!.deleteBoardObject(room, delete, requestId));
    }

    private async Task SubmitOperationAsync(
        ProtocolMessage operation,
        ChatSessionContract? client,
        string room,
        Func<string, Task> send)
    {
        if (client is null || !IsReady ||
            !string.Equals(room, currentRoom, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var requestId = Guid.NewGuid().ToString("N");
        operation.RequestId = requestId;

        var document = GetDocument(room);
        document.PendingOperations.Add(operation);
        pendingRequests.Add(requestId, document);
        BoardChanged?.Invoke();

        try
        {
            await send(requestId);
        }
        catch (InvalidOperationException ex)
        {
            document.PendingOperations.Remove(operation);
            pendingRequests.Remove(requestId);
            Status = $"Could not send board action: {ex.Message}";
            BoardChanged?.Invoke();
        }
    }

    private async Task RequestSnapshotAsync(BoardDocument document, ChatSessionContract client)
    {
        var requestId = Guid.NewGuid().ToString("N");
        document.SnapshotRequestId = requestId;
        pendingRequests[requestId] = document;

        try
        {
            await client.requestBoardSnapshot(document.RoomId, requestId);
        }
        catch (InvalidOperationException ex)
        {
            pendingRequests.Remove(requestId);
            document.SnapshotRequestId = null;
            document.IsSyncing = false;
            document.IsReady = false;
            if (string.Equals(document.RoomId, currentRoom, StringComparison.OrdinalIgnoreCase))
            {
                IsReady = false;
                Status = $"Could not request board snapshot: {ex.Message}";
            }
        }
    }

    private void HandleSnapshot(ProtocolMessage message)
    {
        if (!string.Equals(message.Room, currentRoom, StringComparison.OrdinalIgnoreCase) ||
            !documents.TryGetValue(currentRoom, out var document) ||
            message.RequestId is null ||
            message.RequestId != document.SnapshotRequestId)
        {
            return;
        }

        if (!message.BoardUpdateSequence.HasValue ||
            !ProtocolMessageValidator.TryGetPayload(message, out BoardSnapshotPayload? snapshot) ||
            snapshot is null)
        {
            pendingRequests.Remove(message.RequestId);
            document.SnapshotRequestId = null;
            document.IsSyncing = false;
            document.IsReady = false;
            IsReady = false;
            Status = "Received an invalid board snapshot.";
            BoardChanged?.Invoke();
            return;
        }

        document.Strokes.Clear();
        document.Strokes.AddRange(snapshot.Strokes.Select(Clone));
        document.Texts.Clear();
        document.Texts.AddRange(snapshot.Texts.Select(Clone));
        document.LastAppliedSequence = message.BoardUpdateSequence.Value;
        foreach (var staleSequence in document.BufferedUpdates.Keys
                     .Where(sequence => sequence <= document.LastAppliedSequence)
                     .ToArray())
        {
            document.BufferedUpdates.Remove(staleSequence);
        }
        pendingRequests.Remove(message.RequestId);
        document.SnapshotRequestId = null;
        document.IsSyncing = false;
        document.IsReady = true;
        ClearPending(document);

        IsReady = true;
        Status = $"{document.RoomId} board synced at update {document.LastAppliedSequence}.";
        ApplyBufferedUpdates(document);
        BoardChanged?.Invoke();
    }

    private void ApplyBufferedUpdates(BoardDocument document)
    {
        while (document.BufferedUpdates.Remove(document.LastAppliedSequence + 1, out var update))
        {
            ApplyOperation(document.Strokes, document.Texts, update);
            document.LastAppliedSequence = update.BoardUpdateSequence!.Value;

            if (update.RequestId is not null &&
                pendingRequests.Remove(update.RequestId, out var pendingDocument))
            {
                pendingDocument.PendingOperations.RemoveAll(
                    operation => operation.RequestId == update.RequestId);
            }
        }

        if (string.Equals(document.RoomId, currentRoom, StringComparison.OrdinalIgnoreCase))
        {
            IsReady = document.IsReady;
            if (document.BufferedUpdates.Count > 0 &&
                !document.IsSyncing &&
                document.BufferedUpdates.Keys.First() > document.LastAppliedSequence + 1)
            {
                document.IsSyncing = true;
                IsReady = false;
                Status = "Board updates are out of sequence; requesting a fresh snapshot...";
                BoardChanged?.Invoke();
                if (chatSession is not null)
                {
                    _ = RequestSnapshotAsync(document, chatSession);
                }
            }
            else
            {
                Status = $"{document.RoomId} board synced at update {document.LastAppliedSequence}.";
                BoardChanged?.Invoke();
            }
        }
    }

    private BoardSnapshotPayload CreateProjection()
    {
        var document = GetDocument(currentRoom);
        var projection = new BoardSnapshotPayload
        {
            Strokes = document.Strokes.Select(Clone).ToList(),
            Texts = document.Texts.Select(Clone).ToList()
        };

        foreach (var operation in document.PendingOperations)
        {
            ApplyOperation(projection.Strokes, projection.Texts, operation);
        }

        return projection;
    }

    private static ProtocolMessage CreateOperation<T>(string type, string room, T payload)
    {
        return new ProtocolMessage
        {
            Version = ProtocolMessage.CurrentVersion,
            Type = type,
            Room = room,
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(payload)
        };
    }

    private static bool IsBoardOperation(string type) =>
        type == MessageTypes.BoardStrokeAdd ||
        type == MessageTypes.BoardTextAdd ||
        type == MessageTypes.BoardTextEdit ||
        type == MessageTypes.BoardObjectMove ||
        type == MessageTypes.BoardObjectDelete;

    private static void ApplyOperation(
        List<BoardStroke> strokes,
        List<BoardText> texts,
        ProtocolMessage operation)
    {
        switch (operation.Type)
        {
            case MessageTypes.BoardStrokeAdd:
                if (ProtocolMessageValidator.TryGetPayload(operation, out BoardStroke? stroke) &&
                    stroke is not null &&
                    strokes.All(existing => existing.Id != stroke.Id))
                {
                    strokes.Add(Clone(stroke));
                }
                break;
            case MessageTypes.BoardTextAdd:
                if (ProtocolMessageValidator.TryGetPayload(operation, out BoardText? text) &&
                    text is not null &&
                    texts.All(existing => existing.Id != text.Id))
                {
                    texts.Add(Clone(text));
                }
                break;
            case MessageTypes.BoardTextEdit:
                if (ProtocolMessageValidator.TryGetPayload(
                        operation,
                        out EditBoardTextPayload? edit) &&
                    edit is not null)
                {
                    var target = texts.FirstOrDefault(existing => existing.Id == edit.Id);
                    if (target is not null)
                    {
                        target.Text = edit.Text;
                    }
                }
                break;
            case MessageTypes.BoardObjectMove:
                if (ProtocolMessageValidator.TryGetPayload(
                        operation,
                        out MoveBoardObjectPayload? move) &&
                    move is not null)
                {
                    var textTarget = texts.FirstOrDefault(existing => existing.Id == move.Id);
                    if (textTarget is not null)
                    {
                        textTarget.Position.X += move.Offset.X;
                        textTarget.Position.Y += move.Offset.Y;
                        break;
                    }

                    var strokeTarget = strokes.FirstOrDefault(existing => existing.Id == move.Id);
                    if (strokeTarget is not null)
                    {
                        foreach (var point in strokeTarget.Points)
                        {
                            point.X += move.Offset.X;
                            point.Y += move.Offset.Y;
                        }
                    }
                }
                break;
            case MessageTypes.BoardObjectDelete:
                if (ProtocolMessageValidator.TryGetPayload(
                        operation,
                        out DeleteBoardObjectPayload? delete) &&
                    delete is not null)
                {
                    texts.RemoveAll(existing => existing.Id == delete.Id);
                    strokes.RemoveAll(existing => existing.Id == delete.Id);
                }
                break;
        }
    }

    private BoardDocument GetDocument(string room)
    {
        if (!documents.TryGetValue(room, out var document))
        {
            document = new BoardDocument(room);
            documents.Add(room, document);
        }

        return document;
    }

    private void ClearPending(BoardDocument document)
    {
        foreach (var operation in document.PendingOperations)
        {
            if (operation.RequestId is not null)
            {
                pendingRequests.Remove(operation.RequestId);
            }
        }

        document.PendingOperations.Clear();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static BoardStroke Clone(BoardStroke stroke) =>
        new()
        {
            Id = stroke.Id,
            Width = stroke.Width,
            Color = stroke.Color,
            Points = stroke.Points.Select(point => new BoardPoint { X = point.X, Y = point.Y }).ToList()
        };

    private static BoardText Clone(BoardText text) =>
        new()
        {
            Id = text.Id,
            Text = text.Text,
            FontSize = text.FontSize,
            Color = text.Color,
            Position = new BoardPoint { X = text.Position.X, Y = text.Position.Y }
        };

    private sealed class BoardDocument(string roomId)
    {
        public string RoomId { get; } = roomId;
        public List<BoardStroke> Strokes { get; } = [];
        public List<BoardText> Texts { get; } = [];
        public List<ProtocolMessage> PendingOperations { get; } = [];
        public SortedDictionary<long, ProtocolMessage> BufferedUpdates { get; } = [];
        public long LastAppliedSequence { get; set; }
        public bool IsReady { get; set; }
        public bool IsSyncing { get; set; }
        public string? SnapshotRequestId { get; set; }
    }
}
