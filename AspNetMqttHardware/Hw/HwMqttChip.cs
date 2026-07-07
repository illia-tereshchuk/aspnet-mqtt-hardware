using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Shared;

namespace Hw;

// consider it as a virtual "MQTT chip" in hardware
// TCP connection from one device to MQTT broker
public class HwMqttChip 
{
    private readonly HwItem _hwItem;
    private readonly object _hardwareItemLock = new();
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly Random _random;
    private readonly ILogger _logger;

    private bool _wasOnlineTickAgo = true;

    public HwMqttChip(HwItem hwItem, string host, int port, Random random, ILogger logger)
    {
        // not pinging anything, just preparing

        _hwItem = hwItem;
        _random = random;
        _logger = logger;

        _client = new MqttFactory().CreateMqttClient(); // "give MQTT client to my hardware"

        _options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)  // where hardware pings to
            .WithClientId(hwItem.Id) // how is hardware called
            // when hardware breaks or anything else...
            // send topic "isonline/{deviceId}" with payload "offline"
            .WithWillTopic(MqttTopic.IsOnline(hwItem.Id)) 
            .WithWillPayload(Json.Serialize(new MqttPayload_IsOnline
            {
                DeviceId = hwItem.Id,
                IsOnline = false,
                Ts = DateTime.UtcNow,
            }))
            .WithWillRetain(true) // require it to remain in broker as last known
            // and make sure it's delivered
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce) 
            .Build();

        // when hardware receives a command from MQTT broker
        _client.ApplicationMessageReceivedAsync += OnCommandAsync; 

        // as soon as hardware just reached MQTT broker (or reconnected)
        _client.ConnectedAsync += async _ =>
        {
            // we subscribe each time when connected, not once
            // because on reconnect, broker forgets all subscriptions

            // MQTT client says: dear broker, when something is put in my box, give me a shout
            // literally, MQTT of hardware piece subscribes to messages, dedicated to its personal topic
            await _client.SubscribeAsync(MqttTopic.Command(_hwItem.Id), MqttQualityOfServiceLevel.AtLeastOnce);

            // hw tells "Hey, I am alive"
            await MqttSend_IsOnline_Async(isOnline: true); 
        };

        _client.DisconnectedAsync += async _ => // Infinite retry connecting
        {
            await Task.Delay(2000);
            try { await _client.ConnectAsync(_options); } catch { }
        };
    }

    // device life cycle: connect and "live" — tick by tick
    // called by "HwFleetWorker", when it loops all hardware
    public async Task RunAsync(CancellationToken ct) 
    {
        await ConnectWithRetryAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            MqttPayload_Health health;
            bool isOnline;

            lock (_hardwareItemLock)
            {
                // lock as short as possible: made tick - release lock
                health = _hwItem.Work(_random);
                isOnline = _hwItem.IsOnline;
            }

            try
            {
                if (isOnline) // this approach is called "edge detection"
                {
                    if (!_wasOnlineTickAgo)
                    {
                        // device returned online after a short circuit
                        // publish status and log it
                        _wasOnlineTickAgo = true;
                        await MqttSend_IsOnline_Async(isOnline: true);
                        _logger.LogInformation("{DeviceId}: back online", _hwItem.Id);
                    }
                    else
                    {
                        // is online and was online - great, don't waste messages
                        // that one RETAINED we have in history is fairly enough
                    }
                    await MqttSend_Health_Async(health); // when online, health is always sent
                }
                else if (_wasOnlineTickAgo)
                {
                    // short circuit happened
                    // publish status and log it
                    // if the process is killed, LWT will do the same
                    _wasOnlineTickAgo = false;
                    await MqttSend_IsOnline_Async(isOnline: false);
                    _logger.LogWarning("{DeviceId}: short circuit — offline", _hwItem.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("{DeviceId}: failed to publish ({Message})", _hwItem.Id, ex.Message);
            }

            await Task.Delay(1000, ct);
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        // very first step: set connection 
        // docker container with MQTT broker may be late
        // so device should ping it as long as needed
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _client.ConnectAsync(_options, ct);
                return; // break the cycle after connection is set
            }
            catch
            {
                await Task.Delay(2000, ct);
            }
        }
    }

    private Task OnCommandAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        // when device has INCOMING message from MQTT broker
        var command = Json.Deserialize<MqttPayload_Command>(e.ApplicationMessage.ConvertPayloadToString());
        if (command == null) return Task.CompletedTask; // ignore if payload is broken or empty

        lock (_hardwareItemLock)
        {
            // because hardware queues incoming and outcoming
            // this is called "race condition" bugs potential
            _hwItem.Obey(command);
        }

        _logger.LogInformation("{DeviceId} ← {Command} (factor {Factor})",
            _hwItem.Id, command.Command, command.Factor);

        return Task.CompletedTask; // no awaiting here, no network - just return completed task
    }

    // QoS 0 - don't care if one packet is lost
    // new one will come in 1 second anyway
    private Task MqttSend_Health_Async(MqttPayload_Health health) 
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(MqttTopic.Health(_hwItem.Id))
            .WithPayload(Json.Serialize(health))
            .Build();

        return _client.PublishAsync(message);
    }

    // this is MQTT message
    // QoS 1 - at least once, because we want to be sure that the status is delivered
    private Task MqttSend_IsOnline_Async(bool isOnline)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(MqttTopic.IsOnline(_hwItem.Id))
            .WithPayload(Json.Serialize(new MqttPayload_IsOnline
            {
                DeviceId = _hwItem.Id,
                IsOnline = isOnline,
                Ts = DateTime.UtcNow,
            }))
            .WithRetainFlag() // retained message - "the last known status" for new subscribers
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        return _client.PublishAsync(message);
    }
}
