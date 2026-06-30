using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Note: we expose applicable ConfigureAwait methods for the caller to utilise.
#pragma warning disable CA2007 // Consider calling ConfigureAwait on the awaited task

namespace FulcrumFS;

/// <summary>
/// Represents a task with progress reporting capabilities.
/// </summary>
/// <remarks>
/// <para>
/// If you do not care about progress reporting, you can simply await on this task as you would with a normal <see cref="Task{TResult}" />.
/// </para>
/// <para>
/// If you care about progress reporting, you can use it as an <see cref="IAsyncEnumerable{T}" /> of <see cref="ProgressValue" />s via <see langword="await" />
/// <see langword="foreach" />, and then get the result by a normal <see langword="await" /> after you're done with the progress reporting. You cannot begin
/// progress reporting if you have already begun awaiting normally (you must begin progress reporting first), nor can you start multiple progress reporting
/// enumerations concurrently. You can, however, run multiple normal awaits concurrently (like with <see cref="Task{TResult}" />).
/// </para>
/// <para>
/// Progress values for each stage are reported in the range [0.0, 1.0], are strictly monotonically increasing, and always include both 0.0 and 1.0 (unless a
/// failure occurs partway through).
/// </para>
/// <para>
/// If an exception occurs in the underlying operation and it is not observed by the caller through either <see cref="GetAwaiter" /> or
/// <see cref="GetAsyncEnumerator" />, it will surface as an unobserved task exception like with a normal <see cref="Task{TResult}" />. Additionally, each call
/// to <see cref="GetAwaiter" /> is treated like a new task, so if you call that methods and do not observe the exception, you could also see an unobserved task
/// exception for each of those calls. The handling for <see cref="GetAsyncEnumerator" /> is similar in most cases, but if you're manually enumerating, then
/// ignoring the result of a <c>MoveNextAsync</c> that would throw will not result in an unobserved task exception, and will instead consume the exception.
/// </para>
/// </remarks>
public sealed class TaskWithProgress<T> : IAsyncEnumerable<ProgressValue>
{
    // Fields:
    private readonly TaskFactory _taskFactory;
    private readonly CancellationToken _cancellationToken;
    private InterlockedFlag _isTaskStarted = new(false);
    private T? _result;
    private volatile Task? _callbackTask;
    private List<Func<bool, ValueTask>>? _preTasks;
    private List<Func<bool, T?, ValueTask>>? _postTasks;
    private readonly Lock _prePostTasksLock = new();
    private bool _disablePrePostTasks;

    /// <summary>
    /// A delegate that represents a factory method for the task based on an optional progress callback.
    /// </summary>
    internal delegate ValueTask<T> TaskFactory(CancellationToken cancellationToken, Func<ProgressValue, ValueTask>? progressCallback);

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskWithProgress{T}" /> class with the specified task factory.
    /// </summary>
    /// <remarks>
    /// Callers cannot call the progress callback concurrently - it is expected that only one call occurs at a time.
    /// </remarks>
    internal TaskWithProgress(CancellationToken cancellationToken, TaskFactory taskFactory)
    {
        _cancellationToken = cancellationToken;
        _taskFactory = taskFactory;
    }

    /// <summary>
    /// Creates and starts the task, returning a <see cref="Task{T}" /> that represents the asynchronous operation.
    /// </summary>
    private async Task<T> CreateTask(ConfigureAwaitOptions? options)
    {
        // Check if cancellation has been requested before starting the task:
        _cancellationToken.ThrowIfCancellationRequested();

        // Attempt to start the task:
        if (!_isTaskStarted.TrySet())
        {
            // Wait until _callbackTask is not null and then await on it:
            var callbackTask = _callbackTask;
            while (callbackTask is null)
            {
                await Task.Yield();
                callbackTask = _callbackTask;
            }

            // Await on the callback task:
            try
            {
                if (options is { } o1)
                    await callbackTask.ConfigureAwait(o1.HasFlag(ConfigureAwaitOptions.ContinueOnCapturedContext));
                else
                    await callbackTask;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Rethrow as an aggregate exception when not the primary producer of the exception:
                throw new AggregateException("An exception occurred during the execution of the already-completed task.", ex);
            }

            // Return our result field:
            return _result!;
        }

        // Create a callback task:
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _callbackTask = tcs.Task;

        // Ensure the callback task's exception is always observed, even if no secondary awaiter ever awaits it:
        ObserveException(_callbackTask);

        // Read our pre/post tasks:
        List<Func<bool, ValueTask>>? preTasks;
        List<Func<bool, T?, ValueTask>>? postTasks;
        lock (_prePostTasksLock)
        {
            preTasks = _preTasks;
            postTasks = _postTasks;
            _disablePrePostTasks = true;
        }

        // Run the task with pre/post tasks, and deal with any exceptions:
        T result;
        try
        {
            bool continueOnCapturedContext = options is { } o2 && o2.HasFlag(ConfigureAwaitOptions.ContinueOnCapturedContext);
            result = await RunWithPrePostTasks(
                _taskFactory,
                _cancellationToken,
                progressCallback: null,
                preTasks,
                postTasks,
                continueOnCapturedContext)
            .ConfigureAwait(continueOnCapturedContext);
        }
        catch (OperationCanceledException)
        {
            // Store the cancellation, complete the callback task, and rethrow:
            tcs.SetCanceled();
            throw;
        }
        catch (Exception ex)
        {
            // Store the exception, complete the callback task, and rethrow:
            tcs.SetException(ex);
            throw;
        }

        // Store the result, complete the callback task, and return the result:
        _result = result;
        tcs.SetResult();
        return result!;
    }

