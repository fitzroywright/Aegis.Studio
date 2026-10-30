using System.Security.Claims;
using Common.Security.Hosting;
using Common.Security.Integration;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

/// <summary>Maps preserved Studio staff aliases to verified account IDs for atomic business-role persistence.</summary>
public static class StudioStaffAuthorization
{
    public static T Change<T>(RelationalAccountStore accounts,ClaimsPrincipal actor,string targetAlias,Func<NpgsqlConnection,NpgsqlTransaction,string,T> persist)
    {
        string actorId=actor.FindFirstValue(AccountSecurityHosting.AccountIdClaim) ?? throw new BadHttpRequestException("A verified staff account is required.",401);
        var target=accounts.List().SingleOrDefault(x=>string.Equals(x.LoginName,targetAlias,StringComparison.OrdinalIgnoreCase))
            ?? throw new BadHttpRequestException("Enroll the staff identity before changing its application permissions.",400);
        T value=default!;
        accounts.ChangeAuthorization(target.Id,actorId,(connection,transaction)=>value=persist((NpgsqlConnection)connection,(NpgsqlTransaction)transaction,target.LoginName));
        return value;
    }
}
