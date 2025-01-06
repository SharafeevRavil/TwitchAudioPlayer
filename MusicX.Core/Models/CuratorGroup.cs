using MusicX.Core.Helpers;
using Newtonsoft.Json;

namespace MusicX.Core.Models;

public class CuratorGroup : IIdentifiable
{
    [JsonProperty("id")] public int Id { get; set; }

    [JsonProperty("track_code")] public string TrackCode { get; set; }

    string IIdentifiable.Identifier => Id.ToString();
}