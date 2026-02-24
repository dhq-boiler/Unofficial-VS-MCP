# VS MCP Server

**Model Context Protocol (MCP) server for Visual Studio** — Enables AI agents like Claude Code to control Visual Studio features including debugging, building, editing, and UI automation.

> This is a **beta release**. Feedback and bug reports are welcome on [GitHub Issues](https://github.com/dhq-boiler/Unofficial-VS-MCP/issues).

> **100% AI Generated** — All code in this project was generated entirely by AI (Claude).

## Features

VS MCP Server exposes **77 tools** across the following categories:

| Category | Tools | Description |
|----------|------:|-------------|
| General | 3 | Execute VS commands, get IDE status, view tool help |
| Solution & Project | 5 | Open/close solutions, list/inspect projects |
| Build | 5 | Build solution/project, clean, rebuild, get build errors |
| Editor | 7 | Open/close/read/write/edit files, find in files |
| Debugger | 14 | Start/stop/restart, attach, stepping, call stack, locals, threads, evaluate |
| Breakpoints | 7 | Set/remove/list, conditional, hit count, function breakpoints |
| Output & Diagnostics | 4 | Read/write output panes, error list, XAML binding errors |
| UI Automation | 10 | Capture screenshots, inspect UI trees, find/click/right-click/drag/invoke elements |
| Advanced Debug | 22 | Watch, thread/process management, immediate window, registers, memory, parallel stacks |

### Tool Details

#### General

| Tool | Description |
|------|-------------|
| `execute_command` | Execute a Visual Studio command by name |
| `get_status` | Get the current Visual Studio status including solution state, active document, and debugger mode |
| `get_help` | Get a categorized list of all available vs-mcp tools with descriptions |

#### Solution

| Tool | Description |
|------|-------------|
| `solution_open` | Open a solution or project file in Visual Studio |
| `solution_close` | Close the current solution |
| `solution_info` | Get information about the currently open solution |

#### Project

| Tool | Description |
|------|-------------|
| `project_list` | List all projects in the current solution |
| `project_info` | Get detailed information about a specific project |

#### Build

| Tool | Description |
|------|-------------|
| `build_solution` | Build the entire solution in Visual Studio |
| `build_project` | Build a specific project |
| `clean` | Clean the solution build output |
| `rebuild` | Clean and rebuild the entire solution |
| `get_build_errors` | Get the list of build errors and warnings from the Visual Studio Error List |

#### Editor

| Tool | Description |
|------|-------------|
| `file_open` | Open a file in the Visual Studio editor |
| `file_close` | Close a file in the editor |
| `file_read` | Read the contents of a file with optional line range |
| `file_write` | Write content to a file, replacing its entire contents |
| `file_edit` | Edit a file by replacing a specific text occurrence with new text |
| `get_active_document` | Get information about the currently active document in the editor |
| `find_in_files` | Search for text in files within the solution |

#### Debugger

| Tool | Description |
|------|-------------|
| `debug_start` | Start debugging the startup project (equivalent to F5) |
| `debug_stop` | Stop debugging the current session |
| `debug_restart` | Restart debugging the current session |
| `debug_attach` | Attach the debugger to a running process by name or PID |
| `debug_break` | Break (pause) the debugger at the current execution point |
| `debug_continue` | Continue (resume) execution after a breakpoint or break |
| `debug_step_over` | Step over the current line (F10) |
| `debug_step_into` | Step into the current function call (F11) |
| `debug_step_out` | Step out of the current function (Shift+F11) |
| `debug_get_callstack` | Get the current call stack of the active thread |
| `debug_get_locals` | Get the local variables in the current stack frame |
| `debug_get_threads` | Get all threads in the current debug session |
| `debug_get_mode` | Get the current debugger mode (Design, Running, or Break) |
| `debug_evaluate` | Evaluate an expression in the current debug context (only works in break mode) |

#### Breakpoint

| Tool | Description |
|------|-------------|
| `breakpoint_set` | Set a breakpoint at a specific file and line |
| `breakpoint_set_conditional` | Set a conditional breakpoint at a specific file and line |
| `breakpoint_remove` | Remove a breakpoint at a specific file and line |
| `breakpoint_list` | List all breakpoints in the current solution |
| `breakpoint_enable` | Enable or disable a breakpoint at a specific file and line |
| `breakpoint_set_hitcount` | Set a breakpoint with a hit count condition at a specific file and line |
| `breakpoint_set_function` | Set a breakpoint on a function by name |

#### Output & Diagnostics

| Tool | Description |
|------|-------------|
| `output_write` | Write text to a Visual Studio Output window pane |
| `output_read` | Read the content of a Visual Studio Output window pane |
| `error_list_get` | Get all items from the Visual Studio Error List window |
| `diagnostics_binding_errors` | Extract XAML/WPF binding errors from the Debug output pane |

#### UI Automation

| Tool | Description |
|------|-------------|
| `ui_capture_window` | Capture a screenshot of the debugged application's main window as a base64 PNG image |
| `ui_capture_region` | Capture a screenshot of a specific region of the debugged application's window |
| `ui_get_tree` | Get the UI element tree of the debugged application's main window |
| `ui_find_elements` | Find UI elements matching specified criteria in the debugged application |
| `ui_get_element` | Get detailed properties of a specific UI element by its AutomationId |
| `ui_click` | Click a UI element by AutomationId, Name, or screen coordinates |
| `ui_right_click` | Right-click a UI element by AutomationId, Name, or screen coordinates |
| `ui_drag` | Perform a drag-and-drop operation from start coordinates to end coordinates |
| `ui_set_value` | Set the value of a UI element using ValuePattern |
| `ui_invoke` | Invoke the default action on a UI element using InvokePattern |

#### Watch

| Tool | Description |
|------|-------------|
| `watch_add` | Add a watch expression and return its current value (only works in break mode) |
| `watch_remove` | Remove a watch expression by value or index |
| `watch_list` | List all watch expressions with their current values |

#### Thread

| Tool | Description |
|------|-------------|
| `thread_switch` | Switch the active (current) thread by thread ID |
| `thread_freeze` | Freeze a thread so it does not execute when the debugger continues |
| `thread_thaw` | Thaw (unfreeze) a frozen thread so it resumes execution |
| `thread_get_callstack` | Get the call stack of a specific thread by ID |

#### Process

| Tool | Description |
|------|-------------|
| `process_list_debugged` | List all processes currently being debugged |
| `process_list_local` | List local processes available for attaching the debugger |
| `process_detach` | Detach the debugger from a specific process |
| `process_terminate` | Terminate a process being debugged |

#### Immediate

| Tool | Description |
|------|-------------|
| `immediate_execute` | Execute an expression with side effects in the debugger context (like the Immediate Window) |

#### Module

| Tool | Description |
|------|-------------|
| `module_list` | List all loaded modules (DLLs/assemblies) in the current debug session |

#### Register

| Tool | Description |
|------|-------------|
| `register_list` | Get values of common CPU registers (works best in native or mixed-mode debugging) |
| `register_get` | Get the value of a specific CPU register by name |

#### Exception

| Tool | Description |
|------|-------------|
| `exception_settings_get` | Get exception break settings |
| `exception_settings_set` | Configure when to break on a specific exception type |

#### Memory

| Tool | Description |
|------|-------------|
| `memory_read` | Read memory bytes at a given address expression |
| `memory_read_variable` | Get a variable's memory address and raw byte representation |

#### Parallel

| Tool | Description |
|------|-------------|
| `parallel_stacks` | Get all threads' call stacks in a tree view, grouping threads that share common stack frames |
| `parallel_watch` | Evaluate the same expression on all threads and compare results |
| `parallel_tasks_list` | List TPL (Task Parallel Library) task information |

## Requirements

- **Visual Studio 2022** (17.0 or later) — Community, Professional, or Enterprise edition
- **Windows** (amd64)
- **.NET Framework 4.8**
- **.NET 8.0 Runtime** (for the StdioProxy component)

## Installation

1. Install the extension from [Visual Studio Marketplace](https://marketplace.visualstudio.com/) or download the `.vsix` file from [Releases](https://github.com/dhq-boiler/Unofficial-VS-MCP/releases)
2. Restart Visual Studio
3. The MCP server starts automatically when Visual Studio launches

## Setup — Connecting with Claude Code

> **Note:** `%LOCALAPPDATA%` is not recognized in bash. You must specify the full absolute path (e.g. `C:\Users\<USERNAME>\AppData\Local\...`).

**Option A** — CLI command:

```
claude mcp add vs-mcp -- "C:\Users\<USERNAME>\AppData\Local\VsMcp\bin\VsMcp.StdioProxy.exe"
```

**Option B** — Manual configuration (add to your MCP config JSON):

```json
{
  "mcpServers": {
    "vs-mcp": {
      "command": "C:\\Users\\<USERNAME>\\AppData\\Local\\VsMcp\\bin\\VsMcp.StdioProxy.exe"
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

## Multiple VS Instances

VS MCP Server supports multiple Visual Studio instances running simultaneously. Each VS instance registers itself via a port file (`%LOCALAPPDATA%\VsMcp\server.<PID>.port`) containing its HTTP port and the currently open solution path.

### Instance Selection

StdioProxy selects which VS instance to connect to based on command-line arguments:

| Argument | Behavior |
|----------|----------|
| *(none)* | Auto-detect — walks up from the current working directory to find `.sln` files and connects to the matching VS instance. Falls back to the most recently started VS instance if no `.sln` is found. |
| `--sln <path>` | Connects to the VS instance that has the specified solution open |
| `--pid <pid>` | Connects to the VS instance with the specified process ID |

### CWD-based Auto-Detection

When no `--sln` or `--pid` argument is provided, StdioProxy automatically discovers `.sln` files by walking up from the current working directory. This means that in most cases, **no explicit configuration is needed** — simply launching Claude Code from within a project directory is enough.

| Scenario | Behavior |
|----------|----------|
| 1 `.sln` found | Automatically connects to the VS instance with that solution |
| Multiple `.sln` found, 1 open in VS | Connects to the matching VS instance |
| Multiple `.sln` found, multiple open in VS | Connects to the closest match (nearest to CWD) and includes a hint in the `initialize` response |
| Multiple `.sln` found, none open in VS | Falls back to default behavior and includes a hint prompting the user to choose |
| No `.sln` found | Falls back to the most recently started VS instance |

### Configuration Examples

**Single instance** (auto-detect, no extra config needed):

```
claude mcp add vs-mcp -- "C:\Users\<USERNAME>\AppData\Local\VsMcp\bin\VsMcp.StdioProxy.exe"
```

**Multiple instances** (explicit solution-based — useful when CWD auto-detection is not sufficient):

```json
{
  "mcpServers": {
    "vs-mcp-frontend": {
      "command": "C:\\Users\\<USERNAME>\\AppData\\Local\\VsMcp\\bin\\VsMcp.StdioProxy.exe",
      "args": ["--sln", "C:\\Projects\\Frontend\\Frontend.sln"]
    },
    "vs-mcp-backend": {
      "command": "C:\\Users\\<USERNAME>\\AppData\\Local\\VsMcp\\bin\\VsMcp.StdioProxy.exe",
      "args": ["--sln", "C:\\Projects\\Backend\\Backend.sln"]
    }
  }
}
```

### Reconnection

- If VS is restarted, StdioProxy automatically reconnects on the next `tools/call` request.
- Stale port files from crashed or closed VS instances are cleaned up automatically during discovery.

## Contributing

**Pull Requests are not accepted.**

For feature requests and bug reports, please use [GitHub Issues](https://github.com/dhq-boiler/Unofficial-VS-MCP/issues).

## License

[MIT License](LICENSE.txt)
