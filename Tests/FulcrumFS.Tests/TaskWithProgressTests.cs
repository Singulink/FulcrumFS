using System.Globalization;
using System.Runtime.CompilerServices;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class TaskWithProgressTests
{
    public required TestContext TestContext { get; set; }

    // Creates a simple progress value with a null variant ID and the "Simple" stage carrying the given fraction.
    private static ProgressValue SimpleProgressValue(double progress) => new(null, "Simple", progress);

    // Builds an instance that reports the given progress values (when a callback is present) under the "Simple" stage and then returns the result.
    private static TaskWithProgress<int> MakeSimple(double[] values, int result = 42, CancellationToken cancellationToken = default)
    {
        return new TaskWithProgress<int>(cancellationToken, async (ct, cb) =>
        {
            if (cb is not null)
            {
                foreach (double v in values)
                    await cb(SimpleProgressValue(v));
            }

            return result;
        });
    }

    // Builds an instance whose underlying work throws the given exception.
    private static TaskWithProgress<int> MakeThrows(Exception ex, CancellationToken cancellationToken = default)
    {
        return new TaskWithProgress<int>(cancellationToken, (ct, cb) => throw ex);
    }

    // Collects the progress fractions from a ProgressValue stream (used for the simple, single-stage tests).
    private static async Task<List<double>> CollectSimpleAsync(IAsyncEnumerable<ProgressValue> source, CancellationToken cancellationToken)
    {
        var list = new List<double>();

        await foreach (ProgressValue p in source.WithCancellation(cancellationToken))
            list.Add(p.Progress);

        return list;
    }

    // Builds an instance that reports the given progress values verbatim (when a callback is present) and then returns the result.
    private static TaskWithProgress<int> Make(ProgressValue[] values, int result = 42, CancellationToken cancellationToken = default)
    {
        return new TaskWithProgress<int>(cancellationToken, async (ct, cb) =>
        {
            if (cb is not null)
            {
                foreach (var v in values)
                    await cb(v);
            }

            return result;
        });
    }

    // Collects the full ProgressValue entries from a stream (used for the multi-stage / multi-variant tests).
    private static async Task<List<ProgressValue>> CollectFullAsync(IAsyncEnumerable<ProgressValue> source, CancellationToken cancellationToken)
    {
        var list = new List<ProgressValue>();

        await foreach (ProgressValue p in source.WithCancellation(cancellationToken))
            list.Add(p);

        return list;
    }

    // Starts the task via an enumerator, waits for it to fully complete (so the queue is fully populated and coalesced), then drains all buffered progress.
    // Because the consumer does not read until production has finished, this deterministically exercises the queue-coalescing behaviour.
    private static async Task<List<ProgressValue>> CollectAfterCompletionAsync(TaskWithProgress<int> task)
    {
        var e = task.GetAsyncEnumerator();
        await using (e.ConfigureAwait(false))
        {
            await task; // Wait for production to finish; the queue is now fully populated.

            var list = new List<ProgressValue>();

            while (await e.MoveNextAsync())
                list.Add(e.Current);

            return list;
        }
    }

    // Forces a full GC pass so any abandoned task is finalized (and its unobserved exception, if any, raised).
    private static void ForceFullCollect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // --- Await / result behavior ---

    [TestMethod]
    public async Task Await_ReturnsResult()
    {
        var t = MakeSimple([], result: 123);
        (await t).ShouldBe(123);
    }

    [TestMethod]
    public async Task Await_Twice_ReturnsSameResult()
    {
        var t = MakeSimple([], result: 7);
        (await t).ShouldBe(7);
        (await t).ShouldBe(7);
    }

    [TestMethod]
    public async Task Await_Concurrent_BothReturnResult()
    {
        var t = MakeSimple([], result: 5);
        var a = Task.Run(async () => await t, TestContext.CancellationToken);
        var b = Task.Run(async () => await t, TestContext.CancellationToken);
        (await a).ShouldBe(5);
        (await b).ShouldBe(5);
    }

    // --- Exception / cancellation behavior ---

    [TestMethod]
    public async Task Await_PropagatesException()
    {
        var t = MakeThrows(new InvalidOperationException("boom"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await t);
    }

    [TestMethod]
    public async Task Await_SecondaryObserver_GetsAggregateException()
    {
        var t = MakeThrows(new InvalidOperationException("boom"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await t);

        // A secondary await observes the stored exception re-wrapped as an aggregate.
        await Should.ThrowAsync<AggregateException>(async () => await t);
    }

    [TestMethod]
    public async Task Await_PreCancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var t = MakeSimple([], cancellationToken: cts.Token);
        await Should.ThrowAsync<OperationCanceledException>(async () => await t);
    }

    [TestMethod]
    public async Task Await_Cancelled_MidOperation_Throws()
    {
        using var cts = new CancellationTokenSource();
        var t = new TaskWithProgress<int>(cts.Token, async (ct, cb) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return 1;
        });

        await Should.ThrowAsync<OperationCanceledException>(async () => await t);
    }

    [TestMethod]
    public async Task Cancellation_AbandonedAwaiter_NoUnobserved()
    {
        OperationCanceledException? captured = null;
        int count = await CountUnobservedAsync(null, Produce, filter: (ex) => ex.Flatten().InnerExceptions.Contains(captured!));
        count.ShouldBe(0, "A cancelled task is not faulted, so abandoning it must not raise an unobserved task exception.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        Task<object> Produce()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var t = new TaskWithProgress<int>(cts.Token, (ct, cb) =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(1);
                }
                catch (OperationCanceledException oce)
                {
                    captured = oce; // The exact cancellation that should remain unobserved without surfacing.
                    throw;
                }
            });

            var awaiter = t.GetAwaiter(); // Starts and cancels, never observed.
            return Task.FromResult<object>(awaiter);
        }
    }

    [TestMethod]
    public async Task Cancellation_AbandonedEnumeration_NoUnobserved()
    {
        var captured = new List<Exception>();
        int count = await CountUnobservedAsync(null, Produce, filter: (ex) => ex.Flatten().InnerExceptions.Any(captured.Contains));
        count.ShouldBe(0, "Abandoning a cancelled enumeration must not raise an unobserved task exception.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            var cts = new CancellationTokenSource();
            var t = MakeCancelling(cts.Token, captured);
            var e = t.GetAsyncEnumerator(); // Start enumeration; cancel mid-stream, do not observe.
            (await e.MoveNextAsync()).ShouldBeTrue();
            cts.Cancel();
            await e.DisposeAsync();
            return t;
        }
    }

    [TestMethod]
    public async Task Cancellation_TwoAbandonedAwaiters_NoUnobserved()
    {
        var captured = new List<Exception>();
        int count = await CountUnobservedAsync(null, Produce, filter: (ex) => ex.Flatten().InnerExceptions.Any(captured.Contains));
        count.ShouldBe(0, "Two abandoned awaiters of a cancelled task must not raise unobserved task exceptions.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        Task<object> Produce()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var t = MakeCancelling(cts.Token, captured);
            var awaiter1 = t.GetAwaiter();
            var awaiter2 = t.GetAwaiter();
            return Task.FromResult<object>((awaiter1, awaiter2));
        }
    }

    [TestMethod]
    public async Task Cancellation_EnumerationAndAwaiter_NoUnobserved()
    {
        var captured = new List<Exception>();
        int count = await CountUnobservedAsync(null, Produce, filter: (ex) => ex.Flatten().InnerExceptions.Any(captured.Contains));
        count.ShouldBe(0, "An abandoned enumeration plus an abandoned awaiter of a cancelled task must not raise unobserved task exceptions.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            var cts = new CancellationTokenSource();
            var t = MakeCancelling(cts.Token, captured);
            var e = t.GetAsyncEnumerator();
            (await e.MoveNextAsync()).ShouldBeTrue();
            cts.Cancel();
            await e.DisposeAsync();
            return t.GetAwaiter();
        }
    }

    // Builds an instance that reports one progress value then cancels, capturing the OperationCanceledException it throws so a filter can match it exactly.
    private static TaskWithProgress<int> MakeCancelling(CancellationToken cancellationToken, List<Exception> captured)
    {
        return new TaskWithProgress<int>(cancellationToken, async (ct, cb) =>
        {
            if (cb is not null)
                await cb(SimpleProgressValue(0.5));

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
                return 1;
            }
            catch (OperationCanceledException oce)
            {
                lock (captured) captured.Add(oce);
                throw;
            }
        });
    }

    [TestMethod]
    public async Task Enumeration_CancelledViaToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            if (cb is not null)
                await cb(SimpleProgressValue(0.5));

            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (ProgressValue p in t.WithCancellation(cts.Token))
                cts.Cancel();
        });
    }

    // --- Progress fixup behavior ---

    [TestMethod]
    public async Task Progress_NoneReported_YieldsNone()
    {
        var t = MakeSimple([]);
        var p = await CollectSimpleAsync(t, TestContext.CancellationToken);
        p.ShouldBe([]);
    }

    [TestMethod]
    public async Task Progress_Normal_PrependsZeroAppendsOne()
    {
        var t = MakeSimple([0.5, 0.7]);
        var p = await CollectSimpleAsync(t, TestContext.CancellationToken);
        p.ShouldBe([0.0, 0.5, 0.7, 1.0]);
    }

    [TestMethod]
    public async Task Progress_NonIncreasing_AreDropped()
    {
        var t = MakeSimple([0.0, 0.5, 0.5, 0.3, 0.7]);
        var p = await CollectSimpleAsync(t, TestContext.CancellationToken);
        p.ShouldBe([0.0, 0.5, 0.7, 1.0]);
    }

    [TestMethod]
    public async Task Progress_ReportedOne_TreatedCorrectly()
    {
        var t = MakeSimple([1.0]);
        var p = await CollectSimpleAsync(t, TestContext.CancellationToken);
        p.ShouldBe([0.0, 1.0]);
    }

    [TestMethod]
    [DataRow(1.5)]
    [DataRow(-0.5)]
    [DataRow(double.NaN)]
    public async Task Progress_OutOfRange_ThrowsDuringEnumeration(double bad)
    {
        var t = MakeSimple([bad]);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await CollectSimpleAsync(t, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Enumerate_ThenAwait_ReturnsResult()
    {
        var t = MakeSimple([0.5], result: 99);
        var p = await CollectSimpleAsync(t, TestContext.CancellationToken);
        p.ShouldBe([0.0, 0.5, 1.0]);
        (await t).ShouldBe(99);
    }

    [TestMethod]
    public async Task Enumerate_ThenAwaitTwice_ReturnsSameResult()
    {
        var t = MakeSimple([0.5], result: 31);
        var p = await CollectSimpleAsync(t, TestContext.CancellationToken);
        p.ShouldBe([0.0, 0.5, 1.0]);
        (await t).ShouldBe(31);
        (await t).ShouldBe(31);
    }

    // --- Enumeration constraints ---

    [TestMethod]
    public async Task Enumerate_AfterAwait_ThrowsInvalidOperation()
    {
        var t = MakeSimple([0.5]);
        await t;
        await Should.ThrowAsync<InvalidOperationException>(async () => await CollectSimpleAsync(t, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Enumerate_Twice_ThrowsInvalidOperation()
    {
        var t = MakeSimple([0.5]);
        _ = t.GetAsyncEnumerator(TestContext.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(async () => await CollectSimpleAsync(t, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Enumeration_Fault_ThrowsAfterDrainingProgress()
    {
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            if (cb is not null)
                await cb(SimpleProgressValue(0.5));

            throw new InvalidOperationException("mid-stream");
        });

        await Should.ThrowAsync<InvalidOperationException>(async () => await CollectSimpleAsync(t, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Enumeration_PartialProgress_DeliveredBeforeFault()
    {
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            if (cb is not null)
            {
                await cb(SimpleProgressValue(0.25));
                await cb(SimpleProgressValue(0.75));
            }

            throw new InvalidOperationException("mid-stream");
        });

        var seen = new List<double>();
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (ProgressValue p in t.WithCancellation(TestContext.CancellationToken))
                seen.Add(p.Progress);
        });

        ex.Message.ShouldBe("mid-stream");
        seen.ShouldBe([0.0, 0.25, 0.75]); // Buffered progress drained; never reaches 1.0.
    }

    [TestMethod]
    public async Task Await_PartialProgress_ThenFault_Throws()
    {
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            if (cb is not null)
                await cb(SimpleProgressValue(0.5));

            throw new InvalidOperationException("mid-stream");
        });

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await t);
        ex.Message.ShouldBe("mid-stream");
    }

    // --- ConfigureAwaitOptions ---

    [TestMethod]
    public void ConfigureAwait_InvalidOptions_Throws()
    {
        var t = MakeSimple([]);
        Should.Throw<ArgumentOutOfRangeException>(() => t.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing));
    }

    [TestMethod]
    public async Task ConfigureAwait_ForceYielding_AwaitReturnsResult()
    {
        var t = MakeSimple([], result: 11);
        (await t.ConfigureAwait(ConfigureAwaitOptions.ForceYielding)).ShouldBe(11);
    }

    [TestMethod]
    public async Task ConfigureAwait_Enumeration_ReturnsResult()
    {
        var t = MakeSimple([0.5], result: 8);
        var p = await CollectSimpleAsync(t.ConfigureAwait(ConfigureAwaitOptions.ForceYielding), TestContext.CancellationToken);
        p.ShouldBe([0.0, 0.5, 1.0]);
        (await t.ConfigureAwait(false)).ShouldBe(8);
    }

    // --- Unobserved exception behavior ---

    [TestMethod]
    public async Task Enumeration_AbandonedWithoutAwait_FaultIsUnobserved()
    {
        bool observed = await RunFaultedEnumerationAbandonedAsync(awaitAfter: false);
        observed.ShouldBeTrue("Abandoning a faulted enumeration without awaiting should surface as an unobserved task exception.");
    }

    [TestMethod]
    public async Task Enumeration_AbandonedThenAwaited_NoUnobserved()
    {
        bool observed = await RunFaultedEnumerationAbandonedAsync(awaitAfter: true);
        observed.ShouldBeFalse("Awaiting after abandoning enumeration should observe the fault, so no unobserved exception should fire.");
    }

    [TestMethod]
    public async Task Await_StartedButDiscarded_FaultIsUnobserved()
    {
        var marker = new InvalidOperationException("discarded");
        int count = await CountUnobservedAsync(marker, Produce);
        count.ShouldBe(1, "Starting an await (GetAwaiter) but never observing the result should surface as an unobserved task exception.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            await Task.CompletedTask;
            var t = MakeThrows(marker);
            return t.GetAwaiter(); // Starts the task but never observes its result.
        }
    }

    [TestMethod]
    public async Task Await_Observed_NoUnobserved()
    {
        var marker = new InvalidOperationException("observed");
        int count = await CountUnobservedAsync(marker, Produce);
        count.ShouldBe(0, "Awaiting normally observes the fault, so no unobserved exception should fire.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            var t = MakeThrows(marker);
            await Should.ThrowAsync<InvalidOperationException>(async () => await t);
            return t;
        }
    }

    [TestMethod]
    public async Task TwoAwaiters_NeitherObserved_TwoUnobserved()
    {
        var marker = new InvalidOperationException("two-awaiters");
        int count = await CountUnobservedAsync(marker, Produce);
        count.ShouldBe(2, "Two unobserved awaiters should each surface their own unobserved task exception.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            await Task.CompletedTask;
            var t = MakeThrows(marker);
            var awaiter1 = t.GetAwaiter();
            var awaiter2 = t.GetAwaiter();
            return (awaiter1, awaiter2);
        }
    }

    [TestMethod]
    public async Task TwoAwaiters_OneObserved_OneUnobserved()
    {
        var marker = new InvalidOperationException("two-one-observed");
        int count = await CountUnobservedAsync(marker, Produce);
        count.ShouldBe(1, "Observing one of two awaiters should leave exactly one unobserved task exception.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            var t = MakeThrows(marker);
            var awaiter = t.GetAwaiter(); // Unobserved.
            await Should.ThrowAsync<AggregateException>(async () => await t); // Observed.
            return awaiter;
        }
    }

    [TestMethod]
    public async Task TwoAwaiters_BothObserved_NoUnobserved()
    {
        var marker = new InvalidOperationException("two-both-observed");
        int count = await CountUnobservedAsync(marker, Produce);
        count.ShouldBe(0, "Observing both awaiters should leave no unobserved task exception.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            var t = MakeThrows(marker);
            await Should.ThrowAsync<InvalidOperationException>(async () => await t);
            await Should.ThrowAsync<AggregateException>(async () => await t);
            return t;
        }
    }

    [TestMethod]
    public async Task EnumerateToFault_Observed_NoUnobserved()
    {
        var marker = new InvalidOperationException("enum-fault-observed");
        int count = await CountUnobservedAsync(marker, Produce);
        count.ShouldBe(0, "Observing the fault thrown by the enumeration should leave no unobserved task exception.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            var t = MakeThrows(marker);
            await Should.ThrowAsync<Exception>(async () => await CollectSimpleAsync(t, TestContext.CancellationToken)); // Enumerate to completion; fault thrown and observed.
            return t;
        }
    }

    [TestMethod]
    public async Task Enumerate_Unobserved_ThenAwaitObserved_ObservesAll()
    {
        var marker = new InvalidOperationException("enum-then-await");
        int count = await CountUnobservedAsync(marker, Produce);
        count.ShouldBe(0, "Observing the fault via a later await should observe the abandoned enumeration's fault too.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            var t = MakeThrows(marker);
            var e = t.GetAsyncEnumerator(TestContext.CancellationToken); // Start enumeration, do not observe its fault.
            await e.DisposeAsync();
            await Should.ThrowAsync<Exception>(async () => await t); // Observe via await.
            return t;
        }
    }

    [TestMethod]
    public async Task Enumerate_Unobserved_AndAwaitUnobserved_OneUnobserved()
    {
        var marker = new InvalidOperationException("enum-and-await");
        int count = await CountUnobservedAsync(marker, Produce);

        // The unobserved await observes the abandoned enumeration's fault (so that does not count), then surfaces its own unobserved fault: net 1.
        count.ShouldBe(1, "The await observes the enumeration's fault but is itself unobserved, surfacing one unobserved task exception.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            var t = MakeThrows(marker);
            var e = t.GetAsyncEnumerator(TestContext.CancellationToken); // Start enumeration, do not observe its fault.
            await e.DisposeAsync();
            return t.GetAwaiter(); // Start an await, do not observe its fault.
        }
    }

    [TestMethod]
    public async Task EnumeratePartialProgress_StoppedBeforeFault_Unobserved()
    {
        var marker = new InvalidOperationException("partial-stop");
        int count = await CountUnobservedAsync(marker, Produce);
        count.ShouldBe(1, "Stopping after partial progress (before the fault is reached) does not observe it, so it surfaces unobserved.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            var t = new TaskWithProgress<int>(default, async (ct, cb) =>
            {
                if (cb is not null)
                    await cb(SimpleProgressValue(0.5));

                throw marker;
            });

            var e = t.GetAsyncEnumerator(TestContext.CancellationToken);
            (await e.MoveNextAsync()).ShouldBeTrue(); // Read only buffered progress, not far enough to reach the fault.
            await e.DisposeAsync();
            return t;
        }
    }

    [TestMethod]
    public async Task EnumeratePartialProgress_IteratedToFault_Observed()
    {
        var marker = new InvalidOperationException("partial-full");
        int count = await CountUnobservedAsync(marker, Produce);
        count.ShouldBe(0, "Iterating far enough to surface the fault observes it, so nothing is unobserved.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            var t = new TaskWithProgress<int>(default, async (ct, cb) =>
            {
                if (cb is not null)
                    await cb(SimpleProgressValue(0.5));

                throw marker;
            });

            await Should.ThrowAsync<InvalidOperationException>(async () => await CollectSimpleAsync(t, TestContext.CancellationToken)); // Drain all progress, then hit fault.
            return t;
        }
    }

    private async Task<bool> RunFaultedEnumerationAbandonedAsync(bool awaitAfter)
    {
        var marker = new InvalidOperationException("abandoned");
        return await CountUnobservedAsync(marker, Produce) > 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<object> Produce()
        {
            var t = new TaskWithProgress<int>(default, async (ct, cb) =>
            {
                if (cb is not null)
                    await cb(SimpleProgressValue(0.5));

                throw marker;
            });

            var e = t.GetAsyncEnumerator(TestContext.CancellationToken);
            (await e.MoveNextAsync()).ShouldBeTrue();
            await e.DisposeAsync();

            if (awaitAfter)
                await Should.ThrowAsync<Exception>(async () => await t);

            return t;
        }
    }

    // Returns how many times the given marker exception surfaced as an unobserved task exception. The producer creates and abandons the task(s) and returns the
    // awaiter (or instance) to keep alive across handler registration so a fault cannot be collected before the handler exists. An optional filter overrides the
    // default marker matching (used to assert that a specific captured exception never surfaces).
    private async Task<int> CountUnobservedAsync(Exception? marker, Func<Task<object>> produce, Func<AggregateException, bool>? filter = null)
    {
        filter ??= (ex) => ex.Flatten().InnerExceptions.Contains(marker!);
        int count = 0;
        void Handler(object? s, UnobservedTaskExceptionEventArgs e)
        {
            if (filter(e.Exception))
            {
                Interlocked.Increment(ref count);
                e.SetObserved();
            }
        }

        // Produce, register the handler, and drop the keep-alive all in a non-inlined frame so the task(s) are not rooted by the GC loop below.
        await ProduceAndRegister();
        try
        {
            for (int i = 0; i < 5; i++)
            {
                ForceFullCollect();
                await Task.Delay(20, TestContext.CancellationToken);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }

        return count;

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task ProduceAndRegister()
        {
            object keepAlive = await produce();
            TaskScheduler.UnobservedTaskException += Handler;
            GC.KeepAlive(keepAlive);
        }
    }

    // --- Multi-stage / variant progress ---

    [TestMethod]
    public async Task Progress_MultipleStages_MultipleReportsEach_AutoClosesPrevious()
    {
        var t = Make([new(null, "A", 0.2), new(null, "A", 0.6), new(null, "B", 0.3), new(null, "B", 0.9)]);
        var p = await CollectFullAsync(t, TestContext.CancellationToken);
        p.ShouldBe(
        [
            new(null, "A", 0.0), new(null, "A", 0.2), new(null, "A", 0.6), new(null, "A", 1.0),
            new(null, "B", 0.0), new(null, "B", 0.3), new(null, "B", 0.9), new(null, "B", 1.0),
        ]);
    }

    [TestMethod]
    public async Task Progress_DifferentVariants_MultipleReportsEach_AreDistinctStages()
    {
        var t = Make([new("v1", "S", 0.2), new("v1", "S", 0.4), new("v2", "S", 0.5), new("v2", "S", 0.8)]);
        var p = await CollectFullAsync(t, TestContext.CancellationToken);
        p.ShouldBe(
        [
            new("v1", "S", 0.0), new("v1", "S", 0.2), new("v1", "S", 0.4), new("v1", "S", 1.0),
            new("v2", "S", 0.0), new("v2", "S", 0.5), new("v2", "S", 0.8), new("v2", "S", 1.0),
        ]);
    }

    [TestMethod]
    public async Task Progress_StageAlreadyAtOne_NotDoubleClosed_MidPosition()
    {
        var t = Make([new(null, "A", 1.0), new(null, "B", 0.5)]);
        var p = await CollectFullAsync(t, TestContext.CancellationToken);
        p.ShouldBe(
        [
            new(null, "A", 0.0), new(null, "A", 1.0),
            new(null, "B", 0.0), new(null, "B", 0.5), new(null, "B", 1.0),
        ]);
    }

    [TestMethod]
    public async Task Progress_LastStageEndsAtOne_NotDoubleClosed_EndPosition()
    {
        // The final completion close is handled separately to the mid-stream close, so verify the end position is not double-closed either.
        var t = Make([new(null, "A", 0.5), new(null, "A", 1.0)]);
        var p = await CollectFullAsync(t, TestContext.CancellationToken);
        p.ShouldBe(
        [
            new(null, "A", 0.0), new(null, "A", 0.5), new(null, "A", 1.0),
        ]);
    }

    [TestMethod]
    public async Task Progress_ReReportingStage_Throws()
    {
        var t = Make([new(null, "A", 0.5), new(null, "B", 0.5), new(null, "A", 0.7)]);
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await CollectFullAsync(t, TestContext.CancellationToken));
        ex.Message.ShouldBe("Stage 'A' for variant '' has already been reported.");
    }

    // --- Stage display messages ---

    [TestMethod]
    public async Task Message_FlowsThrough_AndAppliesToSynthesizedValues()
    {
        var t = Make([new(null, "S", 0.5, "working")]);
        var p = await CollectFullAsync(t, TestContext.CancellationToken);
        p.ShouldBe(
        [
            new(null, "S", 0.0), // The synthesized 0.0 carries no message - the message only applies from the reported value onward.
            new(null, "S", 0.5, "working"),
            new(null, "S", 1.0, "working"),
        ]);
    }

    [TestMethod]
    public async Task Message_OnlyChange_ReportedAtSameProgress()
    {
        var t = Make([new(null, "S", 0.5, "a"), new(null, "S", 0.5, "b")]);
        var p = await CollectFullAsync(t, TestContext.CancellationToken);
        p.ShouldBe(
        [
            new(null, "S", 0.0),
            new(null, "S", 0.5, "a"),
            new(null, "S", 0.5, "b"),
            new(null, "S", 1.0, "b"),
        ]);
    }

    [TestMethod]
    public async Task Message_ChangeWithLowerProgress_ReusesLastProgress()
    {
        var t = Make([new(null, "S", 0.5, "a"), new(null, "S", 0.3, "b")]);
        var p = await CollectFullAsync(t, TestContext.CancellationToken);
        p.ShouldBe(
        [
            new(null, "S", 0.0),
            new(null, "S", 0.5, "a"),
            new(null, "S", 0.5, "b"),
            new(null, "S", 1.0, "b"),
        ]);
    }

    [TestMethod]
    public async Task Message_Unchanged_NonIncreasingProgress_Dropped()
    {
        var t = Make([new(null, "S", 0.5, "a"), new(null, "S", 0.5, "a"), new(null, "S", 0.3, "a")]);
        var p = await CollectFullAsync(t, TestContext.CancellationToken);
        p.ShouldBe(
        [
            new(null, "S", 0.0),
            new(null, "S", 0.5, "a"),
            new(null, "S", 1.0, "a"),
        ]);
    }

    [TestMethod]
    public async Task Message_ClearedToNull_Reported()
    {
        var t = Make([new(null, "S", 0.5, "a"), new(null, "S", 0.6, null)]);
        var p = await CollectFullAsync(t, TestContext.CancellationToken);
        p.ShouldBe(
        [
            new(null, "S", 0.0),
            new(null, "S", 0.5, "a"),
            new(null, "S", 0.6),
            new(null, "S", 1.0),
        ]);
    }

    [TestMethod]
    public async Task Message_NewStage_ResetsMessage()
    {
        var t = Make([new(null, "A", 0.5, "a"), new(null, "B", 0.5)]);
        var p = await CollectFullAsync(t, TestContext.CancellationToken);
        p.ShouldBe(
        [
            new(null, "A", 0.0),
            new(null, "A", 0.5, "a"),
            new(null, "A", 1.0, "a"), // Stage close carries the last message for the stage.
            new(null, "B", 0.0),
            new(null, "B", 0.5),
            new(null, "B", 1.0),
        ]);
    }

    [TestMethod]
    public async Task Message_BeyondQueueLimit_CoalescedKeepsLatestMessage()
    {
        // Same coalescing shape as Progress_BeyondQueueLimit_ExcessCoalescedIntoLastSlot, but each report carries a distinct message: the coalesced tail must
        // keep the latest message, and the close inherits it.
        var reports = new List<ProgressValue> { new(null, "S", 0.0, "m0") };

        for (int i = 1; i <= 50; i++)
            reports.Add(new(null, "S", i / 64.0, string.Create(CultureInfo.InvariantCulture, $"m{i}")));

        var t = Make([.. reports], result: 1);
        var seen = await CollectAfterCompletionAsync(t);

        var expected = new List<ProgressValue>();

        for (int i = 0; i <= 14; i++)
            expected.Add(new(null, "S", i / 64.0, string.Create(CultureInfo.InvariantCulture, $"m{i}")));

        expected.Add(new(null, "S", 50 / 64.0, "m50"));
        expected.Add(new(null, "S", 1.0, "m50"));

        seen.ShouldBe(expected);
    }

    [TestMethod]
    public async Task Message_MultipleUpdatesAtOne_BeyondQueueLimit_KeepsOnlyLatest()
    {
        // Fill the stage's queue to the 16-item coalescing limit, then report several message-only updates at 1.0: only the latest 1.0 should be kept.
        var reports = new List<ProgressValue> { new(null, "S", 0.0) };

        for (int i = 1; i <= 15; i++)
            reports.Add(new(null, "S", i / 64.0));

        reports.Add(new(null, "S", 1.0, "a"));
        reports.Add(new(null, "S", 1.0, "b"));
        reports.Add(new(null, "S", 1.0, "c"));

        var t = Make([.. reports], result: 1);
        var seen = await CollectAfterCompletionAsync(t);

        // 0.0 plus 1/64..14/64 are preserved, 15/64 is coalesced away by the first 1.0, and each subsequent 1.0 replaces the previous one.
        var expected = new List<ProgressValue>();

        for (int i = 0; i <= 14; i++)
            expected.Add(new(null, "S", i / 64.0));

        expected.Add(new(null, "S", 1.0, "c"));

        seen.ShouldBe(expected);
    }

    [TestMethod]
    public async Task Message_MultipleUpdatesAtZero_BeyondQueueLimit_KeepsOnlyLatest()
    {
        // Message-only updates at 0.0 accumulate until the 16-item coalescing limit, after which each replaces the previous queued 0.0.
        var reports = new List<ProgressValue>();

        for (int i = 0; i <= 20; i++)
            reports.Add(new(null, "S", 0.0, string.Create(CultureInfo.InvariantCulture, $"m{i}")));

        var t = Make([.. reports], result: 1);
        var seen = await CollectAfterCompletionAsync(t);

        // m0..m14 fill under the limit, m15 hits the 16th slot, then m16..m20 each replace it, leaving m20; the stage then closes at 1.0 with the last message.
        var expected = new List<ProgressValue>();

        for (int i = 0; i <= 14; i++)
            expected.Add(new(null, "S", 0.0, string.Create(CultureInfo.InvariantCulture, $"m{i}")));

        expected.Add(new(null, "S", 0.0, "m20"));
        expected.Add(new(null, "S", 1.0, "m20"));

        seen.ShouldBe(expected);
    }

    // --- Progress queue coalescing ---

    [TestMethod]
    public async Task Progress_AtQueueLimit_NoneDropped()
    {
        // 0.0 plus 15 in-range values fill the queue to exactly 16 items - the limit - so nothing is coalesced.
        var reports = new List<ProgressValue> { new(null, "S", 0.0) };

        for (int i = 1; i <= 15; i++)
            reports.Add(new(null, "S", i / 64.0));

        var t = Make([.. reports], result: 1);
        var seen = await CollectAfterCompletionAsync(t);

        var expected = new List<ProgressValue>();

        for (int i = 0; i <= 15; i++)
            expected.Add(new(null, "S", i / 64.0));

        expected.Add(new(null, "S", 1.0));

        seen.ShouldBe(expected);
    }

    [TestMethod]
    public async Task Progress_BeyondQueueLimit_ExcessCoalescedIntoLastSlot()
    {
        // Once 16 items are queued for the stage (0.0 plus 15 in-range), every subsequent value replaces the last queued slot rather than growing the queue.
        // So reporting many more values collapses the tail down to just the most recent one.
        var reports = new List<ProgressValue> { new(null, "S", 0.0) };

        for (int i = 1; i <= 50; i++)
            reports.Add(new(null, "S", i / 64.0));

        var t = Make([.. reports], result: 1);
        var seen = await CollectAfterCompletionAsync(t);

        // 0.0 plus 1/64..14/64 are preserved (15 items), the entire coalesced tail collapses to the latest value (50/64), then the stage closes at 1.0.
        var expected = new List<ProgressValue>();

        for (int i = 0; i <= 14; i++)
            expected.Add(new(null, "S", i / 64.0));

        expected.Add(new(null, "S", 50 / 64.0));
        expected.Add(new(null, "S", 1.0));

        seen.ShouldBe(expected);
    }

    [TestMethod]
    public async Task Progress_StageEndsAtExplicitOne_NextStageCoalescesIndependently()
    {
        // Stage A ends at an explicit 1.0 (not the auto-close). Stage B should still coalesce based only on its own queued items, independent of A.
        var reports = new List<ProgressValue> { new(null, "A", 0.0) };

        for (int i = 1; i <= 40; i++)
            reports.Add(new(null, "A", i / 64.0));

        reports.Add(new(null, "A", 1.0)); // A ends at an explicit 1.0.
        reports.Add(new(null, "B", 0.0));

        for (int i = 1; i <= 40; i++)
            reports.Add(new(null, "B", i / 64.0));

        var t = Make([.. reports], result: 1);
        var seen = await CollectAfterCompletionAsync(t);

        // B keeps the same full shape as a normal stage: 0.0 plus 1/64..14/64, then the coalesced latest (40/64), then a 1.0 close.
        var expectedB = new List<ProgressValue>();

        for (int i = 0; i <= 14; i++)
            expectedB.Add(new(null, "B", i / 64.0));

        expectedB.Add(new(null, "B", 40 / 64.0));
        expectedB.Add(new(null, "B", 1.0));

        seen.Where(p => p.Stage == "B").ShouldBe(expectedB);
    }

    [TestMethod]
    public async Task Progress_TwoStages_CoalesceLimitIsPerStage()
    {
        // Each stage should coalesce based only on its own queued items, independent of how many items from the previous stage are still buffered ahead of it.
        var reports = new List<ProgressValue> { new(null, "A", 0.0) };

        for (int i = 1; i <= 40; i++)
            reports.Add(new(null, "A", i / 64.0));

        reports.Add(new(null, "B", 0.0));

        for (int i = 1; i <= 40; i++)
            reports.Add(new(null, "B", i / 64.0));

        var t = Make([.. reports], result: 1);
        var seen = await CollectAfterCompletionAsync(t);

        // Both stages preserve exactly the same shape: 0.0 plus 1/64..14/64, then the coalesced latest (40/64), then a 1.0 close.
        var expected = new List<ProgressValue>();

        foreach (string stage in (string[])["A", "B"])
        {
            for (int i = 0; i <= 14; i++)
                expected.Add(new(null, stage, i / 64.0));

            expected.Add(new(null, stage, 40 / 64.0));
            expected.Add(new(null, stage, 1.0));
        }

        seen.ShouldBe(expected);
    }

    // --- Pre/post tasks ---

    [TestMethod]
    public async Task PreTask_RunsBeforeMain_InReverseAdditionOrder()
    {
        var log = new List<string>();
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            log.Add("main");
            return 1;
        });
        t.AddPreTask(async (succeeding) => log.Add("pre1"));
        t.AddPreTask(async (succeeding) => log.Add("pre2"));

        (await t).ShouldBe(1);
        log.ShouldBe(["pre2", "pre1", "main"]);
    }

    [TestMethod]
    public async Task PreTask_AfterFailure_ReceivesSucceedingFalse()
    {
        var log = new List<string>();
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            log.Add("main");
            return 1;
        });
        t.AddPreTask(async (succeeding) => log.Add($"pre1:{succeeding}")); // Runs second.
        t.AddPreTask(async (succeeding) => // Runs first.
        {
            log.Add($"pre2:{succeeding}");
            throw new InvalidOperationException("pre-fail");
        });

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await t);
        ex.Message.ShouldBe("pre-fail");
        log.ShouldBe(["pre2:True", "pre1:False"]);
    }

    [TestMethod]
    public async Task PreTaskFailure_SkipsMain_ButRunsPostTasks()
    {
        bool mainRan = false;
        bool? postSuccess = null;
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            mainRan = true;
            return 1;
        });
        t.AddPreTask(async (succeeding) =>
        {
            throw new InvalidOperationException("pre-fail");
        });
        t.AddPostTask(async (success, result) =>
        {
            postSuccess = success;
        });

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await t);
        ex.Message.ShouldBe("pre-fail");
        mainRan.ShouldBeFalse();
        postSuccess.ShouldBe(false);
    }

    [TestMethod]
    public async Task PostTask_RunsAfterMain_InAdditionOrder_WithResult()
    {
        var log = new List<string>();
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            log.Add("main");
            return 7;
        });
        t.AddPostTask(async (success, result) => log.Add($"post1:{success}:{result}"));
        t.AddPostTask(async (success, result) => log.Add($"post2:{success}:{result}"));

        (await t).ShouldBe(7);
        log.ShouldBe(["main", "post1:True:7", "post2:True:7"]);
    }

    [TestMethod]
    public async Task PostTask_OnFailure_ReceivesFalseResult()
    {
        bool? sawSuccess = null;
        var t = MakeThrows(new InvalidOperationException("boom"));
        t.AddPostTask(async (success, result) =>
        {
            sawSuccess = success;
        });

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await t);
        ex.Message.ShouldBe("boom");
        sawSuccess.ShouldBe(false);
    }

    [TestMethod]
    public async Task PostTaskFailure_Surfaces()
    {
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 1);
        t.AddPostTask(async (success, result) =>
        {
            throw new InvalidOperationException("post-fail");
        });

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await t);
        ex.Message.ShouldBe("post-fail");
    }

    [TestMethod]
    public async Task AddTask_AfterStarted_Throws()
    {
        var t = MakeSimple([0.5]);
        await t;

        var preEx = Should.Throw<InvalidOperationException>(() => t.AddPreTask(async (succeeding) => { }));
        preEx.Message.ShouldBe("Cannot add a task after the task has already begun or been casted.");
        var postEx = Should.Throw<InvalidOperationException>(() => t.AddPostTask(async (success, result) => { }));
        postEx.Message.ShouldBe("Cannot add a task after the task has already begun or been casted.");
    }

    // --- Pre/post task aggregation & cancellation ---

    [TestMethod]
    public async Task PrePostTasks_PreAndPostFailures_Aggregate()
    {
        bool mainRan = false;
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            mainRan = true;
            return 1;
        });
        t.AddPreTask(async (succeeding) =>
        {
            throw new InvalidOperationException("pre-fail");
        });
        t.AddPostTask(async (success, result) =>
        {
            throw new InvalidOperationException("post-fail");
        });

        var ex = await Should.ThrowAsync<AggregateException>(async () => await t);
        ex.Message.ShouldStartWith("One or more exceptions occurred during the execution of the task.");
        ex.InnerExceptions.Select((e) => e.Message).ShouldBe(["pre-fail", "post-fail"]);
        mainRan.ShouldBeFalse(); // The main task is skipped once a pre-task has failed.
    }

    [TestMethod]
    public async Task PrePostTasks_MainAndPostFailures_Aggregate()
    {
        var t = MakeThrows(new InvalidOperationException("main-fail"));
        t.AddPostTask(async (success, result) =>
        {
            throw new InvalidOperationException("post-fail");
        });

        var ex = await Should.ThrowAsync<AggregateException>(async () => await t);
        ex.InnerExceptions.Select((e) => e.Message).ShouldBe(["main-fail", "post-fail"]);
    }

    [TestMethod]
    public async Task PostTasks_MultipleFailures_Aggregate()
    {
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 1);
        t.AddPostTask(async (success, result) =>
        {
            throw new InvalidOperationException("post1-fail");
        });
        t.AddPostTask(async (success, result) =>
        {
            throw new InvalidOperationException("post2-fail");
        });

        var ex = await Should.ThrowAsync<AggregateException>(async () => await t);
        ex.InnerExceptions.Select((e) => e.Message).ShouldBe(["post1-fail", "post2-fail"]);
    }

    [TestMethod]
    public async Task PostTask_OnlyCancellation_ThrowsCancellation()
    {
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 1);
        t.AddPostTask(async (success, result) =>
        {
            success.ShouldBeTrue();
            throw new OperationCanceledException();
        });

        // A lone cancellation from a post-task surfaces as a cancellation (not aggregated or wrapped).
        await Should.ThrowAsync<OperationCanceledException>(async () => await t);
    }

    [TestMethod]
    public async Task PostTasks_CancellationPlusFailure_DropsCancellation_ThrowsReal()
    {
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 1);
        t.AddPostTask(async (success, result) =>
        {
            throw new OperationCanceledException(); // Recorded first (while still succeeding).
        });
        t.AddPostTask(async (success, result) =>
        {
            success.ShouldBeFalse();
            throw new InvalidOperationException("real-fail");
        });

        // The cancellation is dropped in favour of the real exception, which surfaces directly (not aggregated).
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await t);
        ex.Message.ShouldBe("real-fail");
    }

    [TestMethod]
    public async Task PostTask_IllegalCancellationAfterFailure_Ignored()
    {
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 1);
        t.AddPostTask(async (success, result) =>
        {
            throw new InvalidOperationException("real-fail"); // Recorded first.
        });
        t.AddPostTask(async (success, result) =>
        {
            success.ShouldBeFalse();
            throw new OperationCanceledException(); // Illegal once already failing - must be ignored entirely.
        });

        // Only the real exception surfaces; the illegal post-failure cancellation is swallowed (no aggregate).
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await t);
        ex.Message.ShouldBe("real-fail");
    }

    [TestMethod]
    public async Task Cancellation_FromPreTask_SurfacesAsCancellation()
    {
        bool mainRan = false;
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            mainRan = true;
            return 1;
        });
        t.AddPreTask(async (succeeding) =>
        {
            throw new OperationCanceledException();
        });

        await Should.ThrowAsync<OperationCanceledException>(async () => await t);
        mainRan.ShouldBeFalse(); // Cancellation in a pre-task skips the main task.
    }

    [TestMethod]
    public async Task Cancellation_FromMainTask_SurfacesAsCancellation()
    {
        bool? postSuccess = null;
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            throw new OperationCanceledException();
        });
        t.AddPostTask(async (success, result) =>
        {
            postSuccess = success;
        });

        await Should.ThrowAsync<OperationCanceledException>(async () => await t);
        postSuccess.ShouldBe(false); // The post-task still runs and observes the failure.
    }

    [TestMethod]
    public async Task Cancellation_MultiplePostTaskOCEs_SurfaceAsSingle()
    {
        int thrown = 0;
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 1);
        t.AddPostTask(async (success, result) =>
        {
            thrown++;
            throw new OperationCanceledException();
        });
        t.AddPostTask(async (success, result) =>
        {
            thrown++;
            throw new OperationCanceledException();
        });

        // Both post-tasks throw cancellation, but they collapse to a single surfaced cancellation (not aggregated).
        await Should.ThrowAsync<OperationCanceledException>(async () => await t);
        thrown.ShouldBe(2);
    }

    [TestMethod]
    public async Task Cancellation_FromPreAndPost_SurfaceAsSingle()
    {
        int thrown = 0;
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 1);
        t.AddPreTask(async (succeeding) =>
        {
            thrown++;
            throw new OperationCanceledException();
        });
        t.AddPostTask(async (success, result) =>
        {
            thrown++;
            throw new OperationCanceledException();
        });

        // Cancellation arises from both a pre-task and a post-task (across different spots), but collapses to a single surfaced cancellation.
        await Should.ThrowAsync<OperationCanceledException>(async () => await t);
        thrown.ShouldBe(2);
    }

    [TestMethod]
    public async Task Cancellation_FromMainAndPost_SurfaceAsSingle()
    {
        int thrown = 0;
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            thrown++;
            throw new OperationCanceledException();
        });
        t.AddPostTask(async (success, result) =>
        {
            thrown++;
            throw new OperationCanceledException();
        });

        // Cancellation arises from both the main task and a post-task (across different spots), but collapses to a single surfaced cancellation.
        await Should.ThrowAsync<OperationCanceledException>(async () => await t);
        thrown.ShouldBe(2);
    }

    // --- CastTo ---

    [TestMethod]
    public async Task CastTo_ConvertsResult()
    {
        var t = Make([], result: 21);
        var s = t.CastTo(async (v) => $"v={v}");
        (await s).ShouldBe("v=21");
    }

    [TestMethod]
    public async Task CastTo_RunsPreAndPostTasks_WithOriginalResult()
    {
        var log = new List<string>();
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 9);
        t.AddPreTask(async (succeeding) => log.Add($"pre:{succeeding}"));
        t.AddPostTask(async (success, result) => log.Add($"post:{success}:{result}"));

        var s = t.CastTo(async (v) => v * 2);
        (await s).ShouldBe(18);
        log.ShouldBe(["pre:True", "post:True:9"]); // Post-task observes the original (pre-cast) result.
    }

    [TestMethod]
    public async Task CastTo_ForwardsProgress()
    {
        var t = Make([new(null, "A", 0.5)], result: 4);
        var s = t.CastTo(async (v) => v.ToString());

        var p = await CollectFullAsync(s, TestContext.CancellationToken);
        p.ShouldBe([new(null, "A", 0.0), new(null, "A", 0.5), new(null, "A", 1.0)]);
        (await s).ShouldBe("4");
    }

    [TestMethod]
    public async Task CastTo_OriginalCannotBeAwaited()
    {
        var t = Make([], result: 1);
        _ = t.CastTo(async (v) => v);

        var ex = await Should.ThrowAsync<AggregateException>(async () => await t);
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
        ex.InnerException!.Message.ShouldBe("This task has been consumed by a CastTo operation and cannot be awaited directly.");
    }

    [TestMethod]
    public async Task CastTo_AfterStarted_Throws()
    {
        var t = Make([], result: 1);
        await t;

        var ex = Should.Throw<InvalidOperationException>(() => t.CastTo(async (v) => v));
        ex.Message.ShouldBe("Cannot cast the task after it has already begun.");
    }

    [TestMethod]
    public async Task CastTo_PreTaskFailure_SkipsMainAndConverter_RunsPostTasks()
    {
        bool mainRan = false;
        bool converterRan = false;
        bool? postSuccess = null;
        int? postResult = null;
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            mainRan = true;
            return 5;
        });
        t.AddPreTask(async (succeeding) =>
        {
            throw new InvalidOperationException("pre-fail");
        });
        t.AddPostTask(async (success, result) =>
        {
            postSuccess = success;
            postResult = result;
        });

        var s = t.CastTo(async (v) =>
        {
            converterRan = true;
            return v.ToString();
        });

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await s);
        ex.Message.ShouldBe("pre-fail");
        mainRan.ShouldBeFalse();
        converterRan.ShouldBeFalse();
        postSuccess.ShouldBe(false);
        postResult.ShouldBe(0); // Main never ran, so the original result stays at its default.
    }

    [TestMethod]
    public async Task CastTo_MainFailure_SkipsConverter_RunsPostTasks()
    {
        bool converterRan = false;
        bool? postSuccess = null;
        var t = MakeThrows(new InvalidOperationException("main-fail"));
        t.AddPostTask(async (success, result) =>
        {
            postSuccess = success;
        });

        var s = t.CastTo(async (v) =>
        {
            converterRan = true;
            return v.ToString();
        });

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await s);
        ex.Message.ShouldBe("main-fail");
        converterRan.ShouldBeFalse();
        postSuccess.ShouldBe(false);
    }

    [TestMethod]
    public async Task CastTo_ConverterFailure_RunsPostTasksWithOriginalResult_Surfaces()
    {
        bool? postSuccess = null;
        int? postResult = null;
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 13);
        t.AddPostTask(async (success, result) =>
        {
            postSuccess = success;
            postResult = result;
        });

        var s = t.CastTo<string>(async (v) =>
        {
            throw new InvalidOperationException("convert-fail");
        });

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await s);
        ex.Message.ShouldBe("convert-fail");
        postSuccess.ShouldBe(false);
        postResult.ShouldBe(13); // The original result is captured before the converter runs, so post-tasks still observe it.
    }

    [TestMethod]
    public async Task CastTo_PostTaskFailure_Surfaces()
    {
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 1);
        t.AddPostTask(async (success, result) =>
        {
            throw new InvalidOperationException("post-fail");
        });

        var s = t.CastTo(async (v) => v.ToString());

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await s);
        ex.Message.ShouldBe("post-fail");
    }

    [TestMethod]
    public async Task CastTo_TasksAddedBeforeAndAfter_AllRunInOrder()
    {
        var log = new List<string>();
        var t = new TaskWithProgress<int>(default, async (ct, cb) =>
        {
            log.Add("main");
            return 9;
        });
        t.AddPreTask(async (succeeding) => log.Add($"pre1:{succeeding}")); // Before cast.
        t.AddPostTask(async (success, result) => log.Add($"post1:{success}:{result}")); // Before cast - sees the original int result.

        var s = t.CastTo(async (v) => $"#{v}");
        s.AddPreTask(async (succeeding) => log.Add($"pre2:{succeeding}")); // After cast.
        s.AddPostTask(async (success, result) => log.Add($"post2:{success}:{result}")); // After cast - sees the converted string result.

        (await s).ShouldBe("#9");
        log.ShouldBe(["pre2:True", "pre1:True", "main", "post1:True:9", "post2:True:#9"]);
    }

    [TestMethod]
    public async Task CastTo_PostTasksAddedBeforeAndAfter_BothFailuresAggregate()
    {
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 9);
        t.AddPostTask(async (success, result) =>
        {
            throw new InvalidOperationException("before-post-fail");
        });

        var s = t.CastTo(async (v) => v.ToString());
        s.AddPostTask(async (success, result) =>
        {
            success.ShouldBeFalse();
            throw new InvalidOperationException("after-post-fail");
        });

        var ex = await Should.ThrowAsync<AggregateException>(async () => await s);
        ex.InnerExceptions.Select((e) => e.Message).ShouldBe(["before-post-fail", "after-post-fail"]);
    }

    [TestMethod]
    public async Task CastTo_PreTasksAddedBeforeAndAfter_BothFailuresAggregate()
    {
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 9);
        t.AddPreTask(async (succeeding) =>
        {
            succeeding.ShouldBeFalse();
            throw new InvalidOperationException("before-pre-fail");
        });

        var s = t.CastTo(async (v) => v.ToString());
        s.AddPreTask(async (succeeding) =>
        {
            throw new InvalidOperationException("after-pre-fail");
        });

        var ex = await Should.ThrowAsync<AggregateException>(async () => await s);
        ex.InnerExceptions.Select((e) => e.Message).ShouldBe(["after-pre-fail", "before-pre-fail"]);
    }

    [TestMethod]
    public async Task CastTo_PreTasksAddedBeforeAndAfter_RunInReverseOrder()
    {
        var log = new List<string>();
        var t = new TaskWithProgress<int>(default, async (ct, cb) => 1);
        t.AddPreTask(async (succeeding) => log.Add("pre-before")); // Before cast.

        var s = t.CastTo(async (v) => v.ToString());
        s.AddPreTask(async (succeeding) => log.Add("pre-after")); // After cast.

        await s;
        log.ShouldBe(["pre-after", "pre-before"]); // Pre-tasks run in reverse addition order across the cast boundary.
    }
}
