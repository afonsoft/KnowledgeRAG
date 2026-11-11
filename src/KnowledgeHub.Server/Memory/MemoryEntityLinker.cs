using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Graph;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-003: attaches extracted entities to
/// memory units and derives unit↔unit links. Entity identity reuses
/// <see cref="EntityResolver"/> normalization — memory entities live in the
/// shared <see cref="KgNode"/> store so the graph recall arm traverses the
/// same ontology as document knowledge.
/// </summary>
public sealed class MemoryEntityLinker(KnowledgeHubDbContext db)
{
    /// <summary>
    /// Resolve extracted entities to KgNodes (create-if-missing by
    /// (NormalizedName, Type)) and link them to the unit.
    /// </summary>
    public async Task<IReadOnlyList<KgNode>> LinkEntitiesAsync(
        MemoryUnit unit, Guid bankId, IReadOnlyList<ExtractedEntity> entities, CancellationToken ct)
    {
        if (entities.Count == 0)
            return [];

        var nodes = new List<KgNode>();
        foreach (var entity in entities.Where(e => !string.IsNullOrWhiteSpace(e.Name)).Take(20))
        {
            var normalized = EntityResolver.Normalize(entity.Name);
            var type = EntityResolver.NormalizeType(entity.Type);
            var node = await db.KgNodes
                .FirstOrDefaultAsync(n => n.NormalizedName == normalized && n.Type == type, ct);
            if (node is null)
            {
                node = new KgNode
                {
                    Name = entity.Name.Trim(),
                    NormalizedName = normalized,
                    Type = type
                };
                db.KgNodes.Add(node);
            }
            else
            {
                node.ObservedAt = DateTime.UtcNow;
            }
            db.MemoryUnitEntities.Add(new MemoryUnitEntity
            {
                UnitId = unit.Id,
                EntityId = node.Id,
                BankId = bankId
            });
            nodes.Add(node);
        }
        return nodes;
    }

    /// <summary>
    /// Temporal links: units whose EventDate falls within
    /// <paramref name="windowDays"/> of the new unit's event date get a
    /// weighted link (weight decays with distance).
    /// </summary>
    public async Task LinkTemporalNeighborsAsync(
        MemoryUnit unit, int windowDays, CancellationToken ct)
    {
        if (unit.EventDate is not { } when)
            return;
        var from = when.AddDays(-windowDays);
        var to = when.AddDays(windowDays);
        var neighbors = await db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == unit.BankId && u.Id != unit.Id && !u.IsDeleted
                && u.EventDate != null && u.EventDate >= from && u.EventDate <= to)
            .OrderBy(u => u.EventDate)
            .Take(10)
            .Select(u => new { u.Id, u.EventDate })
            .ToListAsync(ct);
        foreach (var n in neighbors)
        {
            var days = Math.Abs((n.EventDate!.Value - when).TotalDays);
            var weight = 1.0 - (days / (windowDays + 1.0));
            db.MemoryLinks.Add(new MemoryLink
            {
                FromUnitId = unit.Id,
                ToUnitId = n.Id,
                LinkType = "temporal",
                Weight = Math.Round(weight, 4)
            });
        }
    }
}
