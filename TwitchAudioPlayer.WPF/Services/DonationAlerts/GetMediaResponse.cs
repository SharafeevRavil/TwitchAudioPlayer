using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TwitchAudioPlayer.WPF.Services;

public partial class Media
{
    public string MediaId { get; set; }
    public string AlertId { get; set; }
    public string UserId { get; set; }
    public string Type { get; set; }
    public string SubType { get; set; }
    public string Title { get; set; }
    public AdditionalData AdditionalData { get; set; }
    public DateTimeOffset DateCreated { get; set; }
    public string IsPlayed { get; set; }
    public string DatePlayed { get; set; }
    
    public static List<Media>? ParseResponse(string responseData)
    {
        var match = MyRegex().Match(responseData);
        if (!match.Success) throw new HttpRequestException($"invalid response: {responseData}");

        var json = match.Groups[1].Value;
        var shitMediaHolder = JsonSerializer.Deserialize<ShitMediaHolder>(json);

        return shitMediaHolder?.Media
            .Select(x => new Media()
            {
                MediaId = x.MediaId,
                AlertId = x.AlertId,
                UserId = x.UserId,
                Type = x.Type,
                SubType = x.SubType,
                Title = x.Title,
                AdditionalData = JsonSerializer.Deserialize<AdditionalData>(x.AdditionalData) ?? new AdditionalData(),
                DateCreated = DateTimeOffset.Parse(x.DateCreated, null, System.Globalization.DateTimeStyles.AssumeUniversal),
                IsPlayed = x.IsPlayed,
                DatePlayed = x.DatePlayed,
            }).ToList();
    }
    
    [GeneratedRegex(@"nothing\((.*)\)")]
    private static partial Regex MyRegex();
    
    private class ShitMediaHolder
    {
        [JsonPropertyName("media")] public List<ShitMedia> Media { get; set; }

        public class ShitMedia
        {
            [JsonPropertyName("media_id")] public string MediaId { get; set; }
            [JsonPropertyName("alert_id")] public string AlertId { get; set; }
            [JsonPropertyName("user_id")] public string UserId { get; set; }
            [JsonPropertyName("type")] public string Type { get; set; }
            [JsonPropertyName("sub_type")] public string SubType { get; set; }
            [JsonPropertyName("title")] public string Title { get; set; }
            [JsonPropertyName("additional_data")] public string AdditionalData { get; set; }
            [JsonPropertyName("date_created")] public string DateCreated { get; set; }
            [JsonPropertyName("is_played")] public string IsPlayed { get; set; }
            [JsonPropertyName("date_played")] public string DatePlayed { get; set; }
        }
    }
}

public class PaidAmounts
{
    public decimal USD { get; set; }
    public decimal RUB { get; set; }
    public decimal EUR { get; set; }
    public decimal BYR { get; set; }
    public decimal KZT { get; set; }
    public decimal UAH { get; set; }
    public decimal BYN { get; set; }
    public decimal BRL { get; set; }
    public decimal TRY { get; set; }
    public decimal PLN { get; set; }
}

public class AdditionalData
{
    [JsonPropertyName("video_id")] public string VideoId { get; set; }
    [JsonPropertyName("alert_id")] public int AlertId { get; set; }
    [JsonPropertyName("alert_type")] public int AlertType { get; set; }
    [JsonPropertyName("owner")] public string Owner { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; }
    [JsonPropertyName("start_from")] public int StartFrom { get; set; }
    [JsonPropertyName("paid_amounts")] public PaidAmounts PaidAmounts { get; set; }
}