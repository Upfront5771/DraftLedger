using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DraftLedger.Core;

public enum ReadingBlockKind { Paragraph, Heading, Code, Quote, BulletList, NumberedList, ListItem, Divider }
public sealed record ReadingRun(string Text, bool Italic = false, bool Bold = false, bool Code = false);
public sealed record ReadingBlock(ReadingBlockKind Kind, IReadOnlyList<ReadingRun> Runs, IReadOnlyList<ReadingBlock> Children, int Level = 0, int Start = 1);

/// <summary>A display-only Markdown projection. It never writes back to the source manuscript.</summary>
public static class MarkdownReading
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public static IReadOnlyList<ReadingBlock> Parse(string source) => Blocks(Markdown.Parse(source, Pipeline));

    private static IReadOnlyList<ReadingBlock> Blocks(ContainerBlock container) => container.Select(ConvertBlock).ToList();

    private static ReadingBlock ConvertBlock(Block block) => block switch
    {
        HeadingBlock heading => new(ReadingBlockKind.Heading, Runs(heading.Inline), [], Level: heading.Level),
        ParagraphBlock paragraph => new(ReadingBlockKind.Paragraph, Runs(paragraph.Inline), []),
        CodeBlock code => new(ReadingBlockKind.Code, [new(code.Lines.ToString(), Code: true)], []),
        QuoteBlock quote => new(ReadingBlockKind.Quote, [], Blocks(quote)),
        ListBlock list => new(list.IsOrdered ? ReadingBlockKind.NumberedList : ReadingBlockKind.BulletList, [], Blocks(list), Start: int.TryParse(list.OrderedStart, out var start) ? Math.Clamp(start, 1, 1000000) : 1),
        ListItemBlock item => new(ReadingBlockKind.ListItem, [], Blocks(item)),
        ThematicBreakBlock => new(ReadingBlockKind.Divider, [], []),
        ContainerBlock nested => new(ReadingBlockKind.Quote, [], Blocks(nested)),
        LeafBlock leaf => new(ReadingBlockKind.Paragraph, leaf.Inline is not null ? Runs(leaf.Inline) : [new(leaf.Lines.ToString())], []),
        _ => new(ReadingBlockKind.Paragraph, [], [])
    };

    private static IReadOnlyList<ReadingRun> Runs(ContainerInline? container)
    {
        var result = new List<ReadingRun>();
        if (container is not null) AddInlines(container, result, false, false);
        return result;
    }

    private static void AddInlines(ContainerInline container, List<ReadingRun> result, bool italic, bool bold)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: result.Add(new(literal.Content.ToString(), italic, bold)); break;
                case EmphasisInline emphasis:
                    AddInlines(emphasis, result, italic || emphasis.DelimiterCount % 2 == 1, bold || emphasis.DelimiterCount >= 2);
                    break;
                case CodeInline code: result.Add(new(code.Content, italic, bold, Code: true)); break;
                case LineBreakInline line: result.Add(new(line.IsHard ? "\n" : " ", italic, bold)); break;
                case HtmlEntityInline entity: result.Add(new(entity.Transcoded.ToString(), italic, bold)); break;
                // Display labels only. The reading projection has no image loader, navigation, or browser.
                case LinkInline link:
                    if (link.IsImage) result.Add(new("[Image: ", italic, bold));
                    AddInlines(link, result, italic, bold);
                    if (link.IsImage) result.Add(new("]", italic, bold));
                    break;
                case AutolinkInline link: result.Add(new(link.Url, italic, bold)); break;
                case HtmlInline html: result.Add(new(html.Tag, italic, bold)); break;
                case ContainerInline nested: AddInlines(nested, result, italic, bold); break;
            }
        }
    }
}
