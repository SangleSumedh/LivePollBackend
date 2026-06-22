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

    public async Task<List<Skill>> GetSkillsAsync()
    {
        return await _db.Skills.OrderBy(s => s.Category).ThenBy(s => s.Name).ToListAsync();
    }

    public async Task<Skill> AddSkillAsync(string name, string category)
    {
        var skill = new Skill
        {
            Name = name.Trim(),
            Category = category.Trim()
        };
        _db.Skills.Add(skill);
        await _db.SaveChangesAsync();
        return skill;
    }

    public async Task DeleteSkillAsync(int id)
    {
        var skill = await _db.Skills.FindAsync(id);
        if (skill == null)
            throw new NotFoundException($"Skill with ID {id} not found");

        _db.Skills.Remove(skill);
        await _db.SaveChangesAsync();
    }

    public async Task<List<BiddingPollResponse>> GetBiddingPollsAsync(string userId)
    {
        return await _db.BiddingPolls
            .Where(bp => bp.CreatedBy == userId)
            .OrderByDescending(bp => bp.CreatedAt)
            .Select(bp => new BiddingPollResponse
            {
                Id = bp.Id,
                Title = bp.Title,
                CreatedBy = bp.CreatedBy,
                CreatedByEmail = bp.CreatedByEmail,
                CreatedByName = bp.CreatedByName,
                IsBiddingActive = bp.IsBiddingActive,
                BiddingClosed = bp.BiddingClosed,
                SkillCost = bp.SkillCost,
                Theme = bp.Theme,
                CreatedAt = bp.CreatedAt,
                UpdatedAt = bp.UpdatedAt,
                Skills = bp.Skills.ToList()
            })
            .ToListAsync();
    }

    public async Task<BiddingPollResponse> GetBiddingPollByIdAsync(string pollId)
    {
        var bp = await _db.BiddingPolls
            .Include(x => x.Skills)
            .FirstOrDefaultAsync(x => x.Id == pollId);

        if (bp == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        return new BiddingPollResponse
        {
            Id = bp.Id,
            Title = bp.Title,
            CreatedBy = bp.CreatedBy,
            CreatedByEmail = bp.CreatedByEmail,
            CreatedByName = bp.CreatedByName,
            IsBiddingActive = bp.IsBiddingActive,
            BiddingClosed = bp.BiddingClosed,
            SkillCost = bp.SkillCost,
            Theme = bp.Theme,
            CreatedAt = bp.CreatedAt,
            UpdatedAt = bp.UpdatedAt,
            Skills = bp.Skills.ToList()
        };
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
            SkillCost = request.SkillCost > 0 ? request.SkillCost : 20,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        if (request.SkillIds != null && request.SkillIds.Count > 0)
        {
            bp.Skills = await _db.Skills.Where(s => request.SkillIds.Contains(s.Id)).ToListAsync();
        }

        _db.BiddingPolls.Add(bp);
        await _db.SaveChangesAsync();

        var res = new BiddingPollResponse
        {
            Id = bp.Id,
            Title = bp.Title,
            CreatedBy = bp.CreatedBy,
            CreatedByEmail = bp.CreatedByEmail,
            CreatedByName = bp.CreatedByName,
            IsBiddingActive = bp.IsBiddingActive,
            BiddingClosed = bp.BiddingClosed,
            SkillCost = bp.SkillCost,
            Theme = bp.Theme,
            CreatedAt = bp.CreatedAt,
            UpdatedAt = bp.UpdatedAt,
            Skills = bp.Skills.ToList()
        };

        await _hubContext.Clients.Group($"poll_{bp.Id}").SendAsync("PollUpdated", res);
        return res;
    }

    public async Task<BiddingPollResponse> UpdateBiddingPollAsync(string pollId, UpdateBiddingPollRequest request)
    {
        var bp = await _db.BiddingPolls
            .Include(x => x.Skills)
            .FirstOrDefaultAsync(x => x.Id == pollId);

        if (bp == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        bp.Title = request.Title.Trim();
        bp.Theme = string.IsNullOrWhiteSpace(request.Theme) ? "default" : request.Theme.Trim();
        bp.SkillCost = request.SkillCost > 0 ? request.SkillCost : 20;
        bp.UpdatedAt = DateTime.UtcNow;

        if (request.SkillIds != null)
        {
            bp.Skills.Clear();
            var skills = await _db.Skills.Where(s => request.SkillIds.Contains(s.Id)).ToListAsync();
            foreach (var skill in skills)
            {
                bp.Skills.Add(skill);
            }
        }

        await _db.SaveChangesAsync();

        var res = new BiddingPollResponse
        {
            Id = bp.Id,
            Title = bp.Title,
            CreatedBy = bp.CreatedBy,
            CreatedByEmail = bp.CreatedByEmail,
            CreatedByName = bp.CreatedByName,
            IsBiddingActive = bp.IsBiddingActive,
            BiddingClosed = bp.BiddingClosed,
            SkillCost = bp.SkillCost,
            Theme = bp.Theme,
            CreatedAt = bp.CreatedAt,
            UpdatedAt = bp.UpdatedAt,
            Skills = bp.Skills.ToList()
        };

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

        _stateTracker.ClearPoll(pollId);

        var bids = await _db.SkillBids.Where(b => b.BiddingPollId == pollId).ToListAsync();
        _db.SkillBids.RemoveRange(bids);

        bp.IsBiddingActive = false;
        bp.BiddingClosed = false;
        bp.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await _hubContext.Clients.Group($"poll_{pollId}").SendAsync("PollUpdated", new { pollId, message = "Bidding poll has been reset" });
    }

    public async Task StartBiddingAsync(string pollId)
    {
        var poll = await _db.BiddingPolls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        poll.IsBiddingActive = true;
        poll.BiddingClosed = false;
        poll.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _hubContext.Clients.Group($"poll_{pollId}").SendAsync("BiddingStarted", new
        {
            pollId,
            skillCost = poll.SkillCost
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

        // Trigger immediate flush of uncommitted ephemeral selections
        await _stateTracker.FlushToDatabaseAsync(pollId);

        await _hubContext.Clients.Group($"poll_{pollId}").SendAsync("BiddingClosed", new { pollId });
    }

    public async Task LockInBidsAsync(string pollId, string sessionId, List<int> skillIds)
    {
        var poll = await _db.BiddingPolls.FindAsync(pollId);
        if (poll == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");

        if (poll.BiddingClosed || !poll.IsBiddingActive)
            throw new InvalidOperationException("Bidding is not currently active for this session.");

        // Check if user has already committed bids
        var alreadyCommitted = await _db.SkillBids
            .AnyAsync(b => b.BiddingPollId == pollId && b.SessionId == sessionId && b.IsCommitted);
        if (alreadyCommitted)
            throw new InvalidOperationException("You have already locked in your selections.");

        // Validate coin budget
        var cost = poll.SkillCost;
        var totalCost = skillIds.Count * cost;
        if (totalCost > 100)
            throw new InvalidOperationException($"Insufficient coins. Your selections cost {totalCost} coins, but you only have 100.");

        // Resolve cohort on the backend based on Theme (SynergySphere = HR, Masterclass = ACADEMIA)
        var themeLower = poll.Theme.ToLower();
        var cohort = themeLower == "synergysphere" || themeLower == "ss" ? "HR" : "ACADEMIA";

        // Remove any prior uncommitted selections for this session
        var priorUncommitted = await _db.SkillBids
            .Where(b => b.BiddingPollId == pollId && b.SessionId == sessionId && !b.IsCommitted)
            .ToListAsync();
        _db.SkillBids.RemoveRange(priorUncommitted);

        // Add committed bids
        var groupedBids = skillIds.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        foreach (var pair in groupedBids)
        {
            var skillId = pair.Key;
            var count = pair.Value;
            _db.SkillBids.Add(new SkillBid
            {
                BiddingPollId = pollId,
                SkillId = skillId,
                SessionId = sessionId,
                Cohort = cohort,
                CoinsSpent = cost * count,
                IsCommitted = true
            });
        }
 
        await _db.SaveChangesAsync();
 
        // Broadcast count update
        var totalParticipantsCommitted = await _db.SkillBids
            .Where(b => b.BiddingPollId == pollId && b.IsCommitted)
            .Select(b => b.SessionId)
            .Distinct()
            .CountAsync();
 
        await _hubContext.Clients.Group($"poll_{pollId}_presenter").SendAsync("ParticipantSubmittedCountUpdate", new
        {
            pollId,
            committedCount = totalParticipantsCommitted
        });
    }
 
    public async Task<BiddingAnalyticsSummary> GetAnalyticsAsync(string pollId)
    {
        var poll = await _db.BiddingPolls
            .Include(p => p.Skills)
            .FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll == null)
            throw new NotFoundException($"Bidding poll '{pollId}' not found");
 
        var skills = poll.Skills.Any() 
            ? poll.Skills.ToList() 
            : await _db.Skills.ToListAsync();
        
        // Fetch all bids (committed + uncommitted) for this poll
        var bids = await _db.SkillBids
            .Where(b => b.BiddingPollId == pollId)
            .ToListAsync();
 
        var cost = poll.SkillCost > 0 ? poll.SkillCost : 20;
        var hrVotes = bids.Where(b => b.Cohort == "HR").GroupBy(b => b.SkillId).ToDictionary(g => g.Key, g => g.Sum(b => b.CoinsSpent / cost));
        var academiaVotes = bids.Where(b => b.Cohort == "ACADEMIA").GroupBy(b => b.SkillId).ToDictionary(g => g.Key, g => g.Sum(b => b.CoinsSpent / cost));

        var summary = new BiddingAnalyticsSummary { PollId = pollId };

        foreach (var skill in skills)
        {
            int hrCount = hrVotes.ContainsKey(skill.Id) ? hrVotes[skill.Id] : 0;
            int acCount = academiaVotes.ContainsKey(skill.Id) ? academiaVotes[skill.Id] : 0;
            int divergence = Math.Abs(hrCount - acCount);

            summary.Rows.Add(new SkillAnalyticsRow
            {
                SkillId = skill.Id,
                SkillName = skill.Name,
                Category = skill.Category,
                HRVotes = hrCount,
                AcademiaVotes = acCount,
                DivergenceScore = divergence
            });
        }

        // Sort by divergence score descending to surface most debated/diverged skills first
        summary.Rows = summary.Rows.OrderByDescending(r => r.DivergenceScore).ToList();
        return summary;
    }
}
