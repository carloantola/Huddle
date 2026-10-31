namespace ChatServer;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Buffers.Binary;
using ChatProtocols;
using System.Drawing;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Security.Principal;

public class ChatServer : IChatServer
{
    const int HeaderSize = 4;
    const string mainRoom = "lobby";
    const string chillRoom = "chill";
    public List<ClientConnection> clientList = new List<ClientConnection>();
    public Dictionary<string, RoomData> roomsData = new Dictionary<string, RoomData>();

    public Dictionary<string, Room> rooms = new(StringComparer.OrdinalIgnoreCase);

    public Channel<BroadCast> jobQueue = Channel.CreateBounded<BroadCast>(new BoundedChannelOptions(10)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true
    });

    readonly CancellationTokenSource cancelToken = new CancellationTokenSource();

    public TcpListener? tcpListener;
    private Task? workerTask;
    private readonly List<Task> clientHandlerTasks = new();
    public Task? Receiver;
    public Task StartAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        const int Port = 5000;
        rooms.Add(mainRoom, new Room(mainRoom, new List<Guid>()));
        rooms.Add(chillRoom, new Room(chillRoom, new List<Guid>()));

        roomsData.Add(mainRoom, new RoomData(new(), new(), mainRoom));
        roomsData.Add(chillRoom, new RoomData(new(), new(), chillRoom));

        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        tcpListener = listener;
        workerTask = StartBackgroundWorkerAsync(jobQueue.Reader, cancelToken.Token);
        Receiver = HandleClient(listener, cancelToken.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        cancelToken.Cancel();
        tcpListener?.Stop();
        tcpListener = null;
        jobQueue.Writer.TryComplete();

        try
        {
            if (Receiver is not null)
            {
                await Receiver;
            }
        }
        finally
        {
            ClientConnection[] clients;
            lock (clientList)
            {
                clients = clientList.ToArray();
            }

            foreach (var client in clients)
            {
                RemoveAndCloseClient(client);
            }

            var tasks = clientHandlerTasks.ToList();
            if (workerTask is not null)
            {
                tasks.Add(workerTask);
            }

            await Task.WhenAll(tasks);
        }
    }

    private async Task HandleClient(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                var clientConnect = new ClientConnection(client, mainRoom);
                lock (clientList)
                {
                    clientList.Add(clientConnect);
                }

                lock (rooms)
                {
                    rooms[mainRoom].clients.Add(clientConnect.Id);
                }

                clientHandlerTasks.RemoveAll(task => task.IsCompleted);
                clientHandlerTasks.Add(HandleClientMessages(clientConnect, token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
    }

    private async Task HandleClientMessages(ClientConnection client, CancellationToken token)
    {
        try
        {
            await ReceiveMessagesSeqAsync(client, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException)
        {
            Console.WriteLine($"Client {client.Id} disconnected.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Client handler {client.Id} failed: {ex}");
        }
    }

    public static async void handleClient(TcpClient client)
    {
        try
        {
            var buffer = new byte[1024];
            var leftOver = new List<byte>();
            var leftoverSize = 0;
            var dataSize = 0;
            var stream = client.GetStream();
            int bytes;
            while ((bytes = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
            {
                if (bytes == 0)
                {
                    client.Close();
                }
                else
                {
                    if (leftOver.Count > 0)
                    {
                        for (int i = 0; i < bytes; i++)
                        {
                            leftOver.Add(buffer[i]);
                        }
                        dataSize = leftOver.Count;
                    }
                    else
                    {
                        leftOver = new List<byte>(buffer);
                        dataSize = bytes;
                        leftOver.RemoveRange(bytes, leftOver.Count - bytes);
                    }

                    var indexList = new List<int>();
                    for (int i = 0; i < dataSize; i++)
                    {
                        if (leftOver[i] == 10)
                        {
                            indexList.Add(i);
                        }
                    }

                    var indexPairList = new List<(int, int)>();
                    var start = 0;
                    for (int i = 0; i < indexList.Count; i++)
                    {
                        indexPairList.Add((start, indexList[i]));
                        start = indexList[i] + 1;
                    }

                    if (indexList.Count == 0)
                    {
                        leftoverSize = dataSize;
                    }
                    else
                    {
                        leftoverSize = dataSize - (indexPairList[indexList.Count - 1].Item2 + 1);
                    }

                    for (int i = 0; i < indexPairList.Count; i++)
                    {
                        var startI = indexPairList[i].Item1;
                        var count = indexPairList[i].Item2 - startI;
                        var data = Encoding.ASCII.GetString([.. leftOver], startI, count);
                    }

                    if (leftoverSize > 0 && indexPairList.Count > 0)
                    {
                        leftOver.RemoveRange(0, indexPairList[indexList.Count - 1].Item2 + 1);
                    }

                    if (leftoverSize == 0)
                    {
                        leftOver.Clear();
                    }

                    var responseMessage = "Hello from server";
                    var rbytes = Encoding.ASCII.GetBytes(responseMessage);
                    await stream.WriteAsync(rbytes);
                }
            }
        }
        finally
        {
            client.Close();
        }
    }

    public async Task broadCastMessages(ClientConnection client, byte[] message)
    {
        var recipients = new List<ClientConnection>();
        var clientRoom = client.RoomId;
        lock (clientList)
        {
            foreach (var connectedClient in clientList)
            {
                if (connectedClient.Id != client.Id && connectedClient.RoomId == clientRoom)
                {
                    recipients.Add(connectedClient);
                }
            }
        }

        foreach (var recipient in recipients)
        {
            try
            {
                await recipient.SendLock.WaitAsync();
                try
                {
                    await recipient.Client.GetStream().WriteAsync(message);
                }
                finally
                {
                    recipient.SendLock.Release();
                }
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Broadcast to client {recipient.Id} failed: {ex.Message}");
                RemoveAndCloseClient(recipient);
            }
            catch (ObjectDisposedException ex)
            {
                Console.Error.WriteLine($"Client {recipient.Id} was already closed: {ex.Message}");
                RemoveAndCloseClient(recipient);
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"Broadcast to client {recipient.Id} failed: {ex.Message}");
                RemoveAndCloseClient(recipient);
            }
        }
    }

    public async Task broadCastBoardUpdate(string room, byte[] message)
    {
        var recipients = new List<ClientConnection>();
        lock (clientList)
        {
            recipients = clientList.Where(client => string.Equals(client.RoomId, room, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        foreach (var recipient in recipients)
        {
            try
            {
                await recipient.SendLock.WaitAsync();
                try
                {
                    await recipient.Client.GetStream().WriteAsync(message);
                }
                finally
                {
                    recipient.SendLock.Release();
                }
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Broadcast to client {recipient.Id} failed: {ex.Message}");
                RemoveAndCloseClient(recipient);
            }
            catch (ObjectDisposedException ex)
            {
                Console.Error.WriteLine($"Client {recipient.Id} was already closed: {ex.Message}");
                RemoveAndCloseClient(recipient);
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"Broadcast to client {recipient.Id} failed: {ex.Message}");
                RemoveAndCloseClient(recipient);
            }
        }
    }

    public async Task broadCastMessageToClient(ClientConnection client, byte[] message)
    {
        try
        {
            await client.SendLock.WaitAsync();
            try
            {
                await client.Client.GetStream().WriteAsync(message);
            }
            finally
            {
                client.SendLock.Release();
            }
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Broadcast to client {client.Id} failed: {ex.Message}");
            RemoveAndCloseClient(client);
        }
        catch (ObjectDisposedException ex)
        {
            Console.Error.WriteLine($"Client {client.Id} was already closed: {ex.Message}");
            RemoveAndCloseClient(client);
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"Broadcast to client {client.Id} failed: {ex.Message}");
            RemoveAndCloseClient(client);
        }

    }

    private async Task ReceiveMessagesSeqAsync(ClientConnection client, CancellationToken token)
    {
        try
        {
            var stream = client.Client.GetStream();
            while (!token.IsCancellationRequested)
            {
                var header = new byte[HeaderSize];
                var headerBytesRead = 0;
                var bytesLefToRead = 0;
                while (headerBytesRead < HeaderSize)
                {
                    bytesLefToRead = HeaderSize - headerBytesRead;
                    var bytes = await stream.ReadAsync(header.AsMemory(headerBytesRead, bytesLefToRead),token);
                    if (bytes == 0)
                    {
                        return;
                    }

                    headerBytesRead += bytes;
                }

                var payloadSize = BinaryPrimitives.ReadInt32BigEndian(header);
                if (payloadSize < 0 || payloadSize > ProtocolLimits.MaximumFramePayloadSize)
                {
                    Console.WriteLine($"Invalid payload size {payloadSize}; closing client.");
                    return;
                }

                var payload = new byte[payloadSize];
                var payloadBytesRead = 0;
                bytesLefToRead = 0;
                while (payloadBytesRead < payloadSize)
                {
                    bytesLefToRead = payloadSize - payloadBytesRead;
                    var bytes = await stream.ReadAsync(payload.AsMemory(payloadBytesRead, bytesLefToRead),token);

                    if (bytes == 0)
                    {
                        return;
                    }

                    payloadBytesRead += bytes;
                }
                await ProcessMessage(client, payload);

            }

        }

        catch (IOException ex) when (
            ex.InnerException is SocketException socketException &&
            (socketException.SocketErrorCode == SocketError.ConnectionReset ||
             socketException.SocketErrorCode == SocketError.ConnectionAborted))
        {
            Console.WriteLine($"Client {client.Id} disconnected.");
        }
        finally
        {
            RemoveAndCloseClient(client);
        }
    }

    async Task ProcessMessage(ClientConnection client, byte[] payload)
    {
        if (!ProtocolMessageValidator.TryDeserialize(payload, out var message, out var protocolError))
        {
            protocolError!.RequestId = message?.RequestId;
            await SendToClientAsync(client, SendProtocolMessage(protocolError));
        }
        else if (message!.Type == MessageTypes.Chat)
        {
            Console.WriteLine("Received payload: " + message.Text);
            message.SenderId = client.DisplayId;
            var messageFrame = SendProtocolMessage(message);
            await broadCastMessages(client, messageFrame);
        }
        else if (message!.Type == MessageTypes.Join)
        {
            Console.WriteLine("Received payload: " + message.Room);
            var room = message.Room;
            if (string.IsNullOrWhiteSpace(room) || !JoinRoom(client, room))
            {
                var roomError = ProtocolMessageValidator.CreateError(
                    "room_not_found",
                    $"Room '{room}' does not exist.");
                await SendToClientAsync(client, SendProtocolMessage(roomError));
            }

        }
        else if (message!.Type == MessageTypes.Leave)
        {
            Console.WriteLine("Received payload: " + message.Room);
            var room = message.Room;
            if (!string.IsNullOrWhiteSpace(room))
            {
                LeaveRoom(client, room);
            }

        }
        else if (message!.Type == MessageTypes.ListRooms)
        {
            var response = new ProtocolMessage
            {
                Version = ProtocolMessage.CurrentVersion,
                Type = MessageTypes.ListRooms,
                Text = GetRoomNames()
            };
            await SendToClientAsync(client, SendProtocolMessage(response));
        }
        else if (message!.Type == MessageTypes.ListUsers)
        {
            var response = new ProtocolMessage
            {
                Version = ProtocolMessage.CurrentVersion,
                Type = MessageTypes.ListUsers,
                Text = GetUsersInCurrentRoom(client)
            };
            await SendToClientAsync(client, SendProtocolMessage(response));

        }
        else if (message!.Type == MessageTypes.BoardStrokeAdd)
        {
            var response = ProtocolMessageValidator.TryGetPayload<BoardStroke>(message, out BoardStroke? stroke);
            if (response && stroke != null && message.Room != null)
            {
                await AddStroke(client, stroke, message.Room, message.RequestId);
            }

        }
        else if (message!.Type == MessageTypes.BoardTextAdd)
        {
            var response = ProtocolMessageValidator.TryGetPayload<BoardText>(message, out BoardText? text);
            if (response && text != null && message.Room != null)
            {
                await AddText(client, text, message.Room, message.RequestId);
            }

        }
        else if (message!.Type == MessageTypes.BoardTextEdit)
        {
            var response = ProtocolMessageValidator.TryGetPayload<EditBoardTextPayload>(message, out EditBoardTextPayload? text);
            if (response && text != null && message.Room != null)
            {
                await EditText(client, text, message.Room, message.RequestId);
            }

        }
        else if (message!.Type == MessageTypes.BoardObjectMove)
        {
            var response = ProtocolMessageValidator.TryGetPayload<MoveBoardObjectPayload>(message, out MoveBoardObjectPayload? move);
            if (response && move != null && message.Room != null)
            {
                await MoveObject(client, move, message.Room, message.RequestId);
            }

        }
        else if (message!.Type == MessageTypes.BoardObjectDelete)
        {
            var response = ProtocolMessageValidator.TryGetPayload<DeleteBoardObjectPayload>(message, out DeleteBoardObjectPayload? delete);
            if (response && delete != null && message.Room != null)
            {
                await DeleteObject(client, delete, message.Room, message.RequestId);
            }

        }
        else if (message!.Type == MessageTypes.BoardSnapshotRequest)
        {
            if (message.Room != null)
            {
                await SendBoardSnapshot(client, message.Room, message.RequestId);
            }

        }
        else
        {
            var unsupportedType = ProtocolMessageValidator.CreateError(
                "unsupported_type",
                $"The server does not handle '{message!.Type}' messages yet.");
            unsupportedType.RequestId = message.RequestId;
            await SendToClientAsync(client, SendProtocolMessage(unsupportedType));
        }
    }

    private ProtocolMessage CreateBoardError(string code,string description,string? requestId)
    {
        var error = ProtocolMessageValidator.CreateError(code, description);
        error.RequestId = requestId;
        return error;
    }

    private async Task SendBoardSnapshot(ClientConnection client,string roomId,string? requestId)
    {
        var currentRoom = client.RoomId;

        if (!string.Equals(currentRoom, roomId, StringComparison.OrdinalIgnoreCase))
        {
            var roomError = CreateBoardError("room_mismatch","The requested board room does not match the client's current room.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }
        if (!IsClientMemberOfRoom(client, currentRoom))
        {
            var membershipError = CreateBoardError("not_room_member","The client is not a member of this room.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(membershipError));
            return;
        }
        if (!roomsData.TryGetValue(currentRoom, out var room))
        {
            var roomError = CreateBoardError("room_not_loaded","The room's board state is not loaded.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }

        ProtocolMessage response;

        await room.Gate.WaitAsync();
        try
        {
            var payload = new BoardSnapshotPayload
            {
                Strokes = room._strokes.Values.ToList(),
                Texts = room._texts.Values.ToList()
            };
            var payloadElement = JsonSerializer.SerializeToElement(payload);

            response = new ProtocolMessage
            {
                Version = ProtocolMessage.CurrentVersion,
                Type = MessageTypes.BoardSnapshot,
                Room = room.roomId,
                RequestId = requestId,
                Payload = payloadElement,
                BoardUpdateSequence = room.BoardUpdateSequence
            };
        }
        finally
        {
            room.Gate.Release();
        }

        await SendToClientAsync(client, SendProtocolMessage(response));
    }

    public async Task AddStroke(ClientConnection client,BoardStroke stroke,string roomId,string? requestId)
    {
        var currentRoom = client.RoomId;

        if (!string.Equals(currentRoom, roomId, StringComparison.OrdinalIgnoreCase))
        {
            var roomError = CreateBoardError("room_mismatch","The requested board room does not match the client's current room.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }
        if (!IsClientMemberOfRoom(client, currentRoom))
        {
            var membershipError = CreateBoardError("not_room_member","The client is not a member of this room.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(membershipError));
            return;
        }
        if (!roomsData.TryGetValue(currentRoom, out var room))
        {
            var roomError = CreateBoardError("room_not_loaded","The room's board state is not loaded.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }

        ProtocolMessage response;

        await room.Gate.WaitAsync();
        try
        {
            if (room._texts.ContainsKey(stroke.Id))
            {
                response = CreateBoardError("object_type_mismatch","That ID is already used by a text object.",requestId);
            }
            else if (room._strokes.ContainsKey(stroke.Id))
            {
                response = CreateBoardError("board_object_id_conflict","A stroke with that ID already exists.",requestId);
            }
            else
            {
                var payload = JsonSerializer.SerializeToElement(stroke);
                room._strokes.Add(stroke.Id, stroke);

                var sequence = ++room.BoardUpdateSequence;
                response = new ProtocolMessage
                {
                    Type = MessageTypes.BoardStrokeAdd,
                    Room = currentRoom,
                    RequestId = requestId,
                    BoardUpdateSequence = sequence,
                    Payload = payload
                };
                await jobQueue.Writer.WriteAsync(new BroadCast(currentRoom, response));
            }
        }
        finally
        {
            room.Gate.Release();
        }
        if (response.Type == MessageTypes.Error)
        {
            await SendToClientAsync(client, SendProtocolMessage(response));
        }
    }

    public async Task AddText(ClientConnection client,BoardText text,string roomId,string? requestId)
    {
        var currentRoom = client.RoomId;

        if (!string.Equals(currentRoom, roomId, StringComparison.OrdinalIgnoreCase))
        {
            var roomError = CreateBoardError("room_mismatch","The requested board room does not match the client's current room.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }
        if (!IsClientMemberOfRoom(client, currentRoom))
        {
            var membershipError = CreateBoardError("not_room_member","The client is not a member of this room.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(membershipError));
            return;
        }

        if (!roomsData.TryGetValue(currentRoom, out var room))
        {
            var roomError = CreateBoardError("room_not_loaded","The room's board state is not loaded.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }

        ProtocolMessage response;

        await room.Gate.WaitAsync();
        try
        {
            if (room._texts.ContainsKey(text.Id))
            {
                response = CreateBoardError("id_conflict","That ID exists",requestId);
            }
            else if (room._strokes.ContainsKey(text.Id))
            {
                response = CreateBoardError("board_object_id_conflict","A stroke with that ID already exists.",requestId);
            }
            else
            {
                var payload = JsonSerializer.SerializeToElement(text);
                room._texts.Add(text.Id, text);

                var sequence = ++room.BoardUpdateSequence;

                response = new ProtocolMessage
                {
                    Type = MessageTypes.BoardTextAdd,
                    Room = currentRoom,
                    RequestId = requestId,
                    BoardUpdateSequence = sequence,
                    Payload = payload
                };
                await jobQueue.Writer.WriteAsync(new BroadCast(currentRoom, response));

            }
        }
        finally
        {
            room.Gate.Release();
        }

        if (response.Type == MessageTypes.Error)
        {
            await SendToClientAsync(client, SendProtocolMessage(response));
        }

    }

    async Task StartBackgroundWorkerAsync(ChannelReader<BroadCast> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (BroadCast job in reader.ReadAllAsync(cancellationToken))
            {
                await broadCastBoardUpdate(job.RoomId, SendProtocolMessage(job.Message));
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[Worker] Canceled.");
        }
    }
    public async Task EditText(ClientConnection client,EditBoardTextPayload text,string roomId,string? requestId)
    {
        var currentRoom = client.RoomId;
        if (!string.Equals(currentRoom, roomId, StringComparison.OrdinalIgnoreCase))
        {
            var roomError = CreateBoardError("room_mismatch","The requested board room does not match the client's current room.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }
        if (!IsClientMemberOfRoom(client, currentRoom))
        {
            var membershipError = CreateBoardError("not_room_member","The client is not a member of this room.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(membershipError));
            return;
        }
        if (!roomsData.TryGetValue(currentRoom, out var room))
        {
            var roomError = CreateBoardError("room_not_loaded","The room's board state is not loaded.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }

        ProtocolMessage response;

        await room.Gate.WaitAsync();
        try
        {
            if (!room._texts.ContainsKey(text.Id))
            {
                response = CreateBoardError("id_missing","That ID does not exist",requestId);
            }
            else if (room._strokes.ContainsKey(text.Id))
            {
                response = CreateBoardError("board_object_id_conflict","A stroke with that ID already exists.",requestId);
            }
            else
            {
                var payload = JsonSerializer.SerializeToElement(text);
                room._texts[text.Id].Text = text.Text;

                var sequence = ++room.BoardUpdateSequence;
                response = new ProtocolMessage
                {
                    Type = MessageTypes.BoardTextEdit,
                    Room = currentRoom,
                    RequestId = requestId,
                    BoardUpdateSequence = sequence,
                    Payload = payload
                };
                await jobQueue.Writer.WriteAsync(new BroadCast(currentRoom, response));
            }
        }
        finally
        {
            room.Gate.Release();
        }

        if (response.Type == MessageTypes.Error)
        {
            await SendToClientAsync(client, SendProtocolMessage(response));
        }
    }

    public async Task MoveObject(ClientConnection client,MoveBoardObjectPayload move,string roomId,string? requestId)
    {
        var currentRoom = client.RoomId;
        if (!string.Equals(currentRoom, roomId, StringComparison.OrdinalIgnoreCase))
        {
            var roomError = CreateBoardError("room_mismatch","The requested board room does not match the client's current room.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }
        if (!IsClientMemberOfRoom(client, currentRoom))
        {
            var membershipError = CreateBoardError("not_room_member","The client is not a member of this room.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(membershipError));
            return;
        }

        if (!roomsData.TryGetValue(currentRoom, out var room))
        {
            var roomError = CreateBoardError("room_not_loaded","The room's board state is not loaded.",requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }

        ProtocolMessage response;

        await room.Gate.WaitAsync();
        try
        {
            if (room._texts.ContainsKey(move.Id))
            {
                var payload = JsonSerializer.SerializeToElement(move);
                var sequence = ++room.BoardUpdateSequence;
                response = new ProtocolMessage
                {
                    Type = MessageTypes.BoardObjectMove,
                    Room = currentRoom,
                    RequestId = requestId,
                    BoardUpdateSequence = sequence,
                    Payload = payload
                };
                var point = room._texts[move.Id];
                point.Position.X = point.Position.X + move.Offset.X;
                point.Position.Y = point.Position.Y + move.Offset.Y;
                await jobQueue.Writer.WriteAsync(new BroadCast(currentRoom, response));
            }
            else if (room._strokes.ContainsKey(move.Id))
            {
                var payload = JsonSerializer.SerializeToElement(move);
                var sequence = ++room.BoardUpdateSequence;
                response = new ProtocolMessage
                {
                    Type = MessageTypes.BoardObjectMove,
                    Room = currentRoom,
                    RequestId = requestId,
                    BoardUpdateSequence = sequence,
                    Payload = payload
                };
                var positions = room._strokes[move.Id];
                foreach (var point in positions.Points)
                {
                    point.X = point.X + move.Offset.X;
                    point.Y = point.Y + move.Offset.Y;
                }
                await jobQueue.Writer.WriteAsync(new BroadCast(currentRoom, response));
            }
            else
            {
                response = CreateBoardError(
                    "id_missing",
                    "That ID does not exist",
                    requestId);
            }
        }
        finally
        {
            room.Gate.Release();
        }

        if (response.Type == MessageTypes.Error)
        {
            await SendToClientAsync(client, SendProtocolMessage(response));
        }
    }

    public async Task DeleteObject(ClientConnection client,DeleteBoardObjectPayload delete,string roomId,string? requestId)
    {
        var currentRoom = client.RoomId;
        if (!string.Equals(currentRoom, roomId, StringComparison.OrdinalIgnoreCase))
        {
            var roomError = CreateBoardError(
                "room_mismatch",
                "The requested board room does not match the client's current room.",
                requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }
        if (!IsClientMemberOfRoom(client, currentRoom))
        {
            var membershipError = CreateBoardError(
                "not_room_member",
                "The client is not a member of this room.",
                requestId);
            await SendToClientAsync(client, SendProtocolMessage(membershipError));
            return;
        }
        if (!roomsData.TryGetValue(currentRoom, out var room))
        {
            var roomError = CreateBoardError(
                "room_not_loaded",
                "The room's board state is not loaded.",
                requestId);
            await SendToClientAsync(client, SendProtocolMessage(roomError));
            return;
        }


        ProtocolMessage response;

        await room.Gate.WaitAsync();
        try
        {
            if (room._texts.ContainsKey(delete.Id))
            {
                var payload = JsonSerializer.SerializeToElement(delete);
                var sequence = ++room.BoardUpdateSequence;
                response = new ProtocolMessage
                {
                    Type = MessageTypes.BoardObjectDelete,
                    Room = currentRoom,
                    RequestId = requestId,
                    BoardUpdateSequence = sequence,
                    Payload = payload
                };
                room._texts.Remove(delete.Id);
                await jobQueue.Writer.WriteAsync(new BroadCast(currentRoom, response));
            }
            else if (room._strokes.ContainsKey(delete.Id))
            {
                var payload = JsonSerializer.SerializeToElement(delete);
                var sequence = ++room.BoardUpdateSequence;
                response = new ProtocolMessage
                {
                    Type = MessageTypes.BoardObjectDelete,
                    Room = currentRoom,
                    RequestId = requestId,
                    BoardUpdateSequence = sequence,
                    Payload = payload
                };
                room._strokes.Remove(delete.Id);
                await jobQueue.Writer.WriteAsync(new BroadCast(currentRoom, response));
            }
            else
            {
                response = CreateBoardError(
                    "id_missing",
                    "That ID does not exist",
                    requestId);
            }
        }
        finally
        {
            room.Gate.Release();
        }

        if (response.Type == MessageTypes.Error)
        {
            await SendToClientAsync(client, SendProtocolMessage(response));
        }
    }




    private  bool IsClientMemberOfRoom(ClientConnection client, string roomId)
    {
        lock (clientList)
        {
            if (!clientList.Contains(client))
            {
                return false;
            }

            lock (rooms)
            {
                return rooms.TryGetValue(roomId, out var room) &&
                    room.clients.Contains(client.Id) &&
                    string.Equals(client.RoomId, room.RoomId, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    void RemoveAndCloseClient(ClientConnection client)
    {
        lock (clientList)
        {
            clientList.Remove(client);

        }
        lock (rooms)
        {
            if (rooms.TryGetValue(client.RoomId, out var room))
            {
                room.clients.Remove(client.Id);
            }
        }

        client.Client.Close();
    }

    public bool JoinRoom(ClientConnection client, string roomid)
    {
        lock (rooms)
        {
            if (!rooms.TryGetValue(roomid, out var targetRoom))
            {
                return false;
            }

            if (rooms.TryGetValue(client.RoomId, out var currentRoom))
            {
                currentRoom.clients.Remove(client.Id);
            }

            if (!targetRoom.clients.Contains(client.Id))
            {
                targetRoom.clients.Add(client.Id);
            }

            client.RoomId = targetRoom.RoomId;
            return true;
        }
    }

    private string GetRoomNames()
    {
        lock (rooms)
        {
            return string.Join(
                "\n",
                rooms.Keys.OrderBy(roomName => roomName, StringComparer.OrdinalIgnoreCase));
        }
    }

    private string GetUsersInCurrentRoom(ClientConnection client)
    {
        HashSet<Guid> roomClientIds;
        lock (rooms)
        {
            if (!rooms.TryGetValue(client.RoomId, out var room))
            {
                return "";
            }

            roomClientIds = room.clients.ToHashSet();
        }

        lock (clientList)
        {
            return string.Join(
                "\n",
                clientList
                    .Where(connectedClient => roomClientIds.Contains(connectedClient.Id))
                    .OrderBy(connectedClient => connectedClient.DisplayId, StringComparer.Ordinal)
                    .Select(connectedClient => connectedClient.DisplayId));
        }
    }

    public void LeaveRoom(ClientConnection client, string roomid)
    {
        lock (rooms)
        {
            if (string.Equals(client.RoomId, mainRoom, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(client.RoomId, roomid, StringComparison.OrdinalIgnoreCase) ||
                !rooms.TryGetValue(mainRoom, out var lobby))
            {
                return;
            }

            if (rooms.TryGetValue(client.RoomId, out var currentRoom))
            {
                currentRoom.clients.Remove(client.Id);
            }

            if (!lobby.clients.Contains(client.Id))
            {
                lobby.clients.Add(client.Id);
            }

            client.RoomId = lobby.RoomId;
        }
    }

    public async Task handleClientLengthPrefix(ClientConnection client)
    {
        try
        {
            var buffer = new byte[10];
            var pendingBytes = new List<byte>();
            var payloadBytes = new List<byte>();
            var headerSize = 4;
            var payloadSize = 0;
            var stream = client.Client.GetStream();
            int bytes;

            while ((bytes = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
            {
                for (int i = 0; i < bytes; i++)
                {
                    pendingBytes.Add(buffer[i]);
                }

                if (pendingBytes.Count >= headerSize)
                {
                    var header = pendingBytes.GetRange(0, headerSize).ToArray();
                    payloadSize = BinaryPrimitives.ReadInt32BigEndian(header);
                    if (payloadSize < 0 || payloadSize > ProtocolLimits.MaximumFramePayloadSize)
                    {
                        Console.WriteLine($"Invalid payload size {payloadSize}; closing client.");
                        return;
                    }
                }

                if (pendingBytes.Count >= headerSize)
                {
                    var header = pendingBytes.GetRange(0, headerSize);
                    payloadSize = BinaryPrimitives.ReadInt32BigEndian([.. header]);

                    if (payloadSize < 0 || payloadSize > ProtocolLimits.MaximumFramePayloadSize)
                    {
                        Console.WriteLine($"Invalid payload size {payloadSize}; closing client.");
                        return;
                    }

                    var fullFrame = pendingBytes.Count >= headerSize + payloadSize;
                    while (fullFrame)
                    {
                        if (pendingBytes.Count >= headerSize + payloadSize)
                        {
                            payloadBytes = pendingBytes.GetRange(headerSize, payloadSize);
                            pendingBytes.RemoveRange(0, headerSize + payloadSize);

                            if (!ProtocolMessageValidator.TryDeserialize(
                                    payloadBytes.ToArray(),
                                    out var incomingMessage,
                                    out var protocolError))
                            {
                                await SendToClientAsync(client, SendProtocolMessage(protocolError!));
                            }
                            else if (incomingMessage!.Type == MessageTypes.Chat)
                            {
                                Console.WriteLine("Received payload: " + incomingMessage.Text);
                                var messageFrame = CreateFrame(payloadBytes.ToArray());
                                await broadCastMessages(client, messageFrame);
                            }
                            else if (incomingMessage!.Type == MessageTypes.Join)
                            {
                                Console.WriteLine("Received payload: " + incomingMessage.Room);
                                var room = incomingMessage.Room;
                                if (!string.IsNullOrWhiteSpace(room))
                                {
                                    JoinRoom(client, room);
                                }
                            }
                            else if (incomingMessage!.Type == MessageTypes.Leave)
                            {
                                Console.WriteLine("Received payload: " + incomingMessage.Room);
                                var room = incomingMessage.Room;
                                if (!string.IsNullOrWhiteSpace(room))
                                {
                                    LeaveRoom(client, room);
                                }
                            }
                            else
                            {
                                var unsupportedType = ProtocolMessageValidator.CreateError(
                                    "unsupported_type",
                                    $"The server does not handle '{incomingMessage!.Type}' messages yet.");
                                await SendToClientAsync(client, SendProtocolMessage(unsupportedType));
                            }

                            payloadBytes.Clear();
                            payloadSize = 0;
                        }

                        if (pendingBytes.Count < headerSize + payloadSize)
                        {
                            break;
                        }

                        var headerF = pendingBytes.GetRange(0, headerSize);
                        payloadSize = BinaryPrimitives.ReadInt32BigEndian([.. headerF]);

                        if (payloadSize < 0 || payloadSize > ProtocolLimits.MaximumFramePayloadSize)
                        {
                            Console.WriteLine($"Invalid payload size {payloadSize}; closing client.");
                            return;
                        }

                        fullFrame = pendingBytes.Count >= headerSize + payloadSize;
                    }
                }
            }
        }
        catch (IOException ex) when (
            ex.InnerException is SocketException socketException &&
            (socketException.SocketErrorCode == SocketError.ConnectionReset ||
             socketException.SocketErrorCode == SocketError.ConnectionAborted))
        {
            Console.WriteLine($"Client {client.Id} disconnected.");
        }
        finally
        {
            RemoveAndCloseClient(client);
        }
    }

    public static byte[] ConvertMessageToPacket(string message)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        return CreateFrame(messageBytes);
    }

    private static byte[] SendProtocolMessage(ProtocolMessage message)
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message);
        return CreateFrame(payload);
    }

    private static byte[] CreateFrame(byte[] payload)
    {
        if (payload.Length > ProtocolLimits.MaximumFramePayloadSize)
        {
            throw new InvalidDataException(
                $"Serialized message is {payload.Length} bytes; the limit is {ProtocolLimits.MaximumFramePayloadSize}.");
        }

        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, HeaderSize), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    private static async Task SendToClientAsync(ClientConnection client, byte[] message)
    {
        await client.SendLock.WaitAsync();
        try
        {
            await client.Client.GetStream().WriteAsync(message);
        }
        finally
        {
            client.SendLock.Release();
        }
    }

}
