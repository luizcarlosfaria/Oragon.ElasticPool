using Oragon.AdaptivePool.RabbitMQ.Sample.LiveDashboard.Web.Components;
using Oragon.AdaptivePool.RabbitMQ.Sample.LiveDashboard.Web.Live;
using Oragon.AdaptivePool.RabbitMQ.DependencyInjection;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbitmq")
                               ?? "amqp://guest:guest@localhost:5672/";

builder.Services.AddAdaptiveConnectionPool(
    name: LivePoolNames.PoolName,
    configureFactory: factory => factory.Uri = new Uri(rabbitMqConnectionString),
    configurePool: pool => pool
        .WithBounds(min: 1, max: 10, initial: 1)
        .WithIdleTimeout(TimeSpan.FromSeconds(3))
        .WithSweepInterval(TimeSpan.FromSeconds(1))
        .WithShrinkOnUtilizationPercent(0.60)
        .WithShrinkTargetUtilizationPercent(0.75)
        .WithShrinkBatchSize(16)
        .WithShrinkCooldownWindows(1));

builder.Services.AddAdaptiveChannelPool(
    name: LivePoolNames.PoolName,
    connectionPoolName: LivePoolNames.PoolName,
    configurePool: pool => pool
        .WithBounds(min: 0, max: 50, initial: 0)
        .WithIdleTimeout(TimeSpan.FromSeconds(3))
        .WithSweepInterval(TimeSpan.FromSeconds(1))
        .WithShrinkOnUtilizationPercent(0.60)
        .WithShrinkTargetUtilizationPercent(0.75)
        .WithShrinkBatchSize(64)
        .WithShrinkCooldownWindows(1)
        // Throughput test mode: publisher confirms/tracking disabled.
        .WithChannelOptions(new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false,
            outstandingPublisherConfirmationsRateLimiter: null,
            consumerDispatchConcurrency: 1))
        .WithMaxChannelsPerConnection(16));

builder.Services.AddSingleton<LiveLoadController>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveLoadController>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
