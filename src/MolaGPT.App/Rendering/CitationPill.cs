using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using MolaGPT.Core.Models;

namespace MolaGPT.App.Rendering;

/// <summary>One source behind a citation, as the renderer needs it.</summary>
/// <param name="Site">Where it came from, named the way the pill prints it.</param>
internal sealed record Citation(string Site, string Title, string Url, string? Date);

/// <summary>
/// The inline source pill, and the card that opens when it is clicked.
///
/// Split out of <see cref="MarkdownTextBlock"/> because it is the only part of
/// inline rendering that is a control rather than a run of text, and because the
/// card is a small UI of its own — paging through the sources one citation names.
/// </summary>
internal static class CitationPill
{
    /// <summary>
    /// The favicon service the web client already uses. Sharing it means a
    /// source looks the same on both, and that the cache is already warm for
    /// the domains this user's searches keep returning.
    /// </summary>
    private static string FaviconUrl(string host) =>
        $"https://cn.cravatar.com/favicon/api/index.php?url={Uri.EscapeDataString(host)}";

    /// <summary>
    /// Builds the pill: favicon, site, and "+n" when the citation names more
    /// sources than the one on the face of it.
    ///
    /// The pill takes the pointer itself rather than leaving clicks to the
    /// rectangle bookkeeping every prose link in <see cref="MarkdownTextBlock"/>
    /// rides on. That was the first design and it did nothing at all: a hit test
    /// over an InlineUIContainer whose content is not hit testable does not fall
    /// through to the text block underneath, it falls past it to the window. The
    /// cost is that a selection dragged through a sentence stops at the pill.
    /// </summary>
    public static Control Build(IReadOnlyList<Citation> citations, double fontSize, IResourceHost host)
    {
        var lead = citations[0];
        var size = Math.Max(10, fontSize * 0.76);

        // The icon is deliberately bigger than the label. Plenty of favicons are
        // a wide wordmark on a square canvas — doi.org's is — so at label size
        // they shrink to an unreadable smudge that reads as a stray dash rather
        // than as a logo.
        var iconSize = Math.Max(13, fontSize * 0.95);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        var icon = new Image
        {
            // Height only. A square box would squash a wordmark favicon —
            // doi.org's is three times as wide as it is tall — into a dash
            // floating next to the text, which is exactly what it looked like.
            // Free width lets it keep its shape at the right height.
            Height = iconSize,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapInterpolationMode(icon, BitmapInterpolationMode.HighQuality);
        var iconHost = new Border
        {
            Height = iconSize,
            MaxWidth = iconSize * 2.4,
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            // Collapsed until the icon actually arrives: favicon services fail
            // often enough that a permanently blank square would be a normal
            // sight, and a pill that is just the site name still reads.
            IsVisible = false,
            Child = icon
        };
        row.Children.Add(iconHost);
        LoadFavicon(icon, lead.Url, iconHost);

        // The explicit line height is what keeps the pill the height of a pill.
        // FontFamily is inherited from the paragraph, and the CJK face this app
        // uses carries about 1.9x the font size in leading — a 11px label was
        // laying out 22px tall and dragging the whole pill, and the line under
        // it, with it. LineSpacing is inherited too, and the paragraph sets it
        // whenever it drops to adaptive line height, so that has to go as well.
        // Domains are ASCII, so there is nothing to clip.
        var labelLine = Math.Ceiling(size * 1.25);

        row.Children.Add(new TextBlock
        {
            Text = lead.Site,
            FontSize = size,
            LineHeight = labelLine,
            LineSpacing = 0,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush(host, "Brush.Text.Secondary"),
            VerticalAlignment = VerticalAlignment.Center
        });

        if (citations.Count > 1)
        {
            row.Children.Add(new TextBlock
            {
                Text = $"+{citations.Count - 1}",
                FontSize = size,
                LineHeight = labelLine,
                LineSpacing = 0,
                Foreground = Brush(host, "Brush.Text.Muted"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var pill = new Border
        {
            Child = row,
            Padding = new Thickness(4, 0, 6, 0),
            Margin = new Thickness(3, 0, 1, 0),
            // The last two pixels, taken after layout so they cost nothing.
            //
            // BaselineAlignment only offers three positions — top, middle and
            // bottom of the line — and the line is taller than the glyphs on it,
            // so the middle one leaves the pill sitting about 0.15em low. Doing
            // this with a margin instead grows the box, which grows the line,
            // which moves the text down by more than it moves the pill up.
            RenderTransform = new TranslateTransform(0, -Math.Round(fontSize * 0.15)),
            CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(host, "Brush.Border.Subtle"),
            Background = Brush(host, "Brush.Bg.Tertiary"),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // The press has to be swallowed here, or the pill never sees the release.
        // SelectableTextBlock captures the pointer on press to start a selection,
        // and a captured pointer reports to the capturing element, not to whatever
        // is under it — which is why this first showed a hand cursor on hover, an
        // I-beam on press, and did nothing at all on release. Marking it handled
        // stops the bubble before the text block's own class handler runs.
        pill.PointerPressed += (_, e) => e.Handled = true;

        pill.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            Show(pill, new Rect(pill.Bounds.Size), citations);
            e.Handled = true;
        };

        return pill;
    }

    /// <summary>
    /// Opens the source card over <paramref name="anchor"/>, paging through the
    /// citation's sources when it names more than one.
    /// </summary>
    public static void Show(Control owner, Rect anchor, IReadOnlyList<Citation> citations)
    {
        var index = 0;

        var site = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush(owner, "Brush.Text.Secondary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var icon = new Image { Width = 14, Height = 14, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(icon, BitmapInterpolationMode.HighQuality);

        var title = new TextBlock
        {
            FontSize = 13.5,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush(owner, "Brush.Text.Primary"),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 9, 0, 6)
        };
        var meta = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush(owner, "Brush.Text.Muted"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var counter = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(3, 0, 0, 0),
            Foreground = Brush(owner, "Brush.Text.Muted"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var previous = NavButton(owner, "");
        var next = NavButton(owner, "");

        var nav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = citations.Count > 1
        };
        nav.Children.Add(previous);
        nav.Children.Add(next);
        nav.Children.Add(counter);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var iconHost = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Child = icon
        };
        Grid.SetColumn(iconHost, 0);
        Grid.SetColumn(site, 1);
        Grid.SetColumn(nav, 2);
        header.Children.Add(iconHost);
        header.Children.Add(site);
        header.Children.Add(nav);

        var card = new Border
        {
            Width = 300,
            Padding = new Thickness(14, 12, 14, 13),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(owner, "Brush.Border"),
            Background = Brush(owner, "Brush.Bg.Elevated") ?? Brush(owner, "Brush.Bg.Canvas"),
            BoxShadow = BoxShadows.Parse("0 8 24 0 #22000000"),
            Child = new StackPanel { Children = { header, title, meta } }
        };

        var popup = new Popup
        {
            Child = card,
            PlacementTarget = owner,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.TopRight,
            PlacementRect = anchor,
            // Let the window system push the card back inside the screen rather
            // than doing the arithmetic here: this is the one case Avalonia's
            // placement already solves, and it solves it per monitor.
            HorizontalOffset = 0,
            VerticalOffset = -6,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = true
        };

        void Render()
        {
            var current = citations[index];
            site.Text = current.Site;
            title.Text = current.Title.Length > 0 ? current.Title : current.Url;
            meta.Text = string.IsNullOrEmpty(current.Date)
                ? SourceReference.SiteOf(current.Url)
                : current.Date;
            counter.Text = $"{index + 1}/{citations.Count}";
            LoadFavicon(icon, current.Url);
        }

        previous.Click += (_, _) =>
        {
            index = (index - 1 + citations.Count) % citations.Count;
            Render();
        };
        next.Click += (_, _) =>
        {
            index = (index + 1) % citations.Count;
            Render();
        };
        title.PointerReleased += (_, _) =>
        {
            LinkLauncher.Open(citations[index].Url);
            popup.IsOpen = false;
        };

        Render();

        // Parented into the owner's own tree so it inherits the theme variant,
        // and unparented on close so a transcript row recycled out from under an
        // open card does not drag the card along with it.
        ((ISetLogicalParent)popup).SetParent(owner);
        popup.Closed += (_, _) => ((ISetLogicalParent)popup).SetParent(null);
        popup.IsOpen = true;
    }

    /// <summary>
    /// Opens the whole source list for a turn, one row per source, unnumbered.
    ///
    /// A Flyout rather than the <see cref="Show"/> popup: that one pages, which
    /// is right for the two or three sources behind a single citation and wrong
    /// for the twenty-odd a search turn collects — nobody clicks "next" twenty
    /// times. A Flyout also gets placement and light dismiss for free.
    /// </summary>
    public static void ShowList(Control owner, IReadOnlyList<Citation> citations)
    {
        var flyout = new Flyout
        {
            Placement = PlacementMode.TopEdgeAlignedLeft,
            ShowMode = FlyoutShowMode.Standard
        };

        flyout.Content = BuildSourceList(owner, citations, () => flyout.Hide());
        flyout.ShowAt(owner);
    }

    /// <summary>
    /// The list itself, separate from the flyout that carries it so it can be
    /// measured offscreen — a flyout opens into its own popup window, which a
    /// test harness cannot walk into.
    /// </summary>
    internal static Control BuildSourceList(
        Control host, IReadOnlyList<Citation> citations, Action close)
    {
        var list = new StackPanel { Width = 380, Spacing = 1 };
        foreach (var citation in citations)
            list.Children.Add(SourceRow(host, citation, close));

        return new ScrollViewer
        {
            // Tall enough to show a handful without scrolling, short enough that
            // the flyout never becomes the window.
            MaxHeight = 420,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list
        };
    }

    private static Control SourceRow(Control host, Citation citation, Action close)
    {
        var icon = new Image { Width = 16, Height = 16, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(icon, BitmapInterpolationMode.HighQuality);
        var iconHost = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 10, 0),
            Child = icon
        };
        LoadFavicon(icon, citation.Url);

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = citation.Title.Length > 0 ? citation.Title : citation.Url,
            FontSize = 13,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush(host, "Brush.Text.Primary")
        });
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(citation.Date)
                ? citation.Site
                : $"{citation.Site} · {citation.Date}",
            FontSize = 11.5,
            Foreground = Brush(host, "Brush.Text.Muted"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(iconHost, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(iconHost);
        row.Children.Add(text);

        var button = new Button
        {
            Content = row,
            Padding = new Thickness(9, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Click += (_, _) =>
        {
            close();
            LinkLauncher.Open(citation.Url);
        };
        return button;
    }

    private static Button NavButton(IResourceHost host, string glyph) => new()
    {
        Content = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = Brush(host, "Brush.Text.Secondary")
        },
        Padding = new Thickness(5, 2),
        MinWidth = 0,
        MinHeight = 0,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static async void LoadFavicon(Image target, string url, Control? host = null)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            var bitmap = await ImageSourceLoader.LoadAsync(FaviconUrl(uri.Host), 64);
            if (bitmap is null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                target.Source = bitmap;
                if (host is not null) host.IsVisible = true;
            });
        }
        catch (OperationCanceledException)
        {
            // Row went away while the icon was in flight; a pill without its
            // favicon still reads.
        }
        catch (Exception)
        {
            // Favicon services fail all the time. Never let one take a paragraph
            // down with it.
        }
    }

    private static IBrush? Brush(IResourceHost host, string key)
    {
        var variant = (host as ThemeVariantScope)?.ActualThemeVariant
                      ?? (host as Control)?.ActualThemeVariant;
        return host.TryFindResource(key, variant, out var value) ? value as IBrush : null;
    }
}
