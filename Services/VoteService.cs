using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using live_poll_backend.Data;
using live_poll_backend.Exceptions;
using live_poll_backend.Hubs;
using live_poll_backend.Models.DTOs;
using live_poll_backend.Models.Entities;

namespace live_poll_backend.Services;

public class VoteService : IVoteService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<PollHub> _hubContext;

    public VoteService(AppDbContext db, IHubContext<PollHub> hubContext)
    {
        _db = db;
        _hubContext = hubContext;
    }

    public async Task<int?> CheckVoteStatusAsync(string pollId, int questionIndex, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(pollId) || questionIndex < 0 || string.IsNullOrWhiteSpace(sessionId))
            return null;

        var vote = await _db.Votes
            .Where(v => v.PollId == pollId && v.QuestionIndex == questionIndex && v.SessionId == sessionId)
            .Select(v => (int?)v.OptionIndex)
            .FirstOrDefaultAsync();

        return vote;
    }

    public async Task CastVoteAsync(string pollId, VoteRequest request)
    {
        // Validate poll exists
        var poll = await _db.Polls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        if (!poll.CurrentQuestionActive)
            throw new InvalidOperationException("Voting is not currently active on this question");

        if (request.QuestionIndex != poll.ActiveQuestionIndex)
            throw new InvalidOperationException("The specified question is not the active question");

        using var transaction = await _db.Database.BeginTransactionAsync();

        try
        {
            // Check for duplicate vote (atomic: unique index enforces this too)
            var existingVote = await _db.Votes
                .AnyAsync(v => v.PollId == pollId && v.QuestionIndex == request.QuestionIndex && v.SessionId == request.SessionId);

            if (existingVote)
                throw new DuplicateVoteException();

            // Insert vote
            _db.Votes.Add(new Vote
            {
                PollId = pollId,
                QuestionIndex = request.QuestionIndex,
                OptionIndex = request.OptionIndex,
                SessionId = request.SessionId,
                VotedAt = DateTime.UtcNow
            });

            // Upsert vote count
            var voteCount = await _db.VoteCounts
                .FirstOrDefaultAsync(vc =>
                    vc.PollId == pollId &&
                    vc.QuestionIndex == request.QuestionIndex &&
                    vc.OptionIndex == request.OptionIndex);

            if (voteCount != null)
            {
                voteCount.Count++;
            }
            else
            {
                _db.VoteCounts.Add(new VoteCount
                {
                    PollId = pollId,
                    QuestionIndex = request.QuestionIndex,
                    OptionIndex = request.OptionIndex,
                    Count = 1
                });
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            // Broadcast updated vote counts to all poll listeners
            var voteCounts = await _db.VoteCounts
                .Where(vc => vc.PollId == pollId)
                .ToDictionaryAsync(vc => $"{vc.QuestionIndex}_{vc.OptionIndex}", vc => vc.Count);

            await _hubContext.Clients.Group($"poll_{pollId}")
                .SendAsync("VoteCountsUpdated", new { pollId, voteCounts });
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
