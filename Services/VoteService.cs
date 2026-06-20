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
    private readonly WordCloudManager _wordCloudManager;

    public VoteService(AppDbContext db, IHubContext<PollHub> hubContext, WordCloudManager wordCloudManager)
    {
        _db = db;
        _hubContext = hubContext;
        _wordCloudManager = wordCloudManager;
    }

    public async Task<int?> CheckVoteStatusAsync(string pollId, int questionIndex, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(pollId) || questionIndex < 0 || string.IsNullOrWhiteSpace(sessionId))
            return null;

        var vote = await _db.Votes
            .Where(v => v.PollId == pollId && v.QuestionIndex == questionIndex && v.SessionId == sessionId)
            .Select(v => v.OptionIndex)
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

        // Load the question to check its type
        var question = await _db.Questions
            .FirstOrDefaultAsync(q => q.PollId == pollId && q.Index == request.QuestionIndex);
        if (question == null)
            throw new NotFoundException($"Question index {request.QuestionIndex} not found in poll '{pollId}'");

        using var transaction = await _db.Database.BeginTransactionAsync();

        try
        {
            // DB uniqueness check (source of truth to prevent double voting)
            var existingVote = await _db.Votes
                .AnyAsync(v => v.PollId == pollId && v.QuestionIndex == request.QuestionIndex && v.SessionId == request.SessionId);

            if (existingVote)
                throw new DuplicateVoteException();

            if (question.Type == QuestionType.WordCloud)
            {
                // Validate WordCloud submission
                if (string.IsNullOrWhiteSpace(request.Text))
                    throw new InvalidOperationException("Text input is required for word cloud questions");

                if (request.Text.Length > 200)
                    throw new InvalidOperationException("Submitted text exceeds maximum length of 200 characters");

                // Insert vote
                _db.Votes.Add(new Vote
                {
                    PollId = pollId,
                    QuestionIndex = request.QuestionIndex,
                    OptionIndex = null,
                    SubmittedText = request.Text.Trim(),
                    SessionId = request.SessionId,
                    VotedAt = DateTime.UtcNow
                });

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();

                // Pass to the in-memory manager for sanitization and processing
                _wordCloudManager.RecordSubmission(pollId, request.QuestionIndex, request.Text);
            }
            else
            {
                if (!request.OptionIndex.HasValue)
                    throw new InvalidOperationException("OptionIndex is required for multiple choice questions");

                // Insert vote
                _db.Votes.Add(new Vote
                {
                    PollId = pollId,
                    QuestionIndex = request.QuestionIndex,
                    OptionIndex = request.OptionIndex.Value,
                    SessionId = request.SessionId,
                    VotedAt = DateTime.UtcNow
                });

                // Upsert vote count
                var voteCount = await _db.VoteCounts
                    .FirstOrDefaultAsync(vc =>
                        vc.PollId == pollId &&
                        vc.QuestionIndex == request.QuestionIndex &&
                        vc.OptionIndex == request.OptionIndex.Value);

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
                        OptionIndex = request.OptionIndex.Value,
                        Count = 1
                    });
                }

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();

                // Broadcast updated vote counts to all poll listeners (Only for MultipleChoice; WordCloud broadcasts via background Timer)
                var voteCounts = await _db.VoteCounts
                    .Where(vc => vc.PollId == pollId)
                    .ToDictionaryAsync(vc => $"{vc.QuestionIndex}_{vc.OptionIndex}", vc => vc.Count);

                await _hubContext.Clients.Group($"poll_{pollId}")
                    .SendAsync("VoteCountsUpdated", new { pollId, voteCounts });
            }
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
