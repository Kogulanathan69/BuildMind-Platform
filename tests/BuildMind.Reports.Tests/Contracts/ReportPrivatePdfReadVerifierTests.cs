using BuildMind.Application.Features.Reports.Services;
using BuildMind.Domain.ValueObjects;

namespace BuildMind.Reports.Tests.Contracts;

public sealed class ReportPrivatePdfReadVerifierTests
{
    private static readonly byte[] Pdf = "%PDF-1.4 private synthetic artifact"u8.ToArray();

    private static ReportArtifact Metadata(byte[] bytes) => new(
        Guid.NewGuid(), "application/pdf", bytes.Length,
        ReportArtifactIntegrity.Hash(bytes), SyntheticReportFixtures.Time);

    [Fact]
    public async Task ExactBytesReturnIndependentVerifiedCopy()
    {
        var original = Pdf.ToArray();
        using var stream = new MemoryStream(original);
        var result = await ReportPrivatePdfReadVerifier.ReadVerifiedAsync(
            stream, Metadata(original), original.Length);

        Assert.Equal(original, result);
        Assert.NotSame(original, result);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task TruncatedStreamNeverReleasesPartialBytes()
    {
        var metadata = Metadata(Pdf);
        using var stream = new MemoryStream(Pdf[..^1]);
        Assert.Null(await ReportPrivatePdfReadVerifier.ReadVerifiedAsync(stream, metadata, Pdf.Length));
    }

    [Fact]
    public async Task ExtraBytesAfterDeclaredLengthAreRejected()
    {
        var metadata = Metadata(Pdf);
        var appended = Pdf.Concat(new byte[] { 0x20 }).ToArray();
        using var stream = new MemoryStream(appended);

        Assert.Null(await ReportPrivatePdfReadVerifier.ReadVerifiedAsync(stream, metadata, appended.Length));
        Assert.Equal(Pdf.Length + 1, stream.Position);
    }

    [Fact]
    public async Task SameLengthCorruptedContentCannotPass()
    {
        var corrupted = Pdf.ToArray();
        corrupted[^1] ^= 1;
        using var stream = new MemoryStream(corrupted);

        Assert.Null(await ReportPrivatePdfReadVerifier.ReadVerifiedAsync(
            stream, Metadata(Pdf), corrupted.Length));
    }

    [Fact]
    public async Task MatchingHashWithoutPdfHeaderIsRejected()
    {
        var bytes = "not-a-pdf-artifact"u8.ToArray();
        using var stream = new MemoryStream(bytes);

        Assert.Null(await ReportPrivatePdfReadVerifier.ReadVerifiedAsync(
            stream, Metadata(bytes), bytes.Length));
    }

    [Fact]
    public async Task MetadataExceedingCapIsRejectedBeforeAnyRead()
    {
        using var stream = new MemoryStream(Pdf);
        var result = await ReportPrivatePdfReadVerifier.ReadVerifiedAsync(
            stream, Metadata(Pdf), Pdf.Length - 1);

        Assert.Null(result);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task CancelledRequestDoesNotRead()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var stream = new MemoryStream(Pdf);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReportPrivatePdfReadVerifier.ReadVerifiedAsync(
                stream, Metadata(Pdf), Pdf.Length, source.Token));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task ReadFailurePropagatesWithoutClaimingSafeEmptyArtifact()
    {
        using var stream = new FailingStream(Pdf);
        await Assert.ThrowsAsync<IOException>(() =>
            ReportPrivatePdfReadVerifier.ReadVerifiedAsync(
                stream, Metadata(Pdf), Pdf.Length));
        Assert.Equal(1, stream.Attempts);
    }

    [Fact]
    public async Task InvalidConfiguredCapIsRejected()
    {
        using var stream = new MemoryStream(Pdf);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ReportPrivatePdfReadVerifier.ReadVerifiedAsync(stream, Metadata(Pdf), 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ReportPrivatePdfReadVerifier.ReadVerifiedAsync(
                stream, Metadata(Pdf), ReportPrivatePdfReadVerifier.AbsoluteMaximumBytes + 1));
    }

    private sealed class FailingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int Attempts { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Attempts++;
            return ValueTask.FromException<int>(new IOException("synthetic private store read error"));
        }
    }
}
