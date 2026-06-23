using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using live_poll_backend.Exceptions;
using live_poll_backend.Services;
using live_poll_backend.Models.DTOs;

namespace live_poll_backend.Controllers;

[ApiController]
[Route("api/bidding")]
public class BiddingController : ControllerBase
{
    private readonly IBiddingService _biddingService;

    public BiddingController(IBiddingService biddingService)
    {
        _biddingService = biddingService;
    }

    private string GetUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";

    private string GetUserEmail() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "";

    private string GetUserName() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Anonymous";

    // ── BiddingPoll CRUD ──

    [AllowAnonymous]
    [HttpGet("polls")]
    public async Task<IActionResult> GetBiddingPolls([FromQuery] string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest(new { error = "userId is required" });

        var polls = await _biddingService.GetBiddingPollsAsync(userId);
        return Ok(polls);
    }

    [AllowAnonymous]
    [HttpGet("polls/{pollId}")]
    public async Task<IActionResult> GetBiddingPollById(string pollId)
    {
        try
        {
            var poll = await _biddingService.GetBiddingPollByIdAsync(pollId);
            return Ok(poll);
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("polls")]
    public async Task<IActionResult> CreateBiddingPoll([FromBody] CreateBiddingPollRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { error = "Title is required" });

        var poll = await _biddingService.CreateBiddingPollAsync(request, GetUserId(), GetUserEmail(), GetUserName());
        return CreatedAtAction(nameof(GetBiddingPollById), new { pollId = poll.Id }, poll);
    }

    [Authorize]
    [HttpPut("polls/{pollId}")]
    public async Task<IActionResult> UpdateBiddingPoll(string pollId, [FromBody] UpdateBiddingPollRequest request)
    {
        try
        {
            var poll = await _biddingService.UpdateBiddingPollAsync(pollId, request);
            return Ok(poll);
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpDelete("polls/{pollId}")]
    public async Task<IActionResult> DeleteBiddingPoll(string pollId)
    {
        try
        {
            await _biddingService.DeleteBiddingPollAsync(pollId);
            return NoContent();
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("polls/{pollId}/restart")]
    public async Task<IActionResult> RestartBiddingPoll(string pollId)
    {
        try
        {
            await _biddingService.RestartBiddingPollAsync(pollId);
            return Ok(new { message = "Bidding poll has been reset" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("polls/{pollId}/clone")]
    public async Task<IActionResult> CloneBiddingPoll(string pollId)
    {
        try
        {
            var poll = await _biddingService.CloneBiddingPollAsync(pollId, GetUserId(), GetUserEmail(), GetUserName());
            return Ok(poll);
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("start/{pollId}")]
    public async Task<IActionResult> StartQuestion(string pollId, [FromBody] StartQuestionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Cohort))
            return BadRequest(new { error = "Cohort is required" });

        try
        {
            await _biddingService.StartQuestionAsync(pollId, request.QuestionIndex, request.Cohort);
            return Ok(new { message = "Question bidding started" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("stop/{pollId}")]
    public async Task<IActionResult> StopBidding(string pollId)
    {
        try
        {
            await _biddingService.StopBiddingAsync(pollId);
            return Ok(new { message = "Bidding stopped" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [AllowAnonymous]
    [HttpPost("bid/{pollId}")]
    public async Task<IActionResult> PlaceBid(string pollId, [FromBody] PlaceBidRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return BadRequest(new { error = "SessionId is required" });
        if (string.IsNullOrWhiteSpace(request.Cohort))
            return BadRequest(new { error = "Cohort is required" });

        try
        {
            await _biddingService.PlaceBidAsync(pollId, request);
            return Ok(new { message = "Bid placed successfully" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [AllowAnonymous]
    [HttpGet("analytics/{pollId}")]
    public async Task<IActionResult> GetAnalytics(string pollId)
    {
        try
        {
            var summary = await _biddingService.GetAnalyticsAsync(pollId);
            return Ok(summary);
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
