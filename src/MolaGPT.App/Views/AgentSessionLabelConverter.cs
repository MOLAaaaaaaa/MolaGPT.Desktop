using System.Globalization;
using Avalonia.Data.Converters;
using MolaGPT.Core.Chat.Agents;
using MolaGPT.ViewModels.Agents;

namespace MolaGPT.App.Views;

public sealed class AgentSessionLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (string.Equals(parameter as string, "backend", StringComparison.Ordinal))
            return AgentBridgeStatusViewModel.BackendLabel(value as string ?? string.Empty);
        return value is AgentSessionPhase phase
            ? AgentBridgeStatusViewModel.PhaseLabel(phase)
            : value?.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
