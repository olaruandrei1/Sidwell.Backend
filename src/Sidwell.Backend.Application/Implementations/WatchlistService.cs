using System.Globalization;
using System.Text.Json;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class WatchlistService(
    IUnitOfWork uow,
    ISettingsService settingsService,
    ISyncTrigger syncTrigger,
    ICoreRecalcTrigger recalcTrigger
) : IWatchlistService
{
    private const string ListSql = """
        SELECT t.id AS TickerId, t.symbol AS Symbol, t.name AS Name, t.exchange AS Exchange, t.currency AS Currency,
               ph.close AS LatestClose, ph_prev.close AS PrevClose,
               comp.score AS CompositeScore, comp.details::text AS CompositeDetailsJson
        FROM watchlist w
        JOIN tickers t ON t.id = w.ticker_id
        LEFT JOIN LATERAL (SELECT close FROM price_history WHERE ticker_id = w.ticker_id ORDER BY date DESC LIMIT 1) ph ON true
        LEFT JOIN LATERAL (SELECT close FROM price_history WHERE ticker_id = w.ticker_id ORDER BY date DESC OFFSET 1 LIMIT 1) ph_prev ON true
        LEFT JOIN LATERAL (
            SELECT score, details
            FROM algorithm_scores
            WHERE algorithm_name = 'composite' AND ticker_id = w.ticker_id AND philosophy = @philosophy
            ORDER BY as_of_date DESC
            LIMIT 1
        ) comp ON true
        WHERE w.user_id = @userId
        ORDER BY t.symbol;
        """;

    private const string FindTickerSql = "SELECT id FROM tickers WHERE upper(symbol) = upper(@symbol) LIMIT 1;";

    private const string CreateTickerSql = """
        INSERT INTO tickers (symbol, name, exchange, currency)
        VALUES (@symbol, @symbol, 'UNKNOWN', 'USD')
        RETURNING id;
        """;

    private const string InsertWatchlistSql = """
        INSERT INTO watchlist (user_id, ticker_id)
        VALUES (@userId, @tickerId)
        ON CONFLICT (user_id, ticker_id) DO NOTHING;
        """;

    private const string DeleteWatchlistSql = """
        DELETE FROM watchlist
        WHERE user_id = @userId AND ticker_id IN (SELECT id FROM tickers WHERE upper(symbol) = upper(@symbol));
        """;

    private const string RowSql = """
        SELECT t.id AS TickerId, t.symbol AS Symbol, t.name AS Name, t.exchange AS Exchange, t.currency AS Currency,
               ph.close AS LatestClose, ph_prev.close AS PrevClose,
               comp.score AS CompositeScore, comp.details::text AS CompositeDetailsJson
        FROM tickers t
        LEFT JOIN LATERAL (SELECT close FROM price_history WHERE ticker_id = t.id ORDER BY date DESC LIMIT 1) ph ON true
        LEFT JOIN LATERAL (SELECT close FROM price_history WHERE ticker_id = t.id ORDER BY date DESC OFFSET 1 LIMIT 1) ph_prev ON true
        LEFT JOIN LATERAL (
            SELECT score, details
            FROM algorithm_scores
            WHERE algorithm_name = 'composite' AND ticker_id = t.id AND philosophy = @philosophy
            ORDER BY as_of_date DESC
            LIMIT 1
        ) comp ON true
        WHERE t.id = @tickerId;
        """;

    public async Task<IReadOnlyList<WatchlistRow>> GetAsync(Guid userId, CancellationToken ct = default)
    {
        SettingsDto settings = await settingsService.GetAsync(userId, ct);

        IReadOnlyList<Row> rows = await uow.Dapper.QueryAsync<Row>(ListSql, new { userId, philosophy = settings.Philosophy }, ct: ct);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (Row row in rows.Where(r => r.CompositeScore is null && r.LatestClose is not null))
            recalcTrigger.FireAndForget(row.TickerId, today);

        return rows.Select(r => BuildRow(settings.Philosophy, r)).ToList();
    }

    public async Task<WatchlistRow> AddAsync(Guid userId, string symbol, CancellationToken ct = default)
    {
        string trimmed = symbol.Trim().ToUpperInvariant();

        Guid? tickerId = await uow.Dapper.ExecuteScalarAsync<Guid?>(FindTickerSql, new { symbol = trimmed }, ct: ct);

        if (tickerId is null)
        {
            tickerId = await uow.Dapper.ExecuteScalarAsync<Guid>(CreateTickerSql, new { symbol = trimmed }, ct: ct);
            await syncTrigger.TriggerAsync(trimmed, ct);
        }

        await uow.Dapper.ExecuteAsync(InsertWatchlistSql, new { userId, tickerId }, ct: ct);

        SettingsDto settings = await settingsService.GetAsync(userId, ct);

        Row row = await uow.Dapper.QueryFirstOrDefaultAsync<Row>(RowSql, new { tickerId, philosophy = settings.Philosophy }, ct: ct)
            ?? throw new InvalidOperationException($"Ticker '{trimmed}' could not be resolved after creation.");

        if (row.CompositeScore is null && row.LatestClose is not null)
            recalcTrigger.FireAndForget(row.TickerId, DateOnly.FromDateTime(DateTime.UtcNow));

        return BuildRow(settings.Philosophy, row);
    }

    public Task RemoveAsync(Guid userId, string symbol, CancellationToken ct = default) =>
        uow.Dapper.ExecuteAsync(DeleteWatchlistSql, new { userId, symbol }, ct: ct);

    private static WatchlistRow BuildRow(string philosophy, Row row)
    {
        TickerSummary ticker = new(row.Symbol, row.Name, row.Exchange, row.Currency);

        string? price = row.LatestClose?.ToString("0.000000", CultureInfo.InvariantCulture);

        string? dayChangePct = row.LatestClose.HasValue && row.PrevClose is { } prev && prev != 0
            ? ((row.LatestClose.Value - prev) / prev * 100m).ToString("0.000000", CultureInfo.InvariantCulture)
            : null;

        CompositeScore? composite = row.CompositeScore.HasValue
            ? BuildCompositeScore(philosophy, row.CompositeScore.Value, row.CompositeDetailsJson)
            : null;

        string status = row.LatestClose.HasValue ? "ready" : "syncing";

        return new WatchlistRow(ticker, price, dayChangePct, composite, status);
    }

    private static CompositeScore BuildCompositeScore(string philosophy, decimal score, string? detailsJson)
    {
        IReadOnlyDictionary<string, JsonElement> outputs = ExtractOutputs(detailsJson);

        string label = outputs.TryGetValue("label", out JsonElement labelEl) ? labelEl.GetString() ?? "Mix-Feelings" : "Mix-Feelings";
        string color = outputs.TryGetValue("color", out JsonElement colorEl) ? colorEl.GetString() ?? "#EAB308" : "#EAB308";

        bool overridden = outputs.TryGetValue("overridden", out JsonElement overriddenEl) && overriddenEl.ValueKind == JsonValueKind.True;

        return new CompositeScore(philosophy, score.ToString("0.000000", CultureInfo.InvariantCulture), label, color, overridden);
    }

    private static IReadOnlyDictionary<string, JsonElement> ExtractOutputs(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
            return new Dictionary<string, JsonElement>();

        using JsonDocument document = JsonDocument.Parse(detailsJson);

        if (!document.RootElement.TryGetProperty("outputs", out JsonElement outputsElement) || outputsElement.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, JsonElement>();

        Dictionary<string, JsonElement> result = new();

        foreach (JsonProperty property in outputsElement.EnumerateObject())
            result[property.Name] = property.Value.Clone();

        return result;
    }

    private sealed record Row(
        Guid TickerId, string Symbol, string Name, string Exchange, string Currency,
        decimal? LatestClose, decimal? PrevClose,
        decimal? CompositeScore, string? CompositeDetailsJson
    );
}
