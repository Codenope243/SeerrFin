namespace Jellyfin.Plugin.SeerrFin.Model;

public sealed class RatingBatchRequestDto
{
    public List<RatingBatchRequestItemDto> Items { get; set; } = new();
}

public sealed class RatingBatchRequestItemDto
{
    public string MediaType { get; set; } = string.Empty;

    public int TmdbId { get; set; }
}
