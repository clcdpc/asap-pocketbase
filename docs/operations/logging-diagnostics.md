# Application logging and temporary diagnostics

ASAP writes structured JSON to the console and the existing daily log file. Normal levels are:

| Category | Minimum level |
| --- | --- |
| `Asap.Web.*` | Information |
| `Microsoft.AspNetCore.*` | Warning |
| `Microsoft.EntityFrameworkCore.*` | Warning |
| `System.Net.Http.HttpClient.*` | Warning |
| `Hangfire.*` | Information |
| `Microsoft.Hosting.Lifetime` | Information |
| Other categories | Warning |

The NLog provider defaults to removing the Microsoft logger factory filter. That made the
`Logging:LogLevel` entries in `appsettings.json` ineffective while NLog's broad Information
rules still sent framework messages to both targets. ASAP now keeps the Microsoft category
filter enabled and applies matching category rules to both NLog targets.

For temporary diagnostics, edit the external JSON file selected by `Asap:ConfigFile`. Add
category prefixes under the optional `LogLevelOverrides` object, then restart the web app. For
example, add the following members at the top level of that file:

```json
"LogLevelOverrides": {
  "Microsoft.EntityFrameworkCore.Database.Command": "Debug",
  "Microsoft.AspNetCore.Hosting.Diagnostics": "Debug",
  "Microsoft.AspNetCore.Routing.EndpointMiddleware": "Debug",
  "Microsoft.AspNetCore.StaticFiles.StaticFileMiddleware": "Debug",
  "Asap.Web.Features.Staff.StaffAuthentication": "Trace"
}
```

EF Core command logging gives detailed SQL diagnostics. ASP.NET Core hosting, endpoint, and
static-file categories give request diagnostics. An application category such as the staff
authentication logger gives focused application diagnostics. Category names are prefixes, and
the most specific matching prefix wins. Values must be `Debug` or `Trace`; invalid entries fail
external configuration validation.

To restore normal logging, remove the `LogLevelOverrides` object (or the specific category
members) and restart the web app. EF Core sensitive-data logging remains disabled, including
when command diagnostics are enabled.
