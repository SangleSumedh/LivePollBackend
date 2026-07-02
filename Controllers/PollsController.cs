using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using live_poll_backend.Exceptions;
using live_poll_backend.Models.DTOs;
using live_poll_backend.Services;

namespace live_poll_backend.Controllers;

[ApiController]
[Route("api/polls")]
public class PollsController : ControllerBase
{
    private readonly IPollService _pollService;
    private readonly IVoteService _voteService;

    public PollsController(IPollService pollService, IVoteService voteService)
    {
        _pollService = pollService;
        _voteService = voteService;
    }

    private string GetUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";

    private string GetUserEmail() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "";

    private string GetUserName() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Anonymous";

    // ── Poll CRUD ──

    /// <summary>Get all polls created by a user</summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> GetPolls([FromQuery] string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest(new { error = "userId is required" });

        var polls = await _pollService.GetPollsAsync(userId);
        return Ok(polls);
    }

    /// <summary>Get a single poll by ID with full details</summary>
    [AllowAnonymous]
    [HttpGet("{pollId}")]
    public async Task<IActionResult> GetPollById(string pollId)
    {
        try
        {
            var poll = await _pollService.GetPollByIdAsync(pollId);
            return Ok(poll);
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Create a new poll (requires authentication)</summary>
    [Authorize]
    [HttpPost]
    public async Task<IActionResult> CreatePoll([FromBody] CreatePollRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { error = "Title is required" });

        if (request.Questions == null || request.Questions.Count == 0)
            return BadRequest(new { error = "At least one question is required" });

        var poll = await _pollService.CreatePollAsync(request, GetUserId(), GetUserEmail(), GetUserName());
        return CreatedAtAction(nameof(GetPollById), new { pollId = poll.Id }, poll);
    }

    /// <summary>Update an existing poll's title and questions (requires auth)</summary>
    [Authorize]
    [HttpPut("{pollId}")]
    public async Task<IActionResult> UpdatePoll(string pollId, [FromBody] UpdatePollRequest request)
    {
        try
        {
            var poll = await _pollService.UpdatePollAsync(pollId, request);
            return Ok(poll);
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Delete a poll and all associated data (requires auth)</summary>
    [Authorize]
    [HttpDelete("{pollId}")]
    public async Task<IActionResult> DeletePoll(string pollId)
    {
        try
        {
            await _pollService.DeletePollAsync(pollId);
            return NoContent();
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ── Presenter Actions (requires auth) ──

    [Authorize]
    [HttpPost("{pollId}/restart")]
    public async Task<IActionResult> RestartPoll(string pollId)
    {
        try
        {
            await _pollService.RestartPollAsync(pollId);
            return Ok(new { message = "Poll has been reset" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("{pollId}/start")]
    public async Task<IActionResult> StartVoting(string pollId, [FromBody] StartVotingRequest request)
    {
        try
        {
            await _pollService.StartVotingAsync(pollId, request.QuestionIndex);
            return Ok(new { message = "Voting started" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("{pollId}/stop")]
    public async Task<IActionResult> StopVoting(string pollId)
    {
        try
        {
            await _pollService.StopVotingAsync(pollId);
            return Ok(new { message = "Voting stopped" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("{pollId}/next")]
    public async Task<IActionResult> NextQuestion(string pollId)
    {
        try
        {
            await _pollService.NextQuestionAsync(pollId);
            return Ok(new { message = "Moved to next question" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("{pollId}/prev")]
    public async Task<IActionResult> PrevQuestion(string pollId)
    {
        try
        {
            await _pollService.PrevQuestionAsync(pollId);
            return Ok(new { message = "Moved to previous question" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("{pollId}/end")]
    public async Task<IActionResult> EndPoll(string pollId)
    {
        try
        {
            await _pollService.EndPollAsync(pollId);
            return Ok(new { message = "Poll ended" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ── Voting (public) ──

    [AllowAnonymous]
    [HttpGet("{pollId}/votes/status")]
    public async Task<IActionResult> CheckVoteStatus(
        string pollId,
        [FromQuery] int questionIndex,
        [FromQuery] string sessionId)
    {
        var optionIndex = await _voteService.CheckVoteStatusAsync(pollId, questionIndex, sessionId);
        return Ok(new { hasVoted = optionIndex.HasValue, optionIndex });
    }

    [AllowAnonymous]
    [HttpPost("{pollId}/votes")]
    public async Task<IActionResult> CastVote(string pollId, [FromBody] VoteRequest request)
    {
        try
        {
            await _voteService.CastVoteAsync(pollId, request);
            return Ok(new { message = "Vote recorded" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (DuplicateVoteException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [AllowAnonymous]
    [HttpGet("{pollId}/export")]
    public async Task<IActionResult> ExportPoll(string pollId)
    {
        try
        {
            var csvBytes = await _pollService.ExportPollDataAsync(pollId);
            return File(csvBytes, "text/csv", $"poll-{pollId}-export.csv");
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
