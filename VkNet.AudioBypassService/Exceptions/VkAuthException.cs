using System;
using JetBrains.Annotations;
using VkNet.Model;

namespace VkNet.AudioBypassService.Exceptions;

[Serializable]
public class VkAuthException : System.Exception
{
    public VkAuthException([NotNull] VkAuthError vkAuthError) : base(vkAuthError.ErrorDescription ?? vkAuthError.Error)
    {
        AuthError = vkAuthError;
    }

    [NotNull] public VkAuthError AuthError { get; }
}