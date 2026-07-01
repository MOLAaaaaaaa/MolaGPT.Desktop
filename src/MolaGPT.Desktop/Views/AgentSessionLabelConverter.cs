using System.Globalization;
using System.Windows.Data;
using MolaGPT.Core.Chat.Agents;
using MolaGPT.ViewModels.Agents;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// Localizes agent session fields for the status surface: phase enum → Chinese
/// label, and backend id → display name. Reuses the view-model's static helpers
/// so labels stay in one place.
/// </summary>
public sealed class AgentSessionLabelConverter : IValueConverter
{
    public static AgentSessionLabelConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is string kind && kind == "backend" && value is string b)
            return AgentBridgeStatusViewModel.BackendLabel(b);
        if (value is AgentSessionPhase p)
            return AgentBridgeStatusViewModel.PhaseLabel(p);
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}