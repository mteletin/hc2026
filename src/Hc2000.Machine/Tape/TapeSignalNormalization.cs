namespace Hc2000.Machine.Tape;

/// <summary>Coalesces equal levels without expanding repeated periods. Splitting a repeated
/// chunk at an edit join costs at most two stored fragments, not one object per repetition.</summary>
internal static class TapeSignalNormalization
{
    internal static IEnumerable<TapeSignalChunk> Coalesce(IEnumerable<TapeSignalChunk> chunks)
    {
        var output = new List<TapeSignalChunk>();
        foreach (var chunk in chunks)
        {
            var runs = new List<TapeRun>();
            foreach (var run in chunk.Runs)
                if (runs.Count != 0 && runs[^1].Level == run.Level)
                    runs[^1] = new(run.Level, runs[^1].Duration + run.Duration);
                else runs.Add(run);
            if (runs.Count == 1) { Append(new(1, [new(runs[0].Level, runs[0].Duration * chunk.Repeat)])); continue; }
            if (chunk.Repeat > 1 && runs[0].Level == runs[^1].Level)
            {
                Append(new(1, [runs[0]]));
                var middle = runs.Skip(1).Take(runs.Count - 2).Append(new TapeRun(runs[0].Level, runs[^1].Duration + runs[0].Duration));
                Append(new(chunk.Repeat - 1, middle));
                Append(new(1, runs.Skip(1)));
            }
            else Append(new(chunk.Repeat, runs));
        }
        return output;

        void Append(TapeSignalChunk chunk)
        {
            if (output.Count == 0 || output[^1].Runs[^1].Level != chunk.Runs[0].Level) { output.Add(chunk); return; }
            var previous = output[^1]; output.RemoveAt(output.Count - 1);
            if (previous.Repeat > 1) output.Add(new(previous.Repeat - 1, previous.Runs));
            if (previous.Runs.Length > 1) output.Add(new(1, previous.Runs.Take(previous.Runs.Length - 1)));
            output.Add(new(1, [new(previous.Runs[^1].Level, previous.Runs[^1].Duration + chunk.Runs[0].Duration)]));
            if (chunk.Runs.Length > 1) output.Add(new(1, chunk.Runs.Skip(1)));
            if (chunk.Repeat > 1) output.Add(new(chunk.Repeat - 1, chunk.Runs));
        }
    }
}
