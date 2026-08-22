using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Route("finances")]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class FinanceController(
    IFinanceService financeService,
    IExpenseExportService expenseExportService,
    ICurrentUserAccessor currentUser
) : ControllerBase
{
    [HttpGet("settings")]
    public async Task<ActionResult<FinanceSettingsDto>> GetSettings(CancellationToken ct)
    {
        return Ok(await financeService.GetSettingsAsync(ResolveUserId(), ct));
    }

    [HttpPost("expenses/export")]
    public async Task<IActionResult> ExportExpenses([FromBody] ExpenseExportRequest request, CancellationToken ct)
    {
        JournalReportFile file = await expenseExportService.ExportAsync(ResolveUserId(), request, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpPut("settings")]
    public async Task<ActionResult<FinanceSettingsDto>> UpdateSettings([FromBody] FinanceSettingsDto request, CancellationToken ct)
    {
        return Ok(await financeService.UpdateSettingsAsync(ResolveUserId(), request, ct));
    }

    [HttpGet("monthly")]
    public async Task<ActionResult<MonthlyFinancesResponse>> GetMonthly([FromQuery] string? month, CancellationToken ct)
    {
        return Ok(await financeService.GetMonthlyAsync(ResolveUserId(), month ?? string.Empty, ct));
    }

    [HttpGet("expenses/{id:guid}")]
    public async Task<IActionResult> GetExpenseById(Guid id, CancellationToken ct)
    {
        ExpenseItemDto? result = await financeService.GetExpenseByIdAsync(ResolveUserId(), id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("expenses/{id:guid}/series-range")]
    public async Task<IActionResult> GetExpenseSeriesRange(Guid id, CancellationToken ct)
    {
        ExpenseSeriesRangeDto? result = await financeService.GetExpenseSeriesRangeAsync(ResolveUserId(), id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("expenses")]
    public async Task<ActionResult<ExpenseItemDto>> AddExpense([FromBody] AddExpenseRequest request, CancellationToken ct)
    {
        AddExpenseCommand command = new(
            request.Name, request.Category, request.Amount, request.Currency, request.Type,
            request.Status, request.DueDate, request.InterestRatePct, request.Month, request.IsRecurring,
            request.LineItems,
            request.PaymentSources);

        ExpenseItemDto created = await financeService.AddExpenseAsync(ResolveUserId(), command, ct);

        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPut("expenses/{id}")]
    public async Task<ActionResult<ExpenseItemDto>> UpdateExpense(string id, [FromBody] AddExpenseRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out Guid expenseId))
            throw new NotFoundException("Expense not found.");

        AddExpenseCommand command = new(
            request.Name, request.Category, request.Amount, request.Currency, request.Type,
            request.Status, request.DueDate, request.InterestRatePct, request.Month, request.IsRecurring,
            request.LineItems,
            request.PaymentSources,
            request.RecurringEditScope);

        return Ok(await financeService.UpdateExpenseAsync(ResolveUserId(), expenseId, command, ct));
    }

    [HttpPut("expenses/{id}/status")]
    public async Task<ActionResult<ExpenseItemDto>> UpdateExpenseStatus(string id, [FromBody] UpdateExpenseStatusRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out Guid expenseId))
            throw new NotFoundException("Expense not found.");

        return Ok(await financeService.UpdateExpenseStatusAsync(ResolveUserId(), expenseId, request.Status, request.Month, ct));
    }

    [HttpDelete("expenses/{id}")]
    public async Task<IActionResult> DeleteExpense(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out Guid expenseId))
            return NoContent();

        await financeService.DeleteExpenseAsync(ResolveUserId(), expenseId, ct);

        return NoContent();
    }

    [HttpPost("wealth-allocations")]
    public async Task<ActionResult<WealthAllocationDto>> AddWealthAllocation([FromBody] AddWealthAllocationRequest request, CancellationToken ct)
    {
        AddWealthAllocationCommand command = new(
            request.Name, request.Institution, request.InstitutionType, request.Type,
            request.Amount, request.Currency, request.InterestRatePct, request.Notes, request.Month);

        WealthAllocationDto created = await financeService.AddWealthAllocationAsync(ResolveUserId(), command, ct);

        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPut("wealth-allocations/{id}")]
    public async Task<ActionResult<WealthAllocationDto>> UpdateWealthAllocation(string id, [FromBody] AddWealthAllocationRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out Guid allocationId))
            throw new NotFoundException("Wealth allocation not found.");

        AddWealthAllocationCommand command = new(
            request.Name, request.Institution, request.InstitutionType, request.Type,
            request.Amount, request.Currency, request.InterestRatePct, request.Notes, request.Month);

        return Ok(await financeService.UpdateWealthAllocationAsync(ResolveUserId(), allocationId, command, ct));
    }

    [HttpDelete("wealth-allocations/{id}")]
    public async Task<IActionResult> DeleteWealthAllocation(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out Guid allocationId))
            return NoContent();

        await financeService.DeleteWealthAllocationAsync(ResolveUserId(), allocationId, ct);

        return NoContent();
    }

    [HttpGet("wealth-allocations/snapshot-preview")]
    public async Task<ActionResult<WealthSnapshotPreviewDto>> GetWealthSnapshotPreview([FromQuery] string? month, CancellationToken ct)
    {
        return Ok(await financeService.GetWealthSnapshotPreviewAsync(ResolveUserId(), month ?? string.Empty, ct));
    }

    [HttpPost("wealth-allocations/snapshot")]
    public async Task<ActionResult<object>> SnapshotWealth([FromBody] WealthSnapshotRequest request, CancellationToken ct)
    {
        int inserted = await financeService.SnapshotWealthFromPriorMonthAsync(ResolveUserId(), request.Month ?? string.Empty, ct);
        return Ok(new { inserted });
    }

    [HttpPost("extra-incomes")]
    public async Task<ActionResult<ExtraIncomeDto>> AddExtraIncome([FromBody] AddExtraIncomeRequest request, CancellationToken ct)
    {
        AddExtraIncomeCommand command = new(request.Name, request.Amount, request.Currency, request.Month, request.Notes);
        ExtraIncomeDto created = await financeService.AddExtraIncomeAsync(ResolveUserId(), command, ct);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpDelete("extra-incomes/{id}")]
    public async Task<IActionResult> DeleteExtraIncome(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out Guid extraId))
            return NoContent();

        await financeService.DeleteExtraIncomeAsync(ResolveUserId(), extraId, ct);
        return NoContent();
    }

    [HttpPost("receipt-scan")]
    public async Task<ActionResult<ExpenseItemDto>> ScanReceipt(IFormFile? image, CancellationToken ct)
    {
        if (image is null || image.Length == 0)
            throw new ValidationException("A receipt image file is required.");

        await using Stream stream = image.OpenReadStream();

        ExpenseItemDto? proposed = await financeService.ScanReceiptAsync(ResolveUserId(), stream, image.ContentType, ct);

        if (proposed is null)
            throw new ValidationException("The receipt could not be read. Please enter the expense manually.");

        return Ok(proposed);
    }

    private Guid ResolveUserId() => Guid.Parse(OwnershipGuard.RequireUserId(currentUser));
}

public sealed record AddExpenseRequest(
    string? Name,
    string? Category,
    string? Amount,
    string? Currency,
    string? Type,
    string? Status,
    string? DueDate,
    string? InterestRatePct,
    string? Month,
    bool? IsRecurring,
    IReadOnlyList<ExpenseLineItemDto>? LineItems = null,
    IReadOnlyList<PaymentSourceEntry>? PaymentSources = null,
    string? RecurringEditScope = null);

public sealed record UpdateExpenseStatusRequest(string Status, string? Month = null);

public sealed record WealthSnapshotRequest(string? Month);

public sealed record AddWealthAllocationRequest(
    string? Name,
    string? Institution,
    string? InstitutionType,
    string? Type,
    string? Amount,
    string? Currency,
    string? InterestRatePct,
    string? Notes,
    string? Month = null);

public sealed record AddExtraIncomeRequest(
    string? Name,
    string? Amount,
    string? Currency,
    string? Month,
    string? Notes);
