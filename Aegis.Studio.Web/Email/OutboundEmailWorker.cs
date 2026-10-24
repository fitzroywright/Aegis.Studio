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
    private readonly StudioDelegatedGraphSession? delegatedSession;
    private readonly IChannelSecretResolver secrets;
    private readonly Aegis.Studio.Web.Messaging.StudioMessageAttachmentStore attachments;
    private readonly Aegis.Studio.Web.Persistence.StudioDatabase? database;
    private readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(60)};
    public OutboundEmailWorker(IEnumerable<IExternalMessageChannel> externalChannels,IConfiguration configuration,OutboundEmailStore outbox,StudioOperationsFlowPublisher flows,ILogger<OutboundEmailWorker> logger,IChannelSecretResolver secrets,Aegis.Studio.Web.Messaging.StudioMessageAttachmentStore attachments,Aegis.Studio.Web.Persistence.StudioDatabase? database=null,StudioDelegatedGraphSession? delegatedSession=null)
    {
        this.delegatedSession=delegatedSession;this.database=database;this.configuration=configuration;this.outbox=outbox;this.flows=flows;this.logger=logger;this.secrets=secrets;this.attachments=attachments;
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
                    logger.LogInformation(new EventId(22110, "OutboundDeliveryAttemptStarted"), "Outbound delivery attempt started. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient} Attempt={Attempt} Sender={Sender} ConversationId={ConversationId} StudioMessageId={StudioMessageId} CorrelationId={CorrelationId}", message.Id, message.DeliveryChannel, message.ToAddress, message.Attempts, message.FromAddress, message.ConversationId, message.StudioMessageId, message.Id.ToString("D"));
                    try
                    {
                        var transport=channels[message.DeliveryChannel];
                        MessageChannel channel=message.DeliveryChannel=="Email"?MessageChannel.MsEmail:MessageChannel.WhatsApp;
                        if(transport is MicrosoftGraphEmailMessageChannel)
                        {
                            // Workspace/person identities may be distribution lists. Graph's users
                            // endpoint must address the real hub mailbox; MIME retains the
                            // audited From identity and monitored Reply-To.
                            if(delegatedSession?.Enabled == true)
                                transport=new StudioDelegatedGraphChannel(http,delegatedSession,configuration);
                            else transport=new MicrosoftGraphEmailMessageChannel(http,new MicrosoftGraphEmailOptions
                            {
                                TenantId=configuration["Messaging:MicrosoftGraph:TenantId"]??"",
                                ClientId=configuration["Messaging:MicrosoftGraph:ClientId"]??"",
                                ClientSecretName=configuration["Messaging:MicrosoftGraph:ClientSecretName"]??"messaging/msgraph/client-secret",
                                SenderUpn=StudioGraphMailbox.Resolve(configuration),SkipSenderRecipient=false
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
                        logger.LogInformation(new EventId(22111, "OutboundDeliverySubmitting"), "Submitting outbound delivery to provider. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient} CorrelationId={CorrelationId} InternetMessageId={InternetMessageId} InReplyTo={InReplyTo} ConversationId={ConversationId} StudioMessageId={StudioMessageId} Attempt={Attempt}", message.Id, message.DeliveryChannel, message.ToAddress, request.CorrelationId, rfcId, metadata.GetValueOrDefault("Email.InReplyTo"), message.ConversationId, message.StudioMessageId, message.Attempts);
                        await transport.SendAsync(recipient,request,message.Id,stoppingToken);
                        providerAccepted=true;
                        logger.LogInformation(new EventId(22112, "OutboundProviderAccepted"), "Outbound provider accepted delivery request. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient} ConversationId={ConversationId} StudioMessageId={StudioMessageId} Attempt={Attempt} CorrelationId={CorrelationId}", message.Id, message.DeliveryChannel, message.ToAddress, message.ConversationId, message.StudioMessageId, message.Attempts, message.Id.ToString("D"));
                        // Persist acceptance BEFORE the independent Sent Items lookup. Never replay
                        // an accepted delivery because threading evidence is delayed or unavailable.
                        if(outbox.MarkProviderAccepted(message.Id,null,lease) is null)
                            throw new InvalidOperationException("Provider accepted, but acceptance persistence requires reconciliation.");
                        flows.TryEnqueueOutbound(message.ConversationId.ToString("D"),"Provider",businessId:message.Id.ToString("D"),detail:"ProviderAccepted");
                        flows.TryEnqueueOutboundUnknown(message.ConversationId.ToString("D"),"Recipient",businessId:message.Id.ToString("D"),detail:"DeliveryReceiptUnavailable");
                        logger.LogInformation("Outbound acceptance persisted. DeliveryId={DeliveryId} ConversationId={ConversationId} StudioMessageId={StudioMessageId} Attempt={Attempt} CorrelationId={CorrelationId}",message.Id,message.ConversationId, message.StudioMessageId, message.Attempts, message.Id.ToString("D"));
                        string? providerId=null;
                        if(channel==MessageChannel.MsEmail)
                        {
                            if(transport is not MicrosoftGraphEmailMessageChannel && transport is not StudioDelegatedGraphChannel)
                                throw new InvalidOperationException("This email transport cannot verify its actual Internet Message-ID.");
                            var verifier=new StudioGraphSentMessageVerifier(http,configuration,secrets,delegatedSession);
                            var verified=await verifier.VerifyAsync(message,metadata.GetValueOrDefault("Studio.MessageId",message.Id.ToString("D")),metadata["Email.ReplyTo"],sendingAt,stoppingToken);
                            outbox.RecordVerifiedInternetId(message.Id,null,verified);providerId=verified.ProviderId;rfcId=verified.InternetMessageId;
                        }
                        logger.LogInformation("Outbound threading verification completed. DeliveryId={DeliveryId} ProviderMessageId={ProviderMessageId} InternetMessageId={InternetMessageId} ConversationId={ConversationId} StudioMessageId={StudioMessageId} Attempt={Attempt} CorrelationId={CorrelationId}",message.Id,providerId,rfcId, message.ConversationId, message.StudioMessageId, message.Attempts, message.Id.ToString("D"));
                    }
                    catch(StudioMailReconnectRequiredException) when(!providerAccepted)
                    {
                        outbox.MarkAttention(message.Id,lease,"OutboundMailboxReconnectRequired");
                        logger.LogWarning("Outbound mailbox authorization requires administrator reconnection. DeliveryId={DeliveryId}; no submission occurred ConversationId={ConversationId} StudioMessageId={StudioMessageId} Attempt={Attempt} CorrelationId={CorrelationId}",message.Id, message.ConversationId, message.StudioMessageId, message.Attempts, message.Id.ToString("D"));
                    }
                    catch(ExternalDeliveryOutcomeUnknownException ex) when(!providerAccepted)
                    {
                        outbox.MarkAttention(message.Id,lease,"ProviderOutcomeUncertain: reconcile sent items or message trace before resending.");
                        logger.LogError("Outbound provider outcome is uncertain. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient} ErrorType={ErrorType} ConversationId={ConversationId} StudioMessageId={StudioMessageId} Attempt={Attempt} CorrelationId={CorrelationId}", message.Id, message.DeliveryChannel, message.ToAddress, ex.GetType().Name, message.ConversationId, message.StudioMessageId, message.Attempts, message.Id.ToString("D"));
                    }
                    catch(HttpRequestException ex) when(!providerAccepted && (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound))
                    {
                        outbox.MarkAttention(message.Id,lease,"ProviderRejected:"+(int)ex.StatusCode.Value);
                        logger.LogError(new EventId(22113, "OutboundProviderRejected"), "Outbound provider rejected delivery. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient} HttpStatus={HttpStatus} ConversationId={ConversationId} StudioMessageId={StudioMessageId} Attempt={Attempt} CorrelationId={CorrelationId}", message.Id, message.DeliveryChannel, message.ToAddress, (int)ex.StatusCode.Value, message.ConversationId, message.StudioMessageId, message.Attempts, message.Id.ToString("D"));
                    }
                    catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){throw;}
                    catch(Exception ex)
                    {
                        if(providerAccepted)
                        {
                            outbox.MarkVerificationFailure(message.Id,ex.GetType().Name);
                            logger.LogError("Outbound accepted delivery needs threading verification. DeliveryId={DeliveryId} ErrorType={ErrorType}; acceptance is retained and no resend is scheduled ConversationId={ConversationId} StudioMessageId={StudioMessageId} Attempt={Attempt} CorrelationId={CorrelationId}",message.Id,ex.GetType().Name, message.ConversationId, message.StudioMessageId, message.Attempts, message.Id.ToString("D"));
                            continue;
                        }
                        var updated=outbox.MarkFailure(message.Id,ex.GetType().Name,DateTimeOffset.UtcNow.AddSeconds(Math.Min(300,5*Math.Pow(2,Math.Min(message.Attempts,6)))),lease);
                        logger.LogError(new EventId(22114, "OutboundDeliveryAttemptFailed"), "Outbound delivery attempt failed before provider acceptance. DeliveryId={DeliveryId} Channel={Channel} Recipient={Recipient} RetryState={RetryState} Attempt={Attempt} ErrorType={ErrorType} ConversationId={ConversationId} StudioMessageId={StudioMessageId} CorrelationId={CorrelationId}", message.Id, message.DeliveryChannel, message.ToAddress, updated?.Status, message.Attempts, ex.GetType().Name, message.ConversationId, message.StudioMessageId, message.Id.ToString("D"));
                        flows.TryEnqueueOutbound(message.ConversationId.ToString("D"),"Provider",businessId:message.Id.ToString("D"),detail:updated?.Status=="Failed"?"RetryExhausted":ex.GetType().Name,failed:true);
                    }
                }
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError("Outbound message processing cycle failed. ErrorType={ErrorType}",ex.GetType().Name);}
            try{await Task.Delay(interval,stoppingToken);}catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
        }
    }
    public override void Dispose(){http.Dispose();base.Dispose();}
}

