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

            Check("PeersWindow", () =>
            {
                var window = new PeersWindow(config);
                Realise(window);
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
