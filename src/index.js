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
const LOCK_STALE_MS = 30_000; // fallback: a lock older than this is considered abandoned (PID reuse)
const LOCK_WAIT_MS = 40_000;  // must exceed LOCK_STALE_MS so waiters outlive an abandoned lock
// Cache + lock live in a directory shared by every server process (one per Claude Code session).
const SHARED_DIR = process.env.CLAUDE_USAGE_CACHE_DIR || path.join(os.tmpdir(), 'claude-usage-mcp');
const CACHE_FILE = path.join(SHARED_DIR, 'usage-cache.json');
const LOCK_FILE = path.join(SHARED_DIR, 'usage.lock');
let inflight = null; // in-process dedupe of concurrent calls

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

const sleep = ms => new Promise(r => setTimeout(r, ms));

function readCache() {
  try {
    const c = JSON.parse(fs.readFileSync(CACHE_FILE, 'utf8'));
    return c && typeof c.at === 'number' && c.data ? c : null;
  } catch { return null; }
}

function writeCache(data) {
  const tmp = `${CACHE_FILE}.${process.pid}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify({ at: Date.now(), data }));
  fs.renameSync(tmp, CACHE_FILE); // atomic replace so readers never see a partial file
}

function isProcessAlive(pid) {
  try { process.kill(pid, 0); return true; } // signal 0 only checks existence (works on Windows)
  catch (e) { return e.code === 'EPERM'; }   // EPERM: exists but owned by another user
}

// True when the current lock holder is gone (killed/crashed) or the lock is too old.
function isLockAbandoned() {
  let st, pid;
  try { st = fs.statSync(LOCK_FILE); pid = parseInt(fs.readFileSync(LOCK_FILE, 'utf8'), 10); }
  catch { return false; } // lock vanished meanwhile: just retry
  if (Date.now() - st.mtimeMs > LOCK_STALE_MS) return true;
  // Empty/garbled file = holder is between create and write; trust only the age check above.
  return Number.isInteger(pid) && pid > 0 && pid !== process.pid && !isProcessAlive(pid);
}

// Cross-process lock via exclusive file creation; returns a release function.
async function acquireLock() {
  fs.mkdirSync(SHARED_DIR, { recursive: true });
  const deadline = Date.now() + LOCK_WAIT_MS;
  for (;;) {
    try {
      const fd = fs.openSync(LOCK_FILE, 'wx');
      fs.writeSync(fd, String(process.pid));
      fs.closeSync(fd);
      return () => {
        // Only delete our own lock, never one another process took over after a stale takeover.
        try { if (fs.readFileSync(LOCK_FILE, 'utf8') === String(process.pid)) fs.unlinkSync(LOCK_FILE); } catch {}
      };
    } catch (e) {
      if (e.code !== 'EEXIST') throw e;
      if (isLockAbandoned()) {
        try { fs.unlinkSync(LOCK_FILE); } catch {}
        continue;
      }
      if (Date.now() > deadline) throw new Error(`Timed out waiting for lock ${LOCK_FILE}`);
      await sleep(100 + Math.random() * 100);
    }
  }
}

function fetchUsage(force) {
  if (!inflight) inflight = loadUsage(force).finally(() => { inflight = null; });
  return inflight;
}

async function loadUsage(force) {
  const started = Date.now();
  const fresh = c => c && (force ? c.at >= started : Date.now() - c.at < CACHE_MS);
  let c = readCache();
  if (fresh(c)) return c.data;
  const release = await acquireLock();
  try {
    c = readCache(); // another process may have refreshed it while we waited
    if (fresh(c)) return c.data;
    try {
      const data = await requestUsage();
      writeCache(data);
      return data;
    } catch (e) {
      if (c) return { ...c.data, _stale: true, _error: e.message, _cached_at: new Date(c.at).toISOString() };
      throw e;
    }
  } finally { release(); }
}

async function requestUsage() {
  if (process.env.CLAUDE_USAGE_DEBUG) console.error(`[${process.pid}] calling usage API`);
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
  return JSON.parse(text);
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

function toText(sum, data) {
  const stale = data && data._stale ? `(stale cache from ${data._cached_at}; refresh failed: ${data._error})
` : '';
  return stale + Object.values(sum).map(s =>
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
      content: [{ type: 'text', text: toText(sum, data) + '\n\n' + JSON.stringify(payload, null, 2) }],
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
  fetchUsage(true).then(d => console.log(toText(summarize(d), d)), e => { console.error(e.message); process.exit(1); });
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
