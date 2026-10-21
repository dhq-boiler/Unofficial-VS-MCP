# Changelog

## [0.13.4] - 2026-10-22

### Fixed
- **File search with multiple filters** — `find_in_files` now supports semicolon-separated patterns such as `*.h;*.cpp;*.cs` instead of returning zero searched files.
- **File search outside the solution directory** — searches project directories and linked project files, including sources outside a `build` directory containing the solution. Overlapping paths are searched once, and generated directories remain excluded. Fixes [#7](https://github.com/dhq-boiler/Unofficial-VS-MCP/issues/7).

## [0.9.0] - 2026-05-13

### Improved
- **Tool descriptions tuned for agent selection accuracy** — 30 tool descriptions were revised based on an empirical bias-free dry-run methodology (see `tuning/vs-mcp/`). No runtime behavior changed; only the strings exposed to LLM agents via `tools/list`. Agents now select the correct tool with 91.7% accuracy on the main scenario set (33/36) and 93.2% with hold-outs (41/44), and 100% accuracy on 10 destructive-or-critical operations (process termination, debug start/stop, etc.).
- **UI Automation vs Browser DOM cluster boundary made explicit** — every `ui_*` description now front-loads `[Windows UIA — desktop app being debugged]` and every `web_*` description front-loads `[Browser DOM — connected via web_connect]`. Each tool also carries a reciprocal cross-cluster reference (e.g. `ui_click` points to `web_element_click` for browser pages; `web_screenshot` points to `ui_capture_window` for desktop apps). Eliminates UIA↔Web cross-cluster confusion that previously occurred on utterances like "click the button" or "take a screenshot".
- **Critical start/stop/kill operations disambiguated** — `debug_start` / `debug_start_without_debugging` front-load `F5` / `Ctrl+F5` and explicitly cross-reference. `debug_stop`, `process_detach`, and `process_terminate` each lead with their distinct intent (normal session stop / release attachment without killing / DESTRUCTIVE force-kill) and cross-reference each other.
- **Expression-evaluation triplet differentiated** — `debug_evaluate` (read-only, one-shot), `immediate_execute` (side-effecting, one-shot), and `watch_add` (persistent, re-evaluates across breaks) now mutually cross-reference and lead with their distinguishing property.
- **File / output / error-list operations differentiated** — `file_read` vs `get_active_document`, `output_read` vs `console_read`, `error_list_get` vs `get_build_errors`, `find_in_files` vs `code_find_references`, `file_write` vs `file_edit` vs `edit_preview` all gained explicit "use X instead when …" clauses.
- **`execute_command` repositioned as a fallback** — description now explicitly directs callers to dedicated tools (`build_solution`, `debug_start`, `file_open`, …) and limits `execute_command` to cases where no dedicated tool exists.
- **`get_status`, `rebuild`, `ui_invoke` narrowed** — each now points to the narrower dedicated tool for common cases (`debug_get_mode` / `get_active_document` / `solution_info` for single-facet status queries; `build_solution` for normal builds; `ui_click` for richer click semantics).

## [0.8.0] - 2026-04-11

### Added
- **`ui_mouse_wheel` tool** — scrolls the mouse wheel over a UI element or screen coordinates. Position can be specified by `automationId`, `name`, or explicit `x`/`y`. Supports both vertical and horizontal scrolling via the `horizontal` flag, and the scroll amount is expressed in wheel notches via `clicks` (positive scrolls up/left, negative scrolls down/right).
- **Cursor-preserving scroll path** — by default `ui_mouse_wheel` tries `ScrollPattern` first when an element is given, then falls back to `PostMessage WM_MOUSEWHEEL` against the debuggee's window under the target point, and only as a last resort uses physical wheel events. The first two paths never move the system cursor, so the user's mouse pointer is not stolen during automated scrolling. The PostMessage path validates that the target HWND belongs to the debuggee process before sending.
- **`restoreCursor` option for all physical mouse tools** (`ui_click`, `ui_double_click`, `ui_right_click`, `ui_drag`, `ui_mouse_wheel`) — when physical mouse events are unavoidable, the cursor's previous position is captured before the operation and restored afterwards. Defaults to `true`.
- **`blockInput` option for all physical mouse tools** — wraps the operation in `BlockInput(true)`/`BlockInput(false)` so that user keyboard/mouse input cannot interfere with the automated action. Defaults to `false`; requires the host process to run with administrator privileges.

## [0.7.0] - 2026-04-10

### Added
- **`ui_snapshot` tool** — single-call compact semantic snapshot of the debugged application's main window. Returns a pruned UI Automation tree (omitting invisible/boring nodes) together with an optional screenshot and focused-element info. Each node includes its role, id, name, bounding rect, supported action patterns (`invoke`, `toggle`, `select`, `setvalue`, `expand`), and state flags (`disabled`, `offscreen`, `focused`, `checked`, `selected`, `expanded`). Optimized for LLM-driven autonomous UI testing; prefer it over `ui_get_tree` + `ui_capture_window`.
- **`ui_wait_for_element` tool** — polls the UI Automation tree until an element matching the given criteria reaches a target state (`appears`, `disappears`, `enabled`, `focused`), with configurable timeout and poll interval. Eliminates the need to sleep after triggering UI actions.
- **`ui_wait_idle` tool** — waits until the element count under the debuggee stops changing for a configurable quiet period, with a timeout. Useful after actions that cause asynchronous UI updates (loading, layout, progressive rendering).
- **`vs-ui-explore` Claude Code skill** — bundled with the extension and automatically deployed to `%USERPROFILE%\.claude\skills\` on startup via a new `VsMcpPackage.DeploySkills()` step. Drives Claude Code through an autonomous UI crawl of a debuggee using `ui_snapshot`, `ui_find_elements`, and the `ui_wait_*` primitives and produces a structured bug/coverage report with hard stops for destructive controls.

### Improved
- **`ui_find_elements` semantic matching** — `name`, `automationId`, and `className` now accept per-field match modes (`exact` (default), `contains`, `regex`, all case-insensitive). New `hasPattern` filter restricts results to elements that expose specific UIA patterns (`invoke`, `toggle`, `select`, `setvalue`, `expand`). New `ancestorAutomationId` scopes the walk to descendants of a specific element (e.g. a particular dialog) instead of every top-level window of the debuggee.
- **`ui_find_elements` ancestor lookup robustness** — the ancestor resolution now walks the debuggee's top-level windows directly and uses `FindAll` so that duplicate `AutomationId` values (such as an offscreen placeholder plus the real visible control) no longer cause scoped searches to return zero results.

### Fixed
- **`get_help` categorization** — the three new UI tools (`ui_snapshot`, `ui_wait_for_element`, `ui_wait_idle`) are now listed under the `UI` category in `get_help` output instead of `Other`, by syncing the `GeneralTools.ToolCategories` mirror with `VsMcp.Shared.ToolCategoryMap`.

## [0.4.0] - 2026-03-24

### Added
- **C++ native test discovery and execution** — `test_discover` and `test_run` now support 4 C++ test frameworks:
  - **Google Test** — discovers tests via `--gtest_list_tests`, runs with XML output
  - **Boost.Test** — discovers tests via `--list_content`, runs with XML log output
  - **MS CppUnitTest** — discovers via `vstest.console.exe` with source-parsing fallback
  - **CTest/CMake** — discovers via `ctest -N` for CMake folder-mode projects
- **CMake folder-mode support** — `test_discover` and `test_run` work when VS opens a CMake folder (no .sln required)
- **Auto-detection of C++ test frameworks** from vcxproj dependencies and source file includes
- **C++ test sample projects** — 4 sample projects for testing and reference (`tests/cpp/`)

### Improved
- **StdioProxy error messages** — clearer diagnostics when VS is running with a different solution than expected

## [0.3.1] - 2026-02-26

### Added
- **Test runner tools** (3 new tools) — `test_discover`, `test_run`, `test_results`
- **NuGet package management tools** (5 new tools) — `nuget_list`, `nuget_search`, `nuget_install`, `nuget_update`, `nuget_uninstall`
- **Code navigation tools** (3 new tools) — `code_goto_definition`, `code_find_references`, `code_goto_implementation`
- **Solution Explorer tools** (6 new tools) — `solution_add_project`, `solution_remove_project`, `project_add_file`, `project_remove_file`, `project_add_reference`, `project_remove_reference`
- **Edit preview and approval tools** (4 new tools) — `edit_preview` shows a diff in VS, `edit_approve`/`edit_reject` to accept or discard changes, `edit_list_pending` to list pending edits
- **`--tools` argument** for dynamic tool loading — filter tools by category presets (`core`, `debug`, `web`, `ui`) or individual category names to reduce token usage

### Changed
- **Consolidated similar tools** from ~120 to 106 for improved usability
  - `breakpoint_set` now handles regular, conditional, hit count, and function breakpoints
  - `debug_step` replaces `debug_step_over`/`debug_step_into`/`debug_step_out` with a `direction` parameter
  - `console_send` replaces `console_send_input`/`console_send_keys`
  - `thread_set_frozen` replaces `thread_freeze`/`thread_thaw`
  - `memory_read` replaces `memory_read`/`memory_read_variable`
  - `web_console` replaces `web_console_enable`/`web_console_get`/`web_console_clear`
  - `web_network` replaces `web_network_enable`/`web_network_get`/`web_network_clear`
  - `web_dom_query` absorbs `web_dom_get_html`/`web_dom_get_attributes`
- **Compressed MCP instructions** from ~3600 chars to ~600 chars for reduced token usage

## [0.3.0] - 2026-02-26

### Added
- **Web debugging tools** — 18 new tools for browser automation via Chrome DevTools Protocol (CDP) and Firefox Remote Debug Protocol (RDP)
  - `web_connect`: Connect to a browser for web debugging (Chrome/Edge via CDP, Firefox via RDP)
  - `web_disconnect`: Disconnect from the browser connection
  - `web_status`: Get the current browser connection status
  - `web_navigate`: Navigate the browser to a URL
  - `web_screenshot`: Capture a screenshot of the current page
  - `web_dom_get`: Get the DOM tree of the current page
  - `web_dom_query`: Query DOM elements using a CSS selector
  - `web_dom_get_html`: Get the outerHTML of a DOM element
  - `web_dom_get_attributes`: Get all attributes of a DOM element
  - `web_console_enable`: Start collecting browser console messages
  - `web_console_get`: Get collected browser console messages
  - `web_console_clear`: Clear the collected console message buffer
  - `web_js_execute`: Execute JavaScript in the browser page context
  - `web_network_enable`: Start monitoring network requests and responses
  - `web_network_get`: Get captured network requests/responses
  - `web_network_clear`: Clear the captured network entry buffer
  - `web_element_click`: Click a DOM element found by CSS selector
  - `web_element_set_value`: Set the value of an input element found by CSS selector
- `IBrowserConnection` interface for protocol abstraction (CDP / Firefox RDP)
- New **Web** tool category in tool help with CDP and Firefox RDP connection guidance

### Fixed
- CDP connecting to browser-level target instead of page-level target
- Firefox RDP connection, tab attachment, and message routing
- Firefox RDP JavaScript evaluation and console message parsing

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
