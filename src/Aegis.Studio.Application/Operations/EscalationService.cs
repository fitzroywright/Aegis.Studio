using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Communications;
using Aegis.Studio.Domain.Operations;

namespace Aegis.Studio.Application.Operations;

public sealed class EscalationService
{
    private readonly ICommunicationRepository communicationRepository;
    private readonly IOperationsRepository operationsRepository;
    private readonly IUnitOfWork unitOfWork;

    public EscalationService(ICommunicationRepository communicationRepository, IOperationsRepository operationsRepository, IUnitOfWork unitOfWork)
    {
        this.communicationRepository = communicationRepository;
        this.operationsRepository = operationsRepository;
        this.unitOfWork = unitOfWork;
    }

    public async Task<int> EvaluateAsync(TimeSpan warningAge, TimeSpan criticalAge, CancellationToken cancellationToken)
    {
        IReadOnlyList<Conversation> conversations = await communicationRepository.GetUnansweredAsync(DateTimeOffset.UtcNow.Subtract(warningAge), cancellationToken);
        int created = 0;
        foreach (Conversation conversation in conversations)
        {
            TimeSpan age = DateTimeOffset.UtcNow - conversation.LastActivityUtc;
            int severity = age >= criticalAge ? 2 : 1;
            string kind = severity == 2 ? "ConversationCritical" : "ConversationWarning";
            bool exists = await communicationRepository.HasOpenEscalationAsync(conversation.Id, kind, cancellationToken);
            if (exists)
            {
                continue;
            }

            OperationalAlert alert = new(conversation.WorkspaceId, conversation.Id, conversation.BookingId, kind, $"Customer conversation has been waiting {Math.Floor(age.TotalMinutes)} minutes without a staff response.", severity);
            await operationsRepository.AddAlertAsync(alert, cancellationToken);
            created++;
        }

        if (created > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return created;
    }
}
