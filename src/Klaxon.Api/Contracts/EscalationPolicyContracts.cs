using Klaxon.Core.Entities;
using NodaTime;

namespace Klaxon.Api.Contracts;

public sealed record EscalationTargetDto(EscalationTargetKind Kind, string Reference);

public sealed record EscalationLevelDto(
    int Position,
    int TimeoutSeconds,
    IReadOnlyList<EscalationTargetDto> Targets);

public sealed record CreateEscalationPolicyRequest(
    Guid TeamId,
    string Name,
    IReadOnlyList<EscalationLevelDto> Levels);

public sealed record EscalationLevelResponse(
    Guid Id,
    int Position,
    int TimeoutSeconds,
    IReadOnlyList<EscalationTargetDto> Targets);

public sealed record EscalationPolicyResponse(
    Guid Id,
    Guid TeamId,
    string Name,
    Instant CreatedAt,
    IReadOnlyList<EscalationLevelResponse> Levels)
{
    public static EscalationPolicyResponse FromEntity(EscalationPolicy policy) => new(
        policy.Id,
        policy.TeamId,
        policy.Name,
        policy.CreatedAt,
        policy.Levels
            .OrderBy(level => level.Position)
            .Select(level => new EscalationLevelResponse(
                level.Id,
                level.Position,
                level.TimeoutSeconds,
                level.Targets
                    .Select(target => new EscalationTargetDto(target.Kind, target.Reference))
                    .ToList()))
            .ToList());
}
