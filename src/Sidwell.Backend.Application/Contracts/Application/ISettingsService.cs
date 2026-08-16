using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface ISettingsService
{
    Task<SettingsDto> GetAsync(Guid userId, CancellationToken ct = default);

    Task<SettingsDto> UpdateAsync(
        Guid userId,
        string? philosophy,
        string? referenceCurrency,
        string? taxCountry,
        string? preferredBroker,
        int? dividendProjectionEndYear,
        bool? dividendReinvestDefault,
        CancellationToken ct = default
    );
}
