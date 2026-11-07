using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Media;
using MediaServer.Api.Processes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Tests;

/// <summary>Synthetic "audio" for the detector: tune-like sequences of tones (the same seed gives the same tune) and speech-like noise.</summary>
internal static class Synth
{
    public const int Rate = AudioFingerprint.SampleRate;

    /// <summary>A tune: a new chord every 0.35 s, drawn from a seeded random walk between 300 Hz and 1.8 kHz.</summary>
    public static short[] Tune(int seed, double seconds, double amplitude = 0.4)
    {
        var rng = new Random(seed);
        var pcm = new short[(int)(seconds * Rate)];
        var step = (int)(0.35 * Rate);
        for (var n = 0; n < pcm.Length; n += step)
        {
            double f1 = 300 + rng.NextDouble() * 1500, f2 = 300 + rng.NextDouble() * 1500, f3 = 300 + rng.NextDouble() * 1500;
            for (var i = n; i < Math.Min(pcm.Length, n + step); i++)
            {
                var t = (double)i / Rate;
                var v = (Math.Sin(2 * Math.PI * f1 * t) + 0.7 * Math.Sin(2 * Math.PI * f2 * t) + 0.5 * Math.Sin(2 * Math.PI * f3 * t)) / 2.2;
                pcm[i] = (short)(v * amplitude * short.MaxValue);
            }
        }
        return pcm;
    }

    public static short[] Noise(int seed, double seconds, double amplitude)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, (int)(seconds * Rate)).Select(_ => (short)((rng.NextDouble() * 2 - 1) * amplitude * short.MaxValue)).ToArray();
    }

    public static short[] Silence(double seconds) => new short[(int)(seconds * Rate)];
    public static short[] Join(params short[][] parts) => parts.SelectMany(p => p).ToArray();
    public static short[] Mix(short[] a, short[] b) => a.Select((x, i) => (short)Math.Clamp(x + (i < b.Length ? b[i] : 0), short.MinValue, short.MaxValue)).ToArray();

    public static void WriteWav(string path, short[] pcm)
    {
        using var w = new BinaryWriter(File.Create(path));
        var data = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var s in pcm) w.Write(s);
    }
}

public sealed class AudioFingerprintTests
{
    private static int Frames(double seconds) => (int)(seconds / AudioFingerprint.FrameSeconds);
    private static double Seconds(int frames) => frames * AudioFingerprint.FrameSeconds;

    [Fact] public void ASharedTune_IsFoundWhereverItSitsInEachRecording()
    {
        var a = Synth.Join(Synth.Tune(1, 20), Synth.Tune(99, 30), Synth.Tune(2, 60));              // unique, THE tune, unique
        var b = Synth.Join(Synth.Tune(3, 41.3), Synth.Tune(99, 30), Synth.Tune(4, 40));
        var m = AudioFingerprint.BestMatch(AudioFingerprint.Compute(a), AudioFingerprint.Compute(b), Frames(15))!;
        Assert.NotNull(m);
        Assert.InRange(Seconds(m.StartA), 17, 22); Assert.InRange(Seconds(m.StartB), 38, 43.3);      // the edges can be a couple of seconds early: chord changes click alike
        Assert.InRange(Seconds(m.Length), 27, 34);
    }

    [Fact] public void TheTuneIsStillFoundWithSpeechOverItAndAnOffsetThatIsNotAWholeFrame()
    {
        var tune = Synth.Tune(7, 30);
        var a = Synth.Join(Synth.Noise(1, 10, 0.1), Synth.Mix(tune, Synth.Noise(5, 30, 0.12)), Synth.Noise(2, 20, 0.1));
        var b = Synth.Join(Synth.Tune(8, 25.0173), Synth.Mix(tune, Synth.Noise(6, 30, 0.12)), Synth.Tune(9, 20));
        var m = AudioFingerprint.BestMatch(AudioFingerprint.Compute(a), AudioFingerprint.Compute(b), Frames(15));
        Assert.NotNull(m);
        Assert.InRange(Seconds(m!.StartA), 9, 12); Assert.InRange(Seconds(m.StartB), 24, 27); Assert.InRange(Seconds(m.Length), 24, 31);
    }

    [Fact] public void UnrelatedAudio_HasNoMatch()
        => Assert.Null(AudioFingerprint.BestMatch(AudioFingerprint.Compute(Synth.Tune(1, 60)), AudioFingerprint.Compute(Synth.Tune(2, 60)), Frames(15)));

