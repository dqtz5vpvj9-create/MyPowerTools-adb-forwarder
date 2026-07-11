using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class AdbForwarderPreflightCheckViewModel
{
    public AdbForwarderPreflightCheckViewModel(AdbForwarderPreflightCheck check)
    {
        Id = check.Id;
        Title = check.Title;
        Detail = check.Detail;
        State = check.State;
    }

    public string Id { get; }
    public string Title { get; }
    public string Detail { get; }
    public AdbForwarderPreflightState State { get; }
    public bool IsPassed => State == AdbForwarderPreflightState.Passed;
    public bool IsWarning => State == AdbForwarderPreflightState.Warning;
    public bool IsFailed => State == AdbForwarderPreflightState.Failed;
    public bool IsSkipped => State == AdbForwarderPreflightState.Skipped;
    public string StateLabel => State switch
    {
        AdbForwarderPreflightState.Passed => "通过",
        AdbForwarderPreflightState.Warning => "待处理",
        AdbForwarderPreflightState.Failed => "阻塞",
        _ => "跳过"
    };
}

public sealed class AdbForwarderWorkflowStepViewModel : ObservableViewModel
{
    private AdbForwarderStepState _state;
    private string _detail;

    public AdbForwarderWorkflowStepViewModel(AdbForwarderWorkflowStep step)
    {
        Id = step.Id;
        Title = step.Title;
        _state = step.State;
        _detail = step.Detail;
    }

    public string Id { get; }
    public string Title { get; }
    public AdbForwarderStepState State => _state;
    public string Detail => _detail;
    public bool IsPending => State == AdbForwarderStepState.Pending;
    public bool IsRunning => State == AdbForwarderStepState.Running;
    public bool IsSucceeded => State is AdbForwarderStepState.Succeeded or AdbForwarderStepState.Cleaned;
    public bool IsFailed => State == AdbForwarderStepState.Failed;
    public bool IsWarning => State is AdbForwarderStepState.ApprovalRequired or AdbForwarderStepState.Canceled;
    public string StateLabel => State switch
    {
        AdbForwarderStepState.Pending => "等待",
        AdbForwarderStepState.Running => "执行中",
        AdbForwarderStepState.Succeeded => "完成",
        AdbForwarderStepState.Failed => "失败",
        AdbForwarderStepState.Skipped => "跳过",
        AdbForwarderStepState.ApprovalRequired => "等待确认",
        AdbForwarderStepState.Canceled => "已取消",
        AdbForwarderStepState.Cleaned => "已清理",
        _ => State.ToString()
    };

    public void Update(AdbForwarderWorkflowStep step)
    {
        _state = step.State;
        _detail = step.Detail;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(StateLabel));
    }
}

public sealed class AdbForwarderWorkflowLogViewModel
{
    public AdbForwarderWorkflowLogViewModel(AdbForwarderWorkflowEvent workflowEvent)
    {
        Time = workflowEvent.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Level = workflowEvent.Level;
        Step = workflowEvent.Step.Title;
        Message = workflowEvent.Message;
    }

    public string Time { get; }
    public string Level { get; }
    public string Step { get; }
    public string Message { get; }
}
