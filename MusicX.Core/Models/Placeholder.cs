using MusicX.Core.Helpers;
using Newtonsoft.Json;

namespace MusicX.Core.Models;

public class Placeholder : IIdentifiable
{
    [JsonProperty("title")] public string Title { get; set; }

    [JsonProperty("id")] public string Id { get; set; }

    [JsonProperty("icons")] public List<Image> Icons { get; set; } = new();

    [JsonProperty("text")] public string Text { get; set; }

    [JsonProperty("buttons")] public List<Button> Buttons { get; set; } = new();

    string IIdentifiable.Identifier => Id;
}