# Huddle

Huddle is a Windows desktop chat and collaborative whiteboard app built with C# and WPF.

## Features

- Host a chat server from the app or connect to a server by hostname or IPv4 address.
- Send chat messages and switch between the built-in rooms.
- Collaborate on a room's whiteboard with strokes and text objects, including add, edit, move, and delete operations.

## Requirements

- Windows
- .NET 9 SDK

## Run the app

From the repository root:

```powershell
dotnet run --project src\ChatUi\ChatUi.csproj
```

Choose **Host** to start a server on the current computer. The app also connects its own client to that server.

Choose **Connect** to join an existing server. Enter its hostname or IP address in the server-address field. The default is `localhost`.

The server listens on TCP port `5000`.

## Connect over a local network

1. Start Huddle on the host computer and choose **Host**.
2. Find the host's private IPv4 address with `ipconfig`.
3. On each other computer, start Huddle, enter that IPv4 address in the server-address field, and choose **Connect**.
4. If needed, allow inbound TCP port `5000` through the host computer's firewall for its **Private** network profile.

The server listens on all network interfaces. The protocol currently has no authentication or encryption, so use it only on a trusted local network. Do not expose the port to the internet or configure router port forwarding.

## Architecture

- `ChatUi` — WPF interface and view models.
- `ChatSession` — coordinates the client and, in Host mode, the local server.
- `ChatClient` — TCP connection, message framing, and protocol handling.
- `ChatServer` — accepts clients and manages rooms, chat broadcasts, and board state.
- `ChatProtocols` — shared JSON message contracts and validation.

Messages use a four-byte big-endian payload length followed by a UTF-8 JSON payload. The server and board state are in memory; messages and boards are not persisted across server restarts.

## Current limitations

- No user authentication, TLS encryption, or host discovery.
- Chat history and board state are not persisted.
- Rooms are currently preconfigured
