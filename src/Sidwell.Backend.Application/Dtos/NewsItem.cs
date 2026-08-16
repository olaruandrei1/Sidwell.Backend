namespace Sidwell.Backend.Application.Dtos;

public record NewsItem(
    string Title,
    string Url,
    string PublishedAt,
    string? Sentiment,
    string Source
);
