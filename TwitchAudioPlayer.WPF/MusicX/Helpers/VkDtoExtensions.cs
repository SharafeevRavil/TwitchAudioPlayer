using VkNet.Model;
using Image = MusicX.Core.Models.Image;

namespace TwitchAudioPlayer.WPF.MusicX.Helpers;

public static class VkDtoExtensions
{
    public static string GetFullName(this User user)
    {
        return $"{user.FirstName} {user.LastName}";
    }

    public static List<Image> ToImageList(this User user)
    {
        return new List<Image>
        {
            new()
            {
                Height = 50,
                Width = 50,
                Url = user.Photo50.ToString()
            },
            new()
            {
                Height = 100,
                Width = 100,
                Url = user.Photo100.ToString()
            }
        };
    }

    public static List<Image> ToImageList(this Group group)
    {
        return new List<Image>
        {
            new()
            {
                Height = 50,
                Width = 50,
                Url = group.Photo50.ToString()
            },
            new()
            {
                Height = 100,
                Width = 100,
                Url = group.Photo100.ToString()
            }
        };
    }

    public static List<Image> ToImageList(this ConversationChatSettings chatSettings, IEnumerable<User> users)
    {
        return chatSettings.Photo is null
            ? users.SingleOrDefault(b => b.Id == chatSettings.OwnerId)?.ToImageList() ?? new List<Image>()
            : new List<Image>
            {
                new()
                {
                    Height = 50,
                    Width = 50,
                    Url = chatSettings.Photo.Photo50.ToString()
                },
                new()
                {
                    Height = 100,
                    Width = 100,
                    Url = chatSettings.Photo.Photo100.ToString()
                }
            };
    }
}