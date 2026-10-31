namespace ChatClient;

using System.Net.Sockets;
using System.Buffers.Binary;
using ChatProtocols;
using System.Threading.Tasks;
using System.Text.Json;

public class ChatClient : IChatClient
{
    private const int HostPort = 5000;
    private const int HeaderSize = sizeof(int);

    readonly TcpClient client;

    NetworkStream? stream;

    private Task? receiveTask;
    private volatile bool disconnectRequested;

    public event Action<ProtocolMessage>? MessageReceived;

    public event Action<ProtocolMessage>? MessageProcessError;

    public event Action<string>? NetworkError;

    public ChatClient()
    {
        client = new TcpClient();
    }

    public async Task connect(string serverHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverHost);
        await client.ConnectAsync(serverHost.Trim(), HostPort);
        stream = client.GetStream();
        receiveTask = ReceiveMessagesSeqAsync(stream);
    }

    public async Task DisconnectAsync()
    {
        disconnectRequested = true;
        client.Close();

        if (receiveTask is not null)
        {
            await receiveTask;
        }
    }

    public async Task users()
    {
        await sendMessge("/users");
    }

    public async Task sendChat(string message)
    {
        await sendMessge(message);
    }

    public async Task joinRoom(string room)
    {
        await sendMessge("/join " + room);
    }

    public async Task leaveRoom(string room)
    {
        await sendMessge("/leave " + room);
    }


    public async Task rooms()
    {
        await sendMessge("/rooms");
    }

    public Task requestBoardSnapshot(string room, string? requestId = null)
    {
        return SendBoardAction(CreateBoardAction(MessageTypes.BoardSnapshotRequest,room,null,requestId));
    }

    public Task addStroke(string room, BoardStroke stroke, string? requestId = null)
    {
        return SendBoardAction(CreateBoardAction(MessageTypes.BoardStrokeAdd,room,JsonSerializer.SerializeToElement(stroke),requestId));
    }

    public Task addText(string room, BoardText text, string? requestId = null)
    {
        return SendBoardAction(CreateBoardAction(MessageTypes.BoardTextAdd,room,JsonSerializer.SerializeToElement(text),requestId));
    }

    public Task editText(string room, EditBoardTextPayload text, string? requestId = null)
    {
        return SendBoardAction(CreateBoardAction(MessageTypes.BoardTextEdit,room,JsonSerializer.SerializeToElement(text),requestId));
    }

    public Task moveBoardObject(string room, MoveBoardObjectPayload move, string? requestId = null)
    {
        return SendBoardAction(CreateBoardAction(MessageTypes.BoardObjectMove,room,JsonSerializer.SerializeToElement(move),requestId));
    }

    public Task deleteBoardObject(string room, DeleteBoardObjectPayload delete, string? requestId = null)
    {
        return SendBoardAction(CreateBoardAction(MessageTypes.BoardObjectDelete,room,JsonSerializer.SerializeToElement(delete),requestId));
    }

    private static ProtocolMessage CreateBoardAction(string type,string room,JsonElement? payload,string? requestId)
    {
        if (requestId is not null && string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("A provided request ID cannot be empty.", nameof(requestId));
        }

        return new ProtocolMessage
        {
            Version = ProtocolMessage.CurrentVersion,
            Type = type,
            Room = room,
            RequestId = requestId ?? Guid.NewGuid().ToString("N"),
            Payload = payload
        };
    }

    public async Task SendBoardAction(ProtocolMessage message)
    {
         await sendLock.WaitAsync();
        try
        {

            var currentStream = stream ?? throw new InvalidOperationException("Connect before sending.");
            await currentStream.WriteAsync(SendMessage(message));
        }
        catch (IOException ex)
        {
            ReportNetworkError(ex);
        }
        catch (SocketException ex)
        {
            ReportNetworkError(ex);
        }
        catch (ObjectDisposedException ex)
        {
            ReportNetworkError(ex);
        }
        catch (InvalidDataException ex)
        {
            MessageProcessError?.Invoke(ProtocolMessageValidator.CreateError("invalid_payload_size",ex.Message));
        }
        finally
        {
            sendLock.Release();
        }
    }


    private readonly SemaphoreSlim sendLock = new(1, 1);
    private async Task sendMessge(string input)
    {
        var protocolMessage = CreateOutgoingMessage(input);
        if (protocolMessage is null)
        {
            return;
        }
        await sendLock.WaitAsync();
        try
        {

            var currentStream = stream ?? throw new InvalidOperationException("Connect before sending.");
            await currentStream.WriteAsync(SendMessage(protocolMessage));
        }
        catch (IOException ex)
        {
            ReportNetworkError(ex);
        }
        catch (SocketException ex)
        {
            ReportNetworkError(ex);
        }
        catch (ObjectDisposedException ex)
        {
            ReportNetworkError(ex);
        }
        catch (InvalidDataException ex)
        {
            MessageProcessError?.Invoke(ProtocolMessageValidator.CreateError("invalid_payload_size",ex.Message));
        }
        finally
        {
            sendLock.Release();
        }
    }

    private void ReportNetworkError(Exception exception)
    {
        if (!disconnectRequested)
        {
            NetworkError?.Invoke(exception.Message);
        }
    }



    private static ProtocolMessage? CreateOutgoingMessage(string input)
    {
        if (!input.StartsWith('/'))
        {
            return new ProtocolMessage
            {
                Version = ProtocolMessage.CurrentVersion,
                Type = MessageTypes.Chat,
                Text = input
            };
        }

        var commandArgs = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = commandArgs[0].ToLowerInvariant();

        if (command == "/join" && commandArgs.Length == 2)
        {
            return new ProtocolMessage
            {
                Version = ProtocolMessage.CurrentVersion,
                Type = MessageTypes.Join,
                Room = commandArgs[1]
            };
        }

        if (command == "/leave" && commandArgs.Length == 2)
        {
            return new ProtocolMessage
            {
                Version = ProtocolMessage.CurrentVersion,
                Type = MessageTypes.Leave,
                Room = commandArgs[1]
            };
        }

        if (command == "/rooms" && commandArgs.Length == 1)
        {
            return new ProtocolMessage
            {
                Version = ProtocolMessage.CurrentVersion,
                Type = MessageTypes.ListRooms
            };
        }

        if (command == "/users" && commandArgs.Length == 1)
        {
            return new ProtocolMessage
            {
                Version = ProtocolMessage.CurrentVersion,
                Type = MessageTypes.ListUsers
            };
        }

        return InvalidCommand();
    }
   
    private static ProtocolMessage? InvalidCommand()
    {
        //Console.WriteLine("Unknown command or invalid arguments. Commands: /join <room>, /leave <room>, /rooms, /users, /quit.");
        return null;
    }


    private async Task ReceiveMessagesSeqAsync(NetworkStream stream)
    {
        try
        {

            while (true)
            {
                var header = new byte[HeaderSize];
                var headerBytesRead = 0;
                var bytesLefToRead = 0;
                while (headerBytesRead < HeaderSize)
                {
                    bytesLefToRead = HeaderSize - headerBytesRead;
                    var bytes = await stream.ReadAsync(header, headerBytesRead, bytesLefToRead);
                    if (bytes == 0)
                    {
                        return;
                    }

                    headerBytesRead += bytes;
                }

                var payloadSize = BinaryPrimitives.ReadInt32BigEndian(header);
                if (payloadSize < 0 || payloadSize > ProtocolLimits.MaximumFramePayloadSize)
                {
                    MessageProcessError?.Invoke(ProtocolMessageValidator.CreateError("invalid_payload_size",$"Server declared payload length {payloadSize}; allowed range is 0..{ProtocolLimits.MaximumFramePayloadSize}."));
                    return;
                }

                var payload = new byte[payloadSize];
                var payloadBytesRead = 0;
                bytesLefToRead = 0;
                while (payloadBytesRead < payloadSize)
                {
                    bytesLefToRead = payloadSize - payloadBytesRead;
                    var bytes = await stream.ReadAsync(payload, payloadBytesRead, bytesLefToRead);

                    if (bytes == 0)
                    {
                        return;
                    }

                    payloadBytesRead += bytes;
                }
                ProcessMessage(payload);
            }
        }
        catch (IOException ex)
        {
            ReportNetworkError(ex);
        }
        catch (SocketException ex)
        {
            ReportNetworkError(ex);
        }
        catch (ObjectDisposedException ex)
        {
            ReportNetworkError(ex);
        }
    }

    void ProcessMessage(byte[] payload)
    {
        if (!ProtocolMessageValidator.TryDeserialize(payload, out var message, out var error))
        {
            MessageProcessError?.Invoke(error!);
            return;
        }

        MessageReceived?.Invoke(message!);
    }
    private static byte[] SendMessage(ProtocolMessage message)
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > ProtocolLimits.MaximumFramePayloadSize)
        {
            throw new InvalidDataException($"Serialized message is {payload.Length} bytes; the limit is {ProtocolLimits.MaximumFramePayloadSize}.");
        }

        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, HeaderSize), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

   
}
