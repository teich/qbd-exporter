namespace QuickBooksProbe;

// COM calls cannot safely be interrupted. Cancellation is checked at their boundaries;
// cleanup calls deliberately remain available after cancellation.
internal sealed class CancellationProcessor(IProcessor inner, CancellationToken token) : IProcessor
{
    private T Invoke<T>(Func<T> action)
    {
        token.ThrowIfCancellationRequested();
        T value = action();
        token.ThrowIfCancellationRequested();
        return value;
    }
    public string? ProcessorVersion => Invoke(() => inner.ProcessorVersion);
    public void Open() { token.ThrowIfCancellationRequested(); inner.Open(); }
    public void SetReadOnly() { token.ThrowIfCancellationRequested(); inner.SetReadOnly(); }
    // Return the ticket before observing cancellation so Proof can always EndSession.
    public string Begin(string company) { token.ThrowIfCancellationRequested(); return inner.Begin(company); }
    public string CurrentCompany(string ticket) => Invoke(() => inner.CurrentCompany(ticket));
    public string[] Versions(string ticket) => Invoke(() => inner.Versions(ticket));
    public string Query(string ticket, string xml) => Invoke(() => inner.Query(ticket, xml));
    public void End(string ticket) => inner.End(ticket);
    public void Close() => inner.Close();
    public void Dispose() => inner.Dispose();
}
