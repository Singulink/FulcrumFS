namespace FulcrumFS;

/// <summary>
/// The time that one commit or rollback of a transaction, or one staging of a staged transaction, can spend waiting for access to the repository, shared by
/// all of its files: <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/> for the whole call instead of for each file. Not thread-safe: the call that
/// creates it reaches the repository for one file at a time, and a transaction serializes its calls through its own lock.
/// </summary>
/// <remarks>
/// <para>
/// Every file of a commit or a rollback needs access to the repository, and an operation that cannot get it waits for up to
/// <see cref="FileRepoOptions.MaxAccessWaitOrRetryTime"/> and then throws a <see cref="TimeoutException"/>. With a wait for each file, a commit of many files
/// on a volume that has stopped answering would wait that long for every one of them before failing, and once the first wait is over the others gain
/// nothing: the files are left for the cleaner either way.</para>
/// <para>
/// Only getting access uses the budget up (waiting for it, probing the volume and the attempts to initialize the repository), not the work of the call (see <see cref="FileRepo.EnsureInitializedAsync(bool, AccessWaitBudget?, CancellationToken)"/>),
/// so a large commit on slow but healthy storage is never cut short. While some of it is left, an access waits as an operation's does, for no longer than
/// what is left, so a short outage is still ridden out and every file is committed. Once it is used up, every further access of the call is attempted once
/// without waiting and fails with the same <see cref="TimeoutException"/>, which the call handles as it handles any failure of a file.</para>
/// </remarks>
internal sealed class AccessWaitBudget(TimeSpan time)
{
    private TimeSpan _remaining = time;

    /// <summary>
    /// Gets the time that is left to wait. It is zero once less than a millisecond is left: waits are made in whole milliseconds, so a smaller remainder
    /// would end at once.
    /// </summary>
    public TimeSpan Remaining => _remaining >= TimeSpan.FromMilliseconds(1) ? _remaining : TimeSpan.Zero;

    /// <summary>
    /// Charges the budget with time spent waiting for access.
    /// </summary>
    public void Charge(TimeSpan waited) => _remaining = waited < _remaining ? _remaining - waited : TimeSpan.Zero;
}
