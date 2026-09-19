namespace Aegis.Studio.Web.WorkDay;

public static class WorkDaySemantics
{
    public static string InitialStatus(string? current)=>
        string.IsNullOrWhiteSpace(current)||current.Equals("Scheduled",StringComparison.OrdinalIgnoreCase)||current.Equals("Assigned",StringComparison.OrdinalIgnoreCase)
            ?"Assigned":current.Trim();

    public static bool CanTransition(string? current,string next)
    {
        string from=InitialStatus(current);
        string to=next.Trim();
        if(from.Equals(to,StringComparison.OrdinalIgnoreCase))return true;
        if(to is "Cancelled" or "NoShow" or "UnableToComplete")
            return from is not ("Completed" or "Cancelled");
        if(to=="Delayed")
            return from is "Assigned" or "Accepted" or "Confirmed" or "Arrived";
        return (from,to) switch
        {
            ("Assigned","Accepted")=>true,
            ("Assigned","Arrived")=>true,
            ("Accepted","Arrived")=>true,
            ("Confirmed","Arrived")=>true,
            ("Delayed","Arrived")=>true,
            ("Arrived","InProgress")=>true,
            ("InProgress","Completed")=>true,
            _=>false
        };
    }

    public static bool IsExceptionStatus(string value)=>
        value is "Cancelled" or "NoShow" or "Delayed" or "UnableToComplete";

    public static string? NormalizeExceptionStatus(string? value)=>
        value?.Trim() switch
        {
            "Cancelled"=>"Cancelled",
            "NoShow"=>"NoShow",
            "Delayed"=>"Delayed",
            "UnableToComplete"=>"UnableToComplete",
            _=>null
        };
}
