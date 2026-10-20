// Hc2000.XvfbHarness: scripted repro/verification harness for the real Avalonia X11 present path.
//
// What it answers: does the app keep presenting new pictures while keys are delivered, on a plain
// Xvfb + llvmpipe software-raster X server (the renderer class of the demo desktop), at a named build?
// It observes the SERVER-SIDE picture of the app window (XGetImage), which is the surface the sign-on
// wedge shows on, and drives input through XTEST - through libXtst when the box has it, else by
// speaking the XTEST extension over the X protocol itself (XTestRaw.cs). Only when neither works
// does it fall back to XSendEvent, and the receipt records which path delivered the keys.
//
// Build:  dotnet build tools/Hc2000.XvfbHarness
// Run:    dotnet tools/Hc2000.XvfbHarness/bin/Debug/net10.0/Hc2000.XvfbHarness.dll \
//             --repo <clone> --sha <build sha> --keys HELLO --out <receipt dir outside the repo>
//         --worktree <path> reuses a worktree you already checked out instead of paying for a
//         fresh one (a fresh checkout smudges ~655 MB of Git LFS files; measured 4 s and 672 s).
//         Put the worktree on a real disk, not in /tmp: on host-02 /tmp is tmpfs, so a checkout
//         there is counted against RAM, and the box was under memory pressure on 2026-10-17.
//         --env KEY=VALUE (repeatable) adds an environment variable for the app, e.g. pointing
//         XDG_CONFIG_HOME at a scratch dir holding the ROMs of the release under test.
//         (--app <Hc2000.App.dll or the Hc2000.App apphost> instead of --repo/--sha tests a
//          prebuilt or fixed release with the native libraries it ships itself; see the harness
//          doc on the work item, "Run against a fixed release".)
// Needs:  Xvfb, libX11, a software GL driver (LIBGL_ALWAYS_SOFTWARE=1 is set by the harness).
//         libXtst is optional: without it the harness still injects with XTEST over the raw X
//         protocol, and only if the server refuses that does it fall back to XSendEvent. The
//         receipt records injector and xtestTransport (libXtst | raw-protocol | none).
//         --xtest-probe <display> answers one question and exits: can this box inject keys with
//         XTEST? No app is started, so the fix lane can check a box in a second.
//
// Proof of delivery: with injector=xtest the harness selects KeyPress|KeyRelease on the app window
// (the way xev does) on its own connection, and the receipt's xevProbe records the key events it saw
// there and that their send_event flag was clear - a key the app cannot tell from a hardware key.
// If no KeyPress arrived the run is ERROR, not STATIC: an unchanged screen then says nothing about
// the present path.
//
// Exit codes (the signal the fix lane consumes):
//   0 GREEN   the picture keeps changing while keys are delivered (>= 2 distinct new frames)
//   1 RED     the wedge signature: one late frame then silence, or presents stop while the guest runs
//   2 ERROR   the harness could not run (no window, app died, capture failed, guest CPU flat)
//   3 STATIC  no change after keys AND a static baseline: inconclusive, NOT a failure. The settled
//             v2 identity screen is legitimately static for >= 3e8 master ticks, so 0-diff pairs
//             alone are never proof of a freeze.
using System.Diagnostics;
using System.Text.Json;

namespace Hc2000.XvfbHarness;

internal static class Program
{
    const int ExitGreen = 0, ExitRed = 1, ExitError = 2, ExitStatic = 3;

