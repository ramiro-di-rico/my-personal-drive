namespace MyPersonalDrive.Models;

public enum SyncQueueState
{
    Pending,
    Running,
    Done,
    Failed,
    Conflict,
    Skipped
}
