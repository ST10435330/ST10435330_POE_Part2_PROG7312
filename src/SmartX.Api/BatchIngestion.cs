using System.Text.Json.Serialization;
namespace SmartX;

public sealed record TelemetryBatch<T>([property: JsonRequired] TelemetryPacket<T>[][] Batches) where T : struct;
public static class BatchIngestion
{
    public static object Receive<T>(TelemetryBatch<T> request, RealtimeEngine engine, Func<TelemetryPacket<T>, ITelemetryWork> wrap) where T : struct
    {
        var batches = request.Batches;
        if (batches is null || batches.Length is < 1 or > 1000 ||
            batches.Any(b => b is null || b.Length == 0 || b.Any(p => p is null)) || batches.Sum(b => b.Length) > 1000)
            throw new GatewayException("Supply non-empty jagged batches totalling 1–1,000 packets.");
        var receipts = new List<QueueReceipt>();
        var errors = new List<object>();
        for (var b = 0; b < batches.Length; b++)
            for (var i = 0; i < batches[b].Length; i++)
            {
                try { receipts.Add(engine.Enqueue(wrap(batches[b][i]))); }
                catch (GatewayException error) { errors.Add(new { batch = b, index = i, error = error.Message }); }
            }
        return new { received = batches.Sum(b => b.Length), queued = receipts.Count, rejected = errors.Count, receipts, errors };
    }
}
