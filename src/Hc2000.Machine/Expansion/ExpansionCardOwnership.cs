using Hc2000.Machine.Io;

namespace Hc2000.Machine.Expansion;

internal enum ExpansionPortAuthority { Exclusive, MainOnly }
internal sealed record ExpansionOwnedPort(ushort Match, ushort Mask, PortAccess Access, ExpansionPortAuthority Authority);
internal sealed record ExpansionOwnedCard(ushort Id, string Kind, int Version, ExpansionOwnedPort[] Ports);

public sealed partial class ExpansionController
{
    private sealed record CardBinding(ExpansionOwnedCard Descriptor, PortMapping[] Mappings, Func<bool> IsQuiescent);
    private readonly Dictionary<ushort, CardBinding> ownedCards = new();
    private bool cardRosterSealed;

    /// <summary>Cold host composition only. The quiescence predicate must be bounded, pure and
    /// include the card's active transactions and shared-DMA reservations. Decoder construction
    /// separately checks these mappings against every other device in the machine profile.</summary>
    internal void RegisterOwnedCard(ushort id, string kind, int version, IReadOnlyList<PortMapping> ports,
        Func<bool> isQuiescent, IReadOnlyList<ExpansionPortAuthority>? authorities = null)
    {
        ArgumentNullException.ThrowIfNull(isQuiescent);
        ValidateOwnedCard(id, kind, version, ports, authorities);
        var rows = ports.ToArray();
        var descriptor = new ExpansionOwnedCard(id, kind, version,
            rows.Select((row, index) => new ExpansionOwnedPort(row.Match, row.Mask, row.Access,
                authorities?[index] ?? ExpansionPortAuthority.Exclusive)).ToArray());
        ownedCards.Add(id, new(descriptor, rows, isQuiescent));
    }

    internal bool SharesArena(ExpansionArena? candidate) => ReferenceEquals(arena, candidate);

    internal void ValidateOwnedCard(ushort id, string kind, int version, IReadOnlyList<PortMapping> ports,
        IReadOnlyList<ExpansionPortAuthority>? authorities = null)
    {
        VerifyOwner();
        ArgumentNullException.ThrowIfNull(ports);
        if (cardRosterSealed || id == 0 || id >= deviceOwners.Length || string.IsNullOrWhiteSpace(kind) || kind.Length > 64 ||
            version <= 0 || ports.Count is < 1 or > 32 || ownedCards.ContainsKey(id) ||
            authorities is not null && authorities.Count != ports.Count)
            throw new ArgumentException("Invalid X5 card registration.");
        var rows = ports.ToArray();
        var existing = mappings.Concat(ownedCards.Values.SelectMany(card => card.Mappings)).ToArray();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (row is null || row.Device is null || (row.Match & ~row.Mask) != 0 ||
                row.Access is < PortAccess.Read or > PortAccess.ReadWrite ||
                authorities is not null && !Enum.IsDefined(authorities[index]))
                throw new ArgumentException("Invalid X5 card port.", nameof(ports));
            foreach (var other in existing.Concat(rows.Take(index)))
                if ((row.Access & other.Access) != 0 && ((row.Match ^ other.Match) & row.Mask & other.Mask) == 0)
                    throw new ArgumentException("Overlapping X5 card ports.", nameof(ports));
        }
    }

    private (CardBinding Card, ExpansionPortAuthority Authority)? CardForPort(ushort port, PortAccess access)
    {
        foreach (var card in ownedCards.Values)
            for (var index = 0; index < card.Mappings.Length; index++)
            {
                var row = card.Mappings[index];
                if ((row.Access & access) != 0 && (port & row.Mask) == row.Match)
                    return (card, card.Descriptor.Ports[index].Authority);
            }
        return null;
    }

    private bool OwnedCardsQuiescent(ushort unitId) => ownedCards.Values
        .Where(card => deviceOwners[card.Descriptor.Id] == unitId)
        .All(card => card.IsQuiescent());

    private ExpansionOwnedCard[] CaptureOwnedCardRoster() => ownedCards.Values
        .Select(card => card.Descriptor with { Ports = card.Descriptor.Ports.ToArray() })
        .OrderBy(card => card.Id).ToArray();

    private void ValidateOwnedCardRoster(ExpansionX5State state)
    {
        var current = CaptureOwnedCardRoster();
        if (state.Cards is null || state.Cards.Length != current.Length) throw new ArgumentException("X5 card roster mismatch.");
        for (var index = 0; index < current.Length; index++)
        {
            var expected = current[index]; var saved = state.Cards[index];
            if (saved is null || saved.Id != expected.Id || saved.Kind != expected.Kind || saved.Version != expected.Version ||
                saved.Ports is null || !saved.Ports.SequenceEqual(expected.Ports))
                throw new ArgumentException("X5 card roster mismatch.");
        }
        for (var id = 0; id < state.DeviceOwners.Length; id++)
            if (state.DeviceOwners[id] != 0 && !ownedCards.ContainsKey((ushort)id))
                throw new ArgumentException("X5 owner for absent card.");
    }
}
