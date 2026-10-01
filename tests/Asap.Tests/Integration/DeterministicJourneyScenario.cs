using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Testing;

namespace Asap.Tests.Integration;

// Finite inputs used by the retained HTTP/browser journeys. New inputs or libraries
// must be declared here or in the owning test; no unknown-call fallback is enabled.
internal static class DeterministicJourneyScenario
{
    public static DeterministicTestingPatronProvider Create()
    {
        var provider = new DeterministicTestingPatronProvider();
        provider.SetReferenceData(
            [new(1, "System", null, 0, null), new(2, "Test Library", "Test", 2, 1),
             new(3, "Other Library", "Other", 2, 1)],
            [new(1, "Adult"), new(2, "Juvenile"), new(3, "Guest"), new(14, "Young adult"),
             new(28, "Video/VG Restricted"), new(91, "Adult legacy"), new(92, "Young adult legacy")]);
        PickupBranch[] branches = [new(101, "Main Library"), new(102, "North Branch")];
        foreach (var suffix in new[] { "00001", "00002", "00003", "00011", "00020", "00030", "00031", "00032",
                     "00033", "00034", "00035", "00036", "00040", "00041", "00045", "00050", "00051", "00098",
                     "00099", "00123", "00801", "00802", "00901", "00902", "00911", "00912", "01234", "01235", "01236", "01331", "01332", "01333", "01334", "01335",
                     "01336", "01337", "01401", "01991", "01992", "01993", "02031", "02032", "02041", "02042",
                     "02103", "02116", "02120", "02121", "02122", "02123", "02124", "02125", "02126", "02127", "02128", "02129", "02901", "02902", "02903", "02904", "02905", "02911", "02912", "02913",
                     "02132", "02133", "03201", "03202", "03211", "03212", "03213", "03214", "03215", "03470",
                     "03910", "03920", "03921", "03930", "09341", "09342", "09343", "09991", "09992", "09993", "09999" })
        {
            var barcode = "200000000" + suffix;
            var patron = Patron(barcode);
            provider.AddPatron(patron, branches, 2);
            provider.AllowPickupUpdate(barcode, 2, 101);
            provider.AllowPickupUpdate(barcode, 2, 102);
        }
        var alex = Patron("20000000000001", "Alex");
        var avery = Patron("20000000000002", "Avery");
        var one = Patron("20000000000003", "One");
        provider.AddPatron(alex, branches, 2);
        provider.AddPatron(avery, branches, 2);
        provider.AddPatron(one, branches, 2);
        var foreign = new PatronSnapshot(8001, "30000000000001", "other@example.org", "Out", "Ofscope",
            2, "Juvenile", 301, 3, "Other Library", 301);
        provider.AddPatron(foreign, [new(301, "Other Main"), new(302, "Other East")], 2, 3);
        provider.SetPatronSearch("MULTIPLE", 2, [alex, avery]);
        provider.SetPatronSearch("MULTIPLE NAME", 2, [alex, avery]);
        provider.SetPatronSearch("INELIGIBLE", 2, [foreign]);
        provider.SetPatronSearch("Alex Example", 2, [alex]);
        provider.SetPatronSearch("One Result", 2, [one]);
        foreach (var query in new[] { "MULTIPLE", "INELIGIBLE", "PROVIDER", "UNRESOLVED" })
        {
            provider.SetFailure(new(TestingPolarisOperation.Refresh, 2, query),
                new PolarisOperationalException("polaris_patron_not_found", "This is a declared name search input."));
        }
        foreach (var query in new[] { "ALPHAFAIL", "PROVIDER123" })
        {
            provider.SetFailure(new(TestingPolarisOperation.Refresh, 2, query),
                new PolarisOperationalException("polaris_patron_refresh_failed", "Declared refresh outage."));
        }
        provider.SetFailure(new(TestingPolarisOperation.PatronSearch, 2, "PROVIDER"),
            new PolarisOperationalException("testing_provider_unavailable", "Declared search outage."));
        provider.SetFailure(new(TestingPolarisOperation.PatronSearch, 2, "UNRESOLVED"),
            new PolarisOperationalException("polaris_home_library_missing", "Declared unresolved home library."));
        foreach (var identifier in new[] { "9780000000001", "9780000000002", "9780000000097", "9780000000098",
                     "9780000000099", "9780000000212", "9780000000219", "9780000000220", "9780000002140",
                     "9780000002141", "9780000002142", "9780000002152", "9780000002153", "9780000002190",
                     "9780000002192", "9780000002193", "9780000013337", "978000009341", "9781234567890",
                     "012345678901", "CROSS-PATRON-ID", "NEW-LIMIT-ID", "FOUND", "MULTIPLE", "TRANSIENT", "OPERATIONAL", "NONE", "NOT_FOUND" })
        {
            provider.SetIdentifierResult(identifier, 2, identifier switch
            {
                "FOUND" or "9780000000001" => new(IdentifierLookupOutcome.Found, 9001),
                "MULTIPLE" => new(IdentifierLookupOutcome.Found, 9002, MultipleMatches: true),
                "TRANSIENT" => new(IdentifierLookupOutcome.TransientFailure, ErrorCode: "testing_transient"),
                "OPERATIONAL" => new(IdentifierLookupOutcome.OperationalFailure, ErrorCode: "testing_operational"),
                _ => new(IdentifierLookupOutcome.DefinitiveNotFound)
            });
        }
        provider.SetIdentifierResult("FOUND", 3, new(IdentifierLookupOutcome.Found, 10001));
        foreach (var bibId in new[] { 9001, 9002, 10001, int.MaxValue })
        {
            provider.SetBib(bibId, 2, new(true, $"Catalog title {bibId}", "Catalog author", "2026", "Book",
                "9780000000001", "Catalog publisher"), new(1, 2, 3, true, true));
        }
        provider.SetBib(10001, 3, new(true, "Other catalog title", "Other author", "2026", "Book"), new(2, 1, 3, true, true));
        var found = new StaffBibSearchResult([new(9001, "Catalog title 9001", "Catalog author", "2026", "Book", "9780000000001")], 1);
        foreach (var (mode, query) in new[] { ("identifier", "9780000000001"), ("upc", "012345678901"),
                     ("title", "Catalog title"), ("author", "Catalog author"), ("title", "Older query"),
                     ("title", "Current query"), ("title", "Fallback metadata"), ("title", "Browser staff title"),
                     ("title", "Browser staff suggestion catalog") })
        {
            provider.SetBibSearch(mode, query, "", "", 2, found);
        }
        provider.SetBibSearch("title_author", "", "Catalog title", "Catalog author", 2, found);
        return provider;
    }

    private static PatronSnapshot Patron(string barcode, string firstName = "Test") =>
        new(7001, barcode, $"{barcode}@example.org", firstName, firstName == "One" ? "Result" : "Example",
            1, "Adult", 101, 2, "Test Library", 101);
}
