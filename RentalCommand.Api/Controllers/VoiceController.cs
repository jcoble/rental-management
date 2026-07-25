using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Voice;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/voice")]
[Produces("application/json")]
public class VoiceController : ManagementControllerBase
{
    private readonly IVoiceIntakeService _voice;

    public VoiceController(IVoiceIntakeService voice)
    {
        _voice = voice;
    }

    [HttpPost("drafts")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ScanDraftResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ScanDraftResponse>> CreateDraft(
        [FromForm] IFormFile? audio,
        [FromForm] string? transcript,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if ((audio is null || audio.Length == 0) && string.IsNullOrWhiteSpace(transcript))
            return BadRequest(new { error = "Provide an audio file or transcript." });
        if (!ValidOperationKey(idempotencyKey))
            return BadRequest(new { error = "A valid Idempotency-Key is required (maximum 200 characters)." });

        byte[] bytes = [];
        string? contentType = null;
        if (audio is not null && audio.Length > 0)
        {
            using var ms = new MemoryStream();
            await audio.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
            contentType = audio.ContentType;
        }

        try
        {
            var draft = await _voice.CreateDraftAsync(
                GetWorkspaceReadScope(), bytes, contentType, transcript, idempotencyKey!, ct);
            return CreatedAtAction(
                "Get",
                "Scan",
                new { id = draft.Id },
                ScanDraftResponse.FromEntity(draft).WithVoiceSlots());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (VoiceTranscriptionUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Answers the current question for an in-progress voice draft (the
    /// slot-filling "Tell me" conversation). Returns the updated draft with its
    /// recomputed slot state (<c>missingRequired</c> / <c>nextPrompt</c> /
    /// <c>complete</c>); the client loops until <c>complete</c> is true.
    /// </summary>
    [HttpPost("drafts/{id:int}/answer")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ScanDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScanDraftResponse>> Answer(
        int id,
        [FromForm] IFormFile? audio,
        [FromForm] string? transcript,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if ((audio is null || audio.Length == 0) && string.IsNullOrWhiteSpace(transcript))
            return BadRequest(new { error = "Provide an audio file or transcript." });
        if (!ValidOperationKey(idempotencyKey))
            return BadRequest(new { error = "A valid Idempotency-Key is required (maximum 200 characters)." });

        byte[] bytes = [];
        string? contentType = null;
        if (audio is not null && audio.Length > 0)
        {
            using var ms = new MemoryStream();
            await audio.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
            contentType = audio.ContentType;
        }

        try
        {
            var draft = await _voice.AnswerAsync(
                GetWorkspaceReadScope(), id, bytes, contentType, transcript, idempotencyKey!, ct);
            return Ok(ScanDraftResponse.FromEntity(draft).WithVoiceSlots());
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (VoiceTranscriptionUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    private static bool ValidOperationKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 200;
}
