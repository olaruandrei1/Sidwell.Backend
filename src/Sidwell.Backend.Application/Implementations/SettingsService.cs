using System.Globalization;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;
using Sidwell.Backend.Domain.Enums;

namespace Sidwell.Backend.Application.Implementations;

public sealed class SettingsService(IUnitOfWork uow) : ISettingsService
{
    private const string PhilosophyKey = "philosophy";
    private const string ReferenceCurrencyKey = "reference_currency";
    private const string TaxCountryKey = "tax_country";
    private const string PreferredBrokerKey = "preferred_broker";
    private const string DividendProjectionEndYearKey = "dividend_projection_end_year";
    private const string DividendReinvestDefaultKey = "dividend_reinvest_default";

    private const string DefaultPhilosophy = "BALANCED";
    private const string DefaultReferenceCurrency = "RON";
    private const string DefaultTaxCountry = "RO";
    private const string DefaultPreferredBroker = "TRADEVILLE";
    private const int DefaultDividendProjectionEndYear = 2060;
    private const bool DefaultDividendReinvestDefault = true;

    private static readonly string[] AllKeys =
    [
        PhilosophyKey, ReferenceCurrencyKey, TaxCountryKey, PreferredBrokerKey,
        DividendProjectionEndYearKey, DividendReinvestDefaultKey
    ];

    private const string SelectSql =
        "SELECT key AS Key, value AS Value FROM user_settings WHERE user_id = @userId AND key = ANY(@keys)";

    private const string UpsertSql = """
        INSERT INTO user_settings (user_id, key, value, updated_at)
        VALUES (@userId, @key, @value, now())
        ON CONFLICT (user_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at = now();
        """;

    public async Task<SettingsDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        IReadOnlyList<SettingRow> rows = await uow.Dapper.QueryAsync<SettingRow>(
            SelectSql,
            new { userId, keys = AllKeys },
            ct: ct);

        string philosophy = rows.FirstOrDefault(r => r.Key == PhilosophyKey)?.Value ?? DefaultPhilosophy;
        string referenceCurrency = rows.FirstOrDefault(r => r.Key == ReferenceCurrencyKey)?.Value ?? DefaultReferenceCurrency;
        string taxCountry = rows.FirstOrDefault(r => r.Key == TaxCountryKey)?.Value ?? DefaultTaxCountry;
        string preferredBroker = rows.FirstOrDefault(r => r.Key == PreferredBrokerKey)?.Value ?? DefaultPreferredBroker;

        int dividendProjectionEndYear = int.TryParse(
            rows.FirstOrDefault(r => r.Key == DividendProjectionEndYearKey)?.Value,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int endYear)
            ? endYear
            : DefaultDividendProjectionEndYear;

        bool dividendReinvestDefault = bool.TryParse(
            rows.FirstOrDefault(r => r.Key == DividendReinvestDefaultKey)?.Value, out bool reinvest)
            ? reinvest
            : DefaultDividendReinvestDefault;

        return new SettingsDto(
            ToContractString(ParsePhilosophy(philosophy)),
            referenceCurrency,
            taxCountry.Trim().ToUpperInvariant(),
            NormalizeBroker(preferredBroker),
            dividendProjectionEndYear,
            dividendReinvestDefault
        );
    }

    public async Task<SettingsDto> UpdateAsync(
        Guid userId,
        string? philosophy,
        string? referenceCurrency,
        string? taxCountry,
        string? preferredBroker,
        int? dividendProjectionEndYear,
        bool? dividendReinvestDefault,
        CancellationToken ct = default
    )
    {
        if (philosophy is not null)
        {
            string normalized = ToContractString(ParsePhilosophy(philosophy));

            await uow.Dapper.ExecuteAsync(UpsertSql, new { userId, key = PhilosophyKey, value = normalized }, ct: ct);
        }

        if (referenceCurrency is not null)
        {
            await uow.Dapper.ExecuteAsync(UpsertSql, new { userId, key = ReferenceCurrencyKey, value = referenceCurrency.Trim().ToUpperInvariant() }, ct: ct);
        }

        if (taxCountry is not null)
        {
            await uow.Dapper.ExecuteAsync(UpsertSql, new { userId, key = TaxCountryKey, value = taxCountry.Trim().ToUpperInvariant() }, ct: ct);
        }

        if (preferredBroker is not null)
        {
            await uow.Dapper.ExecuteAsync(UpsertSql, new { userId, key = PreferredBrokerKey, value = NormalizeBroker(preferredBroker) }, ct: ct);
        }

        if (dividendProjectionEndYear is not null)
        {
            await uow.Dapper.ExecuteAsync(
                UpsertSql,
                new { userId, key = DividendProjectionEndYearKey, value = dividendProjectionEndYear.Value.ToString(CultureInfo.InvariantCulture) },
                ct: ct);
        }

        if (dividendReinvestDefault is not null)
        {
            await uow.Dapper.ExecuteAsync(
                UpsertSql,
                new { userId, key = DividendReinvestDefaultKey, value = dividendReinvestDefault.Value ? "true" : "false" },
                ct: ct);
        }

        return await GetAsync(userId, ct);
    }

    private static string NormalizeBroker(string value)
    {
        try
        {
            return BrokerExtensions.ToDbString(BrokerExtensions.FromDbString(value.Trim().ToUpperInvariant()));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ValidationException($"Unsupported broker code '{value}'.");
        }
    }

    private static Philosophy ParsePhilosophy(string value) => value.Trim().ToUpperInvariant() switch
    {
        "BALANCED" => Philosophy.Balanced,
        "MOMENTUM" => Philosophy.Momentum,
        "MEAN_REVERSION" => Philosophy.MeanReversion,
        "FUNDAMENTAL" => Philosophy.Fundamental,
        _ => Philosophy.Balanced
    };

    private static string ToContractString(Philosophy philosophy) => philosophy switch
    {
        Philosophy.Balanced => "BALANCED",
        Philosophy.Momentum => "MOMENTUM",
        Philosophy.MeanReversion => "MEAN_REVERSION",
        Philosophy.Fundamental => "FUNDAMENTAL",
        _ => "BALANCED"
    };

    private sealed record SettingRow(string Key, string Value);
}
