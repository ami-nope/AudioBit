using System.Windows.Media;

namespace AudioBit.App.Models;

public sealed class SpotifyTrackModel
{
    public string TrackId { get; set; } = string.Empty;

    public string TrackName { get; set; } = string.Empty;

    public string ArtistName { get; set; } = string.Empty;

    public string AlbumName { get; set; } = string.Empty;

    public string AlbumArtUrl { get; set; } = string.Empty;

    public ImageSource? AlbumArt { get; set; }

    public int DurationMs { get; set; }

    public int ProgressMs { get; set; }

    public bool IsPlaying { get; set; }
}
