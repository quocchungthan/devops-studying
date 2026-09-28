namespace Farm.Sandbox.Cows;

internal static class CowLogEvents
{
    internal static readonly EventId Startup = new(2000, "startup_configuration_summary");
    internal static readonly EventId CycleStarted = new(2001, "cycle_started");
    internal static readonly EventId CycleCompleted = new(2002, "cycle_completed");
    internal static readonly EventId CycleFailed = new(2003, "cycle_failed");
    internal static readonly EventId TargetsResolved = new(2010, "targets_resolved");
    internal static readonly EventId NoteKept = new(2011, "note_kept");
    internal static readonly EventId NotePatched = new(2012, "note_patched");
    internal static readonly EventId NoteMarkedRemoved = new(2013, "note_marked_removed");
    internal static readonly EventId NoteDeferred = new(2014, "note_deferred");
    internal static readonly EventId NoteFailed = new(2015, "note_failed");
    internal static readonly EventId NoteRestored = new(2016, "note_restored");
}
