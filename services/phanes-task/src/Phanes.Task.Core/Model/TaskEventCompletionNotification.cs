namespace Phanes.Task.Core.Model;

/// <summary>
/// Sent by an actor to the WatermarkManager when it finishes processing a TaskEvent.
/// Carries the Kafka partition and offset so the watermark can advance the commit cursor.
/// </summary>
public record TaskEventCompletionNotification(int Partition, long Offset);
