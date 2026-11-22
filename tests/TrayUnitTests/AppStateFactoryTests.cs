using SillyTavernTray;
using Xunit;
using SillyTavernTray.Ui;

namespace TrayUnitTests;

public class AppStateFactoryTests
{
    private const string Metadata = "ST 1.12.0 · 端口 8000 · release · 认证未开";

    private static AppViewState Compute(
        ServerState server = ServerState.Stopped,
        bool hasOwnedProcess = false,
        BusyOperation busy = BusyOperation.None,
        ConfigurationState configuration = ConfigurationState.Configured,
        string detail = "未运行",
        IReadOnlyList<StepState>? steps = null,
        bool canCancelStep = false) =>
        AppStateFactory.Compute(server, hasOwnedProcess, busy, configuration, detail, Metadata, steps, canCancelStep);

    [Fact]
    public void BusyOperations_ShowProgressAndHideActions()
    {
        var state = Compute(busy: BusyOperation.Updating,
            steps: [new StepState("预检", StepStatus.Active)]);
        Assert.Equal(StatusTone.Busy, state.Tone);
        Assert.Equal("正在更新 SillyTavern…", state.Title);
        Assert.True(state.ShowProgress);
        Assert.Empty(state.Actions);
        Assert.Equal(PrimaryActionKind.None, state.PrimaryKind); // primary hidden while busy
        Assert.NotNull(state.Steps);
        Assert.Single(state.Steps);
    }

    [Fact]
    public void Unconfigured_ShowsOnboarding()
    {
        var state = Compute(configuration: ConfigurationState.Unconfigured);
        Assert.Equal(StatusTone.Stopped, state.Tone);
        Assert.Equal("未配置", state.Title);
        Assert.True(state.OnboardingVisible);
        Assert.Equal(PrimaryActionKind.ChooseDirectory, state.PrimaryKind);
        Assert.False(state.AddressEnabled);
    }

    [Fact]
    public void Occupied_IsAnErrorWithRetry()
    {
        var state = Compute(server: ServerState.Occupied, detail: "端口 8000 已被占用");
        Assert.Equal(StatusTone.Error, state.Tone);
        Assert.Equal("端口被占用", state.Title);
        Assert.Equal(PrimaryActionKind.Retry, state.PrimaryKind);
        Assert.False(state.AddressEnabled);
    }

    [Fact]
    public void Failed_OffersStartAgain()
    {
        var state = Compute(server: ServerState.Failed, detail: "服务异常退出（退出码 1）");
        Assert.Equal(StatusTone.Error, state.Tone);
        Assert.Equal("服务异常退出", state.Title);
        Assert.Equal(PrimaryActionKind.Start, state.PrimaryKind);
        Assert.False(state.AddressEnabled);
    }

    [Fact]
    public void Starting_IsBusyWithStopAvailable()
    {
        var state = Compute(server: ServerState.Starting, detail: "正在启动，等待服务响应…");
        Assert.Equal(StatusTone.Busy, state.Tone);
        Assert.True(state.ShowProgress);
        Assert.Single(state.Actions);
        Assert.Equal(ViewActionKind.Stop, state.Actions[0].Kind);
        Assert.True(state.Actions[0].Danger);
    }

    [Fact]
    public void Running_ShowsOpenPrimaryAndAddresses()
    {
        var state = Compute(server: ServerState.Running, hasOwnedProcess: true, detail: "运行中");
        Assert.Equal(StatusTone.Running, state.Tone);
        Assert.Equal("运行中", state.Title);
        Assert.Equal(PrimaryActionKind.OpenServer, state.PrimaryKind);
        Assert.True(state.AddressEnabled);
        Assert.False(state.OnboardingVisible);
        Assert.False(state.ShowProgress);
        Assert.Contains(state.Actions, action => action.Kind == ViewActionKind.Stop);
        Assert.Contains(state.Actions, action => action.Kind == ViewActionKind.Restart);
    }

    [Fact]
    public void OwnedProcessEvenWithoutRunningState_RendersAsRunning()
    {
        var state = Compute(server: ServerState.Stopped, hasOwnedProcess: true);
        Assert.Equal(StatusTone.Running, state.Tone);
        Assert.Equal(PrimaryActionKind.OpenServer, state.PrimaryKind);
    }

    [Fact]
    public void RestrictedDetail_IsReflectedInTitle()
    {
        var state = Compute(server: ServerState.Running, hasOwnedProcess: true, detail: "运行中，本机访问受限（401）");
        Assert.Contains("受限", state.Title);
    }

    [Fact]
    public void Stopped_ShowsStartPrimaryAndDisabledOpen()
    {
        var state = Compute(server: ServerState.Stopped, detail: "未运行");
        Assert.Equal(StatusTone.Stopped, state.Tone);
        Assert.Equal("已停止", state.Title);
        Assert.Equal(PrimaryActionKind.Start, state.PrimaryKind);
        var open = Assert.Single(state.Actions);
        Assert.Equal(ViewActionKind.OpenServer, open.Kind);
        Assert.False(open.Enabled);
    }

    [Fact]
    public void BusyTitle_MatchesBusyViewTitle()
    {
        foreach (var operation in Enum.GetValues<BusyOperation>().Where(op => op != BusyOperation.None))
        {
            var state = Compute(busy: operation);
            Assert.Equal(AppStateFactory.BusyTitle(operation).TrimEnd('…'), state.Title.TrimEnd('…'));
        }
    }
}
