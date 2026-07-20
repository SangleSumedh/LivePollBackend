using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using live_poll_backend.Data;
using live_poll_backend.Exceptions;
using live_poll_backend.Hubs;
using live_poll_backend.Models.DTOs;
using live_poll_backend.Models.Entities;
using live_poll_backend.Models.Enums;

namespace live_poll_backend.Services;

public class VoteService : IVoteService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<PollHub> _hubContext;
    private readonly WordCloudManager _wordCloudManager;
    private readonly IVoteStateTracker _voteTracker;

    public VoteService(AppDbContext db, IHubContext<PollHub> hubContext, WordCloudManager wordCloudManager, IVoteStateTracker voteTracker)
    {
        _db = db;
        _hubContext = hubContext;
        _wordCloudManager = wordCloudManager;
        _voteTracker = voteTracker;
    }

    public async Task<int?> CheckVoteStatusAsync(string pollId, int questionIndex, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(pollId) || questionIndex < 0 || string.IsNullOrWhiteSpace(sessionId))
            return null;

        return await _voteTracker.CheckVoteStatusAsync(pollId, questionIndex, sessionId);
    }

    public async Task CastVoteAsync(string pollId, VoteRequest request)
    {
        // 1. Get cached poll/question state
        var state = await _voteTracker.GetActivePollStateAsync(pollId);
        if (state == null)
            throw new NotFoundException($"Poll '{pollId}' not found");

        if (!state.IsActive)
            throw new InvalidOperationException("Voting is not currently active on this question");

        if (request.QuestionIndex != state.ActiveQuestionIndex)
            throw new InvalidOperationException("The specified question is not the active question");

        // 2. DB / Tracker duplicate vote check (prevent double voting)
        var existingVote = await _voteTracker.CheckVoteStatusAsync(pollId, request.QuestionIndex, request.SessionId);
        if (existingVote.HasValue)
            throw new DuplicateVoteException();

        if (state.QuestionType == QuestionType.WordCloud)
        {
            // Validate WordCloud submission
            if (string.IsNullOrWhiteSpace(request.Text))
                throw new InvalidOperationException("Text input is required for word cloud questions");

            if (request.Text.Length > 50)
                throw new InvalidOperationException("Submitted text exceeds maximum length of 50 characters");

            // Record vote in tracker (will flush to DB)
            _voteTracker.RecordVote(pollId, request);

            // Pass to the in-memory manager for sanitization and processing
            _wordCloudManager.RecordSubmission(pollId, request.QuestionIndex, request.Text);
        }
        else
        {
            if (!request.OptionIndex.HasValue)
                throw new InvalidOperationException("OptionIndex is required for multiple choice questions");

            // Record vote in tracker (will flush to DB and trigger debounced broadcast)
            _voteTracker.RecordVote(pollId, request);
        }
    }
}
