using System.Text.Json;

namespace Aegis.Studio.Web.Email;

public sealed class GraphEmailDeltaStateStore
{
    private readonly object syncRoot = new();
    private readonly string stateFile;

    public GraphEmailDeltaStateStore(IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        string dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);
        stateFile = Path.Combine(dataDirectory, "graph-email-delta.json");
    }

    public string? GetDeltaLink()
    {
        lock (syncRoot)
        {
            if (!File.Exists(stateFile))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(stateFile);
                GraphEmailDeltaState? state = JsonSerializer.Deserialize<GraphEmailDeltaState>(json);
                return string.IsNullOrWhiteSpace(state?.DeltaLink) ? null : state.DeltaLink;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public void SaveDeltaLink(string deltaLink)
    {
        if (string.IsNullOrWhiteSpace(deltaLink))
        {
            throw new ArgumentException("Delta link is required.", nameof(deltaLink));
        }

        lock (syncRoot)
        {
            GraphEmailDeltaState state = new(deltaLink, DateTimeOffset.UtcNow);
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            string temporaryFile = stateFile + ".tmp";
            File.WriteAllText(temporaryFile, json);
            File.Move(temporaryFile, stateFile, true);
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            if (File.Exists(stateFile))
            {
                File.Delete(stateFile);
            }
        }
    }
}

public sealed record GraphEmailDeltaState(
    string DeltaLink,
    DateTimeOffset UpdatedAt);
