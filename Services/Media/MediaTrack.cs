using System;

namespace WindowsNotch.Services.Media;

/// <summary>
/// UI-independent snapshot of the active media track's metadata and raw thumbnail bytes.
/// </summary>
public sealed record MediaTrack(
    string Title,
    string Artist,
    string Album,
    byte[]? Artwork)
{
    public bool HasArtwork => Artwork is { Length: > 0 };

    public bool Equals(MediaTrack? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return string.Equals(Title, other.Title, StringComparison.Ordinal)
            && string.Equals(Artist, other.Artist, StringComparison.Ordinal)
            && string.Equals(Album, other.Album, StringComparison.Ordinal)
            && AreArtworkBytesEqual(Artwork, other.Artwork);
    }

    public override int GetHashCode()
    {
        int artworkHash = Artwork is { Length: > 0 } bytes
            ? HashCode.Combine(bytes.Length, bytes[0], bytes[bytes.Length / 2], bytes[^1])
            : 0;

        return HashCode.Combine(Title, Artist, Album, artworkHash);
    }

    private static bool AreArtworkBytesEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }
}
