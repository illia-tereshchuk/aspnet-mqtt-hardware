using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Shared;

namespace Ui;

public class MqttBridge : BackgroundService
{
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly IHubContext<FactoryHub> _hub;
    private readonly ILogger<MqttBridge> _logger;

    private readonly ConcurrentDictionary<string, MqttPayload_Health> _healthAll = new(); // of all devices
    private readonly ConcurrentDictionary<string, bool> _offlineAll = new(); // by "retained" statuses from MQTT
    public double PowerLimitKw { get; } // for graph calibration, and info banner

    private readonly object _lock = new();
    private readonly List<LogEntry> _log = new();
    private readonly Dictionary<string, string> _lastStates = new(); // only changes written, not all info

    private FactorySnapshot? _latestSnapshot;
    private readonly List<ConsumptionHistoryPoint> _history = new(); // graph buffer (to render not from scratch)
    private const int MaxHistoryPoints = 180;
    private const int MaxLogEntries = 50;

    private static readonly HashSet<string> FaultStates = new() // used to compare with previous and log "normal again"
    {
        HwState.Fault_Overheat, HwState.Fault_Jam, HwState.Fault_ShortCircuit, HwState.Offline,
    };

    public MqttBridge(IConfiguration config, IHubContext<FactoryHub> hub, ILogger<MqttBridge> logger)
    {
        _hub = hub;
        _logger = logger;

        string host = config["MQTT_HOST"] ?? "localhost";
        int port = int.TryParse(config["MQTT_PORT"], out var p) ? p : 1883;
        PowerLimitKw = double.TryParse(config["POWER_LIMIT_KW"], out var limit) ? limit : 280;

        _client = new MqttFactory().CreateMqttClient();
        _options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId("ui")
            .Build();

        _client.ApplicationMessageReceivedAsync += OnMessageAsync;

        _client.ConnectedAsync += async _ =>
        {
            await _client.SubscribeAsync(MqttTopic.HealthWildcard);
            await _client.SubscribeAsync(MqttTopic.IsOnlineWildcard, MqttQualityOfServiceLevel.AtLeastOnce);
            _logger.LogInformation("MQTT: connected and subscribed to health and status");
        };

        _client.DisconnectedAsync += async _ =>
        {
            _logger.LogWarning("MQTT: connection lost, reconnecting in 2s...");
            await Task.Delay(2000);
            try { await _client.ConnectAsync(_options); } catch { }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ConnectWithRetryAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = BuildSnapshot();

            SaveSnapshot(snapshot);

            _ = _hub.Clients.All.SendAsync("snapshot", snapshot, stoppingToken); // fire and forget

            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _client.ConnectAsync(_options, ct);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MQTT broker not available yet ({Message}), retrying in 2s...", ex.Message);
                await Task.Delay(2000, ct);
            }
        }
    }

    public async Task SendCommandAsync(string deviceId, MqttPayload_Command command)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(MqttTopic.Command(deviceId))
            .WithPayload(Json.Serialize(command))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(message);
        AddLog($"🖐 Operator: {command.Command} → {deviceId}");
    }

    // ---- Receiving MQTT messages ----

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        string topic = e.ApplicationMessage.Topic;
        string payload = e.ApplicationMessage.ConvertPayloadToString();

        if (topic.StartsWith("health/"))
        {
            var health = Json.Deserialize<MqttPayload_Health>(payload);
            if (health != null)
            {
                _healthAll[health.DeviceId] = health;
                _offlineAll.TryRemove(health.DeviceId, out _); // sends health = alive
                LogStateChange(health.DeviceId, health.State);
            }
        }
        else if (topic.StartsWith("isonline/"))
        {
            var status = Json.Deserialize<MqttPayload_IsOnline>(payload);
            if (status != null)
            {
                if (status.IsOnline)
                {
                    if (_offlineAll.TryRemove(status.DeviceId, out _))
                        AddLog($"✅ {status.DeviceId}: back online");
                }
                else if (_offlineAll.TryAdd(status.DeviceId, true))
                {
                    AddLog($"🔌 {status.DeviceId}: went offline");
                }
            }
        }

        return Task.CompletedTask;
    }

    private void LogStateChange(string deviceId, string state)
    {
        lock (_lock)
        {
            _lastStates.TryGetValue(deviceId, out var oldState);
            if (oldState == state) return;
            _lastStates[deviceId] = state;

            string? message = state switch
            {
                HwState.Fault_Overheat => $"🔥 {deviceId}: overheating!",
                HwState.Fault_Jam => $"⚙ {deviceId}: jammed!",
                HwState.Fault_ShortCircuit => $"💥 {deviceId}: short circuit — breaker tripped",
                _ => oldState != null && FaultStates.Contains(oldState)
                    ? $"✅ {deviceId}: back to normal"
                    : null, // transitions like normal -> stopped are not counted
            };

            if (message != null) AddLogNoLock(message);
        }
    }

    private void AddLog(string message)
    {
        lock (_lock) AddLogNoLock(message);
    }

    private void AddLogNoLock(string message)
    {
        _log.Add(new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Message = message });
        if (_log.Count > MaxLogEntries) _log.RemoveAt(0);
    }

    private FactorySnapshot BuildSnapshot()
    {
        var devices = new List<HwView>();

        foreach (var health in _healthAll.Values.OrderBy(t => t.DeviceId))
        {
            bool isOffline = _offlineAll.ContainsKey(health.DeviceId);

            devices.Add(new HwView
            {
                Id = health.DeviceId,
                Type = health.Type,
                PowerKw = isOffline ? 0 : health.PowerKw,
                TemperatureC = isOffline ? 0 : health.TemperatureC,
                State = isOffline ? HwState.Offline : health.State,
                Online = !isOffline,
            });
        }

        lock (_lock)
        {
            return new FactorySnapshot
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                TotalKw = Math.Round(devices.Where(d => d.Online).Sum(d => d.PowerKw), 1),
                LimitKw = PowerLimitKw,
                HwViews = devices,
                Log = _log.TakeLast(5).Reverse().ToList(),
            };
        }
    }

    private void SaveSnapshot(FactorySnapshot snapshot)
    {
        lock (_lock)
        {
            _latestSnapshot = snapshot;
            _history.Add(new ConsumptionHistoryPoint
            {
                T = snapshot.Time,
                TotalKw = snapshot.TotalKw,
                IsOverLimit = snapshot.TotalKw > snapshot.LimitKw,
            });
            if (_history.Count > MaxHistoryPoints) _history.RemoveAt(0);
        }
    }

    public object GetInitPayload()
    {
        lock (_lock) return new
        {
            Snapshot = _latestSnapshot,
            History = _history.ToList(),
        };
    }
}
