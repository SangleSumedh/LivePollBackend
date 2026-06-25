using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using live_poll_backend.Services;
using live_poll_backend.Data;
using Microsoft.EntityFrameworkCore;

namespace live_poll_backend.Hubs;

/// <summary>
/// SignalR hub for real-time poll updates.
/// Clients connect and join a poll-specific group to receive events.
/// All state-changing actions go through REST endpoints; the hub only broadcasts.
/// </summary>
public class PollHub : Hub
{
    /// <summary>
    /// Tracks connectionId -> (pollId, sessionId) mapping so we can
    /// clean up ephemeral tracker data when a user disconnects.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (string PollId, string SessionId)> _connections = new();

    /// <summary>Clean up ephemeral bidding state when a user disconnects.</summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_connections.TryRemove(Context.ConnectionId, out var info))
        {
            var serviceProvider = Context.GetHttpContext()?.RequestServices;
            if (serviceProvider != null)
            {
                var tracker = serviceProvider.GetRequiredService<BiddingStateTracker>();
                tracker.RemoveSession(info.PollId, info.SessionId);
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Join a poll's group to receive real-time updates.</summary>
    public async Task JoinPollGroup(string pollId, string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"poll_{pollId}");

        var serviceProvider = Context.GetHttpContext()?.RequestServices;
        if (serviceProvider != null)
        {
            var tracker = serviceProvider.GetRequiredService<BiddingStateTracker>();
            var db = serviceProvider.GetRequiredService<AppDbContext>();

            // Track this connection for cleanup on disconnect
            if (!string.IsNullOrEmpty(sessionId))
            {
                _connections[Context.ConnectionId] = (pollId, sessionId);
            }

            // Only load uncommitted bids from DB if the session has NO ephemeral data in the tracker.
            // The tracker always has the most recent data (updated on every stepper change).
            // Loading stale DB data would overwrite the user's current in-memory bids.
            if (!string.IsNullOrEmpty(sessionId) && !tracker.HasSessionData(pollId, sessionId))
            {
                var dbBids = await db.SkillBids
                    .Where(b => b.BiddingPollId == pollId && b.SessionId == sessionId)
                    .ToListAsync();
                foreach (var bid in dbBids)
                {
                    tracker.UpdateBid(pollId, bid.QuestionIndex, sessionId, bid.BiddingSkillId, bid.CoinsSpent);
                }
            }

            var poll = await db.BiddingPolls.FindAsync(pollId);
            if (poll != null && poll.ActiveQuestionIndex >= 0)
            {
                var counts = tracker.GetCounts(pollId, poll.ActiveQuestionIndex);
                await Clients.Caller.SendAsync("ReceiveBubbleData", new
                {
                    pollId,
                    questionIndex = poll.ActiveQuestionIndex,
                    counts = counts.ToDictionary(k => k.Key.ToString(), v => v.Value)
                });
            }
        }
    }

    /// <summary>Leave a poll's group to stop receiving updates.</summary>
    public async Task LeavePollGroup(string pollId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"poll_{pollId}");
    }

    /// <summary>Join the presenter group to receive real-time reactions and updates.</summary>
    public async Task JoinPresenterGroup(string pollId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"poll_{pollId}_presenter");
    }

    /// <summary>Leave the presenter group.</summary>
    public async Task LeavePresenterGroup(string pollId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"poll_{pollId}_presenter");
    }

    /// <summary>Send an emoji reaction to the presenter's group only.</summary>
    public async Task SendEmojiReaction(string pollId, string emoji)
    {
        await Clients.Group($"poll_{pollId}_presenter").SendAsync("EmojiReceived", new { pollId, emoji });
    }

    /// <summary>Notify server of an ephemeral selection change (bid) from a participant.</summary>
    public void NotifyBidChange(string pollId, int questionIndex, string sessionId, int biddingSkillId, int amount)
    {
        var tracker = Context.GetHttpContext()?.RequestServices.GetRequiredService<BiddingStateTracker>();
        tracker?.UpdateBid(pollId, questionIndex, sessionId, biddingSkillId, amount);
    }
}
