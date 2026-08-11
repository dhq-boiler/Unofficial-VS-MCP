# VS MCP Server

**Model Context Protocol (MCP) server for Visual Studio** — Enables AI agents like Claude Code to control Visual Studio features including debugging, building, editing, and UI automation.

> This is a **beta release**. Feedback and bug reports are welcome on [GitHub Issues](https://github.com/dhq-boiler/Unofficial-VS-MCP/issues).

> **100% AI Generated** — All code in this project was generated entirely by AI (Claude).

## Features

VS MCP Server exposes **115 tools** across the following categories:

| Category | Tools | Description |
|----------|------:|-------------|
| General | 7 | Execute VS commands, get IDE status, view tool help, toggle focus guard, audit foreground transitions |
| Solution & Project | 5 | Open/close solutions, list/inspect projects |
| Build | 6 | Build solution/project, clean, rebuild, get build errors, switch configuration |
| Editor | 7 | Open/close/read/write/edit files, find in files |
| Edit Preview | 4 | Diff preview, approve/reject edits with VS diff viewer |
| Code Navigation | 3 | Go to definition, find references, go to implementation |
| Solution Explorer | 6 | Add/remove projects, files, and references |
| Debugger | 13 | Start/stop/restart, attach, stepping, call stack, locals, threads, evaluate, run without debugging |
| Breakpoints | 4 | Set/remove/list, enable/disable breakpoints |
| Output & Diagnostics | 5 | Read/write/clear output panes, error list, XAML binding errors |
| Console | 3 | Read console output, send input/keys, get console info for debugged apps |
| UI Automation | 10 | Capture screenshots, inspect UI trees, find/click/right-click/drag/invoke elements |
| Web | 12 | Browser automation via Chrome CDP and Firefox RDP — navigation, DOM, console, network, screenshots |
| Test | 3 | Discover, run, and view test results |
| NuGet | 5 | Search, install, update, remove NuGet packages |
| Advanced Debug | 20 | Watch, thread/process management, immediate window, registers, memory, parallel stacks |

### Tool Details

#### General

| Tool | Description |
|------|-------------|
| `execute_command` | Execute a Visual Studio command by name |
| `get_status` | Get the current Visual Studio status including solution state, active document, and debugger mode |
| `get_help` | Get a categorized list of all available vs-mcp tools with descriptions |
| `focus_guard_get` | Get whether the focus guard is currently on |
| `focus_guard_set` | Turn the focus guard on or off — when on, MCP-driven builds, output writes, and debug launches do not steal foreground focus from the currently focused application, and VS is kept minimized across `debug_start` if it was already minimized |
| `focus_guard_audit_read` | Read recent foreground-window transitions from a bounded ring buffer — each entry names the process/window that gained (and lost) foreground, with timestamp and whether the guard was on. Use this to identify what actually stole focus. |
| `focus_guard_audit_clear` | Clear the buffered foreground-transition events so a subsequent audit read starts from a clean slate |

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
| `build_solution` | Build the entire solution (auto-stops debugger if active) |
| `build_project` | Build a specific project (auto-stops debugger if active) |
| `clean` | Clean the solution build output (auto-stops debugger if active) |
| `rebuild` | Clean and rebuild the entire solution (auto-stops debugger if active) |
| `get_build_errors` | Get the list of build errors and warnings from the Visual Studio Error List |
| `build_configuration` | Get or set the active solution build configuration and platform (e.g. Debug/Release, Any CPU/x64) |

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

#### Edit Preview

| Tool | Description |
|------|-------------|
| `edit_preview` | Show a diff preview of proposed changes in VS and create a pending edit for approval |
| `edit_approve` | Approve a pending edit and apply the changes to the file |
| `edit_reject` | Reject a pending edit and discard the changes |
| `edit_list_pending` | List all pending edit previews with their status |

#### Code Navigation

| Tool | Description |
|------|-------------|
| `code_goto_definition` | Navigate to the definition of a symbol at the specified position |
| `code_find_references` | Find all references of a symbol at the specified position |
| `code_goto_implementation` | Navigate to the implementation of an interface or abstract member |

#### Solution Explorer

| Tool | Description |
|------|-------------|
| `solution_add_project` | Add an existing project to the current solution |
| `solution_remove_project` | Remove a project from the current solution |
| `project_add_file` | Add an existing file to a project |
| `project_remove_file` | Remove a file from a project |
| `project_add_reference` | Add a project-to-project reference |
| `project_remove_reference` | Remove a reference from a project |

#### Debugger

| Tool | Description |
|------|-------------|
| `debug_start` | Start debugging the startup project (equivalent to F5) |
| `debug_start_without_debugging` | Start the startup project without the debugger attached (equivalent to Ctrl+F5) |
| `debug_stop` | Stop debugging the current session |
| `debug_restart` | Restart debugging the current session |
| `debug_attach` | Attach the debugger to a running process by name or PID |
| `debug_break` | Break (pause) the debugger at the current execution point |
| `debug_continue` | Continue (resume) execution after a breakpoint or break |
| `debug_step` | Step through code (direction: over, into, or out) |
| `debug_get_callstack` | Get the current call stack of the active thread |
| `debug_get_locals` | Get the local variables in the current stack frame |
| `debug_get_threads` | Get all threads in the current debug session |
| `debug_get_mode` | Get the current debugger mode (Design, Running, or Break) |
| `debug_evaluate` | Evaluate an expression in the current debug context (only works in break mode) |

#### Breakpoint

| Tool | Description |
|------|-------------|
| `breakpoint_set` | Set a breakpoint (location, conditional, hit count, or function breakpoint) |
| `breakpoint_remove` | Remove a breakpoint at a specific file and line |
| `breakpoint_list` | List all breakpoints in the current solution |
| `breakpoint_enable` | Enable or disable a breakpoint at a specific file and line |

#### Output & Diagnostics

| Tool | Description |
|------|-------------|
| `output_write` | Write text to a Visual Studio Output window pane |
| `output_read` | Read the content of a Visual Studio Output window pane (supports regex pattern filtering) |
| `output_clear` | Clear the content of a Visual Studio Output window pane |
| `error_list_get` | Get all items from the Visual Studio Error List window |
| `diagnostics_binding_errors` | Extract XAML/WPF binding errors from the Debug output pane |

#### Console

| Tool | Description |
|------|-------------|
| `console_read` | Read the console output buffer of a debugged console application |
| `console_send` | Send text input or special keys to the console of a debugged console application |
| `console_get_info` | Get console buffer size, cursor position, and window information of a debugged console application |

#### UI Automation

| Tool | Description |
|------|-------------|
| `ui_capture_window` | Capture a screenshot of the debugged application's main window (uses WGC for reliable capture even when occluded; works during debug break) |
| `ui_capture_region` | Capture a screenshot of a specific region of the debugged application's window |
| `ui_snapshot` | Capture a compact semantic snapshot (pruned UIA tree + screenshot + focus) in a single call; optimized for LLM-driven UI testing |
| `ui_get_tree` | Get the UI element tree of the debugged application's main window |
| `ui_find_elements` | Find UI elements matching specified criteria (supports `exact`/`contains`/`regex` match modes, `hasPattern` filter, and `ancestorAutomationId` scoping) |
| `ui_get_element` | Get detailed properties of a specific UI element by its AutomationId |
| `ui_click` | Click a UI element by AutomationId, Name, or screen coordinates |
| `ui_double_click` | Double-click a UI element by AutomationId, Name, or screen coordinates |
| `ui_right_click` | Right-click a UI element by AutomationId, Name, or screen coordinates |
| `ui_drag` | Perform a drag-and-drop operation from start coordinates to end coordinates |
| `ui_set_value` | Set the value of a UI element using ValuePattern |
| `ui_invoke` | Invoke the default action on a UI element using InvokePattern |
| `ui_send_keys` | Send keyboard input (shortcuts like `ctrl+f`, text typing) to the debugged application |
| `ui_wait_for_element` | Wait until a UI element reaches a given state (`appears`/`disappears`/`enabled`/`focused`) with a timeout |
| `ui_wait_idle` | Wait until the UI Automation tree stops changing for a quiet period (useful after triggering async UI updates) |

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
| `thread_set_frozen` | Freeze or thaw a thread |
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
| `memory_read` | Read memory bytes at an address expression or get a variable's memory representation |

#### Parallel

| Tool | Description |
|------|-------------|
| `parallel_stacks` | Get all threads' call stacks in a tree view, grouping threads that share common stack frames |
| `parallel_watch` | Evaluate the same expression on all threads and compare results |
| `parallel_tasks_list` | List TPL (Task Parallel Library) task information |

#### Web

| Tool | Description |
|------|-------------|
| `web_connect` | Connect to a browser for web debugging (Chrome/Edge via CDP, Firefox via RDP) |
| `web_disconnect` | Disconnect from the browser connection |
| `web_status` | Get the current browser connection status including console/network message counts |
| `web_navigate` | Navigate the browser to a URL |
| `web_screenshot` | Capture a screenshot of the current page |
| `web_dom_get` | Get the DOM tree of the current page with configurable depth |
| `web_dom_query` | Query DOM elements using a CSS selector |
| `web_console` | Manage browser console messages (enable/get/clear) |
| `web_js_execute` | Execute JavaScript in the browser page context |
| `web_network` | Manage network monitoring (enable/get/clear) |
| `web_element_click` | Click a DOM element found by CSS selector |
| `web_element_set_value` | Set the value of an input element found by CSS selector |

#### Test

| Tool | Description |
|------|-------------|
| `test_discover` | Discover all tests in the solution or a specific project |
| `test_run` | Run tests and get results with optional filtering by test name/category |
| `test_results` | Get detailed results from the last test run (or a specific TRX file) |

#### NuGet

| Tool | Description |
|------|-------------|
| `nuget_list` | List installed NuGet packages for a specific project |
| `nuget_search` | Search for NuGet packages on NuGet.org |
| `nuget_install` | Install a NuGet package into a project |
| `nuget_update` | Update a NuGet package to a specific version |
| `nuget_uninstall` | Remove a NuGet package from a project |

## Requirements

- **Visual Studio 2019** (16.0 or later), **Visual Studio 2022**, or **Visual Studio 2026** — Community, Professional, or Enterprise edition
- **Windows**
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

### Tool Filtering

Use the `--tools` argument to load only the tool categories you need. This reduces the number of tools exposed to the AI agent, which can improve token efficiency and response relevance.

| Preset | Categories |
|--------|------------|
| `core` | General, Solution, Project, Build, Editor, EditPreview, Output, Navigation, NuGet, SolutionExplorer, Test |
| `debug` | Debugger, Breakpoint, Watch, Thread, Process, Immediate, Module, Register, Exception, Memory, Parallel, Diagnostics, Console |
| `web` | Web |
| `ui` | UI |

**Examples:**

```
claude mcp add vs-mcp -- "C:\Users\<USERNAME>\AppData\Local\VsMcp\bin\VsMcp.StdioProxy.exe" --tools core,debug
```

```json
{
  "mcpServers": {
    "vs-mcp": {
      "command": "C:\\Users\\<USERNAME>\\AppData\\Local\\VsMcp\\bin\\VsMcp.StdioProxy.exe",
      "args": ["--tools", "core,debug"]
    }
  }
}
```

You can also specify individual category names (e.g. `--tools General,Build,Debugger`). Omitting `--tools` loads all tools.

### Reconnection

- If VS is restarted, StdioProxy automatically reconnects on the next `tools/call` request.
- Stale port files from crashed or closed VS instances are cleaned up automatically during discovery.

## Showcase: hands-off AI-driven dogfood

With `focus_guard_set enabled=true`, an AI agent can drive a full
build → debug → UI verification cycle in Visual Studio on one monitor
while the developer keeps a fullscreen application (e.g. a game) running
on another — no focus theft, no window flicker, no context switch.

Paired with **[jidodebugger](https://jidodebugger.dhq-boiler.dev/)**
— an upstream MCP server that sits above vs-mcp's desktop-app UIA
layer and supersedes it with WPF-native automation (bridge injection
into the debuggee, direct `ICommand` invocation, and
ViewModel-property waits via `wpf_bridge_inject` /
`wpf_invoke_command` / `wpf_wait_for`, on top of the raw UIA tools
vs-mcp already provides) — the loop becomes: vs-mcp launches the
debuggee under the focus guard, jidodebugger drives its UI over the
same MCP session, and neither side has to touch the foreground.

Recorded end-to-end run against a WPF debuggee (6 tool calls, zero
human interventions, zero foreground steals):

| # | Tool | Result | What the guard did |
|---|------|--------|--------------------|
| 1 | `debug_start` (vs-mcp) | Debugging started | VS restore *and* debuggee launch both stayed background — the user's foreground app was never activated |
| 2 | `wpf_bridge_inject` (jidodebugger) | payload attached | — |
| 3 | `wpf_invoke_command` LoadCommand (jidodebugger) | fired=true, awaitedAsync=true | — |
| 4 | `wpf_wait_for` viewModel appears (jidodebugger) | matchCount=5 (~3.9 s) | — |
| 5 | `wpf_invoke_command` OpenMemberByKeyCommand (jidodebugger) | fired=true, awaitedAsync=true | — |
| 6 | `wpf_wait_for` propertyNotNull (jidodebugger) | matched | — |

How it works under the hood, when the guard is on:

1. `LockSetForegroundWindow` denies foreign `SetForegroundWindow` calls
   during the debug-start window.
2. `SPI_SETFOREGROUNDLOCKTIMEOUT` is temporarily raised so the launched
   debuggee's own activation attempts are demoted to a taskbar flash.
3. If VS is minimized at the moment `debug_start` /
   `debug_start_without_debugging` / `debug_restart` is invoked, it is
   re-minimized (`SW_SHOWMINNOACTIVE`) whenever it tries to auto-restore
   during the lock window.

The setting is off by default, so opt-in via `focus_guard_set` before an
autonomous session and turn it off again when you want normal VS focus
behavior back.

### Observability

When focus does slip through, you don't have to guess what took it. A
background auditor (started automatically) records every foreground
transition Windows fires — process name, window title, timestamp, and
whether the guard was on. Call `focus_guard_audit_read` right after any
perceived focus steal to see exactly which process / window pulled
foreground and when, and `focus_guard_audit_clear` to reset the buffer
before a new test scenario.

## Bundled Claude Code Skills

The VS Extension ships a small set of Claude Code skills that complement the MCP
tools. They are deployed automatically on extension startup to
`%USERPROFILE%\.claude\skills\`, so Claude Code picks them up without manual setup.

| Skill | Purpose |
|-------|---------|
| `vs-ui-explore` | Autonomous UI crawl of a VS debuggee — drives the app via `ui_snapshot`, `ui_find_elements`, and the `ui_wait_*` primitives, then reports crashes, error dialogs, unreachable screens, and disabled-but-expected states. Trigger it with prompts like "crawl the UI and report bugs". |

Source files live under `src/VsMcp.Extension/Skills/`; edit them there and the
extension will redeploy the updated copy on next launch (it compares file
content and skips unchanged files).

## Contributing

**Pull Requests are not accepted.**

For feature requests and bug reports, please use [GitHub Issues](https://github.com/dhq-boiler/Unofficial-VS-MCP/issues).

## License

[MIT License](LICENSE.txt)
