using AtualizadorERP;
using AtualizadorERP.Services;
using Microsoft.Extensions.Hosting;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "Agente Atualizador ERP";
    })
    .ConfigureServices((hostContext, services) =>
    {
        services.AddHostedService<Worker>();
        services.AddSingleton<ApiService>();
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<ExtractionService>();
        services.AddSingleton<ProcessService>();
    })
    .Build();

host.Run();
