using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using DraftLedger.Core;

namespace DraftLedger.WpfTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "DraftLedger-WpfTests-" + Guid.NewGuid().ToString("N"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window? window = null;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")!;
            var document = XDocument.Load(stream);
            // Load the actual library-card control, rather than reconstructing its binding in the test.
            var element = document.Descendants().Single(e => e.Name.LocalName == "ProgressBar" && ((string?)e.Attribute("Value"))?.Contains("Binding Progress", StringComparison.Ordinal) == true);
            var progress = (ProgressBar)XamlReader.Parse(element.ToString());
            var store = new ProjectStore(new Settings { StorageRoot = root });
            var created = store.Create("Existing library story");
            created.Target = 4;
            var chapter = created.Chapters[0];
            var section = chapter.Sections[0];
            section.Text = "One"; section.Statistics = WordCounter.Analyze(section.Text);
            store.SaveSection(created, chapter, section);
            // Reproduce launching with a previously saved story, which realizes a populated card.
            var reopened = store.Load(created.Folder);
            progress.DataContext = reopened;
            window = new Window { Content = progress, Width = 200, Height = 100, ShowInTaskbar = false, Opacity = 0, WindowStyle = WindowStyle.None };
            window.Show();
            AssertProgress(progress, 25, "opening a saved story renders its progress");
            reopened.Target = 2; reopened.Refresh();
            AssertProgress(progress, 50, "progress remains live after a model update");
            store.SaveMetadata(reopened);
            progress.DataContext = store.Load(reopened.Folder);
            AssertProgress(progress, 50, "reopening the persisted story renders correctly");
            var reading = DraftLedger.App.ReadingRenderer.Render("Plain *italic* and **bold** and ***both***", "Georgia", 19, Brushes.Black);
            var runs = ((Paragraph)reading.Blocks.FirstBlock).Inlines.OfType<Run>().ToList();
            if (runs.Single(r => r.Text == "italic").FontStyle != FontStyles.Italic || runs.Single(r => r.Text == "bold").FontWeight != FontWeights.Bold || runs.Single(r => r.Text == "both").FontStyle != FontStyles.Italic || runs.Single(r => r.Text == "both").FontWeight != FontWeights.Bold) throw new Exception("Native reading view lost emphasis styles.");
            Console.WriteLine("PASS: native reading runs apply italic, bold, and combined styles");
            if (new TextRange(reading.ContentStart, reading.ContentEnd).Text.TrimEnd() != "Plain italic and bold and both") throw new Exception("Reading text contains formatting delimiters.");
            Console.WriteLine("PASS: native reading text hides emphasis delimiters");
            var structured = DraftLedger.App.ReadingRenderer.Render("# Title\n\n- First\n- Second\n\n> Quote\n\n```\n*literal*\n```", "Georgia", 19, Brushes.Black);
            var blocks = structured.Blocks.Cast<System.Windows.Documents.Block>().ToList();
            if (blocks.Count != 4 || blocks[1] is not System.Windows.Documents.List list || list.ListItems.Count != 2 || blocks[2] is not System.Windows.Documents.Section) throw new Exception("Native block structure is incorrect.");
            Console.WriteLine("PASS: native renderer creates headings, lists, quotes, and code blocks");
            Console.WriteLine("6 WPF regression checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { window?.Close(); app.Shutdown(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AssertProgress(ProgressBar progress, double expected, string label)
    {
        progress.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        progress.GetBindingExpression(ProgressBar.ValueProperty)!.UpdateTarget();
        if (Math.Abs(progress.Value - expected) > 0.001) throw new Exception($"{label}: expected {expected}, got {progress.Value}.");
        Console.WriteLine("PASS: " + label);
    }
}
