// Engine check: proves audio flows through a PatchBay cable and measures the delay.
// Uses only virtual devices, so nothing is audible:
//   test tone -> "CABLE Input"  ~~VB-Cable~~>  "CABLE Output" --[PatchBay cable]--> "Steam Streaming Speakers"
//   and listens to Steam Streaming Speakers (loopback) for the tone.
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PatchBay.Audio;

var catalog = new DeviceCatalog();
var groups = catalog.Build(includeLoopback: true);
foreach (var g in groups)
{
    Console.WriteLine($"[{g.Title}]");
    foreach (var j in g.Jacks) Console.WriteLine($"   {j.Kind,-3} {j.Label}");
}

var all = groups.SelectMany(g => g.Jacks).ToList();
JackInfo? Find(Func<JackInfo, bool> f) => all.FirstOrDefault(f);
var cableIn = Find(j => j.Kind == JackKind.In && j.GroupTitle.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase));
var cableOut = Find(j => j.Kind == JackKind.Out && !j.IsLoopback && j.Label.Equals("CABLE Output", StringComparison.OrdinalIgnoreCase));
var steamSpk = Find(j => j.Kind == JackKind.In && j.GroupTitle == "Steam Streaming Speakers");
var steamLoop = steamSpk == null ? null : Find(j => j.Id == JackInfo.LoopbackId(steamSpk.EndpointId));
if (cableIn == null || cableOut == null || steamSpk == null || steamLoop == null)
{
    Console.WriteLine("Needed devices not found (VB-Cable + Steam Streaming Speakers).");
    return 1;
}

int latency = args.Length > 0 ? int.Parse(args[0]) : 20;
bool baseline = args.Contains("baseline");
var engine = new AudioEngine { LatencyMs = latency };
var cableId = Guid.NewGuid();
if (!baseline) engine.Connect(cableId, cableOut, steamSpk, 1f, false, PatchBay.Audio.ChannelMode.Normal);
Console.WriteLine(baseline ? "\nBaseline: tone straight into Steam speakers, no PatchBay" : $"\nCable running: CABLE Output -> {steamSpk.Label} (buffer {latency} ms)");
int quietBlocks = 0, loudBlocks = 0, glitches = 0; float last = 0; bool haveLast = false;

// Listen on the Steam speakers' loopback for the tone.
var sw = Stopwatch.StartNew();
long firstHeardMs = -1;
float maxPeak = 0;
using var listen = new WasapiLoopbackCapture(steamSpk.Device!);
listen.DataAvailable += (_, e) =>
{
    var wb = new WaveBuffer(e.Buffer);
    int n = e.BytesRecorded / 4;
    float peak = 0;
    for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(wb.FloatBuffer[i]));
    maxPeak = Math.Max(maxPeak, peak);
    if (peak > 0.02f && firstHeardMs < 0) firstHeardMs = sw.ElapsedMilliseconds;
    if (firstHeardMs >= 0 && sw.ElapsedMilliseconds > firstHeardMs + 100 && sw.ElapsedMilliseconds < firstHeardMs + 1600)
    {
        if (peak < 0.1f) quietBlocks++; else loudBlocks++;
        int ch = listen.WaveFormat.Channels;
        for (int i = 0; i < n; i += ch)
        {
            float v = wb.FloatBuffer[i];
            if (haveLast && Math.Abs(v - last) > 0.03f) glitches++;
            last = v; haveLast = true;
        }
    }
};
listen.StartRecording();
Thread.Sleep(800);

// Quiet test tone into CABLE Input.
var tone = new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.2, Type = SignalGeneratorType.Sin };
using var player = new WasapiOut((baseline ? steamSpk : cableIn).Device!, AudioClientShareMode.Shared, true, 10);
player.Init(tone);
long started = 0;
player.Play();
started = sw.ElapsedMilliseconds;
Thread.Sleep(2000);
var stats = engine.Stats(cableId);
player.Stop();
Thread.Sleep(300);
listen.StopRecording();
engine.Dispose();

Console.WriteLine($"Blocks with tone: {loudBlocks}, dropped/quiet blocks: {quietBlocks}, sample glitches: {glitches}, underruns: {stats.Underruns}, skips: {stats.Skips}");
Console.WriteLine($"Max level heard at the far end: {maxPeak:0.000} (tone was 0.200)");
Console.WriteLine(firstHeardMs < 0
    ? "FAIL: tone never arrived"
    : $"PASS: tone arrived ~{firstHeardMs - started} ms after it started (includes VB-Cable + loopback measuring overhead)");
return firstHeardMs < 0 ? 2 : 0;
