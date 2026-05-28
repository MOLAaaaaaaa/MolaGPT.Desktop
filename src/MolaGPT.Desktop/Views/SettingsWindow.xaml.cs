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
    private readonly CloudSyncService _cloudSync;
    private readonly ConversationListViewModel _conversationList;
    private ProviderEntry? _editing;
    private PersonaItemViewModel? _editingPersona;
    private bool _editingPersonaIsDraft;
    private bool _syncInProgress;
    private bool _applyingPreset;
    private bool _updatingWebSearchUi;
    private bool _loadingPersonaForm;

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
        Func<HttpClient> byokHttpFactory)
    {
        InitializeComponent();
        _vm = vm;
        _personas = personas;
        _auth = auth;
        _registry = registry;
        _cloudSync = cloudSync;
        _conversationList = conversationList;
        _byokHttpFactory = byokHttpFactory;
        DataContext = vm;
        ProviderPresetCombo.ItemsSource = ProviderPresets;
        // The persona tab uses _personas as its DataContext so bindings inside
        // it can target Personas (the collection) directly without going
        // through SettingsViewModel.
        PersonaTabRoot.DataContext = _personas;
        PersonaIconPicker.ItemsSource = PersonaIconCatalog.All;
        PersonaList.SelectedItem = _personas.Personas.FirstOrDefault();
        _vm.Reload();
        UpdateAccountUi();
        InitializeWebSearchUi();
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
            UpdateWebSearchStatusHint();
        }
        finally
        {
            _updatingWebSearchUi = false;
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
            StatusDetail.Text = "登录信息已加密保存在本机";
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
            _registry.Unregister("molagpt-proxy");
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
        if (ProviderList.SelectedItem is not ProviderEntry entry)
        {
            ProviderEditor.IsEnabled = false;
            return;
        }
        BeginEdit(entry);
    }

    private void AddProviderClick(object sender, RoutedEventArgs e)
    {
        var preset = ProviderPresets[0];
        var entry = new ProviderEntry(
            Id: Guid.NewGuid().ToString("N"),
            Type: preset.Type,
            Name: preset.Name,
            BaseUrl: preset.BaseUrl,
            ApiKey: "",
            Models: preset.DefaultModels.ToList(),
            Enabled: true,
            SortOrder: _vm.Providers.Count);
        _vm.Providers.Add(entry);
        ProviderList.SelectedItem = entry;
    }

    private System.Collections.ObjectModel.ObservableCollection<EditableModelEntry> _editingModels = new();

    private void BeginEdit(ProviderEntry entry)
    {
        _editing = entry;
        ProviderEditor.IsEnabled = true;
        var preset = FindPreset(entry);
        SelectPreset(preset);
        EditName.Text = entry.Name;
        SetProviderType(entry.Type);
        EditBaseUrl.Text = entry.BaseUrl ?? "";
        EditApiKey.Password = entry.ApiKey ?? "";
        _editingModels = new(entry.Models.Select(EditableModelEntry.From));
        ModelCards.ItemsSource = _editingModels;
        TypePanel.Visibility = (preset is null || preset.Id == "custom-openai")
            ? Visibility.Visible
            : Visibility.Collapsed;
        EditorMessage.Text = string.Empty;
    }

    private void ProviderPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingPreset || ProviderPresetCombo.SelectedItem is not ProviderPreset preset) return;
        ApplyPresetToForm(preset);
    }

    private void ApplyPresetToForm(ProviderPreset preset)
    {
        EditName.Text = preset.Name;
        SetProviderType(preset.Type);
        EditBaseUrl.Text = preset.BaseUrl;
        _editingModels = new(preset.DefaultModels.Select(EditableModelEntry.From));
        ModelCards.ItemsSource = _editingModels;
        TypePanel.Visibility = preset.Id == "custom-openai"
            ? Visibility.Visible
            : Visibility.Collapsed;
        EditorMessage.Text = $"已套用「{preset.Name}」预设。填入 API Key 后点「自动获取」拉取最新模型列表。";
    }

    private ProviderEntry CollectFromForm() => new(
        Id: _editing?.Id ?? Guid.NewGuid().ToString("N"),
        Type: (EditType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "openai-compat",
        Name: string.IsNullOrWhiteSpace(EditName.Text) ? "未命名" : EditName.Text,
        BaseUrl: string.IsNullOrWhiteSpace(EditBaseUrl.Text) ? null : EditBaseUrl.Text.Trim(),
        ApiKey: string.IsNullOrEmpty(EditApiKey.Password) ? null : EditApiKey.Password,
        Models: _editingModels.Select(m => m.ToRecord()).ToList(),
        Enabled: true,
        SortOrder: _editing?.SortOrder ?? _vm.Providers.Count);

    private void SaveProviderClick(object sender, RoutedEventArgs e)
    {
        var entry = CollectFromForm();
        if (!ValidateProviderBaseUrl(entry)) return;

        _vm.Save(entry);
        var existing = _vm.Providers.FirstOrDefault(p => p.Id == entry.Id);
        if (existing is not null) _vm.Providers.Remove(existing);
        _vm.Providers.Add(entry);
        _editing = entry;

        // Push runtime registration so the model selector picks it up immediately.
        var prov = BuildProvider(entry);
        if (prov is not null)
        {
            _registry.Unregister(entry.Id);
            _registry.Register(prov);
        }
        EditorMessage.Text = "设置已保存";
    }

    private void DeleteProviderClick(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        _vm.Delete(_editing.Id);
        _registry.Unregister(_editing.Id);
        _editing = null;
        ProviderEditor.IsEnabled = false;
    }

    private void AddModelClick(object sender, RoutedEventArgs e)
    {
        _editingModels.Add(new EditableModelEntry { Id = "new-model", DisplayName = "新模型" });
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
        dialog.ShowSingleEdit(model, this, isCustomProvider: isCustom);
    }

    private async void TestProviderClick(object sender, RoutedEventArgs e)
    {
        var entry = CollectFromForm();
        EditorMessage.Text = "测试中…";
        try
        {
            if (!ValidateProviderBaseUrl(entry)) return;

            using var http = _byokHttpFactory();
            var baseUrl = NetworkSecurity.RequireHttpsBaseUrl(entry.BaseUrl ?? DefaultBaseUrl(entry.Type), $"{entry.Name} 接入地址");
            var url = new Uri(new Uri(baseUrl), entry.Type == "anthropic" ? "v1/messages" : "v1/chat/completions");

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
        var baseUrl = NetworkSecurity.RequireHttpsBaseUrl(entry.BaseUrl ?? DefaultBaseUrl(entry.Type), $"{entry.Name} Base URL");
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
        var models = IsOpenRouter(entry)
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
                new OpenAICompatibleProvider(entry.Id, entry.Name, entry.BaseUrl ?? OpenAIProvider.DefaultBaseUrl, entry.ApiKey ?? "", models, http),
            "anthropic" => new AnthropicProvider(entry.Id, entry.Name, entry.ApiKey ?? "", models, http, entry.BaseUrl),
            "gemini" => GeminiProvider.Create(entry.Id, entry.Name, entry.ApiKey ?? "", models, http, entry.BaseUrl),
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
            entry.DisplayName,
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
        return name.Replace('-', ' ').Replace('_', ' ');
    }

    private static bool IsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

    private static bool IsOpenRouter(ProviderEntry entry) =>
        (entry.BaseUrl ?? string.Empty).Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase)
        || entry.Name.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase);

    private static ProviderPreset? FindPreset(ProviderEntry entry)
    {
        return ProviderPresets.FirstOrDefault(p =>
            string.Equals(p.Type, entry.Type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeUrl(p.BaseUrl), NormalizeUrl(entry.BaseUrl), StringComparison.OrdinalIgnoreCase))
            ?? ProviderPresets.FirstOrDefault(p => entry.Name.Contains(p.Name, StringComparison.OrdinalIgnoreCase));
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

    private sealed record ProviderPreset(
        string Id,
        string Name,
        string Type,
        string BaseUrl,
        string ModelsPath,
        ThinkingParamKind DefaultThinkingKind,
        IReadOnlyList<ProviderModelEntry> DefaultModels);

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
            if (ContextWindow is { } ctx) badges.Add(new($"{ctx / 1000}K", "Muted"));
            return badges;
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public static EditableModelEntry From(ProviderModelEntry e) => new()
    {
        Id = e.Id, DisplayName = e.DisplayName, Vision = e.Vision, Tools = e.Tools,
        Thinking = e.Thinking, ReasoningEffort = e.ReasoningEffort, ContextWindow = e.ContextWindow,
        ThinkingParamKind = e.ThinkingParamKind, ThinkingBudgetMin = e.ThinkingBudgetMin,
        ThinkingBudgetMax = e.ThinkingBudgetMax, ThinkingBudgetDefault = e.ThinkingBudgetDefault,
        DefaultEffort = e.DefaultEffort, SystemPrompt = e.SystemPrompt
    };

    public ProviderModelEntry ToRecord() => new(
        Id, DisplayName, Vision, ContextWindow, Thinking, ReasoningEffort, Tools,
        ThinkingParamKind, ThinkingBudgetMin, ThinkingBudgetMax, ThinkingBudgetDefault, DefaultEffort, SystemPrompt);
}
public sealed record CapabilityBadge(string Label, string ColorKey);
