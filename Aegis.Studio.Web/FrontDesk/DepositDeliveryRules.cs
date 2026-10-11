namespace Aegis.Studio.Web.FrontDesk;
public static class DepositDeliveryRules
{
    public static string Mobile(string? phone)
    {
        string normalized=System.Text.RegularExpressions.Regex.Replace(phone??"",@"[\s()\-]","");
        if(!System.Text.RegularExpressions.Regex.IsMatch(normalized,@"^\+[1-9][0-9]{7,14}$"))
            throw new ArgumentException("Set the customer's phone in international format including +country code before requesting a WhatsApp deposit.");
        return normalized[1..];
    }
}
