# LivePoll — SignalR Performance Test Suite

Simulates **150 concurrent SignalR WebSocket connections** against the LivePoll backend and produces detailed latency / throughput statistics for both **Normal Polls** and **Bidding Polls**.

## What It Measures

| Category | Metric |
|---|---|
| **Connection** | Per-client establishment time, ramp-up duration, success rate |
| **SignalR Hub** | `JoinPollGroup` latency, `NotifyBidChange` invocation time |
| **REST API** | Vote casting (`POST /api/polls/{id}/votes`) response time |
| | Bid placement (`POST /api/bidding/bid/{id}`) response time |
| **Broadcast** | Server → Client real-time event propagation latency |
| **Aggregates** | Min, Max, Mean, Median (p50), p90, p95, p99 for all metrics |
| **Throughput** | Actions/sec, Connections/sec |
| **Health** | Pass/fail checks (connection rate ≥ 95%, p95 < thresholds) |

## Prerequisites

1. Backend running at `http://localhost:5065` (or provide `--apiBase`)
2. At least one **Normal Poll** and/or **Bidding Poll** already created in the system
3. For Normal Polls: voting should be started on the poll (so votes can be cast)
4. For Bidding Polls: bidding should be started on a question

## Setup

```bash
cd LivePollBackend/docs/perf-test
npm install
```

## Usage

```bash
# Test both normal + bidding polls
node perf_test.js --pollId ABC123 --biddingPollId XYZ789

# Test only normal poll voting
node perf_test.js --mode normal --pollId ABC123

# Test only bidding poll
node perf_test.js --mode bidding --biddingPollId XYZ789

# Custom client count and API base
node perf_test.js --clients 200 --apiBase https://your-server.com --pollId ABC123
```

### CLI Arguments

| Argument | Default | Description |
|---|---|---|
| `--mode` | `both` | `normal`, `bidding`, or `both` |
| `--pollId` | — | ID of a Normal Poll (required for `normal`/`both`) |
| `--biddingPollId` | — | ID of a Bidding Poll (required for `bidding`/`both`) |
| `--apiBase` | `http://localhost:5065` | Backend API base URL |
| `--clients` | `150` | Number of concurrent connections |

## npm scripts

```bash
npm test              # Same as node perf_test.js (requires CLI args)
npm run test:normal   # --mode normal (still requires --pollId)
npm run test:bidding  # --mode bidding (still requires --biddingPollId)
npm run test:both     # --mode both
```

## Sample Output

```
╔════════════════════════════════════════════════════════════════╗
║  NORMAL POLL — Performance Test                                ║
╚════════════════════════════════════════════════════════════════╝
  Poll ID:   ABC123
  Clients:   150
  Actions:   3 votes/client

  ── 2. Establishing connections ──────────────────────────────
  ✓ 150/150 connected (0 failures)
  ✓ Total ramp-up time: 5032 ms
  ┌─ Connection Establishment Time (n=150)
  │  Min ........... 12.34 ms
  │  Max ........... 89.12 ms
  │  Mean .......... 34.56 ms
  │  Median (p50) .. 31.22 ms
  │  p90 ........... 62.11 ms
  │  p95 ........... 71.34 ms
  └─ p99 ........... 85.00 ms

  ── 4. Casting votes (REST API) ──────────────────────────────
  ✓ Votes: 450/450 succeeded
  ┌─ Vote REST API Response Time (n=450)
  │  Min ........... 5.12 ms
  │  ...
```

## Test Architecture

1. **Ramp-up phase** — Connections are staggered over 5 seconds to simulate realistic arrival
2. **Group join** — All clients invoke `JoinPollGroup` with unique session IDs
3. **Action phase** — Concurrent vote/bid bursts across all connected clients
4. **Broadcast tracking** — Every client listens for SignalR events and measures propagation delay
5. **Teardown** — All connections gracefully disconnected
6. **Report** — Percentile statistics + pass/fail health checks
