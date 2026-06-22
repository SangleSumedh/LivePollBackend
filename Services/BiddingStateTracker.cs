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

    // pollId -> (sessionId -> set of skillIds)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, HashSet<int>>> _ephemeralSelections = new();
    
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

    public void UpdateSelection(string pollId, string sessionId, int skillId, bool isSelected)
    {
        var pollMap = _ephemeralSelections.GetOrAdd(pollId, _ => new ConcurrentDictionary<string, HashSet<int>>());
        var userSet = pollMap.GetOrAdd(sessionId, _ => new HashSet<int>());

        lock (userSet)
        {
            if (isSelected)
            {
                userSet.Add(skillId);
            }
            else
            {
                userSet.Remove(skillId);
            }
        }

        _dirtyPolls[pollId] = 0;
    }

    public Dictionary<int, int> GetCounts(string pollId)
    {
        var result = new Dictionary<int, int>();
        if (!_ephemeralSelections.TryGetValue(pollId, out var pollMap))
        {
            return result;
        }

        foreach (var userPair in pollMap)
        {
            var userSet = userPair.Value;
            lock (userSet)
            {
                foreach (var skillId in userSet)
                {
                    if (result.ContainsKey(skillId))
                        result[skillId]++;
                    else
                        result[skillId] = 1;
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
            var userSet = userPair.Value;
            lock (userSet)
            {
                currentSkills = userSet.ToList();
            }

            // Sync with DB:
            // 1. Get existing uncommitted bids for this session
            var existingBids = await db.SkillBids
                .Where(b => b.BiddingPollId == pollId && b.SessionId == sessionId && !b.IsCommitted)
                .ToListAsync();

            var existingSkillIds = existingBids.Select(b => b.SkillId).ToHashSet();

            // 2. Add missing ones
            foreach (var skillId in currentSkills)
            {
                if (!existingSkillIds.Contains(skillId))
                {
                    db.SkillBids.Add(new SkillBid
                    {
                        BiddingPollId = pollId,
                        SkillId = skillId,
                        SessionId = sessionId,
                        Cohort = cohort,
                        CoinsSpent = cost,
                        IsCommitted = false
                    });
                }
            }

            // 3. Remove deselected ones
            var currentSkillsSet = currentSkills.ToHashSet();
            foreach (var bid in existingBids)
            {
                if (!currentSkillsSet.Contains(bid.SkillId))
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
