
namespace Hw;

public class HwFleetWorker : BackgroundService
{
    private readonly IConfiguration _config; // reads env variables from docker-compose
    private readonly ILogger<HwFleetWorker> _logger; 

    public HwFleetWorker(IConfiguration config, ILogger<HwFleetWorker> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string host = _config["MQTT_HOST"] ?? "localhost"; // env variables from docker compose
        int port = int.TryParse(_config["MQTT_PORT"], out var p) ? p : 1883;
        int count = int.TryParse(_config["HW_COUNT"], out var c) ? c : 20; 
        double faultProbability = double.TryParse(_config["FAULT_PROBABILITY"], out var f) ? f : 0.002;

        var fleet = HwFleetFactory.CreateFleet(count, faultProbability);

        _logger.LogInformation("Starting {Count} devices → mqtt://{Host}:{Port}", fleet.Count, host, port);

        var tasks = new List<Task>();
        for (int i = 0; i < fleet.Count; i++)
        {
            var chip = new HwMqttChip(fleet[i], host, port, new Random(i * 1000 + 7), _logger);

            tasks.Add(chip.RunAsync(stoppingToken)); // ATTENTION: no "await" here, just run

            await Task.Delay(50, stoppingToken); // little delay to not "run all at once"
        }

        await Task.WhenAll(tasks); // will stay here until global cancel, so worker is alive all the cycle
    }
}
