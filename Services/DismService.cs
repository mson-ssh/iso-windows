using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WinISOBuilder.Models;

namespace WinISOBuilder.Services;

public partial class DismService
{
    private readonly string _tempRoot;
    private readonly string _logRoot;
    private string? _mountPath;
    private string? _extractPath;
    private Action<string>? _currentProgress;
    private bool _ownsExtractPath;

    public DismService()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "WinISOBuilder");
        _logRoot = Path.Combine(_tempRoot, "logs");
        Directory.CreateDirectory(_logRoot);
    }

    public string LogRoot => _logRoot;

    public async Task<IsoInfo> GetImageInfoAsync(
        string isoPath,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var info = new IsoInfo { FilePath = isoPath, SourcePath = isoPath, IsExtractedFolder = false };
        var extractPath = GetTempDir("extract");
        _currentProgress = onProgress;

        try
        {
            onProgress?.Invoke("Extracting ISO...");
            var wimPath = await ExtractIsoAndFindWimAsync(isoPath, extractPath, cancellationToken);
            ClearReadOnlyAttributes(extractPath);
            _extractPath = extractPath;
            _ownsExtractPath = true;

            onProgress?.Invoke("Reading image info...");
            var output = await RunDismAsync($"/Get-ImageInfo /ImageFile:\"{wimPath}\"", cancellationToken);
            info = ParseImageInfo(info, output);
            info = ParseEditionInfo(info, output);
            return info;
        }
        finally
        {
            _currentProgress = null;
        }
    }

    public async Task<IsoInfo> GetImageInfoFromExtractedFolderAsync(
        string folderPath,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var info = new IsoInfo { FilePath = folderPath, SourcePath = folderPath, IsExtractedFolder = true };
        _currentProgress = onProgress;

        try
        {
            onProgress?.Invoke("Validating extracted folder...");
            var wimPath = FindWimInSource(folderPath);
            _extractPath = folderPath;
            _ownsExtractPath = false;

            onProgress?.Invoke("Reading image info...");
            var output = await RunDismAsync($"/Get-ImageInfo /ImageFile:\"{wimPath}\"", cancellationToken);
            info = ParseImageInfo(info, output);
            info = ParseEditionInfo(info, output);
            return info;
        }
        finally
        {
            _currentProgress = null;
        }
    }

    public async Task InjectDriversIntoSelectedAsync(
        string driverPath,
        IReadOnlyList<EditionItem> editions,
        Action<string> onProgress,
        CancellationToken cancellationToken = default)
    {
        if (_extractPath == null)
            throw new InvalidOperationException("No ISO has been extracted.");

        _currentProgress = onProgress;

        try
        {
            // The UI may physically remove editions from the list, so treat the incoming
            // collection as the source of truth for editions to keep.
            var selected = editions.Where(e => e.IsSelected).ToList();

            if (selected.Count == 0)
                throw new InvalidOperationException("At least one edition must be selected.");

            EnsureWorkingSource(onProgress);
            var imagePath = FindWimInSource(_extractPath!);
            var prepared = await PrepareImageForSelectedEditionsAsync(imagePath, selected, onProgress, cancellationToken);

            // Service the selected images in the working WIM. If the source was rebuilt,
            // selected editions have new sequential indices in export order.
            for (int i = 0; i < prepared.Editions.Count; i++)
            {
                var index = prepared.Editions[i].Index;
                var name = prepared.Editions[i].Name;
                onProgress($"Injecting drivers into: {name} ({i + 1}/{prepared.Editions.Count})...");

                // Clean any stale mount
                if (Directory.Exists(_mountPath))
                    await TryUnmountDiscardAsync(_mountPath!);

                _mountPath = GetTempDir("mount");
                var mounted = false;

                try
                {
                    await RunDismAsync(
                        $"/Mount-Image /ImageFile:\"{prepared.ImagePath}\" /Index:{index} /MountDir:\"{_mountPath}\" {CheckScratchDir()}",
                        cancellationToken);
                    mounted = true;

                    await RunDismAsync(
                        $"/Image:\"{_mountPath}\" /Add-Driver /Driver:\"{driverPath}\" /Recurse {CheckScratchDir()}",
                        cancellationToken);

                    await RunDismAsync(
                        $"/Unmount-Image /MountDir:\"{_mountPath}\" /Commit {CheckScratchDir()}",
                        cancellationToken);
                    mounted = false;
                }
                catch
                {
                    if (mounted && Directory.Exists(_mountPath))
                        await TryUnmountDiscardAsync(_mountPath!);
                    throw;
                }
                finally
                {
                    if (!mounted && Directory.Exists(_mountPath))
                    {
                        try { Directory.Delete(_mountPath, true); } catch { }
                    }
                    _mountPath = null;
                }
            }

            onProgress("Driver injection complete.");
        }
        finally
        {
            _currentProgress = null;
        }
    }

    private void EnsureWorkingSource(Action<string> onProgress)
    {
        if (_extractPath == null || _ownsExtractPath)
            return;

        onProgress("Preparing writable working copy...");
        var workingExtractPath = GetTempDir("extract");
        CopyDirectory(_extractPath, workingExtractPath);
        ClearReadOnlyAttributes(workingExtractPath);
        _extractPath = workingExtractPath;
        _ownsExtractPath = true;
    }

    private async Task<PreparedImage> PrepareImageForSelectedEditionsAsync(
        string imagePath,
        IReadOnlyList<EditionItem> selected,
        Action<string> onProgress,
        CancellationToken cancellationToken)
    {
        var existingIndices = await GetImageIndicesAsync(imagePath, cancellationToken);
        var isEsd = Path.GetExtension(imagePath).Equals(".esd", StringComparison.OrdinalIgnoreCase);
        var selectedMatchesSource = selected.Count == existingIndices.Count
            && selected.Select(e => e.Index).SequenceEqual(existingIndices);

        if (!isEsd && selectedMatchesSource)
        {
            return new PreparedImage(
                imagePath,
                selected.Select(e => new PreparedEdition(e.Index, e.Name)).ToList());
        }

        onProgress(isEsd
            ? "Converting selected editions from ESD to WIM..."
            : "Exporting selected editions into a new WIM...");

        var sourcesDir = Path.GetDirectoryName(imagePath)!;
        var newWimPath = Path.Combine(sourcesDir, $"install.{Guid.NewGuid():N}.wim");
        var finalWimPath = Path.Combine(sourcesDir, "install.wim");

        for (int i = 0; i < selected.Count; i++)
        {
            var edition = selected[i];
            onProgress($"Exporting edition: {edition.Name} ({i + 1}/{selected.Count})...");
            await RunDismAsync(
                $"/Export-Image /SourceImageFile:\"{imagePath}\" /SourceIndex:{edition.Index} " +
                $"/DestinationImageFile:\"{newWimPath}\" /DestinationName:\"{edition.Name}\" /Compress:max /CheckIntegrity",
                cancellationToken);
        }

        ReplaceImageWithExportedWim(imagePath, newWimPath, finalWimPath);

        return new PreparedImage(
            finalWimPath,
            selected.Select((e, index) => new PreparedEdition(index + 1, e.Name)).ToList());
    }

    private static void ReplaceImageWithExportedWim(string sourceImagePath, string newWimPath, string finalWimPath)
    {
        var backupPath = File.Exists(finalWimPath)
            ? $"{finalWimPath}.{Guid.NewGuid():N}.bak"
            : null;

        try
        {
            if (backupPath != null)
            {
                EnsureWritable(finalWimPath);
                File.Move(finalWimPath, backupPath);
            }

            File.Move(newWimPath, finalWimPath);

            if (!sourceImagePath.Equals(finalWimPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(sourceImagePath))
            {
                EnsureWritable(sourceImagePath);
                File.Delete(sourceImagePath);
            }

            if (backupPath != null && File.Exists(backupPath))
                File.Delete(backupPath);
        }
        catch
        {
            if (!File.Exists(finalWimPath) && backupPath != null && File.Exists(backupPath))
                File.Move(backupPath, finalWimPath);
            throw;
        }
    }

    private async Task<List<int>> GetImageIndicesAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        var output = await RunDismAsync($"/Get-ImageInfo /ImageFile:\"{imagePath}\"", cancellationToken);
        var indices = new List<int>();
        foreach (Match m in EditionRegex().Matches(output))
            if (int.TryParse(m.Groups[1].Value, out var idx))
                indices.Add(idx);
        return indices;
    }

    public async Task<string> BuildIsoAsync(
        string outputPath,
        float progressWeight,
        Action<string> onProgress,
        CancellationToken cancellationToken = default)
    {
        if (_extractPath == null)
            throw new InvalidOperationException("No image has been processed.");

        _currentProgress = onProgress;

        try
        {
            onProgress("Locating boot files...");

            var oscdimg = FindOscdimg();
            if (oscdimg == null)
                throw new InvalidOperationException(
                    "oscdimg.exe not found. Install Windows ADK from https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/");

            var etfsboot = Path.Combine(_extractPath, "boot", "etfsboot.com");
            var efisys = Path.Combine(_extractPath, "efi", "microsoft", "boot", "efisys.bin");

            if (!File.Exists(etfsboot))
                throw new FileNotFoundException("etfsboot.com not found in extracted ISO. This ISO may not be bootable.");
            if (!File.Exists(efisys))
                throw new FileNotFoundException("efisys.bin not found in extracted ISO.");

            onProgress("Building bootable ISO...");
            onProgress("This may take several minutes...");

            var arguments = $"-m -o -u2 -udfver102 " +
                            $"-bootdata:2#p0,e,b\"{etfsboot}\"#pEF,e,b\"{efisys}\" " +
                            $"\"{_extractPath}\" \"{outputPath}\"";
            var result = await RunToolAsync("oscdimg", oscdimg, arguments, cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"oscdimg failed: {result.StandardError}");
            }

            onProgress($"ISO created: {outputPath}");
            Cleanup();
            return outputPath;
        }
        finally
        {
            _currentProgress = null;
        }
    }

    public void InjectUnattendedSetup(string accountName, string timeZoneId, Action<string> onProgress)
    {
        if (_extractPath == null)
            throw new InvalidOperationException("No image has been processed.");

        onProgress("Generating Autounattend.xml...");
        File.WriteAllText(Path.Combine(_extractPath, "Autounattend.xml"), CreateAutounattendXml(accountName, timeZoneId));

        onProgress("Adding SetupComplete.cmd for first-run policies...");
        var scriptsDir = Path.Combine(_extractPath, "sources", "$OEM$", "$$", "Setup", "Scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, "SetupComplete.cmd"), CreateSetupCompleteCmd(accountName));

        onProgress("Unattended setup files injected.");
    }

    private static string CreateAutounattendXml(string accountName, string timeZoneId) =>
        $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <unattend xmlns="urn:schemas-microsoft-com:unattend">
          <settings pass="windowsPE">
            <component name="Microsoft-Windows-International-Core-WinPE" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <InputLocale>en-US</InputLocale>
              <SystemLocale>en-US</SystemLocale>
              <UILanguage>en-US</UILanguage>
              <UserLocale>en-US</UserLocale>
            </component>
          </settings>
          <settings pass="specialize">
            <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <TimeZone>{{timeZoneId}}</TimeZone>
            </component>
          </settings>
          <settings pass="oobeSystem">
            <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <InputLocale>en-US</InputLocale>
              <SystemLocale>en-US</SystemLocale>
              <UILanguage>en-US</UILanguage>
              <UserLocale>en-US</UserLocale>
            </component>
            <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <TimeZone>{{timeZoneId}}</TimeZone>
              <OOBE>
                <HideEULAPage>true</HideEULAPage>
                <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
                <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
                <ProtectYourPC>3</ProtectYourPC>
              </OOBE>
              <UserAccounts>
                <LocalAccounts>
                  <LocalAccount wcm:action="add">
                    <Name>{{accountName}}</Name>
                    <DisplayName>{{accountName}}</DisplayName>
                    <Group>Administrators</Group>
                    <Password>
                      <Value></Value>
                      <PlainText>true</PlainText>
                    </Password>
                  </LocalAccount>
                </LocalAccounts>
              </UserAccounts>
            </component>
          </settings>
        </unattend>
        """;

    private static string CreateSetupCompleteCmd(string accountName) =>
        $$"""
        @echo off
        reg add "HKLM\SOFTWARE\Policies\Microsoft\FVE" /v PreventDeviceEncryption /t REG_DWORD /d 1 /f
        reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v EnableLUA /t REG_DWORD /d 0 /f
        powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Set-LocalUser -Name '{{accountName}}' -PasswordNeverExpires $true" >nul 2>&1
        exit /b 0
        """;

    public void Cleanup()
    {
        if (_mountPath != null && Directory.Exists(_mountPath))
        {
            TryUnmountDiscard(_mountPath);
            try { Directory.Delete(_mountPath, true); } catch { }
            _mountPath = null;
        }

        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }

        _extractPath = null;
        _ownsExtractPath = false;
        _currentProgress = null;
    }

    private string GetTempDir(string suffix)
    {
        var dir = Path.Combine(_tempRoot, suffix, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string CheckScratchDir()
    {
        var scratch = Path.Combine(_tempRoot, "scratch");
        Directory.CreateDirectory(scratch);
        return $"/ScratchDir:\"{scratch}\"";
    }

    private async Task<string> ExtractIsoAndFindWimAsync(
        string isoPath,
        string extractDir,
        CancellationToken cancellationToken)
    {
        // Use DISM to mount/extract, or fallback to PowerShell + 7z
        await ExtractIsoUsingCmdAsync(isoPath, extractDir, cancellationToken);

        return FindWimInSource(extractDir);
    }

    private static string FindWimInSource(string sourcePath)
    {
        var wimPath = Path.Combine(sourcePath, "sources", "install.wim");
        if (File.Exists(wimPath)) return wimPath;

        var esdPath = Path.Combine(sourcePath, "sources", "install.esd");
        if (File.Exists(esdPath)) return esdPath;

        throw new FileNotFoundException(
            "No install.wim or install.esd found in the selected source folder. Expected under 'sources'.");
    }

    private static void EnsureWritable(string filePath)
    {
        var attributes = File.GetAttributes(filePath);
        if ((attributes & FileAttributes.ReadOnly) == 0)
            return;

        File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(destinationDir, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var destinationPath = Path.Combine(destinationDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, true);
        }
    }

    private static void ClearReadOnlyAttributes(string rootPath)
    {
        foreach (var file in Directory.GetFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private async Task ExtractIsoUsingCmdAsync(
        string isoPath,
        string extractDir,
        CancellationToken cancellationToken)
    {
        // Try 7z first (fastest), then PowerShell fallback
        var sevenZip = Find7z();
        if (sevenZip != null)
        {
            var result = await RunToolAsync("7z", sevenZip, $"x \"{isoPath}\" -o\"{extractDir}\" -y", cancellationToken);
            if (result.ExitCode == 0) return;
        }

        // PowerShell fallback: mount ISO and copy
        var script = CreateIsoExtractScript(isoPath, extractDir);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result2 = await RunToolAsync(
            "powershell",
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            cancellationToken);

        if (result2.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to extract ISO: {result2.StandardError}");
        }
    }

    private static string CreateIsoExtractScript(string isoPath, string extractDir)
    {
        var escapedIsoPath = EscapePowerShellSingleQuotedString(isoPath);
        var escapedExtractDir = EscapePowerShellSingleQuotedString(extractDir);
        return $$"""
        $ErrorActionPreference = 'Stop'
        $isoPath = '{{escapedIsoPath}}'
        $extractDir = '{{escapedExtractDir}}'
        $disk = $null
        try {
            $disk = Mount-DiskImage -ImagePath $isoPath -PassThru
            $volume = $disk | Get-Volume | Select-Object -First 1
            if (-not $volume -or -not $volume.DriveLetter) {
                throw 'Mounted ISO volume was not assigned a drive letter.'
            }
            $source = "$($volume.DriveLetter):\*"
            Copy-Item -Path $source -Destination $extractDir -Recurse -Force
        }
        finally {
            if ($disk -ne $null) {
                Dismount-DiskImage -ImagePath $isoPath -ErrorAction SilentlyContinue
            }
        }
        """;
    }

    private static string EscapePowerShellSingleQuotedString(string value) =>
        value.Replace("'", "''");

    private static string? Find7z()
    {
        var paths = new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe"
        };
        foreach (var p in paths)
            if (File.Exists(p)) return p;

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';'))
        {
            var full = Path.Combine(dir, "7z.exe");
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static string? FindOscdimg()
    {
        var adkPaths = new[]
        {
            @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe",
            @"C:\Program Files (x86)\Windows Kits\8.1\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe",
            @"C:\Program Files (x86)\Windows Kits\8.0\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe"
        };
        foreach (var p in adkPaths)
            if (File.Exists(p)) return p;

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';'))
        {
            var full = Path.Combine(dir, "oscdimg.exe");
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private async Task<string> RunDismAsync(
        string arguments,
        CancellationToken cancellationToken = default)
    {
        var result = await RunToolAsync("dism", "dism.exe", arguments, cancellationToken);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"DISM failed (exit {result.ExitCode}): {result.StandardError}\n{result.StandardOutput}");

        return result.StandardOutput;
    }

    private async Task<ProcessRunResult> RunToolAsync(
        string toolName,
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        _currentProgress?.Invoke($"[RUN] {Path.GetFileName(fileName)} {arguments}");
        var result = await ProcessRunner.RunAsync(fileName, arguments, cancellationToken);
        await AppendLogAsync(toolName, fileName, arguments, result.ExitCode, result.StandardOutput, result.StandardError);
        return result;
    }

    private async Task TryUnmountDiscardAsync(string mountPath)
    {
        try
        {
            await RunDismAsync($"/Unmount-Image /MountDir:\"{mountPath}\" /Discard {CheckScratchDir()}");
        }
        catch
        {
            // Best-effort cleanup after a failed servicing operation.
        }
    }

    private void TryUnmountDiscard(string mountPath)
    {
        var arguments = $"/Unmount-Image /MountDir:\"{mountPath}\" /Discard {CheckScratchDir()}";

        try
        {
            var result = ProcessRunner.RunAsync("dism.exe", arguments).GetAwaiter().GetResult();
            AppendLog(
                "dism-cleanup",
                "dism.exe",
                arguments,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError);
        }
        catch
        {
            // Cleanup must never hide the original failure.
        }
    }

    private async Task AppendLogAsync(
        string toolName,
        string fileName,
        string arguments,
        int exitCode,
        string stdout,
        string stderr)
    {
        Directory.CreateDirectory(_logRoot);
        var logPath = Path.Combine(_logRoot, $"{DateTime.Now:yyyyMMdd}.log");
        var content =
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {toolName}\n" +
            $"FileName: {fileName}\n" +
            $"Arguments: {arguments}\n" +
            $"ExitCode: {exitCode}\n" +
            $"STDOUT:\n{stdout}\n" +
            $"STDERR:\n{stderr}\n" +
            "------------------------------------------------------------\n";

        await File.AppendAllTextAsync(logPath, content);
    }

    private void AppendLog(
        string toolName,
        string fileName,
        string arguments,
        int exitCode,
        string stdout,
        string stderr)
    {
        Directory.CreateDirectory(_logRoot);
        var logPath = Path.Combine(_logRoot, $"{DateTime.Now:yyyyMMdd}.log");
        var content =
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {toolName}\n" +
            $"FileName: {fileName}\n" +
            $"Arguments: {arguments}\n" +
            $"ExitCode: {exitCode}\n" +
            $"STDOUT:\n{stdout}\n" +
            $"STDERR:\n{stderr}\n" +
            "------------------------------------------------------------\n";

        File.AppendAllText(logPath, content);
    }

    private static IsoInfo ParseImageInfo(IsoInfo info, string output)
    {
        var match = IndexRegex().Match(output);
        if (match.Success)
            info = info with { ImageName = match.Groups[1].Value };
        return info;
    }

    private static IsoInfo ParseEditionInfo(IsoInfo info, string output)
    {
        var editions = new List<EditionItem>();
        foreach (Match m in EditionRegex().Matches(output))
        {
            if (int.TryParse(m.Groups[1].Value, out var idx))
                editions.Add(new EditionItem
                {
                    Index = idx,
                    Name = m.Groups[2].Value,
                    IsSelected = true
                });
        }
        // Detect architecture
        if (output.Contains("x86", StringComparison.OrdinalIgnoreCase))
            info = info with { Architecture = "x86" };
        else if (output.Contains("amd64", StringComparison.OrdinalIgnoreCase))
            info = info with { Architecture = "amd64" };
        else if (output.Contains("arm64", StringComparison.OrdinalIgnoreCase))
            info = info with { Architecture = "arm64" };

        return info with { Editions = editions };
    }

    [GeneratedRegex(@"^Name\s*:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex IndexRegex();

    [GeneratedRegex(@"Index\s*:\s*(\d+)\r?\nName\s*:\s*([^\r\n]+)", RegexOptions.Multiline)]
    private static partial Regex EditionRegex();

    private sealed record PreparedImage(string ImagePath, List<PreparedEdition> Editions);

    private sealed record PreparedEdition(int Index, string Name);
}
