namespace Ui;
// DTOs to send as JSON via SignalR
public class FactorySnapshot
{
    public string Time { get; set; } = "";
    public double TotalKw { get; set; }
    public double LimitKw { get; set; }
    public List<HwView> HwViews { get; set; } = new();
    public List<LogEntry> Log { get; set; } = new();
}

public class HwView // single device from control panel perspective
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public double PowerKw { get; set; }
    public double TemperatureC { get; set; }
    public string State { get; set; } = "";
    public bool Online { get; set; }
}

public class LogEntry
{
    public string Time { get; set; } = "";
    public string Message { get; set; } = "";
}

public class ConsumptionHistoryPoint // for graph
{
    public string T { get; set; } = ""; // time
    public double TotalKw { get; set; }
    public bool IsOverLimit { get; set; }
}
