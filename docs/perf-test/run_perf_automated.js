#!/usr/bin/env node
import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const __filename = fileURLToPath(import.meta.url);
const __dirname  = dirname(__filename);

// ─── Read API base from frontend .env ───────────────────────────────────────
function readApiBaseFromEnv() {
  const envPaths = [
    resolve(__dirname, "..", "..", "..", "Live-poll", ".env"),
    resolve(__dirname, "..", "..", "..", "Live-poll", ".env.local"),
  ];
  for (const p of envPaths) {
    try {
      const content = readFileSync(p, "utf-8");
      const match = content.match(/NEXT_PUBLIC_API_URL\s*=\s*(.+)/m);
      if (match) {
        return match[1].trim().replace(/["']/g, "").replace(/\/$/, "");
      }
    } catch {}
  }
  return "http://localhost:5065";
}

const DEFAULT_API_BASE = readApiBaseFromEnv();

// ─── CLI Argument Parsing ───────────────────────────────────────────────────
function parseArgs() {
  const args = process.argv.slice(2);
  const opts = {
    mode: "both", // normal, bidding, or both
    clients: 150,
    apiBase: DEFAULT_API_BASE,
    cleanup: true,
  };
  for (let i = 0; i < args.length; i++) {
    if (args[i] === "--mode")           opts.mode    = args[++i];
    if (args[i] === "--clients")        opts.clients = parseInt(args[++i], 10);
    if (args[i] === "--apiBase")        opts.apiBase = args[++i].replace(/\/$/, "");
    if (args[i] === "--no-cleanup")     opts.cleanup = false;
  }
  return opts;
}

const opts = parseArgs();
const rand = Math.floor(Math.random() * 1000000);
const adminEmail = `perf_admin_${rand}@livepoll.com`;
const adminPassword = "Password123!";
const adminName = "Automated Perf Test Admin";

async function fetchJson(url, options = {}) {
  const { headers, ...rest } = options;
  const res = await fetch(url, {
    headers: { "Content-Type": "application/json", ...headers },
    ...rest,
  });
  const text = await res.text();
  let json = null;
  try { json = JSON.parse(text); } catch {}
  return { status: res.status, ok: res.ok, json, text };
}

async function run() {
  console.log(`\n======================================================`);
  console.log(`🚀 AUTOMATED CONCURRENCY TEST INITIALIZER`);
  console.log(`   Target Server: ${opts.apiBase}`);
  console.log(`   Clients Count: ${opts.clients}`);
  console.log(`   Test Mode:     ${opts.mode.toUpperCase()}`);
  console.log(`======================================================\n`);

  // 1. Authenticate / Register Admin
  console.log(`🔑 Registering temporary admin account: ${adminEmail}...`);
  let authRes = await fetchJson(`${opts.apiBase}/api/auth/register`, {
    method: "POST",
    body: JSON.stringify({ email: adminEmail, name: adminName, password: adminPassword }),
  });

  if (!authRes.ok) {
    console.log(`🔑 Registration failed or user already exists. Attempting login...`);
    authRes = await fetchJson(`${opts.apiBase}/api/auth/login`, {
      method: "POST",
      body: JSON.stringify({ email: adminEmail, password: adminPassword }),
    });
  }

  if (!authRes.ok) {
    console.error(`❌ Authentication failed. Cannot continue test.`, authRes.text);
    process.exit(1);
  }

  const token = authRes.json.token;
  console.log(`✅ Authenticated successfully.`);

  const headers = { Authorization: `Bearer ${token}` };
  let normalPollId = "";
  let biddingPollId = "";

  // 2. Create and Start Normal Poll if needed
  if (opts.mode === "normal" || opts.mode === "both") {
    console.log(`🗳️ Creating test normal poll...`);
    const createPollRes = await fetchJson(`${opts.apiBase}/api/polls`, {
      method: "POST",
      headers,
      body: JSON.stringify({
        title: "Automated Normal Concurrency Test Poll",
        theme: "default",
        questions: [
          {
            text: "What is your favorite language?",
            options: ["C#", "JavaScript", "Python", "Rust"]
          }
        ]
      }),
    });

    if (!createPollRes.ok) {
      console.error(`❌ Failed to create normal poll.`, createPollRes.text);
      process.exit(1);
    }

    normalPollId = createPollRes.json.id;
    console.log(`✅ Created normal poll with ID: ${normalPollId}`);

    console.log(`🗳️ Starting voting on question index 0...`);
    const startPollRes = await fetchJson(`${opts.apiBase}/api/polls/${normalPollId}/start`, {
      method: "POST",
      headers,
      body: JSON.stringify({ questionIndex: 0 }),
    });

    if (!startPollRes.ok) {
      console.error(`❌ Failed to start voting on normal poll.`, startPollRes.text);
      process.exit(1);
    }
    console.log(`✅ Normal poll active.`);
  }

  // 3. Create and Start Bidding Poll if needed
  if (opts.mode === "bidding" || opts.mode === "both") {
    console.log(`💰 Creating test bidding poll...`);
    const createBidRes = await fetchJson(`${opts.apiBase}/api/bidding/polls`, {
      method: "POST",
      headers,
      body: JSON.stringify({
        title: "Automated Bidding Concurrency Test Poll",
        theme: "synergy_sphere",
        questions: [
          {
            text: "Rank these leadership skills",
            index: 0,
            skills: [
              { name: "Strategic Thinking", category: "Leadership", index: 0 },
              { name: "Empathy", category: "Leadership", index: 1 },
              { name: "Conflict Resolution", category: "Leadership", index: 2 },
              { name: "Decisiveness", category: "Leadership", index: 3 }
            ]
          }
        ]
      }),
    });

    if (!createBidRes.ok) {
      console.error(`❌ Failed to create bidding poll.`, createBidRes.text);
      process.exit(1);
    }

    biddingPollId = createBidRes.json.id;
    console.log(`✅ Created bidding poll with ID: ${biddingPollId}`);

    console.log(`💰 Starting bidding on question index 0 (cohort HR)...`);
    const startBidRes = await fetchJson(`${opts.apiBase}/api/bidding/start/${biddingPollId}`, {
      method: "POST",
      headers,
      body: JSON.stringify({ questionIndex: 0, cohort: "HR" }),
    });

    if (!startBidRes.ok) {
      console.error(`❌ Failed to start bidding on bidding poll.`, startBidRes.text);
      process.exit(1);
    }
    console.log(`✅ Bidding poll active.`);
  }

  // 4. Run the performance test
  console.log(`\n🔥 Running performance test script...`);
  const perfArgs = [
    resolve(__dirname, "perf_test.js"),
    "--mode", opts.mode,
    "--clients", String(opts.clients),
    "--apiBase", opts.apiBase,
  ];
  if (normalPollId) {
    perfArgs.push("--pollId", normalPollId);
  }
  if (biddingPollId) {
    perfArgs.push("--biddingPollId", biddingPollId);
  }

  const testProcess = spawn("node", perfArgs, { stdio: "inherit" });

  testProcess.on("close", async (code) => {
    console.log(`\n🏁 Performance test finished with code: ${code}`);

    // 5. Cleanup
    if (opts.cleanup) {
      console.log(`\n🧹 Cleaning up test polls...`);
      if (normalPollId) {
        const delRes = await fetchJson(`${opts.apiBase}/api/polls/${normalPollId}`, {
          method: "DELETE",
          headers,
        });
        if (delRes.ok) console.log(`   ✓ Deleted normal poll: ${normalPollId}`);
      }
      if (biddingPollId) {
        const delRes = await fetchJson(`${opts.apiBase}/api/bidding/polls/${biddingPollId}`, {
          method: "DELETE",
          headers,
        });
        if (delRes.ok) console.log(`   ✓ Deleted bidding poll: ${biddingPollId}`);
      }
      console.log(`✨ Cleanup complete.`);
    }

    process.exit(code);
  });
}

run().catch((err) => {
  console.error("Fatal automated test runner error:", err);
  process.exit(1);
});
