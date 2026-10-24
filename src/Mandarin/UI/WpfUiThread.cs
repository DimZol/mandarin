using System.Runtime.CompilerServices;
using System.Windows;
using Mandarin.Core;

namespace Mandarin.UI;

/// <summary>Connects the job queue to the WPF dispatcher of whatever application is running (the app or the self-test).</summary>
static class WpfUiThread
{
    [ModuleInitializer]
    internal static void Init()
    {
        UiThread.CheckAccess = () => Application.Current?.Dispatcher is not { } d || d.CheckAccess();
        UiThread.Post = a =>
        {
            if (Application.Current?.Dispatcher is { } d) d.BeginInvoke(a);
            else a();
        };
        UiThread.Invoke = a =>
        {
            if (Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.Invoke(a);
            else a();
        };
    }
}
