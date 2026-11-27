using Meitou.Data.Audio;

namespace Meitou.Game.Audio;

/// <summary>
/// Meitou's recorded heavy rain (docs/audio.md "Weather"; testing only): in a storm weather it replaces the game's rain bed, a looping 16-bit PCM
/// WAV (<see cref="FilePath"/>, not shipped) played 2D on <see cref="AudioSystem.HeavyRainObject"/> as a child of the rain bed's layer container,
/// so it takes the bed's volume, its <c>Camera_Zoom</c> curves, its sends and the ambience bus (the Ambient slider, the pause ducking). This class
/// keeps the loop running while it is heard and the crossfade's position; <see cref="AmbienceDirector"/> sets the two levels.
/// </summary>
public sealed class HeavyRainRecording(WwisePlayer player)
{
    /// <summary>
    /// The rain bed's layer container in <c>SFX_Amb_Weather_Bank</c> (played by <c>Weather_Rain_Start</c>), the recording's parent node.
    /// </summary>
    const uint RainBedNode = 0x2F6A6941;

    /// <summary>
    /// The bank that holds <see cref="RainBedNode"/>: without it the recording would play on the master bus at full level, so it waits for it.
    /// </summary>
    const string WeatherBank = "SFX_Amb_Weather_Bank";

    /// <summary>
    /// The seconds the crossfade between the rain bed and the recording takes, each way.
    /// </summary>
    const float CrossfadeSeconds = 2.5f;

    /// <summary>
    /// The seconds the loop takes to stop once its share is 0 (a short tail, so no click).
    /// </summary>
    const float StopSeconds = 0.1f;

    /// <summary>
    /// The recording's samples once loaded (null: missing or unreadable); started by the first <see cref="Update"/> that wants it.
    /// </summary>
    Task<WwisePcm?>? _load;

    /// <summary>
    /// The playback of the running loop (<see cref="WwisePlayer.NewPlaying"/>); 0 when it is not playing.
    /// </summary>
    uint _playing;

    /// <summary>
    /// The crossfade's position: 0 all rain bed, 1 all recording; it moves linearly over <see cref="CrossfadeSeconds"/>.
    /// </summary>
    float _share;

    /// <summary>
    /// The recording: <c>%LOCALAPPDATA%\Meitou\audio\heavy-rain.wav</c>, a 16-bit PCM WAV that loops seamlessly (any length, rate and channels).
    /// </summary>
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "audio", "heavy-rain.wav");

    /// <summary>
    /// Whether the recording is heard (its share above 0).
    /// </summary>
    public bool Playing => _playing != 0;

    /// <summary>
    /// The rain bed's part of the crossfade (equal power: cos of the share).
    /// </summary>
    public float BedLevel => MathF.Cos(_share * MathF.PI / 2);

    /// <summary>
    /// The recording's part of the crossfade (equal power: sin of the share).
    /// </summary>
    public float RecordingLevel => MathF.Sin(_share * MathF.PI / 2);

    /// <summary>
    /// Moves the crossfade toward the recording while <paramref name="wanted"/> (a storm weather's rain) and the recording and its parent's bank are
    /// there, else back to the rain bed, by <paramref name="realSeconds"/>; starts the loop when it becomes heard and stops it when it is gone.
    /// </summary>
    public void Update(bool wanted, double realSeconds)
    {
        float target = wanted && Ready() ? 1 : 0;
        float step = (float)realSeconds / CrossfadeSeconds;
        _share = _share < target ? Math.Min(target, _share + step) : Math.Max(target, _share - step);

        if (_share > 0 && _playing == 0 && _load?.Result is { } sound) Start(sound);
        else if (_share <= 0 && _playing != 0) Stop();
    }

    /// <summary>
    /// Sets the recording's gain (<see cref="WwisePlayer.SetObjectGain"/> on its object).
    /// </summary>
    public void SetGain(float gain) => player.SetObjectGain(AudioSystem.HeavyRainObject, gain);

    /// <summary>
    /// Stops the loop at once and returns to the rain bed (back to the main menu).
    /// </summary>
    public void Silence()
    {
        _share = 0;
        if (_playing != 0) Stop();
    }

    /// <summary>
    /// Whether the recording can play: wanted at least once (which starts loading it), loaded, and the rain bed's bank loaded.
    /// </summary>
    bool Ready()
    {
        _load ??= Task.Run(Load);
        return _load.IsCompletedSuccessfully && _load.Result is not null && player.IsBankLoaded(WeatherBank);
    }

    /// <summary>
    /// Starts the loop, silent until <see cref="SetGain"/> (the object's gain is set first).
    /// </summary>
    void Start(WwisePcm sound)
    {
        _playing = player.NewPlaying();
        player.PostLoop(FilePath, sound, AudioSystem.HeavyRainObject, RainBedNode, _playing);
    }

    /// <summary>
    /// Stops the loop.
    /// </summary>
    void Stop()
    {
        player.StopPlaying(_playing, StopSeconds);
        _playing = 0;
    }

    /// <summary>
    /// Reads <see cref="FilePath"/>; null (reported once, then the rain bed plays on in storms) when it is missing or not a 16-bit PCM WAV.
    /// </summary>
    static WwisePcm? Load()
    {
        string path = FilePath;
        try
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"audio     no heavy-rain recording at {path}; storms keep the game's rain bed");
                return null;
            }

            var pcm = WwisePcm.TryRead(File.ReadAllBytes(path));
            if (pcm is { Frames: > 0 }) return pcm;

            Console.Error.WriteLine($"audio     {path} is not a 16-bit PCM WAV; storms keep the game's rain bed");
            return null;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"audio     heavy-rain recording unreadable ({e.Message}); storms keep the game's rain bed");
            return null;
        }
    }
}
