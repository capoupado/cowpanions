using System.Media;

namespace Cowpanion.App.Audio;

/// <summary>
/// Optional moo. Muted by default (mooEnabled=false). Plays assets/audio/moo.wav if it exists; otherwise a no-op.
/// TODO(owner): drop a short, quiet moo.wav at assets/audio/moo.wav (ship it via the csproj like the sprites) —
/// no audio file is bundled yet, so with mooEnabled=true this currently plays nothing.
/// </summary>
internal sealed class MooPlayer
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(45);

    private readonly SoundPlayer? _player;
    private DateTime _lastPlayed = DateTime.MinValue;

    public MooPlayer(string baseDirectory)
    {
        string path = Path.Combine(baseDirectory, "assets", "audio", "moo.wav");
        if (File.Exists(path))
        {
            _player = new SoundPlayer(path);
            try
            {
                _player.Load();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                _player = null;
            }
        }
    }

    public bool HasAudio => _player is not null;

    /// <summary>Plays at most once per cooldown. Never throws.</summary>
    public void TryPlay()
    {
        if (_player is null)
        {
            return;
        }
        var now = DateTime.UtcNow;
        if (now - _lastPlayed < Cooldown)
        {
            return;
        }
        _lastPlayed = now;
        try
        {
            _player.Play();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
        }
    }
}
