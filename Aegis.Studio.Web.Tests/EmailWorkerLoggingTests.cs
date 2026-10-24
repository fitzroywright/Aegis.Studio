using System.Net;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Email;
using Common.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Fixture = Aegis.Studio.Web.Tests.UnifiedMessagingPersistenceTests.Fixture;

namespace Aegis.Studio.Web.Tests;

[Collection("Studio email database")]
public sealed class EmailWorkerLoggingTests
{
    private const string PrivateBody = "private-email-body-never-log";
    private const string PrivateAttachment = "private-attachment-content-never-log";
    private const string PrivateError = "private-provider-response-never-log";

    [Fact]
    public async Task ReceiptIsLoggedBeforeRoutingAndResultRetainsIdentifiers()
    {
        var logger = new CaptureLogger<InboundEmailWorker>();
        using var flows = Flows();
        var email = Email();
        Guid conversation = Guid.NewGuid(), workspace = Guid.NewGuid();
        await InboundEmailWorker.ProcessMessageAsync(email, (received, token) =>
        {
            var receipt = Assert.Single(logger.Entries);
            Assert.Equal(22101, receipt.Event.Id);
            Assert.Same(email, received);
            Assert.Equal(email.MessageId, receipt.Fields["MessageId"]);
            Assert.Equal(email.ProviderMessageId, receipt.Fields["ProviderMessageId"]);
            Assert.Equal(email.MessageId, receipt.Fields["CorrelationId"]);
            Assert.Equal(email.FromAddress, receipt.Fields["Sender"]);
            Assert.Equal(email.Subject, receipt.Fields["Subject"]);
            Assert.Equal(email.ReceivedAt.ToUniversalTime(), receipt.Fields["ReceivedAtUtc"]);
            Assert.Equal(1, receipt.Fields["AttachmentCount"]);
            return Task.FromResult(new StudioEmailIntakeResult(true, conversation, null, false, workspace));
        }, flows, logger, default);
        Assert.Equal(new[] { 22101, 22102 }, logger.Entries.Select(e => e.Event.Id).ToArray());
        var result = logger.Entries[1];
        Assert.Equal(conversation, result.Fields["ConversationId"]);
        Assert.Equal(workspace, result.Fields["WorkspaceId"]);
        Assert.Equal(email.MessageId, result.Fields["CorrelationId"]);
        AssertPrivate(logger.Entries);
    }

