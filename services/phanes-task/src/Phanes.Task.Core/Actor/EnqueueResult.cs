namespace Phanes.Task.Core.Actor;

public enum EnqueueResult
{
    /// <summary>The event is in the actor's mailbox and will be processed.</summary>
    Ok,

    /// <summary>
    /// The actor has already retired (finished, idle, or faulted). The caller
    /// must obtain a fresh actor from the registry and retry — the event was
    /// NOT accepted.
    /// </summary>
    NotRunning,
}
