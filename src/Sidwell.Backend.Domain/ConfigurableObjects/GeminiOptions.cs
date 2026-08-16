namespace Sidwell.Backend.Domain.ConfigurableObjects;

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-2.5-flash";
    public int TimeoutSeconds { get; set; } = 90;
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";
}
