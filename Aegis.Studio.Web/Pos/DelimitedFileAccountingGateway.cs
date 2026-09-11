using System.Globalization;
using System.Text;

namespace Aegis.Studio.Web.Pos;

public sealed class DelimitedFileAccountingGateway : IAccountingGateway
{
    private readonly string outputDirectory;
    private readonly char delimiter;

    public DelimitedFileAccountingGateway(IConfiguration configuration, IWebHostEnvironment environment)
    {
        string configured = configuration["Accounting:Export:Directory"] ?? string.Empty;
        outputDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "Storage", "accounting-exports")
            : Path.GetFullPath(configured);

        string configuredDelimiter = configuration["Accounting:Export:Delimiter"] ?? ",";
        delimiter = configuredDelimiter.Length == 1 ? configuredDelimiter[0] : ',';
        Directory.CreateDirectory(outputDirectory);
    }

    public async Task<AccountingExportResult> ExportAsync(AccountingBatch batch, CancellationToken cancellationToken)
    {
        if (!string.Equals(batch.Status, "Validated", StringComparison.OrdinalIgnoreCase))
        {
            return new AccountingExportResult(false, "InvalidStatus", null, "Only validated accounting batches can be exported.");
        }
        if (batch.DebitTotal != batch.CreditTotal)
        {
            return new AccountingExportResult(false, "Unbalanced", null, "Unbalanced accounting batches cannot be exported.");
        }

        string fileName = SanitizeFileName(batch.Reference) + ".csv";
        string path = Path.Combine(outputDirectory, fileName);
        if (File.Exists(path))
        {
            return new AccountingExportResult(true, "AlreadyExported", fileName, "The export file already exists; duplicate file generation was prevented.");
        }

        StringBuilder builder = new();
        AppendRow(builder, "BatchReference", "PeriodStartUtc", "PeriodEndUtc", "AccountCode", "Description", "Debit", "Credit");
        foreach (AccountingBatchLine line in batch.Lines)
        {
            AppendRow(
                builder,
                batch.Reference,
                batch.PeriodStartUtc.ToString("O", CultureInfo.InvariantCulture),
                batch.PeriodEndUtc.ToString("O", CultureInfo.InvariantCulture),
                line.AccountCode,
                line.Description,
                line.Debit.ToString("0.00", CultureInfo.InvariantCulture),
                line.Credit.ToString("0.00", CultureInfo.InvariantCulture));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(false), cancellationToken);
        return new AccountingExportResult(true, "Exported", fileName, $"Accounting batch exported to {fileName}.");
    }

    private void AppendRow(StringBuilder builder, params string[] values)
    {
        builder.AppendLine(string.Join(delimiter, values.Select(Escape)));
    }

    private string Escape(string value)
    {
        if (value.Contains(delimiter) || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return '"' + value.Replace("\"", "\"\"") + '"';
        }
        return value;
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
