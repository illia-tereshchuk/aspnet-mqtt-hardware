using Shared;

namespace Hw;

// "is online" and "is running" are different
// interaction points are "Work" and "Obey"
public class HwItem // physical device
{
    public string Id { get; }                               // "press-01"
    public string Type { get; }                             // "press" (HwType)
    public double NominalPowerKw { get; }                   // 30
    public bool IsRunning { get; private set; } = true;
    public double SpeedFactor { get; private set; } = 1.0;  // 1.0 = full
    public string? Fault { get; private set; }              // null is "ok"
    private int _faultTicksLeft;
    public bool IsOnline => Fault != HwState.Fault_ShortCircuit;
    public double FaultProbability { get; set; } = 0.002;   // per tick (~1s)
    
    // "phase" concept is to start hardware "not simultaneously"
    // and render consumption of particular types "non-linear"
    private readonly int _phase; 
    private int _tick;

    public HwItem(string id, string type, double nominalPowerKw, int phase = 0)
    {
        Id = id;
        Type = type;
        NominalPowerKw = nominalPowerKw;
        _phase = phase;
    }

    public MqttPayload_Health Work(Random random) // emit MQTT health
    {
        _tick++;
        UpdateFault(random);

        double powerAtMoment = PowerAtMoment(random);

        return new MqttPayload_Health
        {
            DeviceId = Id,
            Type = Type,
            PowerKw = Math.Round(powerAtMoment, 2),
            TemperatureC = Math.Round(TemperatureAtMomentC(random, powerAtMoment), 1),
            State = StateAtMoment(),
            Ts = DateTime.UtcNow,
        };
    }

    public void Obey(MqttPayload_Command command) // accept MQTT command
    {
        switch (command.Command)
        {
            case MqttPayload_Command.Stop:
                IsRunning = false;
                break;

            case MqttPayload_Command.Start:
                IsRunning = true;
                SpeedFactor = 1.0;
                break;

            case MqttPayload_Command.SetSpeed:
                SpeedFactor = command.Factor;
                break;
        }
    }

    public void ForceFault(string fault, int durationTicks)
    {
        Fault = fault;
        _faultTicksLeft = durationTicks;
    }

    private void UpdateFault(Random random)
    {
        if (Fault != null) // if there is a fault, decrement its duration
        {
            if (--_faultTicksLeft <= 0)
                Fault = null; // Restore from fault
            return;
        }
        if (!IsRunning) return; // stopped hardware cannot break
        if (random.NextDouble() >= FaultProbability) return; // this time no fault
        
        // But when it is - decide, which one
        double roll = random.NextDouble();
        if (roll < 0.45)      ForceFault(HwState.Fault_Overheat, random.Next(10, 25));
        else if (roll < 0.80) ForceFault(HwState.Fault_Jam, random.Next(5, 12));
        else                  ForceFault(HwState.Fault_ShortCircuit, random.Next(10, 16));
    }

    private double PowerAtMoment(Random random)
    {
        if (!IsOnline) return 0; // circuit broken - no electricity

        if (!IsRunning) return NominalPowerKw * 0.02; // idle

        double power = NominalPowerKw * FactorAtMoment() * SpeedFactor * (0.94 + random.NextDouble() * 0.12);

        if (Fault == HwState.Fault_Overheat) power *= 0.7; // lowered speed
        if (Fault == HwState.Fault_Jam) power *= 1.7;      // jammed consumes more

        return power;
    }

    private double FactorAtMoment()
    {
        int cyclePosition = (_tick + _phase) % 30;

        return Type switch
        {
            HwType.Press => cyclePosition < 18 ? 1.0 : 0.25, // lower consumption at "relax" moments
            HwType.Compressor => cyclePosition < 20 ? 1.0 : 0.15,
            _ => 1.0, // assume all other types work linear
        };
    }

    private const double AmbientC = 22.0;        // air temperature in the workshop
    private const double MaxLoadHeatC = 45.0;    // extra heat at full load (ambient + 45 = ~67 °C)
    private const double OverheatTargetC = 98.0; // where temperature is heading during overheat
    private const double JamTargetC = 80.0;      // friction heat when jammed

    private double _bodyC = AmbientC;            // current body temperature

    private double TemperatureAtMomentC(Random random, double power)
    {
        double targetC;
        if (Fault == HwState.Fault_Overheat) targetC = OverheatTargetC;
        else if (Fault == HwState.Fault_Jam) targetC = JamTargetC;
        else if (!IsRunning || !IsOnline) targetC = AmbientC; // cools down to ambient
        else targetC = AmbientC + (power / NominalPowerKw) * MaxLoadHeatC;

        // inertia: body moves 25% of the gap per second, not instantly.
        _bodyC += (targetC - _bodyC) * 0.25;

        if (!IsOnline) return 0; // sensor has no power - no reading (body still cools above)

        return _bodyC + Noise(random, 0.8);
    }

    private string StateAtMoment()
    {
        if (!IsOnline) return HwState.Fault_ShortCircuit;
        if (Fault != null) return Fault;
        if (!IsRunning) return HwState.Stopped;
        if (SpeedFactor < 1.0) return HwState.Throttled;
        return HwState.Normal;
    }

    private static double Noise(Random random, double amplitude)
        => (random.NextDouble() * 2 - 1) * amplitude;
}
