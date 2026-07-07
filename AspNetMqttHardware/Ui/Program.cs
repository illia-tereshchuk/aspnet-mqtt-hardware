using Ui;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();   // live channel to the browser

// MqttBridge is both shared state (read by FactoryHub) and a background service (the pulse).
// So we register ONE instance in two roles:
builder.Services.AddSingleton<MqttBridge>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBridge>());

var app = builder.Build();

app.UseDefaultFiles();   // "/" → wwwroot/index.html
app.UseStaticFiles();    // serves wwwroot (the control panel)

app.MapHub<FactoryHub>("/hub/factory");   // the app's only endpoint — everything runs through SignalR

app.Run();
