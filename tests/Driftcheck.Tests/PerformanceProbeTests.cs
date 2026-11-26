using Driftcheck.Mcp.Qa;
using SkiaSharp;

namespace Driftcheck.Tests;

public class PerformanceProbeTests
{
    // Real `dumpsys gfxinfo co.gifted.app` output (API 35), histograms trimmed.
    const string Gfxinfo = """
        Applications Graphics Acceleration Info:
        Uptime: 42804521 Realtime: 42804521

        ** Graphics info for pid 15045 [co.gifted.app] **

        Stats since: 33912710291374ns
        Total frames rendered: 50655
        Janky frames: 41026 (80.99%)
        Janky frames (legacy): 5739 (11.33%)
        50th percentile: 17ms
        90th percentile: 34ms
        95th percentile: 61ms
        99th percentile: 125ms
        Number Missed Vsync: 1881
        Number High input latency: 2571
        Number Slow UI thread: 2753
        Number Slow bitmap uploads: 1415
        Number Slow issue draw commands: 34563
        Number Frame deadline missed: 41026
        Number Frame deadline missed (legacy): 5060
        HISTOGRAM: 5ms=295 6ms=542 7ms=1028
        50th gpu percentile: 13ms
        90th gpu percentile: 20ms
        95th gpu percentile: 4950ms
        99th gpu percentile: 4950ms
        GPU HISTOGRAM: 1ms=2 2ms=742

        Pipeline=Skia (OpenGL)
        """;

    // Real `dumpsys meminfo --local co.gifted.app` output (the plain form timed out on the emulator), plus the
    // Objects block that the full dump adds after App Summary (format from AOSP; not captured from the device).
    const string Meminfo = """
        ** MEMINFO in pid 15045 [co.gifted.app] **
                           Pss  Private  Private  SwapPss      Rss     Heap     Heap     Heap
                         Total    Dirty    Clean    Dirty    Total     Size    Alloc     Free
                        ------   ------   ------   ------   ------   ------   ------   ------
          Native Heap   155903   155876        0       84   156940        0        0        0
          Dalvik Heap    26070    25980       12      181    27888        0        0        0
                TOTAL   363667   257192    87052      454   497168        0        0        0

         App Summary
                               Pss(KB)                        Rss(KB)
                                ------                         ------
                   Java Heap:    35580                          60996
                 Native Heap:   155876                         156940
                        Code:    94200                         211652
                       Stack:     2128                           2132
                    Graphics:        0                              0
               Private Other:    56460
                      System:    19423
                     Unknown:                                   65448

                   TOTAL PSS:   363667            TOTAL RSS:   497168       TOTAL SWAP PSS:      454

         Objects
                       Views:       42         ViewRootImpl:        2
                 AppContexts:        4           Activities:        2
                      Assets:       10        AssetManagers:        0
        """;

    [Fact]
    public void ParseGfxinfo_ReadsRealOutput()
    {
        var j = PerformanceProbe.ParseGfxinfo(Gfxinfo)!;
        Assert.Equal(50655, j.TotalFrames);
        Assert.Equal(41026, j.JankyFrames);
        Assert.Equal(80.99, j.JankyPercent, 2);
        Assert.Equal((17, 34, 61, 125), (j.P50Ms, j.P90Ms, j.P95Ms, j.P99Ms));
        Assert.Equal(2753, j.SlowUiThread);
        Assert.Equal(1415, j.SlowBitmapUploads);
        Assert.Equal(34563, j.SlowDrawCommands);
        Assert.Equal(1881, j.MissedVsync);
        Assert.Equal(2571, j.HighInputLatency);
    }

    [Fact]
    public void ParseGfxinfo_NoFrames_ReturnsNull() =>
        Assert.Null(PerformanceProbe.ParseGfxinfo("No process found for: co.gifted.app"));

    [Fact]
    public void ParseMeminfo_ReadsAppSummaryAndObjects()
    {
        var m = PerformanceProbe.ParseMeminfo(Meminfo)!;
        Assert.Equal(363667 / 1024.0, m.TotalPssMb, 3);
        Assert.Equal(35580 / 1024.0, m.JavaHeapMb, 3);
        Assert.Equal(155876 / 1024.0, m.NativeHeapMb, 3);
        Assert.Equal(0, m.GraphicsMb);
        Assert.Equal(42, m.Views);
        Assert.Equal(2, m.Activities);
    }

