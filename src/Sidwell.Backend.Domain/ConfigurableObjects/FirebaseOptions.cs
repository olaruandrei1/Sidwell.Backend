namespace Sidwell.Backend.Domain.ConfigurableObjects;

public sealed class FirebaseOptions
{
    public const string SectionName = "Firebase";

    public string ProjectId { get; set; } = string.Empty;
    public string PublicKeysUrl { get; set; } = "https://www.googleapis.com/robot/v1/metadata/x509/securetoken@system.gserviceaccount.com";
    public int ClockSkewSeconds { get; set; } = 60;
    public int PublicKeysCacheHours { get; set; } = 1;
}
