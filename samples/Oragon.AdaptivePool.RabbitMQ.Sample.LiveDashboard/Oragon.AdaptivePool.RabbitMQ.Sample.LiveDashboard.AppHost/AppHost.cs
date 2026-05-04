var builder = DistributedApplication.CreateBuilder(args);

var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithManagementPlugin();

builder.AddProject<Projects.Oragon_AdaptivePool_RabbitMQ_Sample_LiveDashboard_Web>("web")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq);

builder.Build().Run();