    [Fact] public void Silence_NeverMatchesSilence()
        => Assert.Null(AudioFingerprint.BestMatch(AudioFingerprint.Compute(Synth.Join(Synth.Silence(30), Synth.Tune(1, 10))), AudioFingerprint.Compute(Synth.Join(Synth.Tune(2, 10), Synth.Silence(30))), Frames(15)));

    [Fact] public void ShortSharedSounds_AreNotReported()                                              // a 6 s sting is shorter than any title sequence
        => Assert.Null(AudioFingerprint.BestMatch(AudioFingerprint.Compute(Synth.Join(Synth.Tune(1, 20), Synth.Tune(50, 6))), AudioFingerprint.Compute(Synth.Join(Synth.Tune(2, 20), Synth.Tune(50, 6))), Frames(15)));

    [Fact] public void TooLittleAudio_GivesAnEmptyPrint()
    {
        Assert.Empty(AudioFingerprint.Compute(new short[100]));
        Assert.Null(AudioFingerprint.BestMatch([], [], 10));
    }

    [Fact] public void PrintsSurviveTheTripThroughTheDatabase()
    {
        var p = AudioFingerprint.Compute(Synth.Tune(3, 5));
        Assert.Equal(p, SkipDetector.ToFrames(SkipDetector.ToBytes(p)));
    }
}

public sealed class SkipDecisionTests
{
    [Fact] public void Neighbours_MustAgree_WhenThereAreSeveral()
    {
        Assert.Null(SkipDetector.Consensus([], 3));
        Assert.Null(SkipDetector.Consensus([(10, 40)], 4));                        // one of four: too thin
        Assert.Equal((11, 41), SkipDetector.Consensus([(10, 40), (11, 41)], 4)!.Value);
        Assert.Equal((10, 40), SkipDetector.Consensus([(10, 40)], 1)!.Value);       // a season of two has only one neighbour to ask
    }

    [Fact] public void TheMiddleValueWins_SoOneOddMatchDoesNotMoveTheMarker()
        => Assert.Equal((30, 91), SkipDetector.Consensus([(30, 90), (30.5, 91), (5, 200)], 3)!.Value);

    [Fact] public void TheCreditsOfAFilm_StartAfterTheLastFadeWithEnoughFilmLeft()
    {
        Assert.Equal(7195, SkipDetector.BlackToCredits([(2000, 2002), (6990, 7000), (7190, 7195)], 7300));   // the last fade that still leaves 105 s of film
        Assert.Null(SkipDetector.BlackToCredits([(7260, 7280)], 7300));                                           // 20 s left: not credits
        Assert.Null(SkipDetector.BlackToCredits([], 7300));
    }
}

public sealed class SkipDetectorEndToEndTests : IDisposable
{
    private readonly TestDb _t = new();
    public SkipDetectorEndToEndTests() => Directory.CreateDirectory(Path.Combine(_t.Dir, "lib"));
    public void Dispose() => _t.Dispose();

