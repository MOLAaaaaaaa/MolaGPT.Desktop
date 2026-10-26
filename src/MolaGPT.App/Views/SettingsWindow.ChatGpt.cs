using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Models;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

/// <summary>
/// The ChatGPT row in the provider editor, where signing in takes the place of
/// an address and a key. The account is shared by every ChatGPT row.
/// </summary>
public partial class SettingsWindow
{
    private const string ChatGptType = "chatgpt";
    private CancellationTokenSource? _chatGptSignIn;
    private bool _chatGptSigningOut;

    private ChatGptAccount? ChatGpt => _piByokProviderFactory?.ChatGpt;

    private ChatGptLoginKind SelectedChatGptLoginKind => PART_ChatGptLoginMethod.SelectedIndex == 1
        ? ChatGptLoginKind.Codex : ChatGptLoginKind.Official;

    private bool EditingChatGpt =>
        _editingProviderPurpose != "image"
        && PART_ProviderChatGptSource.IsChecked == true;

    private void InitializeChatGpt()
    {
        PART_ChatGptLoginMethod.SelectedIndex = ChatGpt?.PreferredLoginKind == ChatGptLoginKind.Codex ? 1 : 0;
        PART_ChatGptLoginMethod.SelectionChanged += (_, _) =>
        {
            if (ChatGpt is { } selectedAccount) selectedAccount.PreferredLoginKind = SelectedChatGptLoginKind;
            PART_ChatGptError.IsVisible = false;
            UpdateChatGptUi();
        };
        PART_ChatGptSignIn.Click += OnChatGptSignIn;
        PART_ChatGptOtherAccount.Click += (_, _) => _ = SignInChatGptAsync(useDifferentAccount: true);
        PART_ChatGptSignOut.Click += async (_, _) =>
        {
            if (ChatGpt is not { } account || _chatGptSigningOut) return;
            _chatGptSigningOut = true;
            UpdateChatGptUi();
            try { await account.SignOutAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                PART_ChatGptError.Text = ex.Message;
                PART_ChatGptError.IsVisible = true;
            }
            finally
            {
                _chatGptSigningOut = false;
                UpdateChatGptUi();
            }
        };
        PART_ChatGptUsage.Click += (_, _) =>
        {
            if (ChatGpt is { } account) OpenExternal(account.UsageUrl);
        };

        if (ChatGpt is not { } account) return;
        EventHandler changed = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            UpdateChatGptUi();
            RefreshProviders();
        });
        account.Changed += changed;
        Closed += (_, _) =>
        {
            account.Changed -= changed;
            _chatGptSignIn?.Cancel();
        };
    }

    /// <summary>Shows the account in place of the address, key and header fields.</summary>
    private void UpdateChatGptUi()
    {
        var chatGpt = EditingChatGpt;
        PART_ProviderApiOptions.IsVisible = !chatGpt;
        PART_ChatGptPanel.IsVisible = chatGpt;
        PART_ProviderHttpsRow.IsVisible = !chatGpt;
        PART_ProviderBaseUrlField.IsVisible = !chatGpt;
        PART_ProviderApiPathField.IsVisible = !chatGpt;
        PART_ProviderEndpointPreview.IsVisible = !chatGpt;
        PART_ProviderApiKeyField.IsVisible = !chatGpt;
        PART_ProviderHeadersField.IsVisible = !chatGpt;
        PART_TestProvider.IsVisible = !chatGpt;
        if (!chatGpt) return;

        var account = ChatGpt;
        if (account is null)
        {
            PART_ChatGptStatus.Text = "不可用";
            PART_ChatGptDetail.Text = "本机没有可用的 Agent 运行环境。";
            PART_ChatGptSignIn.IsVisible = PART_ChatGptSignOut.IsVisible = false;
            PART_ChatGptOtherAccount.IsVisible = false;
            return;
        }

        var signingIn = _chatGptSignIn is not null;
        var selectedKind = SelectedChatGptLoginKind;
        var methodChanged = account.IsSignedIn && account.Kind != selectedKind;
        PART_ChatGptLoginMethod.IsEnabled = !signingIn && !_chatGptSigningOut;
        PART_ChatGptStatus.Text = _chatGptSigningOut ? "正在退出…"
            : signingIn ? "等待浏览器授权…" : account.IsSignedIn
            ? "已连接" + (account.Email is { } email ? " · " + email : string.Empty)
            : "未登录";
        PART_ChatGptDetail.Text = account.IsSignedIn
            ? account.CanUsePlan
                ? "正在使用 ChatGPT 套餐 · " + (account.Kind == ChatGptLoginKind.Codex ? "Codex OAuth" : "SIWC")
                : "尚未授权使用 ChatGPT 套餐。"
            : account.LastError ?? "登录后使用 ChatGPT 套餐的额度。";
        PART_ChatGptSignIn.IsVisible = signingIn || !account.IsSignedIn || !account.CanUsePlan || methodChanged;
        PART_ChatGptSignIn.Content = signingIn ? "取消"
            : account.IsSignedIn && !account.CanUsePlan && !methodChanged ? "授权使用套餐" : "登录 ChatGPT";
        PART_ChatGptSignIn.IsEnabled = !_chatGptSigningOut;
        PART_ChatGptSignOut.IsVisible = account.IsSignedIn && !signingIn;
        PART_ChatGptSignOut.IsEnabled = !signingIn && !_chatGptSigningOut;
        PART_ChatGptOtherAccount.IsVisible = account.IsSignedIn && !signingIn && !methodChanged;
        PART_ChatGptOtherAccount.IsEnabled = !signingIn && !_chatGptSigningOut;
        PART_ChatGptUsage.IsVisible = account.IsSignedIn;
    }

    private void OnChatGptSignIn(object? sender, RoutedEventArgs e)
    {
        if (_chatGptSignIn is { } running)
        {
            running.Cancel();
            return;
        }
        _ = SignInChatGptAsync();
    }

    private async Task SignInChatGptAsync(bool useDifferentAccount = false)
    {
        if (ChatGpt is not { } account || _chatGptSignIn is not null || _chatGptSigningOut) return;
        using var cts = new CancellationTokenSource();
        var kind = SelectedChatGptLoginKind;
        var newRegistration = useDifferentAccount || !account.IsSignedIn || account.Kind != kind;
        _chatGptSignIn = cts;
        PART_ChatGptError.IsVisible = false;
        UpdateChatGptUi();
        try
        {
            if (kind == ChatGptLoginKind.Codex)
                await account.SignInWithCodexAsync(OpenExternal, cts.Token);
            else
                await account.SignInAsync(OpenExternal, cts.Token, newRegistration);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            PART_ChatGptError.Text = ex.Message;
            PART_ChatGptError.IsVisible = true;
            return;
        }
        finally
        {
            _chatGptSignIn = null;
            UpdateChatGptUi();
        }

        if (!EditingChatGpt) return;
        if (string.IsNullOrWhiteSpace(PART_ProviderName.Text)) PART_ProviderName.Text = "ChatGPT";
        if (account.CanUsePlan) OnDetectProviderModels(null, new RoutedEventArgs());
    }

    private async Task<List<ProviderModelEntry>> FetchChatGptModelsAsync()
    {
        if (ChatGpt is not { IsSignedIn: true } account)
            throw new InvalidOperationException("请先登录 ChatGPT。");
        var models = await account.ListModelsAsync(CancellationToken.None);
        return models.Select(model =>
        {
            var levels = ThinkingEffortLevels.Normalize(model.ReasoningEfforts).ToList();
            return new ProviderModelEntry(
                model.Id,
                model.DisplayName,
                Vision: model.Vision,
                ContextWindow: model.ContextWindow,
                Thinking: true,
                ReasoningEffort: true,
                Tools: true,
                ThinkingParamKind: nameof(ThinkingParamKind.OpenAiReasoningEffort),
                EffortLevels: levels.Count > 0 ? levels : null,
                ReasoningMandatory: true,
                SupportsTemperature: false,
                SupportsTopP: false);
        }).ToList();
    }

    private static void OpenExternal(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
