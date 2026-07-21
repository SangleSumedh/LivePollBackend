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

    // Cache of active poll states
    private readonly ConcurrentDictionary<string, (ActivePollState State, DateTime ExpiresAt)> _activePollCache = new();
    private readonly ConcurrentDictionary<string, Task<ActivePollState?>> _activePollQueries = new();

    // Tracks if existing votes for a question have been loaded from DB
    private readonly ConcurrentDictionary<(string PollId, int QuestionIndex), bool> _questionVotesLoaded = new();
    private readonly ConcurrentDictionary<(string PollId, int QuestionIndex), Task> _loadingTasks = new();

    // Cache of poll kind (bidding vs normal vs not found)
    private readonly ConcurrentDictionary<string, Lazy<Task<PollKind>>> _pollKindCache = new();

    private readonly System.Timers.Timer _broadcastTimer;
    private readonly System.Timers.Timer _dbFlushTimer;

    public VoteStateTracker(IHubContext<PollHub> hubContext, IServiceScopeFactory scopeFactory)
    {
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;

        // 250ms debounce timer for SignalR broadcast
        _broadcastTimer = new System.Timers.Timer(250);
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
        
        // 1. Ensure the votes list for this question is fully loaded into memory
        if (!_questionVotesLoaded.ContainsKey(key))
        {
            var loadTask = _loadingTasks.GetOrAdd(key, async k =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var dbVotes = await db.Votes
                        .Where(v => v.PollId == k.PollId && v.QuestionIndex == k.QuestionIndex)
                        .ToListAsync();

                    var questionVotes = _votes.GetOrAdd(k, _ => new ConcurrentDictionary<string, VoteRequest>());
                    foreach (var v in dbVotes)
                    {
                        questionVotes.TryAdd(v.SessionId, new VoteRequest
                        {
                            QuestionIndex = v.QuestionIndex,
                            OptionIndex = v.OptionIndex,
                            SessionId = v.SessionId,
                            Text = v.SubmittedText
                        });
                    }
                    _questionVotesLoaded[k] = true;
                }
                finally
                {
                    _loadingTasks.TryRemove(k, out _);
                }
            });
            await loadTask;
        }

        // 2. Check in-memory first
        if (_votes.TryGetValue(key, out var questionVotes) && questionVotes.TryGetValue(sessionId, out var vote))
        {
            return vote.OptionIndex;
        }

        return null;
    }

    public async Task<ActivePollState?> GetActivePollStateAsync(string pollId)
    {
        var now = DateTime.UtcNow;
        if (_activePollCache.TryGetValue(pollId, out var cached) && cached.ExpiresAt > now)
        {
            return cached.State;
        }

        // Deduplicate concurrent database fetches
        var fetchTask = _activePollQueries.GetOrAdd(pollId, async id =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var poll = await db.Polls.FindAsync(id);
                if (poll == null) return null;

                var question = await db.Questions
                    .FirstOrDefaultAsync(q => q.PollId == id && q.Index == poll.ActiveQuestionIndex);

                // Return a state object even when no question is found.
                // This lets callers distinguish "poll exists but not active"
                // from "poll doesn't exist at all" (null return).
                // NOTE: Do NOT add `&& question != null` to IsActive — if the question
                // lookup returns null due to an index mismatch or transient DB issue, it
                // would silently block all votes even when the presenter has opened voting.
                // The VoteService already guards against wrong/missing questions via
                // `state.ActiveQuestionIndex < 0` and `request.QuestionIndex != state.ActiveQuestionIndex`.
                var questionType = question?.Type ?? QuestionType.MultipleChoice;

                var state = new ActivePollState
                {
                    PollId = id,
                    IsActive = poll.CurrentQuestionActive,
                    ActiveQuestionIndex = poll.ActiveQuestionIndex,
                    QuestionType = questionType,
                    Status = poll.Status.ToString().ToLower()
                };

                _activePollCache[id] = (state, DateTime.UtcNow.AddSeconds(1));
                return state;
            }
            finally
            {
                _activePollQueries.TryRemove(id, out _);
            }
        });

        return await fetchTask;
    }

    public async Task<PollKind> GetPollKindAsync(string pollId)
    {
        // Check cache first — only positive results (Bidding/Normal) are cached
        if (_pollKindCache.TryGetValue(pollId, out var cached))
            return await cached.Value;

        // Query DB
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        PollKind result;
        if (await db.BiddingPolls.AnyAsync(p => p.Id == pollId))
            result = PollKind.Bidding;
        else if (await db.Polls.AnyAsync(p => p.Id == pollId))
            result = PollKind.Normal;
        else
            return PollKind.NotFound; // Don't cache NotFound — poll may be mid-creation

        // Cache only Bidding/Normal results
        _pollKindCache.TryAdd(pollId, new Lazy<Task<PollKind>>(() => Task.FromResult(result)));
        return result;
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
            _questionVotesLoaded.TryRemove(key, out _);
        }
        _activePollCache.TryRemove(pollId, out _);
        _pollKindCache.TryRemove(pollId, out _);
    }

    public void InvalidateActivePollState(string pollId)
    {
        _activePollCache.TryRemove(pollId, out _);
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
