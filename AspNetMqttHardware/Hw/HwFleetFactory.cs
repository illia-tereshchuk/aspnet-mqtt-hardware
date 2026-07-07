using Shared;

namespace Hw;

// press-01, conveyor-02...
public static class HwFleetFactory
{
    private static readonly (string Type, double NominalKw)[] Pattern = // this is called "tuple"
    {
        (HwType.Press, 30.0), 
        (HwType.Conveyor, 10.0),
        (HwType.CncMachine, 15.0),
        (HwType.Compressor, 20.0),
        (HwType.Ventilation, 5.0),
        (HwType.Press, 30.0),
        (HwType.CncMachine, 15.0),
        (HwType.Conveyor, 10.0),
        (HwType.Compressor, 20.0),
        (HwType.Ventilation, 5.0),
    };

    public static List<HwItem> CreateFleet(int count, double faultProbability)
    {
        var perTypeCounters = new Dictionary<string, int>(); // how many devices of each type
        var fleet = new List<HwItem>();

        for (int i = 0; i < count; i++)
        {
            var pair = Pattern[i % Pattern.Length]; // % is for cycling the index
            var (type, nominalKw) = pair; // deconstructuring assignment

            perTypeCounters[type] = perTypeCounters.GetValueOrDefault(type) + 1;

            string id = $"{type}-{perTypeCounters[type]:00}"; // string interpolation

            fleet.Add(new HwItem(id, type, nominalKw, phase: i * 7)
            {
                FaultProbability = faultProbability,
            });
        }

        return fleet;
    }
}
