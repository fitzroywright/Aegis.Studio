using System.Net;
using System.Text;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Operations;
using Common.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class EmailLoggingTests
{
    private const string PrivateBody = "private-body-must-not-appear";
    private const string PrivateAttachment = "private-attachment-must-not-appear";
    private const string PrivateCredential = "private-credential-must-not-appear";

    [Fact]
    public async Task ReceiptPrecedesRoutingAndResultRetainsMessageIdentifiers()
    {
        var log = new RecordingLogger<InboundEmailWorker>();
        var email = Email();
        Guid conversation = Guid.NewGuid();
        var expected = new StudioEmailIntakeResult(true, conversation, null, false);
        var actual = await InboundEmailWorker.RouteReceivedAsync(email, (received, token) =>
        {
            Assert.Same(email, received);
            var receipt = Assert.Single(log.Entries);
            Assert.StartsWith("Inbound email received", receipt.Text);
            Assert.Equal(email.MessageId, receipt.Fields["MessageId"]);
            Assert.Equal(email.ProviderMessageId, receipt.Fields["ProviderMessageId"]);
            Assert.Equal(email.FromAddress, receipt.Fields["Sender"]);
            Assert.Equal(email.Subject, receipt.Fields["Subject"]);
            Assert.Equal(email.ReceivedAt.ToUniversalTime(), receipt.Fields["ReceivedAtUtc"]);
            Assert.Equal(1, receipt.Fields["AttachmentCount"]);
            return Task.FromResult(expected);
        }, Flows(), log, default);

        Assert.Same(expected, actual);
        Assert.Equal(2, log.Entries.Count);
        var result = log.Entries[1];
        Assert.StartsWith("Inbound email routing completed", result.Text);
        Assert.Equal(conversation, result.Fields["ConversationId"]);
        Assert.All(log.Entries, entry => Assert.Equal(email.MessageId, entry.Fields["CorrelationId"]));
        AssertPrivate(log.Entries);
    }

    [Fact]
    public async Task RoutingExceptionLeavesReceiptAndCorrelatedFailureWithoutPayload()
    {
        var log = new RecordingLogger<InboundEmailWorker>();
        var email = Email();
        var error = new InvalidOperationException(PrivateBody + PrivateCredential);
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InboundEmailWorker.RouteReceivedAsync(email, (_, _) => throw error, Flows(), log, default));
        Assert.Same(error, actual);
        Assert.Equal(2, log.Entries.Count);
        var failure = log.Entries[1];
        Assert.Equal(LogLevel.Error, failure.Level);
        Assert.StartsWith("Inbound email routing failed", failure.Text);
        Assert.Equal(email.MessageId, failure.Fields["MessageId"]);
        Assert.Equal(email.ProviderMessageId, failure.Fields["ProviderMessageId"]);
        Assert.Equal(email.MessageId, failure.Fields["CorrelationId"]);
        Assert.Equal(nameof(InvalidOperationException), failure.Fields["FailureType"]);
        AssertPrivate(log.Entries);
    }

    [Fact]
    public async Task UnroutedReceiptRetainsReviewAndDuplicateResult()
    {
        var log = new RecordingLogger<InboundEmailWorker>();
        Guid review = Guid.NewGuid();
        await InboundEmailWorker.RouteReceivedAsync(Email(),
            (_, _) => Task.FromResult(new StudioEmailIntakeResult(false, null, review, true)),
            Flows(), log, default);
        var result = log.Entries[1];
        Assert.Equal(false, result.Fields["Routed"]);
        Assert.Equal(review, result.Fields["ReviewId"]);
        Assert.Equal(true, result.Fields["WasDuplicate"]);
        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public async Task ShutdownCancellationIsNotReportedAsRoutingFailure()
    {
        var log = new RecordingLogger<InboundEmailWorker>();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InboundEmailWorker.RouteReceivedAsync(Email(),
                (_, token) => Task.FromCanceled<StudioEmailIntakeResult>(token), Flows(), log, stop.Token));
        Assert.Single(log.Entries);
        Assert.DoesNotContain(log.Entries, entry => entry.Level == LogLevel.Error);
    }

    [StudioPostgresFact]
    public Task AcceptedDeliveryIsLoggedOnceAndSurvivesThreadingVerificationFailure()
        => CheckOutboundAsync(null, "Outbound accepted delivery needs threading verification", "ProviderAccepted");

    [StudioPostgresFact]
    public Task RejectedAttemptHasNoAcceptanceEventAndIsHeldForAttention()
        => CheckOutboundAsync(new HttpRequestException(PrivateBody + PrivateCredential, null, HttpStatusCode.Forbidden),
            "Outbound provider rejected delivery", "Attention");

    [StudioPostgresFact]
    public Task RetryableFailureProducesOneFailureEventWithoutExceptionPayload()
        => CheckOutboundAsync(new InvalidOperationException(PrivateBody + PrivateCredential),
            "Outbound delivery attempt failed before provider acceptance", "Retry");

    [StudioPostgresFact]
    public Task UncertainProviderOutcomeIsHeldWithoutClaimingAcceptance()
        => CheckOutboundAsync(new ExternalDeliveryOutcomeUnknownException(new HttpRequestException(PrivateCredential)),
            "Outbound provider outcome is uncertain", "Attention");

    private static async Task CheckOutboundAsync(Exception? error, string terminalEvent, string status)
    {
        using var context = new IntegratedWorkflowTests.WorkflowContext();
        IntakeCompletionTests.InitializeOutboxTestSchema(context.Connection);
        Guid id = Guid.NewGuid();
        using (var insert = new NpgsqlCommand("""
            insert into studio_outbound_email
              (id,conversation_id,from_address,to_address,subject,body,status,created_at_utc,kind,sending_identity_type,delivery_metadata)
            select $1,conversation_id,'workspace@example.test','client@example.test','Reply',$3,'Pending',now(),'Reply','Workspace',
              '{"Email.InReplyTo":"<received@example.test>"}'::jsonb
            from studio_jobs where id=$2
            """, context.Connection))
        {
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue(context.Job);
            insert.Parameters.AddWithValue(PrivateBody);
            insert.ExecuteNonQuery();
        }
        var outbox = new OutboundEmailStore(context.Database, new WorkspaceStore(context.Database));
        var channel = new TestChannel(error);
        var log = new RecordingLogger<OutboundEmailWorker>(terminalEvent);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:MicrosoftGraph:Email:SenderUpn"] = "hub@example.test"
        }).Build();
        // This fixture queues no StudioMessageId, so the attachment dependency is never accessed.
        using var worker = new OutboundEmailWorker([channel], configuration, outbox, Flows(), log, new Secrets(), null!);
        try
        {
            await worker.StartAsync(default);
            await log.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await worker.StopAsync(default);
        }

        var entries = log.Entries;
        Assert.Equal(1, channel.Calls);
        var attempt = Assert.Single(entries, e => e.Text.StartsWith("Outbound delivery attempt started"));
        var submit = Assert.Single(entries, e => e.Text.StartsWith("Submitting outbound delivery"));
        var terminal = Assert.Single(entries, e => e.Text.StartsWith(terminalEvent));
        Assert.True(entries.IndexOf(attempt) < entries.IndexOf(submit));
        Assert.True(entries.IndexOf(submit) < entries.IndexOf(terminal));
        Assert.Equal(id.ToString("D"), submit.Fields["CorrelationId"]);
        Assert.Equal("<received@example.test>", submit.Fields["InReplyTo"]);
        Assert.False(string.IsNullOrWhiteSpace(submit.Fields["MessageId"] as string));
        var delivery = outbox.GetDelivery(id)!;
        Assert.Equal(status, delivery.Status);
        Assert.All(entries, entry =>
        {
            Assert.Equal(id, entry.Fields["DeliveryId"]);
            Assert.Equal(delivery.ConversationId, entry.Fields["ConversationId"]);
            Assert.Equal(1, entry.Fields["Attempt"]);
        });
        if (error is null)
        {
            var accepted = Assert.Single(entries, e => e.Text.StartsWith("Outbound provider accepted delivery request"));
            var persisted = Assert.Single(entries, e => e.Text.StartsWith("Outbound acceptance persisted"));
            Assert.True(entries.IndexOf(submit) < entries.IndexOf(accepted));
            Assert.True(entries.IndexOf(accepted) < entries.IndexOf(persisted));
            Assert.True(entries.IndexOf(persisted) < entries.IndexOf(terminal));
            Assert.NotNull(delivery.ProviderAcceptedAtUtc);
            Assert.Null(outbox.ClaimNext(["Email"], Guid.NewGuid()));
        }
        else
        {
            Assert.DoesNotContain(entries, e => e.Text.StartsWith("Outbound provider accepted"));
            Assert.Null(delivery.ProviderAcceptedAtUtc);
            Assert.Single(entries, e => e.Level == LogLevel.Error);
            if (error is HttpRequestException)
            {
                Assert.Equal(403, terminal.Fields["HttpStatus"]);
                Assert.Null(outbox.ClaimNext(["Email"], Guid.NewGuid()));
            }
        }
        AssertPrivate(entries);
    }

    private static InboundEmail Email() => new("<received@example.test>", "graph-thread", "Client", "client@example.test",
        ["workspace@example.test"], [], new Dictionary<string, string> { ["Authorization"] = PrivateCredential },
        "Booking question", PrivateBody, DateTimeOffset.Parse("2026-10-23T12:00:00-05:00"),
        [new("private-file.txt", "text/plain", Encoding.UTF8.GetBytes(PrivateAttachment))], "graph-provider-id");

    private static StudioOperationsFlowPublisher Flows()
        => new(new ConfigurationBuilder().Build(), NullLogger<StudioOperationsFlowPublisher>.Instance);

    private static void AssertPrivate(IEnumerable<Entry> entries)
    {
        foreach (var entry in entries)
        {
            Assert.Null(entry.Exception);
            string output = entry.Text + string.Join(" ", entry.Fields.Values);
            Assert.DoesNotContain(PrivateBody, output);
            Assert.DoesNotContain(PrivateAttachment, output);
            Assert.DoesNotContain(PrivateCredential, output);
            Assert.DoesNotContain("private-file.txt", output);
        }
    }

    private sealed class TestChannel(Exception? error) : IExternalMessageChannel
    {
        public MessageChannel Channel => MessageChannel.MsEmail;
        public int Calls { get; private set; }
        public Task SendAsync(MessageRecipient recipient, MessageRequest request, Guid notificationId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return error is null ? Task.CompletedTask : Task.FromException(error);
        }
    }

    private sealed class Secrets : IChannelSecretResolver
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken token = default)
            => Task.FromResult<string?>(PrivateCredential);
    }

    private sealed record Entry(LogLevel Level, string Text, Dictionary<string, object?> Fields, Exception? Exception);

    private sealed class RecordingLogger<T>(string? terminalEvent = null) : ILogger<T>
    {
        private readonly object gate = new();
        private readonly List<Entry> entries = [];
        public List<Entry> Entries { get { lock (gate) return entries.ToList(); } }
        public TaskCompletionSource<bool> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string text = formatter(state, exception);
            var fields = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value);
            lock (gate) entries.Add(new(level, text, fields, exception));
            if (terminalEvent is not null && text.StartsWith(terminalEvent, StringComparison.Ordinal)) Terminal.TrySetResult(true);
        }
    }
}
