namespace Shared;

public static class MqttTopic // all hw publish, ui is subscribed
{
    public static string Command(string deviceId) => $"command/{deviceId}";     // from dashboard to device
    public const string CommandWildcard = "command/+";

    public static string Health(string deviceId) => $"health/{deviceId}"; // from device to dashboard
    public const string HealthWildcard = "health/+";

    public static string IsOnline(string deviceId) => $"isonline/{deviceId}";       // from device to all
    public const string IsOnlineWildcard = "isonline/+";
}

public static class HwType 
{
    public const string Press = "press";           
    public const string Conveyor = "conveyor";     
    public const string CncMachine = "cnc";        
    public const string Compressor = "compressor"; 
    public const string Ventilation = "vent";      
}

public static class HwState 
{
    public const string Normal = "normal";
    public const string Stopped = "stopped";            
    public const string Throttled = "throttled";
    public const string Offline = "offline"; 

    public const string Fault_Overheat = "overheat";           
    public const string Fault_Jam = "jam";                     
    public const string Fault_ShortCircuit = "short_circuit";  
            
}
public class MqttPayload_Command // command/{deviceId}, QoS 1, control made from ui to hw
{
    public const string Start = "START";
    public const string Stop = "STOP";
    public const string SetSpeed = "SET_SPEED";

    public string Command { get; set; } = ""; // one of above

    public double Factor { get; set; } = 1.0; // 0.5 for half speed
}

public class MqttPayload_Health // health/{deviceId}, data sent from hw to ui
{
    public string DeviceId { get; set; } = "";
    public string Type { get; set; } = "";
    public double PowerKw { get; set; }
    public double TemperatureC { get; set; } // degrees Celsius
    public string State { get; set; } = HwState.Normal;
    public DateTime Ts { get; set; }
}

public class MqttPayload_IsOnline // isonline/{deviceId}
{
    public string DeviceId { get; set; } = "";
    public bool IsOnline { get; set; }
    public DateTime Ts { get; set; }
}
