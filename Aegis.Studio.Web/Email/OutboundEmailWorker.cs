using Common.Messaging;
using Common.Messaging.Channels.MicrosoftGraph;
using System.Text.Json;

namespace Aegis.Studio.Web.Email;

// Retain the existing queue/service identity while supporting its configured transports.
public sealed class OutboundEmailWorker : BackgroundService
{
    private readonly Dictionary<string,IExternalMessageChannel> channels;
    private readonly OutboundEmailStore outbox;
    private readonly StudioOperationsFlowPublisher flows;
    private readonly ILogger<OutboundEmailWorker> logger;
    private readonly IConfiguration configuration;
    private readonly IChannelSecretResolver secrets;
    private readonly Aegis.Studio.Web.Messaging.StudioMessageAttachmentStore attachments;
    private readonly Aegis.Studio.Web.Persistence.StudioDatabase? database;
    private readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(60)};
    public OutboundEmailWorker(IEnumerable<IExternalMessageChannel> externalChannels,IConfiguration configuration,OutboundEmailStore outbox,StudioOperationsFlowPublisher flows,ILogger<OutboundEmailWorker> logger,IChannelSecretResolver secrets,Aegis.Studio.Web.Messaging.StudioMessageAttachmentStore attachments,Aegis.Studio.Web.Persistence.StudioDatabase? database=null)
    {
        this.database=database;this.configuration=configuration;this.outbox=outbox;this.flows=flows;this.logger=logger;this.secrets=secrets;this.attachments=attachments;
        channels=new(StringComparer.Ordinal);
        foreach(var channel in externalChannels)
            if(channel.Channel is MessageChannel.MsEmail or MessageChannel.WhatsApp)channels.TryAdd(channel.Channel==MessageChannel.MsEmail?"Email":"WhatsApp",channel);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval=TimeSpan.FromSeconds(Math.Max(5,configuration.GetValue<int?>("Messaging:DeliveryPollSeconds")??30));
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var operation = database is null ? null : Aegis.Studio.Web.Operations.DemoMaintenance.TryEnterOperation(database);
                if (database is not null && operation is null) { await Task.Delay(1000, stoppingToken); continue; }
                for(int n=0;n<25&&!stoppingToken.IsCancellationRequested;n++)
                {
                    Guid lease=Guid.NewGuid();var message=outbox.ClaimNext(channels.Keys.ToArray(),lease);if(message is null)break;
                    bool providerAccepted=false;
                    logger.LogInformation("Outbound delivery attempt started. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient} Attempt={Attempt} Sender={Sender}", message.Id, message.DeliveryChannel, message.ToAddress, message.Attempts, message.FromAddress);
                    try
                    {
                        var transport=channels[message.DeliveryChannel];
                        MessageChannel channel=message.DeliveryChannel=="Email"?MessageChannel.MsEmail:MessageChannel.WhatsApp;
                        if(transport is MicrosoftGraphEmailMessageChannel)
                        {
                            // Common's Graph channel owns its configured mailbox. Construct it
                            // with the audited workspace/person sender rather than silently
                            // sending all replies from the installation's default mailbox.
                            transport=new MicrosoftGraphEmailMessageChannel(http,new MicrosoftGraphEmailOptions
                            {
                                TenantId=configuration["Messaging:MicrosoftGraph:TenantId"]??"",
                                ClientId=configuration["Messaging:MicrosoftGraph:ClientId"]??"",
                                ClientSecretName=configuration["Messaging:MicrosoftGraph:ClientSecretName"]??"messaging/msgraph/client-secret",
                                SenderUpn=message.SenderMailbox??message.FromAddress,SkipSenderRecipient=false
                            },secrets);
                        }
                        var recipient=new MessageRecipient(message.ToAddress,message.ToAddress,channel==MessageChannel.MsEmail?message.ToAddress:null,
                            Mobile:channel==MessageChannel.WhatsApp?message.ToAddress:null,PreferredChannels:channel);
                        var metadata=JsonSerializer.Deserialize<Dictionary<string,string>>(message.DeliveryMetadata)??new();
                        string? rfcId=null;
                        if(channel==MessageChannel.MsEmail)
                        {
                            rfcId=message.RfcMessageId??$"<studio.{message.Id:N}@{message.FromAddress.Split('@').Last()}>";
                            outbox.SetRfcMessageId(message.Id,lease,rfcId);
                            metadata["Email.MessageId"]=rfcId;metadata["Email.From"]=message.FromAddress;
                            if(!metadata.ContainsKey("Email.ReplyTo"))metadata["Email.ReplyTo"]=configuration["Messaging:MicrosoftGraph:Email:SenderUpn"]??"";
                        }
                        var files=message.StudioMessageId.HasValue?attachments.GetForThread(message.ConversationId).Where(a=>a.MessageId==message.StudioMessageId.Value).Select(a=>new MessageAttachment(a.FileName,a.ContentType,File.ReadAllBytes(attachments.GetPhysicalPath(a)))).ToArray():Array.Empty<MessageAttachment>();
                        if(channel==MessageChannel.MsEmail && string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("Email.ReplyTo")))throw new InvalidOperationException("A monitored Reply-To is required.");
                        string subject=message.Subject,body=message.Body;
                        if(channel==MessageChannel.MsEmail)
                        {
                            string reference=StudioEmailRouting.PublicReference(message.ConversationId,message.StudioMessageId.HasValue);
                            subject=$"[{reference}] "+System.Text.RegularExpressions.Regex.Replace(subject,@"\[AEGIS-[A-Za-z0-9]+\]\s*", "");
                            if(!body.Contains(reference,StringComparison.OrdinalIgnoreCase))body+=$"\n\nFor a new email about this request, include {reference} in the subject.";
                        }
                        var request=new MessageRequest{RecipientIds=[message.ToAddress],Title=subject,Body=body,BodyIsHtml=false,Channels=channel,Source="Aegis.Studio",CorrelationId=message.Id.ToString("D"),Attachments=files,Metadata=metadata};
                        DateTimeOffset sendingAt=DateTimeOffset.UtcNow.AddMinutes(-1);
                        logger.LogInformation("Submitting outbound delivery to provider. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient} CorrelationId={CorrelationId}", message.Id, message.DeliveryChannel, message.ToAddress, request.CorrelationId);
                        await transport.SendAsync(recipient,request,message.Id,stoppingToken);
                        providerAccepted=true;
                        logger.LogInformation("Outbound provider accepted delivery request. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient}", message.Id, message.DeliveryChannel, message.ToAddress);
                        // Persist acceptance BEFORE the independent Sent Items lookup. Never replay
                        // an accepted delivery because threading evidence is delayed or unavailable.
                        if(outbox.MarkProviderAccepted(message.Id,null,lease) is null)
                            throw new InvalidOperationException("Provider accepted, but acceptance persistence requires reconciliation.");
                        flows.TryEnqueueOutbound(message.ConversationId.ToString("D"),"Provider",businessId:message.Id.ToString("D"),detail:"ProviderAccepted");
                        flows.TryEnqueueOutboundUnknown(message.ConversationId.ToString("D"),"Recipient",businessId:message.Id.ToString("D"),detail:"DeliveryReceiptUnavailable");
                        logger.LogInformation("Outbound acceptance persisted. DeliveryId={DeliveryId} ConversationId={ConversationId}",message.Id,message.ConversationId);
                        string? providerId=null;
                        if(channel==MessageChannel.MsEmail)
                        {
                            if(transport is not MicrosoftGraphEmailMessageChannel)
                                throw new InvalidOperationException("This email transport cannot verify its actual Internet Message-ID.");
                            var verifier=new StudioGraphSentMessageVerifier(http,configuration,secrets);
                            var verified=await verifier.VerifyAsync(message,metadata.GetValueOrDefault("Studio.MessageId",message.Id.ToString("D")),metadata["Email.ReplyTo"],sendingAt,stoppingToken);
                            outbox.RecordVerifiedInternetId(message.Id,null,verified);providerId=verified.ProviderId;
                        }
                        logger.LogInformation("Outbound threading verification completed. DeliveryId={DeliveryId} ProviderMessageId={ProviderMessageId}",message.Id,providerId);
                    }
                    catch(ExternalDeliveryOutcomeUnknownException ex) when(!providerAccepted)
                    {
                        outbox.MarkAttention(message.Id,lease,"ProviderOutcomeUncertain: reconcile sent items or message trace before resending.");
                        logger.LogError(ex,"Outbound provider outcome is uncertain. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient}", message.Id, message.DeliveryChannel, message.ToAddress);
                    }
                    catch(HttpRequestException ex) when(!providerAccepted && (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound))
                    {
                        outbox.MarkAttention(message.Id,lease,"ProviderRejected:"+(int)ex.StatusCode.Value);
                        logger.LogError(ex,"Outbound provider rejected delivery. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient} HttpStatus={HttpStatus}", message.Id, message.DeliveryChannel, message.ToAddress, (int)ex.StatusCode.Value);
                    }
                    catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){throw;}
                    catch(Exception ex)
                    {
                        if(providerAccepted)
                        {
                            outbox.MarkVerificationFailure(message.Id,ex.GetType().Name);
                            logger.LogError(ex,"Outbound accepted delivery needs threading verification. DeliveryId={DeliveryId}; acceptance is retained and no resend is scheduled",message.Id);
                            continue;
                        }
                        var updated=outbox.MarkFailure(message.Id,ex.GetType().Name,DateTimeOffset.UtcNow.AddSeconds(Math.Min(300,5*Math.Pow(2,Math.Min(message.Attempts,6)))),lease);
                        logger.LogError(ex,"Outbound delivery attempt failed before provider acceptance. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient} RetryState={RetryState} Attempt={Attempt}", message.Id, message.DeliveryChannel, message.ToAddress, updated?.Status, message.Attempts);
                        flows.TryEnqueueOutbound(message.ConversationId.ToString("D"),"Provider",businessId:message.Id.ToString("D"),detail:updated?.Status=="Failed"?"RetryExhausted":ex.GetType().Name,failed:true);
                        logger.LogError(ex,"Outbound {Channel} message {MessageId} failed; queued retry state {State}",message.DeliveryChannel,message.Id,updated?.Status);
                    }
                }
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError(ex,"Outbound message processing cycle failed");}
            try{await Task.Delay(interval,stoppingToken);}catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
        }
    }
    public override void Dispose(){http.Dispose();base.Dispose();}
}


