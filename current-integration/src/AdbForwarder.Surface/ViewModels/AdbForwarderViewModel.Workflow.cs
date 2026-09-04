using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using AdbForwarder.Surface.Services;

using MyPowerTools.AvaloniaSdk;
namespace AdbForwarder.Surface.ViewModels;

public sealed partial class AdbForwarderViewModel
{
    private Task SelectConnectionModeAsync(AdbForwardConnectionMode mode)
    {
        if (IsForwardBusy || mode == ConnectionMode)
        {
            return Task.CompletedTask;
        }
        if (_cleanupState.HasWork)
        {
            ForwardActionMessage = "当前转发仍拥有待清理资源；请先清理，再切换有线/无线模式。";
            return Task.CompletedTask;
        }

        ConnectionMode = mode;
        SelectedForwardDevice = DevicesForMode(mode).FirstOrDefault(device => device.State == "device")
                                ?? DevicesForMode(mode).FirstOrDefault();
        ResetForwardSteps();
        ForwardActionMessage = SelectedForwardDevice is null
            ? $"当前没有已连接的{(mode == AdbForwardConnectionMode.Wired ? "USB 有线" : "Wi-Fi 无线")} ADB 设备。"
            : $"已切换到{(mode == AdbForwardConnectionMode.Wired ? "USB" : "无线 ADB")}设备，可以开始共享。";
        NotifyForwardState();
        return Task.CompletedTask;
    }

    private async Task PreflightForwardAsync()
    {
        if (SelectedForwardDevice is null)
        {
            return;
        }

        IsForwardBusy = true;
        ForwardActionMessage = "正在运行只读预检…";
        try
        {
            _preflight = await _forwarding.PreflightAsync(
                BuildForwardRequest(),
                BrokerAvailable,
                CancellationToken.None).ConfigureAwait(true);
            ReplacePreflightChecks(_preflight.Checks);
            ForwardActionMessage = _preflight.Summary;
            OnPropertyChanged(nameof(PreflightSummary));
            OnPropertyChanged(nameof(PreflightCanRun));
        }
        catch (Exception ex)
        {
            TechnicalDetails = $"ADB 转发预检失败：{ex.GetType().Name}\n{ex.Message}";
            ForwardActionMessage = "预检失败，请查看诊断信息。";
        }
        finally
        {
            IsForwardBusy = false;
        }
    }

    private async Task RunForwardAsync(bool retry)
    {
        if (SelectedForwardDevice is null)
        {
            return;
        }

        _forwardCancellation?.Dispose();
        _forwardCancellation = new CancellationTokenSource();
        _forwardHasRun = true;
        _forwardSucceeded = false;
        _forwardCleaned = false;
        IsForwardBusy = true;
        _canRetryForward = false;
        _approvalRequired = false;
        ForwardActionMessage = retry ? "正在重试转发工作流…" : "正在建立 ADB 转发…";
        if (!retry)
        {
            _cleanupState = AdbForwardCleanupState.Empty;
            ForwardLogs.Clear();
        }
        ResetForwardSteps();

        try
        {
            var result = await _forwarding.RunForwardAsync(
                BuildForwardRequest(),
                retry ? _cleanupState : null,
                _forwardBroker,
                OnForwardEventAsync,
                _forwardCancellation.Token).ConfigureAwait(true);
            _cleanupState = result.CleanupState;
            _forwardSucceeded = result.Success;
            _canRetryForward = !result.Success;
            _approvalRequired = result.ApprovalRequired;
            _forwarding.PersistSession(BuildForwardRequest(), _cleanupState, _approvalRequired);
            ReplaceForwardSteps(result.Steps);
            ForwardActionMessage = AdbForwarderRedaction.DisplayText(result.Message, SelectedForwardDevice?.Id);
            if (result.ApprovalRequired)
            {
                ForwardActionMessage = "一次性审批已准备。点击“批准并继续”，再在 Windows UAC 中确认。";
            }
        }
        catch (Exception ex)
        {
            _forwardSucceeded = false;
            _canRetryForward = true;
            _approvalRequired = false;
            TechnicalDetails = $"ADB 转发工作流失败：{ex.GetType().Name}\n{ex.Message}";
            ForwardActionMessage = "转发失败，可以重试或清理已创建的资源。";
        }
        finally
        {
            IsForwardBusy = false;
        }
    }

    private Task CancelForwardAsync()
    {
        _forwardCancellation?.Cancel();
        ForwardActionMessage = "正在取消并回收当前子进程…";
        return Task.CompletedTask;
    }

