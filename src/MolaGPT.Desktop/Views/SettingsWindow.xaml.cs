using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.Desktop.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly PersonaListViewModel _personas;
    private readonly MolaGptAuthService _auth;
    private readonly Func<HttpClient> _byokHttpFactory;
    private readonly ProviderRegistry _registry;
    private readonly IChatToolHost _toolHost;
    private readonly PythonRuntimeManager _pythonRuntime;
    private readonly AppStatusService _appStatus;
    private readonly CloudSyncService _cloudSync;
    private readonly ConversationListViewModel _conversationList;
    private ProviderEntry? _editing;
    private PersonaItemViewModel? _editingPersona;
    private bool _editingPersonaIsDraft;
    private bool _syncInProgress;
    private bool _applyingPreset;
    private bool _updatingProviderSelection;
    private bool _updatingWebSearchUi;
    private bool _updatingImageGenerationUi;
    private bool _loadingPersonaForm;
    private bool _loadingEndpointForm;
    private string _editingPurpose = "chat";

    // Provider presets fill in the connection-info side (name, base URL,
    // models endpoint, default thinking-param dialect) but intentionally
    // ship NO default model IDs. Model names move too fast — a hardcoded
    // list goes stale within months. After saving, the user clicks
    // "自动获取" to pull the live list, or adds entries manually.
    private static readonly ProviderPreset[] ProviderPresets =
    {
        new(
            Id: "openrouter",
            Name: "OpenRouter",
            Type: "openai-compat",
            BaseUrl: "https://openrouter.ai/api/",
            ModelsPath: "v1/models",
            DefaultThinkingKind: ThinkingParamKind.OpenAiReasoningEffort,
            DefaultModels: Array.Empty<ProviderModelEntry>()),
        new(
            Id: "openrouter-images",
            Name: "OpenRouter 图像",
            Type: "openai-compat",
            BaseUrl: "https://openrouter.ai/api/",
            ModelsPath: "v1/models",
            DefaultThinkingKind: ThinkingParamKind.None,
            DefaultModels: Array.Empty<ProviderModelEntry>(),
            Purpose: "image",
            ApiPath: "v1/chat/completions",
            ImageFormat: "openai-chat-image"),
        new(
            Id: "deepseek",
            Name: "DeepSeek",
            Type: "openai-compat",
            BaseUrl: "https://api.deepseek.com/",
            ModelsPath: "v1/models",
            DefaultThinkingKind: ThinkingParamKind.DeepSeekV4,
            DefaultModels: Array.Empty<ProviderModelEntry>()),
        new(
            Id: "moonshot",
            Name: "Moonshot (Kimi)",
            Type: "openai-compat",
            BaseUrl: "https://api.moonshot.cn/",
            ModelsPath: "v1/models",
            DefaultThinkingKind: ThinkingParamKind.None,
            DefaultModels: Array.Empty<ProviderModelEntry>()),
        new(
            Id: "openai",
            Name: "OpenAI",
            Type: "openai-compat",
            BaseUrl: OpenAIProvider.DefaultBaseUrl,
            ModelsPath: "v1/models",
            DefaultThinkingKind: ThinkingParamKind.OpenAiReasoningEffort,
            DefaultModels: Array.Empty<ProviderModelEntry>()),
        new(
            Id: "openai-images",
            Name: "OpenAI 图像",
            Type: "openai-compat",
            BaseUrl: OpenAIProvider.DefaultBaseUrl,
            ModelsPath: "v1/models",
            DefaultThinkingKind: ThinkingParamKind.None,
            DefaultModels: Array.Empty<ProviderModelEntry>(),
            Purpose: "image",
            ApiPath: "v1/images/generations",
            ImageEditPath: "v1/images/edits",
            ImageFormat: "openai-images"),
        new(
            Id: "anthropic",
            Name: "Anthropic (Claude)",
            Type: "anthropic",
            BaseUrl: AnthropicProvider.DefaultBaseUrl,
            ModelsPath: "v1/models",
            DefaultThinkingKind: ThinkingParamKind.AnthropicAdaptive,
            DefaultModels: Array.Empty<ProviderModelEntry>()),
        new(
            Id: "gemini",
            Name: "Google Gemini",
            Type: "gemini",
            BaseUrl: GeminiProvider.DefaultBaseUrl,
            ModelsPath: "models",
            DefaultThinkingKind: ThinkingParamKind.GeminiThinkingLevel,
            DefaultModels: Array.Empty<ProviderModelEntry>()),
        new(
            Id: "custom-openai",
            Name: "自定义（OpenAI 兼容）",
            Type: "openai-compat",
            BaseUrl: "https://api.openai.com/",
            ModelsPath: "v1/models",
            DefaultThinkingKind: ThinkingParamKind.OpenAiReasoningEffort,
            DefaultModels: Array.Empty<ProviderModelEntry>())
    };

    public SettingsWindow(
        SettingsViewModel vm,
        MolaGptAuthService auth,
        ProviderRegistry registry,
        CloudSyncService cloudSync,
        ConversationListViewModel conversationList,
        PersonaListViewModel personas,
        Func<HttpClient> byokHttpFactory,
        IChatToolHost toolHost,
        PythonRuntimeManager pythonRuntime,
        AppStatusService appStatus)
    {
        InitializeComponent();
        _vm = vm;
        _personas = personas;
        _auth = auth;
        _registry = registry;
        _cloudSync = cloudSync;
        _conversationList = conversationList;
        _byokHttpFactory = byokHttpFactory;
        _toolHost = toolHost;
        _pythonRuntime = pythonRuntime;
        _appStatus = appStatus;
        DataContext = vm;
        SetPresetItemsForPurpose("chat");
        // The persona tab uses _personas as its DataContext so bindings inside
        // it can target Personas (the collection) directly without going
        // through SettingsViewModel.
        PersonaTabRoot.DataContext = _personas;
        PersonaIconPicker.ItemsSource = PersonaIconCatalog.All;
        PersonaList.SelectedItem = _personas.Personas.FirstOrDefault();
        _vm.Reload();
        UpdateAccountUi();
        InitializeWebSearchUi();
        UpdatePythonRuntimeStatusHint();
        // Open the advanced rules section up front when persistent allow/deny
        // rules already exist, so they are visible rather than hidden behind a
        // collapsed expander.
        RefreshPythonAdvancedRulesExpander();
        // Keep the browse/open button label in sync when the path is edited by
        // hand (typed, pasted, or cleared) in addition to picker-driven changes;
        // and reveal the advanced rules when a permanent allow rule is written
        // (e.g. from the execution approval dialog) while settings is open.
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.PythonToolExecutablePath))
                RefreshPythonBrowseButton();
            else if (args.PropertyName is nameof(SettingsViewModel.PythonToolAllowedImports)
                     or nameof(SettingsViewModel.PythonToolAllowedPathPrefixes)
                     or nameof(SettingsViewModel.PythonToolDeniedImports)
                     or nameof(SettingsViewModel.PythonToolDeniedPathPrefixes))
                RefreshPythonAdvancedRulesExpander();
        };
    }

    private void RefreshPythonAdvancedRulesExpander()
    {
        var hasRules =
            !string.IsNullOrWhiteSpace(_vm.PythonToolAllowedImports)
            || !string.IsNullOrWhiteSpace(_vm.PythonToolDeniedImports)
            || !string.IsNullOrWhiteSpace(_vm.PythonToolAllowedPathPrefixes)
            || !string.IsNullOrWhiteSpace(_vm.PythonToolDeniedPathPrefixes);
        if (hasRules)
            PythonAdvancedRulesExpander.IsExpanded = true;
    }

    public void OpenPersonasTab(bool startNewPersona)
    {
        SettingsTabs.SelectedItem = PersonaTab;
        if (startNewPersona)
            Dispatcher.BeginInvoke(new Action(BeginNewPersonaDraft));
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void InitializeWebSearchUi()
    {
        _updatingWebSearchUi = true;
        try
        {
            SelectWebSearchProvider(_vm.WebSearchProvider);
            WebSearchApiKeyBox.Password = _vm.WebSearchApiKey ?? string.Empty;
            McpServersList.ItemsSource = _vm.McpServers;
            _vm.McpServers.CollectionChanged += (_, _) => UpdateMcpEmptyHint();
            UpdateMcpEmptyHint();
            PopulateVisionCombo();
            SelectVisionModel();
            UpdateWebSearchStatusHint();
            InitializeImageGenerationUi();
        }
        finally
        {
            _updatingWebSearchUi = false;
        }
    }

    private void InitializeImageGenerationUi()
    {
        _updatingImageGenerationUi = true;
        try
        {
            PopulateImageGenerationCombo();
            SelectImageGenerationModel();
            UpdateImageGenerationStatusHint();
        }
        finally
        {
            _updatingImageGenerationUi = false;
        }
    }

    private void UpdateMcpEmptyHint() =>
        McpEmptyHint.Visibility = _vm.McpServers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void PopulateVisionCombo()
    {
        VisionExistingModelCombo.Items.Clear();
        foreach (var model in _vm.VisionProviderModels)
            VisionExistingModelCombo.Items.Add(model);

        if (_vm.VisionProviderModels.Count > 0)
            VisionExistingModelCombo.Items.Add(new Separator());

        var manageItem = new ComboBoxItem
        {
            Content = "模型管理...",
            Tag = VisionManageModelsTag,
            FontWeight = FontWeights.SemiBold
        };
        VisionExistingModelCombo.Items.Add(manageItem);
    }

    private void PopulateImageGenerationCombo()
    {
        ImageGenerationModelCombo.Items.Clear();
        foreach (var model in _vm.ImageGenerationProviderModels)
            ImageGenerationModelCombo.Items.Add(model);

        if (_vm.ImageGenerationProviderModels.Count > 0)
            ImageGenerationModelCombo.Items.Add(new Separator());

        ImageGenerationModelCombo.Items.Add(new ComboBoxItem
        {
            Content = "模型管理...",
            Tag = ImageGenerationManageModelsTag,
            FontWeight = FontWeights.SemiBold
        });

        UpdateImageGenerationStatusHint();
    }

    private void SelectVisionModel()
    {
        foreach (var item in VisionExistingModelCombo.Items)
        {
            if (item is VisionProviderModelOption option
                && string.Equals(option.ProviderId, _vm.VisionProxyProviderId, StringComparison.Ordinal)
                && string.Equals(option.ModelId, _vm.VisionProxyModelId, StringComparison.Ordinal))
            {
                VisionExistingModelCombo.SelectedItem = item;
                return;
            }
        }
    }

    private McpHttpClient CreateMcpClient() => new(_byokHttpFactory());

    private void AddMcpServerClick(object sender, RoutedEventArgs e)
    {
        var dlg = new McpServerDialog();
        dlg.ShowEdit(null, this, CreateMcpClient);
        if (dlg.Entry is not null)
            _vm.UpsertMcpServer(dlg.Entry);
    }

    private void EditMcpServerClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not McpServerEntry entry) return;
        var dlg = new McpServerDialog();
        dlg.ShowEdit(entry, this, CreateMcpClient);
        if (dlg.Entry is not null)
            _vm.UpsertMcpServer(dlg.Entry);
    }

    private void DeleteMcpServerClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is McpServerEntry entry)
            _vm.DeleteMcpServer(entry);
    }

    private const string VisionManageModelsTag = "__manage_models__";
    private const string ImageGenerationManageModelsTag = "__manage_image_models__";

    private void VisionExistingModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingWebSearchUi) return;

        if (VisionExistingModelCombo.SelectedItem is ComboBoxItem cbi
            && string.Equals(cbi.Tag?.ToString(), VisionManageModelsTag, StringComparison.Ordinal))
        {
            if (e.RemovedItems.Count > 0)
                VisionExistingModelCombo.SelectedItem = e.RemovedItems[0];
            NavigateToTab("模型服务");
            return;
        }

        if (VisionExistingModelCombo.SelectedItem is not VisionProviderModelOption option)
            return;
        _vm.VisionProxyProviderId = option.ProviderId;
        _vm.VisionProxyModelId = option.ModelId;
    }

    private void ImageGenerationModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingImageGenerationUi) return;

        if (ImageGenerationModelCombo.SelectedItem is ComboBoxItem cbi
            && string.Equals(cbi.Tag?.ToString(), ImageGenerationManageModelsTag, StringComparison.Ordinal))
        {
            if (e.RemovedItems.Count > 0)
                ImageGenerationModelCombo.SelectedItem = e.RemovedItems[0];
            NavigateToTab("模型服务");
            return;
        }

        if (ImageGenerationModelCombo.SelectedItem is not ImageGenerationProviderModelOption option)
            return;

        _vm.ImageGenerationProviderId = option.ProviderId;
        _vm.ImageGenerationModelId = option.ModelId;
        UpdateImageGenerationStatusHint();
    }

    private void NavigateToTab(string header)
    {
        foreach (TabItem tab in SettingsTabs.Items)
        {
            if (string.Equals(tab.Header?.ToString(), header, StringComparison.Ordinal))
            {
                tab.IsSelected = true;
                return;
            }
        }
    }

    private void SelectWebSearchProvider(string provider)
    {
        foreach (var item in WebSearchProviderCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
            {
                WebSearchProviderCombo.SelectedItem = item;
                return;
            }
        }

        WebSearchProviderCombo.SelectedIndex = 0;
    }

    private void WebSearchProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingWebSearchUi || WebSearchProviderCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.WebSearchProvider = item.Tag?.ToString() ?? "duckduckgo";
        _updatingWebSearchUi = true;
        try
        {
            WebSearchApiKeyBox.Password = _vm.WebSearchApiKey ?? string.Empty;
            UpdateWebSearchStatusHint();
        }
        finally
        {
            _updatingWebSearchUi = false;
        }
    }

    private void WebSearchApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingWebSearchUi) return;
        _vm.WebSearchApiKey = WebSearchApiKeyBox.Password;
    }

    private void UpdateWebSearchStatusHint()
    {
        var provider = SettingsViewModel.NormalizeWebSearchProvider(_vm.WebSearchProvider);
        WebSearchStatusText.Text = provider switch
        {
            "tavily" => "Tavily 使用其搜索 API；网页阅读由本机抓取。",
            "exa" => "Exa 使用其搜索 API 并请求正文摘要；网页阅读由本机抓取。",
            _ => "DuckDuckGo 无需 API Key，但稳定性与结果质量取决于页面可访问性。"
        };
    }

    private void UpdateImageGenerationStatusHint()
    {
        if (_vm.ImageGenerationProviderModels.Count == 0)
        {
            ImageGenerationStatusText.Text = "请先在「模型服务」中添加图像服务。";
            return;
        }

        ImageGenerationStatusText.Text = _vm.IsImageGenerationConfigured
            ? "已启用，BYOK 对话可调用该图像服务。"
            : "选择图像服务与模型后，即可在 BYOK 对话中创建图片。";
    }

    private void UpdatePythonRuntimeStatusHint()
    {
        var runtime = _pythonRuntime.GetInstalledRuntime();
        if (runtime is null)
        {
            PythonRuntimeStatusText.Text = "尚未安装内置运行时；可填写已有 python.exe，或一键下载 MolaGPT 专用便携环境。";
            RefreshPythonRuntimeButtons();
            return;
        }

        var packages = runtime.Packages.Count == 0
            ? string.Empty
            : $"；内置库：{string.Join(", ", runtime.Packages.Take(8))}";
        PythonRuntimeStatusText.Text = $"已配置 MolaGPT 专用 Python {runtime.Version}（{runtime.Runtime}）{packages}";
        RefreshPythonRuntimeButtons();
    }

    private async void ConfigurePythonRuntimeClick(object sender, RoutedEventArgs e)
    {
        ConfigurePythonRuntimeButton.IsEnabled = false;
        ClearPythonRuntimeButton.IsEnabled = false;
        ConfigurePythonRuntimeButton.Content = "配置中…";
        PythonRuntimeStatusText.Text = "正在准备 MolaGPT 专用 Python 环境…";
        _appStatus.Publish("Syncing", "正在后台下载 Python 环境");
        try
        {
            var progress = new Progress<PythonRuntimeProgress>(p =>
            {
                PythonRuntimeStatusText.Text = string.IsNullOrWhiteSpace(p.Message)
                    ? $"正在配置 Python 运行时 {p.Progress:P0}"
                    : p.Message;
                _appStatus.Publish("Syncing", FormatPythonRuntimeProgress(p));
            });
            var runtime = await _pythonRuntime.DownloadAndInstallAsync(progress, CancellationToken.None);
            _vm.PythonToolEnabled = true;
            _vm.PythonToolExecutablePath = runtime.PythonExecutablePath;
            PythonRuntimeStatusText.Text = $"配置完成：{runtime.PythonExecutablePath}";
            _appStatus.Publish("Success", "Python 环境已可用");
        }
        catch (Exception ex)
        {
            PythonRuntimeStatusText.Text = "配置失败：" + ex.Message;
            _appStatus.Publish("Error", "Python 环境配置失败");
        }
        finally
        {
            ConfigurePythonRuntimeButton.Content = "一键配置";
            ConfigurePythonRuntimeButton.IsEnabled = true;
            RefreshPythonRuntimeButtons();
        }
    }

    private void OpenPythonRuntimeDirectoryClick(object sender, RoutedEventArgs e)
    {
        // When a valid interpreter is configured, offer both opening its folder
        // and switching to a different one. Otherwise go straight to the picker.
        var configured = _vm.PythonToolExecutablePath?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            var menu = new ContextMenu();
            var openItem = new MenuItem { Header = "打开所在目录" };
            openItem.Click += (_, _) => RevealInExplorer(configured!);
            var changeItem = new MenuItem { Header = "重新选择…" };
            changeItem.Click += async (_, _) => await PickAndValidatePythonAsync();
            menu.Items.Add(openItem);
            menu.Items.Add(changeItem);
            menu.PlacementTarget = OpenPythonRuntimeDirectoryButton;
            menu.IsOpen = true;
            return;
        }

        _ = PickAndValidatePythonAsync();
    }

    private async Task PickAndValidatePythonAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 Python 解释器",
            Filter = "Python 解释器 (python.exe)|python.exe;python*.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        var runtimeDir = _pythonRuntime.RuntimeDirectory;
        if (Directory.Exists(runtimeDir))
            dialog.InitialDirectory = runtimeDir;

        if (dialog.ShowDialog(this) != true)
            return;

        var picked = dialog.FileName;
        PythonRuntimeStatusText.Text = "正在校验所选 Python…";
        OpenPythonRuntimeDirectoryButton.IsEnabled = false;
        try
        {
            var version = await ProbePythonVersionAsync(picked, CancellationToken.None);
            if (version is null)
            {
                PythonRuntimeStatusText.Text = "无法运行所选文件，请确认它是有效的 python.exe。";
                return;
            }

            _vm.PythonToolExecutablePath = picked;
            _vm.PythonToolEnabled = true;
            PythonRuntimeStatusText.Text = $"已选择 {version}：{picked}";
            _appStatus.Publish("Success", "Python 解释器已就绪");
        }
        catch (Exception ex)
        {
            PythonRuntimeStatusText.Text = "校验失败：" + ex.Message;
        }
        finally
        {
            OpenPythonRuntimeDirectoryButton.IsEnabled = true;
            RefreshPythonRuntimeButtons();
        }
    }

    private void RevealInExplorer(string filePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(filePath);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            PythonRuntimeStatusText.Text = "打开目录失败：" + ex.Message;
        }
    }

    private static async Task<string?> ProbePythonVersionAsync(string pythonPath, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo =
            {
                FileName = pythonPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        if (!process.Start())
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return null;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        var output = (stdout + stderr).Trim();
        return process.ExitCode == 0 && output.StartsWith("Python", StringComparison.OrdinalIgnoreCase)
            ? output
            : null;
    }

    private void ClearPythonRuntimeClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_pythonRuntime.RuntimeDirectory))
        {
            PythonRuntimeStatusText.Text = "Python 环境目录尚不存在。";
            RefreshPythonRuntimeButtons();
            return;
        }

        var result = MessageBox.Show(
            this,
            "仅删除通过一键配置部署的 MolaGPT 专用 Python 环境。",
            "清除环境",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            var runtimeDir = _pythonRuntime.RuntimeDirectory;
            _pythonRuntime.DeleteRuntime();
            if (IsPathInside(_vm.PythonToolExecutablePath, runtimeDir))
            {
                _vm.PythonToolExecutablePath = string.Empty;
                _vm.PythonToolEnabled = false;
            }

            PythonRuntimeStatusText.Text = "已清除 MolaGPT 专用 Python 环境。";
            _appStatus.Publish("Success", "Python 环境已清除");
        }
        catch (Exception ex)
        {
            PythonRuntimeStatusText.Text = "清除失败：" + ex.Message;
            _appStatus.Publish("Error", "Python 环境清除失败");
        }
        finally
        {
            RefreshPythonRuntimeButtons();
        }
    }

    private void RefreshPythonRuntimeButtons()
    {
        // The clear action only makes sense for a one-click downloaded runtime,
        // so it stays hidden until that runtime exists on disk.
        ClearPythonRuntimeButton.Visibility = Directory.Exists(_pythonRuntime.RuntimeDirectory)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshPythonBrowseButton();
    }

    private void RefreshPythonBrowseButton()
    {
        // Dual purpose: when a valid interpreter is configured the button opens
        // its folder (and the user can still pick a different one); otherwise it
        // is a plain "browse" action that opens the file picker.
        var configured = _vm.PythonToolExecutablePath?.Trim().Trim('"');
        var hasValid = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured);
        OpenPythonRuntimeDirectoryButton.Content = hasValid ? "配置目录" : "浏览…";
        OpenPythonRuntimeDirectoryButton.ToolTip = hasValid
            ? "打开解释器所在目录；如需更换可重新选择"
            : "选择 python.exe";
    }

    private static string FormatPythonRuntimeProgress(PythonRuntimeProgress progress) =>
        progress.Stage switch
        {
            "download" => $"正在后台下载 Python 环境 {progress.Progress:P0}",
            "verify" => "正在校验 Python 环境",
            "extract" => "正在解压 Python 环境",
            "done" => "Python 环境已可用",
            _ => "正在配置 Python 环境"
        };

    private static bool IsPathInside(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async void CheckWebSearchClick(object sender, RoutedEventArgs e)
    {
        CheckWebSearchButton.IsEnabled = false;
        CheckWebSearchButton.Content = "测试中…";
        WebSearchStatusText.Text = "正在发送一次测试搜索…";
        try
        {
            var options = new LocalToolOptions(
                Network: true,
                WebPage: false,
                SearchProvider: _vm.WebSearchProvider,
                SearchApiKey: _vm.WebSearchApiKey,
                SearchBaseUrl: _vm.WebSearchBaseUrl,
                SearchMaxResults: Math.Clamp(_vm.WebSearchMaxResults, 1, 10),
                WebPageMaxCharacters: Math.Clamp(_vm.WebPageMaxCharacters, 1000, 30000));
            var result = await LocalToolRegistry.ExecuteAsync(
                "search_web",
                "{\"query\":\"MolaGPT\"}",
                options,
                _byokHttpFactory(),
                CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            var ok = doc.RootElement.TryGetProperty("success", out var success)
                     && success.ValueKind == JsonValueKind.True;
            WebSearchStatusText.Text = ok ? "连接成功，搜索服务可用" : "连接失败，请检查 API Key 或接入地址";
        }
        catch (Exception ex)
        {
            WebSearchStatusText.Text = "连接失败：" + ex.Message;
        }
        finally
        {
            CheckWebSearchButton.Content = "测试连接";
            CheckWebSearchButton.IsEnabled = true;
        }
    }

    private async void CheckImageGenerationClick(object sender, RoutedEventArgs e)
    {
        CheckImageGenerationButton.IsEnabled = false;
        CheckImageGenerationButton.Content = "测试中…";
        ImageGenerationStatusText.Text = "正在发送一次测试生成…";
        try
        {
            var tool = new ImageGenerationTool(_byokHttpFactory);
            var images = await tool.GenerateAsync(
                _vm.BuildImageGenerationOptions(),
                "a single black dot on a white background",
                CancellationToken.None);
            ImageGenerationStatusText.Text = images.Count > 0
                ? "连接成功，图像服务可用"
                : "连接成功，但未返回图片";
        }
        catch (Exception ex)
        {
            ImageGenerationStatusText.Text = "连接失败：" + ex.Message;
        }
        finally
        {
            CheckImageGenerationButton.Content = "测试连接";
            CheckImageGenerationButton.IsEnabled = true;
        }
    }

    private void SelectImageGenerationModel()
    {
        foreach (var item in ImageGenerationModelCombo.Items)
        {
            if (item is ImageGenerationProviderModelOption option
                && string.Equals(option.ProviderId, _vm.ImageGenerationProviderId, StringComparison.Ordinal)
                && string.Equals(option.ModelId, _vm.ImageGenerationModelId, StringComparison.Ordinal))
            {
                ImageGenerationModelCombo.SelectedItem = item;
                return;
            }
        }

        var selected = _vm.SelectedImageGenerationModel;
        if (selected is not null)
            ImageGenerationModelCombo.SelectedItem = selected;
    }

    private void HeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void UpdateAccountUi()
    {
        var jwt = _auth.CurrentJwt;
        var user = _auth.CurrentUsername;
        if (!string.IsNullOrEmpty(jwt))
        {
            _vm.IsLoggedIn = true;
            _vm.MolaGptUsername = user;
            StatusLine.Text = user ?? "MolaGPT 用户";
            StatusDetail.Text = "已登录账号";
            LoginLogoutButton.Content = "退出";
        }
        else
        {
            _vm.IsLoggedIn = false;
            _vm.MolaGptUsername = null;
            StatusLine.Text = "未登录";
            StatusDetail.Text = string.Empty;
            LoginLogoutButton.Content = "登录";
        }
    }

    private void LoginLogoutClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_auth.CurrentJwt))
        {
            _auth.Logout();
            UpdateAccountUi();
        }
        else
        {
            // Defer to App.OnLoginRequested via main window mechanism — simpler to just create the dialog locally.
            DialogResult = null; // keep settings open
            var app = (App)Application.Current;
            var loginDialog = (LoginDialog)app.Host!.Services.GetService(typeof(LoginDialog))!;
            if (loginDialog is null) return;
            loginDialog.Owner = this;
            if (loginDialog.ShowDialog() == true)
            {
                UpdateAccountUi();
                _ = SyncAfterLoginAsync();
            }
        }
    }

    private async Task SyncAfterLoginAsync()
    {
        try
        {
            await _cloudSync.RequestForegroundSyncAsync();
            await _conversationList.ReloadAsync();
        }
        catch
        {
            // CloudSyncService publishes the user-facing status.
        }
    }

    private async void SyncNowClick(object sender, RoutedEventArgs e)
    {
        if (_syncInProgress) return;
        _syncInProgress = true;
        SyncNowButton.IsHitTestVisible = false;
        SyncNowButton.Content = "同步中…";
        CloudSyncStatusText.Text = "正在同步对话…";
        try
        {
            var progress = new Progress<string>(message => CloudSyncStatusText.Text = message);
            var result = await _cloudSync.SyncAsync(progress);
            await _conversationList.ReloadAsync();
            CloudSyncStatusText.Text =
                $"已同步：上传 {result.Uploaded} · 更新 {result.Downloaded} · 删除 {result.Deleted}";
        }
        catch (Exception ex)
        {
            CloudSyncStatusText.Text = $"同步失败：{ex.Message}";
        }
        finally
        {
            _syncInProgress = false;
            SyncNowButton.Content = "立即同步";
            SyncNowButton.IsHitTestVisible = true;
        }
    }

    private async void SyncToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsLoggedIn) return;
        try
        {
            await _cloudSync.UpdateCloudSyncSettingAsync(_vm.SyncConversations);
            CloudSyncStatusText.Text = _vm.SyncConversations
                ? "已开启对话云同步"
                : "已关闭对话云同步";
        }
        catch (Exception ex)
        {
            CloudSyncStatusText.Text = $"同步设置更新失败：{ex.Message}";
        }
    }

    private void ProviderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingProviderSelection)
            return;

        if (ProviderList.SelectedItem is not ProviderEntry entry)
        {
            ProviderEditor.IsEnabled = false;
            return;
        }
        BeginEdit(entry);
    }

    private void CopyTextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: string text } && !string.IsNullOrWhiteSpace(text))
            Clipboard.SetText(text.Trim());
        e.Handled = true;
    }

    private void AddChatProviderClick(object sender, RoutedEventArgs e) => AddProvider("chat");

    private void AddImageProviderClick(object sender, RoutedEventArgs e) => AddProvider("image");

    private void AddProvider(string purpose)
    {
        var preset = PresetsForPurpose(purpose).First();
        var entry = new ProviderEntry(
            Id: Guid.NewGuid().ToString("N"),
            Type: preset.Type,
            Name: preset.Name,
            BaseUrl: preset.BaseUrl,
            ApiKey: "",
            Models: preset.DefaultModels.ToList(),
            Enabled: true,
            SortOrder: _vm.Providers.Count,
            Purpose: preset.Purpose,
            ApiPath: preset.ApiPath,
            ImageEditPath: preset.ImageEditPath,
            ImageFormat: preset.ImageFormat);
        _vm.Providers.Add(entry);
        ProviderList.SelectedItem = entry;
    }

    private System.Collections.ObjectModel.ObservableCollection<EditableModelEntry> _editingModels = new();

    private void BeginEdit(ProviderEntry entry)
    {
        _editing = entry;
        ProviderEditor.IsEnabled = true;
        _loadingEndpointForm = true;
        try
        {
            EditName.Text = entry.Name;
            _applyingPreset = true;
            try { SetFormPurpose(entry.Purpose); }
            finally { _applyingPreset = false; }
            var preset = FindPreset(entry);
            SelectPreset(preset);
            SetProviderType(entry.Type);
            EditBaseUrl.Text = entry.BaseUrl ?? "";
            EditApiPath.Text = string.IsNullOrWhiteSpace(entry.ApiPath)
                ? DefaultApiPathFor(entry.Purpose, entry.ImageFormat, entry.Type)
                : entry.ApiPath;
            SelectImageFormat(entry.ImageFormat);
            EditImageEditPath.Text = string.IsNullOrWhiteSpace(entry.ImageEditPath) ? "v1/images/edits" : entry.ImageEditPath;
            EditApiKey.Password = entry.ApiKey ?? "";
            _editingModels = new(entry.Models.Select(EditableModelEntry.From));
            ModelCards.ItemsSource = _editingModels;
            TypePanel.Visibility = (preset is null || preset.Id == "custom-openai")
                ? Visibility.Visible
                : Visibility.Collapsed;
            EditorMessage.Text = string.Empty;
        }
        finally { _loadingEndpointForm = false; }
        UpdateEndpointFields();
    }

    private void ProviderPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingPreset || ProviderPresetCombo.SelectedItem is not ProviderPreset preset) return;
        ApplyPresetToForm(preset);
    }

    private void ApplyPresetToForm(ProviderPreset preset)
    {
        _loadingEndpointForm = true;
        try
        {
            EditName.Text = preset.Name;
            SetFormPurpose(preset.Purpose);
            SetProviderType(preset.Type);
            EditBaseUrl.Text = preset.BaseUrl;
            EditApiPath.Text = string.IsNullOrWhiteSpace(preset.ApiPath)
                ? DefaultApiPathFor(preset.Purpose, preset.ImageFormat, preset.Type)
                : preset.ApiPath;
            SelectImageFormat(preset.ImageFormat);
            EditImageEditPath.Text = string.IsNullOrWhiteSpace(preset.ImageEditPath) ? "v1/images/edits" : preset.ImageEditPath;
            _editingModels = new(preset.DefaultModels.Select(EditableModelEntry.From));
            ModelCards.ItemsSource = _editingModels;
            TypePanel.Visibility = preset.Id == "custom-openai"
                ? Visibility.Visible
                : Visibility.Collapsed;
            EditorMessage.Text = $"已套用「{preset.Name}」预设。填入 API Key 后点「自动获取」拉取最新模型列表。";
        }
        finally { _loadingEndpointForm = false; }
        UpdateEndpointFields();
    }

    private ProviderEntry CollectFromForm()
    {
        var purpose = CurrentFormPurpose();
        var imageProvider = SettingsViewModel.IsImagePurpose(purpose);
        var chatImage = imageProvider
            && string.Equals(SelectedImageFormat(), ImageApiFormat.OpenAiChatImage, StringComparison.OrdinalIgnoreCase);
        return new ProviderEntry(
            Id: _editing?.Id ?? Guid.NewGuid().ToString("N"),
            Type: (EditType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "openai-compat",
            Name: string.IsNullOrWhiteSpace(EditName.Text) ? "未命名" : EditName.Text,
            BaseUrl: string.IsNullOrWhiteSpace(EditBaseUrl.Text) ? null : EditBaseUrl.Text.Trim(),
            ApiKey: string.IsNullOrEmpty(EditApiKey.Password) ? null : EditApiKey.Password,
            Models: _editingModels.Select(m => imageProvider ? m.ToRecord() : m.ToRecord() with { ImageEdit = false }).ToList(),
            Enabled: true,
            SortOrder: _editing?.SortOrder ?? _vm.Providers.Count,
            Purpose: purpose,
            ApiPath: string.IsNullOrWhiteSpace(EditApiPath.Text) ? null : EditApiPath.Text.Trim(),
            ImageEditPath: imageProvider && !chatImage && !string.IsNullOrWhiteSpace(EditImageEditPath.Text)
                ? EditImageEditPath.Text.Trim()
                : null,
            ImageFormat: imageProvider ? SelectedImageFormat() : null);
    }

    private void SaveProviderClick(object sender, RoutedEventArgs e)
    {
        var entry = NormalizeImageProviderEntry(CollectFromForm());
        if (!ValidateProviderBaseUrl(entry)) return;

        _vm.Save(entry);
        UpsertProviderEntryInPlace(entry);
        _vm.RefreshVisionProviderModels();
        _vm.RefreshImageGenerationProviderModels();
        PopulateVisionCombo();
        PopulateImageGenerationCombo();
        SelectImageGenerationModel();
        _editing = entry;

        // Push runtime registration so the model selector picks it up immediately.
        _registry.Unregister(entry.Id);
        var prov = BuildProvider(entry);
        if (prov is not null && !SettingsViewModel.IsImagePurpose(entry.Purpose))
            _registry.Register(prov);
        EditorMessage.Text = "设置已保存";
    }

    private void UpsertProviderEntryInPlace(ProviderEntry entry)
    {
        _updatingProviderSelection = true;
        try
        {
            var index = -1;
            for (var i = 0; i < _vm.Providers.Count; i++)
            {
                if (string.Equals(_vm.Providers[i].Id, entry.Id, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
                _vm.Providers[index] = entry;
            else
                _vm.Providers.Add(entry);

            ProviderList.SelectedItem = entry;
        }
        finally
        {
            _updatingProviderSelection = false;
        }
    }

    private void DeleteProviderClick(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        _vm.Delete(_editing.Id);
        _registry.Unregister(_editing.Id);
        _vm.RefreshVisionProviderModels();
        _vm.RefreshImageGenerationProviderModels();
        PopulateVisionCombo();
        PopulateImageGenerationCombo();
        _editing = null;
        ProviderEditor.IsEnabled = false;
    }

    private void AddModelClick(object sender, RoutedEventArgs e)
    {
        _editingModels.Add(new EditableModelEntry
        {
            Id = "new-model",
            DisplayName = "新模型",
            ImageEdit = SettingsViewModel.IsImagePurpose(CurrentFormPurpose()) && LooksLikeImageEditModel("new-model")
        });
    }

    private void RemoveModelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not EditableModelEntry model) return;
        _editingModels.Remove(model);
    }

    private void ModelCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not EditableModelEntry model) return;
        var preset = ProviderPresetCombo.SelectedItem as ProviderPreset;
        var isCustom = preset is null || preset.Id == "custom-openai";
        var dialog = new ModelConfigDialog();
        dialog.ShowSingleEdit(
            model,
            this,
            isCustomProvider: isCustom,
            isImageProvider: SettingsViewModel.IsImagePurpose(CurrentFormPurpose()));
    }

    private async void TestProviderClick(object sender, RoutedEventArgs e)
    {
        var entry = CollectFromForm();
        EditorMessage.Text = "测试中…";
        try
        {
            if (!ValidateProviderBaseUrl(entry)) return;

            if (SettingsViewModel.IsImagePurpose(entry.Purpose))
            {
                await TestImageProviderAsync(entry);
                return;
            }

            using var http = _byokHttpFactory();
            var baseUrl = NetworkSecurity.RequireHttpsBaseUrl(entry.BaseUrl ?? DefaultBaseUrl(entry.Type), $"{entry.Name} 接入地址");
            var defaultPath = entry.Type == "anthropic" ? "v1/messages" : "v1/chat/completions";
            var url = NetworkSecurity.CombineEndpoint(
                baseUrl, string.IsNullOrWhiteSpace(entry.ApiPath) ? defaultPath : entry.ApiPath, $"{entry.Name} 接入地址");

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            object body = entry.Type == "anthropic"
                ? new { model = entry.Models.FirstOrDefault()?.Id ?? "claude-3-5-haiku-20241022", max_tokens = 8, messages = new[] { new { role = "user", content = "ping" } } }
                : new { model = entry.Models.FirstOrDefault()?.Id ?? "gpt-4o-mini", messages = new[] { new { role = "user", content = "ping" } }, max_tokens = 4 };
            req.Content = JsonContent.Create(body);
            if (entry.Type == "anthropic")
            {
                req.Headers.Add("x-api-key", entry.ApiKey ?? "");
                req.Headers.Add("anthropic-version", AnthropicProvider.AnthropicVersion);
            }
            else
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.ApiKey ?? "");
            }
            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
                EditorMessage.Text = "✅ 连接正常";
            else
                EditorMessage.Text = $"❌ HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}";
        }
        catch (Exception ex)
        {
            EditorMessage.Text = $"❌ {ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task TestImageProviderAsync(ProviderEntry entry)
    {
        var options = new ImageGenerationOptions(
            Enabled: true,
            BaseUrl: entry.BaseUrl,
            ApiKey: entry.ApiKey,
            Model: entry.Models.FirstOrDefault()?.Id,
            Size: "1024x1024",
            Style: null,
            AsTool: false,
            SupportsEdit: false,
            Format: entry.ImageFormat,
            GenerationPath: entry.ApiPath,
            EditPath: entry.ImageEditPath);
        var tool = new ImageGenerationTool(_byokHttpFactory);
        var images = await tool.GenerateAsync(options, "a single small red dot on a white background", CancellationToken.None);
        EditorMessage.Text = images.Count > 0 ? "连接成功，图像服务可用" : "连接成功，但未返回图片";
    }

    private async void DetectModelsClick(object sender, RoutedEventArgs e)
    {
        var entry = CollectFromForm();
        EditorMessage.Text = "正在获取模型列表…";
        DetectModelsButton.IsEnabled = false;
        try
        {
            if (!ValidateProviderBaseUrl(entry)) return;

            var models = await FetchModelListAsync(entry);
            if (models.Count == 0)
            {
                EditorMessage.Text = "未获取到模型，请检查 API Key 或接入地址";
                return;
            }

            var existingIds = _editingModels.Select(m => m.Id).ToList();
            var dialog = new ModelConfigDialog();
            var selected = dialog.ShowBatchDetect(models, existingIds, this);
            if (selected is null || selected.Count == 0)
            {
                EditorMessage.Text = "未选择模型";
                return;
            }

            foreach (var m in selected)
                _editingModels.Add(EditableModelEntry.From(m));
            EditorMessage.Text = $"已添加 {selected.Count} 个模型，记得保存";

            if (entry.Purpose == "image")
            {
                _vm.RefreshImageGenerationProviderModels();
                PopulateImageGenerationCombo();
                SelectImageGenerationModel();
                UpdateImageGenerationStatusHint();
            }
        }
        catch (Exception ex)
        {
            EditorMessage.Text = $"获取失败：{ex.Message}";
        }
        finally
        {
            DetectModelsButton.IsEnabled = true;
        }
    }

    private async Task<List<ProviderModelEntry>> FetchModelListAsync(ProviderEntry entry)
    {
        var preset = FindPreset(entry);
        var baseUrl = NetworkSecurity.RequireHttpsBaseUrl(entry.BaseUrl ?? DefaultBaseUrl(entry.Type), $"{entry.Name} 接入地址");
        var modelsPath = preset?.ModelsPath ?? "v1/models";
        var url = new Uri(new Uri(baseUrl), modelsPath);

        using var http = _byokHttpFactory();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (entry.Type == "anthropic")
        {
            req.Headers.Add("x-api-key", entry.ApiKey ?? "");
            req.Headers.Add("anthropic-version", AnthropicProvider.AnthropicVersion);
        }
        else if (!string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.ApiKey);
        }

        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var models = entry.Purpose == "image"
            ? ParseOpenAiImageModels(doc.RootElement)
            : IsOpenRouter(entry)
                ? ParseOpenRouterModels(doc.RootElement)
                : ParseOpenAiCompatibleModels(doc.RootElement);

        var thinkingKind = preset?.DefaultThinkingKind ?? InferThinkingKind(entry.Type);
        if (thinkingKind != ThinkingParamKind.None)
        {
            var kindName = thinkingKind.ToString();
            models = models.Select(m => m.Thinking
                && string.IsNullOrWhiteSpace(m.ThinkingParamKind)
                ? m with { ThinkingParamKind = kindName }
                : m).ToList();
        }

        return models;
    }

    private static ThinkingParamKind InferThinkingKind(string type) => type switch
    {
        "openai" or "openai-response" => ThinkingParamKind.OpenAiReasoningEffort,
        "anthropic" => ThinkingParamKind.AnthropicAdaptive,
        "gemini" => ThinkingParamKind.GeminiThinkingLevel,
        _ => ThinkingParamKind.None
    };

    private IChatProvider? BuildProvider(ProviderEntry entry)
    {
        var http = _byokHttpFactory();
        var models = entry.Models.Select(ToProviderModel).ToList();
        return entry.Type switch
        {
            "openai" or "openai-compat" or "openai-response" =>
                new OpenAICompatibleProvider(entry.Id, entry.Name, entry.BaseUrl ?? OpenAIProvider.DefaultBaseUrl, entry.ApiKey ?? "", models, http, _toolHost)
                    { ChatPath = OpenAICompatibleProvider.ResolveChatPath(entry.ApiPath) },
            "anthropic" => new AnthropicProvider(entry.Id, entry.Name, entry.ApiKey ?? "", models, http, entry.BaseUrl)
                { MessagesPath = string.IsNullOrWhiteSpace(entry.ApiPath) ? "v1/messages" : entry.ApiPath.Trim() },
            "gemini" => GeminiProvider.Create(entry.Id, entry.Name, entry.ApiKey ?? "", models, http, entry.BaseUrl, entry.ApiPath),
            _ => null
        };
    }

    private bool ValidateProviderBaseUrl(ProviderEntry entry)
    {
        try
        {
            NetworkSecurity.RequireHttpsBaseUrl(entry.BaseUrl ?? DefaultBaseUrl(entry.Type), $"{entry.Name} 接入地址");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            EditorMessage.Text = "接入地址必须以 https:// 开头";
            return false;
        }
    }

    private static string DefaultBaseUrl(string type) => type switch
    {
        "openai" or "openai-response" => OpenAIProvider.DefaultBaseUrl,
        "anthropic" => AnthropicProvider.DefaultBaseUrl,
        "gemini" => GeminiProvider.DefaultBaseUrl,
        _ => "https://api.openai.com/"
    };

    private static List<ProviderModelEntry> ParseOpenAiImageModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new();

        var models = new List<ProviderModelEntry>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String) continue;
            var id = idNode.GetString();
            if (string.IsNullOrWhiteSpace(id) || !LooksLikeImageModel(id)) continue;

            models.Add(new ProviderModelEntry(
                id,
                BeautifyModelName(id),
                ImageEdit: LooksLikeImageEditModel(id)));
        }

        return models.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static ProviderEntry NormalizeImageProviderEntry(ProviderEntry entry) =>
        IsImageProviderEntry(entry)
            ? entry with { Purpose = "image" }
            : entry;

    private static ProviderModel ToProviderModel(ProviderModelEntry entry)
    {
        ThinkingConfig? thinkingConfig = null;
        var kindStr = entry.ThinkingParamKind;
        if (entry.Thinking && string.IsNullOrWhiteSpace(kindStr))
        {
            var inferred = ThinkingParamKindInference.InferFromModelId(entry.Id);
            if (inferred != ThinkingParamKind.None)
                kindStr = inferred.ToString();
        }

        if (kindStr is { })
        {
            if (Enum.TryParse<ThinkingParamKind>(kindStr, true, out var kind))
            {
                thinkingConfig = new ThinkingConfig(
                    kind,
                    MinBudget: entry.ThinkingBudgetMin,
                    MaxBudget: entry.ThinkingBudgetMax,
                    DefaultBudget: entry.ThinkingBudgetDefault,
                    DefaultEffort: entry.DefaultEffort);
            }
        }

        return new(
            entry.Id,
            NormalizeAutoModelDisplayName(entry.Id, entry.DisplayName),
            SupportsVision: entry.Vision,
            SupportsThinking: entry.Thinking,
            SupportsReasoningEffort: entry.ReasoningEffort,
            SupportsToolCalling: entry.Tools,
            ContextWindow: entry.ContextWindow,
            ThinkingConfig: thinkingConfig);
    }

    private static List<ProviderModelEntry> ParseOpenAiCompatibleModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new();

        var models = new List<ProviderModelEntry>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String) continue;
            var id = idNode.GetString();
            if (string.IsNullOrWhiteSpace(id) || !LooksLikeChatModel(id)) continue;

            var thinkingKind = ThinkingParamKindInference.InferFromModelId(id);
            models.Add(new ProviderModelEntry(
                id,
                BeautifyModelName(id),
                Vision: LooksLikeVisionModel(id),
                Thinking: LooksLikeReasoningModel(id),
                ReasoningEffort: LooksLikeReasoningModel(id),
                Tools: LooksLikeToolModel(id),
                ThinkingParamKind: thinkingKind == ThinkingParamKind.None ? null : thinkingKind.ToString()));
        }

        return models
            .OrderByDescending(m => m.Tools)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ProviderModelEntry> ParseOpenRouterModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new();

        var models = new List<ProviderModelEntry>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String
                ? idNode.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var name = item.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                ? nameNode.GetString()
                : BeautifyModelName(id);
            var parameters = ReadStringArray(item, "supported_parameters");
            var modalities = ReadStringArray(item, "architecture", "input_modalities");
            var contextWindow = item.TryGetProperty("context_length", out var contextNode) && contextNode.ValueKind == JsonValueKind.Number
                ? contextNode.GetInt32()
                : (int?)null;

            var supportsReasoning = parameters.Any(p => IsAny(p, "reasoning", "reasoning_effort", "reasoning_effort_max"))
                || LooksLikeReasoningModel(id);
            models.Add(new ProviderModelEntry(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name!,
                Vision: modalities.Any(m => IsAny(m, "image", "vision")) || LooksLikeVisionModel(id),
                ContextWindow: contextWindow,
                Thinking: supportsReasoning,
                ReasoningEffort: supportsReasoning && parameters.Any(p => p.Contains("effort", StringComparison.OrdinalIgnoreCase)),
                Tools: parameters.Any(p => IsAny(p, "tools", "tool_choice"))));
        }

        return models
            .OrderByDescending(m => m.Tools)
            .ThenByDescending(m => m.Thinking)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
                return Array.Empty<string>();
        }
        if (current.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return current.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static bool LooksLikeChatModel(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.Contains("embedding") || lower.Contains("moderation") || lower.Contains("tts")
            || lower.Contains("transcribe") || lower.Contains("whisper") || lower.Contains("dall-e")
            || lower.Contains("image") || lower.Contains("audio") || lower.Contains("realtime"))
            return false;
        return lower.StartsWith("gpt-", StringComparison.Ordinal)
            || lower.StartsWith("o1", StringComparison.Ordinal)
            || lower.StartsWith("o3", StringComparison.Ordinal)
            || lower.StartsWith("o4", StringComparison.Ordinal)
            || lower.StartsWith("deepseek", StringComparison.Ordinal)
            || lower.StartsWith("moonshot", StringComparison.Ordinal)
            || lower.StartsWith("qwen", StringComparison.Ordinal)
            || lower.StartsWith("gemini", StringComparison.Ordinal)
            || lower.StartsWith("claude", StringComparison.Ordinal)
            || lower.Contains("chat", StringComparison.Ordinal);
    }

    private static bool LooksLikeReasoningModel(string id)
    {
        var lower = id.ToLowerInvariant();
        return lower.StartsWith("o1", StringComparison.Ordinal)
            || lower.StartsWith("o3", StringComparison.Ordinal)
            || lower.StartsWith("o4", StringComparison.Ordinal)
            || lower.StartsWith("gpt-5", StringComparison.Ordinal)
            || lower.Contains("reasoning", StringComparison.Ordinal)
            || lower.Contains("deepseek-r1", StringComparison.Ordinal)
            || lower.Contains("deepseek-reasoner", StringComparison.Ordinal)
            || lower.Contains("qwq", StringComparison.Ordinal)
            || lower.Contains("qwen3", StringComparison.Ordinal)
            || lower.Contains("gemini-2.5", StringComparison.Ordinal)
            || lower.Contains("gemini-3", StringComparison.Ordinal);
    }

    private static bool LooksLikeVisionModel(string id)
    {
        var lower = id.ToLowerInvariant();
        return lower.Contains("vision", StringComparison.Ordinal)
            || lower.Contains("gpt-4o", StringComparison.Ordinal)
            || lower.Contains("gpt-4.1", StringComparison.Ordinal)
            || lower.Contains("gpt-5", StringComparison.Ordinal)
            || lower.Contains("gemini", StringComparison.Ordinal)
            || lower.Contains("claude-3", StringComparison.Ordinal)
            || lower.Contains("claude-sonnet-4", StringComparison.Ordinal)
            || lower.Contains("claude-opus-4", StringComparison.Ordinal)
            || lower.Contains("claude-haiku-4", StringComparison.Ordinal)
            || lower.Contains("deepseek-chat", StringComparison.Ordinal)
            || lower.Contains("qwen-vl", StringComparison.Ordinal);
    }

    private static bool LooksLikeImageModel(string id)
    {
        var lower = id.ToLowerInvariant();
        return lower.Contains("dall-e", StringComparison.Ordinal)
            || lower.Contains("image", StringComparison.Ordinal)
            || lower.Contains("flux", StringComparison.Ordinal)
            || lower.Contains("midjourney", StringComparison.Ordinal)
            || lower.Contains("sdxl", StringComparison.Ordinal)
            || lower.Contains("stable-diffusion", StringComparison.Ordinal)
            || lower.Contains("recraft", StringComparison.Ordinal);
    }

    private static bool LooksLikeImageEditModel(string id)
    {
        var lower = id.ToLowerInvariant();
        return lower.Contains("gpt-image", StringComparison.Ordinal)
            || lower.Contains("gpt image", StringComparison.Ordinal)
            || lower.Contains("imagen", StringComparison.Ordinal)
            || lower.Contains("edit", StringComparison.Ordinal);
    }

    private static bool LooksLikeToolModel(string id)
    {
        var lower = id.ToLowerInvariant();
        return LooksLikeChatModel(id)
            && !lower.Contains("instruct", StringComparison.Ordinal)
            && !lower.Contains("base", StringComparison.Ordinal);
    }

    private static string BeautifyModelName(string id)
    {
        var name = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        return name.Replace('_', ' ');
    }

    private static string NormalizeAutoModelDisplayName(string id, string displayName)
    {
        var trimmed = (displayName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
            return BeautifyModelName(id);

        return string.Equals(trimmed, LegacyBeautifyModelName(id), StringComparison.Ordinal)
            ? BeautifyModelName(id)
            : trimmed;
    }

    private static string LegacyBeautifyModelName(string id)
    {
        var name = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        return name.Replace('-', ' ').Replace('_', ' ');
    }

    private static bool IsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

    private static bool IsOpenRouter(ProviderEntry entry) =>
        (entry.BaseUrl ?? string.Empty).Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase)
        || entry.Name.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase);

    private static bool IsImageProviderEntry(ProviderEntry entry) =>
        SettingsViewModel.IsImagePurpose(entry.Purpose)
        || string.Equals(entry.Name, "OpenAI 图像", StringComparison.OrdinalIgnoreCase)
        || string.Equals(entry.Name, "OpenAI Images", StringComparison.OrdinalIgnoreCase);

    private static ProviderPreset? FindPreset(ProviderEntry entry)
    {
        var purpose = SettingsViewModel.IsImagePurpose(entry.Purpose) ? "image" : "chat";
        return PresetsForPurpose(purpose).FirstOrDefault(p =>
            string.Equals(p.Type, entry.Type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeUrl(p.BaseUrl), NormalizeUrl(entry.BaseUrl), StringComparison.OrdinalIgnoreCase))
            ?? PresetsForPurpose(purpose).FirstOrDefault(p => entry.Name.Contains(p.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectPreset(ProviderPreset? preset)
    {
        _applyingPreset = true;
        try { ProviderPresetCombo.SelectedItem = preset; }
        finally { _applyingPreset = false; }
    }

    private static string NormalizeUrl(string? url) => (url ?? string.Empty).Trim().TrimEnd('/');

    private void SetProviderType(string type)
    {
        var normalized = type switch
        {
            "openai" => "openai-compat",
            _ => type
        };
        foreach (var item in EditType.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                EditType.SelectedItem = item;
                return;
            }
        }
        EditType.SelectedIndex = 0;
    }

    // ---- Purpose (用途: chat | image) form helpers ----

    private string CurrentFormPurpose() =>
        _editingPurpose;

    private void SetFormPurpose(string? purpose)
    {
        var target = SettingsViewModel.IsImagePurpose(purpose) ? "image" : "chat";
        _editingPurpose = target;
        SetPresetItemsForPurpose(target);
        ApplyPurposeProtocolFilter(target);
    }

    private void SetPresetItemsForPurpose(string purpose)
    {
        var selectedId = (ProviderPresetCombo.SelectedItem as ProviderPreset)?.Id;
        var presets = PresetsForPurpose(purpose).ToArray();
        ProviderPresetCombo.ItemsSource = presets;
        ProviderPresetCombo.SelectedItem = presets.FirstOrDefault(p => p.Id == selectedId)
                                           ?? presets.FirstOrDefault();
    }

    private static IEnumerable<ProviderPreset> PresetsForPurpose(string? purpose)
    {
        var image = SettingsViewModel.IsImagePurpose(purpose);
        return ProviderPresets.Where(p => SettingsViewModel.IsImagePurpose(p.Purpose) == image);
    }

    // Only OpenAI-compatible protocol can currently back an image service, so
    // when the purpose is image we restrict the protocol dropdown to it and
    // force the selection there; chat purpose re-enables every protocol.
    private void ApplyPurposeProtocolFilter(string purpose)
    {
        var imageOnly = SettingsViewModel.IsImagePurpose(purpose);
        ComboBoxItem? firstVisible = null;
        foreach (var item in EditType.Items.OfType<ComboBoxItem>())
        {
            var tag = item.Tag?.ToString();
            var allowed = !imageOnly || tag is "openai-compat" or "openai-response";
            item.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
            item.IsEnabled = allowed;
            if (allowed) firstVisible ??= item;
        }
        if (imageOnly && EditType.SelectedItem is ComboBoxItem cur
            && cur.Visibility == Visibility.Collapsed)
        {
            EditType.SelectedItem = firstVisible;
        }
    }

    // ---- Endpoint / API path form helpers ----

    private string SelectedImageFormat() =>
        (EditImageFormat.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ImageApiFormat.OpenAiImages;

    private void SelectImageFormat(string? format)
    {
        var target = ImageApiFormat.IsChatImage(format) ? ImageApiFormat.OpenAiChatImage : ImageApiFormat.OpenAiImages;
        foreach (var item in EditImageFormat.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                EditImageFormat.SelectedItem = item;
                return;
            }
        }
        EditImageFormat.SelectedIndex = 0;
    }

    private static string DefaultApiPathFor(string? purpose, string? imageFormat, string? type)
    {
        if (SettingsViewModel.IsImagePurpose(purpose))
            return ImageApiFormat.IsChatImage(imageFormat) ? "v1/chat/completions" : "v1/images/generations";
        return string.Equals(type, "anthropic", StringComparison.OrdinalIgnoreCase) ? "v1/messages" : "v1/chat/completions";
    }

    private void EndpointInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingEndpointForm) return;
        UpdateEndpointPreview();
    }

    private void ImageFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEndpointForm) return;
        var chatImage = string.Equals(SelectedImageFormat(), ImageApiFormat.OpenAiChatImage, StringComparison.OrdinalIgnoreCase);

        // Re-default the generation path when it still holds the other format's
        // default, so switching format doesn't leave a stale path. A genuinely
        // custom path (not one of the known defaults) is left untouched.
        var known = new[] { "v1/chat/completions", "v1/images/generations" };
        var cur = EditApiPath.Text?.Trim() ?? string.Empty;
        if (cur.Length == 0 || known.Contains(cur, StringComparer.OrdinalIgnoreCase))
            EditApiPath.Text = chatImage ? "v1/chat/completions" : "v1/images/generations";
        if (!chatImage && string.IsNullOrWhiteSpace(EditImageEditPath.Text))
            EditImageEditPath.Text = "v1/images/edits";

        UpdateEndpointFields();
    }

    private void UpdateEndpointFields()
    {
        if (EndpointPreview is null) return;   // early TextChanged during InitializeComponent
        var image = SettingsViewModel.IsImagePurpose(CurrentFormPurpose());
        ApiPathLabel.Text = image ? "生成路径" : "对话路径";
        ImageFormatPanel.Visibility = image ? Visibility.Visible : Visibility.Collapsed;

        var chatImage = image
            && string.Equals(SelectedImageFormat(), ImageApiFormat.OpenAiChatImage, StringComparison.OrdinalIgnoreCase);
        // chat-image edits run through the generation endpoint, so the separate
        // edit-path field is irrelevant for that format.
        ImageEditPathPanel.Visibility = image && !chatImage ? Visibility.Visible : Visibility.Collapsed;

        UpdateEndpointPreview();
    }

    private void UpdateEndpointPreview()
    {
        if (EndpointPreview is null) return;
        var baseUrl = EditBaseUrl.Text?.Trim() ?? string.Empty;
        if (baseUrl.Length == 0)
        {
            EndpointPreview.Text = string.Empty;
            return;
        }

        string Join(string? path, string fallback) =>
            baseUrl.TrimEnd('/') + "/" + (string.IsNullOrWhiteSpace(path) ? fallback : path!.Trim()).TrimStart('/');

        if (!SettingsViewModel.IsImagePurpose(CurrentFormPurpose()))
        {
            var fallback = (EditType.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "anthropic"
                ? "v1/messages"
                : "v1/chat/completions";
            EndpointPreview.Text = $"实际请求地址：{Join(EditApiPath.Text, fallback)}";
            return;
        }

        var chatImage = string.Equals(SelectedImageFormat(), ImageApiFormat.OpenAiChatImage, StringComparison.OrdinalIgnoreCase);
        var gen = Join(EditApiPath.Text, chatImage ? "v1/chat/completions" : "v1/images/generations");
        EndpointPreview.Text = chatImage
            ? $"生成 / 编辑地址：{gen}"
            : $"生成地址：{gen}\n编辑地址：{Join(EditImageEditPath.Text, "v1/images/edits")}";
    }

    private sealed record ProviderPreset(
        string Id,
        string Name,
        string Type,
        string BaseUrl,
        string ModelsPath,
        ThinkingParamKind DefaultThinkingKind,
        IReadOnlyList<ProviderModelEntry> DefaultModels,
        string Purpose = "chat",
        string? ApiPath = null,           // chat: 对话路径; image: 生成路径
        string? ImageEditPath = null,     // image(openai-images): 编辑路径
        string? ImageFormat = null);      // image: openai-images|openai-chat-image

    // ================================================================
    // Persona tab handlers
    // ================================================================

    private void PersonaSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPersonaForm) return;

        // Auto-save the previously selected persona before switching away,
        // so users don't lose edits by misclicking another row.
        if (_editingPersona is not null && !_editingPersonaIsDraft)
            _personas.Save(_editingPersona);

        _editingPersonaIsDraft = false;
        _editingPersona = PersonaList.SelectedItem as PersonaItemViewModel;
        LoadPersonaForm(_editingPersona);
    }

    private void LoadPersonaForm(PersonaItemViewModel? persona)
    {
        if (persona is null)
        {
            PersonaEditPanel.Visibility = Visibility.Collapsed;
            PersonaEmptyHint.Visibility = Visibility.Visible;
            DuplicatePersonaButton.IsEnabled = false;
            DeletePersonaButton.IsEnabled = false;
            SavePersonaButton.IsEnabled = false;
            return;
        }
        _loadingPersonaForm = true;
        try
        {
            PersonaEditPanel.Visibility = Visibility.Visible;
            PersonaEmptyHint.Visibility = Visibility.Collapsed;
            PersonaEditPanel.DataContext = persona;

            // Built-in personas: lock editing to encourage the duplicate flow.
            var editable = !persona.IsBuiltin;
            var isDraft = _editingPersonaIsDraft && ReferenceEquals(persona, _editingPersona);
            PersonaNameBox.IsEnabled = editable;
            PersonaIconPicker.IsEnabled = editable;
            PersonaPromptBox.IsEnabled = editable;
            PersonaDefaultsPanel.IsEnabled = editable;
            BuiltinLockHint.Visibility = persona.IsBuiltin ? Visibility.Visible : Visibility.Collapsed;
            DuplicatePersonaButton.IsEnabled = !isDraft;
            DeletePersonaButton.IsEnabled = editable && !isDraft;
            SavePersonaButton.IsEnabled = editable;
            SavePersonaButton.Content = isDraft ? "创建角色" : "保存角色";
        }
        finally
        {
            _loadingPersonaForm = false;
        }
    }

    private void NewPersonaClick(object sender, RoutedEventArgs e)
    {
        if (_editingPersona is not null && !_editingPersonaIsDraft)
            _personas.Save(_editingPersona);
        BeginNewPersonaDraft();
    }

    private void BeginNewPersonaDraft()
    {
        _loadingPersonaForm = true;
        try
        {
            PersonaList.SelectedItem = null;
        }
        finally
        {
            _loadingPersonaForm = false;
        }

        _editingPersona = _personas.CreateBlankDraft();
        _editingPersonaIsDraft = true;
        LoadPersonaForm(_editingPersona);
        PersonaNameBox.Focus();
        PersonaNameBox.SelectAll();
    }

    private void DuplicatePersonaClick(object sender, RoutedEventArgs e)
    {
        if (_editingPersona is null || _editingPersonaIsDraft) return;
        var copy = _personas.Duplicate(_editingPersona);
        PersonaList.SelectedItem = copy;
        PersonaNameBox.Focus();
        PersonaNameBox.SelectAll();
    }

    private void DeletePersonaClick(object sender, RoutedEventArgs e)
    {
        var target = PersonaList.SelectedItem as PersonaItemViewModel ?? _editingPersona;
        if (target is null) return;
        if (_editingPersonaIsDraft && ReferenceEquals(target, _editingPersona))
        {
            _editingPersona = null;
            _editingPersonaIsDraft = false;
            LoadPersonaForm(null);
            return;
        }
        if (target.IsBuiltin) return;

        var confirm = MessageBox.Show(
            this,
            $"确认删除角色“{target.Name}”？此操作不可撤销。",
            "删除角色",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var next = _personas.Personas.FirstOrDefault(p => p.Id != target.Id);
        var deleted = false;
        _loadingPersonaForm = true;
        try
        {
            _editingPersona = null;
            _editingPersonaIsDraft = false;
            PersonaList.SelectedItem = null;
            deleted = _personas.Delete(target.Id);
        }
        finally
        {
            _loadingPersonaForm = false;
        }

        if (!deleted)
        {
            MessageBox.Show(this, "删除失败：该角色可能已经不存在，或属于内置角色。", "删除角色",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LoadPersonaForm(null);
        PersonaList.SelectedItem = next;
    }

    private void SavePersonaClick(object sender, RoutedEventArgs e)
    {
        if (_editingPersona is null || _editingPersona.IsBuiltin) return;

        PersonaNameBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        PersonaPromptBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (string.IsNullOrWhiteSpace(_editingPersona.Name))
            _editingPersona.Name = "新角色";

        _personas.Save(_editingPersona);
        _editingPersonaIsDraft = false;
        PersonaList.SelectedItem = _editingPersona;
        LoadPersonaForm(_editingPersona);
    }

    private void PersonaFormChanged(object sender, RoutedEventArgs e)
    {
        // The CheckBox triggers Click after the binding has already pushed the
        // new value into the persona — no manual capture needed. We persist
        // here so the next conversation send sees the latest defaults.
        if (_editingPersona is null || _loadingPersonaForm || _editingPersonaIsDraft) return;
        _personas.Save(_editingPersona);
    }

    /// <summary>Avatar picker click — writes the chosen Fluent icon glyph
    /// into the persona's Avatar field and persists immediately.</summary>
    private void OnPersonaIconClick(object sender, RoutedEventArgs e)
    {
        if (_editingPersona is null || _editingPersona.IsBuiltin) return;
        if (sender is not FrameworkElement fe) return;
        var glyph = fe.Tag?.ToString();
        if (string.IsNullOrEmpty(glyph)) return;
        _editingPersona.Avatar = glyph;
        if (!_editingPersonaIsDraft)
            _personas.Save(_editingPersona);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Persist any unsaved persona edits on window close.
        if (_editingPersona is not null && !_loadingPersonaForm && !_editingPersonaIsDraft)
            _personas.Save(_editingPersona);
        base.OnClosing(e);
    }
}

public sealed class EditableModelEntry : System.ComponentModel.INotifyPropertyChanged
{
    private string _id = "";
    private string _displayName = "";
    private bool _vision;
    private bool _tools;
    private bool _thinking;
    private bool _reasoningEffort;
    private int? _contextWindow;
    private string? _thinkingParamKind;
    private int? _thinkingBudgetMin;
    private int? _thinkingBudgetMax;
    private int? _thinkingBudgetDefault;
    private string? _defaultEffort;
    private string? _systemPrompt;
    private bool _imageEdit;

    public string Id { get => _id; set { _id = value; OnPropertyChanged(nameof(Id)); } }
    public string DisplayName { get => _displayName; set { _displayName = value; OnPropertyChanged(nameof(DisplayName)); } }
    public bool Vision { get => _vision; set { _vision = value; OnPropertyChanged(nameof(Vision)); } }
    public bool Tools { get => _tools; set { _tools = value; OnPropertyChanged(nameof(Tools)); } }
    public bool Thinking { get => _thinking; set { _thinking = value; OnPropertyChanged(nameof(Thinking)); } }
    public bool ReasoningEffort { get => _reasoningEffort; set { _reasoningEffort = value; OnPropertyChanged(nameof(ReasoningEffort)); } }
    public int? ContextWindow { get => _contextWindow; set { _contextWindow = value; OnPropertyChanged(nameof(ContextWindow)); } }
    public string? ThinkingParamKind { get => _thinkingParamKind; set { _thinkingParamKind = value; OnPropertyChanged(nameof(ThinkingParamKind)); } }
    public int? ThinkingBudgetMin { get => _thinkingBudgetMin; set { _thinkingBudgetMin = value; OnPropertyChanged(nameof(ThinkingBudgetMin)); } }
    public int? ThinkingBudgetMax { get => _thinkingBudgetMax; set { _thinkingBudgetMax = value; OnPropertyChanged(nameof(ThinkingBudgetMax)); } }
    public int? ThinkingBudgetDefault { get => _thinkingBudgetDefault; set { _thinkingBudgetDefault = value; OnPropertyChanged(nameof(ThinkingBudgetDefault)); } }
    public string? DefaultEffort { get => _defaultEffort; set { _defaultEffort = value; OnPropertyChanged(nameof(DefaultEffort)); } }
    public string? SystemPrompt { get => _systemPrompt; set { _systemPrompt = value; OnPropertyChanged(nameof(SystemPrompt)); } }
    public bool ImageEdit { get => _imageEdit; set { _imageEdit = value; OnPropertyChanged(nameof(ImageEdit)); } }

    public string CapabilityTags
    {
        get
        {
            var parts = new List<string>();
            if (Vision) parts.Add("视觉");
            if (Tools) parts.Add("工具调用");
            if (Thinking) parts.Add("推理");
            if (ReasoningEffort) parts.Add("推理强度");
            if (ContextWindow is { } ctx) parts.Add($"上下文 {ctx / 1000}K");
            return parts.Count > 0 ? string.Join(" · ", parts) : "标准对话";
        }
    }

    public List<CapabilityBadge> CapabilityBadges
    {
        get
        {
            var badges = new List<CapabilityBadge>();
            if (Vision) badges.Add(new("视觉", "Info"));
            if (Tools) badges.Add(new("工具调用", "Success"));
            if (Thinking) badges.Add(new("推理", "Primary"));
            if (ReasoningEffort) badges.Add(new("推理强度", "Warning"));
            if (ImageEdit) badges.Add(new("图像编辑", "Primary"));
            if (ContextWindow is { } ctx) badges.Add(new($"{ctx / 1000}K", "Muted"));
            return badges;
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public static EditableModelEntry From(ProviderModelEntry e) => new()
    {
        Id = e.Id, DisplayName = NormalizeAutoModelDisplayName(e.Id, e.DisplayName), Vision = e.Vision, Tools = e.Tools,
        Thinking = e.Thinking, ReasoningEffort = e.ReasoningEffort, ContextWindow = e.ContextWindow,
        ThinkingParamKind = e.ThinkingParamKind, ThinkingBudgetMin = e.ThinkingBudgetMin,
        ThinkingBudgetMax = e.ThinkingBudgetMax, ThinkingBudgetDefault = e.ThinkingBudgetDefault,
        DefaultEffort = e.DefaultEffort, SystemPrompt = e.SystemPrompt, ImageEdit = e.ImageEdit
    };

    public ProviderModelEntry ToRecord() => new(
        Id, DisplayName, Vision, ContextWindow, Thinking, ReasoningEffort, Tools,
        ThinkingParamKind, ThinkingBudgetMin, ThinkingBudgetMax, ThinkingBudgetDefault, DefaultEffort, SystemPrompt, ImageEdit);

    private static string NormalizeAutoModelDisplayName(string id, string displayName)
    {
        var trimmed = (displayName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
            return BeautifyModelName(id);

        return string.Equals(trimmed, LegacyBeautifyModelName(id), StringComparison.Ordinal)
            ? BeautifyModelName(id)
            : trimmed;
    }

    private static string BeautifyModelName(string id)
    {
        var name = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        return name.Replace('_', ' ');
    }

    private static string LegacyBeautifyModelName(string id)
    {
        var name = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        return name.Replace('-', ' ').Replace('_', ' ');
    }
}
public sealed record CapabilityBadge(string Label, string ColorKey);
