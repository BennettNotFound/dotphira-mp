# DotPmp Server

[English](#english) | [中文](#中文)

---

## English

DotPmp is a highly efficient custom multiplayer game server written in C# and .NET 8.0. It provides both a backend WebSocket server for realtime gameplay and a Web API (including a WebSocket service) for administration and real-time monitoring.

### Features

- **Real-time Gameplay:** TCP-based fast protocol serialization.
- **WebSocket & HTTP API:** For administrative actions and fetching room metrics.
- **Room Management:** Easy creation, disbanding, and tracking of rooms.
- **Contest Mode:** Allows for precise tournament conditions with whitelist support.
- **Replay Recording:** Stores match details and gameplay.

### Requirements

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher.

### Configuration

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

### Running the Server

Run the server from the source using the .NET CLI:

```sh
dotnet run --project DotPmp.Server/DotPmp.Server.csproj
```

### License & Non-binding Declaration

This software is released under the **Apache 2.0 License**.

**Non-binding Declaration:**
本软件虽然采用 Apache 2.0 协议，但是我强烈建议不要恶意倒卖原版程序，如果你硬要卖我也没招 (Although this software is licensed under Apache 2.0, I strongly advise against maliciously reselling the original program. If you insist on selling it, there's nothing I can do).

---

## 中文

DotPmp 是一个基于 C# 和 .NET 8.0 编写的高效自定义多人联机游戏服务端。它提供了一个用于实时游戏的 TCP 后端，以及一个用于管理和实时监控的 Web API（包含 WebSocket 服务）。

### 特性

- **实时游戏:** 基于 TCP 的快速协议序列化。
- **WebSocket & HTTP API:** 用于管理操作和获取房间数据。
- **房间管理:** 轻松创建、解散和追踪房间。
- **比赛模式:** 允许设置包含白名单支持的精确锦标赛条件。
- **回放记录:** 存储对局详情和游戏录像。

### 运行环境需求

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或更高版本。

### 配置

服务器配置可以通过基目录下的 `config.json` 文件或通过环境变量提供。

`config.json` 示例：
```json
{
  "GamePort": 12346,
  "HttpPort": 12347,
  "ServerName": "DotPmp 服务端",
  "WelcomeMessage": "[L]欢迎来到L的联机服务器!!!",
  "HttpService": true,
  "AdminToken": "your-admin-token",
  "AdminDataPath": "admin_data.json"
}
```

### 运行服务端

使用 .NET CLI 从源码运行服务端：

```sh
dotnet run --project DotPmp.Server/DotPmp.Server.csproj
```

### 协议与声明

本项目采用 **Apache 2.0 协议** 开源。

**非约束性声明 (Non-binding Declaration):**
本软件虽然采用 Apache 2.0 协议，但是我强烈建议不要恶意倒卖原版程序，如果你硬要卖我也没招。