    private SkipDetector Detector(Dictionary<string, string?>? extra = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(_t.Config.AsEnumerable().Concat(extra ?? [])) { ["Tools:MaxConcurrency"] = "2" }).Build();
        return new SkipDetector(_t.Db, new DbMediaResolver(_t.Db, cfg), new ProcessRunner(2), cfg, TimeProvider.System, NullLogger<SkipDetector>.Instance);
    }

    /// <summary>An "episode": audio written to a WAV that is named like a video (ffmpeg goes by content, not by name).</summary>
    private long Episode(string show, int season, int ep, short[] audio)
    {
        var id = _t.Insert($"{show} S{season}E{ep}.mkv", "Episode", show, 2020);
        Synth.WriteWav(Path.Combine(_t.Dir, "lib", $"{show} S{season}E{ep}.mkv"), audio);
        using var c = _t.Db.Open();
        Database.Execute(c, null, "UPDATE media_files SET season=@s, episode=@e, duration_seconds=@d WHERE id=@i", ("@s", season), ("@e", ep), ("@d", audio.Length / (double)Synth.Rate), ("@i", id));
        return id;
    }

    private static short[] Show(int unique, double introAt, double length = 330)
        => Synth.Join(Synth.Tune(unique, introAt), Synth.Tune(500, 32), Synth.Tune(unique + 1000, length - introAt - 32 - 40), Synth.Tune(600, 40));    // …intro…story…credits tune

    [Fact] public async Task FindsTheIntroAndTheCredits_AcrossAnEpisodesOfASeason()
    {
        var a = Episode("Some Show", 1, 1, Show(1, 20)); var b = Episode("Some Show", 1, 2, Show(2, 35)); var c = Episode("Some Show", 1, 3, Show(3, 12));
        var d = Detector();
        await d.RunAsync(default);
        foreach (var (id, introAt) in new[] { (a, 20.0), (b, 35.0), (c, 12.0) })
        {
            var markers = d.MarkersFor(id);
            var intro = Assert.Single(markers, m => m.Kind == "intro");
            Assert.InRange(intro.Start, introAt - 3.5, introAt + 1.5); Assert.InRange(intro.End, introAt + 29, introAt + 35); Assert.Equal("audio", intro.Source);
            var credits = Assert.Single(markers, m => m.Kind == "credits");
            Assert.InRange(credits.Start, 285, 292); Assert.Equal(330, credits.End, 1);
        }
        var s = d.Status();
        Assert.False(s.Running); Assert.Equal(3, s.Done); Assert.Equal(3, s.Intros); Assert.Equal(3, s.Credits); Assert.Equal(3, s.Titles);
    }

    [Fact] public async Task ASecondPass_DoesNotRedoWhatIsDone_ButPicksUpANewEpisode()
    {
        Episode("Some Show", 1, 1, Show(1, 20)); Episode("Some Show", 1, 2, Show(2, 20));
        var d = Detector();
        await d.RunAsync(default);
        Assert.Equal(2, d.Status().Total);
        await d.RunAsync(default);
        Assert.Equal(0, d.Status().Total);                                                      // nothing changed
        var fresh = Episode("Some Show", 1, 3, Show(3, 25));
        await d.RunAsync(default);
        Assert.Equal(3, d.Status().Total);                                                      // the new one, plus the two that now have a third neighbour
        Assert.Contains(d.MarkersFor(fresh), m => m.Kind == "intro");
    }

    [Fact] public async Task ALoneEpisode_HasNothingToCompareWith_AndIsRecordedAsLooked()
    {
        var only = Episode("Single", 1, 1, Show(1, 20));
        var d = Detector();
        await d.RunAsync(default);
        Assert.Empty(d.MarkersFor(only));
        Assert.Equal("1", _t.Scalar("SELECT COUNT(*) FROM skip_scans"));
    }

    [Fact] public async Task TheSettings_TurnEachKindOff()
    {
        var a = Episode("Some Show", 1, 1, Show(1, 20)); Episode("Some Show", 1, 2, Show(2, 20));
        var d = Detector(new() { ["Skip:DetectCredits"] = "false" });
        await d.RunAsync(default);
        Assert.Equal(["intro"], d.MarkersFor(a).Select(m => m.Kind));
    }

    [Fact] public async Task ClearingForgetsEverything()
    {
        var a = Episode("Some Show", 1, 1, Show(1, 20)); Episode("Some Show", 1, 2, Show(2, 20));
        var d = Detector();
        await d.RunAsync(default);
        Assert.NotEmpty(d.MarkersFor(a));
        d.Clear();
        Assert.Empty(d.MarkersFor(a)); Assert.Equal("0", _t.Scalar("SELECT COUNT(*) FROM skip_scans"));
    }

    [Fact] public async Task ChaptersInTheFile_TakePrecedenceOverWhatWasHeard()
    {
        var a = Episode("Some Show", 1, 1, Show(1, 20)); Episode("Some Show", 1, 2, Show(2, 20));
        var d = Detector();
        await d.RunAsync(default);
        var chapters = new ChapterService(_t.Db, new DbMediaResolver(_t.Db, _t.Config), new ProcessRunner(1), _t.Config, TimeProvider.System, d);
        var info = (await chapters.GetAsync(a, default))!;                                       // the WAV has no chapters, so the heard ones show
        Assert.Contains(info.Markers, m => m is { Kind: "intro", Source: "audio" });
        var merged = typeof(ChapterService).GetMethod("Augment", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var withChapter = (ChapterInfo)merged.Invoke(chapters, [a, ChapterService.Build([new Chapter(5, 60, "Opening")])])!;
        Assert.Equal(new SkipMarker("intro", 5, 60), Assert.Single(withChapter.Markers, m => m.Kind == "intro"));   // the chapter's, not the heard one
        Assert.Contains(withChapter.Markers, m => m.Kind == "credits");                                           // but the credits still come from listening
    }
}
