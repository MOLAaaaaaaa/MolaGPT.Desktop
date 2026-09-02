using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MolaGPT.App.Infrastructure;

internal sealed class AppAutoUpdateService(HttpClient http)
{
    public sealed record UpdatePackage(string Version, string DownloadUrl, string Sha256, string FileName);

    public async Task<string> DownloadAndVerifyAsync(
        UpdatePackage package,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT Desktop", "Updates", package.Version);
        Directory.CreateDirectory(targetDir);

        var fileName = string.IsNullOrWhiteSpace(package.FileName)
            ? $"MolaGPT.Desktop-{package.Version}-win-x64-setup.exe"
            : package.FileName;
        var targetPath = Path.Combine(targetDir, fileName);
        var temporaryPath = targetPath + ".download";

        if (File.Exists(targetPath) && await VerifySha256Async(targetPath, package.Sha256, ct))
        {
            progress?.Report(1);
            return targetPath;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, package.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("MolaGPT-Desktop");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024];
            long received = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (total is > 0) progress?.Report(Math.Clamp((double)received / total.Value, 0, 1));
            }
        }

        if (!await VerifySha256Async(temporaryPath, package.Sha256, ct))
        {
            File.Delete(temporaryPath);
            throw new InvalidOperationException("安装包校验失败，请稍后重试。");
        }

        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Move(temporaryPath, targetPath);
        progress?.Report(1);
        return targetPath;
    }

    public static void StartInstallerAfterExit(string installerPath)
    {
        if (!File.Exists(installerPath)) throw new FileNotFoundException("安装包不存在。", installerPath);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("无法定位当前程序路径。");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"molagpt-update-{Environment.ProcessId}-{Guid.NewGuid():N}.ps1");
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT Desktop", "Updates");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "update-installer.log");
        File.WriteAllText(scriptPath, InstallerScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-STA");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-Installer");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("-AppExe");
        startInfo.ArgumentList.Add(executable);
        startInfo.ArgumentList.Add("-ParentPid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-LogPath");
        startInfo.ArgumentList.Add(logPath);
        Process.Start(startInfo);
    }

    private static async Task<bool> VerifySha256Async(string path, string expectedSha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private const string InstallerScript = """
param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][string]$AppExe,
    [Parameter(Mandatory = $true)][int]$ParentPid,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = "Stop"

function Write-UpdateLog([string]$Message) {
    $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Add-Content -LiteralPath $LogPath -Value "$stamp $Message" -Encoding UTF8
}

function Test-ProcessRunning([int]$ProcessId) {
    try {
        $process = [System.Diagnostics.Process]::GetProcessById($ProcessId)
        $running = -not $process.HasExited
        $process.Dispose()
        return $running
    } catch [System.ArgumentException] {
        return $false
    }
}

try {
    Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Drawing

    [xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="MolaGPT Desktop"
        Width="544" Height="236"
        WindowStyle="None" ResizeMode="NoResize"
        AllowsTransparency="True" Background="Transparent"
        WindowStartupLocation="CenterScreen" ShowInTaskbar="True"
        FontFamily="Microsoft YaHei UI"
        TextOptions.TextFormattingMode="Display"
        SnapsToDevicePixels="True">
  <Grid Background="Transparent">
    <Border Width="488" Height="180" Margin="28"
            Background="#FFFFFFFF" BorderBrush="#FFCBD2DB" BorderThickness="1"
            CornerRadius="20">
      <Border.Effect>
        <DropShadowEffect Color="#16202A" BlurRadius="30" ShadowDepth="10" Opacity="0.24" />
      </Border.Effect>
      <Grid>
        <Grid.RowDefinitions>
          <RowDefinition Height="52" />
          <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <Border x:Name="TitleBar" BorderBrush="#FFEEF1F4" BorderThickness="0,0,0,1">
          <Grid Margin="18,0,10,0">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto" />
              <ColumnDefinition Width="*" />
              <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <Image x:Name="TitleLogo" Width="22" Height="22" Margin="0,0,14,0"
                   Stretch="Uniform" VerticalAlignment="Center" />
            <TextBlock Grid.Column="1" Text="MolaGPT Desktop" FontSize="14" FontWeight="SemiBold"
                       Foreground="#FF212529" VerticalAlignment="Center" />
            <Button x:Name="CaptionButton" Grid.Column="2" Width="34" Height="32"
                    Background="Transparent" BorderThickness="0" ToolTip="最小化">
              <Button.Template>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="ButtonBackground" Background="{TemplateBinding Background}" CornerRadius="6">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="ButtonBackground" Property="Background" Value="#FFF1F3F5" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Button.Template>
              <TextBlock x:Name="CaptionIcon" Text="&#xE921;" FontFamily="Segoe Fluent Icons"
                         FontSize="13" Foreground="#FF6C757D" />
            </Button>
          </Grid>
        </Border>

        <Grid Grid.Row="1" Margin="18,29,34,27">
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="22" />
            <ColumnDefinition Width="14" />
            <ColumnDefinition Width="*" />
          </Grid.ColumnDefinitions>
          <Grid x:Name="Spinner" Width="22" Height="22" VerticalAlignment="Center">
            <Ellipse Stroke="#FFF1F3F5" StrokeThickness="3" />
            <Ellipse Stroke="#FFBE727F" StrokeThickness="3" StrokeDashArray="14,8"
                     StrokeDashCap="Round" RenderTransformOrigin="0.5,0.5">
              <Ellipse.RenderTransform>
                <RotateTransform x:Name="SpinnerRotation" />
              </Ellipse.RenderTransform>
            </Ellipse>
          </Grid>
          <StackPanel Grid.Column="2" VerticalAlignment="Center">
            <TextBlock x:Name="StatusTitle" Text="正在安装更新" FontSize="20" FontWeight="SemiBold"
                       Foreground="#FF212529" LineHeight="29" />
            <TextBlock x:Name="StatusDescription" Text="安装完成后，MolaGPT 会自动重新启动。"
                       Margin="0,7,0,0" FontSize="13" Foreground="#FF6C757D" LineHeight="19" />
          </StackPanel>
        </Grid>
      </Grid>
    </Border>
  </Grid>
</Window>
'@

    $reader = [System.Xml.XmlNodeReader]::new($xaml)
    $window = [System.Windows.Markup.XamlReader]::Load($reader)
    $titleBar = $window.FindName("TitleBar")
    $titleLogo = $window.FindName("TitleLogo")
    $captionButton = $window.FindName("CaptionButton")
    $captionIcon = $window.FindName("CaptionIcon")
    $spinner = $window.FindName("Spinner")
    $rotation = $window.FindName("SpinnerRotation")
    $statusTitle = $window.FindName("StatusTitle")
    $statusDescription = $window.FindName("StatusDescription")

    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($AppExe)
    if ($null -ne $icon) {
        $iconSource = [System.Windows.Interop.Imaging]::CreateBitmapSourceFromHIcon(
            $icon.Handle,
            [System.Windows.Int32Rect]::Empty,
            [System.Windows.Media.Imaging.BitmapSizeOptions]::FromEmptyOptions())
        $iconSource.Freeze()
        $titleLogo.Source = $iconSource
        $window.Icon = $iconSource
        $icon.Dispose()
    }

    if ([System.Windows.SystemParameters]::ClientAreaAnimation) {
        $animation = [System.Windows.Media.Animation.DoubleAnimation]::new()
        $animation.From = 0
        $animation.To = 360
        $animation.Duration = [System.Windows.Duration]::new([TimeSpan]::FromMilliseconds(850))
        $animation.RepeatBehavior = [System.Windows.Media.Animation.RepeatBehavior]::Forever
        $rotation.BeginAnimation([System.Windows.Media.RotateTransform]::AngleProperty, $animation)
    }

    $script:AllowClose = $false
    $script:Phase = "WaitingForParent"
    $script:InstallerProcess = $null
    $script:Timer = [System.Windows.Threading.DispatcherTimer]::new()
    $script:Timer.Interval = [TimeSpan]::FromMilliseconds(100)

    function Show-InstallFailure([string]$Message) {
        $script:Timer.Stop()
        $script:Phase = "Failed"
        $script:AllowClose = $true
        $spinner.Visibility = [System.Windows.Visibility]::Collapsed
        $statusTitle.Text = "更新安装失败"
        $statusDescription.Text = $Message
        $captionIcon.Text = [char]0xE711
        $captionButton.ToolTip = "关闭"
    }

    $titleBar.Add_MouseLeftButtonDown({
        param($sender, $eventArgs)
        if ($eventArgs.ChangedButton -eq [System.Windows.Input.MouseButton]::Left) {
            $window.DragMove()
        }
    })

    $captionButton.Add_Click({
        if ($script:AllowClose) {
            $window.Close()
        } else {
            $window.WindowState = [System.Windows.WindowState]::Minimized
        }
    })

    $window.Add_Closing({
        param($sender, $eventArgs)
        if (-not $script:AllowClose) {
            $eventArgs.Cancel = $true
            $window.WindowState = [System.Windows.WindowState]::Minimized
        }
    })

    $script:Timer.Add_Tick({
        try {
            if ($script:Phase -eq "WaitingForParent") {
                if (Test-ProcessRunning $ParentPid) { return }

                Write-UpdateLog "starting installer: $Installer"
                $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
                $startInfo.FileName = $Installer
                $startInfo.Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-"
                $startInfo.UseShellExecute = $false
                $script:InstallerProcess = [System.Diagnostics.Process]::Start($startInfo)
                if ($null -eq $script:InstallerProcess) {
                    throw "installer process did not start"
                }
                $script:Phase = "Installing"
                return
            }

            if ($script:Phase -ne "Installing" -or -not $script:InstallerProcess.HasExited) {
                return
            }

            $script:InstallerProcess.WaitForExit()
            $exitCode = $script:InstallerProcess.ExitCode
            Write-UpdateLog "installer exit code: $exitCode"
            if ($exitCode -ne 0) {
                Show-InstallFailure "安装程序未能完成，请重新打开 MolaGPT 后重试。"
                return
            }
            if (-not (Test-Path -LiteralPath $AppExe)) {
                throw "application executable was not found after installation"
            }

            $script:Phase = "Restarting"
            $statusTitle.Text = "更新安装完成"
            $statusDescription.Text = "正在重新启动 MolaGPT，请稍候。"
            $appStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
            $appStartInfo.FileName = $AppExe
            $appStartInfo.WorkingDirectory = [System.IO.Path]::GetDirectoryName($AppExe)
            $appStartInfo.UseShellExecute = $true
            Write-UpdateLog "restarting application: $AppExe"
            [void][System.Diagnostics.Process]::Start($appStartInfo)
            $script:AllowClose = $true
            $window.Close()
        } catch {
            Write-UpdateLog "failed: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
            Show-InstallFailure "更新未能完成，请重新打开 MolaGPT 后重试。"
        }
    })

    Write-UpdateLog "update helper started for parent pid $ParentPid"
    $script:Timer.Start()
    [void]$window.ShowDialog()
} catch {
    Write-UpdateLog "failed: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
    throw
} finally {
    if ($null -ne $script:Timer) {
        $script:Timer.Stop()
    }
    if ($null -ne $script:InstallerProcess) {
        $script:InstallerProcess.Dispose()
    }
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
""";
}
