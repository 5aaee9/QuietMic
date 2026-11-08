using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using QuietMic.Audio;

namespace QuietMic.VirtualMic;

/// <summary>
/// 用户态进程无法直接创建内核音频设备，因此使用现成的虚拟声卡作为“虚拟麦克风”：
/// 应用把降噪结果写入虚拟声卡的播放端，其他软件在录音端选择它即可。
/// </summary>
public static class VirtualCable
{
    private const string InstallerUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip";

    private static readonly (string Render, string Capture)[] Pairs =
    [
        ("CABLE Input", "CABLE Output"),
        ("VoiceMeeter Input", "VoiceMeeter Output"),
        ("VoiceMeeter Aux Input", "VoiceMeeter Aux Output"),
        ("Line 1 (Virtual Audio Cable)", "Line 1 (Virtual Audio Cable)")
    ];

    private static readonly string[] RenameProofAdapters =
    [
        "VB-Audio Virtual Cable",
        "Virtual Audio Cable"
    ];

    public sealed record VirtualCableDevices(AudioDevice Render, AudioDevice Capture);

    public static VirtualCableDevices? Find()
    {
        var render = DeviceList.Render();
        var capture = DeviceList.Capture();

        foreach (var (renderName, captureName) in Pairs)
        {
            var renderDevice = render.FirstOrDefault(device => device.Matches(renderName));
            var captureDevice = capture.FirstOrDefault(device => device.Matches(captureName));
            if (renderDevice is not null && captureDevice is not null)
            {
                return new VirtualCableDevices(renderDevice, captureDevice);
            }
        }

        // 用户改名后名字里可能不再有 CABLE，退回按驱动名称配对。
        foreach (var adapter in RenameProofAdapters)
        {
            var renderDevice = render.FirstOrDefault(device => device.IsAdapter(adapter) && device.Matches("Input"))
                ?? render.FirstOrDefault(device => device.IsAdapter(adapter));
            var captureDevice = capture.FirstOrDefault(device => device.IsAdapter(adapter) && device.Matches("Output"))
                ?? capture.FirstOrDefault(device => device.IsAdapter(adapter));

            if (renderDevice is not null && captureDevice is not null)
            {
                return new VirtualCableDevices(renderDevice, captureDevice);
            }
        }

        return null;
    }

    public static bool IsInstalled => Find() is not null;

    /// <summary>下载并静默安装 VB-CABLE（会弹出 UAC）。</summary>
    public static async Task InstallAsync(Action<string> progress, CancellationToken cancellationToken)
    {
        progress("正在下载虚拟声卡驱动…");

        var workDirectory = Path.Combine(Path.GetTempPath(), "QuietMic", "vbcable");
        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }

        Directory.CreateDirectory(workDirectory);
        var archive = Path.Combine(workDirectory, "vbcable.zip");

        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        await using (var source = await client.GetStreamAsync(InstallerUrl, cancellationToken))
        await using (var target = File.Create(archive))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        progress("正在解压…");
        ZipFile.ExtractToDirectory(archive, workDirectory, overwriteFiles: true);

        var installer = Directory
            .EnumerateFiles(workDirectory, "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories)
            .FirstOrDefault()
            ?? Directory.EnumerateFiles(workDirectory, "VBCABLE_Setup.exe", SearchOption.AllDirectories).FirstOrDefault();

        if (installer is null)
        {
            throw new FileNotFoundException("安装包中没有找到 VBCABLE_Setup_x64.exe。");
        }

        progress("等待管理员授权…");
        var startInfo = new ProcessStartInfo(installer, "-i -h")
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(installer)!
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动安装程序。");
        await process.WaitForExitAsync(cancellationToken);

        progress("正在等待设备出现…");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(1000, cancellationToken);
            if (IsInstalled)
            {
                return;
            }
        }
    }

    public static void OpenSoundSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }
}
