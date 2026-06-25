using System.Collections.Concurrent;
using System.Timers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using live_poll_backend.Data;
using live_poll_backend.Hubs;
using live_poll_backend.Models.Entities;

namespace live_poll_backend.Services;

public class BiddingStateTracker
{
    private readonly IHubContext<PollHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;

    // (pollId, questionIndex) -> (sessionId -> Dict<biddingSkillId, coins>)
    private readonly ConcurrentDictionary<(string PollId, int QuestionIndex), ConcurrentDictionary<string, ConcurrentDictionary<int, int>>> _ephemeralSelections = new();
    
    // Timer for 100ms broadcast debounce
    // Tracks dirty (pollId, questionIndex)
    private readonly ConcurrentDictionary<(string PollId, int QuestionIndex), byte> _dirtyQuestions = new();
    private readonly System.Timers.Timer _debounceTimer;
 
    // Timer for 2-minute DB flush
    private readonly System.Timers.Timer _flushTimer;
 
    public BiddingStateTracker(IHubContext<PollHub> hubContext, IServiceScopeFactory scopeFactory)
    {
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
 
        _debounceTimer = new System.Timers.Timer(100);
        _debounceTimer.Elapsed += OnDebounceTimerElapsed;
        _debounceTimer.AutoReset = true;
        _debounceTimer.Start();
 
        // 2-minute periodic safety net flush
        _flushTimer = new System.Timers.Timer(120000); 
        _flushTimer.Elapsed += OnFlushTimerElapsed;
        _flushTimer.AutoReset = true;
        _flushTimer.Start();
    }

    public void UpdateBid(string pollId, int questionIndex, string sessionId, int biddingSkillId, int amount)
    {
        var key = (pollId, questionIndex);
        var qMap = _ephemeralSelections.GetOrAdd(key, _ => new ConcurrentDictionary<string, ConcurrentDictionary<int, int>>());
        var userBids = qMap.GetOrAdd(sessionId, _ => new ConcurrentDictionary<int, int>());

        if (amount <= 0)
        {
            userBids.TryRemove(biddingSkillId, out _);
        }
        else
        {
            userBids[biddingSkillId] = amount;
        }

        _dirtyQuestions[key] = 0;
    }

    public Dictionary<int, int> GetCounts(string pollId, int questionIndex)
    {
        var result = new Dictionary<int, int>();
        var key = (pollId, questionIndex);

        // 1. Load committed bids from DB
        var committedSessions = new HashSet<string>();
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dbBids = db.SkillBids
                .Where(b => b.BiddingPollId == pollId && b.QuestionIndex == questionIndex && b.IsCommitted)
                .ToList();

            foreach (var bid in dbBids)
            {
                committedSessions.Add(bid.SessionId);
                if (result.ContainsKey(bid.BiddingSkillId))
                    result[bid.BiddingSkillId] += bid.CoinsSpent;
                else
                    result[bid.BiddingSkillId] = bid.CoinsSpent;
            }
        }

        // 2. Add current in-memory bids for active users (only if they are NOT committed in the DB)
        if (_ephemeralSelections.TryGetValue(key, out var qMap))
        {
            foreach (var userPair in qMap)
            {
                var sessionId = userPair.Key;
                // Avoid double counting by skipping sessions that already have committed bids in the DB
                if (committedSessions.Contains(sessionId))
                    continue;

                foreach (var bidPair in userPair.Value)
                {
                    var skillId = bidPair.Key;
                    var coins = bidPair.Value;
                    if (result.ContainsKey(skillId))
                        result[skillId] += coins;
                    else
                        result[skillId] = coins;
                }
            }
        }

