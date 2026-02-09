using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RVUCounter.Data;
using RVUCounter.Models;

namespace RVUCounter.Views;

public partial class PaceComparisonDialog : Window
{
    private readonly DataManager _dataManager;
    private readonly Action<string, Shift?> _onSelection;
    private bool _isDarkMode;
    private bool _isClosing;

    private Shift? _priorShift;
    private Shift? _bestWeekShift;
    private Shift? _bestEverShift;
    private List<Shift> _thisWeekShifts = new();

    public PaceComparisonDialog(DataManager dataManager, Action<string, Shift?> onSelection, Point position)
    {
        InitializeComponent();

        _dataManager = dataManager;
        _onSelection = onSelection;
        _isDarkMode = dataManager.Settings.DarkMode;

        // Load shift data and goal settings first (affects dialog size)
        LoadShiftData();
        LoadGoalSettings();

        // Position near the click, but keep on screen
        Loaded += (s, e) => AdjustPositionToScreen(position);
    }

    private void AdjustPositionToScreen(Point desiredPosition)
    {
        // Get actual dialog size after content is loaded
        var dialogWidth = ActualWidth > 0 ? ActualWidth : Width;
        var dialogHeight = ActualHeight > 0 ? ActualHeight : 300; // fallback estimate

        // Use owner window bounds as reference for the screen (screenLeft/Right/Bottom used for bounds checking)
        double screenLeft = 0, screenRight = SystemParameters.PrimaryScreenWidth, screenBottom = SystemParameters.PrimaryScreenHeight;

        if (Owner != null)
        {
            // Estimate screen bounds from owner position (works for multi-monitor)
            screenLeft = Owner.Left - 100;
            screenRight = Owner.Left + Owner.ActualWidth + 2000; // generous right bound
            screenBottom = SystemParameters.VirtualScreenHeight;
        }

        // Calculate position, keeping dialog on screen
        var left = desiredPosition.X;
        var top = desiredPosition.Y;

        // Adjust if going off the right edge (use virtual screen for multi-monitor)
        var virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        if (left + dialogWidth > virtualRight)
            left = virtualRight - dialogWidth - 10;

        // Adjust if going off the bottom edge
        var virtualBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (top + dialogHeight > virtualBottom - 40) // 40px for taskbar
            top = virtualBottom - dialogHeight - 50;

        // Adjust if going off the left edge
        if (left < SystemParameters.VirtualScreenLeft)
            left = SystemParameters.VirtualScreenLeft + 10;

        // Adjust if going off the top edge
        if (top < SystemParameters.VirtualScreenTop)
            top = SystemParameters.VirtualScreenTop + 10;

        Left = left;
        Top = top;
    }

    private void LoadShiftData()
    {
        // Get all completed shifts and populate their RVU totals
        var allShifts = _dataManager.Database.GetAllShifts()
            .Where(s => s.ShiftEnd != null)
            .OrderByDescending(s => s.ShiftStart)
            .ToList();

        // Populate TotalRvu for each shift
        foreach (var shift in allShifts)
        {
            shift.TotalRvu = _dataManager.Database.GetTotalRvuForShift(shift.Id);
        }

        // Filter to shifts with RVU > 0
        allShifts = allShifts.Where(s => s.TotalRvu > 0).ToList();

        if (allShifts.Count == 0)
        {
            NoShiftsMessage.Visibility = Visibility.Visible;
            return;
        }

        var now = DateTime.Now;

        // Find start of current week (Monday)
        var daysToMonday = ((int)now.DayOfWeek + 6) % 7;
        var weekStart = now.Date.AddDays(-daysToMonday);

        // Typical shift hours: 6 PM to 6 AM (night shift)
        bool IsValidShiftHour(int hour) => hour >= 18 || hour <= 6;

        double bestWeekRvu = 0;
        double bestEverRvu = 0;

        foreach (var shift in allShifts)
        {
            var shiftHours = shift.Duration.TotalHours;

            // Best ever: any ~9 hour shift (7-11 hours)
            if (shiftHours >= 7 && shiftHours <= 11)
            {
                if (shift.TotalRvu > bestEverRvu)
                {
                    bestEverRvu = shift.TotalRvu;
                    _bestEverShift = shift;
                }
            }

            // For prior/week: only include typical shift hours
            var hour = shift.ShiftStart.Hour;
            if (!IsValidShiftHour(hour))
                continue;

            // Prior shift is the first valid one
            _priorShift ??= shift;

            // Check if in this week
            if (shift.ShiftStart >= weekStart)
            {
                _thisWeekShifts.Add(shift);
                if (shift.TotalRvu > bestWeekRvu)
                {
                    bestWeekRvu = shift.TotalRvu;
                    _bestWeekShift = shift;
                }
            }
        }

        // Reverse this week's shifts to show oldest first
        _thisWeekShifts.Reverse();

        // Show Prior Shift option
        if (_priorShift != null)
        {
            PriorOption.Tag = "prior";
            PriorText.Text = $"  Prior: {FormatShiftLabel(_priorShift)} ({_priorShift.TotalRvu:F1} RVU)";
            PriorOption.Visibility = Visibility.Visible;
        }

        // Show Week Best option
        if (_bestWeekShift != null)
        {
            WeekBestOption.Tag = "best_week";
            WeekBestText.Text = $"  Week: {FormatShiftLabel(_bestWeekShift)} ({_bestWeekShift.TotalRvu:F1} RVU)";
            WeekBestOption.Visibility = Visibility.Visible;
        }

        // Show Best Ever option
        if (_bestEverShift != null)
        {
            BestEverOption.Tag = "best_ever";
            BestEverText.Text = $"  Best: {FormatShiftLabel(_bestEverShift)} ({_bestEverShift.TotalRvu:F1} RVU)";
            BestEverOption.Visibility = Visibility.Visible;
        }

        // Show This Week shifts
        if (_thisWeekShifts.Count > 0)
        {
            ThisWeekHeader.Visibility = Visibility.Visible;

            for (int i = 0; i < _thisWeekShifts.Count; i++)
            {
                var shift = _thisWeekShifts[i];
                var border = new Border
                {
                    Tag = $"week_{i}",
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(4, 3, 4, 3)
                };

                var text = new TextBlock
                {
                    Text = $"  {FormatShiftLabel(shift)} ({shift.TotalRvu:F1} RVU)",
                    FontSize = 11,
                    Foreground = (TryFindResource("PrimaryTextBrush") as Brush) ?? Brushes.Black
                };

                border.Child = text;
                border.MouseLeftButtonUp += ThisWeekOption_Click;
                border.MouseEnter += Option_MouseEnter;
                border.MouseLeave += Option_MouseLeave;

                ThisWeekShifts.Children.Add(border);
            }
        }
    }

