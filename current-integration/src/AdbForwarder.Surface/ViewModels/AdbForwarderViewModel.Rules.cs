using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using AdbForwarder.Surface.Services;

using MyPowerTools.AvaloniaSdk;
namespace AdbForwarder.Surface.ViewModels;

public sealed partial class AdbForwarderViewModel
{
    private ICommand RouteCommand(string routeId)
    {
        return new MptAsyncRelayCommand(async () =>
        {
            SelectedRouteId = routeId;
            if (_navigateRoute is not null)
            {
                await _navigateRoute(routeId).ConfigureAwait(true);
            }
        });
    }

    private Task AddMappingAsync()
    {
        Mappings.Add(CreateEditor(new AdbForwarderMapping(
            $"mapping-{Guid.NewGuid():N}",
            $"映射 {Mappings.Count + 1}",
            true,
            "0.0.0.0",
            15555,
            "127.0.0.1",
            30555)));
        NotifyMappingCount();
        ActionMessage = "已添加一条共享端口。确认端口后即可预览更改。";
        return Task.CompletedTask;
    }

    private Task ImportCurrentRulesAsync()
    {
        foreach (var rule in CurrentRules)
        {
            if (Mappings.Any(mapping => mapping.ListenAddress == rule.ListenAddress && mapping.ListenPort == rule.ListenPort.ToString(CultureInfo.InvariantCulture)))
            {
                continue;
            }

            Mappings.Add(CreateEditor(new AdbForwarderMapping(
                $"imported-{rule.ListenAddress}-{rule.ListenPort}",
                $"Port {rule.ListenPort}",
                true,
                rule.ListenAddress,
                rule.ListenPort,
                rule.ConnectAddress,
                rule.ConnectPort)));
        }

        NotifyMappingCount();
        ActionMessage = "已导入当前 Windows 规则。保存后会纳入 MyPowerTools 管理。";
        return Task.CompletedTask;
    }

    private async Task SaveMappingsAsync()
    {
        if (_saveMappings is null || !TryBuildMappings(out var mappings))
        {
            return;
        }

        await RunBusyAsync("正在保存映射…", async () =>
        {
            await _saveMappings(mappings).ConfigureAwait(true);
            _savedMappings = mappings.ToArray();
            OnPropertyChanged(nameof(HasSavedMappings));
            OnPropertyChanged(nameof(CanRevert));
            ((MptAsyncRelayCommand)RevertCommand).NotifyCanExecuteChanged();
            IsMappingsDirty = false;
            ActionMessage = "映射已保存。";
        }).ConfigureAwait(true);
    }

    private async Task PreviewAsync()
    {
        if (_preview is null || !TryBuildMappings(out var mappings))
        {
            return;
        }

        await RunBusyAsync("正在比较配置与 Windows 当前状态…", async () =>
        {
            var plan = await _preview(mappings).ConfigureAwait(true);
            IsPreviewCurrent = true;
            Plan = plan;
            ActionMessage = PlanSummary;
        }).ConfigureAwait(true);
    }

    private async Task ExecuteBrokeredAsync(string commandId, bool useSavedMappings)
    {
        if (_executeBrokered is null)
        {
            return;
        }

        IReadOnlyList<AdbForwarderMapping> mappings;
        if (useSavedMappings)
        {
            mappings = _savedMappings;
        }
        else if (!TryBuildMappings(out mappings))
        {
            return;
        }

        await RunBusyAsync("正在准备管理员确认…", async () =>
        {
            await _executeBrokered(commandId, mappings).ConfigureAwait(true);
            ActionMessage = "一次性管理员审批已执行；刷新后可核对 Windows 当前规则。";
        }).ConfigureAwait(true);
    }

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        IsBusy = true;
        ActionMessage = message;
        try
        {
            TechnicalDetails = "";
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TechnicalDetails = $"错误类型：{ex.GetType().Name}\n模块：adb-forwarder\n详细日志可在“系统”中导出。";
            ActionMessage = "操作失败。请打开“诊断”查看技术详情。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryBuildMappings(out IReadOnlyList<AdbForwarderMapping> mappings)
    {
        var result = new List<AdbForwarderMapping>();
        var valid = true;
        foreach (var editor in Mappings)
        {
            valid &= editor.TryBuild(out var mapping);
            result.Add(mapping);
        }

        if (!valid)
        {
            ActionMessage = "请先修正标出的映射字段。";
        }

        mappings = result;
        return valid;
    }

    private AdbForwarderMappingEditorViewModel CreateEditor(AdbForwarderMapping mapping)
    {
        var editor = new AdbForwarderMappingEditorViewModel(mapping, removed =>
        {
            Mappings.Remove(removed);
            NotifyMappingCount();
            ActionMessage = "已从编辑器移除这条映射，保存后生效。";
        });
        editor.PropertyChanged += (_, _) => InvalidatePreview();
        return editor;
    }

    private void NotifyMappingCount()
    {
        OnPropertyChanged(nameof(HasMappings));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(UserFacingWarnings));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanRevert));
        ((MptAsyncRelayCommand)PreviewChangesCommand).NotifyCanExecuteChanged();
        ((MptAsyncRelayCommand)ApplyCommand).NotifyCanExecuteChanged();
        ((MptAsyncRelayCommand)RevertCommand).NotifyCanExecuteChanged();
        InvalidatePreview();
    }

    private void InvalidatePreview()
    {
        IsMappingsDirty = true;
        IsPreviewCurrent = false;
    }

    private static string LocalizeWarning(string warning)
    {
        if (warning.Contains("No configured mappings", StringComparison.OrdinalIgnoreCase))
        {
            return "还没有已管理的映射。可以导入当前规则，或手动添加一条。";
        }

        if (warning.Contains("already matches", StringComparison.OrdinalIgnoreCase))
        {
            return "当前 Windows 转发状态已与配置一致。";
        }

        if (warning.Contains("approval", StringComparison.OrdinalIgnoreCase))
        {
            return "应用更改时需要管理员确认。";
        }

        return "应用前请检查当前转发配置。";
    }

    private static string NormalizeRoute(string routeId)
    {
        return routeId is "forward" or "rules" or "devices" or "activity" or "diagnostics" or "settings" ? routeId : "forward";
    }
}
