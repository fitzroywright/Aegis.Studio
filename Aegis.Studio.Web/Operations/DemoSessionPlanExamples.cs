using Aegis.Studio.Web.SessionPlanning;
namespace Aegis.Studio.Web.Operations;

// These are demo presets, not application taxonomy. The workspace renders any
// category, hierarchy labels, hierarchy depth and required photograph names.
internal static class DemoSessionPlanExamples
{
    internal static bool IsSchool(string title) => new[] { "school", "academy", "college", "graduation", "class group" }.Any(word => title.Contains(word, StringComparison.OrdinalIgnoreCase));
    internal static DemoPlanPerson[] Build(string title, bool largeSchool)
    {
        var people = new List<DemoPlanPerson>();
        if (IsSchool(title))
        {
            int ordinal = 0;
            for (int grade = 7; grade <= (largeSchool ? 12 : 9); grade++)
            for (int section = 0; section < (largeSchool ? grade <= 10 ? 7 : 6 : 3); section++)
            {
                string className = $"{grade}{(char)('A' + section)}";
                for (int student = 0; student < (largeSchool ? 30 : 8); student++)
                {
                    ordinal++;
                    string name = ordinal is 14 or 28 ? "Amara Williams" : Name(ordinal) == "Amara Williams" ? "Amelia Williams" : Name(ordinal);
                    people.Add(new($"SCH-{ordinal:00000}", name, className, Plan("Students", [new("Grade", $"Grade {grade}"), new("Class", className)], "Student", ["Portrait", "Full length", "Creative pose"])));
                }
                people.Add(new($"CLASS-{className}", $"Class {className} group", "Class group photographs", Plan("Group photos", [new("Grade", $"Grade {grade}"), new("Class", className)], "Class photograph", ["Class group", "Informal group"])));
            }
            string[] departments = ["Administration", "Mathematics", "Science", "Languages", "Humanities", "Arts", "PE", "Support"];
            int[] sizes = largeSchool ? [12, 14, 16, 14, 12, 10, 8, 10] : [2, 2, 2, 2, 2, 2, 2, 2];
            int staff = 0;
            for (int department = 0; department < departments.Length; department++)
            {
                for (int i = 0; i < sizes[department]; i++)
                    people.Add(new($"STF-{++staff:0000}", Name(staff + 2000), departments[department], Plan("Staff", [new("Department", departments[department])], i == 0 ? "Department head" : "Staff member", ["Headshot", "Formal portrait"])));
                people.Add(new($"DEPT-{department + 1:00}", departments[department] + " team", "Department groups", Plan("Group photos", [new("Department", departments[department])], "Department photograph", ["Department group"])));
            }
        }
        else
        {
            bool wedding = title.Contains("wedding", StringComparison.OrdinalIgnoreCase);
            bool corporate = new[] { "corp", "staff", "headshot", "employee", "conference" }.Any(word => title.Contains(word, StringComparison.OrdinalIgnoreCase));
            bool product = title.Contains("product", StringComparison.OrdinalIgnoreCase) || title.Contains("commercial", StringComparison.OrdinalIgnoreCase);
            string category = wedding ? "Wedding party" : corporate ? "People" : product ? "Products" : "Portrait subjects";
            string level = wedding ? "Party" : corporate ? "Team" : product ? "Product range" : "Family group";
            string[] groups = wedding ? ["Couple", "Bridal party", "Family"] : corporate ? ["Leadership", "Operations", "Client services"] : product ? ["Catalogue", "Campaign", "Details"] : ["Whole family", "Parents", "Children"];
            for (int i = 0; i < 9; i++)
            {
                string group = groups[i / 3];
                people.Add(new($"PLAN-{i + 1:000}", product ? $"Sample product {i + 1}" : Name(i + 50), group, Plan(category, [new(level, group)], product ? "Product" : "Participant", product ? ["Front", "Detail", "Lifestyle"] : ["Portrait", "Full length", "Creative pose"])));
            }
            if (!product)
                foreach (string group in groups)
                    people.Add(new("GROUP-" + (people.Count + 1), group + " photograph", "Group photographs", Plan("Group photos", [new(level, group)], "Group photograph", ["Formal group", "Informal group"])));
        }
        return people.ToArray();
    }
    internal static SubjectPlan Plan(string category, PlanGroupLevel[] groups, string role, string[] shots) => new(1, category, groups, role, false, shots.Select(name => new PlannedShot(name, "Pending", [], null, "")).ToArray());
    private static string Name(int ordinal)
    {
        string[] given = ["Amara", "Daniel", "Jada", "Malik", "Simone", "David", "Alicia", "Omar", "Patrice", "Leah", "Joshua", "Naomi"];
        string[] family = ["Williams", "Brown", "Campbell", "Thompson", "Reid", "Morgan", "Grant", "Lewis", "Clarke", "Henry", "Wilson", "Robinson"];
        return given[ordinal % given.Length] + " " + family[(ordinal / given.Length) % family.Length];
    }
}
