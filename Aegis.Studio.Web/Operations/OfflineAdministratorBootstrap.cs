using Aegis.Studio.Web.Persistence;
using Common.Security.Abstractions;
using Common.Security.Authenticators;
using Common.Security.Integration;
using Common.Security.Models;
using Common.Security.Options;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public sealed record AdministratorBootstrapRequest(string LoginName, string SubjectId, string AccountId, string ActorId);
public sealed record AdministratorBootstrapPlan(string LoginName, string SubjectId, string AccountId,
    bool CreateAccount, bool ApproveAccount, bool GrantAdministrator, long? SessionVersion);

/// <summary>
/// OS-authorized, offline Demo maintenance. Planning never initializes schema or resolves a login.
/// Each write uses the shared account/audit mechanism. A failed approval leaves a Pending Access
/// account with an audited prepared role; retry completes approval without duplicating identities.
/// </summary>
public sealed class OfflineAdministratorBootstrap(StudioDatabase database, MockAuthenticationOptions mock)
{
    private const string Application = "Aegis.Studio";
    private readonly GlobalOperationalRoleStore roles = new(database);
    private RelationalAccountStore Accounts() => new(() => database.OpenConnection(), Application);

    public AdministratorBootstrapPlan Plan(AdministratorBootstrapRequest request)
    {
        Validate(request);
        var role = roles.GetRoles().SingleOrDefault(x => x.Name == StudioRoleNames.Administrator);
        if (role is null || !role.IsActive || !role.Permissions.Contains("*"))
            throw new InvalidOperationException("The existing global Administrator role must be active and retain its administration permissions.");
        using var c = database.OpenConnection();
        using var t = c.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);
        using (var readOnly = new NpgsqlCommand("set transaction read only", c, t)) readOnly.ExecuteNonQuery();
        bool schema;
        using (var q = new NpgsqlCommand("select to_regclass('common_security_accounts') is not null", c, t)) schema = (bool)q.ExecuteScalar()!;
        SecurityAccount? account = null;
        if (schema)
        {
            using (var q = new NpgsqlCommand("select 1 from common_security_revoked_identities where application_id=$1 and provider_id='mock' and scope_id=$1 and subject_id=$2", c, t))
            {
                q.Parameters.AddWithValue(Application); q.Parameters.AddWithValue(request.SubjectId);
                if (q.ExecuteScalar() is not null) throw new InvalidOperationException("The selected identity was revoked; explicit reenrollment is required.");
            }
            // Read only the exact selected identity/alias/id. Never infer a binding from a staff row.
            using (var q = new NpgsqlCommand("""
                select a.account_id,a.login_name,a.display_name,a.provider_kind,a.population,a.status,a.session_version,
                    exists(select 1 from common_security_identities i where i.application_id=a.application_id and i.account_id=a.account_id
                      and i.provider_id='mock' and i.scope_id=$1 and i.subject_id=$4 and i.provider_kind=$5) as exact_binding
                from common_security_accounts a where a.application_id=$1 and (a.account_id=$2 or a.login_key=upper($3)
                    or exists(select 1 from common_security_identities i where i.application_id=a.application_id and i.account_id=a.account_id
                      and i.provider_id='mock' and i.scope_id=$1 and i.subject_id=$4))
                """, c, t))
            {
                q.Parameters.AddWithValue(Application); q.Parameters.AddWithValue(request.AccountId); q.Parameters.AddWithValue(request.LoginName);
                q.Parameters.AddWithValue(request.SubjectId); q.Parameters.AddWithValue((int)AuthenticationProviderKind.Mock);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    if (account is not null || r.GetString(0) != request.AccountId || r.GetString(1) != request.LoginName ||
                        r.GetInt32(3) != (int)AuthenticationProviderKind.Mock || r.GetInt32(4) != (int)AccountPopulation.Staff || !r.GetBoolean(7))
                        throw new InvalidOperationException("Account alias, identifier or immutable binding conflicts. An explicit identity decision is required.");
                    account = new(r.GetString(0), r.GetString(1), r.GetString(2), (AuthenticationProviderKind)r.GetInt32(3),
                        (AccountPopulation)r.GetInt32(4), (SecurityAccountStatus)r.GetInt32(5), r.GetInt64(6));
                    if (account.Status is not (SecurityAccountStatus.Active or SecurityAccountStatus.PendingAccess))
                        throw new InvalidOperationException("Disabled or suspended accounts cannot be bootstrapped.");
                }
            }
        }
        var members = roles.GetMembers(role.Id).Where(x => string.Equals(x.UserName, request.LoginName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (members.Any(x => !x.IsActive || x.UserName != request.LoginName))
            throw new InvalidOperationException("The selected Administrator membership was revoked or has a conflicting alias.");
        t.Commit();
        return new(request.LoginName, request.SubjectId, request.AccountId, account is null,
            account?.Status != SecurityAccountStatus.Active, members.Length == 0, account?.SessionVersion);
    }

    public AdministratorBootstrapPlan Apply(AdministratorBootstrapRequest request, bool offlineConfirmed)
    {
        if (!offlineConfirmed) throw new InvalidOperationException("Stop the Studio service and confirm offline maintenance before applying.");
        Validate(request);
        using var gate = database.OpenConnection();
        using var acquire = new NpgsqlCommand("select pg_try_advisory_lock(hashtextextended('studio-offline-administrator:'||current_database()||':'||current_schema(),0))", gate);
        if (!(bool)acquire.ExecuteScalar()!) throw new InvalidOperationException("Another bootstrap operation is in progress.");
        try
        {
            var plan = Plan(request);
            var accounts = Accounts();
            accounts.EnsureSchema();
            if (plan.CreateAccount)
            {
                var identity = mock.Identities.Single(x => x.LoginName == request.LoginName);
                accounts.Create(new(request.AccountId, request.LoginName, identity.DisplayName, AuthenticationProviderKind.Mock,
                    AccountPopulation.Staff, SecurityAccountStatus.PendingAccess, new("mock", Application, request.SubjectId)), request.ActorId);
            }
            // Revalidate every persisted phase. A retry must never overwrite a changed lifecycle or binding.
            plan = Plan(request);
            if (plan.GrantAdministrator)
            {
                Guid role = roles.GetRoles().Single(x => x.Name == StudioRoleNames.Administrator).Id;
                accounts.ChangeAuthorization(request.AccountId, request.ActorId, (connection, transaction) =>
                {
                    using var check = new NpgsqlCommand("select status from common_security_accounts where application_id=$1 and account_id=$2 for update", (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
                    check.Parameters.AddWithValue(Application); check.Parameters.AddWithValue(request.AccountId);
                    if ((int)check.ExecuteScalar()! is not ((int)SecurityAccountStatus.PendingAccess) and not ((int)SecurityAccountStatus.Active))
                        throw new InvalidOperationException("The account lifecycle changed during maintenance.");
                    roles.SaveMember(role, new(request.LoginName, accounts.Find(request.AccountId)!.DisplayName, true),
                        (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
                });
            }
            plan = Plan(request);
            if (plan.ApproveAccount) accounts.SetStatus(request.AccountId, SecurityAccountStatus.Active, request.ActorId);
            return Plan(request);
        }
        finally
        {
            using var release = new NpgsqlCommand("select pg_advisory_unlock(hashtextextended('studio-offline-administrator:'||current_database()||':'||current_schema(),0))", gate);
            release.ExecuteNonQuery();
        }
    }

    private void Validate(AdministratorBootstrapRequest request)
    {
        if (!MockEnvironmentPolicy.IsPermitted(mock.Enabled, mock.EnvironmentName, mock.IsolationConfirmed) || mock.ApplicationId != Application)
            throw new InvalidOperationException("Bootstrap requires isolated Demo or Development configuration; Production is prohibited.");
        _ = new MockAuthenticator(mock); // Also reject duplicate server-owned subjects and aliases.
        var identity = mock.Identities.SingleOrDefault(x => x.LoginName == request.LoginName && x.SubjectId == request.SubjectId);
        if (identity is null) throw new InvalidOperationException("The exact selected login and subject must be in the server-owned mock allowlist.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorId);
        if (string.IsNullOrWhiteSpace(request.AccountId) || request.AccountId.Length > 500 || request.AccountId != request.AccountId.Trim())
            throw new ArgumentException("An explicitly reviewed, unchanged account identifier is required.");
    }
}
