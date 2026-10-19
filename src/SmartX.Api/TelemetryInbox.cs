namespace SmartX;

public interface ITelemetryWork
{
    Guid SensorId { get; }
    DateTimeOffset Timestamp { get; }
    string ValueText { get; }
    bool Critical { get; }
    void Store(Gateway gateway);
}
public sealed record FloatWork(TelemetryPacket<float> Packet) : ITelemetryWork
{
    public Guid SensorId => Packet.SensorId;
    public DateTimeOffset Timestamp => Packet.Timestamp;
    public string ValueText => Packet.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " %";
    public bool Critical => Packet.Value < 10 || Packet.Value > 90;
    public void Store(Gateway gateway) => gateway.StoreQueued(Packet);
}
public sealed record PowerWork(TelemetryPacket<int> Packet) : ITelemetryWork
{
    public Guid SensorId => Packet.SensorId;
    public DateTimeOffset Timestamp => Packet.Timestamp;
    public string ValueText => Packet.Value + " W";
    public bool Critical => Packet.Value >= 7500;
    public void Store(Gateway gateway) => gateway.StoreQueued(Packet);
}
public sealed record ActuatorWork(TelemetryPacket<bool> Packet) : ITelemetryWork
{
    public Guid SensorId => Packet.SensorId;
    public DateTimeOffset Timestamp => Packet.Timestamp;
    public string ValueText => Packet.Value ? "ON / OPEN" : "OFF / CLOSED";
    public bool Critical => false;
    public void Store(Gateway gateway) => gateway.StoreQueued(Packet);
}
public sealed record QueuedWork(Guid Id, long Sequence, ITelemetryWork Work);

public sealed partial class RealtimeEngine
{
    private readonly object sync = new();
    private readonly Gateway gateway;
    private readonly Queue<QueuedWork> normal = new();
    private readonly PriorityQueue<QueuedWork, (int Severity, long Sequence)> critical = new();
    private readonly Dictionary<Guid, QueueReceipt> receipts = new();
    private readonly Queue<Guid> receiptOrder = new();
    private readonly HashSet<(Guid, DateTimeOffset)> pending = new();
    private long sequence, processed, failed, admitted;
    private bool processingPaused;
    public const int QueueCapacity = 10000;
    public const int CriticalCapacity = 1000;
    public RealtimeEngine(Gateway gateway) { this.gateway = gateway; RefreshDevices(); }

    public QueueReceipt Enqueue(ITelemetryWork work)
    {
        lock (sync)
        {
            Validate(work);
            if (work.Critical ? critical.Count >= CriticalCapacity : normal.Count >= QueueCapacity)
                throw new GatewayException("This queue lane is full. Wait for processing and retry.", 429);
            if (!pending.Add((work.SensorId, work.Timestamp)))
                throw new GatewayException("This timestamp is already queued for this sensor.", 409);
            var item = new QueuedWork(Guid.NewGuid(), ++sequence, work);
            if (work.Critical) critical.Enqueue(item, (0, item.Sequence));
            else normal.Enqueue(item);
            var receipt = new QueueReceipt(item.Id, item.Sequence, work.SensorId, work.Timestamp, work.Critical, "Queued", null);
            receipts.Add(item.Id, receipt);
            admitted++;
            return receipt;
        }
    }
    private void Validate(ITelemetryWork work)
    {
        if (!devices.TryGetValue(work.SensorId, out var reading))
            devices[work.SensorId] = reading = gateway.Reading(work.SensorId);
        var sensor = reading.Sensor;
        var valid = work switch
        {
            FloatWork f => sensor.Category == "Environmental" && float.IsFinite(f.Packet.Value) && f.Packet.Value is >= 0 and <= 100,
            PowerWork p => sensor.Category == "Power Consumption" && p.Packet.Value is >= 0 and <= 1000000,
            ActuatorWork => sensor.Category == "Actuator",
            _ => false
        };
        if (timelines.TryGetValue(work.SensorId, out var stored) && stored.ContainsKey(work.Timestamp))
            throw new GatewayException("This sensor timestamp is already stored.", 409);
        if (!valid) throw new GatewayException("Packet type, category or physical range is invalid.");
        var now = DateTimeOffset.UtcNow;
        if (work.Timestamp < now.AddDays(-1) || work.Timestamp > now.AddSeconds(30))
            throw new GatewayException("Timestamp must be within the last day and at most 30 seconds ahead.");
    }
    public QueueReceipt Receipt(Guid id)
    {
        lock (sync) return receipts.TryGetValue(id, out var receipt) ? receipt : throw new GatewayException("Receipt expired or not found.", 404);
    }
    public void SetProcessingPaused(bool paused) { lock (sync) processingPaused = paused; }
    public object QueueStatus()
    {
        lock (sync) return new { normalPending = normal.Count, criticalPending = critical.Count,
            processingPaused, admitted, processed, failed, capacity = QueueCapacity + CriticalCapacity, normalCapacity = QueueCapacity, criticalCapacity = CriticalCapacity };
    }
    public int Process(int maximum = 100)
    {
        lock (sync)
        {
            var count = 0;
            // Critical work bypasses a paused normal queue, but never interrupts a packet already being stored.
            while (count < maximum && (critical.Count > 0 || (!processingPaused && normal.Count > 0)))
            {
                var item = critical.Count > 0 ? critical.Dequeue() : normal.Dequeue();
                try
                {
                    item.Work.Store(gateway);
                    devices[item.Work.SensorId] = gateway.Reading(item.Work.SensorId);
                    receipts[item.Id] = receipts[item.Id] with { Status = "Processed" };
                    RecordProcessed(item);
                    processed++;
                }
                catch (GatewayException error)
                {
                    receipts[item.Id] = receipts[item.Id] with { Status = "Rejected", Error = error.Message };
                    failed++;
                }
                finally
                {
                    pending.Remove((item.Work.SensorId, item.Work.Timestamp));
                    receiptOrder.Enqueue(item.Id);
                    while (receiptOrder.Count > 2000) receipts.Remove(receiptOrder.Dequeue());
                }
                count++;
            }
            return count;
        }
    }
}
