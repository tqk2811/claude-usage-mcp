# claude-usage-mcp

Zero-dependency MCP server (Node.js >= 18, stdio) that reports your Claude subscription usage:
**5-hour session** and **weekly** limits (percent used, reset time).

It reads the OAuth token Claude Code stores in `~/.claude/.credentials.json`
(or `CLAUDE_CODE_OAUTH_TOKEN`) and calls `GET https://api.anthropic.com/api/oauth/usage`,
the same source `/usage` uses. The endpoint is undocumented and may change.

## Install

```bash
git clone https://github.com/tqk2811/claude-usage-mcp
claude mcp add --scope user claude-usage -- node /path/to/claude-usage-mcp/src/index.js
```

Quick check without MCP: `node src/index.js --once`

## Tool

`get_usage` — args: `raw` (include raw API response), `force` (bypass 30s cache).

```
5-hour session: 2% used, resets 11/18/2026, 8:40:00 AM (in 4h 24m)
Weekly (all models): 77% used, resets 11/18/2026, 8:00:00 PM (in 15h 44m)
```

## Environment

- `CLAUDE_CODE_OAUTH_TOKEN` — use this token instead of the credentials file.
- `CLAUDE_CONFIG_DIR` — alternative Claude config directory.

## License

MIT
