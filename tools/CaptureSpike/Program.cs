using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenVector.Capture;
using OpenVector.Capture.Logging;
using OpenVector.Capture.Timing;
using OpenVector.Capture.Windows;

namespace OpenVector.Tools.CaptureSpike;

/// <summary>
/// Spike S1: can an allocation-free C# raw-input loop capture an 8 kHz mouse with acceptable jitter?
/// Runs one or two capture phases, writes .ovcap logs + summary.json, zips them for sending back.
/// </summary>
internal static class Program
{
    private const string Version = "0.1.0";

    private static int Main(string[] args)
    {
        Options opt;
        try { opt = Options.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); Options.PrintUsage(); return 2; }
        if (opt.Help) { Options.PrintUsage(); return 0; }

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"OpenVector CaptureSpike {Version}  (.NET {Environment.Version}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine("Reads raw mouse input only while its own window is in the foreground. No hooks, no background capture,");
        Console.WriteLine("no access to other programs. Nothing is sent anywhere; results are written to a local folder.");
        Console.WriteLine();

        var mice = RawInputDeviceEnumerator.GetMice();
        PrintDevices(mice);
        if (opt.ListOnly) return 0;

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        string outDir = Path.GetFullPath(opt.OutDir ?? Path.Combine("capture-spike-results", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(outDir);

        var phases = new List<PhaseSummary>();
        foreach (var mode in opt.Modes)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine($"PHASE {phases.Count + 1}/{opt.Modes.Count}: {Describe(mode)}");
            Console.WriteLine(new string('=', 78));
            Console.WriteLine("  1. Press Enter here. A window titled \"OpenVector CaptureSpike\" opens.");
            Console.WriteLine("  2. CLICK INSIDE that window (it must be the active window, or Windows delivers no input).");
            Console.WriteLine($"  3. Move the mouse in big, fast, continuous circles for {opt.Seconds} s (both directions,");
            Console.WriteLine("     some slow circles too). The cursor is kept inside the window. Don't click other windows.");
            Console.WriteLine("  The timer only starts once the window is active and the first motion arrives.");
            if (!opt.NoPrompt) Console.ReadLine();
            phases.Add(RunPhase(mode, opt, outDir, mice));
        }

        var summary = new SpikeSummary
        {
            Tool = "OpenVector.CaptureSpike",
            ToolVersion = Version,
            CreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Os = RuntimeInformation.OSDescription,
            OsVersion = Environment.OSVersion.VersionString,
            Dotnet = Environment.Version.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            QpcFrequency = Stopwatch.Frequency,
            ExpectedRateHz = opt.ExpectedHz,
            Devices = mice.Select(DeviceSummary.From).ToList(),
            Phases = phases,
        };
        string summaryPath = Path.Combine(outDir, "summary.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, SpikeJsonContext.Default.SpikeSummary));

        string zipPath = outDir.TrimEnd(Path.DirectorySeparatorChar) + ".zip";
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(outDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine("DONE. Please send back this file:");
        Console.WriteLine($"  {zipPath}");
        Console.WriteLine($"(contains summary.json and the raw .ovcap logs from {outDir})");
        return 0;
    }

    private static string Describe(CaptureWindowKind mode) => mode == CaptureWindowKind.MessageOnly
        ? "message-only capture window on a dedicated thread + visible window on the main thread (planned trainer design)"
        : "visible window owned by the capture thread itself (fallback design)";

    private static PhaseSummary RunPhase(CaptureWindowKind mode, Options opt, string outDir, IReadOnlyList<MouseDeviceInfo> mice)
    {
        string modeName = mode == CaptureWindowKind.MessageOnly ? "message-only" : "visible";
        string logPath = Path.Combine(outDir, $"capture-{modeName}{CaptureLogFormat.FileExtension}");
        var markers = new MarkerQueue();
        const string title = "OpenVector CaptureSpike";

        var capture = new RawInputCapture(new RawInputCaptureOptions
        {
            Window = mode,
            VisibleWindowTitle = title + " - click here, then move the mouse in circles",
            Markers = markers,
            RingCapacity = 1 << 17,
        });

        string metadata = BuildMetadata(modeName, opt, mice);
        int retain = (opt.Seconds + 20) * 9000;

        if (mode == CaptureWindowKind.MessageOnly) SpikeWindow.Create(title, markers, !opt.NoClip);
        capture.Start();
        var writer = CaptureLogWriter.Create(logPath, capture.Timebase, metadata);
        var recorder = new CaptureRecorder(capture.Ring, writer, markers,
            new CaptureRecorderOptions { NominalRateHz = opt.ExpectedHz, RetainCapacity = retain });
        recorder.WriteMarker(new MarkerRecord(Stopwatch.GetTimestamp(), MarkerKind.SessionStart), metadata);

        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        TimeSpan pause0 = GC.GetTotalPauseDuration();
        long mainAlloc0 = GC.GetAllocatedBytesForCurrentThread();

        long startTicks = 0;           // set at first motion while active
        long activeTicks = 0, lastLoop = Stopwatch.GetTimestamp();
        long lastUi = 0;
        bool prevVisibleActive = false;
        long durationTicks = (long)opt.Seconds * Stopwatch.Frequency;
        long hardTimeout = Stopwatch.GetTimestamp() + (long)(opt.Seconds + 300) * Stopwatch.Frequency;

        while (true)
        {
            if (mode == CaptureWindowKind.MessageOnly) SpikeWindow.Pump(2);
            else Thread.Sleep(2);

            recorder.Pump();
            long now = Stopwatch.GetTimestamp();
            bool active = mode == CaptureWindowKind.MessageOnly ? SpikeWindow.IsActive : capture.VisibleWindowActive;
            if (mode == CaptureWindowKind.Visible && active != prevVisibleActive && !opt.NoClip)
            {
                SpikeWindow.Clip(capture.WindowHandle, active);
                prevVisibleActive = active;
            }
            if (active) activeTicks += now - lastLoop;
            lastLoop = now;
            if (startTicks == 0 && active && recorder.MouseEvents > 0) startTicks = now;

            bool done = startTicks != 0 && now - startTicks >= durationTicks;
            bool closed = mode == CaptureWindowKind.MessageOnly ? SpikeWindow.CloseRequested : capture.CloseRequested;
            if (done || closed || now > hardTimeout || !capture.IsRunning) break;

            if (now - lastUi > Stopwatch.Frequency / 4)
            {
                lastUi = now;
                double remaining = startTicks == 0 ? opt.Seconds : Math.Max(0, (durationTicks - (now - startTicks)) / (double)Stopwatch.Frequency);
                string status = !active ? "NOT ACTIVE - click inside this window"
                    : startTicks == 0 ? "active - start moving the mouse"
                    : $"recording - {remaining:0.0} s left";
                string head = $"{title} [{modeName}] {status} | packets {recorder.MouseEvents}";
                if (mode == CaptureWindowKind.MessageOnly)
                {
                    SpikeWindow.SetText(head,
                        $"{status}\n\nPackets: {recorder.MouseEvents}\n\n" +
                        "Move the mouse in big, fast, continuous circles (both directions), plus a few slow ones.\n" +
                        "Keep this window active. The cursor is confined to this window while it is active.\n" +
                        "Closing the window ends this phase early.\n\n" +
                        "OpenVector only reads mouse input sent to this window while it is in the foreground.");
                }
                else
                {
                    Console.Write($"\r  {status} | packets {recorder.MouseEvents}        ");
                }
            }
        }

        bool endedByClose = mode == CaptureWindowKind.MessageOnly ? SpikeWindow.CloseRequested : capture.CloseRequested;
        long mainAlloc = GC.GetAllocatedBytesForCurrentThread() - mainAlloc0;
        capture.Stop();
        recorder.Pump();
        var counters = capture.GetCounters();
        recorder.WriteMarker(new MarkerRecord(Stopwatch.GetTimestamp(), MarkerKind.SessionEnd, 0, recorder.MouseEvents, counters.RingOverflows));
        writer.Dispose();
        if (mode == CaptureWindowKind.MessageOnly) SpikeWindow.Destroy();
        else SpikeWindow.Clip(0, false);
        if (mode == CaptureWindowKind.Visible) Console.WriteLine();

        var gc = new GcSummary
        {
            Gen0 = GC.CollectionCount(0) - gc0,
            Gen1 = GC.CollectionCount(1) - gc1,
            Gen2 = GC.CollectionCount(2) - gc2,
            TotalPauseMs = (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds,
            LatencyMode = GCSettings.LatencyMode.ToString(),
            MainThreadAllocatedBytes = mainAlloc,
        };

        var events = recorder.Retained;
        var report = PollingAnalyzer.Analyze(events, capture.Timebase,
            new PollingAnalysisOptions { NominalRateHzOverride = opt.ExpectedHz });
        var reportAuto = opt.ExpectedHz is null ? null : PollingAnalyzer.Analyze(events, capture.Timebase);

        var perDevice = new Dictionary<string, long>();
        long nonZeroExtra = 0, buttonEvents = 0, wheelEvents = 0;
        foreach (ref readonly var e in events)
        {
            string key = "0x" + e.Device.ToString("X", CultureInfo.InvariantCulture);
            perDevice[key] = perDevice.GetValueOrDefault(key) + 1;
            if (e.ExtraInformation != 0) nonZeroExtra++;
            if ((e.ButtonFlags & ~(RawMouseButtonFlags.Wheel | RawMouseButtonFlags.HWheel)) != 0) buttonEvents++;
            if ((e.ButtonFlags & (RawMouseButtonFlags.Wheel | RawMouseButtonFlags.HWheel)) != 0) wheelEvents++;
        }

        var phase = new PhaseSummary
        {
            Mode = modeName,
            LogFile = Path.GetFileName(logPath),
            EndedByWindowClose = endedByClose,
            RecordedSeconds = startTicks == 0 ? 0 : (lastLoop - startTicks) / (double)Stopwatch.Frequency,
            ForegroundSeconds = activeTicks / (double)Stopwatch.Frequency,
            Counters = counters,
            RecorderMissingPackets = recorder.MissingPackets,
            RecorderCoalescingMarkers = recorder.CoalescingMarkers,
            RetainOverflow = recorder.RetainOverflow,
            MarkersDropped = markers.Dropped,
            Gc = gc,
            Polling = report,
            PollingWithoutExpectedRate = reportAuto,
            PacketsPerDevice = perDevice,
            ExtraInfoNonZeroFraction = events.Length == 0 ? 0 : nonZeroExtra / (double)events.Length,
            RawAccelSetExtraInfoLikely = RawAccelExtraInfo.LooksEnabled(events),
            ButtonTransitionPackets = buttonEvents,
            WheelPackets = wheelEvents,
        };
        PrintPhase(phase);
        return phase;
    }

    private static string BuildMetadata(string mode, Options opt, IReadOnlyList<MouseDeviceInfo> mice)
    {
        var meta = new LogMetadata
        {
            Tool = "OpenVector.CaptureSpike",
            ToolVersion = Version,
            Mode = mode,
            Seconds = opt.Seconds,
            ExpectedRateHz = opt.ExpectedHz,
            Os = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Devices = mice.Select(DeviceSummary.From).ToList(),
        };
        return JsonSerializer.Serialize(meta, SpikeJsonContext.Default.LogMetadata);
    }

    private static void PrintDevices(IReadOnlyList<MouseDeviceInfo> mice)
    {
        Console.WriteLine($"Raw-input mouse devices ({mice.Count}):");
        foreach (var m in mice)
            Console.WriteLine($"  handle 0x{m.Handle:X}  VID:PID {m.VidPid}  buttons {m.ButtonCount}  {m.Name}");
        if (mice.Count == 0) Console.WriteLine("  (none found)");
    }

    private static void PrintPhase(PhaseSummary p)
    {
        var r = p.Polling;
        var c = p.Counters;
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine();
        Console.WriteLine($"--- Result: {p.Mode} ---");
        if (r.Packets == 0)
        {
            Console.WriteLine("  NO PACKETS RECEIVED. Either the window was never active while you moved the mouse, or this");
            Console.WriteLine("  capture mode does not receive foreground input on your Windows build. That is a valid result;");
            Console.WriteLine("  please still send the zip back.");
            return;
        }
        Console.WriteLine(string.Create(inv, $"  packets {r.Packets} in {r.Batches} reads over {p.RecordedSeconds:0.0} s recorded ({p.ForegroundSeconds:0.0} s foreground), {r.ActiveSegments} active segment(s)"));
        Console.WriteLine(string.Create(inv, $"  packets/s while moving: {r.ActivePacketsPerSecond:0}   estimated polling rate: {r.EstimatedRateHz:0} Hz (median window {r.MedianWindowRateHz:0}) -> nominal {(r.NominalRateHz is { } n ? n.ToString("0", inv) + " Hz" : "unknown")}"));
        Console.WriteLine(string.Create(inv, $"  batch size        p50 {r.BatchSize.P50:0}  p90 {r.BatchSize.P90:0}  p99 {r.BatchSize.P99:0}  max {r.BatchSize.Max:0}"));
        Console.WriteLine(string.Create(inv, $"  read interval us  p50 {r.BatchIntervalMicros.P50:0}  p90 {r.BatchIntervalMicros.P90:0}  p99 {r.BatchIntervalMicros.P99:0}  p99.9 {r.BatchIntervalMicros.P999:0}  max {r.BatchIntervalMicros.Max:0}"));
        Console.WriteLine(string.Create(inv, $"  packet interval us (reconstructed) p50 {r.ReconstructedPacketIntervalMicros.P50:0.0}  p99 {r.ReconstructedPacketIntervalMicros.P99:0.0}"));
        Console.WriteLine("  read-interval histogram:");
        long maxCount = Math.Max(1, r.BatchIntervalHistogram.Max(b => b.Count));
        foreach (var b in r.BatchIntervalHistogram)
            Console.WriteLine($"    {b.Label,-18} {b.Count,9}  {new string('#', (int)(40 * b.Count / maxCount))}");
        Console.WriteLine(string.Create(inv, $"  coalescing suspected: {r.CoalescingSuspected}  (fast windows {r.FastWindows}, median rate ratio {r.FastWindowRateRatioMedian?.ToString("0.00", inv) ?? "n/a"})"));
        Console.WriteLine($"  ring overflows {c.RingOverflows}, read errors {c.ReadErrors}, unbuffered WM_INPUT {c.UnbufferedMessages}, absolute packets {c.AbsolutePackets}");
        Console.WriteLine(string.Create(inv, $"  capture thread: max drain {c.MaxDrainMicros:0} us, allocated {c.CaptureThreadAllocatedBytes} B"));
        Console.WriteLine(string.Create(inv, $"  GC during phase: gen0 {p.Gc.Gen0}, gen1 {p.Gc.Gen1}, gen2 {p.Gc.Gen2}, total pause {p.Gc.TotalPauseMs:0.00} ms"));
        Console.WriteLine(string.Create(inv, $"  ulExtraInformation non-zero: {p.ExtraInfoNonZeroFraction:P1}; Raw Accel setExtraInfo likely: {p.RawAccelSetExtraInfoLikely}"));
        Console.WriteLine($"  packets per device: {string.Join(", ", p.PacketsPerDevice.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }
}

internal sealed class Options
{
    public int Seconds { get; private set; } = 15;
    public List<CaptureWindowKind> Modes { get; private set; } = [CaptureWindowKind.MessageOnly, CaptureWindowKind.Visible];
    public string? OutDir { get; private set; }
    public double? ExpectedHz { get; private set; }
    public bool ListOnly { get; private set; }
    public bool NoClip { get; private set; }
    public bool NoPrompt { get; private set; }
    public bool Help { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}.");
            switch (a)
            {
                case "--seconds":
                    o.Seconds = int.Parse(Next(), CultureInfo.InvariantCulture);
                    if (o.Seconds is < 1 or > 600) throw new ArgumentException("--seconds must be 1..600.");
                    break;
                case "--window":
                    o.Modes = Next() switch
                    {
                        "message" => [CaptureWindowKind.MessageOnly],
                        "visible" => [CaptureWindowKind.Visible],
                        "both" => [CaptureWindowKind.MessageOnly, CaptureWindowKind.Visible],
                        var v => throw new ArgumentException($"Unknown --window value '{v}' (message|visible|both)."),
                    };
                    break;
                case "--expected-hz": o.ExpectedHz = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--out": o.OutDir = Next(); break;
                case "--list": o.ListOnly = true; break;
                case "--no-clip": o.NoClip = true; break;
                case "--no-prompt": o.NoPrompt = true; break;
                case "-h" or "--help": o.Help = true; break;
                default: throw new ArgumentException($"Unknown argument '{a}'.");
            }
        }
        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: dotnet run -c Release --project tools/CaptureSpike -- [options]
              --seconds N        seconds to record per phase after motion starts (default 15)
              --window MODE      message | visible | both (default both: two phases)
              --expected-hz HZ   polling rate set in G HUB (e.g. 8000); enables coalescing checks against it
              --out DIR          output folder (default capture-spike-results/<timestamp>)
              --list             only list raw-input mouse devices
              --no-clip          do not confine the cursor to the window while it is active
              --no-prompt        do not wait for Enter before each phase
            """);
    }
}