    [Fact]
    public async Task RoutingFailureRetainsReceiptLogsOneCorrelatedFailureAndRethrows()
    {
        var logger = new CaptureLogger<InboundEmailWorker>();
        using var flows = Flows();
        var failure = new InvalidOperationException(PrivateError);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InboundEmailWorker.ProcessMessageAsync(Email(), (_, _) => Task.FromException<StudioEmailIntakeResult>(failure),
                flows, logger, default));
        Assert.Same(failure, thrown);
        Assert.Equal(new[] { 22101, 22103 }, logger.Entries.Select(e => e.Event.Id).ToArray());
        var entry = logger.Entries[1];
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(Email().MessageId, entry.Fields["MessageId"]);
        Assert.Equal(Email().ProviderMessageId, entry.Fields["ProviderMessageId"]);
        Assert.Equal(Email().MessageId, entry.Fields["CorrelationId"]);
        Assert.Equal(nameof(InvalidOperationException), entry.Fields["ErrorType"]);
        AssertPrivate(logger.Entries);
    }

    [Fact]
    public async Task HeldEmailRecordsReviewAndDuplicateWithoutClaimingItWasRouted()
    {
        var logger = new CaptureLogger<InboundEmailWorker>();
        using var flows = Flows();
        Guid review = Guid.NewGuid();
        await InboundEmailWorker.ProcessMessageAsync(Email(), (_, _) =>
            Task.FromResult(new StudioEmailIntakeResult(false, null, review, true)), flows, logger, default);
        var entry = logger.Entries[1];
        Assert.Equal(false, entry.Fields["Routed"]);
        Assert.Equal(true, entry.Fields["WasDuplicate"]);
        Assert.Equal(review, entry.Fields["ReviewId"]);
    }

    [Fact]
    public async Task ShutdownCancellationIsNotReportedAsRoutingFailure()
    {
        var logger = new CaptureLogger<InboundEmailWorker>();
        using var flows = Flows();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InboundEmailWorker.ProcessMessageAsync(Email(), (_, token) =>
                Task.FromCanceled<StudioEmailIntakeResult>(token), flows, logger, cancellation.Token));
        Assert.Equal(22101, Assert.Single(logger.Entries).Event.Id);
    }

    [StudioPostgresFact]
    public Task ProviderAcceptanceIsLoggedOnceAndRetainedAfterThreadingFailure()
        => CheckOutboundAsync(null);

    [StudioPostgresFact]
    public Task RejectedDeliveryIsLoggedWithoutAcceptanceOrAutomaticResend()
        => CheckOutboundAsync(HttpStatusCode.Forbidden);

    [StudioPostgresFact]
    public Task RetryableFailureHasOneFailureEventAndNoProviderAcceptance()
        => CheckOutboundAsync(HttpStatusCode.ServiceUnavailable);

    [StudioPostgresFact]
    public Task UncertainProviderOutcomeIsHeldWithoutAcceptanceOrAutomaticResend()
        => CheckOutboundAsync(null, outcomeUnknown: true);

    private static async Task CheckOutboundAsync(HttpStatusCode? failureStatus, bool outcomeUnknown = false)
    {
        using var fixture = await Fixture.Create();
        fixture.VerifyPaul();
        var client = fixture.Book.GetOrCreateExternal("Client", "client@outside.test");
        await fixture.Router.SendAsync(new(Guid.NewGuid(), null, "Logging test", PrivateBody, [client.Id]),
            new FormFileCollection(), fixture.Paul, default);
        var delivery = Assert.Single(fixture.Outbox.GetPending());
        var logger = new CaptureLogger<OutboundEmailWorker>(entry =>
            entry.Event.Id is 22113 or 22114 || entry.Text.StartsWith("Outbound accepted delivery needs threading verification.", StringComparison.Ordinal)
            || entry.Text.StartsWith("Outbound provider outcome is uncertain.", StringComparison.Ordinal));
        var channel = new TestChannel(failureStatus, outcomeUnknown);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:MicrosoftGraph:Email:SenderUpn"] = "router@studio.test",
            ["Messaging:DeliveryPollSeconds"] = "5"
        }).Build();
        using var flows = Flows();
        using var worker = new OutboundEmailWorker([channel], configuration, fixture.Outbox, flows,
            logger, new TestSecrets(), fixture.Attachments);
        await worker.StartAsync(default);
        try { await logger.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stop.Token);
        }
        var entries = logger.Entries;
        var attempt = Assert.Single(entries, e => e.Event.Id == 22110);
        var submission = Assert.Single(entries, e => e.Event.Id == 22111);
        Assert.Equal(delivery.Id, attempt.Fields["DeliveryId"]);
        Assert.Equal(delivery.ConversationId, attempt.Fields["ConversationId"]);
        Assert.Equal(delivery.StudioMessageId, attempt.Fields["StudioMessageId"]);
        Assert.Equal(delivery.Id.ToString("D"), attempt.Fields["CorrelationId"]);
        Assert.Equal(delivery.Id.ToString("D"), submission.Fields["CorrelationId"]);
        Assert.False(string.IsNullOrWhiteSpace(submission.Fields["InternetMessageId"] as string));
        Assert.True(submission.Fields.ContainsKey("InReplyTo"));
        Assert.All(entries, entry =>
        {
            Assert.Equal(delivery.Id, entry.Fields["DeliveryId"]);
            Assert.Equal(delivery.ConversationId, entry.Fields["ConversationId"]);
            Assert.Equal(delivery.StudioMessageId, entry.Fields["StudioMessageId"]);
            Assert.Equal(attempt.Fields["Attempt"], entry.Fields["Attempt"]);
            Assert.Equal(delivery.Id.ToString("D"), entry.Fields["CorrelationId"]);
        });
        Assert.True(Array.IndexOf(entries, attempt) < Array.IndexOf(entries, submission));
        Assert.Equal(1, channel.Sends);
        var stored = fixture.Outbox.GetDelivery(delivery.Id)!;
        if (outcomeUnknown)
        {
            Assert.Single(entries, e => e.Text.StartsWith("Outbound provider outcome is uncertain.", StringComparison.Ordinal));
            Assert.Single(entries, e => e.Level == LogLevel.Error);
            Assert.Equal("Attention", stored.Status);
            Assert.Null(stored.ProviderAcceptedAtUtc);
            Assert.Null(fixture.Outbox.ClaimNext(["Email"], Guid.NewGuid()));
            Assert.DoesNotContain(entries, e => e.Event.Id is 22112 or 22113 or 22114);
        }
        else if (failureStatus is null)
        {
            var accepted = Assert.Single(entries, e => e.Event.Id == 22112);
            Assert.True(Array.IndexOf(entries, submission) < Array.IndexOf(entries, accepted));
            Assert.Equal(delivery.Id, accepted.Fields["DeliveryId"]);
            Assert.Equal("ProviderAccepted", stored.Status);
            Assert.NotNull(stored.ProviderAcceptedAtUtc);
            Assert.StartsWith("ThreadingVerificationPending:", stored.LastError);
            Assert.Null(fixture.Outbox.ClaimNext(["Email"], Guid.NewGuid()));
            Assert.DoesNotContain(entries, e => e.Event.Id is 22113 or 22114);
        }
        else if (failureStatus == HttpStatusCode.Forbidden)
        {
            var rejected = Assert.Single(entries, e => e.Event.Id == 22113);
            Assert.Equal(delivery.Id, rejected.Fields["DeliveryId"]);
            Assert.Equal(403, rejected.Fields["HttpStatus"]);
            Assert.Equal("Attention", stored.Status);
            Assert.Null(stored.ProviderAcceptedAtUtc);
            Assert.Null(fixture.Outbox.ClaimNext(["Email"], Guid.NewGuid()));
            Assert.DoesNotContain(entries, e => e.Event.Id == 22112);
        }
        else
        {
            Assert.Single(entries, e => e.Event.Id == 22114);
            Assert.Single(entries, e => e.Level == LogLevel.Error);
            Assert.Equal("Retry", stored.Status);
            Assert.Null(stored.ProviderAcceptedAtUtc);
            Assert.DoesNotContain(entries, e => e.Event.Id == 22112);
        }
        AssertPrivate(entries);
    }

    private static InboundEmail Email() => new("<receipt@outside.test>", "graph-thread", "Client",
        "client@outside.test", ["workspace@studio.test"], [], new Dictionary<string, string> { ["Authorization"] = "test-secret-never-log" },
        "Logging test", PrivateBody, DateTimeOffset.Parse("2026-10-23T12:00:00-05:00"),
        [new("private-file-name.bin", "application/octet-stream", System.Text.Encoding.UTF8.GetBytes(PrivateAttachment))],
        "graph-provider-item");

    private static StudioOperationsFlowPublisher Flows() => new(new ConfigurationBuilder().Build(),
        NullLogger<StudioOperationsFlowPublisher>.Instance);

    private static void AssertPrivate(IEnumerable<LogEntry> entries)
    {
        foreach (var entry in entries)
        {
            Assert.Null(entry.Exception);
            string data = entry.Text + string.Join("|", entry.Fields.Values.Select(v => v?.ToString()));
            Assert.DoesNotContain(PrivateBody, data);
            Assert.DoesNotContain(PrivateAttachment, data);
            Assert.DoesNotContain("private-file-name.bin", data);
            Assert.DoesNotContain(PrivateError, data);
            Assert.DoesNotContain("test-secret-never-log", data);
        }
    }

    private sealed class TestChannel(HttpStatusCode? failureStatus, bool outcomeUnknown) : IExternalMessageChannel
    {
        public MessageChannel Channel => MessageChannel.MsEmail;
        public int Sends { get; private set; }
        public Task SendAsync(MessageRecipient recipient, MessageRequest request, Guid notificationId,
            CancellationToken cancellationToken = default)
        {
            Sends++;
            if (outcomeUnknown)
                return Task.FromException(new ExternalDeliveryOutcomeUnknownException(new HttpRequestException(PrivateError)));
            return failureStatus is null ? Task.CompletedTask :
                Task.FromException(new HttpRequestException(PrivateError, null, failureStatus));
        }
    }

    private sealed class TestSecrets : IChannelSecretResolver
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("test-secret-never-log");
    }

    private sealed record LogEntry(LogLevel Level, EventId Event, string Text,
        IReadOnlyDictionary<string, object?> Fields, Exception? Exception);

    private sealed class CaptureLogger<T>(Func<LogEntry, bool>? terminal = null) : ILogger<T>
    {
        private readonly object gate = new();
        private readonly List<LogEntry> entries = [];
        public LogEntry[] Entries { get { lock (gate) return entries.ToArray(); } }
        public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(p => p.Key, p => p.Value) : new Dictionary<string, object?>();
            var entry = new LogEntry(logLevel, eventId, formatter(state, exception), fields, exception);
            lock (gate) entries.Add(entry);
            if (terminal?.Invoke(entry) == true) Completed.TrySetResult(true);
        }
    }
}

