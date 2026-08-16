namespace Sidwell.Backend.Domain.ConfigurableObjects;

public sealed class FinanceOptions
{
    public const string SectionName = "Finance";

    public string ReceiptStoragePath { get; set; } = "./data/receipts";
    public int ReceiptRetentionDays { get; set; } = 65;
    public int ReceiptMaxLongEdgePx { get; set; } = 1600;
    public int ReceiptJpegQuality { get; set; } = 70;
}
