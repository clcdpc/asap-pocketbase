namespace Asap.Web.Features.Patron;

// Stopped-source imports deliberately retain their exact source text. Present
// only the four known historical seeds as current plain text, without writing
// during reads or changing customized copy, inheritance, or migration evidence.
internal static class PatronMessageDefaults
{
    public static string? PlainText(string? value) => value switch
    {
        "You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>" =>
            "You have successfully submitted your material suggestion! Check your email inbox for status updates.\n\nThank you for using our suggestion service.",
        "This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library's suggestion service.</div>" =>
            "This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.\n\nThank you for using this library's suggestion service.",
        "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>" =>
            "This is an eBook suggestion, please use Libby to notify us of your interest.\n\nLearn how to suggest a purchase using Libby here:\nhttps://help.libbyapp.com/en-us/6260.htm",
        "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>" =>
            "This is an eAudiobook suggestion, please use Libby to notify us of your interest.\n\nLearn how to suggest a purchase using Libby here:\nhttps://help.libbyapp.com/en-us/6260.htm",
        _ => value
    };
}
