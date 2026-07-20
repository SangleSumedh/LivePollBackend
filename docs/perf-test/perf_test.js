#!/usr/bin/env node
/**
 * ╔══════════════════════════════════════════════════════════════════════════════╗
 * ║           LivePoll — SignalR Performance Test Suite (150 Clients)            ║
 * ╠══════════════════════════════════════════════════════════════════════════════╣
 * ║  Simulates 150 concurrent SignalR WebSocket connections and measures:       ║
 * ║    • Connection establishment time (per-client + aggregate)                 ║
 * ║    • Hub method invocation latency (JoinPollGroup, NotifyBidChange)         ║
 * ║    • REST API response times (vote casting, bid placement)                  ║
 * ║    • Real-time event broadcast latency (server → all clients)               ║
 * ║    • Memory & throughput stats                                              ║
 * ║                                                                             ║
 * ║  Modes:  --mode normal   (Normal poll voting)                               ║
 * ║          --mode bidding  (Bidding poll with coin allocation)                 ║
 * ║          --mode both     (Run both sequentially)  [default]                 ║
 * ║                                                                             ║
 * ║  Usage:                                                                     ║
 * ║    node perf_test.js --pollId ABC123 --biddingPollId XYZ789                 ║
 * ║    node perf_test.js --mode normal --pollId ABC123                          ║
 * ║    node perf_test.js --mode bidding --biddingPollId XYZ789                  ║
 * ╚══════════════════════════════════════════════════════════════════════════════╝
 */

import * as signalR from "@microsoft/signalr";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const __filename = fileURLToPath(import.meta.url);
const __dirname  = dirname(__filename);

