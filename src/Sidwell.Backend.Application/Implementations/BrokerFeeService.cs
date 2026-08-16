using System.Globalization;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;
using Sidwell.Backend.Domain.Enums;

namespace Sidwell.Backend.Application.Implementations;

public sealed class BrokerFeeService(
    IUnitOfWork uow,
    TimeProvider clock,
    ILookupQueue queue
) : IBrokerFeeService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);
    private const string DefaultReferenceCurrency = "RON";

    private const string ResolveExchangeSql = "SELECT exchange FROM tickers WHERE symbol = @symbol";

    private const string ReferenceCurrencySql =
        "SELECT value FROM user_settings WHERE user_id = @userId AND key = 'reference_currency'";

    private const string FeeScheduleSql = """
        SELECT percent AS "Percent", min_fee AS "MinFee", fixed_fee AS "FixedFee",
               fx_conversion_percent AS "FxConversionPercent", currency AS "Currency", fetched_at AS "FetchedAt"
        FROM broker_fee_schedules
        WHERE broker = @broker AND market = @market
        """;

    public Task<IReadOnlyList<BrokerDto>> GetBrokersAsync(CancellationToken ct = default)
    {
        IReadOnlyList<BrokerDto> brokers =
        [
            new BrokerDto("TradeVille", Broker.TradeVille.ToDbString(), "Romanian broker, BVB-focused"),
            new BrokerDto("XTB", Broker.Xtb.ToDbString(), "Multi-market broker"),
            new BrokerDto("Interactive Brokers", Broker.Ibkr.ToDbString(), "Global multi-market broker"),
        ];

        return Task.FromResult(brokers);
    }

    public async Task<BrokerFeeEstimate> EstimateFeeAsync(Broker broker, string symbol, decimal shares, decimal price, string currency, Guid userId, CancellationToken ct = default)
    {
        string? market = await uow.Dapper.ExecuteScalarAsync<string>(ResolveExchangeSql, new { symbol }, ct);

        if (market is null)
            throw new NotFoundException($"Ticker '{symbol}' not found.");

        FeeScheduleRow? schedule = await uow.Dapper.QueryFirstOrDefaultAsync<FeeScheduleRow>(
            FeeScheduleSql, new { broker = broker.ToDbString(), market }, 
            ct
        );

        if (schedule is null)
        {
            queue.TryEnqueueBrokerFee(new BrokerFeeLookupJob(broker, market, userId));
            return new BrokerFeeEstimate(string.Empty, string.Empty, string.Empty, null, false, string.Empty);
        }

        if (clock.GetUtcNow() - schedule.FetchedAt > CacheTtl)
            queue.TryEnqueueBrokerFee(new BrokerFeeLookupJob(broker, market, userId));

        decimal notional = shares * price;

        decimal percentFee = schedule.Percent is { } percent ? notional * (percent / 100m) : 0m;
        decimal baseFee = Math.Max(percentFee, schedule.MinFee ?? 0m) + (schedule.FixedFee ?? 0m);

        decimal fxFee = await CalculateFxConversionFeeAsync(userId, currency, notional, schedule.FxConversionPercent, ct);

        decimal totalFee = baseFee + fxFee;

        return new BrokerFeeEstimate(
            Money(totalFee),
            Money(baseFee),
            Money(fxFee),
            currency,
            true,
            schedule.FetchedAt.ToString("O")
        );
    }

    private async Task<decimal> CalculateFxConversionFeeAsync(Guid userId, string currency, decimal notional, decimal? fxPercent, CancellationToken ct)
    {
        if (fxPercent is not { } pct || pct <= 0m || string.IsNullOrWhiteSpace(currency))
            return 0m;

        string referenceCurrency = await uow.Dapper.ExecuteScalarAsync<string>(ReferenceCurrencySql, new { userId }, ct)
            ?? DefaultReferenceCurrency;

        if (string.Equals(currency, referenceCurrency, StringComparison.OrdinalIgnoreCase))
            return 0m;

        return notional * (pct / 100m);
    }

    private static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private sealed record FeeScheduleRow(decimal? Percent, decimal? MinFee, decimal? FixedFee, decimal? FxConversionPercent, string? Currency, DateTimeOffset FetchedAt);
}
