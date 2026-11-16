namespace Luma.Desktop;

// Used only on the UI thread. Record a notification before opening its modal dialog,
// because WPF can dispatch another exception while that dialog is still open.
internal sealed class UiErrorDialogGuard
{
    readonly HashSet<string> reportedErrors = new(StringComparer.Ordinal);
    bool dialogOpen;

    public bool TryBegin(Exception exception)
    {
        if (dialogOpen) return false;
        var error = exception.GetBaseException();
        var key = error.GetType().FullName + "\n" + error.Message;
        if (!reportedErrors.Add(key)) return false;
        dialogOpen = true;
        return true;
    }

    public void End() => dialogOpen = false;
}
