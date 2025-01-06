using MusicX.Core.Helpers;
using Newtonsoft.Json;

namespace MusicX.Core.Models;

public class MusicOwner : IIdentifiable
{
    [JsonProperty("id")] public string Id { get; set; }

    [JsonProperty("image")] public List<Image> Images { get; set; }

    [JsonProperty("subtitle")] public string Subtitle { get; set; }

    [JsonProperty("title")] public string Title { get; set; }

    [JsonProperty("url")] public string Url { get; set; }

    string IIdentifiable.Identifier => Id;
}