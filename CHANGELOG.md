# Changelog

## [0.1.1] - 2026-02-22

### Fixed
- `debug_restart` failing with COM exception (`HRESULT E_FAIL`) without actually restarting the application. Replaced `Debug.Restart` command with explicit Stop → wait for DesignMode → Start sequence for reliability.

## [0.1.0] - 2026-02-22 (Beta)

Initial beta release of VS MCP Server.

### Added
- **75 MCP tools** for Visual Studio automation
- **General tools**: execute commands, get IDE status, tool help
- **Solution & Project tools**: open/close solutions, list/inspect projects
- **Build tools**: build solution/project, clean, rebuild, get build errors
- **Editor tools**: open/close/read/write/edit files, find in files
- **Debugger tools**: start/stop/restart debugging, attach, stepping, call stack, locals, threads, expression evaluation
- **Breakpoint tools**: set/remove/list breakpoints, conditional, hit count, function breakpoints
- **Output tools**: read/write output panes, error list
- **UI Automation tools**: capture screenshots, inspect UI trees, find/click/invoke elements
- **Advanced debug tools**: watch variables, thread management, process management, immediate window, module listing, CPU registers, exception settings, memory reading, parallel stacks/watch/tasks
- **Diagnostics tools**: XAML binding error viewer
- **StdioProxy**: stdio-to-HTTP relay for Claude Code integration
- **Offline mode**: StdioProxy returns cached tool definitions when Visual Studio is not running
- **VS 2022/2026 support**: Compatible with Visual Studio 17.x and 18.x
- Output pane name localization (Japanese aliases)