    /// <summary>
    /// Creates and starts the task with progress reporting, returning a <see cref="ValueTask{T}" /> that represents the asynchronous operation.
    /// </summary>
    private IAsyncEnumerator<ProgressValue> CreateProgressEnumerable(CancellationToken cancellationToken, ConfigureAwaitOptions? options)
    {
        // Our state declaration:
        bool isCancelled;
        LinkedList<ProgressValue?> progressQueue;
        Lock progressQueueLock = new();
        SemaphoreSlim progressSemaphore;
        int processedSinceNewStage = 0;

        // We want to start the background task & take ownership immediately, but report it as if it were a normal IAsyncEnumerator.
        // Thus, we do the starting immediately in a try/catch, and report exceptions as if they were thrown like normal by a IAsyncEnumerator.
        try
        {
            // Check if cancellation has been requested before starting the task:
            cancellationToken.ThrowIfCancellationRequested();
            _cancellationToken.ThrowIfCancellationRequested();

            // Attempt to start the task (if already started, throw an exception):
            if (!_isTaskStarted.TrySet())
                throw new InvalidOperationException("Cannot start the task more than once.");

            // Our state:
            isCancelled = false;
            progressQueue = new();
            progressSemaphore = new(0, int.MaxValue);

            // Start our implementation task and store it to the callback task field:
            _callbackTask = TaskImpl();

            // Return our enumerator implementation:
            return EnumeratorImpl();
        }
        catch (Exception ex)
        {
            // If we got an exception, just return an enumerator that throws it for its only MoveNextAsync call:
            return ExceptionEnumeratorImpl();

            // Our exception enumerator implementation:
            async IAsyncEnumerator<ProgressValue> ExceptionEnumeratorImpl()
            {
                ExceptionDispatchInfo.Throw(ex);
                yield break; // Unreachable, but required for compilation.
            }
        }

        // Our implementation task:
        async Task TaskImpl()
        {
            try
            {
                // Read our pre/post tasks:
                List<Func<bool, ValueTask>>? preTasks;
                List<Func<bool, T?, ValueTask>>? postTasks;
                lock (_prePostTasksLock)
                {
                    preTasks = _preTasks;
                    postTasks = _postTasks;
                    _disablePrePostTasks = true;
                }

                // Local state:
                bool isCancelledLocal = false;
                double lastProgressValue = -1.0; // Store last value so we can perform normalization.
                string? lastStage = null;
                string? lastVariantId = null;
                HashSet<(string? VariantId, string Stage)> reportedStages = [];
                int fromPreviousStage = 0;

                // Get our progress callback:
                Func<ProgressValue, ValueTask> progressCallback = async (progressValue) =>
                {
                    // Update isCancelledLocal:
                    if (!isCancelledLocal && Volatile.Read(in isCancelled))
                        isCancelledLocal = true;

                    // Read parameter:
                    var (variantId, stage, progress) = (progressValue.VariantId, progressValue.Stage, progressValue.Progress);

                    // Normalize what report as follows: always report a 0.0 first, and then only ever report in strictly monotonically increasing order, and
                    // never report 1.0 from here (we do that when the task actually completes).

                    // If we got a value outside of [0.0, 1.0] throw an exception:
                    if (progress < 0.0 || progress > 1.0 || double.IsNaN(progress))
                        throw new ArgumentOutOfRangeException(nameof(progressValue), progress, "Progress value must be in the range [0.0, 1.0].");

                    // Enqueue the progress value and release the semaphore:
                    if (!isCancelledLocal)
                    {
                        // If we're doing a new state / variant, ensure the previous one is finished off and begin this one:
                        if (lastStage != stage || lastVariantId != variantId)
                        {
                            if (!reportedStages.Add((variantId, stage)))
                                throw new InvalidOperationException($"Stage '{stage}' for variant '{variantId}' has already been reported.");

                            if (lastStage is not null && lastProgressValue < 1.0)
                            {
                                lock (progressQueueLock)
                                {
                                    progressQueue.AddLast(new ProgressValue(lastVariantId, lastStage, 1.0));
                                }

                                progressSemaphore.Release();
                                lastProgressValue = 1.0;
                            }

                            lock (progressQueueLock)
                            {
                                processedSinceNewStage = 0;
                                fromPreviousStage = progressQueue.Count;
                            }
                        }

                        // Handle beginning a new stage / variant:
                        if (lastStage != stage || lastVariantId != variantId)
                        {
                            lastStage = stage;
                            lastVariantId = variantId;
                            lastProgressValue = -1.0;
                        }

                        // Normalize -0.0 to 0.0.
                        if (progress == 0.0)
                            progress = 0.0;

                        // If progress has increased.
                        if (progress > lastProgressValue)
                        {
                            // If we missed 0.0, enqueue it first.
                            if (lastProgressValue < 0.0 && progress > 0.0)
                            {
                                lock (progressQueueLock)
                                {
                                    progressQueue.AddLast(new ProgressValue(variantId, stage, 0.0));
                                }

                                progressSemaphore.Release();
                            }

                            // Enqueue the reported progress value.
                            bool addedNew = true;
                            lock (progressQueueLock)
                            {
                                // If we just added one for this before, and nobody has read it yet, then we should just replace that, except for 0.0 and 1.0.
                                // This ensures our queue doesn't get way fuller than a caller can handle.
                                // We only do this if we have at least 16 in the current stage in the queue.
                                if (progressQueue.Count - int.Max(fromPreviousStage - processedSinceNewStage, 0) >= 16 && progressQueue.Last is { Value: ProgressValue lastValue } && lastValue.VariantId == variantId && lastValue.Stage == stage && lastValue.Progress is > 0.0 and < 1.0)
                                {
                                    progressQueue.RemoveLast();
                                    addedNew = false;
                                }

                                // Add our new value
                                progressQueue.AddLast(new ProgressValue(variantId, stage, progress));
                            }

                            if (addedNew)
                                progressSemaphore.Release();

                            // Update the last progress value.
                            lastProgressValue = progress;
                        }
                    }
                };

                // Run the task with pre/post tasks, and store the result if successful:
                bool continueOnCapturedContext = options is { } o2 && o2.HasFlag(ConfigureAwaitOptions.ContinueOnCapturedContext);
                _result = await RunWithPrePostTasks(
                    _taskFactory,
                    _cancellationToken,
                    progressCallback,
                    preTasks,
                    postTasks,
                    continueOnCapturedContext)
                .ConfigureAwait(continueOnCapturedContext);

                // Mark last progress value as 1.0 for the last stage if we haven't already done so:
                if (!isCancelledLocal && lastStage != null && lastProgressValue < 1.0)
                {
                    lock (progressQueueLock)
                    {
                        progressQueue.AddLast(new ProgressValue(lastVariantId, lastStage, 1.0));
                    }

                    progressSemaphore.Release();
                }

                // Mark as fully done:
                if (!isCancelledLocal)
                {
                    lock (progressQueueLock)
                    {
                        progressQueue.AddLast((ProgressValue?)null);
                    }

                    progressSemaphore.Release();
                }
            }
            finally
            {
                // Release when the task is done, so that the enumerator can exit.
                progressSemaphore.Release();
            }
        }

        // Our enumerator implementation:
        async IAsyncEnumerator<ProgressValue> EnumeratorImpl()
        {
            // Begin the enumeration of progress values:
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationToken);
                while (true)
                {
                    // Wait for next value to arrive, or end of enumeration:
                    if (options is { } o)
                        await progressSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(o & (ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding));
                    else
                        await progressSemaphore.WaitAsync(linkedCts.Token);

                    // Dequeue the next value:
                    bool gotValue;
                    ProgressValue? progressValue;
                    lock (progressQueueLock)
                    {
                        gotValue = progressQueue.Count > 0;
                        progressValue = gotValue ? progressQueue.First!.Value : null;
                        if (gotValue) progressQueue.RemoveFirst();
                        processedSinceNewStage++;
                    }

                    // Deal with the value we just got and yield it:
                    if (gotValue)
                    {
                        // If we got null, then we are done (fully successfully).
                        if (progressValue is null)
                            yield break;

                        // If we got a value, yield it.
                        yield return progressValue.Value;
                    }
                    else
                    {
                        // If we got here, it means the task has completed but not successfully, so await it to propagate the exception or cancellation.
                        if (options is { } o2)
                            await _callbackTask.ConfigureAwait(o2);
                        else
                            await _callbackTask;

                        yield break; // Exit explicitly just in case
                    }
                }
            }
            finally
            {
                Volatile.Write(ref isCancelled, true);
            }
        }
    }

    /// <summary>
    /// Observes the exception of the specified task (if any) so that it is never surfaced as an unobserved task exception.
    /// </summary>
    private static void ObserveException(Task task)
    {
        _ = task.ContinueWith(
            static (t) => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Creates an awaiter for this task.
    /// </summary>
    /// <remarks>
    /// This method can be called multiple times safely.
    /// </remarks>
    public Awaiter GetAwaiter()
    {
        return new(CreateTask(null).GetAwaiter());
    }

    /// <summary>
    /// Exposes an asynchronous enumerator for progress reporting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If awaiting has already been started (via either <c>GetAwaiter</c> (i.e., <see langword="await" /> on this task) or <c>GetAsyncEnumerator</c>), then
    /// this method will throw an <see cref="InvalidOperationException" />.
    /// </para>
    /// <para>
    /// To get the final result of this task, you must await on this instance again (which calls to <c>GetAwaiter</c>).
    /// </para>
    /// <para>
    /// The provided <paramref name="cancellationToken" /> only cancels the progress reporting enumeration (which can be cancelled through <c>Dispose</c> also).
    /// </para>
    /// </remarks>
    public IAsyncEnumerator<ProgressValue> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return CreateProgressEnumerable(cancellationToken, null);
    }

    /// <summary>
    /// Configures an awaiter for this task.
    /// </summary>
    public ConfiguredAwaitable ConfigureAwait(bool continueOnCapturedContext)
    {
        return new ConfiguredAwaitable(this, continueOnCapturedContext ? ConfigureAwaitOptions.ContinueOnCapturedContext : ConfigureAwaitOptions.None);
    }

    /// <summary>
    /// Configures an awaiter for this task.
    /// </summary>
    public ConfiguredAwaitable ConfigureAwait(ConfigureAwaitOptions options)
    {
        if ((options & (ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding)) != options)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid ConfigureAwaitOptions value.");

        return new ConfiguredAwaitable(this, options);
    }

    /// <summary>
    /// Provides an awaitable type that enables configured awaits on a <see cref="TaskWithProgress{T}" /> instance.
    /// </summary>
    public readonly struct ConfiguredAwaitable : IAsyncEnumerable<ProgressValue>
    {
        // Fields:
        private readonly TaskWithProgress<T> _task;
        private readonly ConfigureAwaitOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfiguredAwaitable" /> struct with the specified task and configuration.
        /// </summary>
        internal ConfiguredAwaitable(TaskWithProgress<T> taskWithProgress, ConfigureAwaitOptions options)
        {
            _task = taskWithProgress;
            _options = options;
        }

        /// <inheritdoc cref="TaskWithProgress{T}.GetAwaiter()" />
        public ConfiguredAwaiter GetAwaiter()
        {
            return new(_task.CreateTask(_options).ConfigureAwait(_options).GetAwaiter());
        }

        /// <inheritdoc cref="TaskWithProgress{T}.GetAsyncEnumerator(CancellationToken)" />
        public IAsyncEnumerator<ProgressValue> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return _task.CreateProgressEnumerable(cancellationToken, _options);
        }
    }

    /// <summary>
    /// Provides an awaiter for awaiting a <see cref="TaskWithProgress{T}" />.
    /// </summary>
    public readonly struct Awaiter : ICriticalNotifyCompletion
    {
        private readonly TaskAwaiter<T> _awaiter;
        internal Awaiter(TaskAwaiter<T> awaiter) => _awaiter = awaiter;

        /// <inheritdoc cref="TaskAwaiter.IsCompleted" />
        public bool IsCompleted => _awaiter.IsCompleted;

        /// <inheritdoc cref="TaskAwaiter.OnCompleted(Action)" />
        public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

        /// <inheritdoc cref="TaskAwaiter.UnsafeOnCompleted(Action)" />
        public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);

        /// <inheritdoc cref="TaskAwaiter.GetResult()" />
        [StackTraceHidden]
        public T GetResult() => _awaiter.GetResult();
    }

    /// <summary>
    /// Provides an awaiter for awaiting a <see cref="TaskWithProgress{T}" />.
    /// </summary>
    public readonly struct ConfiguredAwaiter : ICriticalNotifyCompletion
    {
        private readonly ConfiguredTaskAwaitable<T>.ConfiguredTaskAwaiter _awaiter;
        internal ConfiguredAwaiter(ConfiguredTaskAwaitable<T>.ConfiguredTaskAwaiter awaiter) => _awaiter = awaiter;

        /// <inheritdoc cref="TaskAwaiter.IsCompleted" />
        public bool IsCompleted => _awaiter.IsCompleted;

        /// <inheritdoc cref="TaskAwaiter.OnCompleted(Action)" />
        public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

        /// <inheritdoc cref="TaskAwaiter.UnsafeOnCompleted(Action)" />
        public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);

        /// <inheritdoc cref="TaskAwaiter.GetResult()" />
        [StackTraceHidden]
        public T GetResult() => _awaiter.GetResult();
    }

    /// <summary>
    /// Appends the task with the specified task action, which is executed when the task completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The purpose of this is so that you can insert additional logic after the task ends (e.g., so you can implement a wrapper method around a task).
    /// </para>
    /// <para>
    /// Tasks should not throw <see cref="OperationCanceledException" /> if <c>IsSuccessful</c> is <see langword="false" />.
    /// </para>
    /// </remarks>
    /// <param name="task">
    /// The task action to execute after the task completes - the first argument represents <c>IsSuccessful</c>, and the second is the <c>Result</c>.
    /// </param>
    public void AddPostTask(Func<bool, T?, ValueTask> task)
    {
        // Add the task to the list
        lock (_prePostTasksLock)
        {
            if (_disablePrePostTasks)
            {
                // Note: technically you can add after starting before the runner reads the list, but that doesn't matter so we don't worry about disallowing
                // it, since it will take this lock and set this field when it actually reads it.
                throw new InvalidOperationException("Cannot add a task after the task has already begun or been casted.");
            }

            (_postTasks ??= []).Add(task);
        }
    }

    /// <summary>
    /// Prepends the task with the specified task action, which is executed before the task begins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The purpose of this is so that you can insert additional logic before the task begins (e.g., so you can implement a wrapper method around a task).
    /// </para>
    /// <para>
    /// Tasks should not throw <see cref="OperationCanceledException" /> if <c>IsSucceeding</c> is <see langword="false" />.
    /// </para>
    /// </remarks>
    /// <param name="task">
    /// The task action to execute before the task begins - the argument represents <c>IsSucceeding</c> (whether we still haven't failed so far).
    /// </param>
    public void AddPreTask(Func<bool, ValueTask> task)
    {
        // Add the task to the list (note - list is stored in reverse execution order)
        lock (_prePostTasksLock)
        {
            if (_disablePrePostTasks)
            {
                // Note: technically you can add after starting before the runner reads the list, but that doesn't matter so we don't worry about disallowing
                // it, since it will take this lock and set this field when it actually reads it.
                throw new InvalidOperationException("Cannot add a task after the task has already begun or been casted.");
            }

            (_preTasks ??= []).Add(task);
        }
    }

    /// <summary>
    /// Casts the result of this task to a new type using the specified converter function.
    /// </summary>
    /// <remarks>
    /// This is only valid if the task has not begun yet, and consumes the current instance of the task to create it.
    /// </remarks>
    public TaskWithProgress<TNew> CastTo<TNew>(Func<T, ValueTask<TNew>> converter, bool continueOnCapturedContext = true)
    {
        // See if we can still take ownership of the task (if not, throw an exception):
        if (!_isTaskStarted.TrySet())
           throw new InvalidOperationException("Cannot cast the task after it has already begun.");

        // Ensure anyone waiting on this task will see an exception instead:
        _callbackTask = Task.FromException(new InvalidOperationException("This task has been consumed by a CastTo operation and cannot be awaited directly."));
        ObserveException(_callbackTask);

        // Read the fields:
        TaskFactory taskFactory = _taskFactory;
        CancellationToken cancellationToken = _cancellationToken;

        // Get all the tasks (and disable any further tasks from being added):
        List<Func<bool, ValueTask>>? preTasks;
        List<Func<bool, T?, ValueTask>>? postTasks;
        lock (_prePostTasksLock)
        {
            preTasks = _preTasks;
            postTasks = _postTasks;
            _disablePrePostTasks = true;
        }

        // State that holds our T result:
        T? outerResult = default;

        // Post task runner:
        List<Func<bool, TNew?, ValueTask>>? newPostTasks = null;
        if (postTasks is not null)
        {
            newPostTasks = new(postTasks.Count);
            foreach (var original in postTasks)
            {
                newPostTasks.Add(async (success, _) => await original(success, outerResult).ConfigureAwait(continueOnCapturedContext));
            }
        }

        // Copy the cancellation token & a task factory with the converter & pre/post tasks applied, and return a new instance over these:
        return new(cancellationToken, async (ct, progressCallback) =>
        {
            // Run the inner task:
            var result = await taskFactory(ct, progressCallback).ConfigureAwait(continueOnCapturedContext);

            // Store to our T result state, so that any post-tasks can access it:
            outerResult = result;

            // Convert the result and return it:
            return await converter(result).ConfigureAwait(continueOnCapturedContext);
        })
        {
            _postTasks = newPostTasks,
            _preTasks = preTasks,
        };
    }

    /// <summary>
    /// Runs the task with pre/post tasks, and returns the result or throws an appropriate exception if any occurred.
    /// </summary>
    private static async ValueTask<T> RunWithPrePostTasks(TaskFactory taskFactory, CancellationToken cancellationToken, Func<ProgressValue, ValueTask>? progressCallback, List<Func<bool, ValueTask>>? preTasks, List<Func<bool, T?, ValueTask>>? postTasks, bool continueOnCapturedContext)
    {
        // State:
        T? result = default;
        List<Exception>? errors = null;

        // Run any pre-tasks (note - these are stored in reverse order):
        if (preTasks is not null)
        {
            for (int i = preTasks.Count - 1; i >= 0; i--)
            {
                var task = preTasks[i];
                try
                {
                    await task(errors is null).ConfigureAwait(continueOnCapturedContext);
                }
                catch (OperationCanceledException) when (errors is not null)
                {
                    // Ignore illegal exception.
                }
                catch (Exception ex)
                {
                    (errors ??= []).Add(ex);
                }
            }
        }

        // Get the T result (if not already failed):
        if (errors is null)
        {
            try
            {
                result = await taskFactory(cancellationToken, progressCallback).ConfigureAwait(continueOnCapturedContext);
            }
            catch (Exception ex)
            {
                errors = [ex];
            }
        }

        // Run any post-tasks:
        if (postTasks is not null)
        {
            foreach (var task in postTasks)
            {
                try
                {
                    await task(errors is null, result).ConfigureAwait(continueOnCapturedContext);
                }
                catch (OperationCanceledException) when (errors is not null)
                {
                    // Ignore illegal exception.
                }
                catch (Exception ex)
                {
                    (errors ??= []).Add(ex);
                }
            }
        }

        // If we have errors, throw an appropriate exception (if any):
        if (errors is not null)
        {
            // If we only have cancellation errors, then we just want to report as cancelled.
            if (errors.All((x) => x is OperationCanceledException))
            {
                ExceptionDispatchInfo.Throw(errors[^1]);
            }

            // If we have real exceptions, then cancellation is irrelevant as it should not have occurred past the point of the first exception, and we consider
            // exceptional state (in what is effectively a finalizer) to have overridden the cancellation here.
            else
            {
                if (errors is [var singleError])
                {
                    ExceptionDispatchInfo.Throw(singleError);
                }
                else
                {
                    errors = [.. errors.Where((x) => x is not OperationCanceledException)];

                    if (errors.Count == 1)
                    {
                        ExceptionDispatchInfo.Throw(errors[0]);
                    }
                    else
                    {
                        throw new AggregateException("One or more exceptions occurred during the execution of the task.", errors);
                    }
                }
            }
        }

        // Otherwise, return the result:
        return result!;
    }
}