        return result;
    }

    private async void OnDebounceTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        var dirtyList = _dirtyQuestions.Keys.ToList();
        _dirtyQuestions.Clear();

        foreach (var key in dirtyList)
        {
            var counts = GetCounts(key.PollId, key.QuestionIndex);
            await _hubContext.Clients.Group($"poll_{key.PollId}").SendAsync("ReceiveBubbleData", new
            {
                pollId = key.PollId,
                questionIndex = key.QuestionIndex,
                counts = counts.ToDictionary(k => k.Key.ToString(), v => v.Value)
            });
        }
    }

    private void OnFlushTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        foreach (var key in _ephemeralSelections.Keys)
        {
            _ = FlushForQuestionAsync(key.PollId, key.QuestionIndex);
        }
    }

    public async Task FlushForQuestionAsync(string pollId, int questionIndex)
    {
        var key = (pollId, questionIndex);
        if (!_ephemeralSelections.TryGetValue(key, out var qMap))
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var poll = await db.BiddingPolls.FindAsync(pollId);
        if (poll == null || poll.BiddingClosed)
            return;

        var cohort = poll.CurrentCohort;
        if (string.IsNullOrEmpty(cohort))
            return;

        // Fetch all existing committed sessionIds for this cohort (across all questions) to ignore them if committed
        var committedSessions = await db.SkillBids
            .Where(b => b.BiddingPollId == pollId && b.Cohort == cohort && b.IsCommitted)
            .Select(b => b.SessionId)
            .Distinct()
            .ToListAsync();

        var committedSet = new HashSet<string>(committedSessions);

        foreach (var userPair in qMap)
        {
            var sessionId = userPair.Key;
            if (committedSet.Contains(sessionId))
                continue; // Ignore committed users

            var currentBids = userPair.Value.ToDictionary(k => k.Key, v => v.Value);

            // Sync with DB:
            // 1. Get existing uncommitted bids for this session, cohort, question
            var existingBids = await db.SkillBids
                .Where(b => b.BiddingPollId == pollId && b.SessionId == sessionId && b.Cohort == cohort && b.QuestionIndex == questionIndex && !b.IsCommitted)
                .ToListAsync();

            var existingBidsDict = existingBids.ToDictionary(b => b.BiddingSkillId);

            // 2. Add or update
            foreach (var pair in currentBids)
            {
                var skillId = pair.Key;
                var coins = pair.Value;

                if (existingBidsDict.TryGetValue(skillId, out var existingBid))
                {
                    existingBid.CoinsSpent = coins;
                }
                else
                {
                    db.SkillBids.Add(new SkillBid
                    {
                        BiddingPollId = pollId,
                        BiddingSkillId = skillId,
                        SessionId = sessionId,
                        Cohort = cohort,
                        CoinsSpent = coins,
                        QuestionIndex = questionIndex,
                        IsCommitted = false
                    });
                }
            }

            // 3. Remove deselected ones
            foreach (var bid in existingBids)
            {
                if (!currentBids.ContainsKey(bid.BiddingSkillId))
                {
                    db.SkillBids.Remove(bid);
                }
            }
        }

        await db.SaveChangesAsync();
    }

    public async Task FlushToDatabaseAsync(string pollId)
    {
        // Flush all questions for this poll
        var keysToFlush = _ephemeralSelections.Keys.Where(k => k.PollId == pollId).ToList();
        foreach (var key in keysToFlush)
        {
            await FlushForQuestionAsync(key.PollId, key.QuestionIndex);
        }
    }

    public Dictionary<int, int> GetUserBidsForQuestion(string pollId, int questionIndex, string sessionId)
    {
        var key = (pollId, questionIndex);
        if (_ephemeralSelections.TryGetValue(key, out var qMap) && qMap.TryGetValue(sessionId, out var userBids))
        {
            return userBids.ToDictionary(k => k.Key, v => v.Value);
        }
        return new Dictionary<int, int>();
    }

    /// <summary>
    /// Returns true if the session already has ephemeral data in the tracker for ANY question in this poll.
    /// Used to avoid overwriting current tracker data with stale DB data on reconnect.
    /// </summary>
    public bool HasSessionData(string pollId, string sessionId)
    {
        return _ephemeralSelections.Any(kvp =>
            kvp.Key.PollId == pollId &&
            kvp.Value.ContainsKey(sessionId));
    }

    /// <summary>
    /// Removes all ephemeral data for a specific session in a poll.
    /// Called when a user disconnects from SignalR.
    /// </summary>
    public void RemoveSession(string pollId, string sessionId)
    {
        var keysToCheck = _ephemeralSelections.Keys.Where(k => k.PollId == pollId).ToList();
        foreach (var key in keysToCheck)
        {
            if (_ephemeralSelections.TryGetValue(key, out var qMap))
            {
                qMap.TryRemove(sessionId, out _);
            }
        }
    }

    public void ClearPoll(string pollId)
    {
        var keysToRemove = _ephemeralSelections.Keys.Where(k => k.PollId == pollId).ToList();
        foreach (var key in keysToRemove)
        {
            _ephemeralSelections.TryRemove(key, out _);
        }
    }
}
