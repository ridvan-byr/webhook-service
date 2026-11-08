using WebhookService.Infrastructure.Dispatch;
using WebhookService.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWebhookDelivery(builder.Configuration);
builder.Services.AddHostedService<DeliveryWorker>();

var host = builder.Build();
host.Run();
