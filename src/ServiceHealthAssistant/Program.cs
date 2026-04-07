using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ServiceHealthAssistant.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.FormatterName = "simple";
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ServiceHealthTools>();

await builder.Build().RunAsync();

