using Hw;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedService<HwFleetWorker>();

var host = builder.Build();
host.Run();
