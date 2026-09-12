using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using DraftLedger.Core;

namespace DraftLedger.App;

public static class ReadingRenderer
{
    public static FlowDocument Render(string source, string font, double fontSize, Brush foreground)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily(font), FontSize = fontSize, FontWeight = FontWeights.Normal, Foreground = foreground,
            PagePadding = new Thickness(44, 30, 44, 54)
        };
        foreach (var block in MarkdownReading.Parse(source)) document.Blocks.Add(RenderBlock(block, fontSize));
        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());
        return document;
    }

    private static Block RenderBlock(ReadingBlock block, double fontSize)
    {
        if (block.Kind is ReadingBlockKind.BulletList or ReadingBlockKind.NumberedList)
        {
            var list = new System.Windows.Documents.List
            {
                MarkerStyle = block.Kind == ReadingBlockKind.NumberedList ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                StartIndex = block.Start, Padding = new Thickness(25, 0, 0, 0), Margin = new Thickness(0, 5, 0, 15)
            };
            foreach (var child in block.Children)
            {
                var item = new ListItem();
                foreach (var body in child.Children) item.Blocks.Add(RenderBlock(body, fontSize));
                if (item.Blocks.Count == 0) item.Blocks.Add(new Paragraph());
                list.ListItems.Add(item);
            }
            return list;
        }
        if (block.Kind is ReadingBlockKind.Quote or ReadingBlockKind.ListItem)
        {
            var section = new System.Windows.Documents.Section { Margin = new Thickness(16, 5, 0, 15), Padding = new Thickness(12, 0, 0, 0) };
            if (block.Kind == ReadingBlockKind.Quote) { section.BorderThickness = new Thickness(3, 0, 0, 0); section.SetResourceReference(Block.BorderBrushProperty, "LineBrush"); }
            foreach (var child in block.Children) section.Blocks.Add(RenderBlock(child, fontSize));
            return section;
        }
        var paragraph = new Paragraph { FontWeight = FontWeights.Normal, Margin = new Thickness(0, 0, 0, fontSize * 0.7), LineHeight = fontSize * 1.52 };
        if (block.Kind == ReadingBlockKind.Divider)
        {
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1); paragraph.SetResourceReference(Block.BorderBrushProperty, "LineBrush");
            paragraph.Margin = new Thickness(0, 12, 0, 22); paragraph.LineHeight = 1;
            return paragraph;
        }
        if (block.Kind == ReadingBlockKind.Heading)
        {
            paragraph.FontSize = fontSize * (block.Level switch { 1 => 1.8, 2 => 1.5, 3 => 1.25, _ => 1.1 });
            paragraph.FontWeight = FontWeights.SemiBold; paragraph.LineHeight = paragraph.FontSize * 1.3;
            paragraph.Margin = new Thickness(0, 10, 0, 15);
        }
        if (block.Kind == ReadingBlockKind.Code)
        {
            paragraph.FontFamily = new FontFamily("Consolas"); paragraph.FontSize = fontSize * 0.88;
            paragraph.Padding = new Thickness(12); paragraph.SetResourceReference(TextElement.BackgroundProperty, "PanelBrush");
        }
        foreach (var text in block.Runs)
        {
            var run = new Run(text.Text);
            if (text.Italic) run.FontStyle = FontStyles.Italic;
            if (text.Bold) run.FontWeight = FontWeights.Bold;
            if (text.Code) { run.FontFamily = new FontFamily("Consolas"); run.SetResourceReference(TextElement.BackgroundProperty, "PanelBrush"); }
            paragraph.Inlines.Add(run);
        }
        return paragraph;
    }
}
