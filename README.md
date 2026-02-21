# VS MCP Server

**Model Context Protocol (MCP) server for Visual Studio** — Enables AI agents like Claude Code to control Visual Studio features including debugging, building, editing, and UI automation.

> This is a **beta release**. Feedback and bug reports are welcome on [GitHub Issues](https://github.com/dhq-boiler/Unoffcial-VS-MCP/issues).

> **100% AI Generated** — このプロジェクトのコードはすべて AI (Claude) によって生成されています。

## Features

VS MCP Server exposes **75 tools** across the following categories:

| Category | Tools | Description |
|----------|------:|-------------|
| General | 3 | Execute VS commands, get IDE status, view tool help |
| Solution & Project | 5 | Open/close solutions, list/inspect projects |
| Build | 5 | Build solution/project, clean, rebuild, get build errors |
| Editor | 7 | Open/close/read/write/edit files, find in files |
| Debugger | 14 | Start/stop/restart, attach, stepping, call stack, locals, threads, evaluate |
| Breakpoints | 7 | Set/remove/list, conditional, hit count, function breakpoints |
| Output & Diagnostics | 4 | Read/write output panes, error list, XAML binding errors |
| UI Automation | 8 | Capture screenshots, inspect UI trees, find/click/invoke elements |
| Advanced Debug | 22 | Watch, thread/process management, immediate window, registers, memory, parallel stacks |

## Requirements

- **Visual Studio 2022** (17.0 or later) — Community, Professional, or Enterprise edition
- **Windows** (amd64)
- **.NET Framework 4.8**
- **.NET 8.0 Runtime** (for the StdioProxy component)

## Installation

1. Install the extension from [Visual Studio Marketplace](https://marketplace.visualstudio.com/) or download the `.vsix` file from [Releases](https://github.com/dhq-boiler/Unoffcial-VS-MCP/releases)
2. Restart Visual Studio
3. The MCP server starts automatically when Visual Studio launches

## Setup — Connecting with Claude Code

Add the following to your Claude Code MCP configuration:

```json
{
  "mcpServers": {
    "vs-mcp": {
      "command": "%LOCALAPPDATA%\\VsMcp\\bin\\VsMcp.StdioProxy.exe"
    }
  }
}
```

## Architecture

The extension runs an HTTP-based MCP server inside Visual Studio. A lightweight StdioProxy bridges stdio-based MCP clients (like Claude Code) to the HTTP server, enabling seamless communication.

```
Claude Code  ──stdio──▶  StdioProxy  ──HTTP──▶  VS Extension
                          (relay)                (MCP server)
```

When Visual Studio is not running, the StdioProxy provides offline responses for basic protocol operations and returns cached tool definitions.

## Contributing

**Pull Request は受け付けておりません。**

要望や不具合の報告は [GitHub Issues](https://github.com/dhq-boiler/Unoffcial-VS-MCP/issues) にてお知らせください。

## License

[MIT License](LICENSE.txt)
