namespace Phanes.Task.Core.Model;

using System.Threading.Tasks;

/// <summary>
/// A TaskEvent plus the Kafka coordinates it was consumed from. Transport
/// metadata stays out of the proto contract; the envelope carries it from the
/// Task Loop to the actor so completion can later be reported per offset
/// (WatermarkManager).
/// </summary>
public sealed record TaskEventEnvelope(TaskEvent Event, int Partition, long Offset);
