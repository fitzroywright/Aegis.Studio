using System.DirectoryServices.Protocols;
using System.Net;
using Aegis.Studio.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aegis.Studio.Infrastructure.Authentication;

public sealed class ActiveDirectoryCredentialValidator : IWindowsCredentialValidator
{
    private readonly ActiveDirectoryOptions options;
    private readonly ActiveDirectoryUserNameFormatter userNameFormatter;
    private readonly ILogger<ActiveDirectoryCredentialValidator> logger;

    public ActiveDirectoryCredentialValidator(IConfiguration configuration, ILogger<ActiveDirectoryCredentialValidator> logger)
    {
        options = ActiveDirectoryOptions.Load(configuration);
        userNameFormatter = new ActiveDirectoryUserNameFormatter(options.Domain);
        this.logger = logger;
    }

    public Task<WindowsCredentialValidationResult> ValidateAsync(string userName, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            return Task.FromResult(WindowsCredentialValidationResult.Failure("Windows username and password are required."));
        }

        if (string.IsNullOrWhiteSpace(options.Server))
        {
            return Task.FromResult(WindowsCredentialValidationResult.Failure("Active Directory authentication has not been configured."));
        }

        if (!options.UseSsl && !options.AllowInsecureLdap)
        {
            return Task.FromResult(WindowsCredentialValidationResult.Failure("Active Directory authentication requires LDAPS unless insecure LDAP is explicitly enabled."));
        }

        try
        {
            ValidateCredentials(userName, password);
            string aegisUserName = userNameFormatter.FormatForAegis(userName);
            return Task.FromResult(WindowsCredentialValidationResult.Success(aegisUserName, aegisUserName));
        }
        catch (LdapException exception) when (exception.ErrorCode == 49)
        {
            logger.LogWarning("Active Directory rejected a login for {UserName}.", userName);
            return Task.FromResult(WindowsCredentialValidationResult.Failure("The Windows username or password is incorrect."));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Active Directory authentication failed for {UserName}.", userName);
            return Task.FromResult(WindowsCredentialValidationResult.Failure("Windows authentication is temporarily unavailable."));
        }
    }

    private void ValidateCredentials(string userName, string password)
    {
        LdapDirectoryIdentifier identifier = new(options.Server, options.Port, false, false);
        using LdapConnection connection = new(identifier)
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(userNameFormatter.FormatForBind(userName), password),
            Timeout = TimeSpan.FromSeconds(10)
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = options.UseSsl;
        connection.Bind();
    }
}