    static int Main(string[] args)
    {
        // --xtest-probe <display> answers one question and exits: can this box inject a key with
        // XTEST that a window sees as a real key? It maps a window of its own, selects KeyPress|
        // KeyRelease on it over the XTEST connection (what xev does), focuses it, injects 'a' and
        // reads the event back. No app, no receipt - the fix lane checks a box with this in a second.
        var probeAt = Array.IndexOf(args, "--xtest-probe");
        if (probeAt >= 0)
        {
            var disp = probeAt + 1 < args.Length ? args[probeAt + 1] : ":0";
            try
            {
                using var x = X11.Open(disp, out _);
                if (!x.HasXTest) { Console.WriteLine($"XTEST unavailable on {disp}: {x.XTestWhy}"); return ExitError; }
                var pw = x.ProbeWindow();
                x.WatchKeys(pw);
                var pf = x.Focus(pw);
                Thread.Sleep(200);
                x.WatchKeys(x.Root);
                // Accumulate what the window's own selection caught, the way xev would report it.
                var total = new Dictionary<nuint, X11.KeySight>();
                for (var t = 0; t < 3; t++)
                {
                    x.SendKey(pw, 'a', "xtest");
                    Thread.Sleep(150);
                    var (b2, e2) = x.DrainKeys();
                    foreach (var kv in b2)
                    {
                        var seen = total.TryGetValue(kv.Key, out var earlier) ? earlier : new X11.KeySight(0, 0, false);
                        total[kv.Key] = seen with { Press = seen.Press + kv.Value.Press, Release = seen.Release + kv.Value.Release, SendEventFlag = seen.SendEventFlag || kv.Value.SendEventFlag };
                    }
                    Console.WriteLine($"  after key {t}: " + string.Join(" ", b2.Select(kv => $"0x{kv.Key:x}:P{kv.Value.Press}/R{kv.Value.Release}/se{kv.Value.SendEventFlag}")) + $" errs={e2} [{string.Join(" ", x.RawErrors)}]");
                }
                var by = total; var errs = x.RawErrors.Count;
                var saw = by.TryGetValue(pw, out var s) ? s : new X11.KeySight(0, 0, false);
                var others = string.Join(" ", by.Where(kv => kv.Key != pw)
                    .Select(kv => $"0x{kv.Key:x}:{kv.Value.Press}/{kv.Value.Release}"));
                Console.WriteLine($"XTEST {x.XTestTransport} v{x.XTestVersion} on {disp}: window 0x{pw:x} focus 0x{pf:x} " +
                    $"keyPress={saw.Press} keyRelease={saw.Release} sendEventFlag={saw.SendEventFlag} " +
                    $"otherWindows[{others}] protocolErrors={errs} errors[{string.Join(" ", x.RawErrors)}]");
                x.DestroyWindow(pw);
                var ok = saw.Press > 0 && !saw.SendEventFlag;
                Console.WriteLine(ok
                    ? $"XTEST keys reach the focused window on {disp} with send_event clear"
                    : $"XTEST keys did NOT reach the focused window on {disp}");
                return ok ? ExitGreen : ExitError;
            }
            catch (Exception e) { Console.WriteLine($"XTEST probe failed on {disp}: {e.Message}"); return ExitError; }
        }


        var o = Options.Parse(args);
        Directory.CreateDirectory(o.Out);
        var logPath = Path.Combine(o.Out, "harness.log");
        var lines = new List<string>();
        void Log(string s)
        {
            var line = $"{DateTime.UtcNow:HH:mm:ss} {s}";
            lines.Add(line);
            Console.WriteLine(line);
            File.AppendAllText(logPath, line + "\n");
        }

        var started = DateTime.UtcNow;
        int? exit = null;
        string verdict = "ERROR", why = "not started";
        try { (exit, verdict, why) = Run(o, Log, started); }
        catch (HarnessException e) { why = e.Message; }
        catch (Exception e) { why = $"{e.GetType().Name}: {e.Message}"; }

        exit ??= ExitError;
        Log($"VERDICT {verdict} exit={exit} {why} durationS={(DateTime.UtcNow - started).TotalSeconds:F1}");
        return exit.Value;
    }

