using System.Net.Http.Headers;
using System.Text.Json;
using Common.Messaging;

namespace Aegis.Studio.Web.Email;

public sealed record VerifiedGraphEmail(string ProviderId,string InternetMessageId);
// Common.Messaging sends MIME with X-Studio-Message-Id. Read the saved provider item
// to verify the actual Internet Message-ID, From, recipient and monitored Reply-To.
public sealed class StudioGraphSentMessageVerifier(HttpClient http,IConfiguration configuration,IChannelSecretResolver secrets)
{
    public async Task<VerifiedGraphEmail> VerifyAsync(OutboundEmail delivery,string correlation,string replyTo,DateTimeOffset since,CancellationToken token)
    {
        string secret=await secrets.GetSecretAsync(configuration["Messaging:MicrosoftGraph:ClientSecretName"]??"messaging/msgraph/client-secret",token)??"";
        if(string.IsNullOrWhiteSpace(secret))throw new InvalidOperationException("Microsoft Graph verification credentials are unavailable.");
        using var auth=new HttpRequestMessage(HttpMethod.Post,$"https://login.microsoftonline.com/{Uri.EscapeDataString(configuration["Messaging:MicrosoftGraph:TenantId"]??"")}/oauth2/v2.0/token")
        {Content=new FormUrlEncodedContent(new Dictionary<string,string>{["client_id"]=configuration["Messaging:MicrosoftGraph:ClientId"]??"",["client_secret"]=secret,["scope"]="https://graph.microsoft.com/.default",["grant_type"]="client_credentials"})};
        using var authResponse=await http.SendAsync(auth,token);authResponse.EnsureSuccessStatusCode();
        using var authJson=JsonDocument.Parse(await authResponse.Content.ReadAsStringAsync(token));
        string bearer=authJson.RootElement.GetProperty("access_token").GetString()!;
        for(int attempt=0;attempt<3;attempt++)
        {
            string mailbox=Uri.EscapeDataString(StudioGraphMailbox.Resolve(configuration));
            string? url=$"https://graph.microsoft.com/v1.0/users/{mailbox}/mailFolders/sentitems/messages?$filter="+
                Uri.EscapeDataString("sentDateTime ge "+since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")+" and sentDateTime le "+since.AddMinutes(15).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))+"&$top=50&$select=id,internetMessageId,internetMessageHeaders,from,toRecipients,replyTo";
            List<VerifiedGraphEmail> matches=[];
            for(int page=0;url is not null&&page<5;page++)
            {
                if(!url.StartsWith("https://graph.microsoft.com/v1.0/",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected Graph continuation URL.");
                using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer);
                using var response=await http.SendAsync(request,token);response.EnsureSuccessStatusCode();
                using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                foreach(var item in json.RootElement.GetProperty("value").EnumerateArray())
                    if(TryMatch(item,delivery.FromAddress,delivery.ToAddress,replyTo,correlation) is { } verified)matches.Add(verified);
                url=json.RootElement.TryGetProperty("@odata.nextLink",out var next)?next.GetString():null;
            }
            var unique=matches.Distinct().ToArray();
            if(unique.Length==1)return unique[0];
            if(unique.Length>1)throw new InvalidOperationException("Multiple sent items match this delivery; reconciliation is required.");
            if(attempt<2)await Task.Delay(TimeSpan.FromSeconds(2),token);
        }
        throw new InvalidOperationException("Provider accepted email but its actual Internet Message-ID has not been verified. Reconcile sent items before resending.");
    }
    public static VerifiedGraphEmail? TryMatch(JsonElement item,string from,string to,string replyTo,string correlation)
    {
        string Address(JsonElement value)=>StudioEmailRouting.NormalizeAddress(value.GetProperty("emailAddress").GetProperty("address").GetString());
        if(!item.TryGetProperty("internetMessageHeaders",out var headers)||!headers.EnumerateArray().Any(h=>
            string.Equals(h.GetProperty("name").GetString(),"X-Studio-Message-Id",StringComparison.OrdinalIgnoreCase)&&h.GetProperty("value").GetString()==correlation))return null;
        if(!item.TryGetProperty("from",out var sender)||Address(sender)!=StudioEmailRouting.NormalizeAddress(from))return null;
        if(!item.GetProperty("toRecipients").EnumerateArray().Any(r=>Address(r)==StudioEmailRouting.NormalizeAddress(to)))return null;
        if(!item.TryGetProperty("replyTo",out var replies)||!replies.EnumerateArray().Any(r=>Address(r)==StudioEmailRouting.NormalizeAddress(replyTo)))return null;
        string? internet=item.GetProperty("internetMessageId").GetString();
        if(StudioEmailRouting.MessageIds(internet).Length!=1)return null;
        return new(item.GetProperty("id").GetString()!,StudioEmailRouting.MessageIds(internet)[0]);
    }
}
