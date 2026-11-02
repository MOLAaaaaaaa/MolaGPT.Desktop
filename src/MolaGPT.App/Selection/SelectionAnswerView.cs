using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MolaGPT.App.Rendering;
using MolaGPT.Presentation;

namespace MolaGPT.App.Selection;

internal sealed class SelectionAnswerView(Action<string> reportError) : StackPanel
{
    internal RenderDocument Document { get; private set; } = RenderDocument.Empty;

    internal void Apply(RenderDocument document, bool live)
    {
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            var block = document.Blocks[i];
            if (i < Document.Blocks.Count && i < Children.Count
                && Document.Blocks[i].GetType() == block.GetType())
            {
                if (Document.Blocks[i].Key != block.Key || block is CodeBlock)
                    Update(Children[i], block, live);
            }
            else
            {
                var view = Create(block, live);
                view.Margin = new Thickness(0, 0, 0, 8);
                if (i < Children.Count) Children[i] = view;
                else Children.Add(view);
            }
        }
        if (Children.Count > document.Blocks.Count)
            Children.RemoveRange(document.Blocks.Count, Children.Count - document.Blocks.Count);
        Document = document;
    }

    private Control Create(RenderBlock block, bool live)
    {
        Control view;
        switch (block)
        {
            case ParagraphBlock:
            case HeadingBlock:
                view = new MarkdownTextBlock { Classes = { "selprose" } };
                break;
            case QuoteBlock:
                view = new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6),
                    Child = new MarkdownTextBlock { Classes = { "selprose" } }
                };
                view.Bind(Border.BackgroundProperty, new DynamicResourceExtension("Brush.Primary.Blockquote"));
                view.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("Brush.Primary.Border"));
                break;
            case ListBlock:
                view = new MarkdownListView { Classes = { "sellist" } };
                break;
            case TableBlock:
                var table = new MarkdownTableView { Classes = { "seltable" } };
                view = new ScrollViewer
                {
                    Content = table, Tag = table,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                break;
            case CodeBlock:
                var body = new CodeBody();
                var label = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
                label.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(CodeBlock.Language)));
                label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Brush.Text.Muted"));
                var copy = new Button { Content = "复制", Classes = { "actionchip" }, HorizontalAlignment = HorizontalAlignment.Right };
                AutomationProperties.SetName(copy, "复制代码");
                copy.Click += async (_, _) =>
                {
                    try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(body.Code); }
                    catch (Exception ex) { reportError(ex.Message); }
                };
                var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), Margin = new Thickness(12, 6, 6, 0) };
                Grid.SetColumn(copy, 1); header.Children.Add(label); header.Children.Add(copy);
                var content = new StackPanel();
                content.Children.Add(header);
                content.Children.Add(new ScrollViewer
                {
                    Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                });
                view = new Border { Classes = { "codepanel" }, Child = content, Tag = body };
                break;
            case MathBlock:
                var math = new MathView { FormulaSize = 18, Margin = new Thickness(8) };
                view = new ScrollViewer
                {
                    Content = math, Tag = math, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                break;
            case ImageBlock:
                view = new MarkdownImageView();
                break;
            case ThematicBreakBlock:
                view = new Border { Height = 1 };
                view.Bind(Border.BackgroundProperty, new DynamicResourceExtension("Brush.Divider"));
                break;
            default:
                view = new SelectableTextBlock { FontSize = 14, LineHeight = 22, TextWrapping = TextWrapping.Wrap };
                break;
        }
        Update(view, block, live);
        return view;
    }

    private static void Update(Control view, RenderBlock block, bool live)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                ((MarkdownTextBlock)view).Markdown = paragraph.Markdown;
                break;
            case HeadingBlock heading:
                var title = (MarkdownTextBlock)view;
                title.FontWeight = FontWeight.SemiBold;
                title.FontSize = Math.Max(14, 22 - (heading.Level - 1) * 2);
                title.Markdown = heading.Markdown;
                break;
            case QuoteBlock quote:
                ((MarkdownTextBlock)((Border)view).Child!).Markdown = quote.Markdown;
                break;
            case ListBlock list:
                ((MarkdownListView)view).Block = list;
                break;
            case TableBlock table:
                ((MarkdownTableView)view.Tag!).Block = table;
                break;
            case CodeBlock code:
                view.DataContext = code;
                var body = (CodeBody)view.Tag!;
                body.IsLive = live && !code.IsClosed;
                body.Language = code.Language;
                body.Code = code.Code;
                break;
            case MathBlock math:
                ((MathView)view.Tag!).Latex = math.Latex;
                break;
            case ImageBlock image:
                var imageView = (MarkdownImageView)view;
                imageView.Alt = image.Alt;
                imageView.Url = image.Url;
                break;
            case RawTextBlock raw:
                ((SelectableTextBlock)view).Text = raw.Text;
                break;
            case MarkupUnitBlock markup:
                ((SelectableTextBlock)view).Text = markup.Unit.Inner;
                break;
        }
    }
}
