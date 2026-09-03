namespace Aegis.Studio.Application.Abstractions;

public sealed record WindowsCredentialValidationResult(bool Succeeded, string UserName, string DisplayName, string? Error)
{
    public static WindowsCredentialValidationResult Success(string userName, string displayName)
    {
        return new WindowsCredentialValidationResult(true, userName, displayName, null);
    }

    public static WindowsCredentialValidationResult Failure(string error)
    {
        return new WindowsCredentialValidationResult(false, string.Empty, string.Empty, error);
    }
}
