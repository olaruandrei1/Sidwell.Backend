namespace Sidwell.Backend.Domain.ConfigurableObjects;

public sealed class InternalServicesOptions
{
    public const string SectionName = "InternalServices";

    public string CoreBaseUrl { get; set; } = "http://localhost:5000/";
    public string SyncApiBaseUrl { get; set; } = "http://localhost:5001/";
    public string Secret { get; set; } = string.Empty;
}
