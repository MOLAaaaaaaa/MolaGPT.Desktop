using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Infrastructure;

internal sealed class AgentRuntimeSetup(
    PiSidecarRuntimeManager runtime,
    PiWorkSidecarLocator locator,
    ProviderRestorer byok,
    MolaGptLocalToolsRegistrar work,
    ProviderRegistry providers,
    SettingsViewModel settings,
    PythonRuntimeSetup python,
    NotificationCenter notifications)
{
    public const string NotificationKey = "pi-sidecar";

    private Task<(InstalledPiSidecar? Runtime, string? Error)>? _installation;
    private bool _activated;

    public bool IsCompatible => locator.TryResolve() is not null;
    private bool IsInstalling => _installation is { IsCompleted: false };
    private bool HasAgentProvider => providers.Providers.Any(provider => provider.Kind != ProviderKind.MolaGptProxy);

    public async Task<bool> EnsureUsableAsync()
    {
        if (IsCompatible)
        {
            // An empty registry can also be the result of a successful refresh.
            if (!_activated && !HasAgentProvider)
                await ActivateAsync();
            return true;
        }

        return _installation is { IsCompleted: false } running && (await running).Runtime is not null;
    }

    public void OfferInstall(Action openProviders, string? detail = null)
    {
        if (IsInstalling) return;
        var updating = runtime.GetInstalled() is not null;
        notifications.Notify(new AppNotification
        {
            Key = NotificationKey,
            Kind = NotifyKind.Info,
            Title = updating ? "Agent 运行环境有更新" : "需要 Agent 运行环境",
            Body = detail ?? (updating ? null : "使用 Work 与 BYOK 前需要此环境。"),
            ActionText = updating ? "更新" : "下载",
            Action = () => _ = InstallAsync(openProviders: openProviders),
            Sticky = true
        });
    }

    public void OfferUpdateForIncompatibleInstall(Action openProviders)
    {
        if (IsCompatible || runtime.GetInstalled() is null) return;
        if (!settings.IsLoggedIn && !settings.Providers.Any(entry => entry.Enabled && !ProviderPurpose.IsImage(entry.Purpose))) return;

        DiagnosticLog.Write("pi-sidecar", "已安装的 Agent 运行环境契约过旧，Work 与 BYOK 暂不可用。");
        OfferInstall(openProviders, "本地模型暂时不可用，更新后恢复 Work 与 BYOK。");
    }

    public Task<(InstalledPiSidecar? Runtime, string? Error)> InstallAsync(
        IProgress<SandboxProgress>? observer = null, Action? openProviders = null) =>
        _installation is { IsCompleted: false } running ? running : _installation = InstallCoreAsync(observer, openProviders);

    private async Task<(InstalledPiSidecar? Runtime, string? Error)> InstallCoreAsync(
        IProgress<SandboxProgress>? observer, Action? openProviders)
    {
        notifications.Progress(NotificationKey, "正在准备 Agent 运行环境");
        try
        {
            var progress = new Progress<SandboxProgress>(item =>
            {
                observer?.Report(item);
                notifications.Progress(NotificationKey, "正在准备 Agent 运行环境", item.Message,
                    item.Fraction > 0 ? item.Fraction : null);
            });
            var installed = await runtime.DownloadAndInstallAsync(progress);
            await ActivateAsync();
            var configure = HasAgentProvider ? null : openProviders;
            notifications.Notify(new AppNotification
            {
                Key = NotificationKey,
                Kind = NotifyKind.Success,
                Title = "Agent 运行环境已就绪",
                Body = configure is null ? installed.Version : "添加模型服务后即可开始对话。",
                ActionText = configure is null ? null : "添加模型服务",
                Action = configure,
                Sticky = configure is not null
            });
            if (openProviders is not null) python.OfferAfterAgentRuntime();
            return (installed, null);
        }
        catch (Exception ex)
        {
            notifications.Error("Agent 运行环境配置失败", ex.Message, NotificationKey);
            return (null, ex.Message);
        }
    }

    private async Task ActivateAsync()
    {
        _activated = true;
        byok.Restore().ReportFailures(notifications);
        try { await work.RefreshAsync(); }
        catch (Exception ex) { DiagnosticLog.Write("pi-work", "刷新 Work 失败：" + ex.Message); }
    }

    public void Remove()
    {
        if (IsInstalling) throw new InvalidOperationException("Agent 运行环境正在配置，请稍后再移除。");
        _activated = false;
        work.Deactivate();
        byok.Deactivate();
        runtime.Delete();
        if (runtime.GetInstalled() is not null)
            throw new IOException("Agent 运行环境仍在使用，请稍后重试。");
    }
}