    static (int, string, string) Run(Options o, Action<string> Log, DateTime started)
    {
        // ---- 1. the build under test -------------------------------------------------------------
        var worktree = (string?)null;
        string dll, sha = "", payloadVersion = "";
        if (o.App is not null)
        {
            dll = Path.GetFullPath(o.App);
            if (!File.Exists(dll)) throw new HarnessException($"--app payload not found: {dll}");
            // The build under test is the payload itself, not whatever the harness clone happens to be
            // checked out at, so sha stays empty and the payload's own VERSION is recorded instead.
            payloadVersion = PayloadVersion(dll);
            Log($"app payload (prebuilt) {dll}{(payloadVersion.Length > 0 ? $" version {payloadVersion}" : "")}");
        }
        else
        {
            if (o.Repo is null || o.Sha is null)
                throw new HarnessException("pass --repo <clone> --sha <git rev>, or --app <Hc2000.App.dll>");
            var repo = Path.GetFullPath(o.Repo);
            if (Path.GetFullPath(o.Out).StartsWith(repo + Path.DirectorySeparatorChar))
                throw new HarnessException($"--out must be outside the repo ({repo}) so a run leaves no build output behind");
            sha = Git(repo, $"rev-parse --verify {o.Sha}", Log);
            worktree = o.Worktree is not null
                ? Path.GetFullPath(o.Worktree)
                : Path.Combine(Path.GetTempPath(), $"hc2000-xvfb-harness-{sha[..12]}");
            // One worktree per sha, kept between runs: a fresh checkout smudges ~655 MB of Git LFS
            // files, which cost 4 s once and 672 s on another run on this box. Reuse is allowed only
            // when the worktree really is at the sha under test.
            if (Directory.Exists(worktree))
            {
                if (Git(worktree, "rev-parse HEAD", Log) != sha)
                    throw new HarnessException($"worktree {worktree} is not at {sha}; remove it or pass --worktree <path>");
                Log($"worktree {worktree} reused at {sha}");
            }
            else
            {
                RunChecked("git", $"-C \"{repo}\" worktree prune", _ => { }, 120);
                RunChecked("git", $"-C \"{repo}\" worktree add --detach \"{worktree}\" {sha}", Log, 1800);
                Log($"worktree {worktree} at {sha}");
            }
            KillHolders(worktree, Log);
            RunChecked("dotnet",
                $"build \"{Path.Combine(worktree, "src", "Hc2000.App", "Hc2000.App.csproj")}\" " +
                $"-c {o.BuildConfig} --nologo -v q", Log, 1200);
            dll = Path.Combine(worktree, "src", "Hc2000.App", "bin", o.BuildConfig, "net10.0", "Hc2000.App.dll");
            if (!File.Exists(dll)) throw new HarnessException($"build produced no dll at {dll}");
        }

        var receipt = new Dictionary<string, object>
        {
            ["verdict"] = "", ["exit"] = 0, ["startedAt"] = started.ToString("O"),
            ["sha"] = sha, ["appDll"] = dll, ["buildConfig"] = o.BuildConfig,
            ["payloadVersion"] = payloadVersion,
            ["keys"] = o.Keys, ["intervalMs"] = o.IntervalMs,
            ["baselineCount"] = o.Baseline, ["postCount"] = o.Post,
        };

        // ---- 2. Xvfb on a free display, software raster ------------------------------------------
        var xvfb = (Process?)null;
        var app = (Process?)null;
        var xvfbLog = new List<string>();
        var appLog = new List<string>();
        var display = 0;
        try
        {
            for (var n = o.DisplayBase; n < o.DisplayBase + 12; n++)
            {
                if (File.Exists($"/tmp/.X{n}-lock")) continue;
                xvfb = Start("Xvfb", $":{n} -screen 0 {o.Screen} -nolisten tcp", null, xvfbLog);
                display = n;
                if (WaitForDisplay($":{n}", 20, Log)) break;
                Kill(xvfb); xvfb = null; display = 0;
            }
            if (xvfb is null) throw new HarnessException($"no Xvfb display accepted a connection from :{o.DisplayBase}");
            Log($"Xvfb pid={xvfb.Id} display=:{display} screen={o.Screen}");

            // ---- 3. the app, on the real X11 backend --------------------------------------------
            var env = new Dictionary<string, string>
            {
                ["DISPLAY"] = $":{display}",
                ["LIBGL_ALWAYS_SOFTWARE"] = "1",
                ["GALLIUM_DRIVER"] = "llvmpipe",
            };
            foreach (var kv in o.Env)
            {
                var eq = kv.IndexOf('=');
                if (eq <= 0) throw new HarnessException($"--env wants KEY=VALUE, got '{kv}'");
                env[kv[..eq]] = kv[(eq + 1)..];
            }
            // A published payload is self-contained: its apphost is the executable, and running its
            // dll through the box's dotnet would not use the runtime or native libs it ships.
            var (appFile, appArgs) = dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? ("dotnet", $"\"{dll}\"")
                : (dll, "");
            app = Start(appFile, $"{appArgs} {o.AppArgs}", env, appLog);
            Log($"app pid={app.Id} file={appFile} args='{o.AppArgs}' " +
                $"env=LIBGL_ALWAYS_SOFTWARE=1,GALLIUM_DRIVER=llvmpipe,{string.Join(",", o.Env)}");

            using var x = X11.Open($":{display}", out var detected);
            var injector = o.Inject == "auto" ? detected : o.Inject;
            if (injector == "xtest" && !x.HasXTest)
                throw new HarnessException($"--inject xtest requested but XTEST is unavailable: {x.XTestWhy}");
            receipt["injector"] = injector;
            receipt["xtestAvailable"] = x.HasXTest;
            receipt["xtestTransport"] = x.XTestTransport;
            if (x.XTestWhy.Length > 0) receipt["xtestUnavailableReason"] = x.XTestWhy;
            Log($"injector={injector} xtestTransport={x.XTestTransport}" +
                (x.XTestWhy.Length > 0 ? $" ({x.XTestWhy})" : ""));

            nuint w = 0;
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(o.WindowTimeoutS);
            while (w == 0 && DateTime.UtcNow < until)
            {
                if (app.HasExited) throw new HarnessException($"app exited early with code {app.ExitCode}");
                w = x.FindWindow(x.Root, o.Title);
                if (w == 0) Thread.Sleep(500);
            }
            if (w == 0) throw new HarnessException($"no window matching '{o.Title}' within {o.WindowTimeoutS}s");
            // The window is created before it is viewable; XGetImage fails until it is mapped and drawn.
            Frame? f0 = null;
            until = DateTime.UtcNow + TimeSpan.FromSeconds(o.WindowTimeoutS);
            while (f0 is null && DateTime.UtcNow < until)
            {
                try { f0 = x.Capture(w, false); }
                catch (HarnessException) { Thread.Sleep(500); }
            }
            if (f0 is null) throw new HarnessException($"window 0x{w:x} never became capturable within {o.WindowTimeoutS}s");
            receipt["window"] = $"0x{w:x}";
            receipt["frame"] = $"{f0.Width}x{f0.Height} {f0.BitsPerPixel}bpp";
            Log($"window 0x{w:x} '{x.WindowName(w)}' {f0.Width}x{f0.Height} {f0.BitsPerPixel}bpp");
            var focus = x.Focus(w);
            Log($"focus set to 0x{w:x}, server gave the keyboard to 0x{focus:x}");
            if (injector == "xtest")
            {
                // xev-style event selection, on the app window and on the root: where an XTEST key
                // lands is the focus window, and on a WM-less Xvfb that is worth recording.
                x.WatchKeys(w);
                x.WatchKeys(x.Root);
            }
            Thread.Sleep(o.SettleMs);   // let the app draw its first real screen before the baseline

            var framesDir = Path.Combine(o.Out, "frames");
            Directory.CreateDirectory(framesDir);
            var cpu0 = CpuTicks(app.Id);

            // ---- 4. baseline: is this screen moving on its own? ----------------------------------
            var baseline = new List<string>();
            for (var i = 0; i < o.Baseline; i++)
            {
                baseline.Add(x.Capture(w, false).Hash);
                Thread.Sleep(o.IntervalMs);
            }
            var baselineMoving = baseline.Distinct().Count() >= 2;
            Log($"baseline n={baseline.Count} distinct={baseline.Distinct().Count()} moving={baselineMoving}");

            // ---- 5. keys, with a capture pair around each one ------------------------------------
            var perKey = new List<Dictionary<string, object>>();
            var post = new List<string>();
            var current = baseline[^1];
            for (var i = 0; i < o.Keys.Length; i++)
            {
                if (app.HasExited) throw new HarnessException($"app exited after {i} keys (code {app.ExitCode})");
                var before = x.Capture(w, true);
                current = before.Hash;
                x.SendKey(w, o.Keys[i], injector);
                var after = new List<string>();
                for (var k = 0; k < o.Post; k++)
                {
                    Thread.Sleep(o.IntervalMs);
                    after.Add(x.Capture(w, false).Hash);
                }
                var last = x.Capture(w, true);
                after.Add(last.Hash);
                var newHashes = after.Where(h => h != before.Hash).Distinct().ToList();
                post.AddRange(after);
                Log($"key[{i}]='{o.Keys[i]}' before={before.Hash} newDistinct={newHashes.Count} " +
                    $"last={last.Hash} tail={string.Join(",", after.TakeLast(4))}");
                before.WritePng(Path.Combine(framesDir, $"key{i:02}-before-{before.Hash}.png"));
                last.WritePng(Path.Combine(framesDir, $"key{i:02}-after-{last.Hash}.png"));
                current = last.Hash;
                perKey.Add(new Dictionary<string, object>
                {
                    ["index"] = i, ["key"] = o.Keys[i].ToString(), ["before"] = before.Hash,
                    ["after"] = last.Hash, ["newDistinct"] = newHashes.Count,
                    ["newHashes"] = newHashes,
                });
            }

            var cpu1 = CpuTicks(app.Id);
            // The xev evidence: what this harness's own connection saw on the app window while the
            // keys were injected. XTEST keys arrive with send_event clear; if none arrived, the run
            // proves nothing about the present path and must not be read as a static screen.
            var xev = injector == "xtest"
                ? x.DrainKeys()
                : (ByWindow: new Dictionary<nuint, X11.KeySight>(), ProtocolErrors: 0);
            var appSight = xev.ByWindow.TryGetValue(w, out var sg) ? sg : new X11.KeySight(0, 0, false);
            foreach (var (win, sight) in xev.ByWindow)
                Log($"xevProbe window=0x{win:x} keyPress={sight.Press} keyRelease={sight.Release} " +
                    $"sendEventFlag={sight.SendEventFlag}");
            Log($"xevProbe appWindow=0x{w:x} focus=0x{focus:x} root=0x{x.Root:x} protocolErrors={xev.ProtocolErrors}");
            var newDistinctTotal = post.Where(h => h != baseline[^1]).Distinct().Count();
            var changesAfterLastKey = perKey.Count == 0 ? 0 : (int)perKey[^1]["newDistinct"];
            var tailCaptures = o.Post + 1;
            var cpuDelta = cpu1 - cpu0;
            Log($"post distinctNew={newDistinctTotal} changesAfterLastKey={changesAfterLastKey} " +
                $"tailCaptures={tailCaptures} cpuTicksDelta={cpuDelta}");

            // ---- 6. the verdict ------------------------------------------------------------------
            string v, reason;
            if (injector == "xtest" && appSight.Press == 0)
                (v, reason) = ("ERROR", "injected keys never reached the app window (xev probe saw no KeyPress): the run says nothing about the present path");
            else if (newDistinctTotal >= 2)
                (v, reason) = ("GREEN", "presents keep coming: >= 2 distinct new frames while keys are delivered");
            else if (newDistinctTotal == 1)
                (v, reason) = o.Keys.Length >= 2 && tailCaptures >= 4 && changesAfterLastKey == 0
                    ? ("RED", "wedge signature: exactly one new frame after the first keys, then silence while keys kept arriving")
                    : ("STATIC", "one new frame only, too few keys/captures to call the silence");
            else
                (v, reason) = baselineMoving
                    ? ("RED", "presents stopped: baseline was moving, zero new frames while keys were delivered")
                    : ("STATIC", "settled screen legitimately static (>= 3e8 master ticks): zero change and a static baseline is inconclusive");

            if (v == "RED" && cpuDelta <= 0)
                (v, reason) = ("ERROR", "RED pattern but guest CPU did not climb: the harness did not reproduce the running-guest precondition");

            receipt["verdict"] = v;
            receipt["exit"] = v switch { "GREEN" => ExitGreen, "RED" => ExitRed, "STATIC" => ExitStatic, _ => ExitError };
            receipt["reason"] = reason;
            receipt["baseline"] = baseline;
            receipt["baselineMoving"] = baselineMoving;
            receipt["postDistinctNew"] = newDistinctTotal;
            receipt["changesAfterLastKey"] = changesAfterLastKey;
            receipt["tailCaptures"] = tailCaptures;
            receipt["perKey"] = perKey;
            receipt["xevProbe"] = new Dictionary<string, object>
            {
                ["window"] = $"0x{w:x}",
                ["focus"] = $"0x{focus:x}",
                ["keyPress"] = appSight.Press,
                ["keyRelease"] = appSight.Release,
                ["sendEventFlag"] = appSight.SendEventFlag,
                ["byWindow"] = xev.ByWindow.ToDictionary(kv => $"0x{kv.Key:x}",
                    kv => (object)new Dictionary<string, object>
                    {
                        ["keyPress"] = kv.Value.Press,
                        ["keyRelease"] = kv.Value.Release,
                        ["sendEventFlag"] = kv.Value.SendEventFlag,
                    }),
                ["protocolErrors"] = xev.ProtocolErrors,
            };
            receipt["cpuTicks"] = new Dictionary<string, object> { ["before"] = cpu0, ["after"] = cpu1, ["delta"] = cpuDelta };
            receipt["xvfbPid"] = xvfb.Id;
            receipt["appPid"] = app.Id;
            receipt["display"] = $":{display}";
            lock (appLog) File.WriteAllLines(Path.Combine(o.Out, "app.log"), appLog);
            lock (xvfbLog) File.WriteAllLines(Path.Combine(o.Out, "xvfb.log"), xvfbLog);
            File.WriteAllText(Path.Combine(o.Out, "receipt.json"),
                JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
            Log($"receipt {Path.Combine(o.Out, "receipt.json")} frames={Directory.GetFiles(framesDir).Length}");

            var exit = v switch { "GREEN" => ExitGreen, "RED" => ExitRed, "STATIC" => ExitStatic, _ => ExitError };
            return (exit, v, reason);
        }
        finally
        {
            if (!o.Keep)
            {
                Kill(app);
                Kill(xvfb);
                foreach (var lockFile in new[] { $"/tmp/.X{display}-lock" })
                    if (display != 0 && File.Exists(lockFile)) TryDelete(lockFile);
            }
            // The worktree is never removed here: it is the input to the next run of the set, and a
            // run that failed mid-build must not throw away 655 MB of checkout. Clean up by hand:
            //   git -C <repo> worktree remove --force <worktree> && git -C <repo> worktree prune
        }
    }

    // ---- process helpers -------------------------------------------------------------------------
    static Process Start(string file, string args, Dictionary<string, string>? env, List<string>? drain = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        var p = Process.Start(psi) ?? throw new HarnessException($"could not start {file}");
        if (drain is not null)
        {
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (drain) drain.Add(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (drain) drain.Add("stderr: " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        return p;
    }

    /// <summary>
    /// A run whose harness was killed hard can leave the app alive with the worktree's dll mapped,
    /// and the next build then fails with ETXTBSY on that dll. Only processes whose command line
    /// names this worktree are touched, which by construction are this harness's own leftovers.
    /// </summary>
    static void KillHolders(string worktree, Action<string> Log)
    {
        foreach (var dir in Directory.GetDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
            try
            {
                var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ');
                if (!cmd.Contains(worktree)) continue;
                Log($"killing stale pid={pid} holding the worktree: {cmd[..Math.Min(140, cmd.Length)]}");
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
            }
            catch (Exception) { /* exited, or not ours to signal */ }
        }
    }

    static void Kill(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } }
        catch (Exception) { /* already gone */ }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { /* not ours to remove */ }
    }

    static string Git(string repo, string sub, Action<string> Log) =>
        RunChecked("git", $"-C \"{repo}\" {sub}", Log, 120).Trim();

    static string RunChecked(string file, string args, Action<string> Log, int timeoutS)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new HarnessException($"could not start {file}");
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutS * 1000))
        {
            Kill(p);
            throw new HarnessException($"'{file} {args}' did not finish in {timeoutS}s");
        }
        var outp = so.GetAwaiter().GetResult();
        var err = se.GetAwaiter().GetResult();
        if (p.ExitCode != 0)
        {
            var tail = Last(err, 3) + " | " + Last(outp, 3);
            throw new HarnessException($"'{file} {args}' exit={p.ExitCode}: {tail}");
        }
        Log($"{file} ok exit=0");
        return outp;
    }

    static string Last(string s, int lines) =>
        string.Join(" / ", s.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines));

    static bool WaitForDisplay(string display, int tries, Action<string> Log)
    {
        for (var i = 0; i < tries; i++)
        {
            try
            {
                using var probe = X11.Open(display, out _);
                return true;
            }
            catch (HarnessException) { Thread.Sleep(250); }
        }
        Log($"no display on {display}");
        return false;
    }

    /// <summary>utime+stime clock ticks of a pid from /proc/&lt;pid&gt;/stat: the "Z80 still running" evidence.</summary>
    static long CpuTicks(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var tail = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Fields after comm are 1-indexed state..; utime is field 14, stime 15, i.e. index 11 and 12 here.
        return long.Parse(tail[11]) + long.Parse(tail[12]);
    }

    // An installer payload is <root>/app/Hc2000.App with <root>/VERSION beside app/; a bare build
    // output has no VERSION, in which case the receipt records an empty payloadVersion.
    static string PayloadVersion(string dll)
    {
        try
        {
            var dir = Path.GetDirectoryName(dll) ?? "";
            foreach (var cand in new[] { Path.Combine(dir, "..", "VERSION"), Path.Combine(dir, "VERSION") })
                if (File.Exists(cand)) return File.ReadAllText(cand).Trim();
        }
        catch (Exception) { /* unreadable payload: the receipt just says nothing about its version */ }
        return "";
    }
}

