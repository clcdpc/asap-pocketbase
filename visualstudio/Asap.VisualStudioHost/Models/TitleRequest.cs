namespace Asap.VisualStudioHost.Models;

public sealed class TitleRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Barcode { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Identifier { get; set; } = string.Empty;

    public string BibId { get; set; } = string.Empty;

    public string AgeGroup { get; set; } = string.Empty;

    public string Format { get; set; } = "book";

    public string Publication { get; set; } = "Already published";

    public string Notes { get; set; } = string.Empty;

    public string EditedBy { get; set; } = "System";

    public string Status { get; set; } = "suggestion";

    public string CloseReason { get; set; } = string.Empty;

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime? LastChecked { get; set; }
}
