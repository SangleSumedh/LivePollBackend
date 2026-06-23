using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using live_poll_backend.Data;
using live_poll_backend.Exceptions;
using live_poll_backend.Hubs;
using live_poll_backend.Models.Entities;
using live_poll_backend.Models.DTOs;

namespace live_poll_backend.Services;

public class BiddingService : IBiddingService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<PollHub> _hubContext;
    private readonly BiddingStateTracker _stateTracker;

    public BiddingService(AppDbContext db, IHubContext<PollHub> hubContext, BiddingStateTracker stateTracker)
    {
        _db = db;
        _hubContext = hubContext;
        _stateTracker = stateTracker;
    }

    private static BiddingPollResponse MapToResponse(BiddingPoll bp)
    {
        return new BiddingPollResponse
        {
            Id = bp.Id,
            Title = bp.Title,
            CreatedBy = bp.CreatedBy,
            CreatedByEmail = bp.CreatedByEmail,
            CreatedByName = bp.CreatedByName,
            IsBiddingActive = bp.IsBiddingActive,
            BiddingClosed = bp.BiddingClosed,
            Theme = bp.Theme,
            ActiveQuestionIndex = bp.ActiveQuestionIndex,
            CurrentCohort = bp.CurrentCohort,
            CreatedAt = bp.CreatedAt,
            UpdatedAt = bp.UpdatedAt,
            Questions = bp.Questions.OrderBy(q => q.Index).Select(q => new BiddingQuestionResponse
            {
                Id = q.Id,
                BiddingPollId = q.BiddingPollId,
                Text = q.Text,
                Index = q.Index,
                Skills = q.Skills.OrderBy(s => s.Index).Select(s => new BiddingSkillResponse
                {
                    Id = s.Id,
                    BiddingQuestionId = s.BiddingQuestionId,
                    Name = s.Name,
                    Category = s.Category,
                    Index = s.Index
                }).ToList()
            }).ToList()
        };
    }

    public async Task<List<BiddingPollResponse>> GetBiddingPollsAsync(string userId)
    {
        var polls = await _db.BiddingPolls
            .Include(bp => bp.Questions)
                .ThenInclude(q => q.Skills)
            .Where(bp => bp.CreatedBy == userId)
            .OrderByDescending(bp => bp.CreatedAt)
            .ToListAsync();

        return polls.Select(MapToResponse).ToList();
    }

    public async Task<BiddingPollResponse> GetBiddingPollByIdAsync(string pollId)
    {
        var bp = await _db.BiddingPolls
            .Include(bp => bp.Questions)
                .ThenInclude(q => q.Skills)
            .FirstOrDefaultAsync(x => x.Id == pollId);

        if (bp == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        return MapToResponse(bp);
    }

    public async Task<BiddingPollResponse> CreateBiddingPollAsync(CreateBiddingPollRequest request, string userId, string userEmail, string userName)
    {
        var id = PollIdGenerator.Generate();
        while (await _db.BiddingPolls.AnyAsync(x => x.Id == id))
        {
            id = PollIdGenerator.Generate();
        }

        var bp = new BiddingPoll
        {
            Id = id,
            Title = request.Title.Trim(),
            Theme = string.IsNullOrWhiteSpace(request.Theme) ? "default" : request.Theme.Trim(),
            CreatedBy = userId,
            CreatedByEmail = userEmail,
            CreatedByName = string.IsNullOrWhiteSpace(userName) ? "Anonymous" : userName,
            IsBiddingActive = false,
            BiddingClosed = false,
            ActiveQuestionIndex = -1,
            CurrentCohort = string.Empty,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        if (request.Questions != null)
        {
            foreach (var qReq in request.Questions)
            {
                var question = new BiddingQuestion
                {
                    Text = qReq.Text.Trim(),
                    Index = qReq.Index,
                    CreatedAt = DateTime.UtcNow
                };

                if (qReq.Skills != null)
                {
                    foreach (var sReq in qReq.Skills)
                    {
                        question.Skills.Add(new BiddingSkill
                        {
                            Name = sReq.Name.Trim(),
                            Category = sReq.Category.Trim(),
                            Index = sReq.Index,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }

                bp.Questions.Add(question);
            }
        }

        _db.BiddingPolls.Add(bp);
        await _db.SaveChangesAsync();

        var res = MapToResponse(bp);
        await _hubContext.Clients.Group($"poll_{bp.Id}").SendAsync("PollUpdated", res);
        return res;
    }

    public async Task<BiddingPollResponse> UpdateBiddingPollAsync(string pollId, UpdateBiddingPollRequest request)
    {
        var bp = await _db.BiddingPolls
            .Include(x => x.Questions)
                .ThenInclude(q => q.Skills)
            .FirstOrDefaultAsync(x => x.Id == pollId);

        if (bp == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        bp.Title = request.Title.Trim();
        bp.Theme = string.IsNullOrWhiteSpace(request.Theme) ? "default" : request.Theme.Trim();
        bp.UpdatedAt = DateTime.UtcNow;

        // Diff and Sync Questions & Skills
        var incomingQuestions = request.Questions ?? new List<UpdateBiddingQuestionRequest>();
        
        // 1. Remove questions not in request
        var incomingQuestionIds = incomingQuestions.Where(q => q.Id.HasValue).Select(q => q.Id!.Value).ToHashSet();
        var questionsToRemove = bp.Questions.Where(q => !incomingQuestionIds.Contains(q.Id)).ToList();
        foreach (var q in questionsToRemove)
        {
            bp.Questions.Remove(q);
            _db.BiddingQuestions.Remove(q);
        }

        // 2. Update existing & Add new questions
        foreach (var qReq in incomingQuestions)
        {
            BiddingQuestion question;
            if (qReq.Id.HasValue)
            {
                question = bp.Questions.First(q => q.Id == qReq.Id.Value);
                question.Text = qReq.Text.Trim();
                question.Index = qReq.Index;
            }
            else
            {
                question = new BiddingQuestion
                {
                    Text = qReq.Text.Trim(),
                    Index = qReq.Index,
                    CreatedAt = DateTime.UtcNow
                };
                bp.Questions.Add(question);
            }

            // Sync skills for this question
            var incomingSkills = qReq.Skills ?? new List<UpdateBiddingSkillRequest>();
            var incomingSkillIds = incomingSkills.Where(s => s.Id.HasValue).Select(s => s.Id!.Value).ToHashSet();
            var skillsToRemove = question.Skills.Where(s => !incomingSkillIds.Contains(s.Id)).ToList();
            foreach (var s in skillsToRemove)
            {
                question.Skills.Remove(s);
                _db.BiddingSkills.Remove(s);
            }

            foreach (var sReq in incomingSkills)
            {
                if (sReq.Id.HasValue)
                {
                    var skill = question.Skills.First(s => s.Id == sReq.Id.Value);
                    skill.Name = sReq.Name.Trim();
                    skill.Category = sReq.Category.Trim();
                    skill.Index = sReq.Index;
                }
                else
                {
                    question.Skills.Add(new BiddingSkill
                    {
                        Name = sReq.Name.Trim(),
                        Category = sReq.Category.Trim(),
                        Index = sReq.Index,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
        }

        await _db.SaveChangesAsync();

        var res = MapToResponse(bp);
        await _hubContext.Clients.Group($"poll_{bp.Id}").SendAsync("PollUpdated", res);
        return res;
    }

    public async Task DeleteBiddingPollAsync(string pollId)
    {
        var bp = await _db.BiddingPolls.FindAsync(pollId);
        if (bp == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        _db.BiddingPolls.Remove(bp);
        await _db.SaveChangesAsync();
    }

    public async Task RestartBiddingPollAsync(string pollId)
    {
        var bp = await _db.BiddingPolls.FindAsync(pollId);
        if (bp == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        // Rule: block restart if committed bids exist
        var committedExists = await _db.SkillBids.AnyAsync(b => b.BiddingPollId == pollId && b.IsCommitted);
        if (committedExists)
            throw new InvalidOperationException("Cannot restart poll: Committed bids already exist. Please clone the poll instead.");

        _stateTracker.ClearPoll(pollId);

        var bids = await _db.SkillBids.Where(b => b.BiddingPollId == pollId).ToListAsync();
        _db.SkillBids.RemoveRange(bids);

        bp.IsBiddingActive = false;
        bp.BiddingClosed = false;
        bp.ActiveQuestionIndex = -1;
        bp.CurrentCohort = string.Empty;
        bp.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await _hubContext.Clients.Group($"poll_{pollId}").SendAsync("PollUpdated", new { pollId, message = "Bidding poll has been reset" });
    }

    public async Task StartQuestionAsync(string pollId, int questionIndex, string cohort)
    {
        var poll = await _db.BiddingPolls
            .Include(p => p.Questions)
            .FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        // Validate cohort
        cohort = cohort.Trim().ToUpper();
        if (cohort != "HR" && cohort != "ACADEMIA")
            throw new ArgumentException("Cohort must be either HR or ACADEMIA.");

        // Rule: If BiddingClosed is true for this cohort, we block starting any question for it
        var hasCommittedForCohort = await _db.SkillBids.AnyAsync(b => b.BiddingPollId == pollId && b.Cohort == cohort && b.IsCommitted);
        if (poll.BiddingClosed && hasCommittedForCohort)
            throw new InvalidOperationException($"Bidding has already been closed for cohort '{cohort}' and cannot be resumed.");

        // Flush any active ephemeral bids for the current active question before switching
        if (poll.ActiveQuestionIndex >= 0)
        {
            await _stateTracker.FlushForQuestionAsync(pollId, poll.ActiveQuestionIndex);
        }

        poll.ActiveQuestionIndex = questionIndex;
        poll.CurrentCohort = cohort;
        poll.IsBiddingActive = true;
        poll.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // Broadcast QuestionActivated
        var currentQuestion = poll.Questions.FirstOrDefault(q => q.Index == questionIndex);
        await _hubContext.Clients.Group($"poll_{pollId}").SendAsync("QuestionActivated", new
        {
            pollId,
            questionIndex,
            cohort,
            questionText = currentQuestion?.Text ?? ""
        });
    }

    public async Task StopBiddingAsync(string pollId)
    {
        var poll = await _db.BiddingPolls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        poll.IsBiddingActive = false;
        poll.BiddingClosed = true;
        poll.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Flush all questions for this poll to DB and mark them committed
        await _stateTracker.FlushToDatabaseAsync(pollId);

        // Mark all bids for the current cohort as committed
        var uncommittedBids = await _db.SkillBids
            .Where(b => b.BiddingPollId == pollId && b.Cohort == poll.CurrentCohort && !b.IsCommitted)
            .ToListAsync();
        foreach (var bid in uncommittedBids)
        {
            bid.IsCommitted = true;
        }
        await _db.SaveChangesAsync();

        await _hubContext.Clients.Group($"poll_{pollId}").SendAsync("BiddingClosed", new { pollId, cohort = poll.CurrentCohort });

        // Update participant count
        var totalParticipantsCommitted = await _db.SkillBids
            .Where(b => b.BiddingPollId == pollId && b.Cohort == poll.CurrentCohort && b.IsCommitted)
            .Select(b => b.SessionId)
            .Distinct()
            .CountAsync();
  
        await _hubContext.Clients.Group($"poll_{pollId}_presenter").SendAsync("ParticipantSubmittedCountUpdate", new
        {
            pollId,
            committedCount = totalParticipantsCommitted
        });
    }

    public async Task PlaceBidAsync(string pollId, PlaceBidRequest request)
    {
        var poll = await _db.BiddingPolls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        // Gate 1: Bidding is active and current cohort matches request
        if (!poll.IsBiddingActive || string.IsNullOrEmpty(poll.CurrentCohort) || poll.CurrentCohort != request.Cohort)
            throw new InvalidOperationException("Bidding is not active for this cohort.");

        // Gate 2: Bidding is open for the specified question only
        if (poll.ActiveQuestionIndex != request.QuestionIndex)
            throw new InvalidOperationException("Bidding is only allowed for the active question.");

        var skill = await _db.BiddingSkills
            .Include(s => s.BiddingQuestion)
            .FirstOrDefaultAsync(s => s.Id == request.BiddingSkillId);
        if (skill == null || skill.BiddingQuestion == null || skill.BiddingQuestion.BiddingPollId != pollId || skill.BiddingQuestion.Index != request.QuestionIndex)
            throw new InvalidOperationException("Invalid skill specified for this question.");

        // Check if cohort bidding is closed (if any committed bids exist for this cohort)
        var cohortBids = await _db.SkillBids
            .Where(b => b.BiddingPollId == pollId && b.Cohort == request.Cohort)
            .ToListAsync();

        if (cohortBids.Any(b => b.IsCommitted))
            throw new InvalidOperationException("Bidding is already closed/committed for this cohort.");

        // Gate 3: Budget check: SUM(CoinsSpent across ALL questions) + newAmount <= 100
        // Get user's current bids for this question from the state tracker (before updating it to check total budget)
        var userActiveQMemoryBids = _stateTracker.GetUserBidsForQuestion(pollId, request.QuestionIndex, request.SessionId);
        
        // Sum of other skills in this active question (excluding the one being updated)
        var currentQOtherSkillsSum = userActiveQMemoryBids
            .Where(b => b.Key != request.BiddingSkillId)
            .Sum(b => b.Value);

        // Sum of bids in other questions in database
        var otherQuestionsDbSum = cohortBids
            .Where(b => b.SessionId == request.SessionId && b.QuestionIndex != request.QuestionIndex)
            .Sum(b => b.CoinsSpent);

        var totalProposedBudget = otherQuestionsDbSum + currentQOtherSkillsSum + request.CoinsSpent;
        if (totalProposedBudget > 100)
            throw new InvalidOperationException($"Insufficient coins. This bid would bring your total to {totalProposedBudget} coins, but your maximum budget is 100.");

        // Safe to update the state tracker
        _stateTracker.UpdateBid(pollId, request.QuestionIndex, request.SessionId, request.BiddingSkillId, request.CoinsSpent);
    }

    public async Task<BiddingPollResponse> CloneBiddingPollAsync(string pollId, string userId, string userEmail, string userName)
    {
        var source = await _db.BiddingPolls
            .Include(p => p.Questions)
                .ThenInclude(q => q.Skills)
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (source == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        var newId = PollIdGenerator.Generate();
        while (await _db.BiddingPolls.AnyAsync(x => x.Id == newId))
        {
            newId = PollIdGenerator.Generate();
        }

        var copy = new BiddingPoll
        {
            Id = newId,
            Title = $"{source.Title} (Clone)",
            Theme = source.Theme,
            CreatedBy = userId,
            CreatedByEmail = userEmail,
            CreatedByName = userName,
            IsBiddingActive = false,
            BiddingClosed = false,
            ActiveQuestionIndex = -1,
            CurrentCohort = string.Empty,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        foreach (var q in source.Questions.OrderBy(q => q.Index))
        {
            var qCopy = new BiddingQuestion
            {
                Text = q.Text,
                Index = q.Index,
                CreatedAt = DateTime.UtcNow
            };

            foreach (var s in q.Skills.OrderBy(s => s.Index))
            {
                qCopy.Skills.Add(new BiddingSkill
                {
                    Name = s.Name,
                    Category = s.Category,
                    Index = s.Index,
                    CreatedAt = DateTime.UtcNow
                });
            }

            copy.Questions.Add(qCopy);
        }

        _db.BiddingPolls.Add(copy);
        await _db.SaveChangesAsync();

        return MapToResponse(copy);
    }

    public async Task<BiddingAnalyticsSummary> GetAnalyticsAsync(string pollId)
    {
        var poll = await _db.BiddingPolls
            .Include(p => p.Questions)
                .ThenInclude(q => q.Skills)
            .FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        var bids = await _db.SkillBids
            .Where(b => b.BiddingPollId == pollId)
            .ToListAsync();

        var summary = new BiddingAnalyticsSummary
        {
            PollId = pollId,
            Questions = new List<QuestionAnalytics>()
        };

        foreach (var question in poll.Questions.OrderBy(q => q.Index))
        {
            var qAnalytics = new QuestionAnalytics
            {
                QuestionId = question.Id,
                QuestionText = question.Text,
                QuestionIndex = question.Index,
                Skills = new List<SkillAnalyticsRow>()
            };

            var qBids = bids.Where(b => b.QuestionIndex == question.Index).ToList();
            var hrCoins = qBids.Where(b => b.Cohort == "HR").GroupBy(b => b.BiddingSkillId).ToDictionary(g => g.Key, g => g.Sum(b => b.CoinsSpent));
            var academiaCoins = qBids.Where(b => b.Cohort == "ACADEMIA").GroupBy(b => b.BiddingSkillId).ToDictionary(g => g.Key, g => g.Sum(b => b.CoinsSpent));

            foreach (var skill in question.Skills.OrderBy(s => s.Index))
            {
                int hrSum = hrCoins.TryGetValue(skill.Id, out var hVal) ? hVal : 0;
                int acSum = academiaCoins.TryGetValue(skill.Id, out var aVal) ? aVal : 0;
                int divergence = Math.Abs(hrSum - acSum);

                qAnalytics.Skills.Add(new SkillAnalyticsRow
                {
                    SkillId = skill.Id,
                    SkillName = skill.Name,
                    Category = skill.Category,
                    HRCoins = hrSum,
                    AcademiaCoins = acSum,
                    DivergenceScore = divergence
                });
            }

            qAnalytics.Skills = qAnalytics.Skills.OrderByDescending(s => s.DivergenceScore).ToList();
            summary.Questions.Add(qAnalytics);
        }

        return summary;
    }
}
