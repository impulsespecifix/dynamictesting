using Meitou.Data.World;

namespace Meitou.Game.Audio;

/// <summary>
/// What the ambience follows each frame (docs/audio.md "What the game sends").
/// </summary>
public readonly record struct AmbienceInput
{
    /// <summary>
    /// The ambience of the region under the camera's pivot (BIOME_GROUP <c>ambience</c>); null when there is none.
    /// </summary>
    public Ambience? Biome { get; init; }
    /// <summary>
    /// Game hour 0..24.
    /// </summary>
    public double Hour { get; init; }
    /// <summary>
    /// GLOBAL CONSTANTS <c>sunrise</c> / <c>sunset</c> (5 and 23 in the base game).
    /// </summary>
    public float Sunrise { get; init; }
    /// <summary>
    /// GLOBAL CONSTANTS <c>sunset</c> (23 in the base game): the hour day ends at.
    /// </summary>
    public float Sunset { get; init; }
    /// <summary>
    /// The camera eye's height above the ground under it.
    /// </summary>
    public float EyeAboveGround { get; init; }
    /// <summary>
    /// The weather's rain level 0..100 (<c>rain intensity</c> × strength).
    /// </summary>
    public float Rain { get; init; }
    /// <summary>
    /// The weather's <c>wind intensity</c> × wind speed × 0.01.
    /// </summary>
    public float Wind { get; init; }
    /// <summary>
    /// Real seconds since the last update.
    /// </summary>
    public double RealSeconds { get; init; }
    /// <summary>
    /// 1 in a storm weather (<c>rain intensity</c> at least <see cref="VolumetricClouds.StormWeatherRain"/>: <c>Heavy_Rain</c>, <c>misty rain</c>),
    /// else 0: Meitou's recorded heavy rain replaces the rain bed in it (<see cref="HeavyRainRecording"/>).
    /// </summary>
    public float Storm { get; init; }
    /// <summary>
    /// Meitou's rain under the cloud: the share of the rain a gap in the clouds over the camera holds back (0..1; 0, the default: all of it falls),
    /// which turns the rain's sound down with the particles (<c>CloudRain</c>, docs/render-clouds.md "Rain under the cloud").
    /// </summary>
    public float RainWithheld { get; init; }
}

/// <summary>
/// The game's ambience rules (docs/audio.md, from the exe's camera update and audio manager): the biome of the region under the pivot, day or
/// night by the hour, the <c>Camera_Zoom</c> and <c>Time_in_Game</c> game parameters, the weather's rain and wind beds, and the unloading of the
/// banks of biomes left behind.
/// </summary>
public sealed class AmbienceDirector(WwisePlayer player)
{
    /// <summary>
    /// Real seconds after a biome's bank was last the current one that it is unloaded (<c>DAT_141689854</c>, 60).
    /// </summary>
    const double UnloadAfter = 60;

    /// <summary>
    /// The biomes whose bank was asked for, with the clock (<see cref="_clock"/>) at which each was last the current one.
    /// </summary>
    readonly Dictionary<Ambience, double> _lastCurrent = [];
    /// <summary>
    /// The load of the current biome's bank, asked for on entering it; a failed one is no longer pending (the exe clears its bit either way).
    /// </summary>
    Task? _load;
    /// <summary>
    /// The state the game compares each frame: the biome index, with bit 8 set at night; -1 before the first.
    /// </summary>
    int _state = -1;
    /// <summary>
    /// Whether the last update was day (between sunrise and sunset); null before the first, so the first sets <c>Time_in_Game</c>.
    /// </summary>
    bool? _day;
    /// <summary>
    /// Whether the rain bed and the wind bed are playing (the weather's rain and wind above 0 at the last update).
    /// </summary>
    bool _raining, _windy;
    /// <summary>
    /// Real seconds summed over the updates: the <c>Time_in_Game</c> game parameter is this as a 0..10 sawtooth over 10 minutes.
    /// </summary>
    double _clock;

    /// <summary>
    /// Meitou's rain level (the banks ignore <c>Rain_Intensity</c>, so the game's bed is as loud in a drizzle as in a storm): the gain at the
    /// least rain and the rain amount (rain / 50) at which it is full.
    /// </summary>
    const float RainQuietGain = 0.35f, RainFullAmount = 0.6f;

