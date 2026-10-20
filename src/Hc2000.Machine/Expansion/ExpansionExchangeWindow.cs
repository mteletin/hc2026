namespace Hc2000.Machine.Expansion;

public sealed partial class ExpansionArena
{
    // The main latch is the mapping, not an overlay. Zero owner (not bank zero) means no flip.
    private ushort exchangeOwner, exchangePreviousBank;
    internal bool ExchangeLive => exchangeOwner != 0;
    internal bool OwnsExchange(ushort id) => exchangeOwner == id;
    internal ushort PreviousExchangeBank(ushort id) => OwnsExchange(id) ? exchangePreviousBank : (ushort)0;

    internal bool TryInstallExchange(ushort id)
    {
        VerifyOwner();
        if (exchangeOwner != 0) return exchangeOwner == id; // Repeat read never resamples the latch.
        var page = GetGrant(id).Pages.Page3;
        var previous = mainBank;
        SetHostBank(page);
        exchangePreviousBank = previous; exchangeOwner = id;
        return true;
    }

    internal void RevokeExchange(ushort id)
    {
        VerifyOwner();
        if (exchangeOwner != id) return;
        SetHostBank(exchangePreviousBank);
        exchangeOwner = exchangePreviousBank = 0;
    }

    private void SetHostBank(ushort bank)
    {
        // Both installation and restoration take the same host latch/page-table path.
        setHostBank(bank); mainBank = bank;
    }
}
