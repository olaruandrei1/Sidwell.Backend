namespace Sidwell.Backend.Domain.ConfigurableObjects;

public sealed class BroadcastOptions
{
    public const string SectionName = "Broadcast";

    public string BaseUrl { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
}
