using System.Collections.Concurrent;
using System.Threading.Channels;
using Sidwell.Backend.Application.Contracts.Infrastructure;

namespace Sidwell.Backend.BackgroundServices.Channels;

public sealed class LookupQueue : ILookupQueue
{
    private readonly Channel<DividendLookupJob> _dividends = Channel.CreateUnbounded<DividendLookupJob>();
    private readonly Channel<BrokerFeeLookupJob> _brokerFees = Channel.CreateUnbounded<BrokerFeeLookupJob>();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public SemaphoreSlim GeminiGate { get; } = new(3, 3);

    public ChannelReader<DividendLookupJob> DividendReader => _dividends.Reader;
    public ChannelReader<BrokerFeeLookupJob> BrokerFeeReader => _brokerFees.Reader;

    public static string DividendDedupKey(string symbol) => $"div:{symbol.ToUpperInvariant()}";
    public static string BrokerFeeDedupKey(string broker, string market) => $"fee:{broker}|{market.ToUpperInvariant()}";

    public bool TryEnqueueDividend(DividendLookupJob job)
    {
        string key = DividendDedupKey(job.Symbol);

        if (!_inFlight.TryAdd(key, 0))
            return false;

        if (_dividends.Writer.TryWrite(job))
            return true;

        _inFlight.TryRemove(key, out _);

        return false;
    }

    public bool TryEnqueueBrokerFee(BrokerFeeLookupJob job)
    {
        string key = BrokerFeeDedupKey(job.Broker.ToString(), job.Market);

        if (!_inFlight.TryAdd(key, 0))
            return false;

        if (_brokerFees.Writer.TryWrite(job))
            return true;

        _inFlight.TryRemove(key, out _);

        return false;
    }

    public void Complete(string dedupKey) => _inFlight.TryRemove(dedupKey, out _);
}
