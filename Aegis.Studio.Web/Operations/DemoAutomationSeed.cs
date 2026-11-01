using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

/// <summary>Demonstration-only automation definitions. Disabled until an administrator reviews and enables them.</summary>
internal static class DemoAutomationSeed
{
    internal static void Seed(StudioDatabase database, Action<DemoSeedProgress>? report = null)
    {
        var definitions = new (string Name, string Description, string Trigger, string[] Actions)[]
        {
            ("DEMO - Booking confirmation", "Notify the front desk to verify booking and deposit details.", "JobStatusChanged",
                ["""{"type":"CreateActivity","activityType":"Other","title":"Verify booking details and deposit","priority":"Normal","dueMinutes":1440}""",
                 """{"type":"NotifyJob","subject":"Booking review required","body":"Check booking details and confirm the deposit before scheduling."}"""]),
            ("DEMO - Session preparation", "Prompt the job team to review session requirements before photography.", "ScheduleChanged",
                ["""{"type":"CreateActivity","activityType":"Other","title":"Review Session Plan and photographer readiness","priority":"High","dueMinutes":1440}""",
                 """{"type":"NotifyJob","subject":"Session preparation","body":"Review the Session Plan, groups, equipment and staff assignments."}"""]),
            ("DEMO - Upload follow-up", "Prompt the job team to verify that all expected session media was uploaded.", "ProductionChanged",
                ["""{"type":"CreateActivity","activityType":"Other","title":"Verify media uploads against Session Plan","priority":"Normal","dueMinutes":1440}"""]),
            ("DEMO - Production milestone", "Notify the job team about a production milestone without dispatching production work.", "ProductionChanged",
                ["""{"type":"NotifyJob","subject":"Production milestone","body":"Review the job's production status and next required action."}"""]),
            ("DEMO - Delivery notification", "Demonstrate requester communication subject to notification preferences.", "DeliveryChanged",
                ["""{"type":"NotifyRequester","subject":"Your photography order has an update","body":"There is an update about delivery of your photography order.","preference":"DeliveryChanged"}"""])
        };
        using var connection = database.OpenConnection();
        for (int i = 0; i < definitions.Length; i++)
        {
            var d = definitions[i];
            string actions = "[" + string.Join(",", d.Actions) + "]";
            using var command = new NpgsqlCommand("""
                insert into studio_workflow_definitions
                  (id,name,description,trigger_event,conditions_json,actions_json,is_enabled,created_at_utc,updated_at_utc)
                values($1,$2,$3,$4,'{}'::jsonb,$5::jsonb,false,now(),now())
                on conflict(name) do nothing
                """, connection);
            command.Parameters.AddWithValue(DemoDataExpansion.Id(59,i+1));
            command.Parameters.AddWithValue(d.Name);
            command.Parameters.AddWithValue(d.Description);
            command.Parameters.AddWithValue(d.Trigger);
            command.Parameters.AddWithValue(actions);
            command.ExecuteNonQuery();
            report?.Invoke(new(98,"Workflow automation examples",i+1,definitions.Length,"Created safe, disabled example automation: " + d.Name));
        }
    }
}
