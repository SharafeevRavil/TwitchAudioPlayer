using MusicX.Core.Helpers;
using Newtonsoft.Json;

namespace MusicX.Core.Models;

public class Suggestion : IIdentifiable
{
    [JsonProperty("id")] public string Id { get; set; }

    [JsonProperty("title")] public string Title { get; set; }

    [JsonProperty("subtitle")] public string Subtitle { get; set; }

    [JsonProperty("context")] public string Context { get; set; }

    string IIdentifiable.Identifier => Id;
}