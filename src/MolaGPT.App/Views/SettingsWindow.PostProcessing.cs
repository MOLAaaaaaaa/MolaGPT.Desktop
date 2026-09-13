using System.Text.RegularExpressions;
using Avalonia.Controls;
using MolaGPT.Presentation;

namespace MolaGPT.App.Views;

public partial class SettingsWindow
{
    private bool _loadingResponseRule;
    private int _editingResponseRuleIndex = -1;

    private void InitializeResponsePostProcessing()
    {
        PART_ResponseRules.ItemsSource = _settings.ResponseRegexRules;
        PART_ResponseRules.SelectionChanged += (_, _) =>
        {
            if (!_loadingResponseRule) LoadResponseRule(PART_ResponseRules.SelectedIndex);
        };
        PART_AddResponseRule.Click += (_, _) =>
        {
            LoadResponseRule(-1);
            PART_ResponseRuleEditor.IsVisible = true;
            PART_ResponseRegexEditor.IsExpanded = true;
            PART_ResponseRuleName.Text = "新规则";
            PART_ResponseRulePattern.Focus();
            UpdateResponsePreview();
        };
        PART_MoveResponseRuleUp.Click += (_, _) => MoveResponseRule(-1);
        PART_MoveResponseRuleDown.Click += (_, _) => MoveResponseRule(1);
        PART_DeleteResponseRule.Click += (_, _) => DeleteResponseRule();
        PART_SaveResponseRule.Click += (_, _) => SaveResponseRule();
        PART_ResponseRuleName.TextChanged += (_, _) => UpdateResponsePreview();
        PART_ResponseRulePattern.TextChanged += (_, _) => UpdateResponsePreview();
        PART_ResponseRuleReplacement.TextChanged += (_, _) => UpdateResponsePreview();
        PART_ResponseRuleEnabled.Click += (_, _) => UpdateResponsePreview();
        PART_ResponsePreviewInput.TextChanged += (_, _) => UpdateResponsePreview();
        PART_ResponsePreviewInput.Text = "他说\"你好,世界\"。";
        LoadResponseRule(_settings.ResponseRegexRules.Count > 0 ? 0 : -1);
    }

    private void LoadResponseRule(int index)
    {
        _loadingResponseRule = true;
        _editingResponseRuleIndex = index;
        PART_ResponseRules.SelectedIndex = index;
        var rule = index >= 0 ? _settings.ResponseRegexRules[index] : null;
        PART_ResponseRuleEditor.IsVisible = rule is not null;
        PART_ResponseRuleName.Text = rule?.Name ?? string.Empty;
        PART_ResponseRulePattern.Text = rule?.Pattern ?? string.Empty;
        PART_ResponseRuleReplacement.Text = rule?.Replacement ?? string.Empty;
        PART_ResponseRuleEnabled.IsChecked = rule?.Enabled ?? true;
        PART_ResponseRegexEditor.IsExpanded = rule is not null && GetResponseRuleExample(rule) is null;
        PART_MoveResponseRuleUp.IsEnabled = index > 0;
        PART_MoveResponseRuleDown.IsEnabled = index >= 0 && index < _settings.ResponseRegexRules.Count - 1;
        PART_DeleteResponseRule.IsEnabled = index >= 0;
        _loadingResponseRule = false;
        UpdateResponsePreview();
    }

    private List<ResponseRegexRule> ReadResponseRuleDraft()
    {
        var rules = _settings.ResponseRegexRules.ToList();
        if (!PART_ResponseRuleEditor.IsVisible) return rules;

        var name = PART_ResponseRuleName.Text?.Trim() ?? string.Empty;
        if (name.Length == 0) throw new ArgumentException("请输入规则名称。");
        var pattern = PART_ResponseRulePattern.Text ?? string.Empty;
        if (pattern.Length == 0) throw new ArgumentException("请输入正则表达式。");
        var rule = new ResponseRegexRule(name, pattern,
            PART_ResponseRuleReplacement.Text ?? string.Empty,
            PART_ResponseRuleEnabled.IsChecked == true);
        if (_editingResponseRuleIndex < 0) rules.Add(rule);
        else rules[_editingResponseRuleIndex] = rule;
        return rules;
    }

    private static string? GetResponseRuleExample(ResponseRegexRule rule)
    {
        var presets = ResponsePostProcessor.DefaultRules;
        if (rule.Pattern == presets[0].Pattern && rule.Replacement == presets[0].Replacement)
            return "你好,世界 → 你好，世界";
        if (rule.Pattern == presets[1].Pattern && rule.Replacement == presets[1].Replacement)
            return "\"你好\" → “你好”";
        return null;
    }

    private void UpdateResponsePreview()
    {
        if (_loadingResponseRule) return;
        try
        {
            var rules = ReadResponseRuleDraft();
            ResponsePostProcessor.Validate(rules);
            PART_ResponsePreviewOutput.Text = ResponsePostProcessor.Apply(
                PART_ResponsePreviewInput.Text ?? string.Empty, rules);
            PART_ResponseRuleExample.Text = PART_ResponseRuleEditor.IsVisible
                ? GetResponseRuleExample(rules[_editingResponseRuleIndex < 0 ? rules.Count - 1 : _editingResponseRuleIndex])
                : null;
            PART_ResponseRuleExample.IsVisible = PART_ResponseRuleExample.Text is not null;
            PART_ResponseRuleError.IsVisible = false;
            PART_SaveResponseRule.IsEnabled = PART_ResponseRuleEditor.IsVisible;
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            PART_ResponseRuleError.Text = ex.Message;
            PART_ResponseRuleError.IsVisible = true;
            PART_ResponseRuleExample.IsVisible = false;
            PART_ResponsePreviewOutput.Text = string.Empty;
            PART_SaveResponseRule.IsEnabled = false;
        }
    }

    private void SaveResponseRule()
    {
        var rules = ReadResponseRuleDraft();
        PersistResponseRules(rules, _editingResponseRuleIndex < 0 ? rules.Count - 1 : _editingResponseRuleIndex);
    }

    private void MoveResponseRule(int direction)
    {
        var index = _editingResponseRuleIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _settings.ResponseRegexRules.Count) return;
        var rules = _settings.ResponseRegexRules.ToList();
        (rules[index], rules[target]) = (rules[target], rules[index]);
        PersistResponseRules(rules, target);
    }

    private void DeleteResponseRule()
    {
        if (_editingResponseRuleIndex < 0) return;
        var rules = _settings.ResponseRegexRules.ToList();
        rules.RemoveAt(_editingResponseRuleIndex);
        PersistResponseRules(rules, Math.Min(_editingResponseRuleIndex, rules.Count - 1));
    }

    private void PersistResponseRules(IReadOnlyList<ResponseRegexRule> rules, int selectedIndex)
    {
        _loadingResponseRule = true;
        try { _settings.SaveResponseRegexRules(rules); }
        finally { _loadingResponseRule = false; }
        LoadResponseRule(selectedIndex);
    }
}