    private string FormatShiftLabel(Shift shift)
    {
        return shift.ShiftStart.ToString("ddd M/d");
    }

    private void LoadGoalSettings()
    {
        var rvuPerHour = _dataManager.Settings.PaceGoalRvuPerHour;
        var hours = _dataManager.Settings.PaceGoalShiftHours;
        var total = _dataManager.Settings.PaceGoalTotalRvu;

        // Use defaults if not set
        if (rvuPerHour <= 0) rvuPerHour = 15.0;
        if (hours <= 0) hours = 9.0;
        if (total <= 0) total = rvuPerHour * hours;

        RvuPerHourBox.Text = rvuPerHour.ToString("F1");
        HoursBox.Text = hours.ToString("F1");
        TotalBox.Text = total.ToString("F1");

        UpdateGoalLabel();
    }

    private void UpdateGoalLabel()
    {
        if (double.TryParse(RvuPerHourBox.Text, out var rvuH) &&
            double.TryParse(HoursBox.Text, out var hours) &&
            double.TryParse(TotalBox.Text, out var total))
        {
            GoalText.Text = $"  Goal: {rvuH:F1}/h x {hours:F0}h = {total:F0} RVU";
        }
    }

    private void GoalInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Recalculate total when RVU/h or hours changes
        if (double.TryParse(RvuPerHourBox.Text, out var rvuH) &&
            double.TryParse(HoursBox.Text, out var hours))
        {
            var newTotal = rvuH * hours;
            TotalBox.Text = newTotal.ToString("F1");
            UpdateGoalLabel();
        }
    }

    private void GoalInput_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveGoalSettings();
    }

    private void TotalInput_LostFocus(object sender, RoutedEventArgs e)
    {
        // Recalculate RVU/h when total is edited directly
        if (double.TryParse(TotalBox.Text, out var total) &&
            double.TryParse(HoursBox.Text, out var hours) && hours > 0)
        {
            var newRvuH = total / hours;
            RvuPerHourBox.Text = newRvuH.ToString("F1");
            UpdateGoalLabel();
            SaveGoalSettings();
        }
    }

    private void SaveGoalSettings()
    {
        if (double.TryParse(RvuPerHourBox.Text, out var rvuH) &&
            double.TryParse(HoursBox.Text, out var hours) &&
            double.TryParse(TotalBox.Text, out var total))
        {
            _dataManager.Settings.PaceGoalRvuPerHour = rvuH;
            _dataManager.Settings.PaceGoalShiftHours = hours;
            _dataManager.Settings.PaceGoalTotalRvu = total;
            _dataManager.SaveSettings();
        }
    }

    private void Option_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing) return;

        if (sender is Border border && border.Tag is string mode)
        {
            _isClosing = true;
            try
            {
                Shift? shift = mode switch
                {
                    "prior" => _priorShift,
                    "best_week" => _bestWeekShift,
                    "best_ever" => _bestEverShift,
                    _ => null
                };

                _onSelection(mode, shift);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Error in Option_Click for mode {Mode}", mode);
            }
            finally
            {
                Close();
            }
        }
    }

    private void ThisWeekOption_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing) return;

        if (sender is Border border && border.Tag is string mode)
        {
            // Parse week index from mode (e.g., "week_0" -> 0)
            if (mode.StartsWith("week_") && int.TryParse(mode[5..], out var idx) && idx < _thisWeekShifts.Count)
            {
                _isClosing = true;
                try
                {
                    _onSelection(mode, _thisWeekShifts[idx]);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Error in ThisWeekOption_Click for mode {Mode}", mode);
                }
                finally
                {
                    Close();
                }
            }
        }
    }

    private void Goal_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        try
        {
            SaveGoalSettings();
            _onSelection("goal", null);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error in Goal_Click");
        }
        finally
        {
            Close();
        }
    }

    private void Cancel_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;
        Close();
    }

    private void Option_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = new SolidColorBrush(_isDarkMode
                ? Color.FromRgb(0x40, 0x40, 0x40)
                : Color.FromRgb(0xE0, 0xE0, 0xE0));
        }
    }

    private void Option_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = Brushes.Transparent;
        }
    }

    private void Cancel_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is TextBlock text)
        {
            text.Foreground = (TryFindResource("PrimaryTextBrush") as Brush) ?? Brushes.Black;
        }
    }

    private void Cancel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is TextBlock text)
        {
            text.Foreground = Brushes.Gray;
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // Close when focus is lost (but only if not already closing)
        if (_isClosing) return;
        _isClosing = true;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_isClosing) return;
            _isClosing = true;
            Close();
        }
    }
}
