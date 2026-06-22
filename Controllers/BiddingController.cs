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
    }

    [AllowAnonymous]
    [HttpGet("skills")]
    public async Task<IActionResult> GetSkills()
    {
        var skills = await _biddingService.GetSkillsAsync();
        return Ok(skills);
    }

    [Authorize]
    [HttpPost("skills")]
    public async Task<IActionResult> AddSkill([FromBody] AddSkillRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Skill name is required" });
        if (string.IsNullOrWhiteSpace(request.Category))
            return BadRequest(new { error = "Category is required" });

        var skill = await _biddingService.AddSkillAsync(request.Name, request.Category);
        return Created("", skill);
    }

    [Authorize]
    [HttpDelete("skills/{id}")]
    public async Task<IActionResult> DeleteSkill(int id)
    {
        try
        {
            await _biddingService.DeleteSkillAsync(id);
            return NoContent();
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("start/{pollId}")]
    public async Task<IActionResult> StartBidding(string pollId)
    {
        try
        {
            await _biddingService.StartBiddingAsync(pollId);
            return Ok(new { message = "Bidding started" });
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
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
    [HttpPost("lock-in/{pollId}")]
    public async Task<IActionResult> LockIn(string pollId, [FromBody] LockInRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return BadRequest(new { error = "SessionId is required" });

        try
        {
            await _biddingService.LockInBidsAsync(pollId, request.SessionId, request.SkillIds);
            return Ok(new { message = "Bids locked in successfully" });
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
