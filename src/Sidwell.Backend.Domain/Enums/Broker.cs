namespace Sidwell.Backend.Domain.Enums;

public enum Broker
{
    TradeVille,
    Xtb,
    Ibkr
}

public static class BrokerExtensions
{
    public static string ToDbString(this Broker broker) => broker switch
    {
        Broker.TradeVille => "TRADEVILLE",
        Broker.Xtb => "XTB",
        Broker.Ibkr => "IBKR",
        _ => throw new ArgumentOutOfRangeException(nameof(broker))
    };

    public static Broker FromDbString(string value) => value switch
    {
        "TRADEVILLE" => Broker.TradeVille,
        "XTB" => Broker.Xtb,
        "IBKR" => Broker.Ibkr,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
