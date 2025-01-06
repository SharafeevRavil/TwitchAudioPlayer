using MusicX.Core.Helpers;
using Newtonsoft.Json;

namespace MusicX.Core.Models;

public class CatalogBanner : IIdentifiable
{
    [JsonProperty("id")] public int Id { get; set; }

    [JsonProperty("click_action")] public ClickAction? ClickAction { get; set; }

    [JsonProperty("buttons")] public List<Button> Buttons { get; set; }

    [JsonProperty("images")] public List<Image> Images { get; set; }

    [JsonProperty("text")] public string Text { get; set; }

    [JsonProperty("subtext")] public string SubText { get; set; }

    [JsonProperty("title")] public string Title { get; set; }

    [JsonProperty("track_code")] public string TrackCode { get; set; }

    [JsonProperty("image_mode")] public string ImageMode { get; set; }

    string IIdentifiable.Identifier => Id.ToString();
}

public class ClickAction
{
    [JsonProperty("action")] public ActionButton Action { get; set; }
}