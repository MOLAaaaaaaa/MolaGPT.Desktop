using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Core.Chat.Tools;

namespace MolaGPT.ViewModels;

/// <summary>
/// The approval mode for this run of the app. Deliberately not persisted: full
/// access is granted for a task, and a grant that outlives a restart is one
/// nobody remembers giving. Every launch starts at automatic review.
///
/// Written on the UI thread, read from tool threads on every call.
/// </summary>
public sealed class ApprovalModeState : ObservableObject, IApprovalModeSource
{
    public const ToolPermissionMode Default = ToolPermissionMode.AutoReview;

    private int _mode = (int)Default;

    public ToolPermissionMode Mode
    {
        get => (ToolPermissionMode)Volatile.Read(ref _mode);
        set
        {
            if (Mode == value) return;
            Volatile.Write(ref _mode, (int)value);
            OnPropertyChanged();
        }
    }

    ToolPermissionMode IApprovalModeSource.Current => Mode;
}