    private async Task CleanupForwardAsync()
    {
        if (SelectedForwardDevice is null || !_cleanupState.HasWork)
        {
            return;
        }

        _forwardCancellation?.Dispose();
        _forwardCancellation = new CancellationTokenSource();
        IsForwardBusy = true;
        _forwardHasRun = true;
        ForwardActionMessage = "正在清理本次工作流创建的资源…";
        ForwardSteps.Clear();
        try
        {
            var result = await _forwarding.CleanupAsync(
                BuildForwardRequest(),
                _cleanupState,
                _forwardBroker,
                OnForwardEventAsync,
                _forwardCancellation.Token).ConfigureAwait(true);
            _cleanupState = result.CleanupState;
            _forwardSucceeded = false;
            _forwardCleaned = result.Success;
            _canRetryForward = !result.Success;
            _approvalRequired = result.ApprovalRequired;
            _forwarding.PersistSession(BuildForwardRequest(), _cleanupState, _approvalRequired);
            ReplaceForwardSteps(result.Steps);
            ForwardActionMessage = result.ApprovalRequired
                ? "一次性清理审批已准备。再次点击“清理资源”，再在 Windows UAC 中确认。"
                : AdbForwarderRedaction.DisplayText(result.Message, SelectedForwardDevice?.Id);
        }
        catch (Exception ex)
        {
            _forwardSucceeded = false;
            _canRetryForward = true;
            _approvalRequired = false;
            TechnicalDetails = $"ADB 转发清理失败：{ex.GetType().Name}\n{ex.Message}";
            ForwardActionMessage = "清理尚未完成，请重试。";
        }
        finally
        {
            IsForwardBusy = false;
        }
    }

    private async Task OnForwardEventAsync(AdbForwarderWorkflowEvent workflowEvent)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var safeStep = workflowEvent.Step with
            {
                Detail = AdbForwarderRedaction.DisplayText(workflowEvent.Step.Detail, SelectedForwardDevice?.Id)
            };
            var safeEvent = workflowEvent with
            {
                Step = safeStep,
                Message = AdbForwarderRedaction.DisplayText(workflowEvent.Message, SelectedForwardDevice?.Id)
            };
            var existing = ForwardSteps.FirstOrDefault(step => step.Id == safeStep.Id);
            if (existing is null)
            {
                ForwardSteps.Add(new AdbForwarderWorkflowStepViewModel(safeStep));
            }
            else
            {
                existing.Update(safeStep);
            }

            ForwardLogs.Add(new AdbForwarderWorkflowLogViewModel(safeEvent));
            while (ForwardLogs.Count > 200)
            {
                ForwardLogs.RemoveAt(0);
            }
            OnPropertyChanged(nameof(HasForwardLogs));
        });
    }

    private AdbForwardRequest BuildForwardRequest()
    {
        return new AdbForwardRequest(
            SelectedForwardDevice?.Id ?? "",
            LocalForwardPort,
            SharedPort,
            DevicePort,
            IncludeSsh,
            RemoteHost.Trim(),
            RemoteAdbPath.Trim(),
            ConnectionMode);
    }

    private void ReplacePreflightChecks(IEnumerable<AdbForwarderPreflightCheck> checks)
    {
        PreflightChecks.Clear();
        foreach (var check in checks)
        {
            PreflightChecks.Add(new AdbForwarderPreflightCheckViewModel(check with
            {
                Detail = AdbForwarderRedaction.DisplayText(check.Detail, SelectedForwardDevice?.Id)
            }));
        }
        OnPropertyChanged(nameof(HasPreflightChecks));
    }

    private void ReplaceForwardSteps(IEnumerable<AdbForwarderWorkflowStep> steps)
    {
        ForwardSteps.Clear();
        foreach (var step in steps)
        {
            ForwardSteps.Add(new AdbForwarderWorkflowStepViewModel(step with
            {
                Detail = AdbForwarderRedaction.DisplayText(step.Detail, SelectedForwardDevice?.Id)
            }));
        }
    }

    private void ResetForwardSteps()
    {
        ReplaceForwardSteps(_forwarding.CreatePendingSteps(ConnectionMode));
    }

    private void InvalidateForwardPreflight()
    {
        _preflight = null;
        PreflightChecks.Clear();
        OnPropertyChanged(nameof(HasPreflightChecks));
        OnPropertyChanged(nameof(PreflightSummary));
        OnPropertyChanged(nameof(PreflightCanRun));
        OnPropertyChanged(nameof(ForwardConnectCommand));
        ForwardActionMessage = "配置已更新。开始共享时会自动检查设备和端口。";
        NotifyForwardState();
    }

    private void NotifyForwardState()
    {
        (AddSelectedDeviceToConfigurationCommand as MptAsyncRelayCommand)?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPreflightForward));
        OnPropertyChanged(nameof(CanStartForward));
        OnPropertyChanged(nameof(CanRetryForward));
        OnPropertyChanged(nameof(CanCleanupForward));
        OnPropertyChanged(nameof(RetryForwardLabel));
        OnPropertyChanged(nameof(HasForwardRun));
        OnPropertyChanged(nameof(ForwardIsReady));
        OnPropertyChanged(nameof(ForwardNeedsAttention));
        OnPropertyChanged(nameof(ForwardStatusLabel));
        ((MptAsyncRelayCommand)PreflightForwardCommand).NotifyCanExecuteChanged();
        ((MptAsyncRelayCommand)StartForwardCommand).NotifyCanExecuteChanged();
        ((MptAsyncRelayCommand)RetryForwardCommand).NotifyCanExecuteChanged();
        ((MptAsyncRelayCommand)CancelForwardCommand).NotifyCanExecuteChanged();
        ((MptAsyncRelayCommand)CleanupForwardCommand).NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _forwardCancellation?.Cancel();
        _forwardCancellation?.Dispose();
    }
}
