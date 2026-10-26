using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Model;
using Phanes.Task.Core.Options;

namespace Phanes.Task.Core.Watermark;

using System.Threading.Tasks;

public sealed class WatermarkManager : IWatermarkManager
{
    private enum Kind : byte { InFlight, Completion }

    private sealed class PartitionState
    {
        public long Committed;
        public readonly PriorityQueue<long, long> Completed = new();
        public readonly HashSet<long> InFlight = new();
        public DateTime? StuckSince;
        public bool StuckReported;
    }

    private readonly ConcurrentQueue<(Kind Kind, int Partition, long Offset)> _inbox = new();
    private readonly Dictionary<int, PartitionState> _partitions = new();
    private readonly TimeSpan _stuckAfter;
    private readonly ILogger<WatermarkManager> _logger;
    private IReadOnlyList<WatermarkStatus> _snapshot = Array.Empty<WatermarkStatus>();

    public WatermarkManager(IOptions<TaskOptions> options, ILogger<WatermarkManager> logger)
    {
        _stuckAfter = TimeSpan.FromSeconds(options.Value.Worker.WatermarkStuckSeconds);
        _logger = logger;
    }

    public IReadOnlyList<WatermarkStatus> Snapshot => Volatile.Read(ref _snapshot);

    public void NotifyInFlight(int partition, long offset) => _inbox.Enqueue((Kind.InFlight, partition, offset));
    public void NotifyCompletion(int partition, long offset) => _inbox.Enqueue((Kind.Completion, partition, offset));

    public void InitializePartition(int partition, long firstOffset)
    {
        if (_partitions.ContainsKey(partition))
        {
            return;
        }
        _partitions[partition] = new PartitionState { Committed = firstOffset - 1 };
        PublishSnapshot();
        _logger.LogInformation(
            "Watermark initialized. partition={Partition} committed={Committed} first_offset={FirstOffset}",
            partition, firstOffset - 1, firstOffset);
    }

    public void ForgetPartition(int partition)
    {
        if (_partitions.Remove(partition, out var state))
        {
            PublishSnapshot();
            _logger.LogInformation(
                "Watermark dropped for revoked partition. partition={Partition} committed={Committed} pending={Pending} in_flight={InFlight}",
                partition, state.Committed, state.Completed.Count, state.InFlight.Count);
        }
    }

    public IReadOnlyList<(int Partition, long NextOffset)> Advance(DateTime utcNow)
    {
        Drain();

        var moved = new List<(int, long)>();
        foreach (var (partition, state) in _partitions)
        {
            var before = state.Committed;
            while (state.Completed.TryPeek(out var min, out _))
            {
                if (min <= state.Committed)
                {
                    state.Completed.Dequeue(); // already covered (duplicate or re-init)
                }
                else if (min == state.Committed + 1)
                {
                    state.Completed.Dequeue();
                    state.Committed = min;
                }
                else
                {
                    break; // gap
                }
            }

            if (state.Committed != before)
            {
                moved.Add((partition, state.Committed + 1));
                state.StuckSince = null;
                state.StuckReported = false;
            }
            else
            {
                EvaluateStuck(partition, state, utcNow);
            }
        }

        PublishSnapshot();
        return moved;
    }

    private void Drain()
    {
        while (_inbox.TryDequeue(out var item))
        {
            if (!_partitions.TryGetValue(item.Partition, out var state))
            {
                _logger.LogWarning(
                    "Notification for unknown partition ignored. kind={Kind} partition={Partition} offset={Offset}",
                    item.Kind, item.Partition, item.Offset);
                continue;
            }
            switch (item.Kind)
            {
                case Kind.InFlight:
                    state.InFlight.Add(item.Offset);
                    break;
                case Kind.Completion:
                    state.InFlight.Remove(item.Offset);
                    if (item.Offset > state.Committed)
                    {
                        state.Completed.Enqueue(item.Offset, item.Offset);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Stuck = completions are waiting behind a gap and no live actor owns an
    /// offset inside that gap. A gap owned by an in-flight actor is just a slow
    /// task, not a stall.
    /// </summary>
    private void EvaluateStuck(int partition, PartitionState state, DateTime utcNow)
    {
        if (state.Completed.Count == 0)
        {
            state.StuckSince = null;
            state.StuckReported = false;
            return;
        }

        var gapStart = state.Committed + 1;
        var gapEnd = state.Completed.Peek() - 1;
        var ownedByLiveActor = state.InFlight.Any(o => o >= gapStart && o <= gapEnd);
        if (ownedByLiveActor)
        {
            state.StuckSince = null;
            state.StuckReported = false;
            return;
        }

        state.StuckSince ??= utcNow;
        if (!state.StuckReported && utcNow - state.StuckSince.Value >= _stuckAfter)
        {
            state.StuckReported = true;
            _logger.LogError(
                "Watermark stuck: no live actor owns the gap. partition={Partition} committed={Committed} gap=[{GapStart},{GapEnd}] pending={Pending} stuck_for={StuckFor}s",
                partition, state.Committed, gapStart, gapEnd, state.Completed.Count,
                (int)(utcNow - state.StuckSince.Value).TotalSeconds);
        }
    }

    private void PublishSnapshot()
    {
        var snapshot = _partitions
            .OrderBy(kv => kv.Key)
            .Select(kv => new WatermarkStatus(
                kv.Key, kv.Value.Committed, kv.Value.Completed.Count, kv.Value.InFlight.Count, kv.Value.StuckReported))
            .ToArray();
        Volatile.Write(ref _snapshot, snapshot);
    }
}
