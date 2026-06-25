using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using live_poll_backend.Data;
using live_poll_backend.Hubs;
using live_poll_backend.Models.DTOs;
using live_poll_backend.Models.Entities;
using live_poll_backend.Models.Enums;

namespace live_poll_backend.Services;

public class VoteStateTracker : IVoteStateTracker, IDisposable
{
    private readonly IHubContext<PollHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;

    // (pollId, questionIndex) -> (sessionId -> VoteRequest)
    private readonly ConcurrentDictionary<(string PollId, int QuestionIndex), ConcurrentDictionary<string, VoteRequest>> _votes = new();

    // Set of dirty (pollId, questionIndex) to broadcast
    private readonly ConcurrentDictionary<(string PollId, int QuestionIndex), byte> _dirtyPollQuestions = new();

    private readonly System.Timers.Timer _broadcastTimer;
    private readonly System.Timers.Timer _dbFlushTimer;

    public VoteStateTracker(IHubContext<PollHub> hubContext, IServiceScopeFactory scopeFactory)
    {
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;

        // 100ms debounce timer for SignalR broadcast
        _broadcastTimer = new System.Timers.Timer(100);
        _broadcastTimer.Elapsed += OnBroadcastTimerElapsed;
        _broadcastTimer.AutoReset = true;
        _broadcastTimer.Start();

        // 30 seconds database batch flush timer
        _dbFlushTimer = new System.Timers.Timer(30000);
        _dbFlushTimer.Elapsed += OnDbFlushTimerElapsed;
        _dbFlushTimer.AutoReset = true;
        _dbFlushTimer.Start();
    }

    public void RecordVote(string pollId, VoteRequest vote)
    {
        var key = (pollId, vote.QuestionIndex);
        var questionVotes = _votes.GetOrAdd(key, _ => new ConcurrentDictionary<string, VoteRequest>());

        if (questionVotes.TryAdd(vote.SessionId, vote))
        {
            _dirtyPollQuestions[key] = 0;
        }
    }

    public async Task<int?> CheckVoteStatusAsync(string pollId, int questionIndex, string sessionId)
    {
        var key = (pollId, questionIndex);
        
        // 1. Check in-memory first
        if (_votes.TryGetValue(key, out var questionVotes) && questionVotes.TryGetValue(sessionId, out var vote))
        {
            return vote.OptionIndex;
        }

        // 2. Fallback to database
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbVote = await db.Votes
            .Where(v => v.PollId == pollId && v.QuestionIndex == questionIndex && v.SessionId == sessionId)
            .Select(v => v.OptionIndex)
            .FirstOrDefaultAsync();

        return dbVote;
    }

    public Dictionary<string, int> GetVoteCounts(string pollId, int questionIndex)
    {
        var result = new Dictionary<string, int>();

        // 1. Load committed counts from DB
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dbCounts = db.VoteCounts
                .Where(vc => vc.PollId == pollId && vc.QuestionIndex == questionIndex)
                .ToList();

            foreach (var vc in dbCounts)
            {
                result[$"{vc.QuestionIndex}_{vc.OptionIndex}"] = vc.Count;
            }
        }

        // 2. Incorporate uncommitted in-memory votes
        var key = (pollId, questionIndex);
        if (_votes.TryGetValue(key, out var questionVotes))
        {
            foreach (var vote in questionVotes.Values)
            {
                if (vote.OptionIndex.HasValue)
                {
                    var countKey = $"{questionIndex}_{vote.OptionIndex.Value}";
                    if (result.ContainsKey(countKey))
                        result[countKey]++;
                    else
                        result[countKey] = 1;
                }
            }
        }

        return result;
    }

    public async Task FlushToDatabaseAsync(string pollId)
    {
        var keysToFlush = _votes.Keys.Where(k => k.PollId == pollId).ToList();
        foreach (var key in keysToFlush)
        {
            await FlushForQuestionAsync(key.PollId, key.QuestionIndex);
        }
    }

    public async Task FlushAllPendingAsync()
    {
        var keysToFlush = _votes.Keys.ToList();
        foreach (var key in keysToFlush)
        {
            await FlushForQuestionAsync(key.PollId, key.QuestionIndex);
        }
    }

    public void ClearPoll(string pollId)
    {
        var keysToRemove = _votes.Keys.Where(k => k.PollId == pollId).ToList();
        foreach (var key in keysToRemove)
        {
            _votes.TryRemove(key, out _);
        }
    }

    private async Task FlushForQuestionAsync(string pollId, int questionIndex)
    {
        var key = (pollId, questionIndex);
        if (!_votes.TryGetValue(key, out var questionVotes) || questionVotes.IsEmpty)
            return;

        // Move items out of the concurrent dictionary to flush them safely
        var votesToPersist = new List<VoteRequest>();
        foreach (var sessionId in questionVotes.Keys.ToList())
        {
            if (questionVotes.TryRemove(sessionId, out var vote))
            {
                votesToPersist.Add(vote);
            }
        }

        if (votesToPersist.Count == 0)
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Filter out duplicate votes that might already be saved in DB
        var sessionIds = votesToPersist.Select(v => v.SessionId).ToList();
        var existingSessionIds = await db.Votes
            .Where(v => v.PollId == pollId && v.QuestionIndex == questionIndex && sessionIds.Contains(v.SessionId))
            .Select(v => v.SessionId)
            .ToListAsync();

        var existingSet = new HashSet<string>(existingSessionIds);
        var uniqueVotesToSave = votesToPersist.Where(v => !existingSet.Contains(v.SessionId)).ToList();

        if (uniqueVotesToSave.Count == 0)
            return;

        // 1. Bulk insert votes
        var votesList = uniqueVotesToSave.Select(v => new Vote
        {
            PollId = pollId,
            QuestionIndex = questionIndex,
            OptionIndex = v.OptionIndex,
            SubmittedText = v.Text?.Trim(),
            SessionId = v.SessionId,
            VotedAt = DateTime.UtcNow
        }).ToList();

        db.Votes.AddRange(votesList);

        // 2. Update VoteCounts
        var optionCounts = uniqueVotesToSave
            .Where(v => v.OptionIndex.HasValue)
            .GroupBy(v => v.OptionIndex!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var kvp in optionCounts)
        {
            var optionIndex = kvp.Key;
            var increment = kvp.Value;

            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO ""VoteCounts"" (""PollId"", ""QuestionIndex"", ""OptionIndex"", ""Count"")
                  VALUES ({pollId}, {questionIndex}, {optionIndex}, {increment})
                  ON CONFLICT (""PollId"", ""QuestionIndex"", ""OptionIndex"")
                  DO UPDATE SET ""Count"" = ""VoteCounts"".""Count"" + {increment}");
        }

        await db.SaveChangesAsync();
    }

    private async void OnBroadcastTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        var dirtyList = _dirtyPollQuestions.Keys.ToList();
        _dirtyPollQuestions.Clear();

        foreach (var key in dirtyList)
        {
            var voteCounts = GetVoteCounts(key.PollId, key.QuestionIndex);
            await _hubContext.Clients.Group($"poll_{key.PollId}").SendAsync("VoteCountsUpdated", new { pollId = key.PollId, voteCounts });
        }
    }

    private async void OnDbFlushTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            await FlushAllPendingAsync();
        }
        catch
        {
            // Fail silently to prevent timer thread crash
        }
    }

    public void Dispose()
    {
        _broadcastTimer.Dispose();
        _dbFlushTimer.Dispose();
    }
}
