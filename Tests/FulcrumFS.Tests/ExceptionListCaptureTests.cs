using FulcrumFS.Utilities;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class ExceptionListCaptureTests
{
    [TestMethod]
    public async Task TryRunAsync_Task_RecordsTheFirstFailure()
    {
        var elc = new ExceptionListCapture(ex => ex is IOException);
        var failure = new IOException("First.");

        (await elc.TryRunAsync(Task.FromException(failure))).ShouldBeFalse();

        elc.HasExceptions.ShouldBeTrue();
        elc.ResultException.ShouldBeSameAs(failure);
    }

    [TestMethod]
    public async Task TryRunAsync_ValueTask_RecordsTheFirstFailure()
    {
        var elc = new ExceptionListCapture(ex => ex is IOException);
        var failure = new IOException("First.");

        (await elc.TryRunAsync(ValueTask.FromException(failure))).ShouldBeFalse();

        elc.HasExceptions.ShouldBeTrue();
        elc.ResultException.ShouldBeSameAs(failure);
    }

    [TestMethod]
    public async Task TryRunAsync_FailureAfterAnAwait_IsRecorded()
    {
        var elc = new ExceptionListCapture(ex => ex is IOException);

        (await elc.TryRunAsync(FailLaterAsync())).ShouldBeFalse();

        elc.ResultException.ShouldBeOfType<IOException>().Message.ShouldBe("Later.");

        static async Task FailLaterAsync()
        {
            await Task.Yield();
            throw new IOException("Later.");
        }
    }

    [TestMethod]
    public async Task TryRunAsync_Success_RecordsNothing()
    {
        var elc = new ExceptionListCapture(ex => ex is IOException);

        (await elc.TryRunAsync(Task.CompletedTask)).ShouldBeTrue();
        (await elc.TryRunAsync(ValueTask.CompletedTask)).ShouldBeTrue();

        elc.HasExceptions.ShouldBeFalse();
        elc.ResultException.ShouldBeNull();
        Should.NotThrow(elc.ThrowIfHasExceptions);
    }

    [TestMethod]
    public async Task SyncAndAsyncFailures_AreAllRecorded_AndThrownTogether()
    {
        var elc = new ExceptionListCapture(ex => ex is IOException);

        await elc.TryRunAsync(Task.FromException(new IOException("1")));
        elc.TryRun(() => throw new IOException("2"));
        await elc.TryRunAsync(ValueTask.FromException(new IOException("3")));
        elc.Add(new IOException("4"));

        var ex = Should.Throw<AggregateException>(elc.ThrowIfHasExceptions);
        ex.InnerExceptions.Select(e => e.Message).ShouldBe(["1", "2", "3", "4"]);
    }

    [TestMethod]
    public async Task TryRunAsync_ExceptionOutsideTheFilter_Propagates_AndIsNotRecorded()
    {
        var elc = new ExceptionListCapture(ex => ex is IOException);

        await Should.ThrowAsync<InvalidOperationException>(() => elc.TryRunAsync(Task.FromException(new InvalidOperationException())));

        elc.HasExceptions.ShouldBeFalse();
    }
}
