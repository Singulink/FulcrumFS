using System.Diagnostics;

namespace FulcrumFS;

/// <summary>
/// The source stream of a transactional add, which the add closes whether it succeeds or fails unless the caller leaves it open: the processing context of
/// the main file once it has taken the stream over, which disposes it with its other resources, and otherwise the add itself, when it fails before a
/// context has taken the stream over (see <see cref="CloseIfNotTakenOverAsync"/>).
/// </summary>
internal sealed class AddSourceStream
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _isTakenOver;

    public AddSourceStream(Stream stream, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Creates the processing context that reads the stream, which takes it over.
    /// </summary>
    public FileProcessingContext CreateContext(
        FileId fileId,
        string? variantId,
        IAbsoluteDirectoryPath tempWorkingDir,
        string extension,
        CancellationToken cancellationToken)
    {
        Debug.Assert(!_isTakenOver, "Only one context can take the stream over.");

        var context = new FileProcessingContext(fileId, variantId, tempWorkingDir, _stream, extension, _leaveOpen, cancellationToken);
        _isTakenOver = true;

        return context;
    }

    /// <summary>
    /// Closes the stream when the add has failed, unless a processing context has taken it over or the caller leaves it open. A failure to close it is
    /// ignored, so that it does not replace the failure of the add.
    /// </summary>
    public async ValueTask CloseIfNotTakenOverAsync()
    {
        if (_leaveOpen || _isTakenOver)
            return;

        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }
}
