using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using StellarisDownloader.App.Controls;

namespace StellarisDownloader.Tests;

public sealed class WorkshopDescriptionViewerTests
{
    [Fact]
    public void NestedHeadingAndBoldRenderWithNativeDocumentElements()
    {
        WpfTestRunner.Run(async () =>
        {
            var viewer = new WorkshopDescriptionViewer
            {
                Description = "[H1][b]Heading[/B][/h1]\nbody",
            };
            var window = ShowInOuterScrollViewer(viewer);

            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var paragraphs = viewer.Document.Blocks.OfType<Paragraph>().ToArray();
                Assert.Equal(2, paragraphs.Length);
                Assert.True(paragraphs[0].FontSize > paragraphs[1].FontSize);
                Assert.Contains(paragraphs[0].Inlines, inline => inline is Bold);
                Assert.Contains("Heading", GetDocumentText(viewer.Document), StringComparison.Ordinal);
                Assert.Same(viewer, ((ScrollViewer)window.Content).Content);
                Assert.Equal(ScrollBarVisibility.Disabled, viewer.VerticalScrollBarVisibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UrlsOnlyLinkAbsoluteHttpAndHttpsTargetsIncludingQuotedArguments()
    {
        WpfTestRunner.Run(() =>
        {
            var viewer = new WorkshopDescriptionViewer
            {
                Description = "[url=https://example.com/mod]named[/url] "
                    + "[url=\"https://example.com/quoted\"]quoted[/url] "
                    + "[url]http://example.org/item[/url] "
                    + "[url=file:///C:/secret.txt]file[/url] "
                    + "[url=javascript:alert(1)]script[/url] "
                    + "[url]relative/path[/url]",
            };

            var links = GetInlines(viewer.Document).OfType<Hyperlink>().ToArray();
            Assert.Equal(3, links.Length);
            Assert.All(
                links,
                link => Assert.Contains(
                    link.NavigateUri!.Scheme,
                    new[] { Uri.UriSchemeHttp, Uri.UriSchemeHttps }));
            Assert.Contains(
                links,
                link => link.NavigateUri!.AbsoluteUri == "https://example.com/quoted");
            Assert.Contains("file", GetDocumentText(viewer.Document), StringComparison.Ordinal);
            Assert.Contains("script", GetDocumentText(viewer.Document), StringComparison.Ordinal);
            Assert.Contains("relative/path", GetDocumentText(viewer.Document), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void CodeParsesNestedWorkshopBbCodeWhileNoParseKeepsBbCodeLiteral()
    {
        WpfTestRunner.Run(() =>
        {
            var viewer = new WorkshopDescriptionViewer
            {
                Description = "[code][h3][list]\n[*][url=https://example.com]link[/url]\n[/list][/h3][/code] "
                    + "[noparse][i]raw italic[/i][/noparse]",
            };

            var inlines = GetInlines(viewer.Document).ToArray();
            Assert.Single(inlines.OfType<Hyperlink>());
            Assert.DoesNotContain(inlines, inline => inline is Italic);
            var text = GetDocumentText(viewer.Document);
            Assert.Contains("\u2022 link", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[h3]", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[list]", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[i]raw italic[/i]", text, StringComparison.Ordinal);
            Assert.Contains(
                inlines.OfType<Span>(),
                span => span.FontFamily.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void UnknownAndMalformedTagsRemainPlainTextWithoutBreakingLaterFormatting()
    {
        WpfTestRunner.Run(() =>
        {
            var viewer = new WorkshopDescriptionViewer
            {
                Description = "[unknown]plain[/unknown] [b=unexpected]not-bold[/b=unexpected] "
                    + "[h2]missing close [i]still italic[/i]",
            };

            var text = GetDocumentText(viewer.Document);
            Assert.Contains("[unknown]plain[/unknown]", text, StringComparison.Ordinal);
            Assert.Contains("[b=unexpected]not-bold[/b=unexpected]", text, StringComparison.Ordinal);
            Assert.Contains("[h2]missing close ", text, StringComparison.Ordinal);
            Assert.Contains("still italic", text, StringComparison.Ordinal);
            Assert.DoesNotContain(GetInlines(viewer.Document), inline => inline is Bold);
            Assert.Single(GetInlines(viewer.Document).OfType<Italic>());
            Assert.Single(viewer.Document.Blocks);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void ListsQuotesSpoilersAndHorizontalRulesRenderCleanly()
    {
        WpfTestRunner.Run(() =>
        {
            var viewer = new WorkshopDescriptionViewer
            {
                Description = "[list]\n[*]Bullet one\n[*][b]Bullet two[/b][/*]\n[/list]\n"
                    + "[hr]\n"
                    + "[olist][*]First[*]Second[/olist]\n"
                    + "[quote=Author]Quoted text[/quote] [spoiler]Hidden lore[/spoiler]",
            };

            var text = GetDocumentText(viewer.Document);
            Assert.Contains("\u2022 Bullet one", text, StringComparison.Ordinal);
            Assert.Contains("\u2022 Bullet two", text, StringComparison.Ordinal);
            Assert.Contains("1. First", text, StringComparison.Ordinal);
            Assert.Contains("2. Second", text, StringComparison.Ordinal);
            Assert.Contains("Author: Quoted text", text, StringComparison.Ordinal);
            Assert.Contains("Hidden lore", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[*]", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[hr]", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                viewer.Document.Blocks.OfType<Paragraph>(),
                paragraph => paragraph.BorderThickness.Bottom > 0);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void ImageTagsNeverCreateImageContentOrDownloadAutomatically()
    {
        WpfTestRunner.Run(() =>
        {
            var viewer = new WorkshopDescriptionViewer
            {
                Description = "[img]https://example.com/preview.png[/img] "
                    + "[img=https://example.com/banner.png] "
                    + "[img]file:///C:/secret.png[/img]",
            };

            Assert.DoesNotContain(
                GetDocumentElements(viewer.Document),
                element => element is BlockUIContainer or InlineUIContainer);
            var links = GetInlines(viewer.Document).OfType<Hyperlink>().ToArray();
            Assert.Equal(2, links.Length);
            Assert.All(links, link => Assert.Equal("https", link.NavigateUri!.Scheme));
            Assert.DoesNotContain("[img=", GetDocumentText(viewer.Document), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("file:///C:/secret.png", GetDocumentText(viewer.Document), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void ImageInsideUrlUsesOnlyTheOuterHyperlink()
    {
        WpfTestRunner.Run(async () =>
        {
            var viewer = new WorkshopDescriptionViewer
            {
                Description = "[url=https://docs.example/mod]"
                    + "[img]https://images.example/preview.png[/img]"
                    + "[/url]",
            };
            var window = ShowInOuterScrollViewer(viewer);

            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var link = Assert.Single(GetInlines(viewer.Document).OfType<Hyperlink>());
                Assert.Equal("https://docs.example/mod", link.NavigateUri!.AbsoluteUri);
                Assert.Contains(
                    "https://images.example/preview.png",
                    GetDocumentText(viewer.Document),
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    GetInlines(link.Inlines),
                    inline => inline is Hyperlink);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MouseWheelBubblesToOuterScrollViewerWhenInternalScrollIsDisabled()
    {
        WpfTestRunner.Run(async () =>
        {
            var viewer = new WorkshopDescriptionViewer
            {
                Description = "Scrollable description",
            };
            var window = ShowInOuterScrollViewer(viewer);

            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var outerScrollViewer = (ScrollViewer)window.Content;
                var parentReceivedWheel = false;
                outerScrollViewer.AddHandler(
                    UIElement.MouseWheelEvent,
                    new System.Windows.Input.MouseWheelEventHandler((_, _) => parentReceivedWheel = true),
                    handledEventsToo: true);

                var wheelArgs = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    -120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = viewer,
                };
                viewer.RaiseEvent(wheelArgs);

                Assert.True(parentReceivedWheel);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InputDepthAndNodeLimitsBoundGeneratedDocument()
    {
        WpfTestRunner.Run(() =>
        {
            var oversizedViewer = new WorkshopDescriptionViewer
            {
                Description = new string('x', WorkshopDescriptionViewer.MaximumInputLength + 1_000),
            };
            var oversizedText = GetDocumentText(oversizedViewer.Document);
            Assert.True(
                oversizedText.Length <= WorkshopDescriptionViewer.MaximumInputLength + 4,
                $"Rendered text was unexpectedly long: {oversizedText.Length}.");
            Assert.Contains("\u2026", oversizedText, StringComparison.Ordinal);

            var nested = string.Concat(
                    Enumerable.Repeat("[b]", WorkshopDescriptionViewer.MaximumNestingDepth + 20))
                + "content"
                + string.Concat(
                    Enumerable.Repeat("[/b]", WorkshopDescriptionViewer.MaximumNestingDepth + 20));
            var nestedViewer = new WorkshopDescriptionViewer { Description = nested };
            Assert.Contains("content", GetDocumentText(nestedViewer.Document), StringComparison.Ordinal);

            var nodeHeavy = string.Concat(
                Enumerable.Repeat("[b]x[/b]", WorkshopDescriptionViewer.MaximumParsedNodes + 100));
            var nodeViewer = new WorkshopDescriptionViewer { Description = nodeHeavy };
            Assert.True(
                GetDocumentElements(nodeViewer.Document).Count()
                    <= WorkshopDescriptionViewer.MaximumDocumentElements + 2);
            Assert.Contains("\u2026", GetDocumentText(nodeViewer.Document), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    private static Window ShowInOuterScrollViewer(WorkshopDescriptionViewer viewer)
    {
        var window = new Window
        {
            Width = 420,
            Height = 260,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new ScrollViewer
            {
                Content = viewer,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };
        window.Show();
        return window;
    }

    private static string GetDocumentText(FlowDocument document) =>
        new TextRange(document.ContentStart, document.ContentEnd).Text;

    private static IEnumerable<Inline> GetInlines(FlowDocument document) =>
        document.Blocks
            .OfType<Paragraph>()
            .SelectMany(paragraph => GetInlines(paragraph.Inlines));

    private static IEnumerable<Inline> GetInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
            {
                foreach (var child in GetInlines(span.Inlines))
                {
                    yield return child;
                }
            }
        }
    }

    private static IEnumerable<TextElement> GetDocumentElements(FlowDocument document)
    {
        foreach (var block in document.Blocks)
        {
            yield return block;
            if (block is Paragraph paragraph)
            {
                foreach (var inline in GetInlines(paragraph.Inlines))
                {
                    yield return inline;
                }
            }
        }
    }
}
