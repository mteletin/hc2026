namespace Hc2000.App.Explorer;

/// <summary>One scanned disk-home row. Path is the scanned file and is never a write target.</summary>
internal sealed record ExplorerCopyRow(string Name, string Path, string Kind, long Size, string Detail)
{
    public override string ToString() => Name + "    " + Kind + "    " + Size + " bytes    " + Detail;
}
