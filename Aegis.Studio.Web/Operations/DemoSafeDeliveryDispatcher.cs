using Aegis.Studio.Web.Persistence;
using Common.Messaging;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

internal sealed class DemoSafeDeliveryDispatcher(StudioDatabase database, IRetryableExternalDeliveryDispatcher inner) : IRetryableExternalDeliveryDispatcher
{
    public async Task<ExternalDeliveryAttemptResult> DeliverWithResultAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default)
    {
        IDisposable? operation;
        while ((operation = DemoMaintenance.TryEnterOperation(database)) is null)
            await Task.Delay(250, cancellationToken);
        using (operation)
        {
            using var connection = database.OpenConnection();
            using var epoch = new NpgsqlCommand("select value from studio_state where key='studio-dataset-epoch'", connection);
            string? currentEpoch = epoch.ExecuteScalar() as string;
            if (!string.Equals(currentEpoch, item.Request.Metadata?.GetValueOrDefault("StudioDatasetEpoch"), StringComparison.Ordinal))
                return new ExternalDeliveryAttemptResult(0, 0, null);
            using var command = new NpgsqlCommand("select exists(select 1 from studio_notifications where id=$1)", connection);
            command.Parameters.AddWithValue(item.NotificationId);
            // A queued item from the removed dataset must never be sent after reset.
            if (!(bool)command.ExecuteScalar()!) return new ExternalDeliveryAttemptResult(0, 0, null);
            return await inner.DeliverWithResultAsync(item, cancellationToken);
        }
    }

    public async Task DeliverAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default)
        => _ = await DeliverWithResultAsync(item, cancellationToken);
}
