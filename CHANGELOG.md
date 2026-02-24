# Changelog

## [0.2.0] - 2026-02-25

### Added
- **Console I/O tools** — 4 new tools for interacting with the console window of debugged console applications
  - `console_read`: Read the console output buffer of a debugged process
  - `console_send_input`: Send text input (stdin) to a debugged console application
  - `console_send_keys`: Send special keys (Ctrl+C, Ctrl+Break, arrow keys, etc.) to a debugged console application
  - `console_get_info`: Get console buffer size, cursor position, and window information
- New **Console** tool category in tool help

## [0.1.5] - 2026-02-24

### Added
- **CWD-based .sln auto-detection** — StdioProxy walks up from the current working directory to find `.sln` files and auto-connects to the matching VS instance, eliminating the need for explicit `--sln` configuration in most cases
- `PortDiscovery.GetAllRunningInstances()` for efficient bulk lookup of all running VS instances
- When multiple `.sln` candidates are found, the `initialize` response includes hints for AI agent disambiguation
- CWD auto-detection documentation in README.md

## [0.1.4] - 2026-02-24

### Added
- **Multi-VS instance support** — StdioProxy `--sln` argument connects to the VS instance with a matching solution open
- `--pid` argument for StdioProxy to target a specific VS process
- Solution-aware port discovery with JSON port file format (`server.<PID>.port` now contains port + solution path)
- Port file automatically updates on solution open/close events
- Detailed tool list (77 tools across 19 categories) in README.md
- Multi-VS instance configuration guide in README.md

### Changed
- MCP instructions updated to reflect multi-instance support (removed single-instance limitation warning)

### Fixed
- Typo in GitHub URLs: `Unoffcial` → `Unofficial` (README.md, vsixmanifest)

## [0.1.3] - 2026-02-23

### Added
- `ui_right_click` tool for context menu support
- `ui_drag` tool for drag-and-drop operations
- Name-based click support and `waitMs` parameter for `ui_click`
- `maxChildren` / `maxElements` parameters for `ui_get_tree` to prevent timeouts on deep UI trees
- UI automation guidelines in MCP instructions (DPI scaling, popup bounds, drag hit-testing)
- Debug logging to `%LOCALAPPDATA%\VsMcp\debug.log` and `proxy-debug.log` for diagnosing request routing issues

### Fixed
- Tool call timeout not firing when VS UI thread is blocked
- `immediate_execute` failing on native stack frames — now searches across threads for a suitable managed frame
- StdioProxy not handling `notifications/cancelled` messages
- StdioProxy incorrectly sending responses for notification messages (no JSON-RPC id)

### Changed
- Rewrite `ui_find_elements` with TreeWalker for cancellable search and partial results on timeout
- Add window bounds validation to `ui_click`, `ui_right_click`, and `ui_drag` to prevent out-of-window clicks
- Add 10s default timeout to `RunOnUIThreadAsync` to prevent indefinite hangs
- Add 100ms delay after `SetForegroundWindow` in UI click/drag tools for window activation reliability
- Increase StdioProxy HTTP timeout to 90s with proper timeout error response
- Use explicit UTF-8 stdout StreamWriter in StdioProxy to avoid encoding issues

## [0.1.2] - 2026-02-22

### Fixed
- UI element search now finds elements across all process windows via `RootElement` + `ProcessId`
- Screenshot capture resized to fit API 2000px limit

### Changed
- Move UI Automation operations to background STA thread with timeout to prevent VS UI thread blocking
- Move screenshot capture off VS UI thread to `Task.Run`
- Improve MCP instructions to prevent common VS operation mistakes

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
