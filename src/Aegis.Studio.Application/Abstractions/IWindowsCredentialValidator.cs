namespace Aegis.Studio.Application.Abstractions;

public interface IWindowsCredentialValidator
{
    Task<WindowsCredentialValidationResult> ValidateAsync(string userName, string password, CancellationToken cancellationToken);
}
