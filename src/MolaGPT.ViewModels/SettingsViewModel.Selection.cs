using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;

namespace MolaGPT.ViewModels;

public sealed partial class SettingsViewModel
{
    public const string SelectionPopupPositionBottomRight = "bottom-right";
    public const string SelectionPopupPositionBottomLeft = "bottom-left";
    public const string SelectionPopupPositionTopRight = "top-right";
    public const string SelectionPopupPositionTopLeft = "top-left";
    public const string DefaultSelectionPopupPosition = SelectionPopupPositionBottomRight;

    public const string DefaultSelectionGameProcesses =
            "cs2.exe\ndota2.exe\nLeague of Legends.exe\nVALORANT.exe\nVALORANT-Win64-Shipping.exe\n" +
            "Overwatch.exe\nr5apex.exe\nTslGame.exe\nRainbowSix.exe\nRainbowSix_Vulkan.exe\n" +
            "FortniteClient-Win64-Shipping.exe\nRocketLeague.exe\nbf2042.exe\ncod.exe\n" +
            "DeltaForceClient-Win64-Shipping.exe\nCrossFire.exe\nCrossFire_x64.exe\nMarvel-Win64-Shipping.exe\n" +
            "EscapeFromTarkov.exe\nNarakaBladepoint.exe\nHaloInfinite.exe\nosu!.exe\n" +
            "TheFinals.exe\nDiscovery.exe\nDeadlock.exe\nFragPunk.exe\n" +
            "ArenaBreakoutInfinite.exe\nGenshinImpact.exe\nYuanShen.exe\nStarRail.exe\n" +
            "ZenlessZoneZero.exe\nWuthering Waves.exe\nBlackMythWukong.exe\nb1-Win64-Shipping.exe\n" +
            "eldenring.exe\nCyberpunk2077.exe\nRDR2.exe\nGTA5.exe\n" +
            "MonsterHunterWilds.exe\nMonsterHunterWorld.exe\n" +
            "DeadByDaylight-Win64-Shipping.exe\nPhasmophobia.exe\n" +
            "RustClient.exe\nPalworld-Win64-Shipping.exe\n" +
            "RobloxPlayerBeta.exe\nMinecraft.Windows.exe\n" +
            "ForzaHorizon5.exe\naces.exe";
    [ObservableProperty] private bool _selectionEnabled;
    [ObservableProperty] private bool _selectionHotkeyOnly;
    [ObservableProperty] private bool _selectionGameMode = true;
    [ObservableProperty] private bool _selectionPauseFullscreen = true;
    [ObservableProperty] private bool _selectionClipboardFallback = true;
    [ObservableProperty] private string _selectionPopupPosition = DefaultSelectionPopupPosition;
    [ObservableProperty] private string _selectionLanguage = "简体中文";
    [ObservableProperty] private string _selectionExcludedApps = "explorer.exe\nsnipaste.exe\npixpin.exe\nsharex.exe\nexcel.exe\npowerpnt.exe\nphotoshop.exe\nmstsc.exe";
    [ObservableProperty] private string _selectionGameProcesses = DefaultSelectionGameProcesses;
    [ObservableProperty] private int _selectionHotkeyModifiers = 3; // Ctrl + Alt
    [ObservableProperty] private int _selectionHotkeyKey = 0x44;
    [ObservableProperty] private string _selectionHotkeyLabel = "Ctrl + Alt + D";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionHasHotkeyError))]
    private string? _selectionHotkeyStatus;
    [ObservableProperty] private TitleProviderModelOption? _selectedSelectionModel;
    private string? _selectionProviderId;
    private string? _selectionModelId;

    public ObservableCollection<TitleProviderModelOption> SelectionModels { get; } = [];
    public bool SelectionHasHotkeyError => !string.IsNullOrEmpty(SelectionHotkeyStatus);
    public bool SelectionModelMissing => SelectedSelectionModel is not { ProviderId: { } p, ModelId: { } m }
        || _providerRegistry?.FindModel(p, m)?.Provider is not IOneShotTarget;

    private void LoadSelectionSettings()
    {
        if (_settingsRepo is null) return;
        SelectionEnabled = _settingsRepo.Get("selection_enabled") == "True";
        SelectionHotkeyOnly = _settingsRepo.Get("selection_hotkey_only") == "True";
        SelectionGameMode = _settingsRepo.Get("selection_game_mode") != "False";
        SelectionGameProcesses = _settingsRepo.Get("selection_game_processes") ?? DefaultSelectionGameProcesses;
        SelectionPauseFullscreen = _settingsRepo.Get("selection_pause_fullscreen") != "False";
        SelectionClipboardFallback = _settingsRepo.Get("selection_clipboard_fallback") != "False";
        SelectionPopupPosition = _settingsRepo.Get("selection_popup_position") ?? DefaultSelectionPopupPosition;
        SelectionLanguage = _settingsRepo.Get("selection_language") ?? "简体中文";
        SelectionExcludedApps = _settingsRepo.Get("selection_excluded_apps") ?? SelectionExcludedApps;
        if (int.TryParse(_settingsRepo.Get("selection_hotkey_modifiers"), out var modifiers)) SelectionHotkeyModifiers = modifiers;
        if (int.TryParse(_settingsRepo.Get("selection_hotkey_key"), out var key)) SelectionHotkeyKey = key;
        SelectionHotkeyLabel = _settingsRepo.Get("selection_hotkey_label") ?? "Ctrl + Alt + D";
        _selectionProviderId = _settingsRepo.Get("selection_provider_id");
        _selectionModelId = _settingsRepo.Get("selection_model_id");
    }

    private void PersistSelectionSetting(object? sender, PropertyChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var pair = e.PropertyName switch
        {
            nameof(SelectionEnabled) => ("selection_enabled", SelectionEnabled.ToString()),
            nameof(SelectionHotkeyOnly) => ("selection_hotkey_only", SelectionHotkeyOnly.ToString()),
            nameof(SelectionGameMode) => ("selection_game_mode", SelectionGameMode.ToString()),
            nameof(SelectionGameProcesses) => ("selection_game_processes", SelectionGameProcesses),
            nameof(SelectionPauseFullscreen) => ("selection_pause_fullscreen", SelectionPauseFullscreen.ToString()),
            nameof(SelectionClipboardFallback) => ("selection_clipboard_fallback", SelectionClipboardFallback.ToString()),
            nameof(SelectionPopupPosition) => ("selection_popup_position", SelectionPopupPosition),
            nameof(SelectionLanguage) => ("selection_language", SelectionLanguage),
            nameof(SelectionExcludedApps) => ("selection_excluded_apps", SelectionExcludedApps),
            nameof(SelectionHotkeyModifiers) => ("selection_hotkey_modifiers", SelectionHotkeyModifiers.ToString()),
            nameof(SelectionHotkeyKey) => ("selection_hotkey_key", SelectionHotkeyKey.ToString()),
            nameof(SelectionHotkeyLabel) => ("selection_hotkey_label", SelectionHotkeyLabel),
            _ => ((string?, string?))(null, null)
        };
        if (pair.Item1 is not null) _settingsRepo?.Set(pair.Item1, pair.Item2!);
    }

    public void RefreshSelectionModels()
    {
        if (!SelectionEnabled) return;
        var loading = _loadingSettings;
        _loadingSettings = true;
        try
        {
            SelectionModels.Clear();
            if (_providerRegistry is not null)
                foreach (var provider in _providerRegistry.Providers.OfType<PiWorkProvider>())
                    foreach (var model in provider.Models)
                        SelectionModels.Add(new(provider.Id, model.Id, $"{provider.DisplayName} / {model.DisplayName}"));
            var selected = SelectionModels.FirstOrDefault(x => x.ProviderId == _selectionProviderId && x.ModelId == _selectionModelId);
            if (selected is null && _selectionModelId is not null)
            {
                selected = new(_selectionProviderId, _selectionModelId, $"{_selectionProviderId} / {_selectionModelId}（不可用）");
                SelectionModels.Add(selected);
            }
            SelectedSelectionModel = selected;
        }
        finally { _loadingSettings = loading; }
        OnPropertyChanged(nameof(SelectionModelMissing));
    }

    partial void OnSelectedSelectionModelChanged(TitleProviderModelOption? value)
    {
        OnPropertyChanged(nameof(SelectionModelMissing));
        if (_loadingSettings || value is null) return;
        _selectionProviderId = value.ProviderId;
        _selectionModelId = value.ModelId;
        SetOrRemove("selection_provider_id", value.ProviderId);
        SetOrRemove("selection_model_id", value.ModelId);
    }

    partial void OnSelectionEnabledChanged(bool value)
    {
        if (value && !_loadingSettings) RefreshSelectionModels();
    }
}
