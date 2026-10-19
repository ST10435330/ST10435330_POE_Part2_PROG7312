namespace SmartX;

public sealed partial class RealtimeEngine
{
    private readonly Dictionary<Guid, SortedDictionary<DateTimeOffset, StreamLog>> timelines = new();
    private readonly Queue<StreamLog> processingOrder = new();
    private long logSequence;
    private void RecordProcessed(QueuedWork item)
    {
        var work = item.Work;
        var row = new StreamLog(++logSequence, work.SensorId, devices[work.SensorId].Sensor.Identifier,
            work.Timestamp, work.ValueText, work.Critical, work.Critical ? "Priority" : "FIFO", DateTimeOffset.UtcNow,
            work is FloatWork f ? f.Packet.Value : work is PowerWork p ? p.Packet.Value : null);
        if (!timelines.TryGetValue(work.SensorId, out var timeline)) timelines[work.SensorId] = timeline = new();
        timeline.Add(work.Timestamp, row);
        while (timeline.Count > 2048) timeline.Remove(timeline.First().Key);
        processingOrder.Enqueue(row);
        while (processingOrder.Count > 300) processingOrder.Dequeue();
    }
    public object History(Guid id, DateTimeOffset? from, DateTimeOffset? to, int take = 120)
    {
        lock (sync)
        {
            gateway.Get(id);
            if (from > to) throw new GatewayException("Start time must be before end time.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var rows = timelines.TryGetValue(id, out var timeline)
                ? timeline.Where(p => (!from.HasValue || p.Key >= from) && (!to.HasValue || p.Key <= to))
                    .TakeLast(Math.Clamp(take, 1, 500)).Select(p => p.Value).ToArray() : [];
            return new { records = rows, stored = timeline?.Count ?? 0,
                algorithm = "SortedDictionary timestamp order; insertion O(log n), range enumeration O(n).",
                searchMilliseconds = watch.Elapsed.TotalMilliseconds };
        }
    }
    public StreamLog[] RecentProcessing(long after = 0)
    {
        lock (sync) return processingOrder.Where(row => row.Sequence > after).TakeLast(100).ToArray();
    }
}
