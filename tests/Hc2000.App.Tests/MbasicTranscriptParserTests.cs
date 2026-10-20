using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Pure synthetic parser fixtures; no machine/ROM/media/guest start.</summary>
public sealed class MbasicTranscriptParserTests
{
    // Original binary/command strings, not a copied emulator numeric oracle.
    private const string Header = "MBASIC SQR\n\nBASIC-80 Rev. 5.2\n[CP/M Version]\n" +
        "Copyright 1977, 78, 79, 80 (C) by Microsoft\nCreated: 14-Jul-80\n24902 Bytes free\n";
    private static HeadlessOracle.MbasicSampleTranscript Parse(string body) => HeadlessOracle.ParseMbasicSample(Header + body);

    [Theory]
    [InlineData("\nemax = 0\nOk\n", 0)] // line50 may suppress every exact-zero result
    [InlineData(" 5  1E-06\n\nemax = 1E-06\nOk\n", 1)]
    [InlineData(" 5 -1E-06\n 7 2E-06\n\nemax = 2E-06\nOk\n", 2)]
    public void AcceptsCompleteSyntheticConditionalRows(string body, int count)
    {
        var parsed = Parse(body);
        Assert.Empty(parsed.Errors);
        Assert.True(parsed.Complete);
        Assert.Equal(count, parsed.CandidateRows);
        Assert.Equal(count, parsed.Rows.Count);
        Assert.Equal(0, parsed.RejectedRows);
    }

    [Fact]
    public void RetainedMalformedExponentIsRejectedNotNormalizedOrSkipped()
    {
        // Exact observed malformed token retained verbatim, not repaired into a
        // positive fixture or used to change tolerance/expected numeric values.
        var parsed = Parse(" 6 -4.76837E--07\n\nemax = 4.57764E-05\nOk\n");
        Assert.Equal(1, parsed.CandidateRows);
        Assert.Empty(parsed.Rows);
        Assert.Equal(1, parsed.RejectedRows);
        Assert.Equal(parsed.CandidateRows, parsed.Rows.Count + parsed.RejectedRows);
        Assert.Contains(parsed.Errors, error => error.Contains("-4.76837E--07", StringComparison.Ordinal));
        Assert.Contains(parsed.Errors, error => error.Contains("malformed/nonfinite", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("5 1E-06\nemax = 1E-06\nOk", "PRINT blank")]
    [InlineData("5 1E-06\n5 1E-06\nemax = 1E-06\nOk", "duplicate/out-of-order")]
    [InlineData("7 1E-06\n5 1E-06\nemax = 1E-06\nOk", "duplicate/out-of-order")]
    [InlineData("101 1E-06\nemax = 1E-06\nOk", "counter outside")]
    [InlineData("5 0\nemax = 0\nOk", "nonzero bound")]
    [InlineData("5 0.0002\nemax = 0.0002\nOk", "independent")]
    [InlineData("5 NaN\nemax = 0\nOk", "malformed/nonfinite")]
    [InlineData("5 1E999\nemax = 0\nOk", "malformed/nonfinite")]
    [InlineData("5 1E-06 extra\nemax = 1E-06\nOk", "exactly counter/error")]
    [InlineData("unclassified\nemax = 0\nOk", "rejected")]
    [InlineData("emax = 0\n5 1E-06\nOk", "data after")]
    [InlineData("emax = 0\nOk\n5 1E-06", "data after")]
    [InlineData("emax = 0\nemax = 0\nOk", "unexpected summary")]
    [InlineData("emax = 1E--06\nOk", "malformed summary")]
    [InlineData("emax = -1E-06\nOk", "summary outside")]
    [InlineData("5 1E-06\nOk", "missing unique summary")]
    [InlineData("5 1E-06\nemax = 1E-06", "missing unique summary")]
    [InlineData("emax = 0\nOk\nOk", "duplicate completion")]
    [InlineData("emax = 1E-06\nOk", "maximum printed error")] // missing maximum row
    public void RejectsEveryInvalidSyntheticRowOrBoundary(string body, string reason)
    {
        var parsed = Parse(body);
        Assert.NotEmpty(parsed.Errors);
        Assert.Contains(parsed.Errors, error => error.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(parsed.CandidateRows, parsed.Rows.Count + parsed.RejectedRows);
    }

    [Fact]
    public void CountsCannotExceedIndependentlyPinnedSourceIterations()
    {
        var body = string.Join('\n', Enumerable.Range(0, HeadlessOracle.MbasicSourceIterationCount + 1)
            .Select(n => $"{n} 1E-06")) + "\nemax = 1E-06\nOk";
        var parsed = Parse(body);
        Assert.Equal(102, parsed.CandidateRows);
        Assert.Equal(101, parsed.Rows.Count);
        Assert.Equal(1, parsed.RejectedRows);
        Assert.Contains(parsed.Errors, error => error.Contains("count exceeds source101", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownBannerLineCannotBeSkippedToFindValidRows()
    {
        var parsed = HeadlessOracle.ParseMbasicSample(Header.Replace("Created: 14-Jul-80", "unclassified", StringComparison.Ordinal) + "emax = 0\nOk");
        Assert.Contains(parsed.Errors, error => error.Contains("header[4]", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingNonmaximumConditionalRowNeedsIndependentRowIdentityOracle()
    {
        // Explicit unresolved coverage gap, NOT a fixture proving the omission
        // correct: source line50 does not supply an exact nonzero row count/set.
        var full = Parse("5 1E-06\n7 2E-06\n\nemax = 2E-06\nOk");
        var missing = Parse("7 2E-06\n\nemax = 2E-06\nOk");
        Assert.Empty(full.Errors);
        Assert.Empty(missing.Errors);
        Assert.Equal(2, full.CandidateRows);
        Assert.Equal(1, missing.CandidateRows);
    }

    [Fact]
    public void DirectReadbackRequiresSourceCompletionAndFourNumericFields()
    {
        var values = HeadlessOracle.ParseMbasicDirect("?X%;P%;S;EMAX\n 101 10000 100 0\nOk\n");
        Assert.Equal(new double[] { 101, 10000, 100, 0 }, values);
    }

    [Theory]
    [InlineData("?X%;P%;S;EMAX\n100 10000 100 0\nOk")] // source NEXT must increment to101
    [InlineData("?X%;P%;S;EMAX\n101 9999 100 0\nOk")]
    [InlineData("?X%;P%;S;EMAX\n101 10000 99 0\nOk")]
    [InlineData("?X%;P%;S;EMAX\n101 10000 100 -1E-06\nOk")]
    [InlineData("?X%;P%;S;EMAX\n101 10000 100 1E--06\nOk")]
    [InlineData("?X%;P%;S;EMAX\n101 10000 100 NaN\nOk")]
    [InlineData("?X%;P%;S;EMAX\n101 10000 100 0\nextra\nOk")]
    [InlineData("?X%;P%;S;EMAX\n101 10000 100 0\n101 10000 100 0\nOk")]
    [InlineData("?X%;P%;S;EMAX\n101 10000 100\nOk")]
    [InlineData("?X%;P%;S;EMAX\n101 10000 100 0")]
    [InlineData("wrong echo\n101 10000 100 0\nOk")]
    public void DirectReadbackNeverCollectsOnlyMatchingRows(string text)
    {
        Assert.Throws<FormatException>(() => HeadlessOracle.ParseMbasicDirect(text));
    }
}
