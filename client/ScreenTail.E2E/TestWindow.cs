using System.Drawing;

namespace ScreenTail.E2E;

/// <summary>
/// The window the test session records: large black text on white, so the redaction worker's OCR reads
/// it as a person would, with a line that redaction must mask. On its own STA thread with its own message
/// loop, because the driver's main thread spends the run waiting on the pipe.
///
/// Its title says what it is and nothing a scope rule would take for a sign-in page or a vault.
/// </summary>
internal sealed class TestWindow : IDisposable
{
    public const string Title = "ScreenTail end-to-end test window";

    private readonly Thread _thread;
    private readonly TaskCompletionSource<Form> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Label? _label;

    public TestWindow(string text)
    {
        _thread = new Thread(() => Run(text)) { IsBackground = true, Name = "e2e test window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Form = _created.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        Handle = (IntPtr)Form.Invoke(() => Form.Handle);
    }

    public Form Form { get; }

    public IntPtr Handle { get; }

    /// <summary>Changes what the window shows, which is also what a screen changing looks like to the scene sampler.</summary>
    public void Show(string text) => Form.Invoke(() =>
    {
        _label!.Text = text;
        Form.Refresh();
    });

    public void Dispose()
    {
        try
        {
            Form.Invoke(Form.Close);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _ = _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Run(string text)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            var form = new Form
            {
                Text = Title,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(40, 40),
                Size = new Size(940, 460),
                BackColor = Color.White,
                ShowInTaskbar = true,
            };
            _label = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 24f, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.Black,
                BackColor = Color.White,
                Padding = new Padding(24),
                Text = text,
            };
            form.Controls.Add(_label);
            form.Shown += (_, _) => _created.TrySetResult(form);
            Application.Run(form);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = _created.TrySetException(ex);
        }
    }
}
