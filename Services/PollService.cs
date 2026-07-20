using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using live_poll_backend.Data;
using live_poll_backend.Exceptions;
using live_poll_backend.Hubs;
using live_poll_backend.Models.DTOs;
using live_poll_backend.Models.Entities;
using live_poll_backend.Models.Enums;

namespace live_poll_backend.Services;

public class PollService : IPollService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<PollHub> _hubContext;
    private readonly WordCloudManager _wordCloudManager;
    private readonly IVoteStateTracker _voteTracker;

    public PollService(AppDbContext db, IHubContext<PollHub> hubContext, WordCloudManager wordCloudManager, IVoteStateTracker voteTracker)
    {
        _db = db;
        _hubContext = hubContext;
        _wordCloudManager = wordCloudManager;
        _voteTracker = voteTracker;
    }

    public async Task<List<PollResponse>> GetPollsAsync(string userId)
    {
        var polls = await _db.Polls
            .Where(p => p.CreatedBy == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new PollResponse
            {
                Id = p.Id,
                Title = p.Title,
                CreatedBy = p.CreatedBy,
                CreatedByEmail = p.CreatedByEmail,
                CreatedByName = p.CreatedByName,
                Status = p.Status.ToString(),
                ActiveQuestionIndex = p.ActiveQuestionIndex,
                CurrentQuestionActive = p.CurrentQuestionActive,
                Theme = p.Theme,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            })
            .ToListAsync();

        return polls;
    }

    public async Task<PollResponse> GetPollByIdAsync(string pollId)
    {
        var poll = await _db.Polls
            .Include(p => p.Questions.OrderBy(q => q.Index))
                .ThenInclude(q => q.Options.OrderBy(o => o.Index))
            .Include(p => p.VoteCounts)
            .Include(p => p.WordCloudCounts)
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        // Populate the in-memory cache from the DB snapshot for word cloud questions
        foreach (var question in poll.Questions.Where(q => q.Type == QuestionType.WordCloud))
        {
            var dbCountsForQuestion = poll.WordCloudCounts
                .Where(w => w.QuestionIndex == question.Index)
                .ToDictionary(w => w.Word, w => w.Count, StringComparer.OrdinalIgnoreCase);

            _wordCloudManager.LoadFromDb(poll.Id, question.Index, dbCountsForQuestion);
        }

        return MapToResponse(poll);
    }

    public async Task<PollResponse> CreatePollAsync(CreatePollRequest request, string userId, string userEmail, string userName)
    {
        var pollId = await GenerateUniqueIdAsync();

        var poll = new Poll
        {
            Id = pollId,
            Title = request.Title.Trim(),
            Theme = string.IsNullOrWhiteSpace(request.Theme) ? "default" : request.Theme.Trim(),
            CreatedBy = userId,
            CreatedByEmail = userEmail,
            CreatedByName = string.IsNullOrWhiteSpace(userName) ? "Anonymous" : userName,
            Status = PollStatus.Draft,
            ActiveQuestionIndex = -1,
            CurrentQuestionActive = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        int qIdx = 0;
        foreach (var qDto in request.Questions)
        {
            var qType = Enum.TryParse<QuestionType>(qDto.Type, true, out var parsedType) 
                ? parsedType 
                : QuestionType.MultipleChoice;

            var question = new Question
            {
                PollId = pollId,
                Index = qIdx,
                Text = qDto.Text.Trim(),
                Type = qType
            };

            int oIdx = 0;
            if (qType == QuestionType.MultipleChoice)
            {
                foreach (var optText in qDto.Options.Where(o => !string.IsNullOrWhiteSpace(o)))
                {
                    question.Options.Add(new Option
                    {
                        Index = oIdx,
                        Text = optText.Trim()
                    });
                    oIdx++;
                }
            }

            poll.Questions.Add(question);

            if (qType == QuestionType.MultipleChoice)
            {
                // Create VoteCount rows for each option
                for (int o = 0; o < oIdx; o++)
                {
                    poll.VoteCounts.Add(new VoteCount
                    {
                        PollId = pollId,
                        QuestionIndex = qIdx,
                        OptionIndex = o,
                        Count = 0
                    });
                }
            }

            qIdx++;
        }

        _db.Polls.Add(poll);
        await _db.SaveChangesAsync();

        var response = MapToResponse(poll);
        await BroadcastPollUpdated(response);
        return response;
    }

    public async Task<PollResponse> UpdatePollAsync(string pollId, UpdatePollRequest request)
    {
        var poll = await _db.Polls
            .Include(p => p.Questions.OrderBy(q => q.Index))
                .ThenInclude(q => q.Options.OrderBy(o => o.Index))
            .Include(p => p.VoteCounts)
            .Include(p => p.WordCloudCounts)
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        // Invalidate word cloud cache keys for old questions
        foreach (var q in poll.Questions.Where(q => q.Type == QuestionType.WordCloud))
        {
            _wordCloudManager.InvalidateKey(pollId, q.Index);
        }

        // Remove existing questions/options (cascade deletes options)
        _db.Questions.RemoveRange(poll.Questions);

        // Remove existing vote counts
        _db.VoteCounts.RemoveRange(poll.VoteCounts);

        // Remove existing word cloud counts
        _db.WordCloudCounts.RemoveRange(poll.WordCloudCounts);

        poll.Title = request.Title.Trim();
        poll.Theme = string.IsNullOrWhiteSpace(request.Theme) ? "default" : request.Theme.Trim();
        poll.UpdatedAt = DateTime.UtcNow;

        int qIdx = 0;
        foreach (var qDto in request.Questions)
        {
            var qType = Enum.TryParse<QuestionType>(qDto.Type, true, out var parsedType) 
                ? parsedType 
                : QuestionType.MultipleChoice;

            var question = new Question
            {
                PollId = pollId,
                Index = qIdx,
                Text = qDto.Text.Trim(),
                Type = qType
            };

            int oIdx = 0;
            if (qType == QuestionType.MultipleChoice)
            {
                foreach (var optText in qDto.Options.Where(o => !string.IsNullOrWhiteSpace(o)))
                {
                    question.Options.Add(new Option
                    {
                        Index = oIdx,
                        Text = optText.Trim()
                    });
                    oIdx++;
                }
            }

            poll.Questions.Add(question);

            if (qType == QuestionType.MultipleChoice)
            {
                for (int o = 0; o < oIdx; o++)
                {
                    poll.VoteCounts.Add(new VoteCount
                    {
                        PollId = pollId,
                        QuestionIndex = qIdx,
                        OptionIndex = o,
                        Count = 0
                    });
                }
            }

            qIdx++;
        }

        await _db.SaveChangesAsync();

        var response = MapToResponse(poll);
        await BroadcastPollUpdated(response);
        return response;
    }

    public async Task DeletePollAsync(string pollId)
    {
        var poll = await _db.Polls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        _db.Polls.Remove(poll); // Cascade deletes questions, options, votes, voteCounts
        await _db.SaveChangesAsync();
    }

    public async Task RestartPollAsync(string pollId)
    {
        var poll = await _db.Polls
            .Include(p => p.Questions)
            .Include(p => p.Votes)
            .Include(p => p.VoteCounts)
            .Include(p => p.WordCloudCounts)
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        _voteTracker.ClearPoll(pollId);

        // 1. Invalidate in-memory word cloud caches first to prevent concurrent submission ghost writes
        foreach (var q in poll.Questions.Where(q => q.Type == QuestionType.WordCloud))
        {
            _wordCloudManager.InvalidateKey(pollId, q.Index);
        }

        // 2. Remove all DB votes
        _db.Votes.RemoveRange(poll.Votes);

        // 3. Remove all DB word cloud counts
        _db.WordCloudCounts.RemoveRange(poll.WordCloudCounts);

        // 4. Reset multiple-choice vote counts to zero
        foreach (var vc in poll.VoteCounts)
        {
            vc.Count = 0;
        }

        // 5. Reset poll state
        poll.Status = PollStatus.Draft;
        poll.ActiveQuestionIndex = -1;
        poll.CurrentQuestionActive = false;
        poll.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await BroadcastToGroup(pollId, "PollUpdated", new { pollId, message = "Poll has been reset" });
    }

    public async Task StartVotingAsync(string pollId, int questionIndex)
    {
        var poll = await _db.Polls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        poll.Status = PollStatus.Live;
        poll.ActiveQuestionIndex = questionIndex >= 0 ? questionIndex : 0;
        poll.CurrentQuestionActive = true;
        poll.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        _voteTracker.InvalidateActivePollState(pollId);

        await BroadcastToGroup(pollId, "PollUpdated", new { pollId, status = "live", activeQuestionIndex = poll.ActiveQuestionIndex, currentQuestionActive = true });
    }

    public async Task StopVotingAsync(string pollId)
    {
        var poll = await _db.Polls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        await _voteTracker.FlushToDatabaseAsync(pollId);

        poll.CurrentQuestionActive = false;
        poll.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        _voteTracker.InvalidateActivePollState(pollId);

        await BroadcastToGroup(pollId, "PollUpdated", new { pollId, currentQuestionActive = false });
    }

    public async Task NextQuestionAsync(string pollId)
    {
        var poll = await _db.Polls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        await _voteTracker.FlushToDatabaseAsync(pollId);

        poll.ActiveQuestionIndex += 1;
        poll.CurrentQuestionActive = false;
        poll.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        _voteTracker.InvalidateActivePollState(pollId);

        await BroadcastToGroup(pollId, "PollUpdated", new { pollId, activeQuestionIndex = poll.ActiveQuestionIndex, currentQuestionActive = false });
    }

    public async Task PrevQuestionAsync(string pollId)
    {
        var poll = await _db.Polls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        await _voteTracker.FlushToDatabaseAsync(pollId);

        poll.ActiveQuestionIndex -= 1;
        poll.CurrentQuestionActive = false;
        poll.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        _voteTracker.InvalidateActivePollState(pollId);

        await BroadcastToGroup(pollId, "PollUpdated", new { pollId, activeQuestionIndex = poll.ActiveQuestionIndex, currentQuestionActive = false });
    }

    public async Task EndPollAsync(string pollId)
    {
        var poll = await _db.Polls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        await _voteTracker.FlushToDatabaseAsync(pollId);

        poll.Status = PollStatus.Ended;
        poll.CurrentQuestionActive = false;
        poll.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        _voteTracker.InvalidateActivePollState(pollId);

        await BroadcastToGroup(pollId, "PollEnded", new { pollId });
    }

    // ── Helpers ──

    private PollResponse MapToResponse(Poll poll)
    {
        var voteCounts = new Dictionary<string, int>();
        foreach (var vc in poll.VoteCounts)
        {
            voteCounts[$"{vc.QuestionIndex}_{vc.OptionIndex}"] = vc.Count;
        }

        var wordCloudCounts = new Dictionary<string, Dictionary<string, int>>();

        // 1. Group DB persisted counts
        foreach (var wcc in poll.WordCloudCounts)
        {
            var qKey = wcc.QuestionIndex.ToString();
            if (!wordCloudCounts.ContainsKey(qKey))
            {
                wordCloudCounts[qKey] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
            wordCloudCounts[qKey][wcc.Word] = wcc.Count;
        }

        // 2. Overwrite/merge with live memory counts if present in cache (source of truth)
        foreach (var q in poll.Questions.Where(q => q.Type == QuestionType.WordCloud))
        {
            var qKey = q.Index.ToString();
            var liveCounts = _wordCloudManager.GetTop50(poll.Id, q.Index);
            if (liveCounts.Count > 0)
            {
                wordCloudCounts[qKey] = liveCounts;
            }
        }

        return new PollResponse
        {
            Id = poll.Id,
            Title = poll.Title,
            CreatedBy = poll.CreatedBy,
            CreatedByEmail = poll.CreatedByEmail,
            CreatedByName = poll.CreatedByName,
            Status = poll.Status.ToString(),
            ActiveQuestionIndex = poll.ActiveQuestionIndex,
            CurrentQuestionActive = poll.CurrentQuestionActive,
            Theme = poll.Theme,
            Questions = poll.Questions.Select(q => new QuestionResponse
            {
                Index = q.Index,
                Text = q.Text,
                Type = q.Type.ToString(),
                Options = q.Options.Select(o => new OptionResponse
                {
                    Index = o.Index,
                    Text = o.Text
                }).ToList()
            }).ToList(),
            VoteCounts = voteCounts,
            WordCloudCounts = wordCloudCounts,
            CreatedAt = poll.CreatedAt,
            UpdatedAt = poll.UpdatedAt
        };
    }

    private async Task<string> GenerateUniqueIdAsync()
    {
        string id;
        do
        {
            id = PollIdGenerator.Generate();
        }
        while (await _db.Polls.AnyAsync(p => p.Id == id));

        return id;
    }

    public async Task<byte[]> ExportPollDataAsync(string pollId)
    {
        // 1. Flush pending in-memory votes to the database
        await _voteTracker.FlushToDatabaseAsync(pollId);

        // 2. Fetch the poll with questions, options, and votes
        var poll = await _db.Polls
            .Include(p => p.Questions.OrderBy(q => q.Index))
                .ThenInclude(q => q.Options.OrderBy(o => o.Index))
            .Include(p => p.Votes)
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        using (var memoryStream = new System.IO.MemoryStream())
        using (var writer = new System.IO.StreamWriter(memoryStream, System.Text.Encoding.UTF8))
        {
            // Write CSV Header
            await writer.WriteLineAsync("Poll ID,Poll Title,Theme,Question Index,Question Text,Question Type,Option Index,Option Text,Voter Session ID,Submitted Text,Voted At");

            foreach (var question in poll.Questions)
            {
                var questionVotes = poll.Votes
                    .Where(v => v.QuestionIndex == question.Index)
                    .OrderBy(v => v.VotedAt)
                    .ToList();

                if (questionVotes.Count == 0)
                {
                    // If no votes, write a row containing only the question details
                    var row = $"{EscapeCsvField(poll.Id)}," +
                              $"{EscapeCsvField(poll.Title)}," +
                              $"{EscapeCsvField(poll.Theme)}," +
                              $"{question.Index + 1}," +
                              $"{EscapeCsvField(question.Text)}," +
                              $"{EscapeCsvField(question.Type.ToString())}," +
                              ",,,,,"; // Option Index, Option Text, Voter Session ID, Submitted Text, Voted At are empty
                    await writer.WriteLineAsync(row);
                }
                else
                {
                    foreach (var vote in questionVotes)
                    {
                        string optionIndexStr = vote.OptionIndex.HasValue ? (vote.OptionIndex.Value + 1).ToString() : "";
                        string optionText = "";
                        if (vote.OptionIndex.HasValue)
                        {
                            var option = question.Options.FirstOrDefault(o => o.Index == vote.OptionIndex.Value);
                            if (option != null)
                            {
                                optionText = option.Text;
                            }
                        }

                        var row = $"{EscapeCsvField(poll.Id)}," +
                                  $"{EscapeCsvField(poll.Title)}," +
                                  $"{EscapeCsvField(poll.Theme)}," +
                                  $"{question.Index + 1}," +
                                  $"{EscapeCsvField(question.Text)}," +
                                  $"{EscapeCsvField(question.Type.ToString())}," +
                                  $"{EscapeCsvField(optionIndexStr)}," +
                                  $"{EscapeCsvField(optionText)}," +
                                  $"{EscapeCsvField(vote.SessionId)}," +
                                  $"{EscapeCsvField(vote.SubmittedText ?? "")}," +
                                  $"{EscapeCsvField(vote.VotedAt.ToString("o"))}";
                        await writer.WriteLineAsync(row);
                    }
                }
            }

            await writer.FlushAsync();
            return memoryStream.ToArray();
        }
    }

    private string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;
        if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }

    private async Task BroadcastPollUpdated(PollResponse response)
    {
        await BroadcastToGroup(response.Id, "PollUpdated", response);
    }

    private async Task BroadcastToGroup(string pollId, string eventName, object payload)
    {
        await _hubContext.Clients.Group($"poll_{pollId}").SendAsync(eventName, payload);
    }
}
