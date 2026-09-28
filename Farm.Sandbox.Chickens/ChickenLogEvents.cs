namespace Farm.Sandbox.Chickens;

internal static class ChickenLogEvents
{
    internal static readonly EventId Startup = new(1000, "startup_configuration_summary");
    internal static readonly EventId CycleStarted = new(1001, "cycle_started");
    internal static readonly EventId CycleCompleted = new(1002, "cycle_completed");
    internal static readonly EventId CycleFailed = new(1003, "cycle_failed");
    internal static readonly EventId CandidateDiscovered = new(1010, "candidate_discovered");
    internal static readonly EventId CandidateDeferred = new(1011, "candidate_deferred");
    internal static readonly EventId CandidateFailed = new(1012, "candidate_failed");
    internal static readonly EventId CandidateSkipped = new(1013, "candidate_skipped");
    internal static readonly EventId FeedbackChangedWorkItems = new(1014, "feedback_changed_work_items");
    internal static readonly EventId AttemptBaselineAdopted = new(1015, "attempt_baseline_adopted");
    internal static readonly EventId LeaseAcquired = new(1020, "lease_acquired");
    internal static readonly EventId LeaseReleased = new(1021, "lease_released");
    internal static readonly EventId LeaseRenewed = new(1022, "lease_renewed");
    internal static readonly EventId ContextFetched = new(1030, "context_fetched");
    internal static readonly EventId ContextWorkItemSkipped = new(1031, "context_work_item_skipped");
    internal static readonly EventId RebaseCompleted = new(1040, "rebase_completed");
    internal static readonly EventId RebaseConflict = new(1041, "rebase_conflict");
    internal static readonly EventId CopilotStarted = new(1050, "copilot_started");
    internal static readonly EventId CopilotCompleted = new(1051, "copilot_completed");
    internal static readonly EventId ValidationCompleted = new(1060, "validation_completed");
    internal static readonly EventId PushCompleted = new(1070, "push_completed");
    internal static readonly EventId PushRejected = new(1071, "push_rejected");
}