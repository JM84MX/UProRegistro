using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UProAttendance.Presentation;

internal sealed record ConfirmationCopy(string Salutation, string Message, string? Closing = null);

public partial class AttendanceConfirmation : UserControl
{
    public AttendanceConfirmation() => InitializeComponent();

    internal void Present(bool departing, string receipt, ConfirmationCopy copy)
    {
        Surface.Background = (Brush)FindResource(departing ? "ExitBackground" : "EntryBackground");
        Surface.BorderBrush = (Brush)FindResource(departing ? "ExitBorder" : "EntryBorder");
        EntrySymbol.Visibility = departing ? Visibility.Collapsed : Visibility.Visible;
        ExitSymbol.Visibility = departing ? Visibility.Visible : Visibility.Collapsed;

        Receipt.Text = receipt;
        Salutation.Text = copy.Salutation;
        Message.Text = copy.Message;
        Closing.Text = copy.Closing;
        Closing.Visibility = string.IsNullOrWhiteSpace(copy.Closing) ? Visibility.Collapsed : Visibility.Visible;

        BeginAnimation(OpacityProperty, null);
        if (SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(330))
            {
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });
        }

        UIElementAutomationPeer.FromElement(Salutation)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    internal void SetCompactLayout(bool compact)
    {
        Surface.Padding = compact ? new Thickness(20, 19, 20, 18) : new Thickness(32, 34, 32, 32);
        Symbol.Width = Symbol.Height = compact ? 75 : 112;
        Symbol.Margin = new Thickness(0, 0, 0, compact ? 9 : 16);
        Receipt.Margin = new Thickness(0, 0, 0, compact ? 8 : 14);
        Salutation.FontSize = compact ? 32 : 38;
        Salutation.LineHeight = compact ? 40 : 48;
        Message.FontSize = compact ? 24 : 27;
        Message.LineHeight = compact ? 32 : 38;
        Closing.Margin = new Thickness(0, compact ? 12 : 20, 0, 0);
    }
}
