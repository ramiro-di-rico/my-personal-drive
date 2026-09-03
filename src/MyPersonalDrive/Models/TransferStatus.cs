namespace MyPersonalDrive.Models;

/// <summary>
/// A transfer queue item's lifecycle state. Plain status, deliberately — there is no byte-level
/// progress anywhere in the CLI wrapper to build a percentage/ETA/speed from (see
/// docs/INTERFACE_IMPROVEMENT_PLAN.md Task 5 and AGENTS.md's "Never invent CLI output shapes").
/// </summary>
public enum TransferStatus
{
    Queued,
    Transferring,
    Done,
    Failed,
    Cancelled
}
