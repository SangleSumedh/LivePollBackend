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

    public PollService(AppDbContext db, IHubContext<PollHub> hubContext)
    {
        _db = db;
        _hubContext = hubContext;
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
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

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
            var question = new Question
            {
                PollId = pollId,
                Index = qIdx,
                Text = qDto.Text.Trim()
            };

            int oIdx = 0;
            foreach (var optText in qDto.Options.Where(o => !string.IsNullOrWhiteSpace(o)))
            {
                question.Options.Add(new Option
                {
                    Index = oIdx,
                    Text = optText.Trim()
                });
                oIdx++;
            }

            poll.Questions.Add(question);

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
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        // Remove existing questions/options (cascade deletes options)
        _db.Questions.RemoveRange(poll.Questions);

        // Remove existing vote counts
        _db.VoteCounts.RemoveRange(poll.VoteCounts);

        poll.Title = request.Title.Trim();
        poll.Theme = string.IsNullOrWhiteSpace(request.Theme) ? "default" : request.Theme.Trim();
        poll.UpdatedAt = DateTime.UtcNow;

        int qIdx = 0;
        foreach (var qDto in request.Questions)
        {
            var question = new Question
            {
                PollId = pollId,
                Index = qIdx,
                Text = qDto.Text.Trim()
            };

            int oIdx = 0;
            foreach (var optText in qDto.Options.Where(o => !string.IsNullOrWhiteSpace(o)))
            {
                question.Options.Add(new Option
                {
                    Index = oIdx,
                    Text = optText.Trim()
                });
                oIdx++;
            }

            poll.Questions.Add(question);

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
            .Include(p => p.Votes)
            .Include(p => p.VoteCounts)
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        // Remove all votes
        _db.Votes.RemoveRange(poll.Votes);

        // Reset vote counts to zero
        foreach (var vc in poll.VoteCounts)
        {
            vc.Count = 0;
        }

        // Reset poll state
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

        await BroadcastToGroup(pollId, "PollUpdated", new { pollId, status = "live", activeQuestionIndex = poll.ActiveQuestionIndex, currentQuestionActive = true });
    }

    public async Task StopVotingAsync(string pollId)
    {
        var poll = await _db.Polls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        poll.CurrentQuestionActive = false;
        poll.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await BroadcastToGroup(pollId, "PollUpdated", new { pollId, currentQuestionActive = false });
    }

    public async Task NextQuestionAsync(string pollId)
    {
        var poll = await _db.Polls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        poll.ActiveQuestionIndex += 1;
        poll.CurrentQuestionActive = false;
        poll.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await BroadcastToGroup(pollId, "PollUpdated", new { pollId, activeQuestionIndex = poll.ActiveQuestionIndex, currentQuestionActive = false });
    }

    public async Task PrevQuestionAsync(string pollId)
    {
        var poll = await _db.Polls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        poll.ActiveQuestionIndex -= 1;
        poll.CurrentQuestionActive = false;
        poll.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await BroadcastToGroup(pollId, "PollUpdated", new { pollId, activeQuestionIndex = poll.ActiveQuestionIndex, currentQuestionActive = false });
    }

    public async Task EndPollAsync(string pollId)
    {
        var poll = await _db.Polls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        poll.Status = PollStatus.Ended;
        poll.CurrentQuestionActive = false;
        poll.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await BroadcastToGroup(pollId, "PollEnded", new { pollId });
    }

    // ── Helpers ──

    private static PollResponse MapToResponse(Poll poll)
    {
        var voteCounts = new Dictionary<string, int>();
        foreach (var vc in poll.VoteCounts)
        {
            voteCounts[$"{vc.QuestionIndex}_{vc.OptionIndex}"] = vc.Count;
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
                Options = q.Options.Select(o => new OptionResponse
                {
                    Index = o.Index,
                    Text = o.Text
                }).ToList()
            }).ToList(),
            VoteCounts = voteCounts,
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

    private async Task BroadcastPollUpdated(PollResponse response)
    {
        await BroadcastToGroup(response.Id, "PollUpdated", response);
    }

    private async Task BroadcastToGroup(string pollId, string eventName, object payload)
    {
        await _hubContext.Clients.Group($"poll_{pollId}").SendAsync(eventName, payload);
    }
}
