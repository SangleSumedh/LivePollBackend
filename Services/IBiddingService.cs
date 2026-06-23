using System.Collections.Generic;
using System.Threading.Tasks;
using live_poll_backend.Models.Entities;
using live_poll_backend.Models.DTOs;

namespace live_poll_backend.Services;

public interface IBiddingService
{
    // BiddingPoll CRUD
    Task<List<BiddingPollResponse>> GetBiddingPollsAsync(string userId);
    Task<BiddingPollResponse> GetBiddingPollByIdAsync(string pollId);
    Task<BiddingPollResponse> CreateBiddingPollAsync(CreateBiddingPollRequest request, string userId, string userEmail, string userName);
    Task<BiddingPollResponse> UpdateBiddingPollAsync(string pollId, UpdateBiddingPollRequest request);
    Task DeleteBiddingPollAsync(string pollId);
    Task RestartBiddingPollAsync(string pollId);

    Task StartQuestionAsync(string pollId, int questionIndex, string cohort);
    Task StopBiddingAsync(string pollId);
    Task PlaceBidAsync(string pollId, PlaceBidRequest request);
    Task<BiddingPollResponse> CloneBiddingPollAsync(string pollId, string userId, string userEmail, string userName);
    Task<BiddingAnalyticsSummary> GetAnalyticsAsync(string pollId);
}
