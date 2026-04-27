using Asap.VisualStudioHost.Services;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<InMemoryAsapStore>();
builder.Services.AddControllers();

var app = builder.Build();

var sharedPublicPath = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "pb_public"));

app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = new PhysicalFileProvider(sharedPublicPath)
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(sharedPublicPath)
});

app.MapControllers();

app.Run();
