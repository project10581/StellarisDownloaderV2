using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

namespace StellarisDownloader.App.Controls;

public sealed class WorkshopDescriptionViewer : FlowDocumentScrollViewer
{
    public const int MaximumInputLength = 131_072;
    public const int MaximumNestingDepth = 24;
    public const int MaximumParsedNodes = 4_096;
    public const int MaximumDocumentElements = 4_096;

    private const int MaximumTagLength = 2_048;
    private const string TruncationMarker = "\u2026";

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description),
            typeof(string),
            typeof(WorkshopDescriptionViewer),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.AffectsMeasure,
                OnDescriptionChanged));

    public WorkshopDescriptionViewer()
    {
        IsToolBarVisible = false;
        IsSelectionEnabled = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = CreateDocument(string.Empty);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!e.Handled
            && VerticalScrollBarVisibility == ScrollBarVisibility.Disabled
            && (VisualTreeHelper.GetParent(this) as UIElement ?? Parent as UIElement) is UIElement parent)
        {
            e.Handled = true;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = this,
            });
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    private static void OnDescriptionChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var viewer = (WorkshopDescriptionViewer)dependencyObject;
        viewer.Document = CreateDocument(eventArgs.NewValue as string ?? string.Empty);
    }

    private static FlowDocument CreateDocument(string description)
    {
        var wasInputTruncated = description.Length > MaximumInputLength;
        var source = wasInputTruncated
            ? description[..MaximumInputLength]
            : description;
        var parser = new Parser(source);
        var nodes = parser.Parse();
        var context = new RenderContext();
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            FontFamily = SystemFonts.MessageFontFamily,
            FontSize = 13,
        };

        var pendingInlineNodes = new List<ParsedNode>();
        foreach (var node in nodes)
        {
            if (node is ElementNode { Kind: TagKind.Heading1 or TagKind.Heading2 or TagKind.Heading3 } heading)
            {
                FlushInlineParagraph(document, pendingInlineNodes, context);
                AddHeading(document, heading, context);
            }
            else if (node is ElementNode { Kind: TagKind.HorizontalRule })
            {
                FlushInlineParagraph(document, pendingInlineNodes, context);
                AddHorizontalRule(document, context);
            }
            else
            {
                pendingInlineNodes.Add(node);
            }

            if (context.IsTruncated)
            {
                break;
            }
        }

        FlushInlineParagraph(document, pendingInlineNodes, context);
        if (wasInputTruncated || parser.WasTruncated || context.IsTruncated)
        {
            AddTruncationMarker(document);
        }

        return document;
    }

    private static void FlushInlineParagraph(
        FlowDocument document,
        List<ParsedNode> nodes,
        RenderContext context)
    {
        if (nodes.Count == 0 || !context.TryConsume())
        {
            nodes.Clear();
            return;
        }

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
        };
        RenderInlineNodes(nodes, paragraph.Inlines, context);
        document.Blocks.Add(paragraph);
        nodes.Clear();
    }

    private static void AddHeading(
        FlowDocument document,
        ElementNode heading,
        RenderContext context)
    {
        if (!context.TryConsume())
        {
            return;
        }

        var paragraph = new Paragraph
        {
            FontSize = heading.Kind switch
            {
                TagKind.Heading1 => 26,
                TagKind.Heading2 => 22,
                _ => 18,
            },
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 4),
        };
        RenderInlineNodes(heading.Children, paragraph.Inlines, context);
        document.Blocks.Add(paragraph);
    }

    private static void AddHorizontalRule(
        FlowDocument document,
        RenderContext context)
    {
        if (!context.TryConsume())
        {
            return;
        }

        document.Blocks.Add(new Paragraph
        {
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(0, 6, 0, 6),
        });
    }

    private static void RenderInlineNodes(
        IEnumerable<ParsedNode> nodes,
        InlineCollection destination,
        RenderContext context,
        bool hyperlinksAllowed = true)
    {
        foreach (var node in nodes)
        {
            if (context.IsTruncated)
            {
                return;
            }

            switch (node)
            {
                case TextNode text:
                    AddText(destination, text.Text, context);
                    break;
                case ElementNode element:
                    AddElement(destination, element, context, hyperlinksAllowed);
                    break;
            }
        }
    }

    private static void AddElement(
        InlineCollection destination,
        ElementNode element,
        RenderContext context,
        bool hyperlinksAllowed)
    {
        switch (element.Kind)
        {
            case TagKind.Image:
                AddImageFallback(destination, element, context, hyperlinksAllowed);
                return;
            case TagKind.Url:
                AddUrl(destination, element, context, hyperlinksAllowed);
                return;
            case TagKind.List or TagKind.OrderedList:
                AddList(destination, element, context, hyperlinksAllowed);
                return;
            case TagKind.ListItemMarker:
                AddListItemMarker(destination, context, itemNumber: null);
                return;
            case TagKind.HorizontalRule:
                if (context.TryConsume())
                {
                    destination.Add(new LineBreak());
                }

                return;
            case TagKind.Quote:
                AddQuote(destination, element, context, hyperlinksAllowed);
                return;
            case TagKind.Code:
                AddCode(destination, element, context, hyperlinksAllowed);
                return;
            case TagKind.Paragraph or TagKind.TableRow:
                RenderInlineNodes(element.Children, destination, context, hyperlinksAllowed);
                if (destination.LastInline is not (null or LineBreak) && context.TryConsume())
                {
                    destination.Add(new LineBreak());
                }

                return;
            case TagKind.TableCell:
                RenderInlineNodes(element.Children, destination, context, hyperlinksAllowed);
                _ = AddRun(destination, "  ", context);
                return;
        }

        if (!context.TryConsume())
        {
            return;
        }

        Span span = element.Kind switch
        {
            TagKind.Bold => new Bold(),
            TagKind.Italic => new Italic(),
            TagKind.Underline => new Underline(),
            TagKind.Strike => new Span
            {
                TextDecorations = TextDecorations.Strikethrough,
            },
            TagKind.Spoiler => new Span
            {
                Background = SystemColors.ControlLightBrush,
            },
            TagKind.Heading1 => new Span
            {
                FontSize = 26,
                FontWeight = FontWeights.Bold,
            },
            TagKind.Heading2 => new Span
            {
                FontSize = 22,
                FontWeight = FontWeights.Bold,
            },
            TagKind.Heading3 => new Span
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
            },
            _ => new Span(),
        };
        RenderInlineNodes(element.Children, span.Inlines, context, hyperlinksAllowed);
        destination.Add(span);
    }

    private static void AddQuote(
        InlineCollection destination,
        ElementNode element,
        RenderContext context,
        bool hyperlinksAllowed)
    {
        if (!context.TryConsume())
        {
            return;
        }

        var quoteSpan = new Span
        {
            FontStyle = FontStyles.Italic,
            Background = SystemColors.ControlLightBrush,
        };
        if (!string.IsNullOrWhiteSpace(element.Argument) && context.TryConsume())
        {
            var authorBold = new Bold();
            if (AddRun(authorBold.Inlines, $"{element.Argument}: ", context))
            {
                quoteSpan.Inlines.Add(authorBold);
            }
        }

        RenderInlineNodes(element.Children, quoteSpan.Inlines, context, hyperlinksAllowed);
        destination.Add(quoteSpan);
    }

    private static void AddList(
        InlineCollection destination,
        ElementNode element,
        RenderContext context,
        bool hyperlinksAllowed)
    {
        if (!context.TryConsume())
        {
            return;
        }

        var listSpan = new Span();
        var isOrdered = element.Kind == TagKind.OrderedList;
        var itemIndex = 0;
        var children = TrimSurroundingListNewlines(element.Children);

        for (var i = 0; i < children.Count; i++)
        {
            if (context.IsTruncated)
            {
                break;
            }

            var child = children[i];
            if (child is ElementNode { Kind: TagKind.ListItemMarker })
            {
                itemIndex++;
                if (itemIndex > 1
                    && listSpan.Inlines.LastInline is not (null or LineBreak)
                    && context.TryConsume())
                {
                    listSpan.Inlines.Add(new LineBreak());
                }

                AddListItemMarker(
                    listSpan.Inlines,
                    context,
                    isOrdered ? itemIndex : null);
            }
            else
            {
                RenderInlineNodes([child], listSpan.Inlines, context, hyperlinksAllowed);
            }
        }

        destination.Add(listSpan);
    }

    private static IReadOnlyList<ParsedNode> TrimSurroundingListNewlines(
        IReadOnlyList<ParsedNode> children)
    {
        if (children.Count == 0)
        {
            return children;
        }

        var result = children.ToList();
        if (result[0] is TextNode firstText)
        {
            var trimmedStart = TrimSingleLeadingNewline(firstText.Text);
            if (trimmedStart.Length == 0)
            {
                result.RemoveAt(0);
            }
            else if (trimmedStart.Length != firstText.Text.Length)
            {
                result[0] = new TextNode(trimmedStart);
            }
        }

        if (result.Count > 0 && result[^1] is TextNode lastText)
        {
            var trimmedEnd = TrimSingleTrailingNewline(lastText.Text);
            if (trimmedEnd.Length == 0)
            {
                result.RemoveAt(result.Count - 1);
            }
            else if (trimmedEnd.Length != lastText.Text.Length)
            {
                result[^1] = new TextNode(trimmedEnd);
            }
        }

        return result;
    }

    private static string TrimSingleLeadingNewline(string text)
    {
        if (text.StartsWith("\r\n", StringComparison.Ordinal))
        {
            return text[2..];
        }

        return text.StartsWith('\n') || text.StartsWith('\r')
            ? text[1..]
            : text;
    }

    private static string TrimSingleTrailingNewline(string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return text[..^2];
        }

        return text.EndsWith('\n') || text.EndsWith('\r')
            ? text[..^1]
            : text;
    }

    private static void AddListItemMarker(
        InlineCollection destination,
        RenderContext context,
        int? itemNumber)
    {
        var prefix = itemNumber.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{itemNumber.Value}. ")
            : "\u2022 ";
        _ = AddRun(destination, prefix, context);
    }

    private static void AddUrl(
        InlineCollection destination,
        ElementNode element,
        RenderContext context,
        bool hyperlinksAllowed)
    {
        var targetText = element.Argument ?? GetPlainText(element.Children).Trim();
        if (!hyperlinksAllowed || !TryGetSafeWebUri(targetText, out var target))
        {
            RenderInlineNodes(
                element.Children,
                destination,
                context,
                hyperlinksAllowed: false);
            return;
        }

        if (!context.TryConsume())
        {
            return;
        }

        var hyperlink = new Hyperlink
        {
            NavigateUri = target,
            ToolTip = target.AbsoluteUri,
        };
        hyperlink.RequestNavigate += OnRequestNavigate;
        RenderInlineNodes(
            element.Children,
            hyperlink.Inlines,
            context,
            hyperlinksAllowed: false);
        if (hyperlink.Inlines.Count == 0)
        {
            AddText(hyperlink.Inlines, target.AbsoluteUri, context);
        }

        destination.Add(hyperlink);
    }

    private static void AddImageFallback(
        InlineCollection destination,
        ElementNode element,
        RenderContext context,
        bool hyperlinksAllowed)
    {
        var sourceText = (element.Argument ?? GetPlainText(element.Children)).Trim();
        if (!hyperlinksAllowed || !TryGetSafeWebUri(sourceText, out var source))
        {
            AddText(destination, sourceText, context);
            return;
        }

        if (!context.TryConsume())
        {
            return;
        }

        var hyperlink = new Hyperlink
        {
            NavigateUri = source,
            ToolTip = source.AbsoluteUri,
        };
        hyperlink.RequestNavigate += OnRequestNavigate;
        AddText(hyperlink.Inlines, source.AbsoluteUri, context);
        destination.Add(hyperlink);
    }

    private static void AddCode(
        InlineCollection destination,
        ElementNode element,
        RenderContext context,
        bool hyperlinksAllowed)
    {
        if (!context.TryConsume())
        {
            return;
        }

        var code = new Span
        {
            Background = SystemColors.ControlLightBrush,
            FontFamily = new FontFamily("Consolas"),
        };
        var children = TrimSurroundingListNewlines(element.Children);
        RenderInlineNodes(children, code.Inlines, context, hyperlinksAllowed);
        destination.Add(code);
    }

    private static void AddText(
        InlineCollection destination,
        string text,
        RenderContext context)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            if (index > start && !AddRun(destination, text[start..index], context))
            {
                return;
            }

            if (!context.TryConsume())
            {
                return;
            }

            destination.Add(new LineBreak());
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        if (start < text.Length)
        {
            _ = AddRun(destination, text[start..], context);
        }
    }

    private static bool AddRun(
        InlineCollection destination,
        string text,
        RenderContext context)
    {
        if (!context.TryConsume())
        {
            return false;
        }

        destination.Add(new Run(text));
        return true;
    }

    private static void AddTruncationMarker(FlowDocument document)
    {
        if (document.Blocks.LastBlock is Paragraph paragraph)
        {
            paragraph.Inlines.Add(new Run(TruncationMarker));
            return;
        }

        document.Blocks.Add(new Paragraph(new Run(TruncationMarker))
        {
            Margin = new Thickness(0),
        });
    }

    private static string GetPlainText(IEnumerable<ParsedNode> nodes)
    {
        var builder = new StringBuilder();
        AppendPlainText(nodes, builder);
        return builder.ToString();
    }

    private static void AppendPlainText(
        IEnumerable<ParsedNode> nodes,
        StringBuilder builder)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    builder.Append(text.Text);
                    break;
                case ElementNode element:
                    AppendPlainText(element.Children, builder);
                    break;
            }
        }
    }

    private static bool TryGetSafeWebUri(string value, out Uri uri)
    {
        var candidateText = UnquoteArgument(value.Trim());
        if (Uri.TryCreate(candidateText, UriKind.Absolute, out var candidate)
            && (candidate.Scheme == Uri.UriSchemeHttp
                || candidate.Scheme == Uri.UriSchemeHttps))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string UnquoteArgument(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (!TryGetSafeWebUri(eventArgs.Uri.AbsoluteUri, out var target))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Link activation is best-effort; rendering remains safe and usable if the shell fails.
        }
    }

    private enum TagKind
    {
        Bold,
        Italic,
        Underline,
        Strike,
        Spoiler,
        Quote,
        Code,
        Paragraph,
        Table,
        TableRow,
        TableCell,
        Heading1,
        Heading2,
        Heading3,
        Url,
        Image,
        List,
        OrderedList,
        ListItemMarker,
        HorizontalRule,
    }

    private abstract record ParsedNode;

    private sealed record TextNode(string Text) : ParsedNode;

    private sealed record ElementNode(
        TagKind Kind,
        string? Argument,
        IReadOnlyList<ParsedNode> Children) : ParsedNode;

    private sealed class RenderContext
    {
        private int remainingElements = MaximumDocumentElements;

        public bool IsTruncated { get; private set; }

        public bool TryConsume()
        {
            if (remainingElements > 0)
            {
                remainingElements--;
                return true;
            }

            IsTruncated = true;
            return false;
        }
    }

    private sealed class Parser(string source)
    {
        private readonly HashSet<string> unclosedTagNames = new(StringComparer.OrdinalIgnoreCase);
        private int index;
        private int nodeCount;
        private int listDepth;

        public bool WasTruncated { get; private set; }

        public List<ParsedNode> Parse()
        {
            var nodes = ParseNodes(expectedClosingTag: null, depth: 0, out _);
            return nodes;
        }

        private List<ParsedNode> ParseNodes(
            string? expectedClosingTag,
            int depth,
            out bool foundClosingTag)
        {
            var nodes = new List<ParsedNode>();
            var text = new StringBuilder();
            foundClosingTag = false;

            while (index < source.Length)
            {
                if (nodeCount >= MaximumParsedNodes)
                {
                    WasTruncated = true;
                    index = source.Length;
                    break;
                }

                if (source[index] != '[' || !TryReadTag(index, out var tag))
                {
                    text.Append(source[index]);
                    index++;
                    continue;
                }

                if (tag.IsClosing)
                {
                    if (tag.Argument is null
                        && listDepth > 0
                        && string.Equals(tag.Name, "*", StringComparison.Ordinal))
                    {
                        index = tag.EndIndex;
                        continue;
                    }

                    if (tag.Argument is null
                        && expectedClosingTag is not null
                        && string.Equals(tag.Name, expectedClosingTag, StringComparison.OrdinalIgnoreCase))
                    {
                        AddTextNode(nodes, text);
                        index = tag.EndIndex;
                        foundClosingTag = true;
                        return nodes;
                    }

                    text.Append(tag.RawText);
                    index = tag.EndIndex;
                    continue;
                }

                if (tag.Argument is null
                    && string.Equals(tag.Name, "noparse", StringComparison.OrdinalIgnoreCase))
                {
                    AddTextNode(nodes, text);
                    var closeIndex = source.IndexOf("[/noparse]", tag.EndIndex, StringComparison.OrdinalIgnoreCase);
                    if (closeIndex < 0)
                    {
                        text.Append(tag.RawText);
                        index = tag.EndIndex;
                        continue;
                    }

                    if (!TryAddNode(nodes, new TextNode(source[tag.EndIndex..closeIndex])))
                    {
                        index = source.Length;
                        break;
                    }

                    index = closeIndex + "[/noparse]".Length;
                    continue;
                }

                if (!TryGetTagKind(tag.Name, out var kind))
                {
                    text.Append(tag.RawText);
                    index = tag.EndIndex;
                    continue;
                }

                if (kind is not (TagKind.Url or TagKind.Quote or TagKind.Image) && tag.Argument is not null)
                {
                    text.Append(tag.RawText);
                    index = tag.EndIndex;
                    continue;
                }

                if (kind == TagKind.Image && tag.Argument is not null)
                {
                    AddTextNode(nodes, text);
                    index = tag.EndIndex;
                    if (TryReadTag(index, out var optionalImgClose)
                        && optionalImgClose.IsClosing
                        && optionalImgClose.Argument is null
                        && string.Equals(optionalImgClose.Name, "img", StringComparison.OrdinalIgnoreCase))
                    {
                        index = optionalImgClose.EndIndex;
                    }

                    if (!TryAddNode(nodes, new ElementNode(TagKind.Image, tag.Argument, [])))
                    {
                        index = source.Length;
                        break;
                    }

                    continue;
                }

                if (kind == TagKind.ListItemMarker)
                {
                    if (listDepth <= 0)
                    {
                        text.Append(tag.RawText);
                        index = tag.EndIndex;
                        continue;
                    }

                    AddTextNode(nodes, text);
                    index = tag.EndIndex;
                    if (!TryAddNode(nodes, new ElementNode(TagKind.ListItemMarker, null, [])))
                    {
                        index = source.Length;
                        break;
                    }

                    continue;
                }

                if (kind == TagKind.HorizontalRule)
                {
                    AddTextNode(nodes, text);
                    index = tag.EndIndex;
                    if (TryReadTag(index, out var optionalClose)
                        && optionalClose.IsClosing
                        && optionalClose.Argument is null
                        && string.Equals(optionalClose.Name, "hr", StringComparison.OrdinalIgnoreCase))
                    {
                        index = optionalClose.EndIndex;
                    }

                    if (!TryAddNode(nodes, new ElementNode(TagKind.HorizontalRule, null, [])))
                    {
                        index = source.Length;
                        break;
                    }

                    continue;
                }

                if (depth >= MaximumNestingDepth || unclosedTagNames.Contains(tag.Name))
                {
                    text.Append(tag.RawText);
                    index = tag.EndIndex;
                    continue;
                }

                AddTextNode(nodes, text);
                var savedNodeCount = nodeCount;
                var savedListDepth = listDepth;
                index = tag.EndIndex;
                if (kind is TagKind.List or TagKind.OrderedList)
                {
                    listDepth++;
                }

                var children = ParseNodes(tag.Name, depth + 1, out var wasClosed);
                listDepth = savedListDepth;
                if (!wasClosed)
                {
                    if (WasTruncated)
                    {
                        _ = TryAddNode(nodes, new ElementNode(kind, tag.Argument, children));
                        index = source.Length;
                        break;
                    }

                    unclosedTagNames.Add(tag.Name);
                    nodeCount = savedNodeCount;
                    index = tag.EndIndex;
                    text.Append(tag.RawText);
                    continue;
                }

                if (!TryAddNode(nodes, new ElementNode(kind, tag.Argument, children)))
                {
                    index = source.Length;
                    break;
                }
            }

            AddTextNode(nodes, text);
            return nodes;
        }

        private void AddTextNode(List<ParsedNode> nodes, StringBuilder text)
        {
            if (text.Length == 0)
            {
                return;
            }

            if (!TryAddNode(nodes, new TextNode(text.ToString())))
            {
                index = source.Length;
            }

            text.Clear();
        }

        private bool TryAddNode(List<ParsedNode> nodes, ParsedNode node)
        {
            if (nodeCount >= MaximumParsedNodes)
            {
                WasTruncated = true;
                return false;
            }

            nodes.Add(node);
            nodeCount++;
            return true;
        }

        private bool TryReadTag(int startIndex, out ParsedTag tag)
        {
            if (startIndex >= source.Length || source[startIndex] != '[')
            {
                tag = default;
                return false;
            }

            var closingBracket = source.IndexOf(']', startIndex + 1);
            if (closingBracket < 0 || closingBracket - startIndex > MaximumTagLength)
            {
                tag = default;
                return false;
            }

            var rawText = source[startIndex..(closingBracket + 1)];
            var content = source[(startIndex + 1)..closingBracket].Trim();
            if (content.Length == 0)
            {
                tag = default;
                return false;
            }

            var isClosing = content[0] == '/';
            if (isClosing)
            {
                content = content[1..].Trim();
            }

            var equalsIndex = content.IndexOf('=');
            var name = (equalsIndex >= 0 ? content[..equalsIndex] : content).Trim();
            var argument = equalsIndex >= 0
                ? UnquoteArgument(content[(equalsIndex + 1)..].Trim())
                : null;
            if (name.Length == 0
                || (name != "*" && name.Any(character => !char.IsAsciiLetterOrDigit(character))))
            {
                tag = default;
                return false;
            }

            tag = new ParsedTag(
                name,
                argument,
                isClosing,
                rawText,
                closingBracket + 1);
            return true;
        }

        private static bool TryGetTagKind(string name, out TagKind kind)
        {
            switch (name.ToLowerInvariant())
            {
                case "b":
                    kind = TagKind.Bold;
                    return true;
                case "i":
                    kind = TagKind.Italic;
                    return true;
                case "u":
                    kind = TagKind.Underline;
                    return true;
                case "strike":
                    kind = TagKind.Strike;
                    return true;
                case "spoiler":
                    kind = TagKind.Spoiler;
                    return true;
                case "quote" or "pullquote":
                    kind = TagKind.Quote;
                    return true;
                case "code":
                    kind = TagKind.Code;
                    return true;
                case "p":
                    kind = TagKind.Paragraph;
                    return true;
                case "table":
                    kind = TagKind.Table;
                    return true;
                case "tr":
                    kind = TagKind.TableRow;
                    return true;
                case "th":
                    kind = TagKind.Bold;
                    return true;
                case "td":
                    kind = TagKind.TableCell;
                    return true;
                case "h1":
                    kind = TagKind.Heading1;
                    return true;
                case "h2":
                    kind = TagKind.Heading2;
                    return true;
                case "h3":
                    kind = TagKind.Heading3;
                    return true;
                case "url":
                    kind = TagKind.Url;
                    return true;
                case "img":
                    kind = TagKind.Image;
                    return true;
                case "list":
                    kind = TagKind.List;
                    return true;
                case "olist":
                    kind = TagKind.OrderedList;
                    return true;
                case "*":
                    kind = TagKind.ListItemMarker;
                    return true;
                case "hr":
                    kind = TagKind.HorizontalRule;
                    return true;
                default:
                    kind = default;
                    return false;
            }
        }

        private readonly record struct ParsedTag(
            string Name,
            string? Argument,
            bool IsClosing,
            string RawText,
            int EndIndex);
    }
}
