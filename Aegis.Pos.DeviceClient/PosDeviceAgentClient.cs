using System.Net.Http.Json;

namespace Aegis.Pos.DeviceClient;

public sealed class PosDeviceAgentClient
{
    private readonly HttpClient httpClient;

    public PosDeviceAgentClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<bool> PrintReceiptAsync(DeviceReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        try
        {
            using HttpResponseMessage response = await this.httpClient.PostAsJsonAsync("/api/receipts/print", receipt, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<bool> OpenCashDrawerAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await this.httpClient.PostAsJsonAsync("/api/cash-drawer/open", new { saleId }, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}

public sealed record DeviceReceipt(
    Guid SaleId,
    string Reference,
    IReadOnlyList<string> Lines,
    decimal Total,
    string Currency);
