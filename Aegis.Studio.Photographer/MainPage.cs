using System.Collections.ObjectModel;

namespace Aegis.Studio.Photographer;

public sealed class MainPage : ContentPage
{
    private readonly StudioApiClient api;
    private readonly Entry server = new() { Placeholder = "https://studio.example.com" };
    private readonly Entry userName = new() { Placeholder = "User name" };
    private readonly Entry password = new() { Placeholder = "Password", IsPassword = true };
    private readonly Label status = new() { Text = "Sign in to see your workday." };
    private readonly Label welcome = new() { FontSize = 24, FontAttributes = FontAttributes.Bold };
    private readonly ObservableCollection<ScheduleEntry> schedule = [];
    private readonly VerticalStackLayout scheduleList = new() { Spacing = 10 };

    public MainPage(StudioApiClient api)
    {
        this.api = api;
        Title = "Studio Photographer";

        Button signIn = new() { Text = "Sign in" };
        signIn.Clicked += SignInClicked;
        Button refresh = new() { Text = "Refresh schedule" };
        refresh.Clicked += async (_, _) => await LoadScheduleAsync();
        Button unavailable = new() { Text = "Mark tomorrow unavailable" };
        unavailable.Clicked += MarkUnavailableClicked;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20,
                Spacing = 14,
                Children =
                {
                    new Label { Text = "Studio Photographer", FontSize = 30, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "Your bookings, schedule and availability." },
                    server, userName, password, signIn, status, welcome,
                    new Label { Text = "Upcoming", FontSize = 20, FontAttributes = FontAttributes.Bold },
                    refresh, scheduleList,
                    new Label { Text = "Availability", FontSize = 20, FontAttributes = FontAttributes.Bold },
                    unavailable
                }
            }
        };
    }

    private async void SignInClicked(object? sender, EventArgs e)
    {
        try
        {
            status.Text = "Signing in...";
            api.SetServer(server.Text ?? string.Empty);
            await api.LoginAsync(userName.Text ?? string.Empty, password.Text ?? string.Empty);
            PhotographerProfile? profile = await api.GetMeAsync();
            welcome.Text = profile is null ? string.Empty : $"Hello, {profile.DisplayName}";
            status.Text = "Connected";
            await LoadScheduleAsync();
        }
        catch (Exception exception)
        {
            status.Text = exception.Message;
        }
    }

    private async Task LoadScheduleAsync()
    {
        try
        {
            IReadOnlyList<ScheduleEntry> entries = await api.GetScheduleAsync(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
            schedule.Clear();
            foreach (ScheduleEntry entry in entries) schedule.Add(entry);
            RenderSchedule();
            status.Text = $"{schedule.Count} upcoming item(s)";
        }
        catch (Exception exception)
        {
            status.Text = exception.Message;
        }
    }

    private void RenderSchedule()
    {
        scheduleList.Children.Clear();
        foreach (ScheduleEntry item in schedule)
        {
            VerticalStackLayout card = new()
            {
                Padding = 12,
                Spacing = 4,
                Children =
                {
                    new Label { Text = item.Title, FontAttributes = FontAttributes.Bold, FontSize = 18 },
                    new Label { Text = $"{item.StartsAtUtc.ToLocalTime():ddd, MMM d h:mm tt} - {item.EndsAtUtc.ToLocalTime():h:mm tt}" },
                    new Label { Text = item.Location ?? "Location not set" },
                    new Label { Text = item.Status },
                    new Label { Text = item.Notes ?? string.Empty }
                }
            };
            scheduleList.Children.Add(new Border { Content = card, StrokeThickness = 1, Padding = 0 });
        }
    }

    private async void MarkUnavailableClicked(object? sender, EventArgs e)
    {
        try
        {
            DateTimeOffset start = new(DateTime.Today.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddDays(1)));
            await api.AddAvailabilityAsync(start.ToUniversalTime(), start.AddDays(1).ToUniversalTime(), "Unavailable", "Added from Studio Photographer");
            status.Text = "Tomorrow marked unavailable.";
        }
        catch (Exception exception)
        {
            status.Text = exception.Message;
        }
    }
}
