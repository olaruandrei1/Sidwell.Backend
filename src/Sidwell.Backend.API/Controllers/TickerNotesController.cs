using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Route("tickers/{symbol}/notes")]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class TickerNotesController(
    ITickerNotesService notesService,
    IJournalReportService reportService,
    ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TickerNoteDto>>> GetNotes(string symbol, CancellationToken ct)
    {
        return Ok(await notesService.GetBySymbolAsync(ResolveUserId(), symbol, ct));
    }

    [HttpPost]
    public async Task<ActionResult<TickerNoteDto>> CreateNote(string symbol, [FromBody] UpsertTickerNoteRequest request, CancellationToken ct)
    {
        TickerNoteDto note = await notesService.CreateAsync(ResolveUserId(), symbol, request, ct);
        return StatusCode(StatusCodes.Status201Created, note);
    }

    [HttpPut("{noteId:guid}")]
    public async Task<ActionResult<TickerNoteDto>> UpdateNote(string symbol, Guid noteId, [FromBody] UpsertTickerNoteRequest request, CancellationToken ct)
    {
        return Ok(await notesService.UpdateAsync(ResolveUserId(), noteId, request, ct));
    }

    [HttpDelete("{noteId:guid}")]
    public async Task<IActionResult> DeleteNote(string symbol, Guid noteId, CancellationToken ct)
    {
        await notesService.DeleteAsync(ResolveUserId(), noteId, ct);
        return NoContent();
    }

    [HttpPost("{noteId:guid}/report")]
    public async Task<IActionResult> GenerateReport(
        string symbol,
        Guid noteId,
        [FromBody] GenerateReportRequest request,
        CancellationToken ct)
    {
        if (!Enum.TryParse(request.Format, ignoreCase: true, out ReportFormat format))
            return BadRequest($"Unknown report format '{request.Format}'.");

        JournalReportFile file = await reportService.GenerateAsync(
            ResolveUserId(), symbol, noteId, format, request.IncludeAttachments, ct);

        return File(file.Content, file.ContentType, file.FileName);
    }

    private Guid ResolveUserId() => Guid.Parse(OwnershipGuard.RequireUserId(currentUser));
}

public sealed record GenerateReportRequest(string Format, bool IncludeAttachments);
