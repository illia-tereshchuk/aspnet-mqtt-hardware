using Shared;
using Microsoft.AspNetCore.SignalR;

namespace Ui;

// hub gives address and connection
// pushes are made by MqttBridge via IHubContext
public class FactoryHub : Hub
{
    private static readonly HashSet<string> AllowedCommands = new()
    {
        MqttPayload_Command.Start, MqttPayload_Command.Stop, MqttPayload_Command.SetSpeed,
    };

    private readonly MqttBridge _bridge;

    public FactoryHub(MqttBridge bridge) => _bridge = bridge;

    public override async Task OnConnectedAsync() // as soon as browser connects
    {
        await Clients.Caller.SendAsync("init", _bridge.GetInitPayload());
        await base.OnConnectedAsync();
    }

    public Task SendCommand(string deviceId, string command, double factor = 1.0) // from UI buttons
    {
        if (!AllowedCommands.Contains(command)) return Task.CompletedTask; // ignore those not allowed

        return _bridge.SendCommandAsync(deviceId, new MqttPayload_Command
        {
            Command = command,
            Factor = factor,
        });
    }
}
