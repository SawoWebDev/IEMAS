using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Iemas.Agent.Models;
using Iemas.Agent.Services;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;

namespace Iemas.Agent.Views;

/// <summary>
/// The first-run welcome tour. Shown once, the first time the Agent connects on this PC (and any time from
/// the tray menu). Five short pages: what IEMAS does, what happens and when (with this company's real
/// working hours and deadlines from the server), the pop-ups with a live "Show me" sample of each, what to
/// do with an inquiry, and a button that sends the employee a practice inquiry so they can watch it run
/// through the reminder and the escalation in a few minutes.
/// </summary>
public partial class WelcomeWindow : Window
{
    private const int PageCount = 5;

    private readonly string? _employeeName;
    private readonly Func<Task<ApiResult<WorkingRulesDto>>> _loadRules;
    private readonly Func<Task<ApiResult<PracticeInquiryResult>>> _sendPractice;
    private WorkingRulesDto? _rules;
    private int _page;
    private bool _practiceSent;

    public WelcomeWindow(string? employeeName, Func<Task<ApiResult<WorkingRulesDto>>> loadRules, Func<Task<ApiResult<PracticeInquiryResult>>> sendPractice)
    {
        InitializeComponent();
        _employeeName = employeeName;
        _loadRules = loadRules;
        _sendPractice = sendPractice;
        Loaded += async (_, _) =>
        {
            ShowPage(0);
            var result = await _loadRules();
            if (result.Succeeded) _rules = result.Value;
            if (_page == 1) ShowPage(1);
        };
    }

    private void ShowPage(int page)
    {
        _page = page;
        StepText.Text = $"STEP {page + 1} OF {PageCount}";
        PageHost.Children.Clear();
        BackButton.Visibility = page == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = page == PageCount - 1 ? "Finish" : "Next";
        switch (page)
        {
            case 0: PageWelcome(); break;
            case 1: PageWhatHappens(); break;
            case 2: PagePopups(); break;
            case 3: PageWhatYouDo(); break;
            default: PageTryIt(); break;
        }
    }

    // ---- Pages ----------------------------------------------------------------------------------

