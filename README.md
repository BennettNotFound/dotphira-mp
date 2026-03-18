# DotPmp Server

DotPmp is a highly efficient custom multiplayer game server written in C# and .NET 8.0, originally designed to interface with game clients for Phira. It provides both a backend WebSocket server for realtime gameplay and a Web API (including a WebSocket service) for administration and real-time monitoring.

## Features

- **Real-time Gameplay:** TCP-based fast protocol serialization.
- **WebSocket & HTTP API:** For administrative actions and fetching room metrics.
- **Room Management:** Easy creation, disbanding, and tracking of rooms.
- **Contest Mode:** Allows for precise tournament conditions with whitelist support.
- **Replay Recording:** Stores match details and gameplay.

## Requirements

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher.

## Configuration

The server configuration can be supplied via a `config.json` file in the base directory or through environment variables.

Example `config.json`:
```json
{
  "GamePort": 12346,
  "HttpPort": 12347,
  "ServerName": "DotPmp Server",
  "WelcomeMessage": "[L] Welcome to the Multiplayer Server!!!",
  "HttpService": true,
  "AdminToken": "your-admin-token",
  "AdminDataPath": "admin_data.json"
}
```

## Running the Server

Run the server from the source using the .NET CLI:

```sh
dotnet run --project DotPmp.Server/DotPmp.Server.csproj
```

## License

This software is released under the **Apache 2.0 License**.

### 非约束性声明 (Non-binding Declaration)
本软件虽然采用 Apache 2.0 协议，但是我强烈建议不要恶意倒卖原版程序，如果你硬要卖我也没招。
