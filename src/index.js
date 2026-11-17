#!/usr/bin/env node
// Zero-dependency MCP stdio server reporting Claude 5-hour session and weekly usage limits.
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const readline = require('readline');

const VERSION = '1.0.0';
const USAGE_URL = 'https://api.anthropic.com/api/oauth/usage';
const CACHE_MS = 30_000;
let cache = null;

function readToken() {
  if (process.env.CLAUDE_CODE_OAUTH_TOKEN) return process.env.CLAUDE_CODE_OAUTH_TOKEN;
  const dir = process.env.CLAUDE_CONFIG_DIR || path.join(os.homedir(), '.claude');
  const file = path.join(dir, '.credentials.json');
  let json;
  try { json = JSON.parse(fs.readFileSync(file, 'utf8')); }
  catch (e) { throw new Error(`Cannot read OAuth credentials at ${file}: ${e.message}`); }
  const oauth = json.claudeAiOauth;
  if (!oauth || !oauth.accessToken) throw new Error(`No claudeAiOauth.accessToken in ${file}`);
  if (oauth.expiresAt && oauth.expiresAt < Date.now())
    throw new Error('OAuth token expired. Run any Claude Code command to refresh it.');
  return oauth.accessToken;
}

async function fetchUsage(force) {
  if (!force && cache && Date.now() - cache.at < CACHE_MS) return cache.data;
  const res = await fetch(USAGE_URL, {
    headers: {
      Authorization: `Bearer ${readToken()}`,
      'anthropic-beta': 'oauth-2025-04-20',
      'Content-Type': 'application/json',
      'User-Agent': `claude-usage-mcp/${VERSION}`,
    },
    signal: AbortSignal.timeout(15_000),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`Usage API HTTP ${res.status}: ${text.slice(0, 300)}`);
  const data = JSON.parse(text);
  cache = { at: Date.now(), data };
  return data;
}

function fmtRemaining(ms) {
  if (ms <= 0) return 'now';
  const m = Math.round(ms / 60000), d = Math.floor(m / 1440), h = Math.floor((m % 1440) / 60);
  return [d && `${d}d`, h && `${h}h`, `${m % 60}m`].filter(Boolean).join(' ');
}

const LABELS = {
  five_hour: '5-hour session',
  seven_day: 'Weekly (all models)',
  seven_day_opus: 'Weekly (Opus)',
  seven_day_sonnet: 'Weekly (Sonnet)',
  seven_day_oauth_apps: 'Weekly (OAuth apps)',
};

function summarize(data) {
  const out = {};
  for (const [key, v] of Object.entries(data)) {
    if (!v || typeof v !== 'object' || typeof v.utilization !== 'number') continue;
    const reset = v.resets_at ? new Date(v.resets_at) : null;
    out[key] = {
      label: LABELS[key] || key,
      utilization_percent: v.utilization,
      resets_at: v.resets_at || null,
      resets_at_local: reset ? reset.toLocaleString() : null,
      resets_in: reset ? fmtRemaining(reset - Date.now()) : null,
    };
  }
  return out;
}

function toText(sum) {
  return Object.values(sum).map(s =>
    `${s.label}: ${s.utilization_percent}% used` +
    (s.resets_at_local ? `, resets ${s.resets_at_local} (in ${s.resets_in})` : '')).join('\n');
}

const TOOLS = [{
  name: 'get_usage',
  description: 'Get current Claude subscription usage: 5-hour session and weekly limits (percent used and reset time).',
  inputSchema: {
    type: 'object',
    properties: {
      raw: { type: 'boolean', description: 'Also return the raw API response.' },
      force: { type: 'boolean', description: 'Bypass the 30s cache.' },
    },
  },
}];

async function callTool(name, args = {}) {
  if (name !== 'get_usage') throw Object.assign(new Error(`Unknown tool: ${name}`), { code: -32602 });
  try {
    const data = await fetchUsage(args.force);
    const sum = summarize(data);
    const payload = args.raw ? { summary: sum, raw: data } : sum;
    return {
      content: [{ type: 'text', text: toText(sum) + '\n\n' + JSON.stringify(payload, null, 2) }],
      structuredContent: payload,
    };
  } catch (e) {
    return { content: [{ type: 'text', text: `Error: ${e.message}` }], isError: true };
  }
}

async function handle(msg) {
  const { id, method, params } = msg;
  switch (method) {
    case 'initialize':
      return {
        protocolVersion: params?.protocolVersion || '2025-06-18',
        capabilities: { tools: {} },
        serverInfo: { name: 'claude-usage', version: VERSION },
      };
    case 'ping': return {};
    case 'tools/list': return { tools: TOOLS };
    case 'tools/call': return callTool(params?.name, params?.arguments);
    default:
      if (id === undefined) return undefined; // notification
      throw Object.assign(new Error(`Method not found: ${method}`), { code: -32601 });
  }
}

function send(obj) { process.stdout.write(JSON.stringify(obj) + '\n'); }

if (process.argv.includes('--once')) {
  fetchUsage(true).then(d => console.log(toText(summarize(d))), e => { console.error(e.message); process.exit(1); });
} else {
  readline.createInterface({ input: process.stdin }).on('line', async line => {
    if (!line.trim()) return;
    let msg;
    try { msg = JSON.parse(line); } catch { return send({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } }); }
    try {
      const result = await handle(msg);
      if (msg.id !== undefined && result !== undefined) send({ jsonrpc: '2.0', id: msg.id, result });
    } catch (e) {
      if (msg.id !== undefined) send({ jsonrpc: '2.0', id: msg.id, error: { code: e.code || -32603, message: e.message } });
    }
  });
}
