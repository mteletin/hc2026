using System.Collections.Immutable;

namespace Hc2000.App.Media;

/// <summary>One tracked MediaFiles.SaveAsync attempt per token. No second writer and no invented publication-phase receipt.
/// A faulted helper stays Unknown even if a later read matches; only a completed successful task is Published.</summary>
internal sealed class DiskWritebackPersistence : IDiskWritebackPersistence
{
    private readonly object gate = new();
    private readonly Dictionary<string, Attempt> attempts = new(StringComparer.Ordinal);

    public async Task<WritebackTargetInfo> InspectAsync(string absolutePath)
    {
        var fact = LinuxFileFacts.Inspect(absolutePath);
        if (!fact.Known || !fact.Regular || fact.Link)
            return new(fact.Regular, fact.Link, false, default, null);
        var bytes = await MediaFiles.ReadAsync(absolutePath, MediaFiles.MaxDiskBytes);
        return new(true, false, fact.Writable, bytes, fact.FileId);
    }

    public Task<WritebackPublishResult> PublishAsync(string absolutePath, ImmutableArray<byte> bytes, PersistToken token)
    {
        var attempt = new Attempt(absolutePath, token);
        lock (gate) attempts[token.AttemptId] = attempt;
        var save = MediaFiles.SaveAsync(absolutePath, bytes);
        var observation = Observe(save, bytes);
        attempt.Observation = observation;
        return observation;
    }

    public Task<WritebackPublishResult> ReconcileAsync(string absolutePath, PersistToken token)
    {
        Attempt? attempt;
        lock (gate) attempts.TryGetValue(token.AttemptId, out attempt);
        if (attempt is null || attempt.Token != token || !string.Equals(attempt.Path, absolutePath, StringComparison.Ordinal))
            return Task.FromResult(new WritebackPublishResult(WritebackPublication.Unknown, false, Error: "No tracked helper attempt."));
        if (attempt.Observation is null)
            return Task.FromResult(new WritebackPublishResult(WritebackPublication.Unknown, false, Error: "Helper has not started."));
        return attempt.Observation;
    }

    private static async Task<WritebackPublishResult> Observe(Task save, ImmutableArray<byte> bytes)
    {
        try
        {
            await save;
            return new(WritebackPublication.Published, true, DiskWritebackCoordinator.Hash(bytes));
        }
        catch (Exception ex)
        {
            // The helper has returned, so it cannot start another rename, but a fault does not say whether Move happened.
            return new(WritebackPublication.Unknown, save.IsCompleted, Error: ex.GetBaseException().Message);
        }
    }

    private sealed class Attempt(string path, PersistToken token)
    {
        public string Path { get; } = path;
        public PersistToken Token { get; } = token;
        public Task<WritebackPublishResult>? Observation { get; set; }
    }
}