    private void PageWelcome()
    {
        var first = (_employeeName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        HeadingText.Text = string.IsNullOrEmpty(first) ? "Welcome to IEMAS" : $"Welcome to IEMAS, {first}";
        Para("IEMAS watches your company mailbox and makes sure no customer is left waiting.");
        Bullet("When a customer writes to you, a pop-up appears in the bottom-right corner of this screen.");
        Bullet("You answer the customer from Outlook, exactly as you do today. IEMAS notices your reply on its own; there is nothing to click.");
        Bullet("If a customer is waiting too long, IEMAS reminds you, and only if it is still unanswered does it tell your supervisor.");
        Bullet("Supplier offers, newsletters, invoices and other mail that is not a customer inquiry are ignored.");
        Note("This tour takes about two minutes. At the end you can send yourself a practice inquiry to see it all happen.");
    }

    private void PageWhatHappens()
    {
        HeadingText.Text = "What happens, and when";
        var r = _rules;
        var first = r?.FirstReplyBusinessHours ?? 8;
        var gap = r?.ReminderIntervalBusinessHours ?? 3;
        var reminders = r?.RemindersBeforeEscalation ?? 2;
        var supervisor = string.IsNullOrWhiteSpace(r?.SupervisorName) ? "your supervisor" : r!.SupervisorName!;

        Timeline("Within a few minutes", "#2563EB", "A customer email arrives. IEMAS checks that it is a real inquiry and shows you a blue NEW EMAIL pop-up.");
        Timeline($"Within {first} working hours", "#16A34A", "Reply to the customer from Outlook. Once your reply is found (about 2 minutes), the reminders stop. That is all most inquiries need.");
        Timeline($"{gap} working hours after that", "#D97706", "Still no reply: an amber REMINDER pop-up.");
        if (reminders > 1)
            Timeline($"{gap} working hours later", "#EA580C", "An orange ESCALATION WARNING: last call before your supervisor is told.");
        Timeline($"{gap} working hours later", "#DC2626", $"A red ESCALATED pop-up, and {supervisor} receives an email about the inquiry.");

        var total = first + gap * (reminders + 1);
        Note($"So an inquiry that arrives in the morning should be answered the same day, and your supervisor only hears about it after about {total} working hours (roughly a day and a half) without a reply.");
        Card("Your working hours", r?.WorkingHours ?? "Loading...", "#F9FAFB");
        Para("Time outside working hours does not count: an email that arrives on Friday evening is first counted on Monday morning. "
             + "After you reply, IEMAS keeps an eye on the conversation: if the customer writes again you get a pop-up, and if the customer goes quiet "
             + $"for {r?.CustomerWaitCalendarDays ?? 5} days you get a gentle nudge to follow up. A customer's silence never escalates you.");
    }

    private void PagePopups()
    {
        HeadingText.Text = "The pop-ups you will see";
        Para("Each pop-up has a coloured label. Click \"Show me\" to see a sample on your screen now (it is only a preview).");
        PopupRow("NEW EMAIL", "#2563EB", "#DBEAFE", "#1E40AF", "A new customer inquiry for you.",
            "New customer inquiry", "Maria Santos asks for a quotation for a SAWO heater with a control unit.");
        PopupRow("REMINDER", "#D97706", "#FEF3C7", "#92400E", "You have not replied yet.",
            "Reminder: the customer is waiting for your reply", "Quotation request from Maria Santos: 1 unanswered email.");
        PopupRow("ESCALATION WARNING", "#EA580C", "#FFEDD5", "#9A3412", "Last call: reply now or your supervisor is told.",
            "Escalation warning: reply now", "If this inquiry is still unanswered at the next check, your supervisor will be emailed.");
        PopupRow("ESCALATED", "#DC2626", "#FEE2E2", "#991B1B", "Your supervisor has been emailed. Reply as soon as you can.",
            "Escalated to your supervisor", "Quotation request from Maria Santos is still unanswered; your supervisor has been told.");
        PopupRow("NEEDS RESPONSE", "#E11D48", "#FFE4E6", "#9F1239", "The customer wrote again and is still waiting.",
            "Customer wrote again - needs your response", "Maria Santos sent a follow-up: 2 unanswered emails.");
        PopupRow("PLEASE CONFIRM", "#059669", "#D1FAE5", "#065F46", "IEMAS thinks the conversation is finished. Confirm, or say it is not.",
            "Looks resolved - please confirm", "The customer thanked you for the quotation. Is this inquiry resolved?");
        PopupRow("FOLLOW UP", "#7C3AED", "#EDE9FE", "#5B21B6", "The customer has gone quiet; a follow-up may help.",
            "Follow-up may be needed", "Maria Santos has not replied to your quotation for 5 days.");
        Note("Pop-ups stay on top until you click Open Case, Acknowledge or the x. Acknowledge only says you have seen it; it does not count as a reply.");
    }

    private void PageWhatYouDo()
    {
        HeadingText.Text = "What you do with an inquiry";
        Term("Reply from Outlook", "Always reply from the mailbox the customer wrote to. IEMAS finds the reply in its Sent folder within about 2 minutes.");
        Term("Mark as resolved", "When the customer's request is fully handled. Only you (or your supervisor) can close an inquiry; a first reply does not close it.");
        Term("Looks resolved - Yes / Not yet", "IEMAS may suggest an inquiry is finished. Click Yes if it is, or Not resolved yet if something is still open.");
        Term("Waiting on a colleague", "You need information from another department first. You get more time and the inquiry moves to the Waiting tab.");
        Term("Not a customer inquiry", "If IEMAS ever flags something by mistake (an offer from a supplier, a newsletter, internal mail), click this. It closes it, and helps IEMAS learn.");
        Note("Keep IEMAS running: closing the window only hides it to the tray (the IEMAS icon near the clock). It starts with Windows.");
    }

    private void PageTryIt()
    {
        HeadingText.Text = "Try it: send yourself a practice inquiry";
        Para("IEMAS can send a sample customer email to your own mailbox. It runs fast, so you can watch the whole journey in about 10 minutes:");
        Bullet("within about 3 minutes, the blue NEW EMAIL pop-up;");
        Bullet("if you do not reply: the REMINDER, the ESCALATION WARNING and the ESCALATED pop-ups, about a minute apart;");
        Bullet("reply to it from Outlook at any time to see the reminders stop.");
        Note("It is clearly marked [IEMAS practice]. The escalation email goes only to you, never to your supervisor.");

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 13 };
        var button = new Button
        {
            Content = _practiceSent ? "Practice inquiry sent" : "Send me a practice inquiry",
            IsEnabled = !_practiceSent,
            Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush("#16A34A"), Foreground = Brushes.White, BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            status.Foreground = Brush("#374151");
            status.Text = "Sending...";
            var result = await _sendPractice();
            if (result.Succeeded && result.Value is { Sent: true } sent)
            {
                _practiceSent = true;
                button.Content = "Practice inquiry sent";
                status.Foreground = Brush("#166534");
                status.Text = sent.Message + " You can close this window now; the pop-ups will appear on their own.";
            }
            else
            {
                button.IsEnabled = true;
                status.Foreground = Brush("#991B1B");
                status.Text = (result.Succeeded ? result.Value?.Message : result.Error) ?? "The practice inquiry could not be sent. Ask your administrator.";
            }
        };
        PageHost.Children.Add(button);
        PageHost.Children.Add(status);
        Para("You can open this tour again any time: right-click the IEMAS icon near the clock and choose Welcome tour.");
    }

