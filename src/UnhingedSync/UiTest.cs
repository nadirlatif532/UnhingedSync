using System.IO;
using System.Text.Json;
using System.Windows;
using UnhingedSync.Models;
using UnhingedSync.Services;
using UnhingedSync.ViewModels;

namespace UnhingedSync;

/// <summary>
/// Builds every window and forces a layout pass without showing anything, so that all
/// ControlTemplates are actually applied.
///   UnhingedSync.exe --uitest [outputPath]
///
/// Parsing a template and applying one are different things: a template can load fine
/// from the resource dictionary and still throw when a control tries to use it. Nothing
/// else in the headless modes instantiates a window, so nothing else would catch that.
/// </summary>
public static class UiTest
{
    /// <summary>A line about as long as the ones UnrealBuildTool prints for a plugin compile.</summary>
    private static string UbtLine(int n) =>
        $"[{n}] Compile [x64] Module.HoudiniEngineEditor.{n}.cpp  E:/Work/Lahore/Plugins/HoudiniEngine/" +
        $"Intermediate/Build/Win64/x64/UnrealEditor/Development/HoudiniEngineEditor/File{n}.cpp";

    public static async Task<int> RunAsync(string? outputPath)
    {
        outputPath ??= Path.Combine(Path.GetTempPath(), "unhingedsync-uitest.json");

        var results = new List<object>();
        var failures = 0;

        void Check(string name, Action action)
        {
            try
            {
                action();
                results.Add(new { control = name, ok = true, error = (string?)null });
            }
            catch (Exception e)
            {
                failures++;
                results.Add(new { control = name, ok = false, error = $"{e.GetType().Name}: {e.Message}" });
            }
        }

        // Async work has to be awaited, not blocked on. This runs on the WPF dispatcher
        // thread, so a .GetAwaiter().GetResult() here deadlocks the moment the awaited code
        // tries to resume on that same thread.
        async Task CheckAsync(string name, Func<Task> action)
        {
            try
            {
                await action();
                results.Add(new { control = name, ok = true, error = (string?)null });
            }
            catch (Exception e)
            {
                failures++;
                results.Add(new { control = name, ok = false, error = $"{e.GetType().Name}: {e.Message}" });
            }
        }

        // Applying a template requires a measure pass; Show() is not needed and would
        // put a window on someone's screen during a test.
        static void Realise(FrameworkElement element)
        {
            element.Measure(new Size(1280, 900));
            element.Arrange(new Rect(0, 0, 1280, 900));
            element.UpdateLayout();
        }

        AppConfig? config = null;
        Check("config", () => config = ConfigLoader.Load());

        Check("MainWindow", () =>
        {
            var window = new MainWindow();
            Realise(window);
        });

        if (config is not null)
        {
            Check("ProjectView", () =>
            {
                var view = new ProjectView { DataContext = new MainViewModel(config.ProjectRoot) };
                Realise(view);
            });

            // A local build streams thousands of lines into the log. If the log box ever
            // grows to fit them instead of scrolling inside the window, it stops
            // virtualizing and every update re-lays-out a box tens of thousands of pixels
            // tall, which froze the whole UI for the length of the build.
            Check("ProjectView.logStaysBounded", () =>
            {
                var view = new ProjectView { DataContext = new MainViewModel(config.ProjectRoot) };
                view.AppendLog(string.Join('\n', Enumerable.Range(0, 4000).Select(UbtLine)));
                Realise(view);
                if (view.LogBox.ActualHeight > 900)
                    throw new InvalidOperationException(
                        $"The log box grew to {view.LogBox.ActualHeight:0}px instead of scrolling.");
            });

            // Each log flush during a build must cost the same however long the log already
            // is. Re-setting the whole Text measured 0.5-1 s per flush at a few thousand
            // lines, which kept the UI thread busy and froze the window minutes into a
            // build; appending measures ~15-20 ms. The limit is far from both.
            Check("ProjectView.logAppendStaysCheap", () =>
            {
                var view = new ProjectView { DataContext = new MainViewModel(config.ProjectRoot) };
                Realise(view);
                view.AppendLog(string.Join('\n', Enumerable.Range(0, MainViewModel.MaxLogLines).Select(UbtLine)));
                view.UpdateLayout();

                const int flushes = 40;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (var f = 0; f < flushes; f++)
                {
                    view.AppendLog(string.Join('\n',
                        Enumerable.Range(10_000 + f * 25, 25).Select(UbtLine)));
                    view.UpdateLayout();
                }
                var perFlush = sw.ElapsedMilliseconds / (double)flushes;

                if (view.LogBox.LineCount > MainViewModel.MaxLogLines)
                    throw new InvalidOperationException(
                        $"The log box holds {view.LogBox.LineCount} lines; it should trim to {MainViewModel.MaxLogLines}.");
                if (!view.LogBox.Text.EndsWith(UbtLine(10_000 + flushes * 25 - 1), StringComparison.Ordinal))
                    throw new InvalidOperationException("The newest log line is missing from the end of the log box.");
                if (perFlush > 150)
                    throw new InvalidOperationException(
                        $"A log flush took {perFlush:0} ms on a full log; it should stay flat (~20 ms).");
            });

            Check("ManageBinariesWindow", () =>
            {
                var window = new ManageBinariesWindow(config);
                Realise(window);
            });

            // The delete gate lives in an async Loaded handler, which Realise() does not
            // run, so without driving it explicitly the single most consequential piece of
            // logic in that window (may this machine delete other people's builds?) has no
            // coverage at all. Awaited here so a throw inside it fails the test.
            await CheckAsync("ManageBinariesWindow.sharePolicy", async () =>
            {
                var window = new ManageBinariesWindow(config);
                Realise(window);
                await window.ApplySharePolicyAsync();

                // Whatever Syncthing said, deleting must never be enabled without a
                // confirmed send-receive folder.
                if (window.CanDelete && !window.PolicyConfirmedWritable)
                    throw new InvalidOperationException(
                        "Delete is enabled without a confirmed writable share.");
            });
        }

        Check("ComboBox", () =>
        {
            var combo = new System.Windows.Controls.ComboBox { ItemsSource = new[] { "one", "two" } };
            var host = new System.Windows.Controls.Grid();
            host.Children.Add(combo);
            Realise(host);
        });

        // The drop-down itself is deliberately NOT opened: a Popup needs a running
        // message pump, and without one this blocks forever. Realising an item directly
        // still exercises the ComboBoxItem template, which is the part that decides
        // whether the list is legible.
        Check("ComboBoxItem", () =>
        {
            var item = new System.Windows.Controls.ComboBoxItem { Content = "an item" };
            Realise(item);
        });

        Check("ToolTip", () =>
        {
            var tip = new System.Windows.Controls.ToolTip { Content = "hello" };
            tip.Measure(new Size(400, 200));
        });

        Check("CheckBox", () =>
        {
            var box = new System.Windows.Controls.CheckBox { Content = "x", IsChecked = true };
            Realise(box);
        });

        var report = new
        {
            generatedUtc = DateTimeOffset.UtcNow.ToString("o"),
            version = EmbeddedScripts.Version,
            failures,
            checks = results
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json);
        Console.Error.WriteLine(json);

        return failures == 0 ? 0 : 1;
    }
}
