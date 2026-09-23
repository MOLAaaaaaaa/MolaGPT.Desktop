using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MolaGPT.Presentation.Visuals;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Items as cards, two across when there is room, filterable by tag.
///
/// A card with a link is a button to it, and says where it goes before it is
/// clicked — the host sits at the foot of the card. Links go through
/// <see cref="LinkLauncher"/> like every other link a model writes, so only
/// http and https ever get a card that responds.
/// </summary>
public sealed class CardGridView : UserControl
{
    public const double EstimatedHeight = 280;

    private readonly List<(Control Card, string? Tag)> _cards = new();
    private readonly List<(Button Chip, string? Tag)> _chips = new();
    private readonly TextBlock _count = new() { Classes = { "muted" }, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };

    public CardGridView(CardGridSpec spec)
    {
        var filtered = spec.Tags.Count > 1;

        var root = new StackPanel();
        if (!string.IsNullOrWhiteSpace(spec.Title) || filtered)
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 10, 14, 0) };
            header.Children.Add(new TextBlock
            {
                Text = spec.Title ?? string.Empty,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            // The count only means something while a filter can change it.
            if (filtered)
            {
                Grid.SetColumn(_count, 1);
                header.Children.Add(_count);
            }

            root.Children.Add(header);
        }

        if (filtered)
        {
            var chips = new WrapPanel { Margin = new Thickness(12, 8, 12, 0) };
            chips.Children.Add(Chip("全部", null));
            foreach (var tag in spec.Tags) chips.Children.Add(Chip(tag, tag));
            root.Children.Add(chips);
        }

        var grid = new AutoFitGrid
        {
            MinColumnWidth = 240,
            MaxColumns = 2,
            Spacing = 10,
            Margin = new Thickness(12, root.Children.Count == 0 ? 12 : 10, 12, 12),
        };
        foreach (var item in spec.Items)
        {
            var card = Card(item);
            _cards.Add((card, item.Tag));
            grid.Children.Add(card);
        }

        root.Children.Add(grid);
        Content = new Border { Classes = { "uiblockframe" }, Child = root };
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Filter(null);
    }

    private Button Chip(string text, string? tag)
    {
        var chip = new Button { Classes = { "uifilter" }, Content = text };
        chip.Click += (_, _) => Filter(tag);
        _chips.Add((chip, tag));
        return chip;
    }

    private void Filter(string? tag)
    {
        var shown = 0;
        foreach (var (card, cardTag) in _cards)
        {
            card.IsVisible = tag is null || string.Equals(tag, cardTag, StringComparison.Ordinal);
            if (card.IsVisible) shown++;
        }

        foreach (var (chip, chipTag) in _chips)
            chip.Classes.Set("active", string.Equals(tag, chipTag, StringComparison.Ordinal));
        _count.Text = $"{shown} 项";
    }

    private static Control Card(CardItem item)
    {
        var host = LinkLauncher.CanOpen(item.Url) ? new Uri(item.Url!).Host : null;
        if (host?.StartsWith("www.", StringComparison.OrdinalIgnoreCase) == true) host = host[4..];
        // A source that is just the link's host would say the same thing twice.
        var source = item.Source is { } s && !string.Equals(s.Trim().TrimEnd('/'), host, StringComparison.OrdinalIgnoreCase) ? s : null;

        var top = new StackPanel();
        if (item.Tag is not null || source is not null)
        {
            var meta = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 6) };
            if (item.Tag is not null)
            {
                meta.Children.Add(new Border
                {
                    Classes = { "uitag" },
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = item.Tag, FontSize = 11 },
                });
            }

            if (source is not null)
            {
                var sourceText = new TextBlock
                {
                    Text = source,
                    Classes = { "muted" },
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                Grid.SetColumn(sourceText, 1);
                meta.Children.Add(sourceText);
            }

            top.Children.Add(meta);
        }

        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
        };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("Brush.Text.Primary"));
        top.Children.Add(title);

        if (item.Summary is not null)
        {
            var summary = new TextBlock
            {
                Text = item.Summary,
                FontSize = 12.5,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            summary.Bind(TextBlock.ForegroundProperty, summary.GetResourceObservable("Brush.Text.Secondary"));
            top.Children.Add(summary);
        }

        var body = new DockPanel();
        if (host is not null)
        {
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 8, 0, 0) };
            footer.Children.Add(new TextBlock { Classes = { "icon", "muted" }, Text = "\uE71B", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            footer.Children.Add(new TextBlock { Text = host, Classes = { "muted" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            // Docked to the bottom so cards sharing a row keep their links level.
            DockPanel.SetDock(footer, Dock.Bottom);
            body.Children.Add(footer);
            body.Children.Add(top);

            var button = new Button
            {
                Classes = { "uicard" },
                Content = body,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            ToolTip.SetTip(button, item.Url);
            Avalonia.Automation.AutomationProperties.SetName(button, item.Title);
            button.Click += (_, _) => LinkLauncher.Open(item.Url);
            return button;
        }

        body.Children.Add(top);
        return new Border { Classes = { "uicard" }, Child = body };
    }
}
