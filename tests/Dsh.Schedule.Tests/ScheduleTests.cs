namespace Dsh.Schedule.Tests;

public static class ScheduleTests
{
    public static async Task RegistersAsTheScheduleService_AndRequiresTheTimer()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var service = new TimerScheduleProvider(ctx);
            Assert.Same(service, ctx.Get<IScheduleService>("schedule"));
            Assert.True(ctx.Get<IScheduleService>("schedule") is not null);
            await ctx.DisposeAsync();
            Assert.Null(ctx.Get<TimerScheduleProvider>("schedule"), "the registration effect unwound");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    public static void MissingTimerService_FailsLoud()
    {
        using var ctx = new Context();
        var error = Assert.Throws<InvalidOperationException>(() => new TimerScheduleProvider(ctx));
        Assert.True(error.Message.Contains("timer", StringComparison.Ordinal), "the failure names the missing timer service");
    }

    public static async Task RegisterOnce_FiresOnSchedule_ThenLeavesTheList()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var schedule = new TimerScheduleProvider(ctx);
            var fired = 0;
            var id = schedule.RegisterOnce("ping the provider", TimeSpan.FromMilliseconds(30), () => Interlocked.Increment(ref fired));
            await WaitUntil(() => fired >= 1);
            Assert.Equal(1, fired, "once task fired exactly once");
            Assert.True(schedule.List().All(task => task.Id != id), "a fired once task leaves the list");
            await Task.Delay(120);
            Assert.Equal(1, fired, "once task must not fire again");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    public static async Task RegisterOnce_Cancel_PreventsFireAndRemovesTheTask()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var schedule = new TimerScheduleProvider(ctx);
            var fired = 0;
            var id = schedule.RegisterOnce("cancel me", TimeSpan.FromMilliseconds(30), () => Interlocked.Increment(ref fired));
            Assert.True(schedule.Cancel(id), "cancel of a registered task succeeds");
            await Task.Delay(200);
            Assert.Equal(0, fired, "a cancelled once task must not fire");
            Assert.Empty(schedule.List(), "cancelling removes the task from the list");
            Assert.False(schedule.Cancel(id), "cancelling an absent task reports false");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    public static async Task RegisterRecurring_FiresRepeatedly_UntilCancelled()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var schedule = new TimerScheduleProvider(ctx);
            var fired = 0;
            var id = schedule.RegisterRecurring("tick", TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref fired));
            await WaitUntil(() => fired >= 3);
            var listed = schedule.List().Single(task => task.Id == id);
            Assert.Equal(ScheduleKind.Recurring, listed.Kind);
            Assert.True(listed.Fired >= 3, "the list reflects the recurring fire count");
            Assert.True(schedule.Cancel(id), "cancel stops the recurring task");
            var after = fired;
            await Task.Delay(150);
            Assert.Equal(after, fired, "no ticks after cancellation");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    public static void List_ReflectsRegistration()
    {
        using var ctx = new Context();
        TimerPlugin.Apply(ctx);
        var schedule = new TimerScheduleProvider(ctx);

        var onceId = schedule.RegisterOnce("remind me", TimeSpan.FromSeconds(5), () => { });
        var recurringId = schedule.RegisterRecurring("poll", TimeSpan.FromSeconds(1), () => { });

        var tasks = schedule.List();
        Assert.Equal(2, tasks.Count);
        var once = tasks.Single(task => task.Id == onceId);
        Assert.Equal(ScheduleKind.Once, once.Kind);
        Assert.Equal(5000L, once.DelayMs);
        Assert.Equal("remind me", once.Name);
        var recurring = tasks.Single(task => task.Id == recurringId);
        Assert.Equal(ScheduleKind.Recurring, recurring.Kind);
        Assert.Equal(1000L, recurring.DelayMs);

        Assert.True(schedule.Cancel(onceId));
        Assert.Equal(1, schedule.List().Count);
        Assert.Equal(recurringId, schedule.List()[0].Id, "the list preserves registration order");
    }

    public static async Task CallbackFailure_IsContainedAndLogged_AndTheScheduleSurvives()
    {
        var ctx = new Context();
        try
        {
            TimerPlugin.Apply(ctx);
            var schedule = new TimerScheduleProvider(ctx);
            var fired = 0;
            var id = schedule.RegisterRecurring("flaky", TimeSpan.FromMilliseconds(20), () =>
            {
                Interlocked.Increment(ref fired);
                throw new InvalidOperationException("the task blew up");
            });
            await WaitUntil(() => fired >= 2);
            Assert.True(fired >= 2, "the schedule kept ticking past task failures");
            Assert.True(schedule.List().Any(task => task.Id == id), "a failing recurring task stays registered");
            await Task.Delay(100);
            Assert.True(fired >= 3, "contained failures never stop a recurring task");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    public static void Registration_RejectsEmptyNamesAndNonPositiveDelays()
    {
        using var ctx = new Context();
        TimerPlugin.Apply(ctx);
        var schedule = new TimerScheduleProvider(ctx);

        Assert.Throws<ArgumentException>(() => schedule.RegisterOnce("   ", TimeSpan.FromSeconds(1), () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.RegisterOnce("x", TimeSpan.Zero, () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.RegisterRecurring("x", TimeSpan.FromMilliseconds(-1), () => { }));
        Assert.Throws<ArgumentNullException>(() => schedule.RegisterOnce("x", TimeSpan.FromSeconds(1), null!));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) return;
            await Task.Delay(10);
        }
    }
}