    // ---- Building blocks ------------------------------------------------------------------------

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private void Para(string text) => PageHost.Children.Add(new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Foreground = Brush("#1F2937"), Margin = new Thickness(0, 0, 0, 10), LineHeight = 20,
    });

    private void Bullet(string text)
    {
        var row = new DockPanel { Margin = new Thickness(4, 0, 0, 8) };
        var dot = new TextBlock { Text = "•", FontSize = 14, Foreground = Brush("#2563EB"), Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Foreground = Brush("#1F2937"), LineHeight = 20 });
        PageHost.Children.Add(row);
    }

    private void Note(string text) => PageHost.Children.Add(new Border
    {
        Background = Brush("#EFF6FF"), BorderBrush = Brush("#BFDBFE"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 0, 10),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brush("#1E3A8A"), LineHeight = 19 },
    });

    private void Card(string title, string text, string background)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 13, Foreground = Brush("#111827") });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brush("#374151"), Margin = new Thickness(0, 2, 0, 0) });
        PageHost.Children.Add(new Border
        {
            Background = Brush(background), BorderBrush = Brush("#E5E7EB"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 0, 10), Child = panel,
        });
    }

    private void Term(string term, string text)
    {
        PageHost.Children.Add(new TextBlock { Text = term, FontWeight = FontWeights.Bold, FontSize = 13.5, Foreground = Brush("#111827"), Margin = new Thickness(0, 4, 0, 2) });
        PageHost.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brush("#374151"), Margin = new Thickness(0, 0, 0, 8), LineHeight = 19 });
    }

    private void Timeline(string when, string colour, string what)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bar = new Border { Background = Brush(colour), CornerRadius = new CornerRadius(3) };
        var text = new StackPanel { Margin = new Thickness(10, 2, 0, 2) };
        text.Children.Add(new TextBlock { Text = when, FontWeight = FontWeights.Bold, FontSize = 13, Foreground = Brush(colour) });
        text.Children.Add(new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brush("#1F2937"), LineHeight = 19 });
        Grid.SetColumn(text, 1);
        grid.Children.Add(bar);
        grid.Children.Add(text);
        PageHost.Children.Add(grid);
    }

    private void PopupRow(string badge, string accent, string tint, string badgeText, string meaning, string sampleTitle, string sampleMessage)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new Border
        {
            Background = Brush(tint), BorderBrush = Brush(accent), BorderThickness = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = badge, FontWeight = FontWeights.Bold, FontSize = 11, Foreground = Brush(badgeText) },
        };
        var text = new TextBlock { Text = meaning, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brush("#1F2937"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        var show = new Button
        {
            Content = "Show me", Padding = new Thickness(10, 4, 10, 4), Background = Brush("#E5E7EB"), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Shows a sample of this pop-up in the bottom-right corner. Nothing is sent or recorded.",
        };
        show.Click += (_, _) => PopupWindow.Show(
            new AgentPushMessage("SHOW_NOTIFICATION", Guid.NewGuid(), "SAMPLE", sampleTitle, sampleMessage + " (This is only a sample.)", DateTimeOffset.UtcNow),
            openCase: _ => { }, acknowledge: _ => { });

        Grid.SetColumn(text, 1);
        Grid.SetColumn(show, 2);
        grid.Children.Add(label);
        grid.Children.Add(text);
        grid.Children.Add(show);
        PageHost.Children.Add(grid);
    }

    // ---- Navigation -----------------------------------------------------------------------------

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page >= PageCount - 1) { Close(); return; }
        ShowPage(_page + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 0) ShowPage(_page - 1);
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();
}
