namespace FulcrumFS;

/// <summary>
/// Represents a handler invoked when repository corruption is detected. Handlers receive a <see cref="RepoCorruptionInfo"/> describing the condition and
/// may perform logging, alerting, or automated repair. The handler fires every time corruption is encountered (no per-process debouncing); handlers can
/// debounce or log as appropriate. Handler exceptions are not caught and will propagate out of the operation that detected the corruption.
/// </summary>
public delegate Task RepoCorruptionHandler(RepoCorruptionInfo info);