    /// <summary>
    /// The seconds the rain level takes to follow (the time constant of its smoothing).
    /// </summary>
    const float RainGainSeconds = 2;

    /// <summary>
    /// The rain level now (-1: none yet, the first update snaps).
    /// </summary>
    float _rainGain = -1;

    /// <summary>
    /// The recorded heavy rain that replaces the rain bed in storm weathers.
    /// </summary>
    readonly HeavyRainRecording _heavyRain = new(player);

    /// <summary>
    /// The ambience last entered (the low byte of the state); null before the first update or after <see cref="Silence"/>.
    /// </summary>
    public Ambience? Biome => _state < 0 ? null : (Ambience)(_state & 0xFF);
    /// <summary>
    /// Whether the last entered state is the night one (bit 8 of the state).
    /// </summary>
    public bool Night => (_state & 0x100) != 0;
    /// <summary>
    /// Whether the current biome's bank was asked for and has not loaded yet (the exe's pending-load bits, which hold back every unload).
    /// </summary>
    public bool LoadPending =>
        _state >= 0 && _load is { IsFaulted: false, IsCanceled: false } && !player.IsBankLoaded(BankOf((Ambience)(_state & 0xFF)));
    /// <summary>
    /// Whether the rain bed is playing.
    /// </summary>
    public bool Raining => _raining;
    /// <summary>
    /// Whether the recorded heavy rain is heard (a storm weather, or the crossfade back from one).
    /// </summary>
    public bool RecordedRain => _heavyRain.Playing;
    /// <summary>
    /// Whether the wind bed is playing.
    /// </summary>
    public bool Windy => _windy;

    /// <summary>
    /// Fix (default): a biome entered before its bank is loaded gets the right <c>_Day</c> / <c>_Night</c> event. Off: the game's bug, the opposite
    /// suffix on that path.
    /// </summary>
    public bool FixDelayedSuffix { get; set; } = true;
    /// <summary>
    /// Fix (default): the wind bed ends with the bank's <c>Weather_Wind_End</c>. Off: the game's <c>Weather_Wind_Stop</c>, which no bank has, so the
    /// wind never stops.
    /// </summary>
    public bool FixWindStop { get; set; } = true;

    /// <summary>
    /// The banks the game loads at start (with Init, the music and the voices, which the viewer does not play; the voices are not played yet).
    /// </summary>
    public void Start()
    {
        player.LoadBank("SFX_Amb_Weather_Bank");
        player.LoadBank("SFX_Amb_Indoors_Bank");
        player.LoadBank("SFX_General_Bank");
    }

    /// <summary>
    /// Ends the beds and returns to the state before the first update, so the next one enters its biome afresh (the game never does this; Meitou
    /// does when the player returns to the main menu).
    /// </summary>
    public void Silence()
    {
        if (_state >= 0) player.Post($"Leave_{(Ambience)(_state & 0xFF)}", AudioSystem.MusicObject);
        if (_raining) player.Post("Weather_Rain_End", AudioSystem.RainObject);
        if (_windy) player.Post("Weather_Wind_End", AudioSystem.MusicObject);
        _state = -1;
        _day = null;
        _raining = _windy = false;
        _rainGain = -1;
        _heavyRain.Silence();
    }

