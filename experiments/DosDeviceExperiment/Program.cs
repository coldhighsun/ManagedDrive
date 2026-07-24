using System.Diagnostics;
using System.Runtime.InteropServices;

// This experiment answers: can a process in another Windows session (SYSTEM, session 0)
// reach a WinFsp-mounted RAM disk — either via its raw NT device path, or via a global
// drive-letter symlink that SYSTEM itself creates?
//
// It runs in two modes:
//   * default (interactive, elevated): mount the disk, then schedule a SYSTEM task that
//     re-launches this exe in --system-probe mode, and report the collected results.
//   * --system-probe <devicePath> <resultPath> <globalLetter>: runs as SYSTEM. Tries to
//     reach the disk three ways and writes findings to <resultPath>.

const string DriveLetter = "Y:";
const string GlobalProbeLetter = "W:";
const string ProbeFileName = "probe.txt";
const string ProbeContent = "dos-device-experiment-probe";
const string TaskName = "DosDeviceExperimentProbe";

if (args.Length >= 1 && args[0] == "--system-probe")
{
    RunSystemProbe(args[1], args[2], args[3]);
    return;
}

var resultPath = Path.Combine(Path.GetTempPath(), "dos-device-experiment-result.txt");
File.Delete(resultPath);

Console.WriteLine("=== Step 1: Mount test RAM disk ===");
var options = new DiskOptions
{
    CapacityBytes = 8 * 1024 * 1024,
    MountPoint = DriveLetter,
    VolumeLabel = "DosDeviceExperiment",
};

RamDisk disk = RamDisk.Create(options);
Console.WriteLine($"Mounted at {disk.MountPoint}");

var probePath = Path.Combine(DriveLetter + "\\", ProbeFileName);
File.WriteAllText(probePath, ProbeContent);
Console.WriteLine($"Wrote probe file: {probePath}");

try
{
    Console.WriteLine();
    Console.WriteLine("=== Step 2: Query NT device path behind the drive letter (this session) ===");
    var target = new char[1024];
    uint len = NativeMethods.QueryDosDevice(DriveLetter, target, (uint)target.Length);
    if (len == 0)
    {
        Console.WriteLine($"QueryDosDevice FAILED. Win32Error={Marshal.GetLastWin32Error()}");
        return;
    }

    var devicePath = new string(target, 0, (int)len).TrimEnd('\0');
    Console.WriteLine($"This-session target: {devicePath}");

    Console.WriteLine();
    Console.WriteLine("=== Step 3: Cross-session probe via a SYSTEM-run scheduled task ===");
    var exePath = Environment.ProcessPath!;
    var startTime = DateTime.Now.AddMinutes(1).ToString("HH:mm");
    var probeCommand = $"\"{exePath}\" --system-probe \"{devicePath}\" \"{resultPath}\" {GlobalProbeLetter}";

    RunSchtasks($"/create /tn {TaskName} /tr \"{probeCommand}\" /sc once /st {startTime} /ru SYSTEM /rl HIGHEST /f");
    Console.WriteLine($"Scheduled task '{TaskName}' created for {startTime} (SYSTEM). Waiting up to 100 s...");

    var deadline = DateTime.UtcNow.AddSeconds(100);
    while (!File.Exists(resultPath) && DateTime.UtcNow < deadline)
    {
        Thread.Sleep(1000);
    }

    Console.WriteLine();
    Console.WriteLine("=== Result (as seen by SYSTEM in session 0) ===");
    if (!File.Exists(resultPath))
    {
        Console.WriteLine("TIMED OUT waiting for scheduled task result. Check Task Scheduler history manually.");
    }
    else
    {
        Console.WriteLine(File.ReadAllText(resultPath));
    }
}
finally
{
    Console.WriteLine();
    Console.WriteLine("=== Cleanup ===");
    RunSchtasks($"/delete /tn {TaskName} /f");
    disk.Dispose();
    Console.WriteLine("Test RAM disk unmounted.");
}

// Runs as SYSTEM (session 0). Writes its findings to resultPath.
static void RunSystemProbe(string devicePath, string resultPath, string globalLetter)
{
    var log = new System.Text.StringBuilder();
    log.AppendLine($"[system-probe] running as: {Environment.UserName} (session {Process.GetCurrentProcess().SessionId})");
    log.AppendLine($"[system-probe] device path: {devicePath}");

    // (A) Raw NT device access via GLOBALROOT — does session 0 reach the volume device at all?
    var rawPath = @"\\?\GLOBALROOT" + devicePath + "\\probe.txt";
    log.AppendLine();
    log.AppendLine($"(A) Raw device read: {rawPath}");
    try
    {
        var content = File.ReadAllText(rawPath);
        log.AppendLine($"    SUCCESS -> \"{content.Trim()}\"");
    }
    catch (Exception ex)
    {
        log.AppendLine($"    FAILED -> {ex.GetType().Name}: {ex.Message}");
    }

    // (B) SYSTEM creates a GLOBAL drive-letter symlink, then reads through it.
    log.AppendLine();
    log.AppendLine($"(B) SYSTEM DefineDosDevice {globalLetter} -> {devicePath}, then read {globalLetter}\\probe.txt");
    bool defined = NativeMethods.DefineDosDevice(NativeMethods.DDD_RAW_TARGET_PATH, globalLetter, devicePath);
    if (!defined)
    {
        log.AppendLine($"    DefineDosDevice FAILED. Win32Error={Marshal.GetLastWin32Error()}");
    }
    else
    {
        log.AppendLine("    DefineDosDevice succeeded.");
        try
        {
            var content = File.ReadAllText(globalLetter + "\\probe.txt");
            log.AppendLine($"    Read via {globalLetter} SUCCESS -> \"{content.Trim()}\"");
        }
        catch (Exception ex)
        {
            log.AppendLine($"    Read via {globalLetter} FAILED -> {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            NativeMethods.DefineDosDevice(
                NativeMethods.DDD_RAW_TARGET_PATH | NativeMethods.DDD_REMOVE_DEFINITION | NativeMethods.DDD_EXACT_MATCH_ON_REMOVE,
                globalLetter,
                devicePath);
        }
    }

    File.WriteAllText(resultPath, log.ToString());
}

static void RunSchtasks(string arguments)
{
    var psi = new ProcessStartInfo("schtasks.exe", arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var process = Process.Start(psi)!;
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Console.WriteLine($"schtasks {arguments} -> exit={process.ExitCode} {output}{error}".TrimEnd());
}

internal static class NativeMethods
{
    public const uint DDD_RAW_TARGET_PATH = 0x00000001;
    public const uint DDD_REMOVE_DEFINITION = 0x00000002;
    public const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern uint QueryDosDevice(string deviceName, char[] targetPath, uint max);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool DefineDosDevice(uint flags, string deviceName, string? targetPath);
}
