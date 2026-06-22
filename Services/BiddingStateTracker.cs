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

    // pollId -> (sessionId -> list of skillIds)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<int>>> _ephemeralSelections = new();
    
    // Timer for 100ms broadcast debounce
    private readonly ConcurrentDictionary<string, byte> _dirtyPolls = new();
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
 
    // pollId -> skillCost
    private readonly ConcurrentDictionary<string, int> _pollCosts = new();

    public void UpdateSelection(string pollId, string sessionId, int skillId, bool isSelected)
    {
        var pollMap = _ephemeralSelections.GetOrAdd(pollId, _ => new ConcurrentDictionary<string, List<int>>());
        var userList = pollMap.GetOrAdd(sessionId, _ => new List<int>());

        int cost = _pollCosts.GetOrAdd(pollId, id =>
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var poll = db.BiddingPolls.Find(id);
            return poll?.SkillCost ?? 20;
        });

        int maxPicks = 100 / cost;

        lock (userList)
        {
            if (isSelected)
            {
                if (userList.Count < maxPicks)
                {
                    userList.Add(skillId);
                }
            }
            else
            {
                userList.Remove(skillId);
            }
        }

        _dirtyPolls[pollId] = 0;
    }

    public Dictionary<int, int> GetCounts(string pollId)
    {
        var result = new Dictionary<int, int>();

        // Find poll cost
        int cost = _pollCosts.GetOrAdd(pollId, id =>
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var poll = db.BiddingPolls.Find(id);
            return poll?.SkillCost ?? 20;
        });

        // 1. Get all sessionIds that are currently active in memory
        var activeSessionIds = new HashSet<string>();
        ConcurrentDictionary<string, List<int>>? pollMap = null;
        if (_ephemeralSelections.TryGetValue(pollId, out pollMap))
        {
            activeSessionIds = pollMap.Keys.ToHashSet();
        }

        // 2. Load votes from DB for committed users, or users who are not active in memory right now
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dbBids = db.SkillBids
                .Where(b => b.BiddingPollId == pollId && (b.IsCommitted || !activeSessionIds.Contains(b.SessionId)))
                .ToList();

            foreach (var bid in dbBids)
            {
                int voteWeight = cost > 0 ? bid.CoinsSpent / cost : 1;
                if (result.ContainsKey(bid.SkillId))
                    result[bid.SkillId] += voteWeight;
                else
                    result[bid.SkillId] = voteWeight;
            }
        }

        // 3. Add current in-memory votes for active users
        if (pollMap != null)
        {
            foreach (var userPair in pollMap)
            {
                var userList = userPair.Value;
                lock (userList)
                {
                    foreach (var skillId in userList)
                    {
                        if (result.ContainsKey(skillId))
                            result[skillId]++;
                        else
                            result[skillId] = 1;
                    }
                }
            }
        }

        return result;
    }

    private async void OnDebounceTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        var dirtyList = _dirtyPolls.Keys.ToList();
        _dirtyPolls.Clear();

        foreach (var pollId in dirtyList)
        {
            var counts = GetCounts(pollId);
            await _hubContext.Clients.Group($"poll_{pollId}").SendAsync("ReceiveBubbleData", new
            {
                pollId,
                counts = counts.ToDictionary(k => k.Key.ToString(), v => v.Value)
            });
        }
    }

    private void OnFlushTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        foreach (var pollId in _ephemeralSelections.Keys)
        {
            _ = FlushToDatabaseAsync(pollId);
        }
    }

    public async Task FlushToDatabaseAsync(string pollId)
    {
        if (!_ephemeralSelections.TryGetValue(pollId, out var pollMap))
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var poll = await db.BiddingPolls.FindAsync(pollId);
        if (poll == null || poll.BiddingClosed)
            return;

        var cohort = poll.Theme.ToLower() == "synergysphere" || poll.Theme.ToLower() == "ss" ? "HR" : "ACADEMIA";
        var cost = poll.SkillCost;

        // Fetch all existing committed sessionIds to ignore them
        var committedSessions = await db.SkillBids
            .Where(b => b.BiddingPollId == pollId && b.IsCommitted)
            .Select(b => b.SessionId)
            .Distinct()
            .ToListAsync();

        var committedSet = new HashSet<string>(committedSessions);

        foreach (var userPair in pollMap)
        {
            var sessionId = userPair.Key;
            if (committedSet.Contains(sessionId))
                continue; // Ignore committed users

            List<int> currentSkills;
            var userList = userPair.Value;
            lock (userList)
            {
                currentSkills = userList.ToList();
            }

            // Sync with DB:
            // 1. Get existing uncommitted bids for this session
            var existingBids = await db.SkillBids
                .Where(b => b.BiddingPollId == pollId && b.SessionId == sessionId && !b.IsCommitted)
                .ToListAsync();

            var currentGroups = currentSkills.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            var existingBidsDict = existingBids.ToDictionary(b => b.SkillId);

            // 2. Add or update
            foreach (var pair in currentGroups)
            {
                var skillId = pair.Key;
                var count = pair.Value;
                var spent = cost * count;

                if (existingBidsDict.TryGetValue(skillId, out var existingBid))
                {
                    existingBid.CoinsSpent = spent;
                }
                else
                {
                    db.SkillBids.Add(new SkillBid
                    {
                        BiddingPollId = pollId,
                        SkillId = skillId,
                        SessionId = sessionId,
                        Cohort = cohort,
                        CoinsSpent = spent,
                        IsCommitted = false
                    });
                }
            }

            // 3. Remove deselected ones
            foreach (var bid in existingBids)
            {
                if (!currentGroups.ContainsKey(bid.SkillId))
                {
                    db.SkillBids.Remove(bid);
                }
            }
        }

        await db.SaveChangesAsync();
    }

    public void ClearPoll(string pollId)
    {
        _ephemeralSelections.TryRemove(pollId, out _);
    }
}
