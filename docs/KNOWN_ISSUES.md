# Known Issues: Bidding Polls

This document outlines known issues, race conditions, and logical flaws identified in the Bidding Polls implementation within the backend.

---

## 1. Loophole in Cohort Lockout Guard

### File Reference
* [BiddingService.cs](file:///D:/Gryphon/LivePollFullstack/LivePollBackend/Services/BiddingService.cs#L284-L287) — `StartQuestionAsync`

### Issue Description
The guard designed to prevent resuming bidding for a cohort that has already closed/committed its bids contains a logical loophole:

```csharp
// Rule: If BiddingClosed is true for this cohort, we block starting any question for it
var hasCommittedForCohort = await _db.SkillBids.AnyAsync(b => b.BiddingPollId == pollId && b.Cohort == cohort && b.IsCommitted);
if (poll.BiddingClosed && hasCommittedForCohort)
    throw new InvalidOperationException($"Bidding has already been closed for cohort '{cohort}' and cannot be resumed.");
```

Because it checks `poll.BiddingClosed && hasCommittedForCohort`, the guard only triggers if `poll.BiddingClosed` is currently `true` on the database record for the poll. 

However, `poll.BiddingClosed` is a global poll-level flag. If bidding for **Cohort B** is currently active, `poll.BiddingClosed` is set to `false`. During this window, if the presenter calls `StartQuestionAsync` for **Cohort A** (which has already committed bids), the check will evaluate to `false && true = false`. The lockout guard is bypassed, and Cohort A's bidding is incorrectly reopened.

### Impact
Allows closed cohorts to reopen and alter their bids if the presenter changes cohorts while another bidding phase is active.

### Proposed Fix
Remove the dependency on the poll-wide `poll.BiddingClosed` flag:
```diff
- if (poll.BiddingClosed && hasCommittedForCohort)
+ if (hasCommittedForCohort)
      throw new InvalidOperationException($"Bidding has already been closed for cohort '{cohort}' and cannot be resumed.");
```

---

## 2. Ephemeral Bid Loss on Stopping Bidding

### File References
* [BiddingService.cs](file:///D:/Gryphon/LivePollFullstack/LivePollBackend/Services/BiddingService.cs#L327-L339) — `StopBiddingAsync`
* [BiddingStateTracker.cs](file:///D:/Gryphon/LivePollFullstack/LivePollBackend/Services/BiddingStateTracker.cs#L169-L171) — `FlushForQuestionAsync`

### Issue Description
When a presenter stops bidding for a cohort, the backend executes the following sequence:

1. Updates the poll's status to inactive and sets `BiddingClosed = true` in the DB:
   ```csharp
   poll.IsBiddingActive = false;
   poll.BiddingClosed = true;
   await _db.SaveChangesAsync();
   ```
2. Instructs the state tracker to flush the latest memory-cached bids to the DB:
   ```csharp
   await _stateTracker.FlushToDatabaseAsync(pollId);
   ```

However, inside `BiddingStateTracker.FlushForQuestionAsync`, the flusher queries the database for the poll and checks:
```csharp
var poll = await db.BiddingPolls.FindAsync(pollId);
if (poll == null || poll.BiddingClosed)
    return;
```

Since step 1 has already saved `BiddingClosed = true` to the database, `FlushForQuestionAsync` always exits early and does nothing. 

### Impact
Any bids placed by users since the last background flush (which runs every 120 seconds) are silently discarded and lost when bidding is closed.

### Proposed Fix
1. Reorder the calls in `StopBiddingAsync` so that `_stateTracker.FlushToDatabaseAsync(pollId)` is called **before** `poll.BiddingClosed = true` is saved to the database.
2. Remove the `poll.BiddingClosed` check from `FlushForQuestionAsync` (or pass a `force` parameter) to ensure final flushes are never blocked:
   ```diff
   var poll = await db.BiddingPolls.FindAsync(pollId);
-  if (poll == null || poll.BiddingClosed)
+  if (poll == null)
       return;
   ```

---

## 3. Multi-Tab Session Disconnection Clears Active Bids

### File References
* [PollHub.cs](file:///D:/Gryphon/LivePollFullstack/LivePollBackend/Hubs/PollHub.cs#L23-L35) — `OnDisconnectedAsync`
* [BiddingStateTracker.cs](file:///D:/Gryphon/LivePollFullstack/LivePollBackend/Services/BiddingStateTracker.cs#L275-L285) — `RemoveSession`

### Issue Description
When a client connection disconnects (e.g. tab closed or network interruption), the SignalR hub handles cleanup by removing all session data:

```csharp
public override async Task OnDisconnectedAsync(Exception? exception)
{
    if (_connections.TryRemove(Context.ConnectionId, out var info))
    {
        ...
        tracker.RemoveSession(info.PollId, info.SessionId);
    }
    await base.OnDisconnectedAsync(exception);
}
```

If a user has multiple browser tabs open under the same `SessionId`, closing a single tab will trigger this cleanup, completely purging their in-memory/ephemeral bids from the state tracker for all other open tabs.

### Impact
A user's live bids will suddenly disappear from the presenter's screen (the bubble count drops) even if they still have the main poll tab open.

### Proposed Fix
Implement connection reference counting per `SessionId` (e.g. tracking how many connection IDs are active for a given `SessionId`), and only invoke `tracker.RemoveSession` when the count drops to zero.