    /// <summary>
    /// One frame's ambience: flips <c>Time_in_Game</c> between day and night, sets the <c>Time_in_Game</c> and <c>Camera_Zoom</c> parameters, posts
    /// the Leave and Enter events when the biome or the day/night half changes (loading the biome's bank), starts and ends the rain and wind beds and
    /// sets their intensities.
    /// </summary>
    public void Update(in AmbienceInput input)
    {
        // Day strictly between sunrise and sunset; Time_in_Game (the state group) changes only when that flips.
        bool isDay = input.Hour > input.Sunrise && input.Hour < input.Sunset;
        if (_day != isDay)
        {
            _day = isDay;
            player.SetState("Time_in_Game", isDay ? "Day" : "Night");
        }

        // The game parameter Time_in_Game is not the clock: a 0..10 sawtooth over 10 real minutes.
        _clock += input.RealSeconds;
        player.SetParameter("Time_in_Game", (float)(_clock % 600 / 60));
        player.SetParameter("Camera_Zoom", input.EyeAboveGround / 2000 * 100);

        if (input.Biome is { } biome)
        {
            int next = (int)biome | (isDay ? 0 : 0x100);
            if (next != _state)
            {
                if (_state >= 0 && (_state & 0xFF) != (int)biome) player.Post($"Leave_{(Ambience)(_state & 0xFF)}", AudioSystem.MusicObject);
                _state = next;
                // The game posts Enter at once when the bank is loaded, otherwise after loading it (and then, by a bug, with the opposite
                // suffix); the player keeps the event until its bank arrives, so with the fix the suffix here is always the right one.
                bool suffixDay = FixDelayedSuffix || player.IsBankLoaded(BankOf(biome)) ? isDay : !isDay;
                _load = player.LoadBank(BankOf(biome));
                player.Post($"Enter_{biome}_{(suffixDay ? "Day" : "Night")}", AudioSystem.MusicObject);
            }
        }

        if (input.Rain > 0 != _raining)
        {
            _raining = input.Rain > 0;
            player.Post(_raining ? "Weather_Rain_Start" : "Weather_Rain_End", AudioSystem.RainObject);
        }
        UpdateRainGain(input);
        if (input.Wind > 0 != _windy)
        {
            _windy = input.Wind > 0;
            // The game posts Weather_Wind_Stop, which no bank has, so its wind never stops; the fix posts the bank's Weather_Wind_End.
            player.Post(_windy ? "Weather_Wind_Start" : FixWindStop ? "Weather_Wind_End" : "Weather_Wind_Stop", AudioSystem.MusicObject);
        }
        player.SetParameter("Rain_Intensity", input.Rain);
        player.SetParameter("Intensity", input.Wind);

        if (_state >= 0) _lastCurrent[(Ambience)(_state & 0xFF)] = _clock;
    }

    /// <summary>
    /// Meitou: the rain's level follows the rain, from <see cref="RainQuietGain"/> at the least rain to 1 at <see cref="RainFullAmount"/> (smooth),
    /// less what a gap in the clouds holds back (<see cref="AmbienceInput.RainWithheld"/>), smoothed over <see cref="RainGainSeconds"/>. In a storm weather the recorded heavy rain takes over from the rain bed (an equal-power
    /// crossfade); both keep the bank's camera-height fade on top (the recording's held at a floor, <see cref="HeavyRainRecording.SetLevel"/>).
    /// </summary>
    void UpdateRainGain(in AmbienceInput input)
    {
        float t = Math.Clamp(input.Rain / 50 / RainFullAmount, 0, 1);
        float target = float.Lerp(RainQuietGain, 1, t * t * (3 - 2 * t)) * (1 - Math.Clamp(input.RainWithheld, 0, 1));
        float follow = 1 - MathF.Exp(-(float)input.RealSeconds / RainGainSeconds);
        _rainGain = _rainGain < 0 ? target : _rainGain + (target - _rainGain) * follow;

        _heavyRain.Update(_raining && input.Storm >= 0.5f, input.RealSeconds);
        player.SetObjectGain(AudioSystem.RainObject, _rainGain * _heavyRain.BedLevel);
        _heavyRain.SetLevel(_rainGain * _heavyRain.RecordingLevel, input.EyeAboveGround / 2000 * 100);
    }

    /// <summary>
    /// The audio manager's tick (<c>FUN_140258e30</c>): a biome bank that is loaded and was last current more than 60 s ago is unloaded, which cuts
    /// what still plays from it. Nothing is unloaded while a bank load is pending, this director's or <paramref name="otherLoadPending"/> (the
    /// music's): the exe keeps one pending-load word for both.
    /// </summary>
    public void UnloadStale(bool otherLoadPending)
    {
        if (otherLoadPending || LoadPending) return;

        foreach (var (biome, last) in _lastCurrent)
        {
            if (_clock - last <= UnloadAfter || !player.IsBankLoaded(BankOf(biome))) continue;
            _lastCurrent.Remove(biome);
            player.UnloadBank(BankOf(biome));
        }
    }

    /// <summary>
    /// A biome's ambience bank, <c>SFX_Amb_&lt;Name&gt;</c>.
    /// </summary>
    static string BankOf(Ambience biome) => $"SFX_Amb_{biome}";
}