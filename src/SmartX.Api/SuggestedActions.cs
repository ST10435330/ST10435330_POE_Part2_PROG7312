namespace SmartX;

public sealed partial class RealtimeEngine
{
    public Insight[] Insights()
    {
        lock (sync)
        {
            RefreshDevices();
            var result = new List<Insight>();
            foreach (var reading in devices.Values)
            {
                var id = reading.Sensor.Id;
                var status = DeviceStatus(reading);
                if (status == "Disconnected")
                {
                    result.Add(new($"disconnect:{id}", id, reading.Sensor.Identifier, "Check device power and connectivity before sending an override.",
                        $"No packet for more than {3 * reading.Sensor.ExpectedIntervalSeconds} seconds.", null, null, 0, null));
                    continue;
                }
                var trend = DecliningMoisture(reading);
                if (trend is not null) result.Add(trend);
                if (reading.Sensor.Category == "Environmental" && reading.NumericValue < 20)
                {
                    var learned = patterns.Where(p => p.Key.Source == id).OrderByDescending(p => p.Value).FirstOrDefault();
                    var total = patterns.Where(p => p.Key.Source == id).Sum(p => p.Value);
                    if (learned.Value >= 3 && (double)learned.Value / total >= 0.6)
                    {
                        var target = devices[learned.Key.Target];
                        if (DeviceStatus(target) != "Disconnected" && overrides.GetValueOrDefault(target.Sensor.Id, target.BooleanValue ?? false) != learned.Key.State)
                            result.Add(new($"pattern:{id}", id, reading.Sensor.Identifier, $"Consider turning {target.Sensor.Identifier} {(learned.Key.State ? "ON" : "OFF")}.",
                                $"After low-moisture searches, this command occurred {learned.Value} of {total} times within 2 minutes. This is association, not proof of cause.",
                                target.Sensor.Id, learned.Key.State, learned.Value, (double)learned.Value / total));
                    }
                    else if (pumpLinks.TryGetValue(id, out var pumpId))
                    {
                        var pump = devices[pumpId];
                        if (DeviceStatus(pump) != "Disconnected" && !overrides.GetValueOrDefault(pumpId, pump.BooleanValue ?? false))
                            result.Add(new($"linked:{id}", id, reading.Sensor.Identifier, $"Consider switching linked pump {pump.Sensor.Identifier} ON.",
                                $"Moisture is {reading.Value}, below 20%. Based on your configured link; inspect irrigation conditions first.", pumpId, true, 0, null));
                    }
                    else result.Add(new($"moisture:{id}", id, reading.Sensor.Identifier, "Inspect irrigation and link the correct pump if appropriate.",
                        $"Moisture is {reading.Value}, below the 20% threshold. No pump relationship has been configured or learned.", null, null, 0, null));
                }
                else if (status is "Critical" or "Warning")
                    result.Add(new($"range:{id}", id, reading.Sensor.Identifier, "Inspect load, calibration and device wiring.",
                        $"Latest reading {reading.Value} is outside the operating threshold.", null, null, 0, null));
            }
            return result.OrderBy(i => i.Key.StartsWith("disconnect:", StringComparison.Ordinal) ? 1 : 0).Take(50).ToArray();
        }
    }
}
