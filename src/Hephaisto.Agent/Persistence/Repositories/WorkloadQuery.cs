using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Persistence.Repositories;

/// <summary>
/// The one place that turns a <see cref="TargetRef"/> into a predicate over the flattened
/// target columns.
/// </summary>
/// <remarks>
/// <see cref="TargetRef.WorkloadKey"/> is a computed property with no backing field, so it
/// is not a column and cannot appear in a translated query. Matching its two branches in
/// SQL instead - owner triple when there is a controller, object triple when there is not -
/// keeps cooldowns, budgets and flap detection keyed on the same identity the rest of the
/// system uses, and lets the composite indexes in the migration serve them. Written twice
/// rather than once generically because composing an expression over an owned navigation is
/// far more code than the duplication it would remove.
/// </remarks>
internal static class WorkloadQuery
{
    public static IQueryable<Incident> ForWorkload(IQueryable<Incident> source, TargetRef target)
    {
        var ns = target.Namespace;

        if (HasOwner(target))
        {
            var ownerKind = target.OwnerKind;
            var ownerName = target.OwnerName;

            return source.Where(i =>
                i.Target.Namespace == ns
                && i.Target.OwnerKind == ownerKind
                && i.Target.OwnerName == ownerName);
        }

        var kind = target.Kind;
        var name = target.Name;

        return source.Where(i =>
            i.Target.Namespace == ns
            && i.Target.Kind == kind
            && i.Target.Name == name
            && (i.Target.OwnerKind == null || i.Target.OwnerKind == ""));
    }

    public static IQueryable<AgentAction> ForWorkload(IQueryable<AgentAction> source, TargetRef target)
    {
        var ns = target.Namespace;

        if (HasOwner(target))
        {
            var ownerKind = target.OwnerKind;
            var ownerName = target.OwnerName;

            return source.Where(a =>
                a.Target.Namespace == ns
                && a.Target.OwnerKind == ownerKind
                && a.Target.OwnerName == ownerName);
        }

        var kind = target.Kind;
        var name = target.Name;

        return source.Where(a =>
            a.Target.Namespace == ns
            && a.Target.Kind == kind
            && a.Target.Name == name
            && (a.Target.OwnerKind == null || a.Target.OwnerKind == ""));
    }

    private static bool HasOwner(TargetRef target) =>
        target.OwnerKind is { Length: > 0 } && target.OwnerName is { Length: > 0 };
}
