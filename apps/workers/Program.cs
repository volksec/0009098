using PortalDoCorretor.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();

// Cada worker roda no seu próprio laço: a falha de um não interrompe os demais.
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<RenewalScanner>();
builder.Services.AddHostedService<BillingScheduler>();
builder.Services.AddHostedService<QuotationExpirer>();
builder.Services.AddHostedService<IntegrityChecker>();

var host = builder.Build();
await host.RunAsync();
