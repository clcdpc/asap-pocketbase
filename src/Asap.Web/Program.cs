using Asap.Core.Services;
using Asap.Infrastructure.Data;
using Asap.Infrastructure.Repositories;
using Asap.Web.Background;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SqlServerOptions>(builder.Configuration.GetSection(SqlServerOptions.SectionName));
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<ITitleRequestRepository, TitleRequestRepository>();
builder.Services.AddScoped<ITitleRequestService, TitleRequestService>();
builder.Services.AddScoped<IOutstandingTimeoutProcessor, OutstandingTimeoutProcessor>();

builder.Services.AddHostedService<OutstandingTimeoutHostedService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.Combine(builder.Environment.ContentRootPath, "..", "..", "pb_public")),
    RequestPath = ""
});

app.Run();