    [Fact]
    public void ParseMeminfo_RealOutputWithoutObjects_DefaultsToZero()
    {
        var m = PerformanceProbe.ParseMeminfo(Meminfo[..Meminfo.IndexOf(" Objects", StringComparison.Ordinal)])!;
        Assert.Equal(0, m.Views);
        Assert.Equal(0, m.Activities);
    }

    [Fact]
    public void ParseMeminfo_TimeoutDump_ReturnsNull() =>
        Assert.Null(PerformanceProbe.ParseMeminfo("*** SERVICE 'meminfo' DUMP TIMEOUT (10000ms) EXPIRED ***"));

    [Fact]
    public void ParseAmStart_ReadsTimes()
    {
        var r = PerformanceProbe.ParseAmStart("Starting: Intent { cmp=co.gifted.app/.MainActivity }\nStatus: ok\nLaunchState: COLD\nActivity: co.gifted.app/.MainActivity\nTotalTime: 1234\nWaitTime: 1250\nComplete\n")!;
        Assert.Equal("COLD", r.LaunchState);
        Assert.Equal(1234, r.TotalTime);
        Assert.Equal(1250, r.WaitTime);
        Assert.Null(PerformanceProbe.ParseAmStart("Error: Activity class does not exist."));
    }

    [Fact]
    public void ParseLauncherActivity_TakesLastLine()
    {
        const string output = "priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=false\nco.gifted.app/.MainActivity\n";
        Assert.Equal("co.gifted.app/.MainActivity", PerformanceProbe.ParseLauncherActivity(output));
        Assert.Null(PerformanceProbe.ParseLauncherActivity("No activity found"));
    }

    [Fact]
    public void ClassifyOverdraw_CountsEachTintOverWhiteContent()
    {
        // 10x10 white screen: columns 0-1 blue (1x), 2-3 green (2x), 4-5 pink (3x), 6-7 red (4x+), 8-9 untouched.
        using var normal = Solid(10, 10, new SKColor(255, 255, 255));
        using var over = Solid(10, 10, new SKColor(255, 255, 255));
        for (var y = 0; y < 10; y++)
        {
            Fill(over, 0, y, new SKColor(209, 209, 255));  // blue a=0.18
            Fill(over, 2, y, new SKColor(209, 255, 209));  // green a=0.18
            Fill(over, 4, y, new SKColor(255, 191, 191));  // red a=0.25
            Fill(over, 6, y, new SKColor(255, 145, 145));  // red a=0.43
        }

        var r = PerformanceProbe.ClassifyOverdraw(over, normal, "x.png");
        Assert.Equal(0.2, r.Ratio1x, 3);
        Assert.Equal(0.2, r.Ratio2x, 3);
        Assert.Equal(0.2, r.Ratio3x, 3);
        Assert.Equal(0.2, r.Ratio4xPlus, 3);
        Assert.Equal("x.png", r.ScreenshotPath);
    }

    [Fact]
    public void ClassifyOverdraw_IdenticalImages_IsAllZero()
    {
        using var a = Solid(8, 8, new SKColor(40, 90, 200));
        using var b = Solid(8, 8, new SKColor(40, 90, 200));
        var r = PerformanceProbe.ClassifyOverdraw(a, b, "p");
        Assert.Equal((0, 0, 0, 0), (r.Ratio1x, r.Ratio2x, r.Ratio3x, r.Ratio4xPlus));
    }

    static SKBitmap Solid(int w, int h, SKColor c)
    {
        var bmp = new SKBitmap(w, h);
        bmp.Erase(c);
        return bmp;
    }

    // Colors the two columns starting at x on row y.
    static void Fill(SKBitmap bmp, int x, int y, SKColor c)
    {
        bmp.SetPixel(x, y, c);
        bmp.SetPixel(x + 1, y, c);
    }
}
