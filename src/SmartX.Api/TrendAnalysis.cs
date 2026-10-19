namespace SmartX;

public sealed partial class RealtimeEngine
{
    private Insight? DecliningMoisture(DeviceReading reading)
    {
        if (reading.Sensor.Category != "Environmental" || reading.NumericValue is null or < 20 || !timelines.TryGetValue(reading.Sensor.Id, out var history)) return null;
        var points = history.Values.Where(p => p.NumericValue.HasValue).TakeLast(6).ToArray();
        if (points.Length < 5 || (points[^1].Timestamp - points[0].Timestamp).TotalSeconds < 4) return null;
        var x = points.Select(p => (p.Timestamp - points[0].Timestamp).TotalSeconds).ToArray();
        var y = points.Select(p => p.NumericValue!.Value).ToArray();
        var mx = x.Average(); var my = y.Average();
        var denominator = x.Sum(v => (v - mx) * (v - mx));
        if (denominator <= 0) return null;
        var slope = x.Select((v, i) => (v - mx) * (y[i] - my)).Sum() / denominator;
        var projected = y[^1] + slope * 60;
        if (slope >= 0 || projected >= 20) return null;
        return new($"trend:{reading.Sensor.Id}", reading.Sensor.Id, reading.Sensor.Identifier,
            "Moisture is falling; inspect this device before it crosses 20%.",
            $"Least-squares trend across {points.Length} API readings projects {projected:0.0}% in 60 seconds if the trend continues. This is an estimate.",
            null, null, points.Length, null);
    }
}
