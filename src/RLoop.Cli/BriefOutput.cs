using RLoop.Core;

namespace RLoop.Cli;

/// <summary>Presentation only: never reduces validation, observation or mutation scope.</summary>
public static class BriefOutput
{
    public static object? Project(object? data) => data switch
    {
        UixAuditReport audit => new
        {
            audit.RootId, audit.Valid, audit.StructuralOnly, audit.Verification, audit.Truncated,
            audit.ObservedSlots, issueCount = audit.Issues.Count, issues = audit.Issues
        },
        ApplyValidationResult validation => new
        {
            validation.Valid, validation.Strict, validation.Slots, validation.Components, validation.References,
            issueCount = validation.Issues.Count, issues = validation.Issues
        },
        ApplyTestReport tests => new
        {
            tests.Passed, tests.StructuralOnly, tests.Verification, tests.Total, tests.PassedCount,
            tests = tests.Tests.Where(test => !test.Passed || test.StructuralOnly).ToArray()
        },
        _ => data
    };

    public static object Plan(ApplyPlanResult plan, IReadOnlyList<ApplyPlanEntry> displayed) => new
    {
        plan.Valid, plan.OwnershipKey, plan.StateFile, plan.Creates, plan.Updates, plan.Renames,
        plan.Deletes, plan.NoOps, plan.Atomic, plan.Recovery, plan.Warnings,
        // One list, no duplicated changes/diffs. Keep exact targets and reasons reviewable.
        operations = displayed.Select(entry => new
        {
            entry.Action, entry.Kind, entry.Key, entry.Path, entry.Reason, entry.Members
        }).ToArray()
    };
}
