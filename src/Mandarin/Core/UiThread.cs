namespace Mandarin.Core;

/// <summary>
/// The thread of the interface (WPF or Avalonia), so engines and the job queue can report to it. The interface sets the
/// delegates at startup; without one (self-test, command line) everything runs in place.
/// </summary>
public static class UiThread
{
    public static Func<bool> CheckAccess { get; set; } = () => true;

    /// <summary>Runs the action on the interface thread later.</summary>
    public static Action<Action> Post { get; set; } = a => a();

    /// <summary>Runs the action on the interface thread and waits for it.</summary>
    public static Action<Action> Invoke { get; set; } = a => a();
}
