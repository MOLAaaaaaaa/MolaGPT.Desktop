using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using MolaGPT.ViewModels.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class SettingsWindow
{
    private readonly Dictionary<StackPanel, INameScope> _deferredPageNames = [];

    private bool HasPageContent(StackPanel page) => _deferredPageNames.ContainsKey(page);

    private T PagePart<T>(StackPanel page, string name) where T : Control =>
        _deferredPageNames[page].Get<T>(name);

    private void InitializeProviderPage()
    {
        PART_ProviderModels.ItemsSource = _providerModels;
        PART_ProviderHeaders.ItemsSource = _providerHeaders;
        _providerModels.CollectionChanged += (_, _) => RefreshProviderModelEmptyState();

        PART_AddProvider.Click += (_, _) => EditProvider(null, "chat");
        PART_AddImageProvider.Click += (_, _) => EditProvider(null, "image");
        PART_AddProviderModel.Click += (_, _) => AddProviderModel();
        PART_AddProviderHeader.Click += (_, _) => _providerHeaders.Add(new HeaderRow());
        PART_RevealProviderKey.Click += (_, _) =>
            PART_ProviderApiKey.PasswordChar = PART_ProviderApiKey.PasswordChar == '\0' ? '•' : '\0';
        PART_CancelProviderEdit.Click += (_, _) => CloseProviderEditor();
        PART_BackProvider.Click += (_, _) => CloseProviderEditor();
        PART_SaveProvider.Click += (_, _) => SaveProvider();
        PART_DeleteEditingProvider.Click += OnDeleteEditingProvider;
        PART_DetectProviderModels.Click += OnDetectProviderModels;
        PART_FetchEditingProviderPricing.Click += OnFetchEditingProviderPricing;
        PART_FetchAllPricing.Click += OnFetchAllPricing;
        PART_ResolvePricingConflicts.Click += OnResolvePricingConflicts;
        PART_ClosePricingResult.Click += (_, _) => PART_PricingResult.IsVisible = false;
        PART_TestProvider.Click += OnTestProvider;
        PART_ProviderBuiltinSearch.IsCheckedChanged += OnProviderBuiltinSearchChanged;
        PART_ProviderPreset.SelectionChanged += OnProviderPresetChanged;
        PART_ProviderType.SelectionChanged += OnProviderTypeChanged;
        PART_ProviderApiSource.IsCheckedChanged += OnProviderSourceChanged;
        PART_ProviderChatGptSource.IsCheckedChanged += OnProviderSourceChanged;
        InitializeChatGpt();
        PART_ProviderImageFormat.SelectionChanged += OnProviderImageFormatChanged;
        PART_ProviderBaseUrl.TextChanged += OnProviderEndpointChanged;
        PART_ProviderHttps.IsCheckedChanged += OnProviderHttpsChanged;
        PART_ProviderApiPath.TextChanged += OnProviderEndpointChanged;
        PART_ProviderImageEditPath.TextChanged += OnProviderEndpointChanged;
        PART_ProviderImageChatPath.TextChanged += OnProviderEndpointChanged;
        PART_DetectedModelSearch.TextChanged += (_, _) => RefreshDetectedModelFilter();
        PART_SelectAllDetectedModels.Click += OnSelectAllDetectedModels;
        PART_CloseDetectedModels.Click += (_, _) => CloseDetectedModels();
        PART_CancelDetectedModels.Click += (_, _) => CloseDetectedModels();
        PART_AddDetectedModels.Click += OnAddDetectedModels;
    }

    private void InitializePersonaPage()
    {
        PART_PersonaEditor.DataContext = _emptyPersona;
        PART_ContentScroll.SizeChanged += (_, _) => UpdatePersonaViewport();
        PART_PersonaPageHeader.SizeChanged += (_, _) => UpdatePersonaViewport();
        PART_PersonaModeFilter.SelectionChanged += (_, _) => RefreshPersonaLibrary();
        PART_ClearPersonaSearch.Click += (_, _) =>
        {
            PART_PersonaSearch.Clear();
            PART_PersonaSearch.Focus();
        };

        PART_PersonaList.ItemsSource = _personas.Personas;
        PART_PersonaIcons.ItemsSource = PersonaIconCatalog.All
            .Select(icon => new PersonaIconRow(icon.Glyph, icon.Label));
        LoadPersonaForm(null);
        RefreshPersonaLibrary();
    }

    private void InitializeSystemPage()
    {
        PART_TrayMinimize.IsChecked = _settings.TrayCloseBehavior == TrayCloseBehavior.MinimizeToTray;
        PART_TrayExit.IsChecked = _settings.TrayCloseBehavior == TrayCloseBehavior.Exit;
        PART_TrayMinimize.Click += (_, _) => _settings.TrayCloseBehavior = TrayCloseBehavior.MinimizeToTray;
        PART_TrayExit.Click += (_, _) => _settings.TrayCloseBehavior = TrayCloseBehavior.Exit;
    }

    private void InitializeSearchPage()
    {
        BuildSearchProviderChoices();
        PART_RevealSearchKey.Click += (_, _) =>
            PART_SearchApiKey.PasswordChar = PART_SearchApiKey.PasswordChar == '\0' ? '•' : '\0';
        PART_TestSearch.Click += OnTestSearch;
        PART_UseDetectedDeepSeek.Click += OnUseDetectedDeepSeek;
        PART_UnlinkDeepSeekSearch.Click += OnUnlinkDeepSeekSearch;
        PART_DeepSeekLinkedCombo.SelectionChanged += OnDeepSeekLinkedComboChanged;
        PART_OpenBuiltinSearchProviders.Click += (_, _) => OpenProvidersPage();
        PART_WebFetchMode.SelectionChanged += OnWebFetchModeChanged;
    }

    private void InitializeImageGenerationPage()
    {
        PART_TestImageGeneration.IsEnabled = _imageGenerationTool is not null;
        RefreshSpecializedModelChoices();
    }

    private void InitializeSandboxPage()
    {
        PART_ConfigureSandbox.IsEnabled = _pythonRuntime is not null && _piSidecar is not null;
        PART_BrowsePython.IsEnabled = _pythonRuntime is not null;
        RefreshPythonBrowseButton();
    }

    private void InitializeBrowserPage()
    {
        PART_BrowserBridgeCard.DataContext = _browserBridge;
        PART_OpenApprovalSettings.Click += (_, _) => PART_Nav.SelectedItem = PART_ApprovalNav;
    }

    private void InitializeApprovalPage()
    {
        BuildPermissionChoices();
        PART_RevokeAll.Click += (_, _) => _settings.RevokeAllToolGrants();
        PART_ApprovalSandboxSettings.Click += (_, _) => OpenSandboxPage();
        PART_ApprovalBrowserSettings.Click += (_, _) => OpenBrowserPage();
    }

    private void InitializeMcpPage()
    {
        PART_McpServers.ItemsSource = _settings.McpServers;
        PART_AddMcp.Click += (_, _) => EditMcp(null);
        PART_CancelMcp.Click += (_, _) => CloseMcpEditor();
        PART_SaveMcp.Click += (_, _) => SaveMcp();
        PART_TestMcp.Click += OnTestMcp;
        PART_McpTransport.SelectionChanged += (_, _) => ShowMcpTransportFields();
        PART_RevealMcpToken.Click += (_, _) =>
            PART_McpToken.PasswordChar = PART_McpToken.PasswordChar == '\0' ? '•' : '\0';
        RefreshMcpServers();
    }

    private void InitializeSkillsPage()
    {
        // A skill is a SKILL.md the model opens on demand, so skills work as soon
        // as the chat can open one — read_file is enough, Python is not required.
        // The page stays in the rail either way: hiding it only taught people the
        // app had no skills. When nothing can read files, the page says so.
        PART_SkillsUnreachable.IsVisible = !CanReachSkills();
        RefreshSkills();
    }

    private void InitializeAppearancePage()
    {
        PART_AccountEntryNewBadge.IsVisible = _accountEntryWasNew && _settings.ShowStatusDots;
        PART_AccountEntryCard.EffectiveViewportChanged += (_, e) =>
        {
            _accountEntryViewport = e.EffectiveViewport;
            MarkAccountEntrySeen();
        };
        BuildThemeChoices();
        BuildCornerStyleChoices();
        BuildFontScaleChoices();
    }

    private StackPanel PART_ProviderOverview => PagePart<StackPanel>(PAGE_Providers, nameof(PART_ProviderOverview));
    private Button PART_FetchAllPricing => PagePart<Button>(PAGE_Providers, nameof(PART_FetchAllPricing));
    private Button PART_AddProvider => PagePart<Button>(PAGE_Providers, nameof(PART_AddProvider));
    private Button PART_AddImageProvider => PagePart<Button>(PAGE_Providers, nameof(PART_AddImageProvider));
    private Border PART_PricingResult => PagePart<Border>(PAGE_Providers, nameof(PART_PricingResult));
    private TextBlock PART_PricingResultText => PagePart<TextBlock>(PAGE_Providers, nameof(PART_PricingResultText));
    private Button PART_ResolvePricingConflicts => PagePart<Button>(PAGE_Providers, nameof(PART_ResolvePricingConflicts));
    private Button PART_ClosePricingResult => PagePart<Button>(PAGE_Providers, nameof(PART_ClosePricingResult));
    private ItemsControl PART_ChatProviderList => PagePart<ItemsControl>(PAGE_Providers, nameof(PART_ChatProviderList));
    private Border PART_NoChatProviders => PagePart<Border>(PAGE_Providers, nameof(PART_NoChatProviders));
    private ItemsControl PART_ImageProviderList => PagePart<ItemsControl>(PAGE_Providers, nameof(PART_ImageProviderList));
    private Border PART_NoImageProviders => PagePart<Border>(PAGE_Providers, nameof(PART_NoImageProviders));
    private StackPanel PART_ProviderEditor => PagePart<StackPanel>(PAGE_Providers, nameof(PART_ProviderEditor));
    private Button PART_BackProvider => PagePart<Button>(PAGE_Providers, nameof(PART_BackProvider));
    private TextBlock PART_ProviderEditorTitle => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ProviderEditorTitle));
    private TextBlock PART_ProviderPurpose => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ProviderPurpose));
    private StackPanel PART_ProviderSourceField => PagePart<StackPanel>(PAGE_Providers, nameof(PART_ProviderSourceField));
    private RadioButton PART_ProviderApiSource => PagePart<RadioButton>(PAGE_Providers, nameof(PART_ProviderApiSource));
    private RadioButton PART_ProviderChatGptSource => PagePart<RadioButton>(PAGE_Providers, nameof(PART_ProviderChatGptSource));
    private StackPanel PART_ProviderApiOptions => PagePart<StackPanel>(PAGE_Providers, nameof(PART_ProviderApiOptions));
    private ComboBox PART_ProviderPreset => PagePart<ComboBox>(PAGE_Providers, nameof(PART_ProviderPreset));
    private ComboBox PART_ProviderType => PagePart<ComboBox>(PAGE_Providers, nameof(PART_ProviderType));
    private TextBox PART_ProviderName => PagePart<TextBox>(PAGE_Providers, nameof(PART_ProviderName));
    private StackPanel PART_ChatGptPanel => PagePart<StackPanel>(PAGE_Providers, nameof(PART_ChatGptPanel));
    private ComboBox PART_ChatGptLoginMethod => PagePart<ComboBox>(PAGE_Providers, nameof(PART_ChatGptLoginMethod));
    private TextBlock PART_ChatGptStatus => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ChatGptStatus));
    private TextBlock PART_ChatGptDetail => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ChatGptDetail));
    private Button PART_ChatGptOtherAccount => PagePart<Button>(PAGE_Providers, nameof(PART_ChatGptOtherAccount));
    private Button PART_ChatGptSignOut => PagePart<Button>(PAGE_Providers, nameof(PART_ChatGptSignOut));
    private Button PART_ChatGptSignIn => PagePart<Button>(PAGE_Providers, nameof(PART_ChatGptSignIn));
    private TextBlock PART_ChatGptError => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ChatGptError));
    private Button PART_ChatGptUsage => PagePart<Button>(PAGE_Providers, nameof(PART_ChatGptUsage));
    private Grid PART_ProviderHttpsRow => PagePart<Grid>(PAGE_Providers, nameof(PART_ProviderHttpsRow));
    private ToggleButton PART_ProviderHttps => PagePart<ToggleButton>(PAGE_Providers, nameof(PART_ProviderHttps));
    private StackPanel PART_ProviderBaseUrlField => PagePart<StackPanel>(PAGE_Providers, nameof(PART_ProviderBaseUrlField));
    private TextBox PART_ProviderBaseUrl => PagePart<TextBox>(PAGE_Providers, nameof(PART_ProviderBaseUrl));
    private StackPanel PART_ProviderApiPathField => PagePart<StackPanel>(PAGE_Providers, nameof(PART_ProviderApiPathField));
    private TextBlock PART_ProviderApiPathLabel => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ProviderApiPathLabel));
    private TextBox PART_ProviderApiPath => PagePart<TextBox>(PAGE_Providers, nameof(PART_ProviderApiPath));
    private StackPanel PART_ProviderImageFields => PagePart<StackPanel>(PAGE_Providers, nameof(PART_ProviderImageFields));
    private ComboBox PART_ProviderImageFormat => PagePart<ComboBox>(PAGE_Providers, nameof(PART_ProviderImageFormat));
    private TextBlock PART_ProviderImageFormatHint => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ProviderImageFormatHint));
    private TextBox PART_ProviderImageChatPath => PagePart<TextBox>(PAGE_Providers, nameof(PART_ProviderImageChatPath));
    private StackPanel PART_ProviderImageEditPathField => PagePart<StackPanel>(PAGE_Providers, nameof(PART_ProviderImageEditPathField));
    private TextBox PART_ProviderImageEditPath => PagePart<TextBox>(PAGE_Providers, nameof(PART_ProviderImageEditPath));
    private TextBlock PART_ProviderEndpointPreview => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ProviderEndpointPreview));
    private StackPanel PART_ProviderApiKeyField => PagePart<StackPanel>(PAGE_Providers, nameof(PART_ProviderApiKeyField));
    private TextBox PART_ProviderApiKey => PagePart<TextBox>(PAGE_Providers, nameof(PART_ProviderApiKey));
    private Button PART_RevealProviderKey => PagePart<Button>(PAGE_Providers, nameof(PART_RevealProviderKey));
    private Expander PART_ProviderHeadersField => PagePart<Expander>(PAGE_Providers, nameof(PART_ProviderHeadersField));
    private ItemsControl PART_ProviderHeaders => PagePart<ItemsControl>(PAGE_Providers, nameof(PART_ProviderHeaders));
    private Button PART_AddProviderHeader => PagePart<Button>(PAGE_Providers, nameof(PART_AddProviderHeader));
    private StackPanel PART_ProviderToolsSection => PagePart<StackPanel>(PAGE_Providers, nameof(PART_ProviderToolsSection));
    private Border PART_ProviderToolsCard => PagePart<Border>(PAGE_Providers, nameof(PART_ProviderToolsCard));
    private TextBlock PART_ProviderBuiltinSearchTitle => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ProviderBuiltinSearchTitle));
    private TextBlock PART_ProviderBuiltinSearchHint => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ProviderBuiltinSearchHint));
    private Grid PART_ProviderBuiltinSearchNeed => PagePart<Grid>(PAGE_Providers, nameof(PART_ProviderBuiltinSearchNeed));
    private TextBlock PART_ProviderBuiltinSearchNeedText => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ProviderBuiltinSearchNeedText));
    private ToggleButton PART_ProviderBuiltinSearch => PagePart<ToggleButton>(PAGE_Providers, nameof(PART_ProviderBuiltinSearch));
    private Button PART_FetchEditingProviderPricing => PagePart<Button>(PAGE_Providers, nameof(PART_FetchEditingProviderPricing));
    private Button PART_DetectProviderModels => PagePart<Button>(PAGE_Providers, nameof(PART_DetectProviderModels));
    private Button PART_AddProviderModel => PagePart<Button>(PAGE_Providers, nameof(PART_AddProviderModel));
    private Border PART_DetectedModelsPanel => PagePart<Border>(PAGE_Providers, nameof(PART_DetectedModelsPanel));
    private TextBlock PART_DetectedModelsTitle => PagePart<TextBlock>(PAGE_Providers, nameof(PART_DetectedModelsTitle));
    private Button PART_CloseDetectedModels => PagePart<Button>(PAGE_Providers, nameof(PART_CloseDetectedModels));
    private TextBox PART_DetectedModelSearch => PagePart<TextBox>(PAGE_Providers, nameof(PART_DetectedModelSearch));
    private CheckBox PART_SelectAllDetectedModels => PagePart<CheckBox>(PAGE_Providers, nameof(PART_SelectAllDetectedModels));
    private ListBox PART_DetectedModels => PagePart<ListBox>(PAGE_Providers, nameof(PART_DetectedModels));
    private TextBlock PART_NoDetectedModels => PagePart<TextBlock>(PAGE_Providers, nameof(PART_NoDetectedModels));
    private Button PART_CancelDetectedModels => PagePart<Button>(PAGE_Providers, nameof(PART_CancelDetectedModels));
    private Button PART_AddDetectedModels => PagePart<Button>(PAGE_Providers, nameof(PART_AddDetectedModels));
    private ItemsControl PART_ProviderModels => PagePart<ItemsControl>(PAGE_Providers, nameof(PART_ProviderModels));
    private TextBlock PART_NoProviderModels => PagePart<TextBlock>(PAGE_Providers, nameof(PART_NoProviderModels));
    private TextBlock PART_ProviderStatus => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ProviderStatus));
    private TextBlock PART_ProviderError => PagePart<TextBlock>(PAGE_Providers, nameof(PART_ProviderError));
    private Button PART_DeleteEditingProvider => PagePart<Button>(PAGE_Providers, nameof(PART_DeleteEditingProvider));
    private Button PART_TestProvider => PagePart<Button>(PAGE_Providers, nameof(PART_TestProvider));
    private Button PART_SaveProvider => PagePart<Button>(PAGE_Providers, nameof(PART_SaveProvider));
    private Button PART_CancelProviderEdit => PagePart<Button>(PAGE_Providers, nameof(PART_CancelProviderEdit));
    private Grid PART_PersonaPageHeader => PagePart<Grid>(PAGE_Personas, nameof(PART_PersonaPageHeader));
    private Button PART_PersonaBack => PagePart<Button>(PAGE_Personas, nameof(PART_PersonaBack));
    private TextBlock PART_PersonaPageTitle => PagePart<TextBlock>(PAGE_Personas, nameof(PART_PersonaPageTitle));
    private StackPanel PART_PersonaLibraryActions => PagePart<StackPanel>(PAGE_Personas, nameof(PART_PersonaLibraryActions));
    private Grid PART_PersonaLayout => PagePart<Grid>(PAGE_Personas, nameof(PART_PersonaLayout));
    private Grid PART_PersonaRail => PagePart<Grid>(PAGE_Personas, nameof(PART_PersonaRail));
    private TextBox PART_PersonaSearch => PagePart<TextBox>(PAGE_Personas, nameof(PART_PersonaSearch));
    private Button PART_ClearPersonaSearch => PagePart<Button>(PAGE_Personas, nameof(PART_ClearPersonaSearch));
    private ComboBox PART_PersonaModeFilter => PagePart<ComboBox>(PAGE_Personas, nameof(PART_PersonaModeFilter));
    private ListBox PART_PersonaList => PagePart<ListBox>(PAGE_Personas, nameof(PART_PersonaList));
    private TextBlock PART_PersonaEmpty => PagePart<TextBlock>(PAGE_Personas, nameof(PART_PersonaEmpty));
    private Grid PART_PersonaEditorSurface => PagePart<Grid>(PAGE_Personas, nameof(PART_PersonaEditorSurface));
    private Grid PART_PersonaEditor => PagePart<Grid>(PAGE_Personas, nameof(PART_PersonaEditor));
    private Border PART_PersonaBuiltinHint => PagePart<Border>(PAGE_Personas, nameof(PART_PersonaBuiltinHint));
    private Button PART_PersonaIconPicker => PagePart<Button>(PAGE_Personas, nameof(PART_PersonaIconPicker));
    private ItemsControl PART_PersonaIcons => PagePart<ItemsControl>(PAGE_Personas, nameof(PART_PersonaIcons));
    private TextBox PART_PersonaName => PagePart<TextBox>(PAGE_Personas, nameof(PART_PersonaName));
    private ComboBox PART_PersonaMode => PagePart<ComboBox>(PAGE_Personas, nameof(PART_PersonaMode));
    private TabControl PART_PersonaTabs => PagePart<TabControl>(PAGE_Personas, nameof(PART_PersonaTabs));
    private ComboBox PART_RoleIdentity => PagePart<ComboBox>(PAGE_Personas, nameof(PART_RoleIdentity));
    private Expander PART_RoleExtraDefinitions => PagePart<Expander>(PAGE_Personas, nameof(PART_RoleExtraDefinitions));
    private StackPanel PART_RoleManualIdentity => PagePart<StackPanel>(PAGE_Personas, nameof(PART_RoleManualIdentity));
    private TextBox PART_PersonaPrompt => PagePart<TextBox>(PAGE_Personas, nameof(PART_PersonaPrompt));
    private TextBlock PART_PersonaPromptCacheHint => PagePart<TextBlock>(PAGE_Personas, nameof(PART_PersonaPromptCacheHint));
    private Button PART_PersonaVariables => PagePart<Button>(PAGE_Personas, nameof(PART_PersonaVariables));
    private Expander PART_AlternateGreetingsExpander => PagePart<Expander>(PAGE_Personas, nameof(PART_AlternateGreetingsExpander));
    private ComboBox PART_AlternateGreetings => PagePart<ComboBox>(PAGE_Personas, nameof(PART_AlternateGreetings));
    private Button PART_EditGreeting => PagePart<Button>(PAGE_Personas, nameof(PART_EditGreeting));
    private Button PART_DeleteGreeting => PagePart<Button>(PAGE_Personas, nameof(PART_DeleteGreeting));
    private Button PART_RoleLorebooks => PagePart<Button>(PAGE_Personas, nameof(PART_RoleLorebooks));
    private ItemsControl PART_RoleBooks => PagePart<ItemsControl>(PAGE_Personas, nameof(PART_RoleBooks));
    private TextBlock PART_RoleBooksEmpty => PagePart<TextBlock>(PAGE_Personas, nameof(PART_RoleBooksEmpty));
    private StackPanel PART_SharedBooks => PagePart<StackPanel>(PAGE_Personas, nameof(PART_SharedBooks));
    private TextBlock PART_SharedBooksEmpty => PagePart<TextBlock>(PAGE_Personas, nameof(PART_SharedBooksEmpty));
    private StackPanel PART_GlobalLoreBudgetRow => PagePart<StackPanel>(PAGE_Personas, nameof(PART_GlobalLoreBudgetRow));
    private TextBlock PART_CardSummary => PagePart<TextBlock>(PAGE_Personas, nameof(PART_CardSummary));
    private ComboBox PART_RoleCompatibility => PagePart<ComboBox>(PAGE_Personas, nameof(PART_RoleCompatibility));
    private Expander PART_ImportDetails => PagePart<Expander>(PAGE_Personas, nameof(PART_ImportDetails));
    private TextBlock PART_ImportNotes => PagePart<TextBlock>(PAGE_Personas, nameof(PART_ImportNotes));
    private ComboBox PART_RoleNoteRole => PagePart<ComboBox>(PAGE_Personas, nameof(PART_RoleNoteRole));
    private StackPanel PART_ExportBookRow => PagePart<StackPanel>(PAGE_Personas, nameof(PART_ExportBookRow));
    private ComboBox PART_ExportBook => PagePart<ComboBox>(PAGE_Personas, nameof(PART_ExportBook));
    private ComboBox PART_CardExportVersion => PagePart<ComboBox>(PAGE_Personas, nameof(PART_CardExportVersion));
    private Button PART_UpdateRoleCard => PagePart<Button>(PAGE_Personas, nameof(PART_UpdateRoleCard));
    private ComboBox PART_RoleModel => PagePart<ComboBox>(PAGE_Personas, nameof(PART_RoleModel));
    private ComboBox PART_RolePromptTemplate => PagePart<ComboBox>(PAGE_Personas, nameof(PART_RolePromptTemplate));
    private StackPanel PART_PersonaDefaults => PagePart<StackPanel>(PAGE_Personas, nameof(PART_PersonaDefaults));
    private ComboBox PART_PersonaNetwork => PagePart<ComboBox>(PAGE_Personas, nameof(PART_PersonaNetwork));
    private ComboBox PART_PersonaThinking => PagePart<ComboBox>(PAGE_Personas, nameof(PART_PersonaThinking));
    private ComboBox PART_PersonaPython => PagePart<ComboBox>(PAGE_Personas, nameof(PART_PersonaPython));
    private ComboBox PART_PersonaFileTools => PagePart<ComboBox>(PAGE_Personas, nameof(PART_PersonaFileTools));
    private ComboBox PART_PersonaImageGeneration => PagePart<ComboBox>(PAGE_Personas, nameof(PART_PersonaImageGeneration));
    private ComboBox PART_PersonaMcp => PagePart<ComboBox>(PAGE_Personas, nameof(PART_PersonaMcp));
    private StackPanel PART_PersonaSampling => PagePart<StackPanel>(PAGE_Personas, nameof(PART_PersonaSampling));
    private NumericUpDown PART_RoleTemperature => PagePart<NumericUpDown>(PAGE_Personas, nameof(PART_RoleTemperature));
    private NumericUpDown PART_RoleTopP => PagePart<NumericUpDown>(PAGE_Personas, nameof(PART_RoleTopP));
    private Border PART_PersonaFooter => PagePart<Border>(PAGE_Personas, nameof(PART_PersonaFooter));
    private Button PART_DuplicatePersona => PagePart<Button>(PAGE_Personas, nameof(PART_DuplicatePersona));
    private Button PART_DeletePersona => PagePart<Button>(PAGE_Personas, nameof(PART_DeletePersona));
    private Button PART_SavePersona => PagePart<Button>(PAGE_Personas, nameof(PART_SavePersona));

    private RadioButton PART_TrayMinimize => PagePart<RadioButton>(PAGE_System, nameof(PART_TrayMinimize));
    private RadioButton PART_TrayExit => PagePart<RadioButton>(PAGE_System, nameof(PART_TrayExit));
    private Button PART_RefreshStorage => PagePart<Button>(PAGE_Storage, nameof(PART_RefreshStorage));
    private Border PART_StorageLoading => PagePart<Border>(PAGE_Storage, nameof(PART_StorageLoading));
    private TextBlock PART_StorageStatus => PagePart<TextBlock>(PAGE_Storage, nameof(PART_StorageStatus));
    private ProgressBar PART_StorageProgress => PagePart<ProgressBar>(PAGE_Storage, nameof(PART_StorageProgress));
    private StackPanel PART_StorageContent => PagePart<StackPanel>(PAGE_Storage, nameof(PART_StorageContent));
    private TextBlock PART_StorageTotal => PagePart<TextBlock>(PAGE_Storage, nameof(PART_StorageTotal));
    private Grid PART_StorageBar => PagePart<Grid>(PAGE_Storage, nameof(PART_StorageBar));
    private TextBlock PART_StorageLegend => PagePart<TextBlock>(PAGE_Storage, nameof(PART_StorageLegend));
    private Button PART_CleanAllStorage => PagePart<Button>(PAGE_Storage, nameof(PART_CleanAllStorage));
    private StackPanel PART_StorageSafeRows => PagePart<StackPanel>(PAGE_Storage, nameof(PART_StorageSafeRows));
    private StackPanel PART_StorageWorkspaceRows => PagePart<StackPanel>(PAGE_Storage, nameof(PART_StorageWorkspaceRows));
    private Button PART_ShowAllWorkspaces => PagePart<Button>(PAGE_Storage, nameof(PART_ShowAllWorkspaces));
    private TextBlock PART_StorageDeletedTitle => PagePart<TextBlock>(PAGE_Storage, nameof(PART_StorageDeletedTitle));
    private Button PART_PurgeDeleted => PagePart<Button>(PAGE_Storage, nameof(PART_PurgeDeleted));
    private StackPanel PART_StorageOtherRows => PagePart<StackPanel>(PAGE_Storage, nameof(PART_StorageOtherRows));
    private Button PART_MoveResponseRuleUp => PagePart<Button>(PAGE_PostProcessing, nameof(PART_MoveResponseRuleUp));
    private Button PART_MoveResponseRuleDown => PagePart<Button>(PAGE_PostProcessing, nameof(PART_MoveResponseRuleDown));
    private Button PART_DeleteResponseRule => PagePart<Button>(PAGE_PostProcessing, nameof(PART_DeleteResponseRule));
    private Button PART_AddResponseRule => PagePart<Button>(PAGE_PostProcessing, nameof(PART_AddResponseRule));
    private ListBox PART_ResponseRules => PagePart<ListBox>(PAGE_PostProcessing, nameof(PART_ResponseRules));
    private Border PART_ResponseRuleEditor => PagePart<Border>(PAGE_PostProcessing, nameof(PART_ResponseRuleEditor));
    private TextBox PART_ResponseRuleName => PagePart<TextBox>(PAGE_PostProcessing, nameof(PART_ResponseRuleName));
    private CheckBox PART_ResponseRuleEnabled => PagePart<CheckBox>(PAGE_PostProcessing, nameof(PART_ResponseRuleEnabled));
    private TextBlock PART_ResponseRuleExample => PagePart<TextBlock>(PAGE_PostProcessing, nameof(PART_ResponseRuleExample));
    private Expander PART_ResponseRegexEditor => PagePart<Expander>(PAGE_PostProcessing, nameof(PART_ResponseRegexEditor));
    private TextBox PART_ResponseRulePattern => PagePart<TextBox>(PAGE_PostProcessing, nameof(PART_ResponseRulePattern));
    private TextBox PART_ResponseRuleReplacement => PagePart<TextBox>(PAGE_PostProcessing, nameof(PART_ResponseRuleReplacement));
    private Button PART_SaveResponseRule => PagePart<Button>(PAGE_PostProcessing, nameof(PART_SaveResponseRule));
    private TextBlock PART_ResponseRuleError => PagePart<TextBlock>(PAGE_PostProcessing, nameof(PART_ResponseRuleError));
    private TextBox PART_ResponsePreviewInput => PagePart<TextBox>(PAGE_PostProcessing, nameof(PART_ResponsePreviewInput));
    private TextBox PART_ResponsePreviewOutput => PagePart<TextBox>(PAGE_PostProcessing, nameof(PART_ResponsePreviewOutput));
    private StackPanel PART_MemoryBody => PagePart<StackPanel>(PAGE_Memory, nameof(PART_MemoryBody));
    private StackPanel PART_MemoryDetail => PagePart<StackPanel>(PAGE_Memory, nameof(PART_MemoryDetail));
    private Expander PART_MemorySettings => PagePart<Expander>(PAGE_Memory, nameof(PART_MemorySettings));
    private ComboBox PART_MemoryBudget => PagePart<ComboBox>(PAGE_Memory, nameof(PART_MemoryBudget));

    private Border PART_DeepSeekDetectBar => PagePart<Border>(PAGE_Search, nameof(PART_DeepSeekDetectBar));
    private TextBlock PART_DeepSeekDetectText => PagePart<TextBlock>(PAGE_Search, nameof(PART_DeepSeekDetectText));
    private ComboBox PART_DeepSeekDetectCombo => PagePart<ComboBox>(PAGE_Search, nameof(PART_DeepSeekDetectCombo));
    private Button PART_UseDetectedDeepSeek => PagePart<Button>(PAGE_Search, nameof(PART_UseDetectedDeepSeek));
    private StackPanel PART_DeepSeekLinkedRow => PagePart<StackPanel>(PAGE_Search, nameof(PART_DeepSeekLinkedRow));
    private TextBlock PART_DeepSeekLinkedMask => PagePart<TextBlock>(PAGE_Search, nameof(PART_DeepSeekLinkedMask));
    private TextBlock PART_DeepSeekLinkedName => PagePart<TextBlock>(PAGE_Search, nameof(PART_DeepSeekLinkedName));
    private ComboBox PART_DeepSeekLinkedCombo => PagePart<ComboBox>(PAGE_Search, nameof(PART_DeepSeekLinkedCombo));
    private Button PART_UnlinkDeepSeekSearch => PagePart<Button>(PAGE_Search, nameof(PART_UnlinkDeepSeekSearch));
    private Grid PART_BuiltinSearchTipRow => PagePart<Grid>(PAGE_Search, nameof(PART_BuiltinSearchTipRow));
    private TextBlock PART_BuiltinSearchTipText => PagePart<TextBlock>(PAGE_Search, nameof(PART_BuiltinSearchTipText));
    private Button PART_OpenBuiltinSearchProviders => PagePart<Button>(PAGE_Search, nameof(PART_OpenBuiltinSearchProviders));
    private ComboBox PART_WebFetchMode => PagePart<ComboBox>(PAGE_Search, nameof(PART_WebFetchMode));
    private ComboBoxItem PART_WebFetchDeepSeekItem => PagePart<ComboBoxItem>(PAGE_Search, nameof(PART_WebFetchDeepSeekItem));
    private ComboBoxItem PART_WebFetchTavilyItem => PagePart<ComboBoxItem>(PAGE_Search, nameof(PART_WebFetchTavilyItem));
    private ComboBoxItem PART_WebFetchExaItem => PagePart<ComboBoxItem>(PAGE_Search, nameof(PART_WebFetchExaItem));
    private TextBlock PART_WebFetchHint => PagePart<TextBlock>(PAGE_Search, nameof(PART_WebFetchHint));
    private TextBlock PART_SearchProviderHint => PagePart<TextBlock>(PAGE_Search, nameof(PART_SearchProviderHint));
    private ComboBox PART_SearchProvider => PagePart<ComboBox>(PAGE_Search, nameof(PART_SearchProvider));
    private StackPanel PART_SearchKeyRows => PagePart<StackPanel>(PAGE_Search, nameof(PART_SearchKeyRows));
    private TextBox PART_SearchApiKey => PagePart<TextBox>(PAGE_Search, nameof(PART_SearchApiKey));
    private Button PART_RevealSearchKey => PagePart<Button>(PAGE_Search, nameof(PART_RevealSearchKey));
    private NumericUpDown PART_SearchMaxResults => PagePart<NumericUpDown>(PAGE_Search, nameof(PART_SearchMaxResults));
    private TextBlock PART_SearchStatus => PagePart<TextBlock>(PAGE_Search, nameof(PART_SearchStatus));
    private Button PART_TestSearch => PagePart<Button>(PAGE_Search, nameof(PART_TestSearch));
    private ComboBox PART_ImageGenerationModel => PagePart<ComboBox>(PAGE_ImageGeneration, nameof(PART_ImageGenerationModel));
    private TextBlock PART_ImageGenerationStatus => PagePart<TextBlock>(PAGE_ImageGeneration, nameof(PART_ImageGenerationStatus));
    private Button PART_TestImageGeneration => PagePart<Button>(PAGE_ImageGeneration, nameof(PART_TestImageGeneration));
    private ComboBox PART_VisionModel => PagePart<ComboBox>(PAGE_Vision, nameof(PART_VisionModel));
    private TextBlock PART_SandboxStatus => PagePart<TextBlock>(PAGE_Sandbox, nameof(PART_SandboxStatus));
    private TextBlock PART_SandboxDetail => PagePart<TextBlock>(PAGE_Sandbox, nameof(PART_SandboxDetail));
    private Button PART_ConfigureSandbox => PagePart<Button>(PAGE_Sandbox, nameof(PART_ConfigureSandbox));
    private TextBlock PART_PythonRuntimeStatus => PagePart<TextBlock>(PAGE_Sandbox, nameof(PART_PythonRuntimeStatus));
    private TextBlock PART_PythonRuntimeDetail => PagePart<TextBlock>(PAGE_Sandbox, nameof(PART_PythonRuntimeDetail));
    private Button PART_ResetPythonRuntime => PagePart<Button>(PAGE_Sandbox, nameof(PART_ResetPythonRuntime));
    private Button PART_BrowsePython => PagePart<Button>(PAGE_Sandbox, nameof(PART_BrowsePython));
    private TextBlock PART_PiSidecarStatus => PagePart<TextBlock>(PAGE_Sandbox, nameof(PART_PiSidecarStatus));
    private TextBlock PART_PiSidecarDetail => PagePart<TextBlock>(PAGE_Sandbox, nameof(PART_PiSidecarDetail));
    private Button PART_RemovePiSidecar => PagePart<Button>(PAGE_Sandbox, nameof(PART_RemovePiSidecar));
    private Border PART_BrowserBridgeCard => PagePart<Border>(PAGE_Browser, nameof(PART_BrowserBridgeCard));
    private Button PART_OpenApprovalSettings => PagePart<Button>(PAGE_Browser, nameof(PART_OpenApprovalSettings));
    private TextBlock PART_BrowserActivityEmpty => PagePart<TextBlock>(PAGE_Browser, nameof(PART_BrowserActivityEmpty));
    private ItemsControl PART_BrowserActivity => PagePart<ItemsControl>(PAGE_Browser, nameof(PART_BrowserActivity));
    private ComboBox PART_PythonPermission => PagePart<ComboBox>(PAGE_Approval, nameof(PART_PythonPermission));
    private ComboBox PART_McpPermission => PagePart<ComboBox>(PAGE_Approval, nameof(PART_McpPermission));
    private ComboBox PART_BrowserPermission => PagePart<ComboBox>(PAGE_Approval, nameof(PART_BrowserPermission));
    private ComboBox PART_ImagePermission => PagePart<ComboBox>(PAGE_Approval, nameof(PART_ImagePermission));
    private ComboBox PART_VisionPermission => PagePart<ComboBox>(PAGE_Approval, nameof(PART_VisionPermission));
    private Button PART_RevokeAll => PagePart<Button>(PAGE_Approval, nameof(PART_RevokeAll));
    private Button PART_ApprovalSandboxSettings => PagePart<Button>(PAGE_Approval, nameof(PART_ApprovalSandboxSettings));
    private Button PART_ApprovalBrowserSettings => PagePart<Button>(PAGE_Approval, nameof(PART_ApprovalBrowserSettings));
    private StackPanel PART_McpOverview => PagePart<StackPanel>(PAGE_Mcp, nameof(PART_McpOverview));
    private Button PART_AddMcp => PagePart<Button>(PAGE_Mcp, nameof(PART_AddMcp));
    private ItemsControl PART_McpServers => PagePart<ItemsControl>(PAGE_Mcp, nameof(PART_McpServers));
    private Border PART_NoMcpServers => PagePart<Border>(PAGE_Mcp, nameof(PART_NoMcpServers));
    private StackPanel PART_McpEditor => PagePart<StackPanel>(PAGE_Mcp, nameof(PART_McpEditor));
    private TextBlock PART_McpEditorTitle => PagePart<TextBlock>(PAGE_Mcp, nameof(PART_McpEditorTitle));
    private TextBox PART_McpName => PagePart<TextBox>(PAGE_Mcp, nameof(PART_McpName));
    private ComboBox PART_McpTransport => PagePart<ComboBox>(PAGE_Mcp, nameof(PART_McpTransport));
    private StackPanel PART_McpStdioFields => PagePart<StackPanel>(PAGE_Mcp, nameof(PART_McpStdioFields));
    private TextBox PART_McpCommand => PagePart<TextBox>(PAGE_Mcp, nameof(PART_McpCommand));
    private TextBox PART_McpArguments => PagePart<TextBox>(PAGE_Mcp, nameof(PART_McpArguments));
    private TextBox PART_McpWorkingDirectory => PagePart<TextBox>(PAGE_Mcp, nameof(PART_McpWorkingDirectory));
    private TextBox PART_McpEnvironment => PagePart<TextBox>(PAGE_Mcp, nameof(PART_McpEnvironment));
    private StackPanel PART_McpHttpFields => PagePart<StackPanel>(PAGE_Mcp, nameof(PART_McpHttpFields));
    private TextBox PART_McpUrl => PagePart<TextBox>(PAGE_Mcp, nameof(PART_McpUrl));
    private TextBox PART_McpHeader => PagePart<TextBox>(PAGE_Mcp, nameof(PART_McpHeader));
    private TextBox PART_McpToken => PagePart<TextBox>(PAGE_Mcp, nameof(PART_McpToken));
    private Button PART_RevealMcpToken => PagePart<Button>(PAGE_Mcp, nameof(PART_RevealMcpToken));
    private ComboBox PART_McpExposure => PagePart<ComboBox>(PAGE_Mcp, nameof(PART_McpExposure));
    private ToggleButton PART_McpEnabled => PagePart<ToggleButton>(PAGE_Mcp, nameof(PART_McpEnabled));
    private TextBlock PART_McpStatus => PagePart<TextBlock>(PAGE_Mcp, nameof(PART_McpStatus));
    private TextBlock PART_McpError => PagePart<TextBlock>(PAGE_Mcp, nameof(PART_McpError));
    private Button PART_TestMcp => PagePart<Button>(PAGE_Mcp, nameof(PART_TestMcp));
    private Button PART_CancelMcp => PagePart<Button>(PAGE_Mcp, nameof(PART_CancelMcp));
    private Button PART_SaveMcp => PagePart<Button>(PAGE_Mcp, nameof(PART_SaveMcp));
    private Border PART_SkillsUnreachable => PagePart<Border>(PAGE_Skills, nameof(PART_SkillsUnreachable));
    private TextBlock PART_SkillStatus => PagePart<TextBlock>(PAGE_Skills, nameof(PART_SkillStatus));
    private ItemsControl PART_BuiltinSkills => PagePart<ItemsControl>(PAGE_Skills, nameof(PART_BuiltinSkills));
    private Border PART_NoBuiltinSkills => PagePart<Border>(PAGE_Skills, nameof(PART_NoBuiltinSkills));
    private ItemsControl PART_UserSkills => PagePart<ItemsControl>(PAGE_Skills, nameof(PART_UserSkills));
    private Border PART_NoUserSkills => PagePart<Border>(PAGE_Skills, nameof(PART_NoUserSkills));

    private Button PART_AccountSecurity => PagePart<Button>(PAGE_Account, nameof(PART_AccountSecurity));
    private Button PART_AccountApiKeys => PagePart<Button>(PAGE_Account, nameof(PART_AccountApiKeys));
    private Border PART_AccountSecurityBanner => PagePart<Border>(PAGE_Account, nameof(PART_AccountSecurityBanner));
    private TextBlock PART_AccountSecurityMessage => PagePart<TextBlock>(PAGE_Account, nameof(PART_AccountSecurityMessage));
    private Button PART_AccountSecurityDetail => PagePart<Button>(PAGE_Account, nameof(PART_AccountSecurityDetail));
    private TextBlock PART_AccountStatus => PagePart<TextBlock>(PAGE_Account, nameof(PART_AccountStatus));
    private Ellipse PART_AccountBadgeDot => PagePart<Ellipse>(PAGE_Account, nameof(PART_AccountBadgeDot));
    private TextBlock PART_AccountDetail => PagePart<TextBlock>(PAGE_Account, nameof(PART_AccountDetail));
    private Button PART_AccountAction => PagePart<Button>(PAGE_Account, nameof(PART_AccountAction));
    private StackPanel PART_UsageSection => PagePart<StackPanel>(PAGE_Account, nameof(PART_UsageSection));
    private TextBlock PART_UsageTitle => PagePart<TextBlock>(PAGE_Account, nameof(PART_UsageTitle));
    private Button PART_RefreshUsage => PagePart<Button>(PAGE_Account, nameof(PART_RefreshUsage));
    private TextBlock PART_TotalRequestsLabel => PagePart<TextBlock>(PAGE_Account, nameof(PART_TotalRequestsLabel));
    private TextBlock PART_TotalRequests => PagePart<TextBlock>(PAGE_Account, nameof(PART_TotalRequests));
    private TextBlock PART_TotalTokensLabel => PagePart<TextBlock>(PAGE_Account, nameof(PART_TotalTokensLabel));
    private TextBlock PART_TotalTokens => PagePart<TextBlock>(PAGE_Account, nameof(PART_TotalTokens));
    private StackPanel PART_CreditsPanel => PagePart<StackPanel>(PAGE_Account, nameof(PART_CreditsPanel));
    private TextBlock PART_CreditsTier => PagePart<TextBlock>(PAGE_Account, nameof(PART_CreditsTier));
    private TextBlock PART_CreditsWindow => PagePart<TextBlock>(PAGE_Account, nameof(PART_CreditsWindow));
    private ProgressBar PART_CreditsProgress => PagePart<ProgressBar>(PAGE_Account, nameof(PART_CreditsProgress));
    private TextBlock PART_CreditsRefresh => PagePart<TextBlock>(PAGE_Account, nameof(PART_CreditsRefresh));
    private TextBlock PART_CreditsHint => PagePart<TextBlock>(PAGE_Account, nameof(PART_CreditsHint));
    private TextBlock PART_UsageStatus => PagePart<TextBlock>(PAGE_Account, nameof(PART_UsageStatus));
    private Expander PART_UsageModelsExpander => PagePart<Expander>(PAGE_Account, nameof(PART_UsageModelsExpander));
    private ItemsControl PART_UsageModels => PagePart<ItemsControl>(PAGE_Account, nameof(PART_UsageModels));
    private TextBlock PART_UsageEmpty => PagePart<TextBlock>(PAGE_Account, nameof(PART_UsageEmpty));
    private TextBlock PART_EstimateHint => PagePart<TextBlock>(PAGE_Account, nameof(PART_EstimateHint));
    private ToggleButton PART_SyncConversations => PagePart<ToggleButton>(PAGE_Account, nameof(PART_SyncConversations));
    private TextBlock PART_CloudSyncStatus => PagePart<TextBlock>(PAGE_Account, nameof(PART_CloudSyncStatus));
    private Button PART_SyncNow => PagePart<Button>(PAGE_Account, nameof(PART_SyncNow));
    private TextBlock PART_TracksSummary => PagePart<TextBlock>(PAGE_Account, nameof(PART_TracksSummary));
    private Button PART_OpenSupport => PagePart<Button>(PAGE_Account, nameof(PART_OpenSupport));
    private Button PART_OpenGitHubStar => PagePart<Button>(PAGE_Account, nameof(PART_OpenGitHubStar));
    private Button PART_Logout => PagePart<Button>(PAGE_Account, nameof(PART_Logout));
    private ComboBox PART_Theme => PagePart<ComboBox>(PAGE_Appearance, nameof(PART_Theme));
    private ComboBox PART_FontScale => PagePart<ComboBox>(PAGE_Appearance, nameof(PART_FontScale));
    private ComboBox PART_CornerStyle => PagePart<ComboBox>(PAGE_Appearance, nameof(PART_CornerStyle));
    private Border PART_AccountEntryCard => PagePart<Border>(PAGE_Appearance, nameof(PART_AccountEntryCard));
    private Border PART_AccountEntryNewBadge => PagePart<Border>(PAGE_Appearance, nameof(PART_AccountEntryNewBadge));
}
