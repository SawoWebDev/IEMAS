using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Iemas.Agent.Models;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Iemas.Agent.Views;

/// <summary>
/// The first-time walkthrough: what appears on this PC, what to do with an inquiry, when it escalates (with this
/// employee's real hours and supervisor, from the server), and a practice inquiry that raises the real pop-ups
/// from "new email" to "escalated". Opens by itself the first time the Agent connects.
/// </summary>
public partial class TourWindow : Window
{
    private static TourWindow? _open;

    private readonly Action<Guid> _openCase;
    private readonly List<(string Heading, Action Build)> _pages;
    private int _page;
    private AgentGuideDto? _guide;
    private PracticeStateDto? _practice;
    private string? _practiceError;
    private bool _busy;

    private TourWindow(Action<Guid> openCase)
    {
        InitializeComponent();
        _openCase = openCase;
        _pages = new List<(string, Action)>
        {
            ("Welcome", BuildWelcome),
            ("What you will see on this PC", BuildWhatYouSee),
            ("When an inquiry arrives", BuildWhatToDo),
            ("When it goes to your supervisor", BuildTiming),
            ("Try it: a practice inquiry", BuildPractice),
        };
        Closing += OnClosing;
        Show(0);
        _ = LoadGuideAsync();
    }

    /// <summary>Opens the walkthrough, or brings the open one forward. <paramref name="atPractice"/> jumps to the practice page.</summary>
    public static void Open(Action<Guid> openCase, bool atPractice = false)
    {
        if (_open is null)
        {
            _open = new TourWindow(openCase);
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        if (atPractice) _open.Show(_open._pages.Count - 1);
        _open.WindowState = WindowState.Normal;
        _open.Activate();
    }

    private async Task LoadGuideAsync()
    {
        _guide = await App.Session.GetGuideAsync();
        Show(_page);
    }

    private void Show(int page)
    {
        _page = Math.Clamp(page, 0, _pages.Count - 1);
        PageHost.Children.Clear();
        StepText.Text = $"STEP {_page + 1} OF {_pages.Count}";
        HeadingText.Text = _pages[_page].Heading;
        _pages[_page].Build();
        BackButton.IsEnabled = _page > 0;
        var last = _page == _pages.Count - 1;
        NextButton.Content = last ? "Finish" : "Next";
        SkipButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        Scroller.ScrollToTop();
    }

    // ---- Pages ----

    private void BuildWelcome()
    {
        Paragraph(_guide is null ? "Hi!" : $"Hi {_guide.EmployeeName}!", bold: true, size: 15);
        Paragraph("IEMAS watches your work mailbox and tells you when a customer is waiting for an answer, so nothing slips through.");
        if (_guide is { Mailboxes.Count: > 0 })
        {
            Card("#EFF6FF", "#1E40AF", "Watched for you: " + string.Join(", ", _guide.Mailboxes));
        }
        Bullet("You keep using Outlook exactly as before. There is nothing to click after you reply.");
        Bullet("Only customer inquiries raise alerts. Newsletters, receipts and internal mail are left alone.");
        Bullet("If an alert is wrong, choose \"Not a customer inquiry\". IEMAS learns from it.");
        Paragraph("This takes about two minutes and ends with a practice run.", muted: true);
    }

    private void BuildWhatYouSee()
    {
        Paragraph("1. The IEMAS icon next to the clock", bold: true);
        Paragraph("A blue envelope. Double-click it to open the app. Closing the window only hides it; pop-ups keep coming.");
        Paragraph("2. Pop-ups in the bottom-right corner", bold: true);
        Paragraph("They stay until you open, acknowledge or close them. The colour tells you how urgent it is:");
        Legend("#2563EB", "NEW EMAIL", "a customer sent an inquiry");
        Legend("#E11D48", "NEEDS RESPONSE", "the customer wrote again and is still waiting");
        Legend("#D97706", "REMINDER", "the reply time has passed");
        Legend("#EA580C", "ESCALATION WARNING", "last reminder, with the date");
        Legend("#DC2626", "ESCALATED / OVERDUE", "your supervisor was asked to help");
        Legend("#7C3AED", "FOLLOW UP", "a conversation has gone quiet");
        Legend("#059669", "PLEASE CONFIRM", "it looks finished; you decide");
        Paragraph("3. The app window", bold: true);
        Paragraph("Action Required lists every inquiry that needs you. Waiting holds the ones you are waiting on. Help explains every button.");
    }

    private void BuildWhatToDo()
    {
        Paragraph("Open the inquiry and answer the customer. That is usually all.", bold: true);
        Legend("#2563EB", "Reply in email", "opens Outlook; IEMAS sees your reply within about 2 minutes");
        Legend("#059669", "Mark as resolved", "the request is fully handled; reminders stop for good");
        Legend("#1E40AF", "I'm working on it", "shows you picked it up (a reply is still needed)");
        Legend("#6B7280", "Waiting on a colleague", "you need information first; you get more time");
        Legend("#6B7280", "Remind me in 1 hour", "pops it up again later");
        Legend("#DC2626", "Not a customer inquiry", "for anything flagged by mistake");
        Paragraph("A first reply does not close an inquiry. IEMAS keeps watching until you mark it resolved, so a customer who writes again is never forgotten.", muted: true);
    }

    private void BuildTiming()
    {
        if (_guide is null)
        {
            Paragraph("The times could not be loaded from the server. Your administrator can tell you the reply and escalation times.", muted: true);
            return;
        }
        var g = _guide;
        Paragraph($"You have {g.FirstReplyHours} working hours for the first reply. Then {Count(g.Reminders, "reminder")}, {g.ReminderIntervalHours} working hours apart. "
            + $"{(g.SupervisorName is null ? "Your supervisor" : g.SupervisorName)} is only asked to help if there is still no reply after {g.HoursUntilSupervisorIsTold} working hours.");
        Card("#EFF6FF", "#1E40AF", $"Working hours: {g.WorkingHours} ({g.TimeZone}). Nights, weekends and holidays do not count, and no reminders are sent then.");
        Paragraph("For example, if a customer emailed you right now:", bold: true);
        foreach (var step in g.Example) Row(step.When, step.Label);
        Paragraph($"A customer follow-up should be answered within {Count(g.FollowUpBusinessDays, "working day")}. "
            + $"If the customer goes quiet for {Count(g.CustomerSilentDays, "day")}, you get a gentle note to follow up.", muted: true);
        if (g.SupervisorName is null) Paragraph("No supervisor is set for you yet; your administrator can set one.", muted: true);
    }

    private void BuildPractice()
    {
        Paragraph("See a whole inquiry from start to escalation in under a minute. Nobody else is contacted: the pop-ups appear on this PC and the sample emails go only to your own inbox.");

        if (_practice is null)
        {
            ActionButton(_busy ? "Sending..." : "Send me a practice inquiry", StartPracticeAsync, !_busy);
        }
        else
        {
            for (var i = 0; i <= 3; i++)
            {
                var label = i switch { 0 => "New inquiry", 1 => "Reminder", 2 => "Last reminder", _ => "Escalated" };
                var done = i <= _practice.Step;
                Row(done ? "✔" : "○", label);
            }
            Card(_practice.Finished ? "#D1FAE5" : "#FEF3C7", _practice.Finished ? "#065F46" : "#92400E",
                $"{_practice.Title}\n{_practice.Explanation}");
            if (_practice.Step == 0 && !_practice.SampleEmailSent)
            {
                Paragraph("The sample email could not be sent (the outgoing mail account is not set up), but the pop-ups work.", muted: true);
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            if (!_practice.Finished) buttons.Children.Add(Button(_busy ? "Working..." : "Show what happens next", AdvancePracticeAsync, !_busy, primary: true));
            var id = _practice.CaseId;
            buttons.Children.Add(Button("Open the practice inquiry", () => { _openCase(id); return Task.CompletedTask; }, !_busy, primary: false));
            if (_practice.Finished) buttons.Children.Add(Button("Run it again", StartPracticeAsync, !_busy, primary: false));
            PageHost.Children.Add(buttons);
            Paragraph("Press Finish when you are done: the practice inquiry is then removed.", muted: true);
        }

        if (_practiceError is not null) Card("#FEE2E2", "#991B1B", _practiceError);
    }

    // ---- Practice ----

    private Task StartPracticeAsync() => RunPracticeAsync(() => App.Session.StartPracticeAsync());

    private Task AdvancePracticeAsync() => RunPracticeAsync(() => App.Session.AdvancePracticeAsync(_practice!.CaseId));

    private async Task RunPracticeAsync(Func<Task<Services.ApiResult<PracticeStateDto>>> call)
    {
        if (_busy) return;
        _busy = true;
        _practiceError = null;
        Show(_page);
        try
        {
            var result = await call();
            if (result.Succeeded && result.Value is not null) _practice = result.Value;
            else
            {
                _practiceError = result.Error ?? "The practice could not continue.";
                if (_practiceError.Contains("ended", StringComparison.OrdinalIgnoreCase)) _practice = null;
            }
        }
        finally
        {
            _busy = false;
            Show(_page);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        App.Session.MarkTourSeen();
        if (_practice is not null) _ = App.Session.EndPracticeAsync(_practice.CaseId);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page == _pages.Count - 1) Close();
        else Show(_page + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Show(_page - 1);
    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Small building blocks ----

    private static string Count(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private static Brush Paint(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    private void Paragraph(string text, bool bold = false, bool muted = false, double size = 13) =>
        PageHost.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, LineHeight = size + 6,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Paint(muted ? "#6B7280" : "#111827"),
            Margin = new Thickness(0, bold ? 8 : 0, 0, 8),
        });

    private void Bullet(string text) => Row("•", text);

    private void Row(string left, string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left.Length > 2 ? 160 : 20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var first = new TextBlock { Text = left, FontWeight = FontWeights.SemiBold, Foreground = Paint("#374151") };
        var second = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Paint("#111827") };
        Grid.SetColumn(second, 1);
        grid.Children.Add(first);
        grid.Children.Add(second);
        PageHost.Children.Add(grid);
    }

    private void Legend(string colour, string name, string meaning)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 0) };
        line.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(new System.Windows.Documents.Run("  " + meaning) { Foreground = Paint("#374151") });
        PageHost.Children.Add(new Border
        {
            BorderBrush = Paint(colour), BorderThickness = new Thickness(5, 0, 0, 0), Background = Paint("#FFFFFF"),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(0, 6, 8, 6), Margin = new Thickness(0, 0, 0, 5), Child = line,
        });
    }

    private void Card(string background, string foreground, string text) =>
        PageHost.Children.Add(new Border
        {
            Background = Paint(background), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 6, 0, 10),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Paint(foreground), LineHeight = 19 },
        });

    private void ActionButton(string text, Func<Task> action, bool enabled) =>
        PageHost.Children.Add(Button(text, action, enabled, primary: true));

    private Button Button(string text, Func<Task> action, bool enabled, bool primary)
    {
        var button = new Button
        {
            Content = text, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 8, 8),
            Style = (Style)FindResource(primary ? "PrimaryBtn" : "PlainBtn"),
        };
        button.Click += async (_, _) => await action();
        return button;
    }
}
