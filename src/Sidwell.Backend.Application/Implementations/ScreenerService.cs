using System.Globalization;
using System.Text.Json;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class ScreenerService(IUnitOfWork uow, ISettingsService settingsService) : IScreenerService
{
    private const string SearchSql = """
        SELECT
            t.symbol AS Symbol, t.name AS Name, t.exchange AS Exchange, t.currency AS Currency,
            f.dividend_yield AS DividendYield, f.pe_ratio AS PeRatio,
            f.dividend_per_share AS DividendPerShare, f.eps AS Eps, f.dividend_growth AS DividendGrowth,
            pio.score AS PiotroskiScore,
            alt.details::text AS AltmanDetailsJson,
            comp.score AS CompositeScore, comp.details::text AS CompositeDetailsJson
        FROM tickers t
        LEFT JOIN LATERAL (
            SELECT * FROM fundamentals WHERE ticker_id = t.id ORDER BY as_of_date DESC LIMIT 1
        ) f ON true
        LEFT JOIN LATERAL (
            SELECT score
            FROM algorithm_scores
            WHERE algorithm_name = 'piotroski' AND ticker_id = t.id
            ORDER BY as_of_date DESC LIMIT 1
        ) pio ON true
        LEFT JOIN LATERAL (
            SELECT details
            FROM algorithm_scores
            WHERE algorithm_name = 'altman_z' AND ticker_id = t.id
            ORDER BY as_of_date DESC LIMIT 1
        ) alt ON true
        LEFT JOIN LATERAL (
            SELECT score, details
            FROM algorithm_scores
            WHERE algorithm_name = 'composite' AND philosophy = @philosophy AND ticker_id = t.id
            ORDER BY as_of_date DESC LIMIT 1
        ) comp ON true
        WHERE (@exchange IS NULL OR t.exchange = @exchange)
          AND (@minYield IS NULL OR f.dividend_yield >= @minYield)
          AND (@maxPe IS NULL OR f.pe_ratio <= @maxPe)
          AND (@minPiotroski IS NULL OR pio.score >= @minPiotroski)
          AND (@minDividendGrowth IS NULL OR f.dividend_growth >= @minDividendGrowth)
          AND (@maxPayoutRatio IS NULL OR (f.eps IS NOT NULL AND f.eps <> 0 AND f.dividend_per_share / f.eps <= @maxPayoutRatio))
          AND (@minComposite IS NULL OR comp.score >= @minComposite)
          AND (@altmanZone IS NULL OR (alt.details -> 'outputs' ->> 'zone') = @altmanZone)
        ORDER BY t.symbol
        LIMIT 200;
        """;

    private const string PresetsSql = """
        SELECT id AS Id, name AS Name, criteria::text AS CriteriaJson
        FROM screener_presets
        WHERE user_id = @userId
        ORDER BY name;
        """;

    private const string UpsertPresetSql = """
        INSERT INTO screener_presets (user_id, name, criteria)
        VALUES (@userId, @name, @criteriaJson::jsonb)
        ON CONFLICT (user_id, name) DO UPDATE SET criteria = EXCLUDED.criteria
        RETURNING id;
        """;

    private const string DeletePresetSql = "DELETE FROM screener_presets WHERE id = @id AND user_id = @userId;";

    public async Task<IReadOnlyList<ScreenerResultRow>> SearchAsync(Guid userId, ScreenerCriteria criteria, CancellationToken ct = default)
    {
        SettingsDto settings = await settingsService.GetAsync(userId, ct);
        IReadOnlyDictionary<string, object?>? filters = criteria.Filters;

        string? exchange = ReadString(filters, "exchange");
        decimal? minYieldPct = ReadDecimal(filters, "minYield", "yield");
        decimal? maxPe = ReadDecimal(filters, "maxPe", "pe");
        decimal? minPiotroski = ReadDecimal(filters, "minPiotroski", "piotroski");
        decimal? minDividendGrowthPct = ReadDecimal(filters, "minDividendGrowth", "dividendGrowth");
        decimal? maxPayoutRatioPct = ReadDecimal(filters, "maxPayoutRatio", "payoutRatio");
        decimal? minComposite = ReadDecimal(filters, "minComposite");
        string? altmanZone = ReadString(filters, "altmanZone")?.ToUpperInvariant();
        bool? altmanSafe = ReadBool(filters, "altmanSafe");

        if (altmanZone is null && altmanSafe == true)
            altmanZone = "SAFE";

        var parameters = new
        {
            philosophy = settings.Philosophy,
            exchange = string.IsNullOrWhiteSpace(exchange) ? null : exchange.Trim().ToUpperInvariant(),
            minYield = minYieldPct.HasValue ? minYieldPct.Value / 100m : (decimal?)null,
            maxPe,
            minPiotroski,
            minDividendGrowth = minDividendGrowthPct.HasValue ? minDividendGrowthPct.Value / 100m : (decimal?)null,
            maxPayoutRatio = maxPayoutRatioPct.HasValue ? maxPayoutRatioPct.Value / 100m : (decimal?)null,
            minComposite,
            altmanZone
        };

        IReadOnlyList<ScreenerRow> rows = await uow.Dapper.QueryAsync<ScreenerRow>(SearchSql, parameters, ct: ct);

        return rows.Select(r => BuildRow(settings.Philosophy, r)).ToList();
    }

    public async Task<IReadOnlyList<ScreenerPreset>> GetPresetsAsync(Guid userId, CancellationToken ct = default)
    {
        IReadOnlyList<PresetRow> rows = await uow.Dapper.QueryAsync<PresetRow>(PresetsSql, new { userId }, ct: ct);

        return rows.Select(ToPreset).ToList();
    }

    public async Task<ScreenerPreset> CreatePresetAsync(Guid userId, string name, ScreenerCriteria criteria, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("Preset name is required.");

        string trimmedName = name.Trim();
        string criteriaJson = JsonSerializer.Serialize(criteria.Filters ?? new Dictionary<string, object?>());

        Guid id = await uow.Dapper.ExecuteScalarAsync<Guid?>(UpsertPresetSql, new { userId, name = trimmedName, criteriaJson }, ct: ct)
            ?? throw new InvalidOperationException("Screener preset insert did not return an id.");

        return new ScreenerPreset(id.ToString(), trimmedName, criteria);
    }

    public async Task DeletePresetAsync(Guid userId, Guid presetId, CancellationToken ct = default)
    {
        int affected = await uow.Dapper.ExecuteAsync(DeletePresetSql, new { id = presetId, userId }, ct: ct);

        if (affected == 0)
            throw new NotFoundException($"Screener preset '{presetId}' not found.");
    }

    private static ScreenerPreset ToPreset(PresetRow row)
    {
        Dictionary<string, object?> filters = string.IsNullOrWhiteSpace(row.CriteriaJson)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.CriteriaJson)!
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

        return new ScreenerPreset(row.Id.ToString(), row.Name, new ScreenerCriteria(filters));
    }

    private static ScreenerResultRow BuildRow(string philosophy, ScreenerRow row)
    {
        TickerSummary ticker = new(row.Symbol, row.Name, row.Exchange, row.Currency);

        CompositeScore? composite = row.CompositeScore.HasValue
            ? BuildCompositeScore(philosophy, row.CompositeScore.Value, row.CompositeDetailsJson)
            : null;

        decimal? payoutRatio = row is { Eps: { } eps, DividendPerShare: { } dps } && eps != 0
            ? dps / eps * 100m
            : null;

        Dictionary<string, string?> metrics = new()
        {
            ["dividendYield"] = row.DividendYield.HasValue ? FormatDecimal(row.DividendYield.Value * 100m) : null,
            ["peTrailing"] = row.PeRatio.HasValue ? FormatDecimal(row.PeRatio.Value) : null,
            ["piotroski"] = row.PiotroskiScore.HasValue ? row.PiotroskiScore.Value.ToString("0", CultureInfo.InvariantCulture) : null,
            ["dividendGrowth"] = row.DividendGrowth.HasValue ? FormatDecimal(row.DividendGrowth.Value * 100m) : null,
            ["payoutRatio"] = payoutRatio.HasValue ? FormatDecimal(payoutRatio.Value) : null,
            ["altmanZone"] = ExtractAltmanZone(row.AltmanDetailsJson)
        };

        return new ScreenerResultRow(ticker, composite, metrics);
    }

    private static CompositeScore BuildCompositeScore(string philosophy, decimal score, string? detailsJson)
    {
        IReadOnlyDictionary<string, JsonElement> outputs = ExtractOutputs(detailsJson);

        string label = outputs.TryGetValue("label", out JsonElement labelEl) ? labelEl.GetString() ?? "Mix-Feelings" : "Mix-Feelings";
        string color = outputs.TryGetValue("color", out JsonElement colorEl) ? colorEl.GetString() ?? "#EAB308" : "#EAB308";

        bool overridden = outputs.TryGetValue("overridden", out JsonElement overriddenEl) && overriddenEl.ValueKind == JsonValueKind.True;

        return new CompositeScore(philosophy, FormatDecimal(score), label, color, overridden);
    }

    private static string? ExtractAltmanZone(string? detailsJson)
    {
        IReadOnlyDictionary<string, JsonElement> outputs = ExtractOutputs(detailsJson);

        return outputs.TryGetValue("zone", out JsonElement zoneEl) ? zoneEl.GetString() : null;
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

    private static decimal? ReadDecimal(IReadOnlyDictionary<string, object?>? filters, params string[] keys)
    {
        if (filters is null)
            return null;

        foreach (string key in keys)
        {
            if (!filters.TryGetValue(key, out object? raw) || raw is null)
                continue;

            decimal? value = raw switch
            {
                JsonElement { ValueKind: JsonValueKind.Number } el when el.TryGetDecimal(out decimal d) => d,
                JsonElement { ValueKind: JsonValueKind.String } el when decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal ds) => ds,
                decimal dec => dec,
                double dbl => (decimal)dbl,
                int i => i,
                long l => l,
                string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal dsv) => dsv,
                _ => (decimal?)null
            };

            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?>? filters, params string[] keys)
    {
        if (filters is null)
            return null;

        foreach (string key in keys)
        {
            if (!filters.TryGetValue(key, out object? raw) || raw is null)
                continue;

            string? value = raw switch
            {
                JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
                string s => s,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object?>? filters, params string[] keys)
    {
        if (filters is null)
            return null;

        foreach (string key in keys)
        {
            if (!filters.TryGetValue(key, out object? raw) || raw is null)
                continue;

            bool? value = raw switch
            {
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                bool b => b,
                _ => (bool?)null
            };

            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static string FormatDecimal(decimal value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    private sealed record ScreenerRow(
        string Symbol, string Name, string Exchange, string Currency,
        decimal? DividendYield, decimal? PeRatio, decimal? DividendPerShare, decimal? Eps, decimal? DividendGrowth,
        decimal? PiotroskiScore, string? AltmanDetailsJson,
        decimal? CompositeScore, string? CompositeDetailsJson
    );

    private sealed record PresetRow(Guid Id, string Name, string? CriteriaJson);
}
