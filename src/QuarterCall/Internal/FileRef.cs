using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace QuarterCall.Internal;

/// <summary>A file the manifest names: its address, and the SHA-256 digest and length it must have.</summary>
/// <param name="Uri">The file's address, beneath the host's base address.</param>
/// <param name="Sha256">The file's SHA-256 digest as the manifest writes it, 64 lowercase hex characters.</param>
/// <param name="Bytes">The file's length in bytes.</param>
internal sealed record FileRef(Uri Uri, string Sha256, long Bytes)
{
    // The manifest writes each of the digest's bytes as two hex characters.
    private const int HexLength = SHA256.HashSizeInBytes * 2;

    private static readonly SearchValues<char> LowerHexDigits = SearchValues.Create("0123456789abcdef");

    /// <summary>The file at <paramref name="uri"/>, as the manifest describes it.</summary>
    /// <param name="uri">The file's address.</param>
    /// <param name="sha256">The digest the manifest names, or null when it names none.</param>
    /// <param name="bytes">The length the manifest names, or null when it names none.</param>
    /// <exception cref="DataIntegrityException">
    /// The digest is missing or is not 64 lowercase hex characters, or the length is missing or negative: the file
    /// could not be verified, so it is not read.
    /// </exception>
    public static FileRef Create(Uri uri, string? sha256, long? bytes)
    {
        if (!IsDigest(sha256) || bytes is not { } length || length < 0)
        {
            throw new DataIntegrityException(
                $"The manifest names no usable digest for {uri}: a file's SHA-256 must be 64 lowercase hex characters, "
                + "and its length a number of bytes that is not negative. Without them the file cannot be verified, "
                + "so it is not read. The manifest may be in the middle of being republished; try again in a few "
                + "minutes. If it stays this way, check that the data address points to a QuarterCall data host.");
        }

        return new FileRef(uri, sha256, length);
    }

    /// <summary>Whether <paramref name="digest"/>, a SHA-256 digest of 32 bytes, is the manifest's.</summary>
    public bool HasDigest(ReadOnlySpan<byte> digest)
    {
        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        var status = Convert.FromHexString(Sha256, expected, out _, out var written);
        return status == OperationStatus.Done && written == expected.Length && digest.SequenceEqual(expected);
    }

    private static bool IsDigest([NotNullWhen(true)] string? sha256)
    {
        return sha256 is { Length: HexLength } && !sha256.AsSpan().ContainsAnyExcept(LowerHexDigits);
    }
}
