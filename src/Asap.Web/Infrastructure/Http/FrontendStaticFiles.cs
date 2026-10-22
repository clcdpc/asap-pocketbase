namespace Asap.Web.Infrastructure.Http;

public static class FrontendStaticFiles
{
    public static IApplicationBuilder UseFrontendStaticFiles(this IApplicationBuilder app)
    {
        return app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                var path = context.Context.Request.Path;
                if (path.StartsWithSegments("/vendor"))
                {
                    // Vendor URLs contain pinned versions; their bytes are manifest-verified.
                    context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                    return;
                }

                var extension = Path.GetExtension(context.File.Name);
                if ((path.StartsWithSegments("/staff") || path.StartsWithSegments("/patron") ||
                     path.StartsWithSegments("/shared")) &&
                    (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".css", StringComparison.OrdinalIgnoreCase)))
                {
                    // Relative ES module imports have independent cache entries.
                    // Keep validators/304s, but require a server check on every use.
                    context.Context.Response.Headers.CacheControl = "no-cache, max-age=0, must-revalidate";
                }
            }
        });
    }
}
