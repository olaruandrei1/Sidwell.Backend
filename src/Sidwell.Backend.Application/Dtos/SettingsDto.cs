namespace Sidwell.Backend.Application.Dtos;

public record SettingsDto(
    string Philosophy,
    string ReferenceCurrency,
    string TaxCountry,
    string PreferredBroker,
    int DividendProjectionEndYear,
    bool DividendReinvestDefault
);