// ─── Read API base from frontend .env ───────────────────────────────────────
function readApiBaseFromEnv() {
  // Walk up from docs/perf-test → LivePollBackend → LivePollFullstack, then into Live-poll/.env
  const envPaths = [
    resolve(__dirname, "..", "..", "..", "Live-poll", ".env"),
    resolve(__dirname, "..", "..", "..", "Live-poll", ".env.local"),
  ];
  for (const p of envPaths) {
    try {
      const content = readFileSync(p, "utf-8");
      const match = content.match(/NEXT_PUBLIC_API_URL\s*=\s*(.+)/m);
      if (match) {
        const url = match[1].trim().replace(/["']/g, "").replace(/\/$/, "");
        console.log(`  ✓ Loaded API URL from ${p}`);
        console.log(`    → ${url}`);
        return url;
      }
    } catch {}
  }
  console.warn("  ⚠ Could not read NEXT_PUBLIC_API_URL from .env, falling back to localhost");
  return "http://localhost:5065";
}

const ENV_API_BASE = readApiBaseFromEnv();

// ─── Configuration ──────────────────────────────────────────────────────────
const CONFIG = {
  API_BASE: ENV_API_BASE,
  HUB_URL:  `${ENV_API_BASE}/hubs/poll`,
  TOTAL_CLIENTS: 150,
  /** Stagger connections over this window to avoid thundering-herd */
  CONNECTION_RAMP_MS: 5000,
  /** How many votes/bids each client sends */
  ACTIONS_PER_CLIENT: 3,
  /** Delay between individual actions (ms) */
  ACTION_DELAY_MS: 100,
  /** Max number of options in a normal poll question */
  OPTION_COUNT: 4,
};

// ─── CLI Argument Parsing ───────────────────────────────────────────────────
function parseArgs() {
  const args = process.argv.slice(2);
  const opts = {
    mode: "both",
    pollId: "",
    biddingPollId: "",
    apiBase: CONFIG.API_BASE,
    clients: CONFIG.TOTAL_CLIENTS,
  };
  for (let i = 0; i < args.length; i++) {
    if (args[i] === "--mode")           opts.mode           = args[++i];
    if (args[i] === "--pollId")         opts.pollId         = args[++i];
    if (args[i] === "--biddingPollId")  opts.biddingPollId  = args[++i];
    if (args[i] === "--apiBase")        opts.apiBase        = args[++i];
    if (args[i] === "--clients")        opts.clients        = parseInt(args[++i]);
  }
  CONFIG.API_BASE = opts.apiBase;
  CONFIG.HUB_URL  = `${opts.apiBase}/hubs/poll`;
  if (opts.clients) CONFIG.TOTAL_CLIENTS = opts.clients;
  return opts;
}

// ─── Utility Helpers ────────────────────────────────────────────────────────
const uid = () => `perf_${Date.now()}_${Math.random().toString(36).slice(2, 9)}`;

function percentile(sorted, p) {
  const idx = Math.ceil((p / 100) * sorted.length) - 1;
  return sorted[Math.max(0, idx)];
}

function computeStats(values, label) {
  if (!values.length) return { label, count: 0 };
  const sorted = [...values].sort((a, b) => a - b);
  return {
    label,
    count: sorted.length,
    min:   sorted[0].toFixed(2),
    max:   sorted[sorted.length - 1].toFixed(2),
    mean:  (sorted.reduce((a, b) => a + b, 0) / sorted.length).toFixed(2),
    median: percentile(sorted, 50).toFixed(2),
    p90:   percentile(sorted, 90).toFixed(2),
    p95:   percentile(sorted, 95).toFixed(2),
    p99:   percentile(sorted, 99).toFixed(2),
  };
}

function printStats(stats) {
  if (stats.count === 0) {
    console.log(`  ${stats.label}: no data`);
    return;
  }
  console.log(`  ┌─ ${stats.label} (n=${stats.count})`);
  console.log(`  │  Min ........... ${stats.min} ms`);
  console.log(`  │  Max ........... ${stats.max} ms`);
  console.log(`  │  Mean .......... ${stats.mean} ms`);
  console.log(`  │  Median (p50) .. ${stats.median} ms`);
  console.log(`  │  p90 ........... ${stats.p90} ms`);
  console.log(`  │  p95 ........... ${stats.p95} ms`);
  console.log(`  └─ p99 ........... ${stats.p99} ms`);
}

function printSectionHeader(title) {
  const line = "═".repeat(60);
  console.log(`\n╔${line}╗`);
  console.log(`║  ${title.padEnd(58)}║`);
  console.log(`╚${line}╝`);
}

function printSubHeader(title) {
  console.log(`\n  ── ${title} ${"─".repeat(Math.max(0, 50 - title.length))}`);
}

async function fetchJson(url, options = {}) {
  const res = await fetch(url, {
    headers: { "Content-Type": "application/json", ...options.headers },
    ...options,
  });
  const text = await res.text();
  let json = null;
  try { json = JSON.parse(text); } catch {}
  return { status: res.status, ok: res.ok, json, text };
}

// ─── Connection Pool ────────────────────────────────────────────────────────
class ClientPool {
  constructor(count) {
    this.count = count;
    this.connections = [];
    this.sessionIds = [];
    this.connectTimes = [];
    this.errors = { connect: 0, joinGroup: 0, action: 0 };
    this.eventLatencies = [];
    this._eventTimestamps = new Map(); // eventKey -> sendTimestamp
  }

  /**
   * Create `count` SignalR connections with a ramp-up window.
   */
  async connectAll() {
    const delay = CONFIG.CONNECTION_RAMP_MS / this.count;
    const promises = [];

    for (let i = 0; i < this.count; i++) {
      promises.push(this._connectOne(i));
      if (i < this.count - 1) {
        await new Promise(r => setTimeout(r, delay));
      }
    }

    await Promise.allSettled(promises);
    return this;
  }

  async _connectOne(index) {
    const sessionId = uid();
    const start = performance.now();

    try {
      const conn = new signalR.HubConnectionBuilder()
        .withUrl(CONFIG.HUB_URL)
        .configureLogging(signalR.LogLevel.None)
        .build();

      await conn.start();
      const elapsed = performance.now() - start;

      this.connections.push(conn);
      this.sessionIds.push(sessionId);
      this.connectTimes.push(elapsed);
    } catch (err) {
      this.errors.connect++;
      if (index < 5 || index === this.count - 1) {
        console.error(`  ✗ Client ${index}: connect failed — ${err.message}`);
      }
    }
  }

  /**
   * Have every connection join a poll group.
   */
  async joinAll(pollId) {
    const joinTimes = [];
    const promises = this.connections.map(async (conn, i) => {
      const start = performance.now();
      try {
        await conn.invoke("JoinPollGroup", pollId, this.sessionIds[i]);
        joinTimes.push(performance.now() - start);
      } catch {
        this.errors.joinGroup++;
      }
    });
    await Promise.allSettled(promises);
    return joinTimes;
  }

  /**
   * Register an event listener on ALL connections and track broadcast latency.
   */
  registerBroadcastTracker(eventName) {
    this.connections.forEach((conn) => {
      conn.on(eventName, (_data) => {
        const now = performance.now();
        // Find the most recent send timestamp for this event
        const key = eventName;
        if (this._eventTimestamps.has(key)) {
          this.eventLatencies.push(now - this._eventTimestamps.get(key));
        }
      });
    });
  }

  /** Mark when we send an action that should trigger a broadcast */
  markEventSent(eventName) {
    this._eventTimestamps.set(eventName, performance.now());
  }

  async disconnectAll() {
    await Promise.allSettled(
      this.connections.map(c => c.stop().catch(() => {}))
    );
    this.connections = [];
  }
}

// ═══════════════════════════════════════════════════════════════════════════
//  Normal Poll Performance Test
// ═══════════════════════════════════════════════════════════════════════════
async function runNormalPollTest(pollId) {
  printSectionHeader("NORMAL POLL — Performance Test");
  console.log(`  Poll ID:   ${pollId}`);
  console.log(`  Clients:   ${CONFIG.TOTAL_CLIENTS}`);
  console.log(`  Actions:   ${CONFIG.ACTIONS_PER_CLIENT} votes/client`);

  // ── 1. Fetch poll to get structure ──
  printSubHeader("1. Fetching poll metadata");
  const { ok, json: poll } = await fetchJson(`${CONFIG.API_BASE}/api/polls/${pollId}`);
  if (!ok) {
    console.error(`  ✗ Could not fetch poll ${pollId}. Is it a valid normal poll ID?`);
    console.error(`    Response:`, poll);
    return;
  }
  const questionCount = poll.questions?.length ?? 0;
  console.log(`  ✓ Poll "${poll.title}" — ${questionCount} questions`);
  if (questionCount === 0) {
    console.error("  ✗ Poll has no questions, cannot vote.");
    return;
  }

  // ── 2. Connect all clients ──
  printSubHeader("2. Establishing connections");
  const startConnect = performance.now();
  const pool = new ClientPool(CONFIG.TOTAL_CLIENTS);
  await pool.connectAll();
  const totalConnectTime = performance.now() - startConnect;

  const connectedCount = pool.connections.length;
  console.log(`  ✓ ${connectedCount}/${CONFIG.TOTAL_CLIENTS} connected (${pool.errors.connect} failures)`);
  console.log(`  ✓ Total ramp-up time: ${totalConnectTime.toFixed(0)} ms`);
  printStats(computeStats(pool.connectTimes, "Connection Establishment Time"));

  // ── 3. Join poll group ──
  printSubHeader("3. Joining poll group");
  const joinTimes = await pool.joinAll(pollId);
  printStats(computeStats(joinTimes, "JoinPollGroup Invocation Time"));

  // ── 4. Register broadcast trackers ──
  pool.registerBroadcastTracker("VoteCountsUpdated");
  pool.registerBroadcastTracker("PollUpdated");

  // ── 5. Cast votes ──
  printSubHeader("4. Casting votes (REST API)");
  const voteTimes = [];
  const voteErrors = [];
  let votesAttempted = 0;
  let votesSucceeded = 0;

  // Each client votes on each question (up to ACTIONS_PER_CLIENT questions)
  const questionsToVote = Math.min(CONFIG.ACTIONS_PER_CLIENT, questionCount);

  for (let qIdx = 0; qIdx < questionsToVote; qIdx++) {
    const question = poll.questions[qIdx];
    const optionCount = question.options?.length ?? CONFIG.OPTION_COUNT;

    // Fire all client votes for this question concurrently
    const batchPromises = pool.connections.map(async (_, clientIdx) => {
      const optionIndex = clientIdx % optionCount;
      const sessionId = pool.sessionIds[clientIdx];

      const start = performance.now();
      pool.markEventSent("VoteCountsUpdated");
      votesAttempted++;

      try {
        const res = await fetchJson(`${CONFIG.API_BASE}/api/polls/${pollId}/votes`, {
          method: "POST",
          body: JSON.stringify({
            questionIndex: qIdx,
            optionIndex,
            sessionId,
          }),
        });
        const elapsed = performance.now() - start;
        voteTimes.push(elapsed);

        if (res.ok) {
          votesSucceeded++;
        } else {
          voteErrors.push({ client: clientIdx, question: qIdx, status: res.status, body: res.text });
        }
      } catch (err) {
        voteTimes.push(performance.now() - start);
        voteErrors.push({ client: clientIdx, question: qIdx, error: err.message });
      }
    });

    await Promise.allSettled(batchPromises);

    // Small delay between question batches
    if (qIdx < questionsToVote - 1) {
      await new Promise(r => setTimeout(r, CONFIG.ACTION_DELAY_MS));
    }
  }

  // Allow broadcasts to propagate
  await new Promise(r => setTimeout(r, 2000));

  console.log(`  ✓ Votes: ${votesSucceeded}/${votesAttempted} succeeded`);
  if (voteErrors.length > 0) {
    // Show unique error statuses
    const statusCounts = {};
    voteErrors.forEach(e => {
      const key = e.status || e.error || "unknown";
      statusCounts[key] = (statusCounts[key] || 0) + 1;
    });
    console.log(`  ⚠ Errors: ${JSON.stringify(statusCounts)}`);
  }

  printStats(computeStats(voteTimes, "Vote REST API Response Time"));

  // ── 6. Broadcast latency ──
  printSubHeader("5. Real-time broadcast latency");
  printStats(computeStats(pool.eventLatencies, "Server → Client Broadcast Latency"));

  // ── 7. Throughput ──
  printSubHeader("6. Throughput summary");
  const testDuration = (performance.now() - startConnect) / 1000;
  console.log(`  Total test duration ......... ${testDuration.toFixed(2)} s`);
  console.log(`  Votes/sec (successful) ...... ${(votesSucceeded / testDuration).toFixed(1)}`);
  console.log(`  Connections/sec ............. ${(connectedCount / (totalConnectTime / 1000)).toFixed(1)}`);

  // ── Cleanup ──
  printSubHeader("7. Disconnecting clients");
  await pool.disconnectAll();
  console.log(`  ✓ All clients disconnected.`);

  return {
    mode: "normal",
    connectedCount,
    connectStats: computeStats(pool.connectTimes, "Connection Time"),
    joinStats: computeStats(joinTimes, "Join Time"),
    voteStats: computeStats(voteTimes, "Vote API Time"),
    broadcastStats: computeStats(pool.eventLatencies, "Broadcast Latency"),
    votesSucceeded,
    votesAttempted,
    errors: pool.errors,
    durationSec: testDuration,
  };
}

// ═══════════════════════════════════════════════════════════════════════════
//  Bidding Poll Performance Test
// ═══════════════════════════════════════════════════════════════════════════
async function runBiddingPollTest(biddingPollId) {
  printSectionHeader("BIDDING POLL — Performance Test");
  console.log(`  Bidding Poll ID:  ${biddingPollId}`);
  console.log(`  Clients:          ${CONFIG.TOTAL_CLIENTS}`);
  console.log(`  Actions:          ${CONFIG.ACTIONS_PER_CLIENT} bid changes/client`);

  // ── 1. Fetch bidding poll ──
  printSubHeader("1. Fetching bidding poll metadata");
  const { ok, json: poll } = await fetchJson(`${CONFIG.API_BASE}/api/bidding/polls/${biddingPollId}`);
  if (!ok) {
    console.error(`  ✗ Could not fetch bidding poll ${biddingPollId}.`);
    console.error(`    Response:`, poll);
    return;
  }
  const questionCount = poll.questions?.length ?? 0;
  console.log(`  ✓ Poll "${poll.title}" — ${questionCount} questions`);
  if (questionCount === 0) {
    console.error("  ✗ Bidding poll has no questions.");
    return;
  }

  // Collect all skill IDs from first question
  const firstQuestion = poll.questions[0];
  const skillIds = firstQuestion.skills?.map(s => s.id) ?? [];
  const questionIndex = firstQuestion.index ?? 0;
  console.log(`  ✓ Question 0: "${firstQuestion.text}" — ${skillIds.length} skills`);

  if (skillIds.length === 0) {
    console.error("  ✗ No skills to bid on.");
    return;
  }

  // ── 2. Connect all clients ──
  printSubHeader("2. Establishing connections");
  const startConnect = performance.now();
  const pool = new ClientPool(CONFIG.TOTAL_CLIENTS);
  await pool.connectAll();
  const totalConnectTime = performance.now() - startConnect;

  const connectedCount = pool.connections.length;
  console.log(`  ✓ ${connectedCount}/${CONFIG.TOTAL_CLIENTS} connected (${pool.errors.connect} failures)`);
  console.log(`  ✓ Total ramp-up time: ${totalConnectTime.toFixed(0)} ms`);
  printStats(computeStats(pool.connectTimes, "Connection Establishment Time"));

  // ── 3. Join poll group ──
  printSubHeader("3. Joining poll group");
  const joinTimes = await pool.joinAll(biddingPollId);
  printStats(computeStats(joinTimes, "JoinPollGroup Invocation Time"));

  // ── 4. Register broadcast trackers ──
  pool.registerBroadcastTracker("ReceiveBubbleData");
  pool.registerBroadcastTracker("BiddingStarted");

  // ── 5. Ephemeral bid changes (SignalR hub invocations) ──
  printSubHeader("4. Ephemeral bid changes (NotifyBidChange via SignalR)");
  const bidChangeTimes = [];
  let bidChangesAttempted = 0;
  let bidChangesSucceeded = 0;

  for (let round = 0; round < CONFIG.ACTIONS_PER_CLIENT; round++) {
    const batchPromises = pool.connections.map(async (conn, clientIdx) => {
      const skillId = skillIds[clientIdx % skillIds.length];
      const amount = Math.floor(Math.random() * 10) + 1; // 1-10 coins
      const sessionId = pool.sessionIds[clientIdx];

      const start = performance.now();
      bidChangesAttempted++;

      try {
        await conn.invoke("NotifyBidChange", biddingPollId, questionIndex, sessionId, skillId, amount);
        bidChangeTimes.push(performance.now() - start);
        bidChangesSucceeded++;
      } catch (err) {
        bidChangeTimes.push(performance.now() - start);
        pool.errors.action++;
      }
    });

    await Promise.allSettled(batchPromises);

    if (round < CONFIG.ACTIONS_PER_CLIENT - 1) {
      await new Promise(r => setTimeout(r, CONFIG.ACTION_DELAY_MS));
    }
  }

  console.log(`  ✓ Bid changes: ${bidChangesSucceeded}/${bidChangesAttempted} succeeded`);
  printStats(computeStats(bidChangeTimes, "NotifyBidChange Hub Invocation Time"));

  // ── 6. REST bid placement ──
  printSubHeader("5. Committing bids (REST API)");
  const restBidTimes = [];
  let restBidsAttempted = 0;
  let restBidsSucceeded = 0;
  const restBidErrors = [];

  const cohort = "HR"; // default cohort

  const commitPromises = pool.connections.map(async (_, clientIdx) => {
    const skillId = skillIds[clientIdx % skillIds.length];
    const sessionId = pool.sessionIds[clientIdx];
    const coins = Math.floor(Math.random() * 10) + 1;

    const start = performance.now();
    restBidsAttempted++;

    try {
      pool.markEventSent("ReceiveBubbleData");
      const res = await fetchJson(`${CONFIG.API_BASE}/api/bidding/bid/${biddingPollId}`, {
        method: "POST",
        body: JSON.stringify({
          sessionId,
          cohort,
          biddingSkillId: skillId,
          questionIndex,
          coinsSpent: coins,
        }),
      });
      const elapsed = performance.now() - start;
      restBidTimes.push(elapsed);

      if (res.ok) {
        restBidsSucceeded++;
      } else {
        restBidErrors.push({ client: clientIdx, status: res.status, body: res.text });
      }
    } catch (err) {
      restBidTimes.push(performance.now() - start);
      restBidErrors.push({ client: clientIdx, error: err.message });
    }
  });

  await Promise.allSettled(commitPromises);

  // Allow broadcasts to propagate
  await new Promise(r => setTimeout(r, 2000));

  console.log(`  ✓ REST bids: ${restBidsSucceeded}/${restBidsAttempted} succeeded`);
  if (restBidErrors.length > 0) {
    const statusCounts = {};
    restBidErrors.forEach(e => {
      const key = e.status || e.error || "unknown";
      statusCounts[key] = (statusCounts[key] || 0) + 1;
    });
    console.log(`  ⚠ Errors: ${JSON.stringify(statusCounts)}`);
  }
  printStats(computeStats(restBidTimes, "Bid REST API Response Time"));

  // ── 7. Broadcast latency ──
  printSubHeader("6. Real-time broadcast latency");
  printStats(computeStats(pool.eventLatencies, "Server → Client Broadcast Latency"));

  // ── 8. Throughput ──
  printSubHeader("7. Throughput summary");
  const testDuration = (performance.now() - startConnect) / 1000;
  console.log(`  Total test duration ......... ${testDuration.toFixed(2)} s`);
  console.log(`  Bid changes/sec ............. ${(bidChangesSucceeded / testDuration).toFixed(1)}`);
  console.log(`  REST bids/sec ............... ${(restBidsSucceeded / testDuration).toFixed(1)}`);
  console.log(`  Connections/sec ............. ${(connectedCount / (totalConnectTime / 1000)).toFixed(1)}`);

  // ── Cleanup ──
  printSubHeader("8. Disconnecting clients");
  await pool.disconnectAll();
  console.log(`  ✓ All clients disconnected.`);

  return {
    mode: "bidding",
    connectedCount,
    connectStats: computeStats(pool.connectTimes, "Connection Time"),
    joinStats: computeStats(joinTimes, "Join Time"),
    bidChangeStats: computeStats(bidChangeTimes, "Bid Change Time"),
    restBidStats: computeStats(restBidTimes, "REST Bid Time"),
    broadcastStats: computeStats(pool.eventLatencies, "Broadcast Latency"),
    bidChangesSucceeded,
    restBidsSucceeded,
    errors: pool.errors,
    durationSec: testDuration,
  };
}

// ═══════════════════════════════════════════════════════════════════════════
//  Final Combined Report
// ═══════════════════════════════════════════════════════════════════════════
function printFinalReport(results) {
  printSectionHeader("FINAL PERFORMANCE REPORT");

  for (const r of results) {
    if (!r) continue;

    console.log(`\n  ▸ Mode: ${r.mode.toUpperCase()}`);
    console.log(`  ▸ Connected: ${r.connectedCount}/${CONFIG.TOTAL_CLIENTS}`);
    console.log(`  ▸ Duration: ${r.durationSec.toFixed(2)}s`);

    printStats(r.connectStats);
    printStats(r.joinStats);

    if (r.mode === "normal") {
      printStats(r.voteStats);
      console.log(`  ▸ Votes: ${r.votesSucceeded}/${r.votesAttempted}`);
    }

    if (r.mode === "bidding") {
      printStats(r.bidChangeStats);
      printStats(r.restBidStats);
      console.log(`  ▸ Ephemeral bid changes: ${r.bidChangesSucceeded}`);
      console.log(`  ▸ REST bids committed: ${r.restBidsSucceeded}`);
    }

    printStats(r.broadcastStats);

    if (r.errors.connect || r.errors.joinGroup || r.errors.action) {
      console.log(`  ⚠ Errors — connect: ${r.errors.connect}, join: ${r.errors.joinGroup}, action: ${r.errors.action}`);
    }
  }

  // ── Pass / Fail criteria ──
  console.log(`\n  ── Health Check ${"─".repeat(40)}`);
  for (const r of results) {
    if (!r) continue;
    const connectRate = (r.connectedCount / CONFIG.TOTAL_CLIENTS) * 100;
    const p95Connect = parseFloat(r.connectStats.p95 || 0);
    const p95Api = r.mode === "normal"
      ? parseFloat(r.voteStats?.p95 || 0)
      : parseFloat(r.restBidStats?.p95 || 0);

    const p95Broadcast = parseFloat(r.broadcastStats?.p95 || 0);

    const checks = [
      { name: "Connection Success Rate ≥ 95%",   pass: connectRate >= 95,   val: `${connectRate.toFixed(1)}%` },
      { name: "p95 Connect Time < 2000ms",        pass: p95Connect < 2000,  val: `${p95Connect}ms` },
      { name: "p95 API Response Time < 1000ms",   pass: p95Api < 1000,      val: `${p95Api}ms` },
      { name: "p95 Broadcast Latency < 500ms",    pass: p95Broadcast < 500 || p95Broadcast === 0, val: p95Broadcast ? `${p95Broadcast}ms` : "N/A" },
    ];

    console.log(`\n  [${r.mode.toUpperCase()}]`);
    for (const c of checks) {
      console.log(`    ${c.pass ? "✅" : "❌"} ${c.name} — ${c.val}`);
    }
  }

  console.log("");
}

// ═══════════════════════════════════════════════════════════════════════════
//  Markdown Report Generator
// ═══════════════════════════════════════════════════════════════════════════
function statsTableMd(stats) {
  if (!stats || stats.count === 0) return "*No data collected.*\n";
  return `| Metric | Value |
|--------|-------|
| Samples | ${stats.count} |
| Min | ${stats.min} ms |
| Max | ${stats.max} ms |
| Mean | ${stats.mean} ms |
| Median (p50) | ${stats.median} ms |
| p90 | ${stats.p90} ms |
| p95 | ${stats.p95} ms |
| p99 | ${stats.p99} ms |
`;
}

function generateReportMarkdown(results, opts) {
  const now = new Date();
  const timestamp = now.toISOString();
  const localTime = now.toLocaleString("en-IN", { timeZone: "Asia/Kolkata" });

  let md = `# 📊 LivePoll — Performance Test Report

> **Generated:** ${timestamp}  
> **Local Time:** ${localTime}  
> **API Endpoint:** \`${CONFIG.API_BASE}\`  
> **Hub Endpoint:** \`${CONFIG.HUB_URL}\`  
> **Simulated Clients:** ${CONFIG.TOTAL_CLIENTS}  
> **Connection Ramp-up:** ${CONFIG.CONNECTION_RAMP_MS} ms  
> **Actions per Client:** ${CONFIG.ACTIONS_PER_CLIENT}  
> **Test Mode:** ${opts.mode}

---

## Test Environment

| Parameter | Value |
|-----------|-------|
| Target Server | \`${CONFIG.API_BASE}\` |
| Transport | SignalR WebSocket |
| Concurrent Clients | ${CONFIG.TOTAL_CLIENTS} |
| Ramp-up Window | ${CONFIG.CONNECTION_RAMP_MS} ms |
| Actions per Client | ${CONFIG.ACTIONS_PER_CLIENT} |

---

`;

  for (const r of results) {
    if (!r) continue;

    if (r.mode === "normal") {
      md += `## 🗳️ Normal Poll Results

| Summary | Value |
|---------|-------|
| Clients Connected | ${r.connectedCount} / ${CONFIG.TOTAL_CLIENTS} |
| Connection Failures | ${r.errors.connect} |
| Join Group Failures | ${r.errors.joinGroup} |
| Votes Succeeded | ${r.votesSucceeded} / ${r.votesAttempted} |
| Total Test Duration | ${r.durationSec.toFixed(2)} s |
| Votes/sec | ${(r.votesSucceeded / r.durationSec).toFixed(1)} |
| Connections/sec | ${(r.connectedCount / (r.durationSec)).toFixed(1)} |

### Connection Establishment Time

${statsTableMd(r.connectStats)}

### JoinPollGroup Invocation Time

${statsTableMd(r.joinStats)}

### Vote REST API Response Time (\`POST /api/polls/{id}/votes\`)

${statsTableMd(r.voteStats)}

### Server → Client Broadcast Latency (\`VoteCountsUpdated\`)

${statsTableMd(r.broadcastStats)}

`;
    }

    if (r.mode === "bidding") {
      md += `## 💰 Bidding Poll Results

| Summary | Value |
|---------|-------|
| Clients Connected | ${r.connectedCount} / ${CONFIG.TOTAL_CLIENTS} |
| Connection Failures | ${r.errors.connect} |
| Join Group Failures | ${r.errors.joinGroup} |
| Hub Action Errors | ${r.errors.action} |
| Ephemeral Bid Changes Succeeded | ${r.bidChangesSucceeded} |
| REST Bids Committed | ${r.restBidsSucceeded} |
| Total Test Duration | ${r.durationSec.toFixed(2)} s |
| Bid Changes/sec | ${(r.bidChangesSucceeded / r.durationSec).toFixed(1)} |
| REST Bids/sec | ${(r.restBidsSucceeded / r.durationSec).toFixed(1)} |

### Connection Establishment Time

${statsTableMd(r.connectStats)}

### JoinPollGroup Invocation Time

${statsTableMd(r.joinStats)}

### NotifyBidChange Hub Invocation Time (Ephemeral)

${statsTableMd(r.bidChangeStats)}

### Bid REST API Response Time (\`POST /api/bidding/bid/{id}\`)

${statsTableMd(r.restBidStats)}

### Server → Client Broadcast Latency (\`ReceiveBubbleData\`)

${statsTableMd(r.broadcastStats)}

`;
    }
  }

  // ── Health Check Summary ──
  md += `---

## ✅ Health Check Summary

| Mode | Check | Result | Value |
|------|-------|--------|-------|
`;

  for (const r of results) {
    if (!r) continue;
    const connectRate = (r.connectedCount / CONFIG.TOTAL_CLIENTS) * 100;
    const p95Connect = parseFloat(r.connectStats.p95 || 0);
    const p95Api = r.mode === "normal"
      ? parseFloat(r.voteStats?.p95 || 0)
      : parseFloat(r.restBidStats?.p95 || 0);
    const p95Broadcast = parseFloat(r.broadcastStats?.p95 || 0);

    const checks = [
      { name: "Connection Success Rate ≥ 95%",   pass: connectRate >= 95,   val: `${connectRate.toFixed(1)}%` },
      { name: "p95 Connect Time < 2000ms",        pass: p95Connect < 2000,  val: `${p95Connect} ms` },
      { name: "p95 API Response Time < 1000ms",   pass: p95Api < 1000,      val: `${p95Api} ms` },
      { name: "p95 Broadcast Latency < 500ms",    pass: p95Broadcast < 500 || p95Broadcast === 0, val: p95Broadcast ? `${p95Broadcast} ms` : "N/A" },
    ];

    for (const c of checks) {
      md += `| ${r.mode.toUpperCase()} | ${c.name} | ${c.pass ? "✅ PASS" : "❌ FAIL"} | ${c.val} |\n`;
    }
  }

  md += `
---

## 📝 Notes

- **Connection Ramp-up**: Clients are staggered over ${CONFIG.CONNECTION_RAMP_MS}ms to simulate realistic arrival patterns (not a thundering herd).
- **Broadcast Latency**: Measured from the moment the client sends an action to when the first SignalR event arrives back at any listening client. This is an approximation — clock skew between the client sending an HTTP request and the hub broadcasting can introduce variance.
- **Session IDs**: Each simulated client uses a unique session ID (\`perf_*\`), so vote deduplication may reject repeat votes on the same question.
- **Errors**: Vote \`409 Conflict\` errors are expected when the same session votes twice on the same question (normal deduplication behavior).

---

*Report generated by LivePoll Performance Test Suite*
`;

  return md;
}

// ═══════════════════════════════════════════════════════════════════════════
//  Main
// ═══════════════════════════════════════════════════════════════════════════
async function main() {
  const opts = parseArgs();

  console.log(`
╔══════════════════════════════════════════════════════════════╗
║         LivePoll SignalR Performance Test Suite              ║
║         ──────────────────────────────────────               ║
║         Clients:  ${String(CONFIG.TOTAL_CLIENTS).padEnd(39)}║
║         Mode:     ${opts.mode.padEnd(39)}║
║         API:      ${CONFIG.API_BASE.padEnd(39)}║
╚══════════════════════════════════════════════════════════════╝`);

  if (!opts.pollId && !opts.biddingPollId) {
    console.error(`
  ✗ ERROR: You must provide at least one poll ID.

  Usage:
    node perf_test.js --mode normal  --pollId <POLL_ID>
    node perf_test.js --mode bidding --biddingPollId <BIDDING_POLL_ID>
    node perf_test.js --mode both    --pollId <POLL_ID> --biddingPollId <BIDDING_POLL_ID>

  Example:
    node perf_test.js --pollId ABC123 --biddingPollId XYZ789
`);
    process.exit(1);
  }

  const results = [];

  if ((opts.mode === "normal" || opts.mode === "both") && opts.pollId) {
    results.push(await runNormalPollTest(opts.pollId));
  }

  if ((opts.mode === "bidding" || opts.mode === "both") && opts.biddingPollId) {
    results.push(await runBiddingPollTest(opts.biddingPollId));
  }

  const filtered = results.filter(Boolean);
  printFinalReport(filtered);

  // ── Generate report.md ──
  const reportPath = resolve(__dirname, "report.md");
  const markdown = generateReportMarkdown(filtered, opts);
  writeFileSync(reportPath, markdown, "utf-8");
  console.log(`\n  📄 Report written to: ${reportPath}`);

  process.exit(0);
}

main().catch((err) => {
  console.error("Fatal error:", err);
  process.exit(1);
});
