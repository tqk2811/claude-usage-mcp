# claude-usage-mcp

Zero-dependency MCP server (Node.js >= 18, stdio) that reports your Claude subscription usage:
**5-hour session** and **weekly** limits (percent used, reset time).

It reads the OAuth token Claude Code stores in `~/.claude/.credentials.json`
(or `CLAUDE_CODE_OAUTH_TOKEN`) and calls `GET https://api.anthropic.com/api/oauth/usage`,
the same source `/usage` uses. The endpoint is undocumented and may change.

## Install

```bash
git clone https://github.com/tqk2811/claude-usage-mcp
claude mcp add --scope user claude-usage -- node /path/to/claude-usage-mcp/src/node/index.js
```

Quick check without MCP: `node src/node/index.js --once`

## Tool

`get_usage` — args: `raw` (include raw API response), `force` (bypass 30s cache).

## Cache & concurrency

All server processes (one per Claude Code session) share a 30s cache file and a lock file in
`%TEMP%/claude-usage-mcp` (or `$TMPDIR/claude-usage-mcp`), so concurrent calls make a single API request.
If a refresh fails (e.g. HTTP 429) the last cached value is returned and marked stale.

```
5-hour session: 2% used, resets 11/18/2026, 8:40:00 AM (in 4h 24m)
Weekly (all models): 77% used, resets 11/18/2026, 8:00:00 PM (in 15h 44m)
```

## Environment

- `CLAUDE_CODE_OAUTH_TOKEN` — use this token instead of the credentials file.
- `CLAUDE_CONFIG_DIR` — alternative Claude config directory.
- `CLAUDE_USAGE_CACHE_DIR` — shared cache/lock directory.
- `CLAUDE_USAGE_DEBUG` — log each real API call to stderr.

## Tray app (Windows)

`src/ClaudeUsageTray` is a small WPF tray app (.NET 8) that shows two live numbers in the notification area:
the **5-hour session** percent and the **weekly (all models)** percent. The same exe also works as the MCP server.

Build:

```bash
dotnet build src/ClaudeUsageTray/ClaudeUsageTray.slnx -c Release
# output: src/ClaudeUsageTray/ClaudeUsageTray/bin/Release/net8.0-windows/ClaudeUsageTray.exe
```

- Each icon is drawn at runtime with plain GDI: the number (0-100) in a color picked by thresholds
  (default green below 50, amber below 80, red above), `!` on error, `?` before the first value.
  Hover for the full text (percent, reset time, time left). Double-click opens settings; right-click: Refresh now,
  Settings, Exit.
- Settings (applied live, saved to `%APPDATA%\ClaudeUsageTray\config.json`): show/hide each icon, thresholds and
  colors per icon, background color (default transparent), font family and bold, icon order, refresh interval
  (default 60 s, minimum 30 s), start with Windows (`HKCU\...\Run` with `--tray`), language (English/Vietnamese),
  theme (System/Light/Dark).
- Icon order is applied by re-adding both icons; Windows 11 remembers icon positions, and dragging an icon by hand
  overrides the setting.
- Launching the exe again while it runs opens the settings window of the running instance (useful when both icons
  are hidden).
- The idle tray runs on WPF core (`Application`, the `HwndSource` message window, `Dispatcher`), with no
  WinForms/System.Drawing. Only the settings window and the WPF-UI library are loaded on demand; after closing it
  they are released and the process trims its working set.

Command line:

- `ClaudeUsageTray.exe --mcp` — stdio MCP server, same `get_usage` tool and output as the Node server.
  Register it from the settings window (MCP section: copy the command, Register/Unregister) or by hand:
  `claude mcp add --scope user claude-usage -- "C:\path\to\ClaudeUsageTray.exe" --mcp`
- `ClaudeUsageTray.exe --once` — print the summary and exit.
- `ClaudeUsageTray.exe --tray` — start hidden in the tray (what autostart uses).

Unlike the Node server, the C# app does **not** use the shared cache/lock files: each process keeps its own
in-memory cache (30 s, one request in flight; on a failed refresh the last value is returned marked stale).
It honors `CLAUDE_CODE_OAUTH_TOKEN`, `CLAUDE_CONFIG_DIR` and `CLAUDE_USAGE_DEBUG`.

## License

MIT
