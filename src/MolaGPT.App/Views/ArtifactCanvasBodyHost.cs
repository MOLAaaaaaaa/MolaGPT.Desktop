using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using MolaGPT.App.Rendering;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

/// <summary>
/// Keeps <see cref="ArtifactCanvasBody"/> pointed at the selected artifact.
///
/// The body de-duplicates on the artifact's render token, so this can forward
/// every change it hears about without the canvas redrawing for a selection
/// that did not actually change what is shown.
/// </summary>
public sealed class ArtifactCanvasBodyHost : UserControl
{
    public static readonly StyledProperty<MainViewModel?> MainProperty =
        AvaloniaProperty.Register<ArtifactCanvasBodyHost, MainViewModel?>(nameof(Main));

    private readonly ArtifactCanvasBody _body = new();
    private MainViewModel? _main;
    private ArtifactItemViewModel? _watched;

    public ArtifactCanvasBodyHost()
    {
        Content = _body;
        _body.FixErrorRequested += (_, message) => _main?.ReportArtifactError(message);
    }

    public MainViewModel? Main
    {
        get => GetValue(MainProperty);
        set => SetValue(MainProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != MainProperty) return;

        if (_main is not null) _main.PropertyChanged -= OnMainChanged;
        _main = change.NewValue as MainViewModel;
        if (_main is not null) _main.PropertyChanged += OnMainChanged;
        Sync();
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedArtifact)
            or nameof(MainViewModel.ArtifactSourceMode)
            or nameof(MainViewModel.ArtifactCanvasVisible)
            or nameof(MainViewModel.ArtifactPanelVisible))
        {
            Sync();
        }
    }

    private void OnArtifactChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArtifactItemViewModel.RenderToken)) Sync();
    }

    private void Sync()
    {
        var selected = _main?.SelectedArtifact;
        if (!ReferenceEquals(selected, _watched))
        {
            if (_watched is not null) _watched.PropertyChanged -= OnArtifactChanged;
            _watched = selected;
            if (_watched is not null) _watched.PropertyChanged += OnArtifactChanged;
        }

        // A closed drawer runs nothing: the page is blanked, not just hidden.
        if (_main is null || !_main.ArtifactCanvasVisible || !_main.ArtifactPanelVisible)
        {
            _body.Clear();
            return;
        }

        _body.Show(selected, _main.ArtifactSourceMode, _main.Chat.ConversationId);
    }
}
