using Restaurant.Contracts.Receipts;

namespace Restaurant.Application.Common.Interfaces;

/// <summary>Lays out a resolved receipt document as a PDF (QuestPDF in Infrastructure).</summary>
public interface IReceiptPdfRenderer
{
    byte[] Render(ReceiptDocumentDto document, byte[]? logo);
}
