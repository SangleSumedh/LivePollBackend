using Microsoft.AspNetCore.SignalR;

namespace live_poll_backend.Hubs;

/// <summary>
/// SignalR hub for real-time poll updates.
/// Clients connect and join a poll-specific group to receive events.
/// All state-changing actions go through REST endpoints; the hub only broadcasts.
/// </summary>
public class PollHub : Hub
{
    /// <summary>Join a poll's group to receive real-time updates.</summary>
    public async Task JoinPollGroup(string pollId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"poll_{pollId}");
    }

    /// <summary>Leave a poll's group to stop receiving updates.</summary>
    public async Task LeavePollGroup(string pollId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"poll_{pollId}");
    }
}
