using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Display;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class DisplayArrivalAndRetirementTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartiallyIdentifiedArrival_IsPolledUntilBothFieldsAreAvailable(bool edidFirst)
    {
        var rig = new DisplayOwnershipFixture();
        var container = Guid.NewGuid();
        int samples = 0;
        rig.Arrive = id => rig.DuringInspection = scan =>
        {
            if (++samples == 1) scan.ObservePresent(edidFirst ? id : null, edidFirst ? Guid.Empty : container);
            else scan.ObservePresent(id, container);
        };
        var svc = rig.Service();
        Assert.Equal(0u, await svc.CreateAsync(ScreenFit.Standard));
        Assert.Equal(container, Assert.Single(rig.Attachments).ContainerId);
        Assert.True(samples >= 2);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task PreexistingUnknownId_TimesOutWithoutSendingAnyDriverMutation()
    {
        var rig = new DisplayOwnershipFixture();
        var container = Guid.NewGuid();
        int samples = 0;
        rig.DuringInspection = scan => { samples++; scan.ObservePresent(null, container); };
        var ex = await Assert.ThrowsAsync<PhoneScreenException>(() => rig.Service().CreateAsync(ScreenFit.Standard));
        Assert.Equal(PhoneScreenErrorKind.CreateFailed, ex.Kind);
        Assert.True(samples >= 2);
        Assert.Empty(rig.Adds);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task KnownIdWithoutContainer_IsReservedAndDoesNotBlockOtherSlots()
    {
        var rig = new DisplayOwnershipFixture { DuringInspection = scan => scan.ObservePresent(0, Guid.Empty) };
        Assert.Equal(1u, await rig.Service().CreateAsync(ScreenFit.Standard));
        Assert.Equal([1u], rig.Adds);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task PreexistingContainerSeenWhileWaiting_CannotLaterBeClaimed()
    {
        var rig = new DisplayOwnershipFixture();
        var old = new CherryMonitor(0, Guid.NewGuid());
        rig.DuringInspection = scan => { scan.ObservePresent(null, old.ContainerId); rig.DuringInspection = null; };
        rig.Arrive = _ => rig.Present.Add(old);
        await Assert.ThrowsAsync<PhoneScreenException>(() => rig.Service().CreateAsync(ScreenFit.Standard));
        Assert.Equal([0u], rig.Adds);
        Assert.Empty(rig.Removes);
        Assert.Empty(rig.Attachments);
    }

    [Fact]
    public async Task ReusedContainerId_FromDisconnectedOrPhantomDevice_CanBeClaimedOnNewArrival()
    {
        var rig = new DisplayOwnershipFixture();
        var reused = new CherryMonitor(0, Guid.NewGuid());
        rig.Arrive = _ => rig.Present.Add(reused);
        var svc = rig.Service();
        Assert.Equal(0u, await svc.CreateAsync(ScreenFit.Standard));
        Assert.Equal(reused.ContainerId, Assert.Single(rig.Attachments).ContainerId);
    }

    [Fact]
    public async Task Reshape_ReusingSameContainerId_SucceedsViaExpectedContainer()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snug-reshape-same-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var paths = new SnugWin.Core.Persistence.SnugPaths(dir);
            var rig = new DisplayOwnershipFixture();
            var monitor = new CherryMonitor(0, Guid.NewGuid());
            rig.Arrive = _ => rig.Present.Add(monitor);
            var svc = rig.Service(paths);
            uint id = await svc.CreateAsync(ScreenFit.Standard);
            Assert.Equal(0u, id);

            var newFit = new ScreenFit(2);
            Assert.True(await svc.ReshapeAsync(id, newFit));
            Assert.Equal(newFit, svc.FitOf(id));
            Assert.Equal([monitor], new DisplayOwnership(paths.OwnedDisplaysFile).All());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryAfterPipeFailure_RechecksIdentityAndRefusesPrimaryOrSoleRemoval(bool replaced)
    {
        var rig = new DisplayOwnershipFixture();
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        rig.Desktop[0] = rig.Desktop[0] with { IsPrimary = true };
        Assert.True(svc.IsOnline(id));
        Assert.Null(svc.BoundsOf(id));
        rig.PipeUp = false;
        Assert.False(await svc.RetireAsync(id));
        Assert.True(svc.IsOnline(id));
        Assert.Empty(rig.Removes);
        rig.PipeUp = true;
        // Safety guard: while still sole/primary active output, retirement must refuse deletion.
        Assert.False(await svc.RetireAsync(id));
        Assert.Empty(rig.Removes);

        if (replaced)
        {
            rig.Unplug(id);
            rig.Plug(id);
        }
        else
        {
            rig.Desktop.Insert(0, DisplayOwnershipFixture.Monitor("physical-primary", primary: true));
            rig.Desktop[1] = rig.Desktop[1] with { IsPrimary = false };
        }
        Assert.True(await svc.RetireAsync(id));
        Assert.Equal(replaced ? [] : new[] { id }, rig.Removes);
        Assert.False(svc.IsOnline(id));
    }

    [Fact]
    public async Task ReplaceFailure_WhenRollbackCannotRemove_TracksLiveDisplayInRetiring()
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = false };
        rig.Desktop.Add(DisplayOwnershipFixture.Monitor("physical-primary", primary: true));
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        var original = Assert.Single(rig.Present);

        // During ReplaceScreenAsync: new arrival never replaces cleanly (same old monitor remains detached)
        // and the pipe drops before rollback removal can execute.
        rig.Arrive = _ =>
        {
            rig.Present.Add(original);
            rig.PipeUp = false;
        };
        Assert.False(await svc.ReshapeAsync(id, new ScreenFit(2)));
        Assert.Null(svc.FitOf(id));
        Assert.True(svc.IsOnline(id));

        rig.PipeUp = true;
        Assert.True(await svc.RetireAsync(id));
        Assert.Equal([id], rig.Removes);
        Assert.False(svc.IsOnline(id));
    }

    [Fact]
    public async Task Create_Requires60HzRefreshRateEvenWhenDimensionsMatch()
    {
        var rig = new DisplayOwnershipFixture();
        rig.AttachAs = target =>
        {
            var m = DisplayOwnershipFixture.Monitor(@"\\.\DISPLAY80", target);
            var (pw, ph) = ScreenFit.Standard.PixelSize(ScreenFit.Standard.BaseWidth);
            rig.Desktop.Add(m with { Mode = m.Mode with { Width = pw, Height = ph, RefreshRate = 144, DpiScalePercent = 200 } });
            return m.DeviceName;
        };
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        Assert.Contains(@"\\.\DISPLAY80", rig.Writes);
        Assert.Equal(60.0, Assert.Single(rig.Desktop).Mode.RefreshRate);
        Assert.NotNull(svc.BoundsOf(id));
    }

    [Fact]
    public async Task Retire_WhileIdentityIsPending_NeitherRemovesNorLosesReceipt()
    {
        var rig = new DisplayOwnershipFixture();
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        var owned = Assert.Single(rig.Present);
        rig.Unplug(id);
        rig.DuringInspection = scan => scan.ObservePresent(null, owned.ContainerId);
        Assert.False(await svc.RetireAsync(id));
        Assert.True(svc.IsOnline(id));
        Assert.Empty(rig.Removes);
        rig.DuringInspection = null;
        rig.Present.Add(owned);
        Assert.True(await svc.RetireAsync(id));
        Assert.Equal([id], rig.Removes);
    }

    [Fact]
    public async Task V2Arrival_WithDifferentModeTable_IsNotClaimedOrRolledBack()
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = false };
        rig.Arrive = id =>
        {
            rig.Plug(id);
            rig.Config[0] = rig.Config[0] with { Modes = [new(1920, 1080, [60])] };
        };
        var svc = rig.Service();
        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(ScreenFit.Standard));
        Assert.Null(svc.FitOf(0));
        Assert.Empty(rig.Attachments);
        Assert.Empty(rig.Writes);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task TryReactivate_ReattachesBoundCherryDisplayAndYieldsToSuperScreen()
    {
        var rig = new DisplayOwnershipFixture();
        var physical = DisplayOwnershipFixture.Monitor("physical-primary", primary: true);
        rig.Desktop.Add(physical);
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        Assert.True(svc.IsAttachedToDesktop(id));

        // Simulate UU super-screen taking over exclusively: physical detached + GameViewer adapter active.
        rig.Desktop.Clear();
        rig.Desktop.Add(DisplayOwnershipFixture.Monitor("uu-super", adapter: @"Root\GameViewerIddDriver", primary: true)
            with { DeviceString = "GameViewer Virtual Display Adapter" });
        Assert.False(svc.IsAttachedToDesktop(id));
        Assert.True(svc.IsRemoteExclusiveDisplayActive());
        Assert.False(await svc.TryReactivateAsync(id, ScreenFit.Standard.BaseWidth));
        Assert.False(svc.IsAttachedToDesktop(id));

        // Layout returns to normal with physical display back: TryReactivateAsync reattaches exact Cherry display.
        rig.Desktop.Clear();
        rig.Desktop.Add(physical);
        Assert.False(svc.IsRemoteExclusiveDisplayActive());
        Assert.True(await svc.TryReactivateAsync(id, ScreenFit.Standard.BaseWidth));
        Assert.True(svc.IsAttachedToDesktop(id));
        Assert.NotNull(svc.BoundsOf(id));
    }

    [Fact]
    public async Task Create_ToleratesTransientEnumerationGlitches()
    {
        var rig = new DisplayOwnershipFixture();
        var physical = DisplayOwnershipFixture.Monitor("physical-primary", primary: true);
        rig.Desktop.Add(physical);

        // 模拟Windows枚举API的瞬态问题：首次Active()调用返回空列表，之后正常
        int activeCallCount = 0;
        var originalDesktop = rig.Desktop;
        rig.TopologyOverride = new TestTopologyWithTransientGlitch(rig, () =>
        {
            activeCallCount++;
            return activeCallCount == 1 ? new List<MonitorSnapshot>() : originalDesktop;
        });

        var svc = rig.Service();
        // 应该成功创建，因为容错机制会重试
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        Assert.Equal(0u, id);
        Assert.True(activeCallCount >= 2, "应该至少尝试了2次枚举");
    }

    [Fact]
    public async Task Create_FailsWhenPrimaryDisplayActuallyLost()
    {
        var rig = new DisplayOwnershipFixture();
        var physical = DisplayOwnershipFixture.Monitor("physical-primary", primary: true);
        rig.Desktop.Add(physical);

        // 模拟真实的显示器丢失：所有枚举都返回空
        rig.TopologyOverride = new TestTopologyWithPermanentLoss();

        var svc = rig.Service();
        // 应该失败，因为显示器确实丢失了
        var ex = await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(ScreenFit.Standard));
        Assert.Equal(PhoneScreenErrorKind.CreateFailed, ex.Kind);
        Assert.True(ex.IsSafetyAbort);
    }
}
