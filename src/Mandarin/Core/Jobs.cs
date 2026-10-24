using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Mandarin.Core;

public enum JobState { Queued, Running, Done, Failed, Canceled }

public sealed class Job : INotifyPropertyChanged
{
    string _detail = "";
    double _progress = -1;
    JobState _state = JobState.Queued;
    string? _error;

    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }
    public DateTime Started { get; } = DateTime.Now;
    public CancellationTokenSource Cancel { get; } = new();
    public ObservableCollection<string> Outputs { get; } = new();

    public string Detail { get => _detail; set => Set(ref _detail, value); }

    /// <summary>0..1, or a negative value for indeterminate progress.</summary>
    public double Progress
    {
        get => _progress;
        set { Set(ref _progress, value); OnChanged(nameof(IsIndeterminate)); OnChanged(nameof(Percent)); }
    }

    public bool IsIndeterminate => _progress < 0 && _state == JobState.Running;
    public double Percent => Math.Clamp(_progress, 0, 1) * 100;

    public JobState State
    {
        get => _state;
        set
        {
            Set(ref _state, value);
            OnChanged(nameof(IsFinished)); OnChanged(nameof(IsRunning)); OnChanged(nameof(IsIndeterminate));
            OnChanged(nameof(StateText)); OnChanged(nameof(HasOutputs));
        }
    }

    public string? Error { get => _error; set => Set(ref _error, value); }
    public bool IsFinished => _state is JobState.Done or JobState.Failed or JobState.Canceled;
    public bool IsRunning => _state is JobState.Running or JobState.Queued;
    public bool HasOutputs => _state == JobState.Done && Outputs.Count > 0;

    public string StateText => _state switch
    {
        JobState.Queued => L.T("Queued"),
        JobState.Running => L.T("Running"),
        JobState.Done => L.T("Done"),
        JobState.Failed => L.T("Error"),
        _ => L.T("Canceled")
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    void OnChanged(string? name)
    {
        if (UiThread.CheckAccess()) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        else UiThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }
}

/// <summary>What conversion code sees: progress, cancellation, results.</summary>
public sealed class JobContext
{
    readonly Job _job;
    int _index;
    int _count = 1;

    public JobContext(Job job) => _job = job;

    public CancellationToken Token => _job.Cancel.Token;
    public Job Job => _job;

    public void Detail(string text) => _job.Detail = text;

    /// <summary>Progress of the current step (file) 0..1; negative means indeterminate.</summary>
    public void Report(double fraction)
    {
        if (fraction < 0) { _job.Progress = _count > 1 ? (double)_index / _count : -1; return; }
        _job.Progress = (_index + Math.Clamp(fraction, 0, 1)) / _count;
    }

    public void Step(int index, int count)
    {
        _index = index;
        _count = Math.Max(1, count);
        _job.Progress = (double)_index / _count;
    }

    public void AddOutput(string path)
    {
        if (UiThread.CheckAccess()) _job.Outputs.Add(path);
        else UiThread.Invoke(() => _job.Outputs.Add(path));
    }
}

public sealed class UserFacingException(string message) : Exception(message);

public static class JobManager
{
    static readonly SemaphoreSlim Slots = new(Math.Clamp(Environment.ProcessorCount / 4, 2, 4));

    public static ObservableCollection<Job> Jobs { get; } = new();

    public static event Action<Job>? JobStarted;
    public static event Action<Job>? JobFinished;

    /// <summary>Runs an arbitrary task and shows it in the HUD.</summary>
    public static Job Run(string title, string glyph, IReadOnlyList<string> inputs, Func<JobContext, Task> work)
    {
        var job = new Job { Title = title, Glyph = glyph, Inputs = inputs };
        var ctx = new JobContext(job);
        UiThread.Invoke(() =>
        {
            Jobs.Insert(0, job);
            while (Jobs.Count > 60) Jobs.RemoveAt(Jobs.Count - 1);
            JobStarted?.Invoke(job);
        });

        _ = Task.Run(async () =>
        {
            await Slots.WaitAsync();
            try
            {
                job.State = JobState.Running;
                await work(ctx);
                job.Progress = 1;
                job.State = job.Cancel.IsCancellationRequested ? JobState.Canceled : JobState.Done;
                if (string.IsNullOrEmpty(job.Detail) || job.State == JobState.Done)
                    job.Detail = job.Outputs.Count switch
                    {
                        0 => job.Detail,
                        1 => Path.GetFileName(job.Outputs[0]),
                        var n => L.F("Files created: {0}", n)
                    };
            }
            catch (OperationCanceledException)
            {
                job.State = JobState.Canceled;
                job.Detail = L.T("Canceled");
            }
            catch (Exception ex)
            {
                Log.Error($"Task \"{title}\" failed", ex);
                job.Error = ex is UserFacingException ? ex.Message : Describe(ex);
                job.Detail = job.Error;
                job.State = JobState.Failed;
            }
            finally
            {
                Slots.Release();
                UiThread.Post(() => JobFinished?.Invoke(job));
            }
        });
        return job;
    }

    /// <summary>Batch processing: every file separately, one failure does not stop the others.</summary>
    public static Job RunBatch(string title, string glyph, IReadOnlyList<string> inputs,
        Func<string, JobContext, Task> perFile)
    {
        return Run(title, glyph, inputs, async ctx =>
        {
            var errors = new List<string>();
            for (var i = 0; i < inputs.Count; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Step(i, inputs.Count);
                ctx.Detail(inputs.Count > 1
                    ? L.F("{0} of {1}: {2}", i + 1, inputs.Count, Path.GetFileName(inputs[i]))
                    : Path.GetFileName(inputs[i]));
                try
                {
                    await perFile(inputs[i], ctx);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error($"Failed to process {inputs[i]}", ex);
                    errors.Add($"{Path.GetFileName(inputs[i])}: {(ex is UserFacingException ? ex.Message : Describe(ex))}");
                }
            }
            ctx.Step(inputs.Count, inputs.Count);
            if (errors.Count > 0)
            {
                if (errors.Count == inputs.Count) throw new UserFacingException(string.Join("\n", errors));
                ctx.Detail(L.F("Done with errors ({0}): ", errors.Count) + string.Join("; ", errors));
            }
        });
    }

    public static string Describe(Exception ex)
    {
        var e = ex;
        while (e is AggregateException { InnerException: not null } || e is System.Reflection.TargetInvocationException { InnerException: not null })
            e = e.InnerException!;
        return e switch
        {
            UnauthorizedAccessException => L.T("Access to the file or folder was denied"),
            FileNotFoundException f => L.F("File not found: {0}", Path.GetFileName(f.FileName)),
            IOException io => io.Message,
            _ => e.Message
        };
    }
}
