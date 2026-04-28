using System.ComponentModel.DataAnnotations;

namespace Asap.Web.Models;

public sealed class SuggestionCreateInput
{
    [Required]
    [StringLength(40)]
    public string PatronBarcode { get; init; } = string.Empty;

    [Required]
    [StringLength(256)]
    public string Title { get; init; } = string.Empty;

    [StringLength(256)]
    public string Author { get; init; } = string.Empty;

    [Required]
    [StringLength(80)]
    public string Format { get; init; } = "Book";
}
