namespace ServiceHealthAssistant.Repair;

/// <summary>
/// Lightweight exponential back-off retry utility used by the repair agent
/// to handle transient Geneva API failures and rate-limit responses.
///
/// Back-off formula: <c>baseDelay × 2^(attempt-1)</c>, capped at <see cref="MaxDelay"/>.
/// Jitter (±10 %) is applied to prevent thundering-herd retries across a batch.
/// </summary>
public static class RetryPolicy
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Executes <paramref name="operation"/> up to <paramref name="maxAttempts"/> times,
    /// applying exponential back-off between failures.
    /// </summary>
    /// <typeparam name="T">Return type of the operation.</typeparam>
    /// <param name="operation">Async operation factory.</param>
    /// <param name="maxAttempts">Total maximum attempts (must be ≥ 1).</param>
    /// <param name="baseDelay">Base back-off delay; defaults to 1 second.</param>
    /// <param name="isTransient">
    ///   Optional predicate that decides whether an exception warrants a retry.
    ///   Defaults to retrying on any exception.
    /// </param>
    /// <param name="onRetry">
    ///   Optional callback invoked before each retry (attempt index, exception, delay).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the first successful invocation.</returns>
    /// <exception cref="AggregateException">
    ///   Thrown after all attempts are exhausted, containing all caught exceptions.
    /// </exception>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxAttempts = 3,
        TimeSpan? baseDelay = null,
        Func<Exception, bool>? isTransient = null,
        Action<int, Exception, TimeSpan>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "maxAttempts must be ≥ 1.");

        var delay   = baseDelay ?? TimeSpan.FromSeconds(1);
        var errors  = new List<Exception>();
        var rng     = new Random();

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when ((isTransient?.Invoke(ex) ?? true) && attempt < maxAttempts)
            {
                errors.Add(ex);

                // Exponential back-off: base × 2^(attempt-1), capped at MaxDelay.
                var rawDelay = TimeSpan.FromMilliseconds(
                    delay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                var capped   = rawDelay > MaxDelay ? MaxDelay : rawDelay;

                // ±10 % jitter.
                var jitterMs = (rng.NextDouble() * 0.2 - 0.1) * capped.TotalMilliseconds;
                var actual   = TimeSpan.FromMilliseconds(capped.TotalMilliseconds + jitterMs);

                onRetry?.Invoke(attempt, ex, actual);

                await Task.Delay(actual, cancellationToken);
            }
            catch (Exception ex)
            {
                // Last attempt or non-transient exception – fail immediately.
                errors.Add(ex);
                throw new AggregateException(
                    $"Operation failed after {attempt} attempt(s).", errors);
            }
        }

        // Unreachable: the loop body always returns or throws.
        throw new InvalidOperationException("Unreachable.");
    }

    /// <summary>
    /// Convenience overload for void operations.
    /// </summary>
    public static Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        int maxAttempts = 3,
        TimeSpan? baseDelay = null,
        Func<Exception, bool>? isTransient = null,
        Action<int, Exception, TimeSpan>? onRetry = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            async ct => { await operation(ct); return 0; },
            maxAttempts, baseDelay, isTransient, onRetry, cancellationToken);
}