internal sealed record Options(
    string? Repo, string? Sha, string? App, string Keys, int Baseline, int Post, int IntervalMs,
    string Out, string Inject, int DisplayBase, string Title, string AppArgs, string BuildConfig,
    int WindowTimeoutS, string Screen, int SettleMs, bool Keep, string? Worktree, string[] Env)
{
    static string S(string[] a, string n, string d)
    {
        for (var i = 0; i < a.Length - 1; i++) if (a[i] == n) return a[i + 1];
        return d;
    }

    static int I(string[] a, string n, int d) => int.Parse(S(a, n, d.ToString()));

    static string[] All(string[] a, string n)
    {
        var r = new List<string>();
        for (var i = 0; i < a.Length - 1; i++) if (a[i] == n) r.Add(a[i + 1]);
        return r.ToArray();
    }

    public static Options Parse(string[] a)
    {
        var inject = S(a, "--inject", "auto");
        if (inject is not ("auto" or "xtest" or "sendevent"))
            throw new HarnessException("--inject must be auto, xtest or sendevent");
        var keys = S(a, "--keys", "HELLO");
        if (keys.Length == 0) throw new HarnessException("--keys must not be empty");
        return new Options(
            Repo: S(a, "--repo", "").Length > 0 ? S(a, "--repo", "") : null,
            Sha: S(a, "--sha", "").Length > 0 ? S(a, "--sha", "") : null,
            App: S(a, "--app", "").Length > 0 ? S(a, "--app", "") : null,
            Keys: keys,
            Baseline: I(a, "--baseline", 6),
            Post: I(a, "--post", 15),
            IntervalMs: I(a, "--interval", 250),
            Out: S(a, "--out", "") is var o && o.Length > 0
                ? Path.GetFullPath(o)
                : throw new HarnessException("--out <receipt dir outside the repo> is required"),
            Inject: inject,
            DisplayBase: I(a, "--display-base", 97),
            Title: S(a, "--title", "HC2000"),
            AppArgs: S(a, "--app-args", "--start"),
            BuildConfig: S(a, "--build-config", "Release"),
            WindowTimeoutS: I(a, "--window-timeout", 40),
            Screen: S(a, "--screen", "1280x1024x24"),
            SettleMs: I(a, "--settle", 5000),
            Keep: a.Contains("--keep"),
            Worktree: S(a, "--worktree", "").Length > 0 ? S(a, "--worktree", "") : null,
            Env: All(a, "--env"));
    }

}
