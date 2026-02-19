using System.Windows;
using System.Windows.Input;
using RVUCounter.Core;

namespace RVUCounter.Views;

public partial class ShiftDurationDialog : Window
{
    private const double MinHours = 0.5;
    private const double MaxHours = 24;
    private const double Step = 0.5;

    public double ShiftHours { get; private set; }

    public ShiftDurationDialog()
    {
        InitializeComponent();
        ThemeManager.ApplyCurrentThemeTitleBar(this);
        HoursTextBox.Focus();
        HoursTextBox.SelectAll();
    }

    private double GetCurrentValue()
    {
        if (double.TryParse(HoursTextBox.Text, out var val))
            return val;
        return 4;
    }

    private void SetValue(double val)
    {
        val = Math.Max(MinHours, Math.Min(MaxHours, val));
        HoursTextBox.Text = val % 1 == 0 ? val.ToString("0") : val.ToString("0.0");
    }

    private void SpinUp_Click(object sender, RoutedEventArgs e)
    {
        SetValue(GetCurrentValue() + Step);
    }

    private void SpinDown_Click(object sender, RoutedEventArgs e)
    {
        SetValue(GetCurrentValue() - Step);
    }

    private void HoursTextBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetValue(GetCurrentValue() + (e.Delta > 0 ? Step : -Step));
        e.Handled = true;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(HoursTextBox.Text, out var hours) || hours < MinHours || hours > MaxHours)
        {
            MessageBox.Show($"Please enter a valid number of hours ({MinHours}-{MaxHours}).", "Invalid Input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ShiftHours = hours;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
