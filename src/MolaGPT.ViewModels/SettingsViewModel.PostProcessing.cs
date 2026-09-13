using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Presentation;

namespace MolaGPT.ViewModels;

public sealed partial class SettingsViewModel
{
    private const string ResponsePostProcessingEnabledKey = "response_post_processing_enabled";
    private const string ResponseRegexRulesKey = "response_regex_rules";

    [ObservableProperty] private bool _responsePostProcessingEnabled = true;

    public ObservableCollection<ResponseRegexRule> ResponseRegexRules { get; } =
        new(ResponsePostProcessor.DefaultRules);

    private void LoadResponsePostProcessing()
    {
        if (bool.TryParse(_settingsRepo!.Get(ResponsePostProcessingEnabledKey), out var enabled))
            ResponsePostProcessingEnabled = enabled;

        var json = _settingsRepo.Get(ResponseRegexRulesKey);
        var rules = json is null ? ResponsePostProcessor.DefaultRules
            : JsonSerializer.Deserialize<ResponseRegexRule[]>(json)
              ?? throw new JsonException("回答后处理规则为空。");
        ResponsePostProcessor.Validate(rules);
        ResponseRegexRules.Clear();
        foreach (var rule in rules) ResponseRegexRules.Add(rule);
    }

    public void SaveResponseRegexRules(IReadOnlyList<ResponseRegexRule> rules)
    {
        ResponsePostProcessor.Validate(rules);
        var saved = rules.ToArray();
        _settingsRepo?.Set(ResponseRegexRulesKey, JsonSerializer.Serialize(saved));
        ResponseRegexRules.Clear();
        foreach (var rule in saved) ResponseRegexRules.Add(rule);
    }

    partial void OnResponsePostProcessingEnabledChanged(bool value)
    {
        if (!_loadingSettings)
            _settingsRepo?.Set(ResponsePostProcessingEnabledKey, value.ToString());
    }
}
