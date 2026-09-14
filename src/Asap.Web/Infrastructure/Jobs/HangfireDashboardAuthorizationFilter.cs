using Hangfire.Dashboard;
using Asap.Web.Features.Staff;

namespace Asap.Web.Infrastructure.Jobs;

public sealed class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        return http.Items[StaffCurrentUserMiddleware.ItemKey] is CurrentStaff staff &&
               staff.Role == "super_admin";
    }
}
