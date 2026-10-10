namespace SSGen.Core;

/// <summary>
/// An error whose message is safe and meaningful to show to the user, with a suggested fix.
/// </summary>
public class UserFacingException : Exception
{
    public UserFacingException() { }

    public UserFacingException(string message) : base(message) { }

    public UserFacingException(string message, Exception innerException) : base(message, innerException) { }

    public UserFacingException(string message, string? suggestedFix, Exception? innerException = null)
        : base(message, innerException)
    {
        SuggestedFix = suggestedFix;
    }

    public string? SuggestedFix { get; }
}
